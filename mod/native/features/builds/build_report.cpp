// features/builds: the client half of M5-18 (see build_report.h for the contract).
#include "features/builds/build_report.h"

#include <utility>

namespace x4mp::features::builds {

bool BuildReporter::on_event(const events::MdEvent& event) {
  if (event.kind != events::EventKind::Build) return false;
  if (event.component == 0 || event.macro.empty()) {
    ++stats_.bad;  // the ring line cannot name the station: nothing to ask the authority for
    return true;
  }
  if (!seen_.insert(event.component).second) {
    ++stats_.deduped;  // the same build reported twice (a double-fired cue, a replayed frame)
    return true;
  }
  if (seen_.size() > kMaxSeen) {
    seen_.clear();
    seen_.insert(event.component);
  }
  const std::uint64_t now = deps_.now_ms ? deps_.now_ms() : 0;
  if (report(event.component, event.macro, now)) return true;
  ++stats_.unresolved;  // no sector index or no pose yet: the galaxy map or the component is not ready
  if (pending_.size() >= kMaxPending) {
    pending_.erase(pending_.begin());
    ++stats_.dropped;
  }
  pending_.push_back(Pending{event.component, event.macro, now});
  return true;
}

void BuildReporter::retry(std::uint64_t now_ms) {
  if (pending_.empty()) return;
  std::vector<Pending> still;
  still.reserve(pending_.size());
  for (auto& item : pending_) {
    if (now_ms != 0 && item.at_ms != 0 && now_ms - item.at_ms > kRetryMs) {
      ++stats_.dropped;  // M5-18: the sector never became nameable; a guess would spawn a station in the wrong place
      continue;
    }
    if (!report(item.component, item.macro, item.at_ms)) still.push_back(std::move(item));
  }
  pending_ = std::move(still);
}

bool BuildReporter::report(std::uint64_t component, const std::string& macro, std::uint64_t now_ms) {
  (void)now_ms;
  BuildDescribe described;
  if (!deps_.describe || !deps_.describe(component, described) || described.sector == 0) return false;
  events::StationBuildData build;
  build.macro = macro;
  build.construction_plan.clear();  // M5 Q7: the macro plus its modules first; a plan id is the stretch
  build.sector = described.sector;
  for (int i = 0; i < 3; ++i) {
    build.position[i] = described.position[i];
    build.rotation[i] = described.rotation[i];
  }
  build.local_component_id = component;
  events::IntentRequest request;
  request.key_lo = ++next_key_;
  request.key_hi = deps_.self_player_id ? deps_.self_player_id() : 0;
  request.request_id = static_cast<std::uint32_t>(next_key_);
  request.player_id = static_cast<std::uint16_t>(request.key_hi);
  request.game_time = 0.0;  // not sampled here: the authority stamps the spawn with its own game time
  const auto bytes = events::encode_station_build(request, build);
  if (!deps_.send_control || !deps_.send_control(X4MP::Proto::MsgType::Intent, std::span<const std::uint8_t>(bytes))) {
    ++stats_.failed;  // counted, not retried: a lost send is the link's problem, and a retry would spawn twice
    return true;
  }
  ++stats_.sent;
  if (outstanding_.size() >= 64) outstanding_.erase(outstanding_.begin());
  outstanding_.push_back(OutstandingBuild{request.request_id, component, described.sector, macro});
  return true;
}

bool BuildReporter::take_outstanding(std::uint32_t request_id, OutstandingBuild& out) {
  for (auto it = outstanding_.begin(); it != outstanding_.end(); ++it) {
    if (it->request_id != request_id) continue;
    out = std::move(*it);
    outstanding_.erase(it);
    return true;
  }
  return false;
}

void BuildReporter::reset() {
  stats_ = BuildReportStats{};
  seen_.clear();
  pending_.clear();
  outstanding_.clear();
  next_key_ = 0;
}

}  // namespace x4mp::features::builds
