#include "features/teams/teams_feature.h"

#include <algorithm>
#include <mutex>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

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

TeamsFeature::TeamsFeature() : inbox_(std::make_shared<Inbox>()) {}

void TeamsFeature::on_init(host::HostContext& ctx) {
  auto inbox = inbox_;
  // The MD report, forwarded by ui/x4mp_teams.lua. May arrive on any thread: copy only.
  const bool ok = ctx.platform.subscribe_event("x4mp.teams_md", [inbox](std::string_view text) { inbox->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "teams: bridge verb x4mp.teams_md {}", ok ? "subscribed" : "not available");
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

  const auto before = hub.state();
  if (const auto pending = hub.poll(info.universe_ready, ctx.gates.universe_epoch, info.delta_s)) {
    const std::string json = teams::plan_json(pending->plan, pending->seq, pending->reason);
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
