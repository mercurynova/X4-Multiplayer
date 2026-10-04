#pragma once
// features/janitor: save hygiene v1 (ADR-023; M2-10 skeleton, M3-13 removal; docs/m3-plan.md 4.8, 4.10).
//
// 1. Load-time janitor (both roles). After a SAVE LOAD (a real on_universe_ready; NOT after /reloadui, where the universe is the same and the
//    ghosts / avatars are adopted from the stash: gates.universe_ready_after_reload) the janitor removes the leftovers of an earlier session that
//    the save carries: ships and stations named "[MP] " and, on a client, team-owned (x4mp_team_*) ones. The rule is in janitor_plan.h; what is
//    never removed: live ghosts, the authority's avatars (bound ids / record idcodes), the takeover's own copy and the host copy until the
//    takeover removed it, the player's guarded ids. Removal only through game::safe_remove.
//    WHEN: the first frames after universe ready would race the takeover (a returning player's avatar sits in the save and the takeover wants it),
//    so a node waits: while a client's takeover is not Done; while a session is pending (connecting .. in game); on the authority once it is
//    connected (its avatars are protected by their records). A node that is not in a session sweeps after kStandaloneGraceS of game-loop time
//    (a launch.json auto-connect starts within frames, so a joining node never gets there). One sweep per universe; a /reloadui in the middle
//    of the wait keeps the wait (stash key janitor.pending).
//    Counts go to the log and to LogForward (diag_hub().forward_log) when connected.
// 2. Checkpoint check (authority, before SaveGame): checkpoint_check() removes the stale leftovers and reports whether the authority universe
//    is clean enough to flag the save ghosts_cleaned=true (docs/m3-plan.md 4.10).
//
// Game calls run on the frame thread (on_frame / the authority flow's step). Bounded: kMaxObjects names read per pass, kMaxRemovePerFrame
// removals per frame, one pass per frame until a pass finds nothing new (an id acted on once is not acted on again).

#include <atomic>
#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_set>

#include "features/janitor/janitor_plan.h"
#include "host/feature.h"

namespace x4mp::features {

struct JanitorResult {
  bool ran = false;
  bool exports_missing = false;
  std::uint32_t scanned = 0;
  std::uint32_t marked = 0;            // names starting with kGhostNamePrefix
  std::uint32_t factions_queried = 0;  // "player" + the team factions the game actually lists
  // M3-13 removal
  std::uint32_t leftovers = 0;         // verdict Remove (new this pass)
  std::uint32_t removed = 0;           // handed to the game through safe_remove
  std::uint32_t refused = 0;           // safe_remove said no (guard, wrong thread, no backend)
  std::uint32_t kept_ghost = 0, kept_avatar = 0, kept_own = 0, kept_guarded = 0;
};

// What the authority's SaveJob learns before it asks the game to save.
struct CheckpointHygiene {
  bool ran = false;                  // the game exports to list objects exist (false: unverifiable, treated as clean with a warning)
  bool clean = true;                 // flag the checkpoint ghosts_cleaned
  std::uint32_t stale_found = 0;     // non-avatar "[MP] " objects found in the authority universe
  std::uint32_t removed = 0;         // ... removed now (counted as gone)
  std::uint32_t remaining = 0;       // ... that could not be removed
  std::uint32_t ghost_registry = 0;  // ghosts in this node's GhostRegistry (the authority has none)
  std::uint32_t avatars_kept = 0;
};

class JanitorFeature final : public host::IFeature {
 public:
  static constexpr std::uint32_t kMaxObjects = 4000;
  static constexpr std::uint32_t kMaxRemovePerFrame = 20;
  static constexpr std::uint32_t kMaxPasses = 40;
  static constexpr double kStandaloneGraceS = 15.0;

  [[nodiscard]] std::string_view name() const noexcept override { return "janitor"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_universe_ready(host::HostContext& ctx) override;
  void on_game_loaded(host::HostContext&) override { pending_ = false; }
  void on_shutdown(host::HostContext& ctx) override;

  // Counting scan (nothing removed); the protection is the live one (hubs + player guard).
  [[nodiscard]] static JanitorResult scan(host::HostContext& ctx);
  // The authority's pre-SaveGame check; removes what it finds (never an avatar, never a guarded id). Frame thread.
  [[nodiscard]] static CheckpointHygiene checkpoint_check(host::HostContext& ctx);

  [[nodiscard]] const JanitorResult& last() const noexcept { return last_; }
  [[nodiscard]] const JanitorResult& total() const noexcept { return total_; }
  [[nodiscard]] bool pending() const noexcept { return pending_; }
  [[nodiscard]] bool swept() const noexcept { return swept_; }

 private:
  std::atomic<bool> pending_{false};
  bool swept_ = false;
  bool wait_logged_ = false;
  bool carried_pending_ = false;  // the previous incarnation shut down mid-wait (stash)
  double grace_s_ = 0.0;
  std::uint32_t passes_ = 0;
  std::unordered_set<std::uint64_t> acted_;
  JanitorResult last_;
  JanitorResult total_;
};

}  // namespace x4mp::features
