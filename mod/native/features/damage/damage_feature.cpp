#include "features/damage/damage_feature.h"

#include <chrono>
#include <vector>

#include <nlohmann/json.hpp>

#include "host/host_log.h"

namespace x4mp::features {

namespace {
using host::Cat;
using host::Level;
using Clock = std::chrono::steady_clock;
constexpr std::int64_t kReportEveryUs = 30'000'000;  // M15: the same 30 s line the other features print
}  // namespace

void DamageFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  (void)info;
  flush(ctx);
  const std::int64_t now_us =
      std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();
  if (now_us - last_log_us_ >= kReportEveryUs) {
    last_log_us_ = now_us;
    report(ctx);
  }
}

void DamageFeature::on_shutdown(host::HostContext& ctx) {
  // Nothing is kept across a save: a queued line is one hit, and the authority re-reads the real hull after a reload.
  flush(ctx);
}

void DamageFeature::flush(host::HostContext& ctx) {
  std::vector<damage::DamageOp> ops = damage::damage_hub().take_ops(kPerFrame);
  if (ops.empty()) return;
  damage::DamageStats& stats = damage::damage_hub().stats();
  ++stats.frames;

  nlohmann::json j;
  j["v"] = 1;
  j["damage"] = nlohmann::json::array();
  for (const damage::DamageOp& op : ops) j["damage"].push_back({op.component, op.hull, op.shield});

  const std::string text = j.dump();
  if (!ctx.platform.raise_lua("x4mp.actions", text)) {
    ++stats.raises_failed;
    stats.ops_skipped += ops.size();
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "damage: actions shim refused {} damage line(s)", ops.size());
    return;
  }

  stats.ops_applied += ops.size();
}

void DamageFeature::report(host::HostContext& ctx) {
  const damage::DamageStats& s = damage::damage_hub().stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "[damage] queued={} applied={} skipped={} dropped={} rejected={} frames={} shim refused={}",
            s.ops_queued, s.ops_applied, s.ops_skipped, s.ops_dropped, s.hits_rejected, s.frames, s.raises_failed);
}

}  // namespace x4mp::features
