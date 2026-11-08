#pragma once
// features/ghosts: the ghost driver (M3-10; docs/m3-plan.md 4.2, 4.4, 4.6, 4.8, 4.12; sitting-0 verdicts S13.1-S13.10).
//
// One GhostDriver turns "player ship X exists, here are its samples" into a ghost object in the local game: spawn + dress + inert +
// radar + minimum hull, a per-frame pose from core/ghost's interpolator (render(server_now)), a velocity hint at 5 Hz (mode c: without
// it the target info shows 0 m/s), hide / show, sector changes, parked / offline labels, the registry in the stash and its adoption
// after a DLL re-init. It knows NOTHING about FlatBuffers, the SDK or Lua: the game side is the IGhostWorld interface (the feature
// implements it over game/ghosts_api and MD, the Catch2 tests over a fake universe), the wire side is plain structs. Every IGhostWorld
// call happens inside frame() / adopt() (the frame thread); message handlers only change tables.
//
// Rules this file keeps (sitting 0 + CLAUDE.md):
//   * Removal only through IGhostWorld::remove (= game::safe_remove). A ghost is removed only when we spawned it (registry entry) and
//     never an id the world refuses.
//   * Component ids change on every save load: ghosts persisted in the stash are re-found by id + idcode + name prefix, then by
//     idcode among the team factions' ships, never by the stored id alone.
//   * Positions handed to the world are SECTOR-LOCAL metres of the sector the pose says (cross-sector = one place() with the new
//     sector's id; tests land it within 1 m).
//   * A ghost is solid: a pushed ghost is put back on its path every frame (moving) or at least every parked_set_interval (parked).
//   * Never spawn onto the local player's ship: a spawn within spawn_clearance_m of it waits (the player is not removed for it).
//   * Budget: <= max_spawns_per_frame spawns, one place() per moving ghost and frame, no allocation in the steady frame path.

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "core/ghost/interpolator.h"
#include "core/ghost/registry.h"
#include "core/ghost/replication.h"
#include "core/ghost/sync_stats.h"

