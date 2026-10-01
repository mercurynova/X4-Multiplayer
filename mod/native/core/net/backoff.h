#pragma once
// core/net: reconnect backoff schedule (docs/protocol.md 7 / roadmap M1-N2): 1, 2, 4, 8, then 10 s (capped).
// Pure logic, no clock and no sockets, so it is trivially testable. Resettable.

#include <algorithm>
#include <cstdint>

namespace x4mp::net {

class Backoff {
 public:
  explicit Backoff(std::int64_t first_ms = 1000, std::int64_t cap_ms = 10000) noexcept
      : first_ms_(first_ms), cap_ms_(cap_ms) {}

  // Delay before the next attempt, then advances the schedule. 1000, 2000, 4000, 8000, 10000, 10000, ...
  [[nodiscard]] std::int64_t next_delay_ms() noexcept {
    const std::int64_t delay = peek_delay_ms();
    if (step_ < 30) ++step_;
    return delay;
  }
  // The delay the next call would return, without advancing.
  [[nodiscard]] std::int64_t peek_delay_ms() const noexcept {
    return std::min(cap_ms_, first_ms_ << std::min<std::uint32_t>(step_, 20));
  }
  void reset() noexcept { step_ = 0; }
  [[nodiscard]] std::uint32_t attempts() const noexcept { return step_; }

 private:
  std::int64_t first_ms_;
  std::int64_t cap_ms_;
  std::uint32_t step_ = 0;
};

}  // namespace x4mp::net
