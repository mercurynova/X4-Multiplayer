#pragma once
// features/avatars: the pure (SDK-free, session-free) pieces of the authority's avatars (M3-11; docs/m3-plan.md 4.1, 4.3, section 6.3 row M3-11).
//
//   AvatarSettings     Avatars.StarterShipMacro / StarterLoadout / SpawnOffsetMeters, as the server pushes them (ServerSettingsUpdate)
//   resolve_starter()  THE one place the avatar ship macro + loadout come from (user answer Q5 + the sitting-0 note: a basic early-game
//                      loadout, never the SpawnObjectAtPos2 default equipment). A later milestone picks it per team origin / race here.
//   place_near()       where a NEW avatar appears next to the host's ship (a per-slot spot 1x..2x the spawn offset away)
//   Record             what the authority remembers about one avatar (identity for the binder after a save load, pose for a lost one)
//   bind_records()     the AvatarBinder: matches records to the team-owned ships a loaded save contains, by idcode (component ids change
//                      on every save load, session-4 sitting 0 S13.8), never by a stored id
//   VelocityEstimator  smoothed velocity of a driven avatar from its rendered positions (the MD velocity hint, spike S13.2 mode c)
//
// Units: metres, radians (UIPosRot angles are radians, S13.4). No game calls in this file.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::features::avatars {

inline constexpr std::string_view kBasicStarterMacro = "ship_arg_s_fighter_01_a_macro";  // the Argon Elite (Q5)
inline constexpr std::string_view kAvatarNamePrefix = "[MP] ";
inline constexpr std::string_view kTeamFactionPrefix = "x4mp_team_";
inline constexpr double kDefaultSpawnOffsetM = 300.0;
inline constexpr int kAvatarMinHullPercent = 100;  // Q11: the avatar cannot be killed before M5 (min hull)

struct Pose {
  double x = 0, y = 0, z = 0;
  double yaw = 0, pitch = 0, roll = 0;
  bool operator==(const Pose&) const = default;
};

[[nodiscard]] double distance_m(const Pose& a, const Pose& b) noexcept;

// ---- settings -----------------------------------------------------------------------------------------------------------------
struct AvatarSettings {
  std::string starter_macro;    // "" = the default (kBasicStarterMacro)
  std::string starter_loadout;  // "" = the basic early-game loadout (md/x4mp_avatars.xml)
  double spawn_offset_m = kDefaultSpawnOffsetM;

  // One ServerSettingsUpdate entry. true when the key was one of ours (an invalid value keeps the old/default value and returns true).
  bool apply(std::string_view key, std::string_view value);
};

// Macro / loadout ids are game data ids: lower-case letters, digits and underscore only.
[[nodiscard]] bool valid_game_id(std::string_view id) noexcept;

struct StarterSpec {
  std::string macro;
  std::string loadout;  // vanilla loadout id, "" when the basic loadout is meant
  bool basic_loadout = true;
};
// The ONE place the avatar's ship macro + loadout are chosen. team / race are for the later per-team-origin table (ADR-049/051); unused today.
[[nodiscard]] StarterSpec resolve_starter(const AvatarSettings& settings, std::uint16_t team, std::string_view race = {});

// "[MP] <player>" with control characters removed and at most 48 characters of the player name.
[[nodiscard]] std::string avatar_name(std::string_view player_name);

// ---- placement ----------------------------------------------------------------------------------------------------------------
// A spot for the avatar of `player_id` around `host`: direction by the golden angle of the slot, distance between offset_m and 2 x offset_m
// (so everyone starts together without overlapping), +-40 m of height alternating by slot. Orientation = the host's.
[[nodiscard]] Pose place_near(const Pose& host, double offset_m, std::uint16_t player_id);

// Entity kind (EntityKind numbering of common.fbs, mirrored by authority::SpawnKind) of a ship macro: _xs_ 1, _s_ 2, _m_ 3, _l_ 4, _xl_ 5, else 0.
[[nodiscard]] std::uint8_t ship_kind_of_macro(std::string_view macro) noexcept;

// ---- records ------------------------------------------------------------------------------------------------------------------
struct Record {
  std::uint16_t player_id = 0;
  std::uint16_t team = 0;
  std::uint32_t net_id = 0;
  std::string name;          // "[MP] Alice" (never "(offline)": that is added by the clients)
  std::string macro;
  std::string idcode;
  std::string owner;         // "x4mp_team_<slot>"
  std::string sector_macro;  // "" when unknown
  Pose pose;
  bool online = false;       // the player is connected (driven); false = parked

  bool operator==(const Record&) const = default;
};

// One line per record, fields separated by '|' (sanitised: no '|' or line breaks inside a field), preceded by "x4av 1". Tolerant reader:
// lines that do not parse are skipped and counted. Local component ids are NOT part of it (they change on every load).
[[nodiscard]] std::string records_to_text(const std::vector<Record>& records);
struct ParsedRecords {
  std::vector<Record> records;
  std::size_t bad_lines = 0;
  bool header_ok = false;
};
[[nodiscard]] ParsedRecords records_from_text(std::string_view text);

// ---- the binder ---------------------------------------------------------------------------------------------------------------
struct Candidate {
  std::uint64_t id = 0;
  std::string idcode;
  std::string name;
  std::string owner;  // faction id
  Pose pose;          // sector-relative; only used to tell equal candidates apart
};

struct BindResult {
  std::vector<std::pair<std::size_t, std::uint64_t>> bound;  // (record index, candidate id)
  std::vector<std::size_t> lost;                              // records nothing matched (to be respawned at their last pose)
  std::vector<std::uint64_t> strays;                          // "[MP] " ships of a team faction no record claims (logged, never removed here)
};

// Rules (the owner faction must always be equal): tier 0 idcode + name equal, tier 1 idcode equal (the name was never applied), tier 2 name
// equal (the idcode is unknown or changed). Pairs are taken best tier first, then nearest first; one candidate is never given to two
// records and one record gets one candidate. Deterministic.
[[nodiscard]] BindResult bind_records(const std::vector<Record>& records, const std::vector<Candidate>& candidates);

// ---- velocity hint ------------------------------------------------------------------------------------------------------------
class VelocityEstimator {
 public:
  struct V {
    double x = 0, y = 0, z = 0;
  };
  // A rendered position at time t (seconds, any monotonic clock). `discontinuity` (sector change, snap, hide) restarts the estimate.
  void sample(double t_s, double x, double y, double z, bool discontinuity);
  [[nodiscard]] V velocity() const noexcept { return v_; }
  [[nodiscard]] bool valid() const noexcept { return valid_; }
  void reset() noexcept { *this = VelocityEstimator{}; }

 private:
  bool have_prev_ = false, valid_ = false;
  double t_ = 0, x_ = 0, y_ = 0, z_ = 0;
  V v_{};
};

}  // namespace x4mp::features::avatars
