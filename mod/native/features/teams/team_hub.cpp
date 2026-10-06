#include "features/teams/team_hub.h"

#include <charconv>

namespace x4mp::features::teams {

const char* state_name(SetupState s) noexcept {
  switch (s) {
    case SetupState::Off: return "off";
    case SetupState::Starting: return "starting";
    case SetupState::Ok: return "ok";
    case SetupState::Failed: return "failed";
    default: return "unknown";
  }
}

namespace {
bool parse_int(std::string_view s, long long& out) {
  if (s.empty()) return false;
  const auto* end = s.data() + s.size();
  const auto r = std::from_chars(s.data(), end, out);
  return r.ec == std::errc() && r.ptr == end;
}

std::vector<std::string_view> split(std::string_view text, char sep) {
  std::vector<std::string_view> out;
  std::size_t pos = 0;
  while (true) {
    const auto next = text.find(sep, pos);
    if (next == std::string_view::npos) {
      out.push_back(text.substr(pos));
      return out;
    }
    out.push_back(text.substr(pos, next - pos));
    pos = next + 1;
  }
}
}  // namespace

MdReport parse_md_report(std::string_view text) {
  MdReport r;
  const auto parts = split(text, ';');
  long long n = 0;
  if (parts.size() >= 3 && parts[0] == "E" && parse_int(parts[1], n) && n >= 0) {
    r.valid = true;
    r.error = true;
    r.seq = static_cast<std::uint32_t>(n);
    r.reason = std::string(parts[2]);
    return r;
  }
  if (parts.size() != 7 || parts[0] != "R") return r;
  long long v[6] = {};
  for (int i = 0; i < 6; ++i) {
    if (!parse_int(parts[static_cast<std::size_t>(i) + 1], v[i]) || v[i] < 0) return r;
  }
  r.valid = true;
  r.seq = static_cast<std::uint32_t>(v[0]);
  r.active = static_cast<int>(v[1]);
  r.mismatches = static_cast<int>(v[2]);
  r.player_locked = static_cast<int>(v[3]);
  r.slots = static_cast<int>(v[4]);
  r.relations = static_cast<int>(v[5]);
  return r;
}

void TeamHub::on_welcome(std::span<const std::uint8_t> payload) {
  if (!model_.apply_welcome(payload)) return;
  // A (resumed) Welcome carries the full picture: send it again even when it equals the last plan (the DLL may be new, the MD state may be gone).
  sent_plan_opt_.reset();
  dirty_ = true;
  dirty_reason_ = "welcome";
}

void TeamHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id) {
  self_id_ = self_id;
  if (model_.apply_frame(type, payload, self_id)) {
    dirty_ = true;
    if (dirty_reason_.empty()) dirty_reason_ = "teams";
  }
}

void TeamHub::session_ended() {
  model_.reset();
  self_id_ = 0;
  dirty_ = false;
  dirty_reason_.clear();
  sent_plan_opt_.reset();
  state_ = SetupState::Unknown;
  detail_.clear();
}

std::optional<Pending> TeamHub::poll(bool universe_ready, std::uint64_t universe_epoch, double delta_s) {
  if (state_ == SetupState::Starting) {
    waited_s_ += delta_s;
    if (waited_s_ >= kReportTimeoutS) {
      state_ = SetupState::Failed;
      detail_ = "no_md_report";
    }
  }
  if (!model_.has_baseline() || !universe_ready) return std::nullopt;
  const Plan plan = model_.plan(own_value_);
  if (plan.slots.empty()) {
    if (dirty_) dirty_ = false;
    dirty_reason_.clear();
    return std::nullopt;
  }
  const bool new_epoch = universe_epoch != applied_epoch_;
  if (!new_epoch && !dirty_) return std::nullopt;
  if (!new_epoch && sent_plan_opt_ && *sent_plan_opt_ == plan) {  // the change did not alter the plan (a relation between teams we do not use ...)
    dirty_ = false;
    dirty_reason_.clear();
    return std::nullopt;
  }
  Pending p;
  p.plan = plan;
  p.skip_known = skip_known_;
  p.seq = ++seq_;
  p.reason = new_epoch ? "universe_ready" : (dirty_reason_.empty() ? "teams" : dirty_reason_);
  sent_plan_opt_ = plan;
  sent_plan_ = plan;
  applied_epoch_ = universe_epoch;
  dirty_ = false;
  dirty_reason_.clear();
  state_ = SetupState::Starting;
  detail_.clear();
  waited_s_ = 0.0;
  return p;
}

void TeamHub::send_failed() {
  dirty_ = true;
  sent_plan_opt_.reset();
  applied_epoch_ = ~std::uint64_t{0};
  state_ = SetupState::Failed;
  detail_ = "raise_lua_failed";
}

bool TeamHub::on_md_report(std::string_view text) {
  const MdReport r = parse_md_report(text);
  if (!r.valid || r.seq != seq_ || seq_ == 0) return false;  // stale (an older plan) or garbage
  if (r.error) {
    state_ = SetupState::Failed;
    detail_ = "md_error:" + r.reason;
    return true;
  }
  const bool ok = r.active == r.slots && r.mismatches == 0 && r.player_locked == 0 &&
                  r.slots == static_cast<int>(sent_plan_.slots.size()) && r.relations == static_cast<int>(sent_plan_.relations.size());
  if (ok) {
    state_ = SetupState::Ok;
    detail_.clear();
    ++reports_ok_;
  } else {
    state_ = SetupState::Failed;
    detail_ = "active=" + std::to_string(r.active) + "/" + std::to_string(r.slots) + " relation_mismatches=" + std::to_string(r.mismatches) +
              " player_locked=" + std::to_string(r.player_locked);
  }
  return true;
}

std::string TeamHub::faction_of_team(std::uint16_t team_id) const {
  const auto slot = model_.slot_of_team(team_id);
  return slot == 0 ? std::string() : "x4mp_team_" + std::to_string(slot);
}

TeamHub& team_hub() {
  static TeamHub hub;
  return hub;
}

}  // namespace x4mp::features::teams
