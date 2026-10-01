#pragma once
// core/net: RTT and server-clock estimator (docs/protocol.md 7). Pure logic, no clock of its own: the caller
// passes local monotonic microseconds, so tests drive it with synthetic numbers.
//
//   Ping carries our local send time. Pong echoes it and adds the server's recv/reply times:
//     rtt    = (now - echo_send) - (reply - recv)          (server hold time removed)
//     offset = ((recv - echo_send) + (reply - now)) / 2    (NTP form; server - local. On a symmetric path this
//                                                           equals recv - (echo_send + rtt/2))
//   The estimator keeps the last 16 samples and uses the one with the LOWEST rtt (ties: the newest), because
//   queueing delay only ever inflates a sample. rtt is also smoothed (EWMA, alpha = 1/8). The applied offset slews
//   toward the best offset by at most 1 ms per second, or steps when the error is over 50 ms (and on the first
//   sample). server_now = local_now + applied offset.

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>

namespace x4mp::net {

class ClockSync {
 public:
  static constexpr std::size_t kSamples = 16;
  static constexpr std::int64_t kStepThresholdUs = 50'000;
  static constexpr std::int64_t kMaxSlewUsPerSecond = 1'000;

  struct Sample {
    std::int64_t rtt_us = 0;
    std::int64_t offset_us = 0;
  };

  // Returns the sample it computed, or nullopt if it was rejected (negative rtt or hold time: the clock went
  // backwards or the peer lied).
  std::optional<Sample> add_pong(std::int64_t echo_send_us, std::int64_t recv_us, std::int64_t reply_us,
                                 std::int64_t now_us) noexcept {
    const std::int64_t hold = reply_us - recv_us;
    const std::int64_t rtt = (now_us - echo_send_us) - hold;
    if (rtt < 0 || hold < 0) return std::nullopt;
    const std::int64_t offset = ((recv_us - echo_send_us) + (reply_us - now_us)) / 2;
    const Sample s{rtt, offset};
    samples_[next_ % kSamples] = s;
    ++next_;
    smoothed_rtt_ = have_smoothed_ ? smoothed_rtt_ + (rtt - smoothed_rtt_) / 8 : rtt;
    have_smoothed_ = true;
    last_rtt_ = rtt;
    return s;
  }

  [[nodiscard]] bool has_samples() const noexcept { return next_ != 0; }
  [[nodiscard]] std::size_t sample_count() const noexcept { return next_ < kSamples ? next_ : kSamples; }

  // The lowest-rtt sample of the window (newest wins ties).
  [[nodiscard]] std::optional<Sample> best() const noexcept {
    if (next_ == 0) return std::nullopt;
    const std::size_t n = sample_count();
    std::optional<Sample> best;
    for (std::size_t k = 0; k < n; ++k) {  // k = 0 is the newest
      const Sample& s = samples_[(next_ - 1 - k) % kSamples];
      if (!best || s.rtt_us < best->rtt_us) best = s;
    }
    return best;
  }

  [[nodiscard]] std::int64_t smoothed_rtt_us() const noexcept { return smoothed_rtt_; }
  [[nodiscard]] std::int64_t last_rtt_us() const noexcept { return last_rtt_; }

  // Advances the slewed offset to `now_us` and returns it. Call regularly (the net thread does, every loop).
  std::int64_t update(std::int64_t now_us) noexcept {
    const auto b = best();
    if (!b) return 0;
    if (!have_applied_) {
      applied_ = b->offset_us;
      have_applied_ = true;
    } else {
      const std::int64_t err = b->offset_us - applied_;
      const std::int64_t dt = now_us > last_update_ ? now_us - last_update_ : 0;
      const std::int64_t max_step = dt * kMaxSlewUsPerSecond / 1'000'000;
      if (err > kStepThresholdUs || err < -kStepThresholdUs) {
        applied_ = b->offset_us;
      } else if (err > max_step) {
        applied_ += max_step;
      } else if (err < -max_step) {
        applied_ -= max_step;
      } else {
        applied_ = b->offset_us;
      }
    }
    last_update_ = now_us;
    return applied_;
  }
  [[nodiscard]] bool has_offset() const noexcept { return have_applied_; }
  [[nodiscard]] std::int64_t applied_offset_us() const noexcept { return applied_; }

  void reset() noexcept { *this = ClockSync{}; }

 private:
  std::array<Sample, kSamples> samples_{};
  std::size_t next_ = 0;
  std::int64_t smoothed_rtt_ = 0;
  std::int64_t last_rtt_ = 0;
  bool have_smoothed_ = false;
  std::int64_t applied_ = 0;
  std::int64_t last_update_ = 0;
  bool have_applied_ = false;
};

}  // namespace x4mp::net
