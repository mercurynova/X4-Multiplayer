#pragma once
// features/avatars: AvatarTakeover, the CLIENT half of the avatars (M3-12; docs/m3-plan.md 4.3 steps 1-6, 4.8, session-4 sitting 0 S13.6).
// Pure C++ over ITakeoverEnv (game, session, stash and HUD behind one interface), so Catch2 tests it with a fake game and the real adapter
// (avatars_client.cpp) is only plumbing. The authority's half is AvatarDirector (avatar_director.h); the two never run on the same node.
//
// Everybody loads the same checkpoint, so a client wakes up STANDING in the host's ship (a local copy of it). The machine:
//
//   Idle        waits until the universe is ready, the node is in game (the client link is up), the own player id, the own ship and the
//               sector map are known; from then on the PlayerState stream is held back (hold_states): until the avatar is taken over the
//               own ship is the host's ship and must not move the avatar on the authority (m3-plan 4.3 step 6).
//   Requesting  PlayerShip ("I stand in the host's ship here, give me my avatar") every 10 s until the EntitySpawn{origin=PlayerShip} of the
//               own avatar arrives (the authority answers a repeated request with the same ship and net id).
//   Locating    the avatar's local copy. Evidence (docs/m3-plan.md section 8, M3-12): an avatar is in the checkpoint save only when it was
//               spawned BEFORE the checkpoint (a rejoin, a returning player); a new player's avatar is not. So the machine looks among the
//               ships of the x4mp_team_* factions for the idcode (+ name) of the EntitySpawn (the same tiers as the authority's binder) and,
//               when none matches, spawns a copy at the avatar's place (sector index -> local sector, the pose the authority chose with its
//               get_safe_pos clearance). The enumeration being unavailable never leads to a spawn.
//   Teleporting SetComponentOwner(copy, "player") + ActivateObject, then CanTeleportPlayerTo (the real game answers "granted", S13.6), then
//               TeleportPlayerTo(copy, allow_controlling, instant, force). A refusal retries with a growing back-off (2, 2, 2, 3, 3, 4, 4, 5,
//               5, 5 s, then every 20 s); from the 3rd refusal on the HUD hint "sit in the pilot seat" is shown; a sit-down retries at once.
//   Confirming  GetPlayerOccupiedShipID or GetPlayerControlledShipID equals the copy for 10 consecutive frames (the guard, S13.6: 10 frames
//               90 ms after the teleport when standing, 623 ms when docked); 10 s without it counts as a refusal.
//   Removing    the guard has confirmed: PlayerState goes out with the avatar's net id (hold released), then the vacated local copy of the
//               host's ship and every other avatar copy (a ship whose idcode the checkpoint manifest / the EntitySpawns name as an avatar;
//               a team ship named "[MP] ..." only while no manifest could be read; NEVER a ghost: the real env filters them out) are removed through SafeRemove, a few per frame; a refusal retries a few frames. NOTHING is removed before the
//               guard confirmed, and nothing while the player is not in the copy.
//               Ghosts are held back (hold_ghosts) from the start until Done, so a ghost can never stand on a copy that is about to go
//               and the removal can never hit one (M3-10 integration).
//   Done        the persisted record says so: after /reloadui (same universe) the machine is Done again at once.
//
// Never a Game Over: the player's own ship is never removed (the game layer's guard refuses it), every failure only retries or waits.
//
// Reload safety: the record (phase, avatar, host ship and removal ids with their idcodes, net id) is saved on every phase change
// (stash, join::PlatformStash) and read back by the new DLL instance. Local ids are trusted only when the avatar still has the recorded idcode
// (= the same universe); otherwise the record is dropped and the machine starts from scratch.
//
// Threading: frame thread only (step() and on_*() are called from on_frame; the env calls game APIs).

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/config/config.h"
#include "features/avatars/avatar_director.h"
#include "features/avatars/avatar_plan.h"
#include "features/avatars/avatar_wire.h"

namespace x4mp::features::avatars {

inline constexpr std::string_view kTakeoverHint = "Sit in the pilot seat to take over your ship";

// ---- the persisted record -------------------------------------------------------------------------------------------------------
struct TakeoverRecord {
  enum class Phase : std::uint8_t { None = 0, Progress = 1, Seated = 2, Done = 3 };
  Phase phase = Phase::None;
  std::uint16_t player_id = 0;
  std::uint32_t net_id = 0;
  std::string idcode;                 // of the avatar copy
  std::uint64_t avatar_id = 0;        // local ids: valid only in the universe that wrote them
  std::uint64_t host_id = 0;
  std::string host_idcode;
  struct Pending {
    std::uint64_t id = 0;
    std::string idcode;
    bool operator==(const Pending&) const = default;
  };
  std::vector<Pending> remove;        // phase Seated: what is still to be removed

