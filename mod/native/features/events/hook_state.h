#pragma once
// features/events/hook_state: the `md_hook` state AC item 1 of MOD 9 M5 asks for (M5-03; docs/m5-plan.md W1).
//
// The failure this exists for is silent: an MD half that never loads, or loads with a broken cue, leaves the mod doing
// nothing at all while every log line still looks healthy. So the MD script announces itself once (`H;<n>`, the number of
// hooks it armed) and the node keeps the age of the newest event. The block below rides in the 30 s budget line, next to the
// numbers it explains:
//
//   hooks=alive md=9 fired=41 age=1.2s fb=2 dup=0 queued=0 claims=3 unmapped=1
//   hooks=dead  md=0 fired=0  age=none fb=0 dup=0 queued=0 claims=0 unmapped=0
//
//   md        hooks the MD half armed (H;<n>); 0 means it never said hello
//   fired     lines the hooks delivered since the last universe load
//   age       how long ago the newest line arrived; `none` when nothing has arrived yet
//   fb        events the fallback detectors produced (M5-04) - a high number with fired=0 says the hooks are dead
//   dup       events dropped as repeats inside the one-second window (M5-02)
//   queued    events waiting for the next frame's send budget
//   claims    KillClaim intents this node sent since the universe load (M5-06)
//   unmapped  kills whose victim is not a ghost this node holds - nothing to claim

#include <cstdint>
#include <format>
#include <string>

namespace x4mp::features::events {

struct HookState {
  std::uint32_t hooks_armed = 0;
  bool alive = false;
  std::uint32_t fired = 0;
  std::uint32_t fallbacks = 0;
  std::uint32_t deduped = 0;
  std::uint32_t queued = 0;
  std::uint32_t claims = 0;    // M5-06: KillClaim intents this node sent since the universe load
  std::uint32_t unmapped = 0;  // kills whose victim is not a ghost this node holds (not ours to claim)
  std::int64_t last_age_ms = -1;  // -1: nothing has arrived since the universe load
};

[[nodiscard]] inline std::string format_hooks(const HookState& s) {
  std::string age = "none";
  if (s.last_age_ms >= 0) age = std::format("{:.1f}s", static_cast<double>(s.last_age_ms) / 1000.0);
  return std::format("hooks={} md={} fired={} age={} fb={} dup={} queued={} claims={} unmapped={}", s.alive ? "alive" : "dead", s.hooks_armed,
                     s.fired, age, s.fallbacks, s.deduped, s.queued, s.claims, s.unmapped);
}

}  // namespace x4mp::features::events