namespace x4mp::features::ghosts {

inline constexpr std::string_view kNamePrefix = "[MP] ";
inline constexpr std::string_view kOfflineSuffix = " (offline)";
inline constexpr std::uint64_t kRegistryEpoch = 1;  // fixed: identity (id + idcode + name) decides adoption, not a universe epoch

using Vec3 = ghost::Vec3;
using Euler = ghost::Euler;

// A pose in the game's sector frame (metres, radians).
struct Pose {
  Vec3 pos{};
  Euler rot{};
};

struct VelocityHint {
  std::uint64_t id = 0;
  double vx = 0, vy = 0, vz = 0;  // m/s, sector frame
};

// NotReady: the team factions are not set up yet (M3-08 reports Ok per universe): wait quietly, no error.
enum class FactionPresence : std::uint8_t { Unknown, Present, Missing, NotReady };
enum class RemoveOutcome : std::uint8_t { Removed, Blocked, Failed };

// The game, as the driver needs it. All calls on the frame thread.
class IGhostWorld {
 public:
  virtual ~IGhostWorld() = default;
  // Local sector id for a wire sector index; 0 = not mapped (yet).
  virtual std::uint64_t sector_id(std::uint16_t index) = 0;
  // 0 = failed. The ghost must already be inert-able; the driver calls make_inert right after.
  virtual std::uint64_t spawn(std::string_view macro, std::uint64_t sector, const Pose& pose, std::string_view owner) = 0;
  virtual void make_inert(std::uint64_t id) = 0;
  // M4-29: the `activate` half of the attention A/B - ActivateObject(true) on a ghost the driver has just made inert. Default: nothing,
  // so every fake and every old world keeps the `native` policy without a change.
  virtual void activate(std::uint64_t /*id*/) {}
  virtual void place(std::uint64_t id, std::uint64_t sector, const Pose& pose) = 0;
  // Name, owner-independent attributes the game needs a script for: label, minimum hull percent, known + radar.
  virtual void dress(std::uint64_t id, std::string_view label, int min_hull_percent) = 0;
  virtual void hint_velocities(std::span<const VelocityHint> hints) = 0;
  // M3-34: what the GAME says about the object right now (sector, sector-local pose, whether it is in a highway context). nullopt = the world cannot read
  // it (the default: fakes, an old game). The engine guard compares it with the pose we placed: a difference means the engine moved our object.
  struct ObjectState {
    std::uint64_t sector = 0;
    // 2026-11-08: the zone the object lives in and the zone the player stands in (both 0 = unknown / the world does not model zones).
    // The engine keeps a low-attention object's position in its own zone frame; the read-back of a ghost outside the player's zone is
    // not the pose that was placed (live: whole groups of ghosts answered with one identical 51-362 km translation), so the guard
    // must not fight it there.
    std::uint64_t zone = 0;
    std::uint64_t player_zone = 0;
    Pose pose;
    bool in_highway = false;
  };
  virtual std::optional<ObjectState> read_state(std::uint64_t /*id*/) { return std::nullopt; }
  virtual void set_owner(std::uint64_t id, std::string_view owner) = 0;
  virtual bool valid(std::uint64_t id) = 0;
  virtual bool wrecked(std::uint64_t id) = 0;
  virtual RemoveOutcome remove(std::uint64_t id) = 0;
  virtual std::string id_code(std::uint64_t id) = 0;
  virtual std::string name(std::uint64_t id) = 0;
  // An object owned by one of the team factions with this idcode and our name prefix; nullopt when none.
  virtual std::optional<std::uint64_t> find_ghost_by_idcode(std::string_view idcode) = 0;
  virtual FactionPresence faction(std::string_view faction) = 0;
  // True when the local player's ship is in `sector` within `radius` metres of `pos` (spawn clearance).
  virtual bool local_ship_within(std::uint64_t sector, const Vec3& pos, double radius) = 0;
  // M4-12: how far a not-yet-spawned ghost is from the player, in metres, for the spawn budget's order. A world without a player pose
  // (the test fakes) returns 0 and keeps the record order.
  virtual double player_distance(std::uint64_t /*sector*/, const Vec3& /*pos*/) { return 0.0; }
};

// What the wire says about one player ship (EntitySpawn / EntityRecord, or restored from the stash).
struct SpawnInfo {
  std::uint32_t net_id = 0;
  std::uint16_t player_id = 0;    // the pilot, else the owning player (parked avatar)
  std::uint16_t controller = 0;   // 0 = nobody flies it: parked / offline
  std::uint16_t team = 0;
  std::string name;               // as sent; the label is derived (prefix, offline suffix)
  std::string macro;
  std::string owner;              // faction id passed to the spawn, e.g. "x4mp_team_1"
  // M4-13: an NPC entity of the authority's world (EntityOrigin::AuthorityRuntime / Manifest). It has no player, no controller and no
  // team, is never labelled or dressed, and keeps the authority's real owner faction (mod-design 4.4).
  bool npc = false;
};

// The pose an EntitySpawn carried (used only when no Replication sample arrives within kSpawnPoseFallbackUs).
struct InitialState {
  std::uint16_t sector = 0;
  std::uint16_t flags = 0;
  Vec3 pos{};
  Vec3 vel{};
  Euler rot{};
};

enum class DespawnKind : std::uint8_t {
  Remove,     // Destroyed / Removed / PlayerLeft / unknown: the ghost goes away for good
  HideOnly,   // DockedInside: the ship still exists, the ghost is hidden until samples say otherwise
  OutOfInterest  // M4-14: the sector left the client's interest: the object is kept for the leave grace, then removed
};

struct GhostConfig {
  // M4-12 (mod-design 4.3): ghosts are created at most this many per frame, nearest to the player first. The M3 value was 2 (player
  // ships only); a sector entry can carry a few hundred, so the design's 40 is the default now.
  int max_spawns_per_frame = 40;
  // M4-12: how many records may wait for their turn. Beyond this the farthest are dropped from the pending set (their samples stay, so
  // they spawn when the budget reaches them again).
  int max_pending_spawns = 512;
  // M4-14: a ghost the server despawns (OutOfInterest) keeps its object this long, so leaving and re-entering a sector within the window
  // reuses the same object instead of a despawn/respawn pair.
  std::int64_t leave_grace_us = 10'000'000;
  std::int64_t hint_interval_us = 200'000;           // velocity hint 5 Hz (S13.2 mode c)
  std::int64_t zero_hint_interval_us = 2'000'000;    // a stationary ghost gets its zero velocity re-sent this often
  std::int64_t parked_set_interval_us = 250'000;     // unchanged pose: re-assert this often (puts a pushed ghost back)
  std::int64_t inert_interval_us = 5'000'000;        // ActivateObject(false) again (m3-plan 4.2)
  bool inert_once = false;                           // M3-28 diag.avatars_inert_once: ActivateObject(false) only right after the spawn, never re-asserted
  // M4-29 diag.ghost_attention_activate: the attention-policy A/B. false = `native` (the ghost stays as make_inert left it), true =
  // `activate` (ActivateObject(true) right after make_inert, so the engine's attention runs on the streamed ship).
  bool activate_streamed = false;
  std::int64_t validity_interval_us = 500'000;       // IsValidComponent / wrecked check
  std::int64_t dress_check_delay_us = 2'000'000;     // after a spawn / rename: is the name there? else dress again
  std::int64_t dress_check_interval_us = 5'000'000;
  int dress_max_retries = 5;
  std::int64_t spawn_pose_fallback_us = 1'000'000;   // no Replication sample this long after EntitySpawn: use the spawn state
  std::int64_t spawn_retry_us = 2'000'000;           // after a failed spawn / missing faction
  int respawn_burst = 3;                             // this many respawns within respawn_window_us -> back off
  std::int64_t respawn_window_us = 30'000'000;
  std::int64_t respawn_backoff_us = 10'000'000;
  double spawn_clearance_m = 40.0;
  int min_hull_percent = 100;                        // Q11: indestructible locally until M5
  // Plan 4.6 says "stale > 5 s -> hide", but the server sends NOTHING for a ship that does not change (a parked avatar was silent for
  // 30 s in the ghost e2e), so silence is not staleness: a ghost without data stays where it is. Removal comes from EntityDespawn.
  std::int64_t stale_us = 3'600'000'000;
  // A ghost dead-reckons through the streamer's keepalive: a hull whose velocity is right is sent nothing until the 1 s (near) or 5 s
  // (far) keyframe, so the interpolator's 500 ms default froze it and the error grew with the freeze (stand 2026-11-08: 19465 m at
  // 8170 m/s with 17 of 42 frames held). The cap covers the far keepalive; past it the ghost holds, because silence is not staleness.
  std::int64_t extrap_cap_us = 6'000'000;
  double motion_epsilon_m = 0.001;
  double motion_epsilon_rad = 0.0001;
  // M3-34 (Finding 23): the velocity hint is for the target info's speed, but the ENGINE flies the inert ship with it between our per-frame placements and
  // can capture it into a highway. Above this speed the hint is zero (the placements alone carry the ghost; the target info shows 0 m/s at travel speed).
  double hint_max_mps = 1000.0;
  bool hints_off = false;                            // diag.hints_off: no velocity hint at all (the ghost is only ever placed)
  bool hints_all_speeds = false;                     // diag.hints_all_speeds: the old behaviour, a hint at any speed
  // M3-34 engine guard: every guard_interval_us the game's own idea of the object is read; when it differs from what we placed by more than
  // guard_min_m + speed x guard_speed_s (or the sector differs, or the engine says "highway") the object is put back at once, its velocity zeroed,
  // and the fact is logged (rate limited). The evidence for "the engine moves our objects" and the repair in one.
  bool engine_guard = true;                          // diag.engine_guard_off turns it off
  std::int64_t guard_interval_us = 200'000;
  std::int64_t guard_hot_us = 2'000'000;             // after a hit the check runs every frame for this long
  std::int64_t guard_hot_interval_us = 50'000;       // ... but no more often than this (20 Hz), not once per frame
  std::int64_t guard_log_interval_us = 20'000'000;
  double guard_min_m = 30.0;
  // 2026-11-08: the read-back lags our placement by about 1.1-1.3 s of travel, not by a fixed distance. The stand's remaining hits were all
  // 41.7-52.0 m at 40-52 m/s (tolerance 40-43 m with the old 0.3), i.e. d/speed was the same second every time: the engine animates a
  // SetObjectSectorPos instead of teleporting it (and a low-attention object's logical position updates slower than we place). At 0.3 s the
  // gate sat inside that lag and repaired ~5 good ghosts a second for nothing. 1.5 s covers the lag and still catches a real move (286 km
  // captures, the 126-187 km reads) by three orders of magnitude.
  double guard_speed_s = 1.5;
  // A ghost the engine keeps putting back where it wants it (a docked hull the game pins, a placement that never sticks) would be read
  // back and re-placed for the whole session: the two-client stand logged 17.6k repairs and 284k reads in 15 minutes, which is real
  // frame time. After this many hits in a row the guard drops that ghost for guard_giveup_us.
  int guard_giveup_hits = 8;
  std::int64_t guard_giveup_us = 30'000'000;
};

struct DriverCounters {
  std::uint64_t spawned = 0, respawned = 0, removed = 0, hidden = 0, shown = 0, adopted = 0, adopt_dropped = 0;
  std::uint64_t spawn_failed = 0, faction_missing = 0, invalid_dropped = 0, blocked_removals = 0, remove_failed = 0;
  std::uint64_t places = 0, sector_changes = 0, hints_sent = 0, redressed = 0, unmapped_frames = 0, deferred_clearance = 0;
  std::uint64_t guard_reads = 0, engine_moved = 0, engine_sector = 0, engine_highway = 0;  // M3-34 engine guard
  // 2026-11-08: a read that would have counted as "the engine moved it" but was taken outside the player's zone, where the game's
  // answer is a frame of its own and not the pose we placed. Not a repair: the guard skipped it on purpose.
  std::uint64_t guard_zone_skip = 0;
  // M4-23: a spawn that landed inside the player's 5 km bubble. Zero is the acceptance criterion on a highway transit; a non-zero value
  // names the sector and the net id in the log line that counted it.
  std::uint64_t spawn_near = 0;
};

struct FrameStats {
  int visible = 0;       // ghosts that exist in the game
  int tracked = 0;       // ghost records
  int spawns = 0;
  int places = 0;
};

class GhostDriver {
 public:
  using LogFn = std::function<void(int level /*0 debug 1 info 2 warn 3 error*/, const std::string& text)>;

