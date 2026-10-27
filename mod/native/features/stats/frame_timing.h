#pragma once
// features/stats/frame_timing: per-subsystem frame cost, sampled by whoever owns the work and read by the 30 s budget line (M4-30).
//
// The host FrameBudget measures the WHOLE mod per frame (the `stats:` line's mod_p95/mod_max). The M4 acceptance is narrower:
// "< 3 ms/frame authority mod cost", and the authority's step is only one part of the mod's frame. So a subsystem times itself and
// drops the milliseconds into a named slot; the report takes the slot - p50/p95/max over the window - and resets it, so one window
// never leaks into the next and a stalled report cannot hide a spike behind stale samples.
//
//   AuthorityFlow::step   -> timing_hub().slot("authority").add(elapsed_ms)
//   the 30 s budget line  -> timing_hub().take("authority")
//
// Main thread only. Header-only on purpose (like stats_lane.h): no CMake edit, and a Catch2 test drives a slot directly.

#include <algorithm>
#include <cstdint>
#include <map>
#include <string>
#include <string_view>
#include <vector>

#include "features/stats/stats_collector.h"

namespace x4mp::features::stats {

struct TimingSummary {
  std::uint32_t samples = 0;
  double ms_p50 = 0.0;
  double ms_p95 = 0.0;
  double ms_max = 0.0;
};

class FrameTiming {
 public:
  // 30 s at 120 fps is 3600 frames; a 240 fps window is 7200, so the cap keeps a full window and then overwrites the oldest sample
  // in place. Order does not matter to percentile(), so there is no ring arithmetic to get wrong.
  static constexpr std::size_t kMaxSamples = 8192;

  void add(double ms) {
    if (!(ms >= 0.0)) return;  // a NaN from a broken clock must not poison the window
    if (ms_.size() < kMaxSamples) {
      ms_.push_back(ms);
    } else {
      ms_[next_] = ms;
      next_ = (next_ + 1) % kMaxSamples;
    }
  }

  // p50/p95/max since the last take(); starts a new window.
  [[nodiscard]] TimingSummary take() {
    TimingSummary s;
    s.samples = static_cast<std::uint32_t>(ms_.size());
    if (!ms_.empty()) {
      s.ms_max = *std::max_element(ms_.begin(), ms_.end());
      s.ms_p50 = percentile(ms_, 0.50);
      s.ms_p95 = percentile(ms_, 0.95);
    }
    clear();
    return s;
  }

  void clear() noexcept {
    ms_.clear();
    next_ = 0;
  }
  [[nodiscard]] std::size_t size() const noexcept { return ms_.size(); }

 private:
  std::vector<double> ms_;
  std::size_t next_ = 0;
};

// The process-wide set of slots. A map, not a vector: `slot()` hands out a reference and a vector would invalidate it on growth.
class TimingHub {
 public:
  FrameTiming& slot(std::string_view name) { return slots_[std::string(name)]; }
  // The window of `name`; an empty summary for a slot nobody ever wrote.
  [[nodiscard]] TimingSummary take(std::string_view name) {
    const auto it = slots_.find(std::string(name));
    return it == slots_.end() ? TimingSummary{} : it->second.take();
  }
  void clear() noexcept { slots_.clear(); }  // tests

 private:
  std::map<std::string, FrameTiming, std::less<>> slots_;
};

inline TimingHub& timing_hub() noexcept {
  static TimingHub hub;
  return hub;
}

}  // namespace x4mp::features::stats
