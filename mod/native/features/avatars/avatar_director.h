#pragma once
// features/avatars: AvatarDirector, the authority's avatar state machine (M3-11; docs/m3-plan.md 4.1, 4.3, 4.6, 4.8, session-4 sitting 0 S13.1/2/6/8).
// Pure C++ over IAvatarEnv (the game, the session, the clock and the stash behind one interface), so Catch2 tests it with a fake world
// and the real adapter (avatars_feature.cpp) is only plumbing.
//
// What it does, per player (an "avatar" = a real, persistent ship owned by the player's team faction x4mp_team_<slot>):
//   PlayerShip request  -> provision: wait until the team factions are active and the roster names the player, choose the spot next to the
//                          host's ship (place_near), ask MD for a SAFE position there (get_safe_pos: a spawn 300 m from a docked ship landed
//                          inside the station, S13.6), SpawnObjectAtPos2 under the team faction, ActivateObject(false) (inert), ask MD to dress it
//                          (name "[MP] <player>", min hull, early-game loadout, forced radar), then EntitySpawn{origin=PlayerShip,
//                          controller_player} to the server. The same player asking again (rejoin, held requests after an authority resume)
//                          gets the SAME avatar: a refreshing EntitySpawn, never a second ship.
//   PlayerState         -> samples of the avatar's Interpolator (core/ghost, server time); every frame the interpolated pose is put on the ship
//                          with SetObjectSectorPos (spike S13.2 mode c) and every 200 ms an MD velocity hint follows (so the target info shows a
//                          speed and the ship looks right).
//   player left         -> park: stop driving, keep the ship exactly where it is, EntityChange{Controller=0}. A parked avatar is snapped back to
//                          its pose when something pushes it (S13.2 collision note). The "(offline)" suffix is the clients' business.
//   EntityDespawn{Removed} (admin kick/ban with asset removal) -> remove the real ship through SafeRemove (never the player's own ship).
//   player moved team  -> (M3-18) the roster upsert carries a new team id: the avatar is re-owned to x4mp_team_<slot> (SetComponentOwner, still inert),
//                          the record follows (owner, team; persisted) and ONE EntityChange{Owner|OwnerTeam} goes to the server (mirror + all clients).
//                          Trigger = the roster, not ReassignPlayerAssets: that message is gated by MoveAssetsWithPlayer and says nothing about avatars.
//   save load / new universe -> ids are void: the AvatarBinder (bind_records) finds the ships again by idcode among the objects of the team
//                          factions; records nothing matches are respawned at their last pose; the survivors are announced again.
//
// Threading: everything on the frame thread. No game call outside step()/on_*() (which the feature calls from on_frame).

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/authority/entity_spawn.h"
#include "core/ghost/interpolator.h"
#include "features/avatars/avatar_plan.h"
#include "features/avatars/avatar_wire.h"

namespace x4mp::features::avatars {

struct HostPlace {
  std::uint64_t sector_id = 0;  // local sector component
  Pose pose;                    // the host ship (or what stands in for it), sector-relative
};

struct VelHint {
  std::uint64_t id = 0;
  double vx = 0, vy = 0, vz = 0;  // m/s
};

enum class StrKind : std::uint8_t { Macro, Faction };

enum class LogLevel : std::uint8_t { Debug, Info, Warn, Error };

class IAvatarEnv {
 public:
  virtual ~IAvatarEnv() = default;

