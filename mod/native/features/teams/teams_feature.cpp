#include "features/teams/teams_feature.h"

#include <algorithm>
#include <mutex>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/events/events_hub.h"
#include "features/teams/team_hub.h"
#include "features/teams/team_plan.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 16;
constexpr std::size_t kMaxReportBytes = 256;
}  // namespace

struct TeamsFeature::Inbox {
  std::mutex m;
  std::vector<std::string> q;
  void push(std::string_view text) {
    if (text.size() > kMaxReportBytes) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(text);
  }
  std::vector<std::string> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

TeamsFeature::TeamsFeature() : inbox_(std::make_shared<Inbox>()), invite_inbox_(std::make_shared<Inbox>()) {}

void TeamsFeature::on_init(host::HostContext& ctx) {
  auto inbox = inbox_;
  // The MD report, forwarded by ui/x4mp_teams.lua. May arrive on any thread: copy only.
  const bool ok = ctx.platform.subscribe_event("x4mp.teams_md", [inbox](std::string_view text) { inbox->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: bridge verb x4mp.teams_md {}", ok ? "subscribed" : "not available");
  // M5b: the player's own team actions (invite a player, answer an invitation), asked for by ui/x4mp_teams.lua.
  auto invites = invite_inbox_;
  const bool invite_ok = ctx.platform.subscribe_event("x4mp.teams_invite", [invites](std::string_view text) { invites->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: bridge verb x4mp.teams_invite {}", invite_ok ? "subscribed" : "not available");
  teams::team_hub().set_diag(teams::own_team_value(ctx.config.diag.team_self_relation_100), ctx.config.diag.no_set_faction_known);
  if (ctx.config.diag.team_self_relation_100) {
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "teams: diag.team_self_relation_100 is set: player <-> own team is written as +1.0 (the engine's self range) instead of +0.99");
  }
  if (ctx.config.diag.no_set_faction_known) {
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "teams: diag.no_set_faction_known is set: the team apply skips set_faction_known (M3-28 experiment)");
  }
}

void TeamsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  auto& hub = teams::team_hub();
  for (const auto& raw : inbox_->take()) {
    // {"v":1,"data":"R;..."} from ui/x4mp_teams.lua
    std::string text;
    try {
      const auto j = nlohmann::json::parse(raw);
      if (j.is_object() && j.contains("data") && j["data"].is_string()) text = j["data"].get<std::string>();
    } catch (const nlohmann::json::exception&) {
    }
    const bool accepted = hub.on_md_report(text);
    if (accepted) {
      ++counters_.reports;
      X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: MD report '{}' -> {}{}{}", text, teams::state_name(hub.state()),
                hub.detail().empty() ? "" : " ", hub.detail());
    } else {
      ++counters_.stale_reports;
      X4MP_CLOG(ctx.log, Cat::Md, Level::Debug, "teams: stale or invalid MD report ignored '{}'", text);
    }
  }

  // ---- M5b: the player's own team actions (invite a player, answer an invitation) -------------------------------
  // The UI asks here; the hub builds the frames and matches the answers; nothing leaves this frame unless the link
  // takes it, and what it refuses waits in the queue for the next frame.
  for (const auto& raw : invite_inbox_->take()) {
    std::string op;
    int to = 0;
    unsigned long long id = 0;
    bool accept = false;
    try {
      const auto j = nlohmann::json::parse(raw);
      if (j.is_object()) {
        if (j.contains("op") && j["op"].is_string()) op = j["op"].get<std::string>();
        if (j.contains("to") && j["to"].is_number_integer()) to = j["to"].get<int>();
        if (j.contains("id") && j["id"].is_number_unsigned()) id = j["id"].get<unsigned long long>();
        if (j.contains("accept") && j["accept"].is_boolean()) accept = j["accept"].get<bool>();
      }
    } catch (const nlohmann::json::exception&) {
    }
    std::string error;
    bool queued = false;
    if (op == "invite") {
      queued = hub.invite(static_cast<std::uint16_t>(to), error);
    } else if (op == "respond") {
      queued = hub.invite_respond(static_cast<std::uint32_t>(id), accept, error);
    } else {
      error = "unknown op";
    }
    if (queued) {
      ++counters_.invites;
      continue;
    }
    ++counters_.invite_failed;
    const nlohmann::json out = {{"v", 1}, {"kind", "error"}, {"op", op}, {"error", error}};
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "teams: invite request '{}' refused locally: {}", op, error);
    ctx.platform.raise_lua("x4mp.teams_invite", out.dump());
  }