  bool operator==(const TakeoverRecord&) const = default;
};
[[nodiscard]] std::string takeover_record_to_text(const TakeoverRecord& record);
[[nodiscard]] std::optional<TakeoverRecord> takeover_record_from_text(std::string_view text);  // nullopt = unreadable

// ---- pure helpers (tested alone) --------------------------------------------------------------------------------------------------
// The local copy of the avatar named by `grant` among the team-owned candidates: tier 0 idcode + name, tier 1 idcode, tier 2 name; among equal
// tiers the expected owner, then the nearest pose. Candidates not owned by an x4mp_team_* faction and `exclude` never match.
[[nodiscard]] std::optional<std::uint64_t> pick_avatar_copy(const AvatarInfo& grant, std::string_view expected_owner, const std::vector<Candidate>& candidates,
                                                            std::uint64_t exclude = 0);
// Seconds before the next teleport attempt after `tries` refusals (1-based): 2, 2, 2, 3, 3, 4, 4, 5, 5, 5, then 20.
[[nodiscard]] double teleport_backoff_s(int tries) noexcept;

// ---- the environment ----------------------------------------------------------------------------------------------------------------
class ITakeoverEnv {
 public:
  virtual ~ITakeoverEnv() = default;
  // ---- node ----
  virtual bool ready() = 0;                      // universe ready, exports present, sector map ready, in game as a client (link up)
  virtual std::uint16_t own_player_id() = 0;     // 0 = unknown (no Welcome yet)
  virtual std::uint64_t own_ship() = 0;          // the ship the player is in or stands in (selfship status), 0 = unknown
  virtual std::uint64_t seated_ship() = 0;       // GetPlayerOccupiedShipID, else GetPlayerControlledShipID; 0 = none
  virtual std::uint32_t sit_downs() = 0;         // counts the pilot-seat sit-downs
  // ---- game ----
  virtual bool valid(std::uint64_t id) = 0;
  virtual std::string idcode(std::uint64_t id) = 0;
  virtual std::optional<std::vector<Candidate>> team_candidates() = 0;  // nullopt = the enumeration is not available
  virtual std::string faction_of_team(std::uint16_t team) = 0;          // "x4mp_team_<slot>", "" = unknown
  virtual std::uint64_t sector_id_of_index(std::uint16_t index) = 0;    // 0 = unknown
  virtual std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const Pose& pose, const std::string& owner) = 0;  // 0 = failed
  virtual bool set_owner(std::uint64_t id, const std::string& faction) = 0;
  virtual void activate(std::uint64_t id, bool active) = 0;
  virtual std::optional<std::string> can_teleport(std::uint64_t id) = 0;  // the game's text ("granted" = allowed); nullopt = the export is missing
  virtual bool teleport(std::uint64_t id) = 0;                            // TeleportPlayerTo(id, allow_controlling, instant, force); false = refused
  virtual bool remove(std::uint64_t id) = 0;                              // SafeRemove; false = refused (the guard)
  // ---- session ----
  virtual bool send_request(std::uint64_t host_ship) = 0;                 // PlayerShip; false = not sent
  virtual void set_own_net_id(std::uint32_t net_id) = 0;
  virtual void hold_states(bool hold) = 0;                                // PlayerState is held back while true
  virtual void hold_ghosts(bool hold) = 0;                                // the ghost feature spawns nothing while true (until the takeover is Done)
  // ---- UI, persistence, log ----
  virtual void show_hint(bool show, const std::string& text) = 0;
  virtual void save_record(const std::string& text) = 0;
  virtual void log(LogLevel level, const std::string& text) = 0;
  // M3-23: asks for a knowledge probe line (features/diag/knowledge_feature.h) labelled `tag`; a diagnostic only, the default does nothing.
  virtual void probe(const std::string& tag) { (void)tag; }
};

struct TakeoverStats {
  std::uint32_t requests = 0, grants = 0, bound = 0, spawned = 0, spawn_failed = 0, refusals = 0, teleports = 0, guard_resets = 0;
  std::uint32_t removed = 0, remove_refused = 0, hints = 0, restarts = 0;
};

class AvatarTakeover {
 public:
  enum class Stage : std::uint8_t { Idle, Requesting, Locating, Teleporting, Confirming, Removing, Done };

