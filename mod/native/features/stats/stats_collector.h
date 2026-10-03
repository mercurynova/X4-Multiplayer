#pragma once
// features/stats/stats_collector: the numbers behind NodeStats and the 5 s perf line (M2-12). SDK-free and clock-free (time comes
// in as the frames' delta_s), so Catch2 tests it directly.
//
// Per frame the feature feeds on_frame(delta_s, mod_cost_ms):
//   delta_s      wall-clock since the previous frame (FrameInfo.delta_s): FPS and frame_ms_p95 come from it.
//   mod_cost_ms  what the mod itself spent on the main thread this frame so far (the host's FrameBudget elapsed time): the
//                "main-thread net ms" p95. While connected the join/session pump is the bulk of it.
// take_window() closes the current window (~2 s) and returns its summary; rates come from the cumulative byte counters.

#include <cstdint>
#include <vector>

namespace x4mp::features::stats {

// Nearest-rank percentile (p in 0..1) of `values`; 0 for an empty set. Takes the vector by value (it is reordered).
[[nodiscard]] double percentile(std::vector<double> values, double p);

struct WindowSummary {
  std::uint32_t frames = 0;
  double window_s = 0.0;
  float fps = 0.0f;
  float frame_ms_p95 = 0.0f;
  float mod_ms_p95 = 0.0f;
  float mod_ms_max = 0.0f;
};

class StatsCollector {
 public:
  static constexpr std::size_t kMaxSamples = 1024;  // per window; a 2 s window at 240 fps is 480

  void on_frame(double delta_s, double mod_cost_ms);
  [[nodiscard]] double window_seconds() const noexcept { return window_s_; }
  // Summary of everything since the last take_window(); starts a new window.
  WindowSummary take_window();

 private:
  std::vector<double> frame_ms_;
  std::vector<double> mod_ms_;
  double window_s_ = 0.0;
  std::uint32_t frames_ = 0;
};

// Bytes per second between two cumulative counters over `seconds` (0 when the counter went backwards, e.g. a new connection, or no time passed).
[[nodiscard]] std::uint32_t rate_per_s(std::uint64_t previous_total, std::uint64_t current_total, double seconds);

}  // namespace x4mp::features::stats