  explicit GhostDriver(GhostConfig cfg = {}, LogFn log = {});

  void bind_world(IGhostWorld* world) noexcept { world_ = world; }
  void set_self(std::uint16_t player_id) noexcept { self_ = player_id; }
  // M4-17: the net id of the ship this client already has as its own. A passenger inside another player's ship reports that hull as
  // its own ship (own_ship_tracker), so without this the other side would create a second copy of a ship it already owns.
  void set_own_ship_net_id(std::uint32_t net_id) noexcept { own_ship_net_id_ = net_id; }
  [[nodiscard]] std::uint16_t self() const noexcept { return self_; }
  // M4-16: the link is gone. While frozen frame() does nothing at all: the ghosts stay where they were, unmoving, and no stale-hide,
  // placement, hint or validity check runs. Unfreezing resumes from the interpolators with no respawn.
  void set_frozen(bool frozen) noexcept { frozen_ = frozen; }
  [[nodiscard]] bool frozen() const noexcept { return frozen_; }

  // ---- messages (no game calls) ----
  // Own ship (controller == self, or parked with owner == self) is ignored; an NPC entity (info.npc) is admitted when it has a macro and an
  // owner. Returns true when a ghost record exists afterwards. A second spawn for a known net_id refreshes it (a resume re-sends every spawn)
  // and cancels a pending leave-grace erase.
  bool on_spawn(const SpawnInfo& info, std::int64_t now_us, const std::optional<InitialState>& state = std::nullopt);
  // EntityChange: any subset; an empty optional leaves the field alone.
  void on_change(std::uint32_t net_id, std::optional<std::string> name, std::optional<std::uint16_t> controller, std::optional<std::string> owner,
                 std::optional<std::uint16_t> team);
  void on_despawn(std::uint32_t net_id, DespawnKind kind);
  // The server stopped replicating everything (session end / kick): every ghost is removed on the next frame.
  void remove_all();
  // Replication: ingest into the interpolators (arrival_us = server-time estimate now).
  ghost::StreamSet& streams() noexcept { return streams_; }

