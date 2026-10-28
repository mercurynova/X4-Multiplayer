#pragma once
// features/resume: the reload-survival state and the "new universe vs /reloadui" rule (M2-07; docs/m2-plan.md 5.3).
//
// Session 2 (docs/spikes/session-2-results.md) showed that both a save load and /reloadui unload and re-initialise the DLL while the
// X4Native stash survives. So the join feature writes this State into its stash key "join.state" at shutdown and the next
// incarnation resumes the Session from it (resume token in session.intent) without the server seeing a leave.
//
// What the next incarnation cannot see by itself is WHETHER the universe changed: after a save load the game builds a new
// universe (the node must redo Loading -> Matching -> NodeReady with a new epoch), after /reloadui it is the same universe
// (stay in-game, nothing to reload). X4Native delivers on_game_loaded / on_universe_ready in both cases (session 2 saw
// on_game_loaded after /reloadui as well), so the events alone cannot decide. The rule uses a universe fingerprint sampled while
// in-game (never at shutdown: no game calls while the game tears the universe down): the player id and the game clock.
//   same universe  <=>  the node was in-game  AND  a fingerprint was sampled  AND  player ids are equal
//                       AND  -kClockBackSlackS <= now.game_time - prev.game_time <= kClockForwardWindowS
// A save load of a different checkpoint moves the clock backwards or far forwards; /reloadui moves it by about the UI rebuild time.
// Anything uncertain is a NEW universe (the safe side: the node re-matches and reports NodeReady again).

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/session/session.h"

namespace x4mp::features::resume {

inline constexpr double kClockBackSlackS = 0.5;        // sampling jitter / pause rounding
inline constexpr double kClockForwardWindowS = 15.0;   // game seconds that may pass between the last sample and the re-init (SETA included)
inline constexpr std::int64_t kUnloadBudgetMs = 200;   // unload_for_reload() (net thread join) must finish within this; measured and logged

struct Fingerprint {
  bool valid = false;
  std::uint64_t player_id = 0;
  double game_time = 0.0;
};

// The persisted join state ("join.state" in the stash).
struct State {
  std::string stage;  // joining | downloading | preparing | loading | ingame | rejoining
  std::string save_name;
  std::vector<std::uint8_t> save_sha;
  bool has_manifest = false;
  session::Id128 checkpoint;
  std::uint64_t epoch = 0;  // universe epoch of the last NodeReady this node sent (0 = none yet)
  Fingerprint fingerprint;  // last in-game sample
};

[[nodiscard]] bool is_resumable_stage(std::string_view stage) noexcept;

[[nodiscard]] std::string to_json(const State& state);
// nullopt when the text is not a JSON object (corrupt / truncated); a missing field takes its default.
[[nodiscard]] std::optional<State> parse(std::string_view text);

enum class Verdict : std::uint8_t { SameUniverse, NewUniverse };
struct Decision {
  Verdict verdict = Verdict::NewUniverse;
  const char* reason = "";
};
[[nodiscard]] Decision decide_universe(const State& previous, const Fingerprint& now) noexcept;

// F1 (ADR-052): exit to the main menu counts as LEAVING the session. The DLL cannot see the exit itself (X4Native has no "game unloaded"
// event; every unload looks the same from inside: a shutdown with the stash kept). What tells the cases apart is what the NEW incarnation
// sees while it is still waiting to decide "same universe or new one" (JoinFeature::undecided_):
//   /reloadui      on_game_loaded is replayed at once (IsPlayerValid), the Lua state is NOT the start menu;
//   save load      the Lua state is not the start menu while loading, on_game_loaded comes ~16 s in;
//   exit to menu   the Lua state IS the start menu (x4mp.ui_ready startmenu=true, IsStartmenu()) and no game ever loads.
// So: the watch arms on ui_ready{startmenu:true} while the decision is open, and fires once it has stayed armed for confirm_ms (default
// 30 s, longer than the 16 s load gap, so a Lua state that reports the start menu during a load never causes a false leave).
// on_game_loaded / on_universe_ready / ui_ready{startmenu:false} disarm it. Pure state, no clock of its own (tests pass the time).
inline constexpr std::int64_t kMenuLeaveConfirmMs = 30000;

// F1c: the "this game runs save X" memory (AuthorityState::loaded_sha, sent as ClientHello.loaded_save_sha256 by the next fresh join) is void while
// the Lua state is the start menu: no universe is loaded, whatever save was running before the player exited to the main menu. A load in progress
// (stage Loading) is excluded: the loaded sha is set when that load reports ready.
[[nodiscard]] constexpr bool start_menu_voids_loaded_save(bool startmenu, bool loading_session_save) noexcept { return startmenu && !loading_session_save; }

// F1d: the authority's world-clock keepalive (WorldUpdate game_time, the reference every player sample is stamped with) runs only while a universe exists:
// the gate is open and the Lua state is not the start menu. At the main menu game_time() still answers (a fresh ~2 s clock) and sent into the session it
// shifted every ghost's sample stamps by tens of seconds.
[[nodiscard]] constexpr bool world_clock_may_run(bool universe_ready, bool startmenu) noexcept { return universe_ready && !startmenu; }

class MenuExitWatch {
 public:
  explicit MenuExitWatch(std::int64_t confirm_ms = kMenuLeaveConfirmMs) noexcept : confirm_ms_(confirm_ms) {}
  void set_confirm_ms(std::int64_t ms) noexcept { confirm_ms_ = ms; }
  // x4mp.ui_ready: `awaiting_decision` = a resumed in-game incarnation that has not yet decided same-vs-new universe.
  void note_ui_ready(bool startmenu, bool awaiting_decision, std::int64_t now_ms) noexcept {
    if (startmenu && awaiting_decision) {
      if (!armed_) {
        armed_ = true;
        since_ms_ = now_ms;
      }
    } else {
      armed_ = false;
    }
  }
  void note_game_loaded() noexcept { armed_ = false; }  // also on_universe_ready, a stage change
  // F1b: any leave (manual Disconnect, a new Join, the F1 leave itself) ends the session this watch was armed for: a later Join is a new
  // session the player started on purpose and must never be cut by a watch that armed before it.
  void disarm() noexcept { armed_ = false; }
  [[nodiscard]] bool armed() const noexcept { return armed_; }
  [[nodiscard]] bool due(std::int64_t now_ms) const noexcept { return armed_ && now_ms - since_ms_ >= confirm_ms_; }

 private:
  std::int64_t confirm_ms_;
  bool armed_ = false;
  std::int64_t since_ms_ = 0;
};

}  // namespace x4mp::features::resume
