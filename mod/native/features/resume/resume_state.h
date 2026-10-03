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
  std::string stage;  // joining | downloading | preparing | loading | ingame
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

}  // namespace x4mp::features::resume