  // ---- game side (frame thread) ----
  // Spawns, places, hides, hints, validates. now_us = server clock estimate.
  FrameStats frame(std::int64_t now_us);
  // Re-find the ghosts restore() brought back (call once the universe is ready, before the first frame()): the registry blob in
  // `stash` is adopted with an identity check (id valid + idcode equal + name prefix), what fails is searched by idcode among the team
  // factions' ships, what is still missing respawns from its samples. Returns how many ghosts were adopted.
  std::size_t adopt(session::IStash& stash, std::int64_t now_us);
  // Forget local ids (a new universe, ids are meaningless): ghosts stay as records and respawn from their samples.
  void forget_local_ids();

  // ---- persistence ----
  // Writes "ghost.meta" (one line per ghost record) and the registry blob "ghost.registries" into `stash`. No game calls.
  void save(session::IStash& stash) const;
  [[nodiscard]] std::string to_meta() const;
  // Parses a "ghost.meta" text into ghost records (no local ids are trusted yet: adopt() decides). False when the text is unusable.
  bool restore(std::string_view meta, std::int64_t now_us);
  [[nodiscard]] bool dirty() const noexcept { return dirty_; }
  void mark_clean() noexcept { dirty_ = false; }
  ghost::Registries& registries() noexcept { return regs_; }
  [[nodiscard]] bool has_pending_adoption() const noexcept { return adopt_pending_; }
  void set_pending_adoption(bool v) noexcept { adopt_pending_ = v; }

