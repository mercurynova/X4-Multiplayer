#pragma once
// features/events/events_hub: the one place the rest of the mod reads "is the MD half alive" (M5-03; docs/m5-plan.md W1).
//
// Same shape as ghost_hub / diag_hub: a process-wide accessor holding a plain struct that the events feature publishes and the
// stats feature reads in its 30 s block. Main thread only - the feature writes it in on_frame, the stats feature reads it a
// few microseconds later in the same frame, so no lock is needed and none is taken.

#include "features/events/hook_state.h"

namespace x4mp::features::events {

class EventsHub {
 public:
  void publish(const HookState& state) noexcept { state_ = state; }
  [[nodiscard]] const HookState& state() const noexcept { return state_; }
  void reset() noexcept { state_ = HookState{}; }

 private:
  HookState state_{};
};

[[nodiscard]] inline EventsHub& events_hub() {
  static EventsHub hub;
  return hub;
}

}  // namespace x4mp::features::events
