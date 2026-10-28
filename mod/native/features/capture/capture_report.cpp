#include "features/capture/capture_report.h"

#include <utility>

namespace x4mp::features::capture {

namespace {
constexpr const char* kPlayerFaction = "player";
}  // namespace

CaptureReporter::CaptureReporter(CaptureReportDeps deps) : deps_(std::move(deps)) {}

bool CaptureReporter::on_owner_change(std::uint64_t component) {
  if (component == 0 || !deps_.ghost_net_id) {
    ++stats_.bad;
    return false;
  }
  const std::uint32_t net_id = deps_.ghost_net_id(component);
  if (net_id == 0) {
    ++stats_.not_ghost;
    return false;
  }
  const std::string owner = deps_.owner_faction ? deps_.owner_faction(component) : std::string();
  if (owner.empty()) {
    ++stats_.unknown_owner;
    return false;
  }
  if (owner != kPlayerFaction) {
    ++stats_.not_player;
    return false;
  }
  const std::uint64_t now = deps_.now_ms ? deps_.now_ms() : 0;
  const auto seen = reported_ms_.find(net_id);
  if (seen != reported_ms_.end() && now >= seen->second && now - seen->second < kDedupeMs) {
    ++stats_.deduped;
    return false;
  }
  if (reported_ms_.size() >= kMaxSeen) reported_ms_.clear();  // a bound, not a cache: forget and report again

  events::IntentRequest request;
  request.key_lo = ++next_key_;
  request.key_hi = deps_.self_player_id ? deps_.self_player_id() : 0;
  request.request_id = static_cast<std::uint32_t>(next_key_);
  request.player_id = static_cast<std::uint16_t>(request.key_hi);
  request.game_time = 0.0;

  events::CaptureReportData data;
  data.target = net_id;
  data.new_owner_ref = 0;  // the authority resolves the team; a client never names it (M5-21)

  const auto bytes = events::encode_capture_report(request, data);
  if (!deps_.send_control) {
    ++stats_.send_failed;
    return false;
  }
  if (!deps_.send_control(X4MP::Proto::MsgType::Intent, std::span<const std::uint8_t>(bytes))) {
    ++stats_.send_failed;
    return false;
  }
  reported_ms_[net_id] = now;
  ++stats_.sent;
  return true;
}

void CaptureReporter::reset() {
  stats_ = CaptureReportStats{};
  reported_ms_.clear();
  next_key_ = 0;
}

}  // namespace x4mp::features::capture