  // ---- reporting ----
  struct GhostView {
    std::uint32_t net_id = 0;
    std::uint16_t player_id = 0, controller = 0;
    std::string label;
    std::uint64_t local_id = 0;
    bool shown = false;
    ghost::PoseState state = ghost::PoseState::Empty;
    std::uint16_t sector = 0;
    Vec3 pos{};
    std::int64_t represented_t_us = 0;  // server time the displayed pose stands for
    std::uint64_t sector_id = 0;        // local sector id of the last placement
  };
  [[nodiscard]] std::vector<GhostView> views() const;
  [[nodiscard]] std::size_t size() const noexcept { return ghosts_.size(); }
  [[nodiscard]] bool has(std::uint32_t net_id) const noexcept { return find(net_id) != nullptr; }
  [[nodiscard]] const DriverCounters& counters() const noexcept { return counters_; }
  // One "[sync] player=.. net=.. ..." line per tracked ghost with samples; resets the stats windows.
  [[nodiscard]] std::vector<std::string> take_sync_lines();
  [[nodiscard]] const GhostConfig& config() const noexcept { return cfg_; }

  // The label a ghost with these attributes shows: "[MP] <name>" or "[MP] <name> (offline)".
  [[nodiscard]] static std::string make_label(std::string_view name, std::uint16_t controller, std::uint16_t player_id);

