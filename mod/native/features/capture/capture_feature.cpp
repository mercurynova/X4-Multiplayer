#include "features/capture/capture_feature.h"

#include <chrono>
#include <span>
#include <utility>

#include "features/diag/diag_hub.h"
#include "features/events/events_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "host/host_log.h"

namespace x4mp::features::capture {

namespace {
using host::Cat;
using host::Level;

constexpr std::size_t kRingCap = 256;
constexpr std::size_t kDrainPerFrame = 64;
constexpr std::int64_t kLogUs = 30'000'000;

std::int64_t now_us() {
  using clock = std::chrono::steady_clock;
  return std::chrono::duration_cast<std::chrono::microseconds>(clock::now().time_since_epoch()).count();
}
}  // namespace

void CaptureFeature::on_init(host::HostContext& ctx) {
  CaptureReportDeps deps;
  deps.ghost_net_id = [](std::uint64_t component) { return ghosts::ghost_hub().net_id_of_local(component); };
  deps.owner_faction = [&ctx](std::uint64_t component) {
    return ctx.game.owner_faction(static_cast<game::UniverseId>(component));
  };
  deps.self_player_id = [] { return events::events_hub().self_player_id(); };
  deps.send_control = [](X4MP::Proto::MsgType type, std::span<const std::uint8_t> payload) {
    return events::events_hub().send_control(static_cast<std::uint16_t>(type), payload);
  };
  deps.now_ms = [] { return static_cast<std::uint64_t>(now_us() / 1000); };
  reporter_ = std::make_unique<CaptureReporter>(std::move(deps));

  hook_armed_ = ctx.platform.subscribe_owner_change([this](const host::IPlatform::MdOwnerChange& change) {
    push(change.component);
  });
  if (hook_armed_) {
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "capture: the typed EntityChangedOwner hook is armed (M5-20)");
  } else {
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn,
              "capture: the typed EntityChangedOwner hook is not available; a capture of a ghost will not be reported");
  }
}

void CaptureFeature::push(std::uint64_t component) {
  if (component == 0) return;
  const std::lock_guard<std::mutex> lock(mutex_);
  if (ring_.size() >= kRingCap) {
    ++dropped_;
    return;
  }
  ring_.push_back(component);
}

void CaptureFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  on_authority_ = diag_hub().role() == NodeRole::Authority;
  if (on_authority_ || !reporter_) return;
  if (!info.universe_ready) return;

  std::vector<std::uint64_t> batch;
  {
    const std::lock_guard<std::mutex> lock(mutex_);
    const std::size_t take = ring_.size() < kDrainPerFrame ? ring_.size() : kDrainPerFrame;
    batch.assign(ring_.begin(), ring_.begin() + static_cast<std::ptrdiff_t>(take));
    ring_.erase(ring_.begin(), ring_.begin() + static_cast<std::ptrdiff_t>(take));
  }
  for (const std::uint64_t component : batch) {
    (void)reporter_->on_owner_change(component);
    ++drained_;
  }
  report(ctx, false);
}

void CaptureFeature::report(host::HostContext& ctx, bool force) {
  const std::int64_t now = now_us();
  if (!force && last_log_us_ != 0 && now - last_log_us_ < kLogUs) return;
  last_log_us_ = now;
  const CaptureReportStats& s = reporter_->stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[capture] sent={} not_ghost={} not_player={} unknown_owner={} failed={} deduped={} bad={} | hook={} changes={} dropped={}",
            s.sent, s.not_ghost, s.not_player, s.unknown_owner, s.send_failed, s.deduped, s.bad,
            hook_armed_ ? "armed" : "OFF", drained_, dropped_);
}

void CaptureFeature::on_shutdown(host::HostContext& ctx) {
  if (!reporter_ || on_authority_) return;
  report(ctx, true);
  reporter_->reset();
}

}  // namespace x4mp::features::capture