  // ---- game ----
  virtual bool game_ready() = 0;        // the universe is ready and the needed exports exist
  virtual bool factions_ready() = 0;    // the team factions are active (team_hub().factions_ready())
  [[nodiscard]] virtual std::string faction_of_team(std::uint16_t team) = 0;  // "x4mp_team_<slot>", "" when unknown
  virtual std::optional<HostPlace> host_place() = 0;
  // Sector tables (index = GalaxyMetadata index, macro = the game's sector macro, id = the local component, void after every load).
  virtual std::uint64_t sector_id_of_index(std::uint16_t index) = 0;
  virtual std::uint64_t sector_id_of_macro(const std::string& macro) = 0;
  virtual std::string sector_macro_of_id(std::uint64_t id) = 0;
  virtual std::uint16_t sector_index_of_macro(const std::string& macro) = 0;
  virtual std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const Pose& pose, const std::string& owner) = 0;  // 0 = failed
  virtual void activate(std::uint64_t id, bool active) = 0;
  virtual bool set_owner(std::uint64_t id, const std::string& faction) = 0;  // SetComponentOwner; false = the call was not made (retried)
  virtual bool valid(std::uint64_t id) = 0;
  virtual std::string idcode(std::uint64_t id) = 0;
  virtual bool read_pose(std::uint64_t id, std::uint64_t& sector, Pose& pose) = 0;
  virtual bool set_pose(std::uint64_t id, std::uint64_t sector, const Pose& pose) = 0;
  virtual bool remove(std::uint64_t id) = 0;  // through SafeRemove; false when refused (the player's own ship, wrong thread, ...)
  // Every ship owned by a team faction (x4mp_team_*). nullopt = the enumeration is not available (never respawn on that).
  virtual std::optional<std::vector<Candidate>> team_candidates() = 0;
  // ---- MD (through Lua) ----
  virtual void ask_safepos(std::uint32_t seq, std::uint64_t sector, const Pose& wanted, double radius) = 0;
  virtual void ask_dress(std::uint32_t seq, std::uint64_t id, const std::string& name, const StarterSpec& starter, int min_hull_percent) = 0;
  virtual void send_velocity(const std::vector<VelHint>& hints) = 0;
  // ---- session ----
  virtual bool net_ready() = 0;  // welcomed as the authority; the ids and strings below are usable
  virtual std::uint32_t alloc_net_id() = 0;
  virtual std::uint32_t string_ref(StrKind kind, const std::string& value) = 0;
  virtual double game_time() = 0;  // > 0 when valid
  virtual bool send_spawn(const std::vector<authority::SpawnEntity>& entities, double game_time) = 0;
  virtual bool send_controller(std::uint32_t net_id, std::uint16_t player) = 0;
  // EntityChange{Owner|OwnerTeam}: the avatar now belongs to `team` / `faction` (M3-18, the player moved team). false = retried next frame.
  virtual bool send_owner(std::uint32_t net_id, std::uint16_t team, const std::string& faction) = 0;
  // ---- persistence, log ----
  virtual void save_records(const std::string& text) = 0;
  virtual void log(LogLevel level, const std::string& text) = 0;
};

struct DirectorStats {
  std::uint32_t requests = 0, provisioned = 0, refreshed = 0, spawn_failed = 0, safepos_replies = 0, safepos_timeouts = 0;
  std::uint32_t bound = 0, lost = 0, strays = 0, respawned = 0, parked = 0, removed = 0, remove_refused = 0;
  std::uint32_t states_in = 0, states_unknown = 0, set_pose_calls = 0, unmapped_sector = 0, repairs = 0, vel_hints = 0;
  std::uint32_t dress_ok = 0, dress_failed = 0, announced = 0, reowned = 0;
};

class AvatarDirector {
 public:
  enum class Stage : std::uint8_t { Unbound, Wanted, AwaitSafePos, Live, Failed };

  struct View {  // read-only snapshot of one avatar for logs, tests and stats
    Record rec;
    std::uint64_t local_id = 0;
    Stage stage = Stage::Unbound;
    bool suspended = false;
    std::size_t samples = 0;
  };

  static constexpr double kSafePosTimeoutS = 5.0;
  static constexpr double kSafePosRadiusM = 150.0;
  static constexpr double kRetryS = 2.0;
  static constexpr int kMaxSpawnTries = 5;
  static constexpr double kVelHintPeriodS = 0.2;

  explicit AvatarDirector(IAvatarEnv& env);
  ~AvatarDirector();
  AvatarDirector(const AvatarDirector&) = delete;
  AvatarDirector& operator=(const AvatarDirector&) = delete;

  void set_settings(const AvatarSettings& settings) { settings_ = settings; }
  [[nodiscard]] const AvatarSettings& settings() const noexcept { return settings_; }

