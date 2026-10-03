#pragma once
// core/ghost: per-entity snapshot interpolator (M3-02; docs/m3-plan.md 4.6, 4.7). Used by client ghosts and by the authority's
// avatars. Pure C++, no game calls, NO allocation after construction (fixed arrays).
//
//   push(sample, arrival)   a sample from the wire (server time). Sorted ring of 8; out-of-order is fine, older than the ring is dropped.
//   apply_state(...)        flags/status change without a new pose (Replication entry without POS): updates the governing sample.
//   render(now)             advances the adaptive delay, evaluates the pose at now - delay and returns it, updates the statistics.
//
// Rules (all server time, never game time):
//   * position: cubic Hermite between neighbours with the sender's velocities (linear when both velocities are zero);
//     rotation: slerp of the quaternions of the Euler angles (math.h).
//   * past the newest sample: linear extrapolation (rotation: continued angular rate) for at most extrap_cap (500 ms), then HOLD at
//     that pose (state Held); stale after stale_us (5 s) without data -> hidden.
//   * a sample with Teleport, another sector or a Hidden neighbour is a DISCONTINUITY: the pose holds the earlier sample until the
//     render time reaches the new one, then SNAPS (RenderPose::snapped). No interpolation across it.
//   * corrections: when the raw pose jumps relative to the previous frame's prediction by more than 0.5 m (a late sample after an
//     extrapolation, a delay change), the displayed pose blends the difference away over blend_us (200 ms); above
//     max(200 m, speed x 0.5 s) it snaps instead.
//   * delay: starts at 100 ms, then follows 1.5 x sample interval + 2 x p95 arrival jitter, clamped to 80..250 ms, rising at most
//     100 ms/s and falling at most 10 ms/s (render time never jumps).

#include <array>
#include <cstddef>
#include <cstdint>

#include "core/ghost/math.h"
#include "core/ghost/sample.h"
#include "core/ghost/sync_stats.h"

namespace x4mp::ghost {

struct InterpolatorConfig {
  std::int64_t default_delay_us = 100'000;
  std::int64_t min_delay_us = 80'000;
  std::int64_t max_delay_us = 250'000;
  std::int64_t extrap_cap_us = 500'000;
  std::int64_t stale_us = 5'000'000;
  std::int64_t blend_us = 200'000;
  double snap_min_m = 200.0;
  double snap_speed_s = 0.5;     // snap distance = max(snap_min_m, speed * snap_speed_s)
  double blend_deadband_m = 0.5; // smaller deviations are not treated as corrections
  bool adaptive_delay = true;
};

// Adaptive interpolation delay from observed arrival jitter. Fixed storage.
class DelayController {
 public:
  explicit DelayController(const InterpolatorConfig& cfg = {}) noexcept : cfg_(cfg), current_us_(cfg.default_delay_us) {}
  // d = arrival_server_time - sample_time of one received sample; sample_dt_us = its spacing to the previous sample (0 = unknown).
  void on_sample(std::int64_t arrival_us, std::int64_t sample_t_us, std::int64_t sample_dt_us) noexcept;
  void step(std::int64_t dt_us) noexcept;  // slew current towards target
  [[nodiscard]] std::int64_t current_us() const noexcept { return current_us_; }
  [[nodiscard]] std::int64_t target_us() const noexcept { return target_us_; }
  [[nodiscard]] double jitter_p95_us() const noexcept { return jitter_p95_us_; }

 private:
  static constexpr std::size_t kWin = 64;
  InterpolatorConfig cfg_;
  std::array<std::int64_t, kWin> d_{};
  std::size_t head_ = 0, n_ = 0;
  double interval_us_ = 50'000;
  double jitter_p95_us_ = 0;
  std::int64_t target_us_ = 100'000;
  std::int64_t current_us_;
};

enum class PoseState : std::uint8_t {
  Empty,           // no sample yet: nothing to show
  Early,           // render time precedes the oldest sample: shown at that sample
  Interpolating,   // between two samples (or holding in front of a discontinuity)
  Extrapolating,   // past the newest sample, within the cap
  Held,            // past the cap: parked at the last extrapolated pose
  Stale            // no data for stale_us: should be hidden
};

struct RenderPose {
  PoseState state = PoseState::Empty;
  std::uint16_t sector = 0;
  Vec3 pos{};             // displayed position (blend offset included)
  Euler rot{};
  std::uint16_t flags = 0;
  std::uint8_t hull = 255, shield = 255;
  bool snapped = false;   // discontinuity this frame: place the ghost, do not move it
  bool hidden = true;     // Empty, Stale, or the governing sample carries the Hidden flag
  double speed_mps = 0;
  std::int64_t render_t_us = 0;       // server time that was evaluated (now - delay)
  std::int64_t represented_t_us = 0;  // server time the pose stands for (differs from render_t_us when Held)
  [[nodiscard]] bool visible() const noexcept { return !hidden; }
};

class Interpolator {
 public:
  static constexpr std::size_t kRing = 8;

  explicit Interpolator(const InterpolatorConfig& cfg = {}) noexcept : cfg_(cfg), delay_(cfg) {}

  // arrival_us = the receiver's estimate of server time when the sample arrived (feeds the jitter estimate; 0 = unknown).
  void push(const Sample& s, std::int64_t arrival_us) noexcept;
  void apply_state(std::int64_t t_us, std::uint16_t flags, std::uint8_t hull, std::uint8_t shield) noexcept;
  [[nodiscard]] RenderPose render(std::int64_t now_us) noexcept;
  void reset() noexcept { *this = Interpolator(cfg_); }  // drops samples, stats and the blend; keeps the config

  [[nodiscard]] std::size_t sample_count() const noexcept { return n_; }
  [[nodiscard]] std::int64_t delay_us() const noexcept { return delay_.current_us(); }
  [[nodiscard]] const DelayController& delay_controller() const noexcept { return delay_; }
  [[nodiscard]] SyncStats& stats() noexcept { return stats_; }
  [[nodiscard]] const SyncStats& stats() const noexcept { return stats_; }
  [[nodiscard]] std::int64_t newest_sample_us() const noexcept { return n_ ? ring_[n_ - 1].s.t_us : 0; }

 private:
  struct Node {
    Sample s;
    Quat q;
  };
  struct Raw {
    PoseState state = PoseState::Empty;
    std::uint16_t sector = 0, flags = 0;
    std::uint8_t hull = 255, shield = 255;
    Vec3 pos{}, vel{};
    Euler rot{};
    double speed = 0;
    std::int64_t governing_t_us = 0;
    std::uint16_t governing_flags = 0;
    std::int64_t represented_t_us = 0;
  };
  [[nodiscard]] static bool discontinuity(const Sample& a, const Sample& b) noexcept;
  [[nodiscard]] Raw evaluate(std::int64_t t_us) const noexcept;

  InterpolatorConfig cfg_;
  DelayController delay_;
  std::array<Node, kRing> ring_{};
  std::size_t n_ = 0;
  std::int64_t last_push_t_us_ = 0;
  bool have_push_ = false;

  // render state
  std::int64_t last_now_us_ = 0;
  bool have_prev_ = false;
  Raw prev_{};
  std::int64_t prev_render_t_us_ = 0;
  bool prev_hidden_ = true;
  Vec3 offset_{};
  SyncStats stats_;
};

}  // namespace x4mp::ghost