 private:
  struct Ghost {
    SpawnInfo info;
    std::string label;
    std::string idcode;           // GetObjectIDCode at spawn; identity after a reload
    std::uint64_t local_id = 0;   // 0 = no object in the game (not spawned yet, or hidden)
    std::uint64_t restored_id = 0;  // the id the stash held before adopt() ran
    std::uint64_t sector_id = 0;  // sector the object was last placed in
    bool hidden_by_server = false;  // DespawnKind::HideOnly
    std::int64_t hidden_at_newest_us = 0;
    bool label_dirty = false;
    bool left_by_hide = false;   // the object is gone because we hid it (not because the game lost it): a re-show is not a respawn
    // timing (server clock, us)
    std::int64_t first_seen_us = 0, last_set_us = 0, last_hint_us = 0, last_inert_us = 0, last_valid_us = 0, spawn_not_before_us = 0;
    std::int64_t dress_check_us = 0, defer_since_us = 0, prev_t_us = 0;
    int dress_retries = 0;
    bool dressed_ok = false;
    bool hint_was_zero = false;
    std::int64_t last_guard_us = 0, guard_hot_until_us = 0, last_guard_log_us = 0;  // M3-34 engine guard
    std::uint16_t guard_hits = 0;          // engine-moved reads in a row; guard_giveup_hits stops the loop
    std::int64_t guard_skip_until_us = 0;  // this ghost is not read back until then
    std::int64_t erase_at_us = -1;  // M4-14: >= 0 = the server dropped it, the object goes when the leave grace runs out
    bool force_place = false;
    bool have_prev = false;
    Pose last_pose{}, prev_pose{};
    std::uint16_t last_sector_index = 0;
    std::optional<InitialState> initial;
    bool initial_used = false;
    std::int64_t respawn_times[8]{};
    int respawn_n = 0;
    bool owner_dirty = false;
    ghost::PoseState last_state = ghost::PoseState::Empty;
    std::uint16_t last_render_sector = 0;
    Vec3 last_render_pos{};
    std::int64_t last_represented_us = 0;
    bool ever_shown = false;
  };

  [[nodiscard]] Ghost* find(std::uint32_t net_id) noexcept;
  [[nodiscard]] const Ghost* find(std::uint32_t net_id) const noexcept;
  void log(int level, const std::string& text) const;
  void register_ghost(Ghost& g);
  void unregister_ghost(Ghost& g);
  void hide(Ghost& g, const char* why);           // removes the object, keeps the record
  void drop_object(Ghost& g, const char* why);    // the object is gone (invalid): forget the id
  bool try_spawn(Ghost& g, std::int64_t now_us, std::uint64_t sector_id, const Pose& pose);
  bool note_respawn(Ghost& g, std::int64_t now_us);
  void guard_check(Ghost& g, std::int64_t now_us, double speed_mps);  // M3-34 engine guard
  void process_removals();
  void erase_record(std::uint32_t net_id);

  GhostConfig cfg_;
  LogFn log_;
  IGhostWorld* world_ = nullptr;
  std::uint16_t self_ = 0;
  std::uint32_t own_ship_net_id_ = 0;  // the hull this client already owns: never ghost it (a passenger reports it as its own ship)
  ghost::StreamSet streams_;
  ghost::Registries regs_;
  std::vector<Ghost> ghosts_;
  std::vector<std::uint64_t> remove_queue_;  // local ids of records erased by messages: removed on the next frame
  std::vector<VelocityHint> hint_batch_;
  DriverCounters counters_;
  bool dirty_ = false;
  bool adopt_pending_ = false;
  bool remove_all_ = false;
  bool frozen_ = false;  // M4-16: the link is gone (see set_frozen)
  // M4-12: the not-yet-spawned ghosts of this frame, nearest first, cut to max_pending_spawns; the first max_spawns_per_frame of them spawn.
  struct PendingSpawn {
    double distance = 0.0;
    std::uint32_t net_id = 0;
    std::uint64_t sector = 0;
    Pose pose;
  };
  std::vector<PendingSpawn> pending_spawns_;
  std::vector<std::uint32_t> pending_erase_;  // M4-14: net ids whose leave grace ran out this frame
  std::int64_t last_now_us_ = 0;  // server clock of the last frame (for the age of the newest sample in the [sync] line)
};

}  // namespace x4mp::features::ghosts