  // The identity records of an earlier run (stash / file). Local ids are unknown: the binder finds the ships. `hint_ids` (player id -> local id)
  // are ids that the registry adoption kept; they are accepted only when the object still has the record's idcode and name.
  void load_records(const std::vector<Record>& records, const std::vector<std::pair<std::uint16_t, std::uint64_t>>& hint_ids = {});
  // The universe changed (save load): every local id is void, rebind on the next step.
  void new_universe();
  // The session (re)welcomed the authority: announce every avatar again on the next step (the server may have lost its mirror).
  void on_welcome() { announce_pending_ = true; }

  // ---- M3-22: the records belong to one session lineage (see Lineage in avatar_plan.h) ----
  // The lineage read from the file / stash together with the records (before load_records).
  void set_lineage(Lineage lineage) { lineage_ = std::move(lineage); }
  [[nodiscard]] const Lineage& lineage() const noexcept { return lineage_; }
  // The server's session id (lowercase hex) after a welcome. Records of another session (or without one) are dropped; one log line with the count.
  void on_session(const std::string& session);
  // A save THIS node is about to load (not a kept universe): only a checkpoint of the session keeps its avatars (those its manifest lists); any
  // other save (the plain start save) keeps none. One log line with the counts.
  void on_loaded_save(const std::string& sha_hex);
  // A checkpoint was stored: its manifest's avatars (idcodes) join the ledger (persisted at once).
  void on_checkpoint_stored(const std::string& sha_hex, const std::vector<std::string>& idcodes);

  // ---- inputs ----
  void on_roster(const RosterIn& roster);
  void on_player_ship(const PlayerShipReq& request);
  void on_player_state(const PlayerStateIn& state, std::int64_t arrival_server_us);
  void on_despawn(const DespawnIn& despawn);
  void on_safepos(std::uint32_t seq, bool ok, const Pose& pos);
  void on_dress(std::uint32_t seq, bool ok, const std::string& detail);

  // ---- every frame ----
  void step(double now_s, std::int64_t server_now_us);
  void persist_now();  // write the records (shutdown, checkpoint)

  // ---- queries ----
  // The records with their CURRENT poses (for the checkpoint manifest and the stash). Reads the game for live avatars.
  [[nodiscard]] std::vector<Record> snapshot();
  [[nodiscard]] std::vector<View> views() const;
  [[nodiscard]] std::size_t size() const noexcept { return avatars_.size(); }
  [[nodiscard]] std::size_t live_count() const noexcept;
  [[nodiscard]] std::uint32_t max_net_id() const noexcept;
  [[nodiscard]] const DirectorStats& stats() const noexcept { return stats_; }
  [[nodiscard]] bool rebind_pending() const noexcept { return rebind_pending_; }

 private:
  struct Avatar;
  Avatar* find(std::uint16_t player);
  Avatar* find_net(std::uint32_t net_id);
  Avatar& create(std::uint16_t player);
  void do_rebind();
  void try_start_spawn(Avatar& av);
  void spawn_at(Avatar& av, const Pose& pose, const char* why);
  void announce(Avatar& av, bool force_controller_zero = false);
  void park(Avatar& av);
  void apply_team_move(Avatar& av);
  void drive(Avatar& av, std::int64_t server_now_us);
  void maintain(Avatar& av);
  void remember(Avatar& av);
  void mark_dirty() { dirty_ = true; }
  [[nodiscard]] std::string owner_of(const Avatar& av) const;

  IAvatarEnv& env_;
  AvatarSettings settings_;
  std::vector<std::unique_ptr<Avatar>> avatars_;
  struct RosterEntry {
    std::string name;
    std::uint16_t team = 0;
    bool online = true;
  };
  std::vector<std::pair<std::uint16_t, RosterEntry>> roster_;
  bool have_full_roster_ = false;
  bool rebind_pending_ = false;
  double rebind_since_ = 0;
  int rebind_tries_ = 0;
  bool announce_pending_ = false;
  Lineage lineage_;
  bool dirty_ = false;
  double now_s_ = 0;
  double next_vel_s_ = 0;
  double next_persist_s_ = 0;
  std::uint32_t next_seq_ = 1;
  DirectorStats stats_;
};

}  // namespace x4mp::features::avatars