  for (const auto& frame : hub.take_unsent()) {
    const std::span<const std::uint8_t> payload(frame.bytes.data(), frame.bytes.size());
    if (!events::events_hub().send_control(frame.type, payload)) {
      ++counters_.send_failed;
      hub.requeue(frame);
      break;  // the link is down; the queue keeps the order for the next frame
    }
  }

  for (const auto& notice : hub.take_notices()) {
    ++counters_.invite_notices;
    const nlohmann::json out = {{"v", 1},   {"kind", "notice"}, {"id", notice.invite_id},      {"from", notice.from_player},
                                {"team", notice.from_team}, {"name", notice.team_name},       {"expires", notice.expires_in_s}};
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: invite {} from player {} to team {} '{}'", notice.invite_id, notice.from_player,
              notice.from_team, notice.team_name);
    ctx.platform.raise_lua("x4mp.teams_invite", out.dump());
  }

  for (const auto& result : hub.take_results()) {
    const bool ok = result.status == 0;
    const std::string reason = teams::reject_token(result.reason);
    const nlohmann::json out = {{"v", 1},      {"kind", "result"}, {"op", result.op}, {"ok", ok},
                                {"reason", reason}, {"team", result.team_id}, {"detail", result.detail}};
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: {} -> {}{}{}", result.op, ok ? "ok" : "rejected",
              ok ? "" : (" " + reason), result.detail.empty() ? "" : (": " + result.detail));
    ctx.platform.raise_lua("x4mp.teams_invite", out.dump());
  }

  const auto before = hub.state();
  if (const auto pending = hub.poll(info.universe_ready, ctx.gates.universe_epoch, info.delta_s)) {
    const std::string json = teams::plan_json(pending->plan, pending->seq, pending->reason, pending->skip_known);
    if (ctx.platform.raise_lua("x4mp.teams_apply", json)) {
      ++counters_.applies;
      std::string slots;
      for (const auto s : pending->plan.slots) slots += (slots.empty() ? "" : ",") + std::to_string(s);
      X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: apply seq={} reason={} slots=[{}] relations={} own_slot={}", pending->seq, pending->reason, slots,
                pending->plan.relations.size(), static_cast<int>(pending->plan.own_slot));
      for (const auto& call : teams::build_calls(pending->plan)) {
        X4MP_CLOG(ctx.log, Cat::Md, Level::Debug, "teams: md call {}", teams::call_text(call));
      }
    } else {
      ++counters_.send_failed;
      hub.send_failed();
      X4MP_CLOG(ctx.log, Cat::Md, Level::Debug, "teams: raise_lua x4mp.teams_apply failed (seq {}), retrying next frame", pending->seq);
    }
  }
  if (hub.state() != before && hub.state() == teams::SetupState::Failed && last_state_logged_ != hub.detail()) {
    last_state_logged_ = hub.detail();
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "teams: team setup failed ({}). The team factions may be missing (libraries/factions.xml) or md/x4mp_teams.xml did not run",
              hub.detail());
    ctx.platform.raise_lua("x4mp.notify", R"({"v":1,"level":"error","text":"X4MP: the team factions or their relations could not be applied. Team colours and relations may be wrong. Details are in x4mp.log."})");
  }
}

namespace teams {

namespace {
using NumFactionsFn = std::uint32_t (*)(bool includehidden);
using FactionsFn = std::uint32_t (*)(const char** result, std::uint32_t resultlen, bool includehidden);
}  // namespace

std::optional<std::vector<std::string>> game_team_factions(host::IPlatform& platform) {
  const auto num = reinterpret_cast<NumFactionsFn>(platform.get_game_function("GetNumAllFactions"));
  const auto list = reinterpret_cast<FactionsFn>(platform.get_game_function("GetAllFactions"));
  if (num == nullptr || list == nullptr) return std::nullopt;
  std::vector<std::string> out;
  const std::uint32_t n = num(true);
  if (n == 0) return out;
  std::vector<const char*> ids(n, nullptr);
  const std::uint32_t got = list(ids.data(), n, true);
  for (std::uint32_t i = 0; i < got && i < n; ++i) {
    if (ids[i] != nullptr && std::string_view(ids[i]).starts_with("x4mp_team_")) out.emplace_back(ids[i]);
  }
  std::sort(out.begin(), out.end());
  return out;
}

}  // namespace teams

}  // namespace x4mp::features
