// features/builds: M5-18 client wiring (see builds_feature.h).
#include "features/builds/builds_feature.h"

#include <chrono>
#include <utility>

#include "features/diag/diag_hub.h"
#include "features/events/events_hub.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "features/statics/statics_hub.h"
#include "host/host_log.h"

namespace x4mp::features::builds {
namespace {

using host::Cat;
using host::Level;

constexpr std::int64_t kLogMs = 30'000;

std::uint64_t now_ms() {
  return static_cast<std::uint64_t>(
      std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

}  // namespace

BuildsHub& builds_hub() {
  static BuildsHub hub;
  return hub;
}

void BuildsHub::set_result_sink(std::function<void(const events::IntentResultView&)> sink) { sink_ = std::move(sink); }

void BuildsHub::on_frame_message(X4MP::Proto::MsgType type, std::span<const std::uint8_t> payload) {
  if (type != X4MP::Proto::MsgType::IntentResult) return;
  events::IntentResultView view;
  if (!events::decode_intent_result(payload, view)) {
    ++bad_;
    return;
  }
  ++results_;
  if (sink_) sink_(view);
}

void BuildsHub::reset() {
  sink_ = nullptr;
  event_sink_ = nullptr;
  results_ = 0;
  bad_ = 0;
}

void BuildsFeature::on_init(host::HostContext& ctx) {
  ctx_ = &ctx;
  builds_hub().reset();
  builds::BuildReportDeps deps;
  deps.describe = [this](std::uint64_t component, builds::BuildDescribe& out) {
    if (ctx_ == nullptr) return false;
    const auto pose = ctx_->game.object_position(component);
    if (!pose) return false;  // the buildanchor is gone (a removed plan) or the game refused the id
    const auto sector = ctx_->game.context_by_class(component, "sector", false);
    if (sector == 0) return false;
    const auto* map = selfship::selfship_hub().map();
    const std::uint16_t index = map != nullptr ? map->index_of(sector) : 0;
    if (index == 0) return false;  // the galaxy map has not named this sector yet: wait, never guess
    out = builds::BuildDescribe{};
    out.sector = index;
    out.position[0] = pose->x;
    out.position[1] = pose->y;
    out.position[2] = pose->z;
    out.rotation[0] = pose->yaw;
    out.rotation[1] = pose->pitch;
    out.rotation[2] = pose->roll;
    return true;
  };
  deps.self_player_id = [] { return events::events_hub().self_player_id(); };
  deps.send_control = [](X4MP::Proto::MsgType type, std::span<const std::uint8_t> payload) {
    return events::events_hub().send_control(static_cast<std::uint16_t>(type), payload);
  };
  deps.now_ms = [] { return now_ms(); };
  reporter_ = std::make_unique<builds::BuildReporter>(std::move(deps));
  builds_hub().set_event_sink([this](const events::MdEvent& event) { return reporter_ != nullptr && reporter_->on_event(event); });
  builds_hub().set_result_sink([this](const events::IntentResultView& view) {
    if (ctx_ != nullptr) on_result(*ctx_, view);
  });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "builds: build capture armed (one request per finished station)");
}

void BuildsFeature::on_game_loaded(host::HostContext& ctx) {
  (void)ctx;
  if (reporter_) reporter_->reset();  // a new universe has new components
  last_log_ms_ = 0;
}

void BuildsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  on_authority_ = diag_hub().role() == NodeRole::Authority;
  if (on_authority_) return;  // the authority spawns the station; it never reports its own build
  if (!info.universe_ready || !reporter_) return;
  reporter_->retry(now_ms());
  report(ctx, false);
}

void BuildsFeature::on_result(host::HostContext& ctx, const events::IntentResultView& view) {
  if (!reporter_) return;
  builds::OutstandingBuild build;
  if (!reporter_->take_outstanding(view.request_id, build)) return;  // not a build of ours (a kill claim, a trade)
  if (view.status == X4MP::Proto::IntentStatus::Accepted && view.result_net_id != 0) {
    statics::statics_hub().pin_station(build.sector, build.component, view.result_net_id, {});
    ++bound_;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "builds: station '{}' bound to net {} (component {})", build.macro, view.result_net_id,
              build.component);
    return;
  }
  ++rejected_;
  X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "builds: station '{}' was not spawned: reason {} {} (request {})", build.macro,
            static_cast<int>(view.reason), view.detail, view.request_id);
}

void BuildsFeature::report(host::HostContext& ctx, bool force) {
  if (!reporter_) return;
  const auto t = now_ms();
  if (!force && last_log_ms_ != 0 && t - last_log_ms_ < static_cast<std::uint64_t>(kLogMs)) return;
  last_log_ms_ = t;
  const auto& s = reporter_->stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[builds] sent={} deduped={} unresolved={} bad={} failed={} dropped={} pending={} | results={} bound={} rejected={} "
            "bad_frames={}",
            s.sent, s.deduped, s.unresolved, s.bad, s.failed, s.dropped, reporter_->pending(), builds_hub().results(), bound_,
            rejected_, builds_hub().bad());
}

void BuildsFeature::on_shutdown(host::HostContext& ctx) {
  report(ctx, true);
  builds_hub().reset();
  reporter_.reset();
  ctx_ = nullptr;
}

}  // namespace x4mp::features::builds