  static constexpr double kRequestEveryS = 10.0;
  static constexpr double kLocateRetryS = 1.0;
  static constexpr double kLocateGiveUpLogS = 10.0;
  static constexpr double kConfirmTimeoutS = 10.0;
  static constexpr double kHintRepeatS = 30.0;
  static constexpr double kRemoveWaitS = 30.0;
  static constexpr int kHintAfterRefusals = 3;
  static constexpr int kMaxTeleportTries = 10;  // after these the retry cadence is the slow one (20 s); nothing is ever given up
  static constexpr int kGuardFrames = 10;
  static constexpr int kRemoveFrames = 30;      // retries per object that SafeRemove refuses
  static constexpr int kRemovePerFrame = 4;

  explicit AvatarTakeover(ITakeoverEnv& env) : env_(env) {}

  void set_settings(const AvatarSettings& settings) { settings_ = settings; }
  // M3-23 diagnostic switches (x4mp.json "diag"): takeover_off = no takeover (the machine goes straight to Done, the player stays in the save's
  // ship, PlayerState stays held, ghosts are released); takeover_keep_original = the host ship copy is never removed.
  void set_diag(const config::DiagConfig& diag) { diag_ = diag; }
  // The record of an earlier DLL instance (stash). Validated at the first ready step.
  void load_record(const TakeoverRecord& record) { loaded_ = record; }
  // Avatars named by an EntitySpawn (the own one starts the takeover) or by the checkpoint manifest (only their idcodes are used).
  void on_spawn_avatars(const std::vector<AvatarInfo>& avatars);
  void set_manifest_avatars(const std::vector<AvatarInfo>& avatars) {
    manifest_ = avatars;
    manifest_loaded_ = true;
  }
  // The universe changed under this instance (a save load without a DLL re-init): start over.
  void restart();
  // The session ended: forget the grant, release the hold, hide the hint. The persisted record stays.
  void session_ended();

  void step(double now_s);  // once per frame

  [[nodiscard]] Stage stage() const noexcept { return stage_; }
  [[nodiscard]] bool done() const noexcept { return stage_ == Stage::Done; }
  [[nodiscard]] bool hint_shown() const noexcept { return hint_shown_; }
  [[nodiscard]] std::uint64_t avatar_id() const noexcept { return avatar_id_; }
  [[nodiscard]] std::uint64_t host_id() const noexcept { return host_id_; }
  [[nodiscard]] std::uint32_t net_id() const noexcept { return grant_ ? grant_->net_id : 0; }
  [[nodiscard]] const TakeoverStats& stats() const noexcept { return stats_; }
  [[nodiscard]] static const char* stage_name(Stage s) noexcept;

 private:
  struct Todo {
    std::uint64_t id = 0;
    std::string idcode;
    int tries = 0;
  };
  void start(double now_s, std::uint16_t player, std::uint64_t ship);
  bool resume_from_record(double now_s, std::uint16_t player, std::uint64_t ship);
  void step_requesting(double now_s, std::uint16_t player);
  void step_locating(double now_s);
  void step_teleporting(double now_s);
  void step_confirming(double now_s);
  void step_removing(double now_s);
  void refused(double now_s, const std::string& reason);
  void begin_removal(double now_s);
  void finish(double now_s);
  void set_hint(double now_s, bool show);
  void persist(TakeoverRecord::Phase phase);
  [[nodiscard]] std::string expected_owner() const;
  [[nodiscard]] bool known_other_idcode(const std::string& idcode) const;
  void log(LogLevel level, const std::string& text) { env_.log(level, "takeover: " + text); }

  ITakeoverEnv& env_;
  AvatarSettings settings_;
  config::DiagConfig diag_;
  std::optional<TakeoverRecord> loaded_;
  std::vector<AvatarInfo> manifest_;
  bool manifest_loaded_ = false;  // the checkpoint manifest was read (an empty list counts): then only listed idcodes are avatar copies
  std::map<std::uint32_t, AvatarInfo> seen_;  // by net id: every avatar an EntitySpawn named
  std::optional<AvatarInfo> grant_;           // the own avatar
  Stage stage_ = Stage::Idle;
  bool held_ = false;
  std::uint16_t player_ = 0;
  std::uint64_t host_id_ = 0, avatar_id_ = 0;
  std::string host_idcode_, avatar_idcode_;
  double next_action_ = 0, stage_since_ = 0, started_ = 0, next_hint_ = 0, next_log_ = 0;
  int tries_ = 0, frames_ok_ = 0;
  std::uint32_t last_sit_downs_ = 0;
  bool hint_shown_ = false;
  bool owner_set_ = false;
  std::vector<Todo> todo_;
  TakeoverStats stats_;
};

}  // namespace x4mp::features::avatars
