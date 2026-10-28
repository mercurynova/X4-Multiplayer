#pragma once
// features/events/events_hub: the one place the rest of the mod reads "is the MD half alive", and the link the event capture
// sends through (M5-03, M5-06; docs/m5-plan.md W1/W2).
//
// Same shape as ghost_hub / diag_hub: a process-wide accessor holding a plain struct that the events feature publishes and the
// stats feature reads in its 30 s block. Main thread only - the feature writes it in on_frame, the stats feature reads it a
// few microseconds later in the same frame, so no lock is needed and none is taken.
//
// The Link is installed by JoinFeature exactly like the other per-feature links (selfship, onfoot, avatars, stats): the lambdas
// capture the Session and are cleared when the session ends, so an event that fires while disconnected is counted and dropped
// rather than queued forever.

#include <cstdint>
#include <functional>
#include <span>
#include <utility>

#include "features/events/hook_state.h"

namespace x4mp::features::events {

// The sender the kill reporter (and later waves) use. `send_control` is the Session's Control lane: reliable, ordered, and
// replayed after a RESUME, which is what an idempotent Intent wants.
struct Link {
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_control;
  std::function<std::uint16_t()> self_player_id;
};

class EventsHub {
 public:
  void publish(const HookState& state) noexcept { state_ = state; }
  [[nodiscard]] const HookState& state() const noexcept { return state_; }
  void reset() noexcept { state_ = HookState{}; }

  void set_link(Link link) { link_ = std::move(link); }
  void clear_link() { link_ = Link{}; }
  [[nodiscard]] bool linked() const noexcept { return static_cast<bool>(link_.send_control); }
  [[nodiscard]] bool send_control(std::uint16_t type, std::span<const std::uint8_t> payload) {
    return link_.send_control ? link_.send_control(type, payload) : false;
  }
  [[nodiscard]] std::uint16_t self_player_id() const { return link_.self_player_id ? link_.self_player_id() : 0; }

 private:
  HookState state_{};
  Link link_{};
};

[[nodiscard]] inline EventsHub& events_hub() {
  static EventsHub hub;
  return hub;
}

}  // namespace x4mp::features::events
