#pragma once
// features/teams/team_plan: the team model the mod keeps from the server (Welcome / TeamTable / TeamRelations / TeamMemberChanged) and the
// mapping "team matrix -> faction plan -> ordered MD call list" (M3-08; ADR-016 with the 2026-10-01 correction, docs/mod-design.md 11.5/11.6).
// SDK-free and free of the session layer, so Catch2 tests it directly.
//
// Faction codes in a plan: 0 = the game's `player` faction, 1..8 = x4mp_team_<n> (the slot, TeamInfo.faction_slot).
//
// Rules (docs/m3-plan.md 4.9):
//   * team factions: every team with a slot 1..8 in the table is activated;
//   * team <-> team: one value per pair from the matrix (Allied +0.75, Neutral 0, Hostile -1.0; the default relation for pairs not listed);
//   * player <-> team: only when the local player's own team is known: +1.0 to the own team, the matrix value own<->other for the rest.
//     Without an own team nothing about `player` is touched. `player` is NEVER locked, only the team factions are (ADR-016 correction);
//   * the call order MD runs (md/x4mp_teams.xml) is build_calls(): unlock every team faction, activate + mark known, set every relation in both
//     directions, lock every team faction again.

#include <cstdint>
#include <map>
#include <span>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace x4mp::features::teams {

enum class Rel : std::int8_t { Hostile = -1, Neutral = 0, Allied = 1 };  // = TeamRelation on the wire

inline constexpr double kAlliedValue = 0.75;  // X4 ally band, ADR-016
inline constexpr double kOwnTeamValue = 1.0;
inline constexpr double kHostileValue = -1.0;
inline constexpr int kMaxSlots = 8;

[[nodiscard]] double relation_value(Rel rel) noexcept;

struct TeamEntry {
  std::uint16_t id = 0;
  std::uint8_t slot = 0;  // 0 = none
  std::string name;
  std::uint32_t color_rgb = 0;
  std::vector<std::uint16_t> members;
};

struct RelationTriple {
  std::uint8_t a = 0;
  std::uint8_t b = 0;
  double value = 0.0;
  bool operator==(const RelationTriple&) const = default;
};

struct Plan {
  std::vector<std::uint8_t> slots;        // ascending, 1..8
  std::vector<RelationTriple> relations;  // team pairs (a < b) ascending, then player (a = 0) vs each slot ascending
  std::uint8_t own_slot = 0;              // 0 = the local player has no team factions slot
  bool operator==(const Plan&) const = default;
};

enum class Op : std::uint8_t { Unlock, Activate, Known, SetRelation, Lock };

struct Call {
  Op op = Op::Unlock;
  std::uint8_t a = 0;  // faction code
  std::uint8_t b = 0;  // SetRelation: the other faction code
  double value = 0.0;  // SetRelation
  bool operator==(const Call&) const = default;
};

// The ordered MD call list: Unlock(s) for every slot, then Activate(s) + Known(s) per slot, then SetRelation(a,b) and SetRelation(b,a) per triple,
// then Lock(s) for every slot. Empty for a plan without slots.
[[nodiscard]] std::vector<Call> build_calls(const Plan& plan);
// "unlock 1", "activate 2", "known 2", "relation 0 1 1.00", "lock 1" (for logs and tests).
[[nodiscard]] std::string call_text(const Call& call);
// The payload of the Lua topic x4mp.teams_apply: {"v":1,"seq":N,"reason":"..","own":S,"slots":[..],"rel":[[a,b,value],..]}
[[nodiscard]] std::string plan_json(const Plan& plan, std::uint32_t seq, std::string_view reason);

// What the mod knows about the session's teams.
class TeamModel {
 public:
  void reset();
  // Welcome (the raw frame): full TeamTable + TeamRelations + the own team. false when the payload is not a valid Welcome.
  bool apply_welcome(std::span<const std::uint8_t> payload);
  // TeamTable / TeamRelations / TeamMemberChanged frames; anything else is ignored. true when the model changed.
  bool apply_frame(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);

  [[nodiscard]] Plan plan() const;
  [[nodiscard]] std::uint16_t own_team() const noexcept { return own_team_; }
  [[nodiscard]] bool has_baseline() const noexcept { return baseline_; }
  [[nodiscard]] const std::map<std::uint16_t, TeamEntry>& teams() const noexcept { return teams_; }
  [[nodiscard]] Rel relation(std::uint16_t team_a, std::uint16_t team_b) const;
  [[nodiscard]] std::uint8_t slot_of_team(std::uint16_t team_id) const;

 private:
  std::map<std::uint16_t, TeamEntry> teams_;
  std::map<std::pair<std::uint16_t, std::uint16_t>, Rel> matrix_;  // key (min, max)
  Rel default_ = Rel::Neutral;
  std::uint16_t own_team_ = 0;
  bool baseline_ = false;
};

}  // namespace x4mp::features::teams
