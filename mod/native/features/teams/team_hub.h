#pragma once
// features/teams/team_hub: the hand-over point between the join feature's session pump and the teams feature (M3-08), and the apply state
// machine (SDK-free, tested without a game):
//
//   join feature: team_hub().on_welcome(payload) / on_frame_message(type, payload, self_id) / session_ended()   (main thread, like chat_hub)
//   teams feature: poll() each frame -> when a Pending comes back it raises x4mp.teams_apply (Lua -> AddUITriggeredEvent -> md/x4mp_teams.xml);
//                  the MD cue answers with one report string that Lua forwards (x4mp.teams_md) and on_md_report() turns into the state.
//
// A plan is (re)sent when: the universe becomes ready (a new universe epoch), a Welcome arrives (join, resume), or a TeamTable / TeamRelations /
// TeamMemberChanged frame changed the resulting plan. It is sent on the first poll() after the change, i.e. in the same frame as the frame that
// brought it (the teams feature runs after the join feature) -> "re-applied within 1 frame of a TeamRelations change".
//
// MD report format (md/x4mp_teams.xml): "R;<seq>;<active>;<mismatches>;<player_locked>;<slots>;<relations>" or "E;<seq>;<reason>".

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>

#include "features/teams/team_plan.h"

namespace x4mp::features::teams {

// = FeatureState on the wire (admin.fbs)
enum class SetupState : std::uint8_t { Unknown = 0, Off = 1, Starting = 2, Ok = 3, Failed = 4 };

[[nodiscard]] const char* state_name(SetupState s) noexcept;

struct Pending {
  Plan plan;
  std::uint32_t seq = 0;
  std::string reason;  // "universe_ready" | "welcome" | "teams"
};

struct MdReport {
  bool valid = false;
  bool error = false;  // "E;..."
  std::uint32_t seq = 0;
  int active = 0;
  int mismatches = 0;
  int player_locked = 0;
  int slots = 0;
  int relations = 0;
  std::string reason;  // E only
};

[[nodiscard]] MdReport parse_md_report(std::string_view text);

class TeamHub {
 public:
  static constexpr double kReportTimeoutS = 8.0;  // no MD answer within this much frame time -> Failed

  void on_welcome(std::span<const std::uint8_t> payload);
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);
  void session_ended();

  // Feature side. delta_s = frame time since the last call. nullopt = nothing to send.
  [[nodiscard]] std::optional<Pending> poll(bool universe_ready, std::uint64_t universe_epoch, double delta_s);
  // raise_lua failed: the plan goes out again on the next poll.
  void send_failed();
  // The MD answer. true when it was accepted (valid and for the current seq); the state changes accordingly.
  bool on_md_report(std::string_view text);

  [[nodiscard]] bool has_session() const noexcept { return model_.has_baseline(); }
  [[nodiscard]] SetupState state() const noexcept { return state_; }
  [[nodiscard]] const std::string& detail() const noexcept { return detail_; }
  [[nodiscard]] std::uint32_t seq() const noexcept { return seq_; }
  [[nodiscard]] const Plan& applied_plan() const noexcept { return sent_plan_; }
  [[nodiscard]] const TeamModel& model() const noexcept { return model_; }
  [[nodiscard]] std::uint32_t reports_ok() const noexcept { return reports_ok_; }

  // The faction id of a team (what M3-11/M3-10 pass to SpawnObjectAtPos2 / SetComponentOwner): "x4mp_team_<slot>", empty when the team is
  // unknown or has no slot.
  [[nodiscard]] std::string faction_of_team(std::uint16_t team_id) const;
  // True once the MD setup of the current universe reported Ok: the factions are active and the relations are in place.
  [[nodiscard]] bool factions_ready() const noexcept { return state_ == SetupState::Ok; }

 private:
  TeamModel model_;
  std::uint16_t self_id_ = 0;
  bool dirty_ = false;
  std::string dirty_reason_;
  std::optional<Plan> sent_plan_opt_;  // the plan of the last send (a poll with an equal plan and epoch sends nothing)
  Plan sent_plan_;
  std::uint64_t applied_epoch_ = ~std::uint64_t{0};
  std::uint32_t seq_ = 0;
  SetupState state_ = SetupState::Unknown;
  std::string detail_;
  double waited_s_ = 0.0;
  std::uint32_t reports_ok_ = 0;
};

TeamHub& team_hub();

}  // namespace x4mp::features::teams
