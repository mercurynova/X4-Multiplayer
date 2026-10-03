#pragma once
// features/teams: the team factions in game (M3-08; docs/m3-plan.md 4.9, docs/mod-design.md 11.5/11.6, ADR-016/047).
//
//   libraries/factions.xml + colors.xml (extension x4mp): x4mp_team_1..8, inactive until a session activates them.
//   server Welcome / TeamTable / TeamRelations / TeamMemberChanged -> the join feature hands the frames to team_hub() (one hook line each in
//   join_feature.cpp) -> this feature, every frame: team_hub().poll(...) -> a plan -> raise_lua "x4mp.teams_apply" (JSON, plan_json()) ->
//   ui/x4mp_teams.lua flattens it and fires AddUITriggeredEvent("X4MP_Teams", "apply", {...}) -> md/x4mp_teams.xml unlocks, activates, sets the
//   relations and locks the team factions, reads everything back and answers with a report -> Lua verb x4mp.teams_md -> on_frame ->
//   team_hub().on_md_report() -> team_setup_state (NodeStats) and the self-test line team.factions.
//
// Main-thread rules: the bridge verb callback only copies the text into an inbox; everything else runs in on_frame. This feature makes no
// game call itself (MD does the work); the self-test reads GetAllFactions through the platform export lookup, on the frame thread.

#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "host/feature.h"

namespace x4mp::features {

class TeamsFeature final : public host::IFeature {
 public:
  struct Counters {
    std::uint32_t applies = 0;
    std::uint32_t send_failed = 0;
    std::uint32_t reports = 0;
    std::uint32_t stale_reports = 0;
  };

  TeamsFeature();
  [[nodiscard]] std::string_view name() const noexcept override { return "teams"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  [[nodiscard]] const Counters& counters() const noexcept { return counters_; }

 private:
  struct Inbox;
  std::shared_ptr<Inbox> inbox_;
  Counters counters_;
  std::string last_state_logged_;
};

namespace teams {
// The x4mp_team_* factions the game lists (GetAllFactions, hidden included). nullopt when the exports are not available.
[[nodiscard]] std::optional<std::vector<std::string>> game_team_factions(host::IPlatform& platform);
}  // namespace teams

}  // namespace x4mp::features
