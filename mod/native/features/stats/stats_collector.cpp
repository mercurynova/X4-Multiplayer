#include "features/stats/stats_collector.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace x4mp::features::stats {

double percentile(std::vector<double> values, double p) {
  if (values.empty()) return 0.0;
  p = std::clamp(p, 0.0, 1.0);
  const auto n = values.size();
  const auto rank = static_cast<std::size_t>(std::ceil(p * static_cast<double>(n)));  // 1-based nearest rank
  const std::size_t idx = rank == 0 ? 0 : std::min(rank, n) - 1;
  std::nth_element(values.begin(), values.begin() + static_cast<std::ptrdiff_t>(idx), values.end());
  return values[idx];
}

void StatsCollector::on_frame(double delta_s, double mod_cost_ms) {
  if (!(delta_s >= 0.0) || !std::isfinite(delta_s)) return;
  window_s_ += delta_s;
  ++frames_;
  if (frame_ms_.size() < kMaxSamples) {
    frame_ms_.push_back(delta_s * 1000.0);
    mod_ms_.push_back(std::max(0.0, mod_cost_ms));
  }
}

WindowSummary StatsCollector::take_window() {
  WindowSummary s;
  s.frames = frames_;
  s.window_s = window_s_;
  if (window_s_ > 0.0) s.fps = static_cast<float>(static_cast<double>(frames_) / window_s_);
  s.frame_ms_p95 = static_cast<float>(percentile(frame_ms_, 0.95));
  s.mod_ms_p95 = static_cast<float>(percentile(mod_ms_, 0.95));
  s.mod_ms_max = mod_ms_.empty() ? 0.0f : static_cast<float>(*std::max_element(mod_ms_.begin(), mod_ms_.end()));
  frame_ms_.clear();
  mod_ms_.clear();
  window_s_ = 0.0;
  frames_ = 0;
  return s;
}

std::uint32_t rate_per_s(std::uint64_t previous_total, std::uint64_t current_total, double seconds) {
  if (current_total < previous_total || !(seconds > 0.0)) return 0;
  const double rate = static_cast<double>(current_total - previous_total) / seconds;
  return rate >= static_cast<double>(std::numeric_limits<std::uint32_t>::max()) ? std::numeric_limits<std::uint32_t>::max()
                                                                                  : static_cast<std::uint32_t>(rate + 0.5);
}

}  // namespace x4mp::features::stats
