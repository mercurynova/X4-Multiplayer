// hostsim fake universe (M3-04): a handful of objects, sectors, one player ship with seat / docked / SETA state, and
// scripted flight paths for that ship. It sits behind the M3 game exports (SpawnObjectAtPos2, SetObjectSectorPos,
// GetObjectPositionInSector, ...) that Host registers, and behind the script commands `ship`, `seat`, `dock`, `seta`,
// `world` and `expect-object`. Everything here is single-threaded by contract (the script thread = the main thread); Host records an
// off-thread game call as a contract violation before it reaches this class.
//
// This is NOT a simulation: positions change only when the mod calls SetObjectSectorPos or a `ship path` advances during
// `frame`. Units: metres; angles are stored raw as given (the mod and the script agree on the unit; paths write radians (S13.4: the game gives radians)).
#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include <x4_game_types.h>  // UIPosRot, UniverseID

namespace hostsim {

struct Obj {
  std::uint64_t id = 0;
  std::string cls = "ship";  // ship | station (GetContextByClass matches it)
  std::string macro, owner, name, idcode;
  std::uint64_t sector = 0;
  UIPosRot pos{};
  bool active = true;
  bool radar = false;
  bool wrecked = false;
  int min_hull = -1;                 // set by the emulated MD dress (x4mp.ghost_dress); -1 = never dressed
  double vx = 0, vy = 0, vz = 0;     // set by the emulated MD velocity hint (x4mp.ghost_velocity)
  long long velocity_hints = 0;
};

enum class PathKind { None, Circle, Line, Gate };

struct Vec3 {
  double x = 0, y = 0, z = 0;
};

struct Path {
  PathKind kind = PathKind::None;
  Vec3 center, from, to, exit;
  double radius = 0, speed = 0;   // m/s
  double angle = 0;               // circle phase (rad) / line progress (m)
  bool loop = false;
  std::uint64_t sector = 0;       // sector the path starts in
  std::uint64_t to_sector = 0;    // gate: sector entered at the end
};

class World {
 public:
  static constexpr std::uint64_t kPlayerEntity = 1000042;  // GetPlayerID() (hostsim has always answered this)
  static constexpr std::uint64_t kPlayerShip = 200001;
  static constexpr std::uint64_t kStation = 300001;
  static constexpr std::uint64_t kSector1 = 100001;
  static constexpr std::uint64_t kSector2 = 100002;

  World();

  // ---- state the exports read ----
  std::uint64_t player_ship = kPlayerShip;
  bool seat = false;      // the player sits in the pilot seat of player_ship
  bool docked = false;    // IsPlayerOccupiedShipDocked
  bool seta = false;      // IsSetaActive
  bool in_highway = false;  // GetContextByClass(ship, "highway") answers a highway (M3-09)
  bool controlled_when_docked = true;  // assumption pending S13.9: GetPlayerControlledShipID stays set while docked
  bool teleport_allowed = true;
  std::string teleport_reason = "hostsim: teleport denied";
  int spawn_fail_budget = 0;  // the next N spawns return 0
  // M3-10: factions the game lists (GetAllFactions); the mod's ghost spawn refuses an owner that is not in the list.
  std::vector<std::string> factions = {"player", "argon", "paranid", "xenon", "x4mp_team_1", "x4mp_team_2", "x4mp_team_3", "x4mp_team_4",
                                       "x4mp_team_5", "x4mp_team_6", "x4mp_team_7", "x4mp_team_8"};
  // M3-10: `world md-emulate on` makes the fake play the MD / Lua half of the ghost feature: x4mp.ghost_dress sets name + min hull,
  // x4mp.ghost_velocity stores the velocity hint, x4mp.teams_apply is answered with the matching x4mp.teams_md report (M3-08).
  bool md_emulate = false;
  std::vector<std::string> md_sector_map;  // `world md-sectors`: the x4mp.sector_map payloads (S;.. / E;n) sent when selfship asks (x4mp.sector_map_collect)
  long long dress_events = 0, velocity_events = 0, teams_applies = 0;

  // counters (expect-state)
  long long spawns = 0, set_pos_calls = 0, teleports = 0, owner_calls = 0, activate_calls = 0, radar_calls = 0, removed = 0;

  // ---- objects and sectors ----
  std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const UIPosRot& pos, const std::string& owner);  // 0 = refused
  Obj* find(std::uint64_t id);  // caller holds no lock; only the script thread uses the pointer
  [[nodiscard]] bool exists(std::uint64_t id) const;
  bool add_sector(std::uint64_t id) { return sectors_.insert(id).second; }
  [[nodiscard]] bool has_sector(std::uint64_t id) const { return sectors_.count(id) != 0; }
  bool remove(std::uint64_t id);  // never the player ship
  // A save load: every spawned (mod or scripted) object gets a new id, like the game's component ids; positions, names, id codes stay. The player ship,
  // the station and the sectors keep theirs. Returns how many objects changed id.
  std::size_t renumber();
  // The ships owned by a faction (GetAllFactionShips).
  [[nodiscard]] std::vector<std::uint64_t> ships_of(const std::string& faction) const;
  [[nodiscard]] std::vector<Obj> snapshot() const;
  [[nodiscard]] std::uint64_t last_spawned() const { return last_; }
  void set_last(std::uint64_t id) { last_ = id; }
  [[nodiscard]] std::size_t count() const;

  // ---- export semantics ----
  [[nodiscard]] std::uint64_t occupied() const { return seat ? player_ship : 0; }
  [[nodiscard]] std::uint64_t controlled() const { return seat && (!docked || controlled_when_docked) ? player_ship : 0; }
  [[nodiscard]] std::uint64_t container() const { return docked ? kStation : player_ship; }
  [[nodiscard]] std::uint64_t context(std::uint64_t id, const std::string& cls, bool include_self);
  [[nodiscard]] std::string can_teleport(std::uint64_t id);  // "granted" = allowed (as the real game), else the reason
  bool teleport(std::uint64_t id, bool allow_controlling);

  // ---- scripted player-ship flight ----
  void start_path(const Path& p);
  void stop_path() { path_.kind = PathKind::None; }
  [[nodiscard]] bool path_active() const { return path_.kind != PathKind::None; }
  void advance(double dt);  // game seconds; moves the player ship along the path
  [[nodiscard]] long long path_sector_changes() const { return gate_jumps_; }

  // ---- ghost error samples (the `expect-ghost` plumbing, filled in by M3-10) ----
  std::map<std::string, std::vector<double>> ghost_errors;  // player name -> error in metres

 private:
  std::map<std::uint64_t, Obj> objs_;
  std::set<std::uint64_t> sectors_;
  std::uint64_t next_id_ = 400001;
  std::uint64_t last_ = 0;
  Path path_;
  long long gate_jumps_ = 0;
};

// "x,y,z" -> Vec3 (nullopt on a malformed triple)
std::optional<Vec3> parse_vec3(const std::string& s);

}  // namespace hostsim
