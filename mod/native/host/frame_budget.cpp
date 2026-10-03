#include "host/frame_budget.h"

#include <algorithm>
#include <chrono>
#include <vector>

namespace x4mp::host {

std::int64_t qpc_now_ns() noexcept {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

FrameBudget::FrameBudget(std::int64_t budget_us, ClockNs clock) noexcept
    : clock_(clock ? clock : &qpc_now_ns), budget_ns_(budget_us * 1000) {}

void FrameBudget::set_budget_us(std::int64_t budget_us) noexcept { budget_ns_ = budget_us * 1000; }

std::int64_t FrameBudget::now() const noexcept { return clock_(); }

void FrameBudget::begin_frame() noexcept {
  frame_start_ = now();
  open_ = true;
}

std::int64_t FrameBudget::elapsed_ns() const noexcept { return open_ ? std::max<std::int64_t>(0, now() - frame_start_) : 0; }

std::int64_t FrameBudget::remaining_ns() const noexcept {
  return open_ ? std::max<std::int64_t>(0, budget_ns_ - elapsed_ns()) : budget_ns_;
}

std::int64_t FrameBudget::end_frame() noexcept {
  if (!open_) return 0;
  const std::int64_t e = elapsed_ns();
  open_ = false;
  ++frames_;
  if (e > budget_ns_) ++over_;
  last_ = e;
  max_ = std::max(max_, e);
  window_[window_head_] = e;
  window_head_ = (window_head_ + 1) % kWindow;
  window_count_ = std::min(window_count_ + 1, kWindow);
  return e;
}

FrameStats FrameBudget::stats() const noexcept {
  FrameStats s;
  s.frames = frames_;
  s.over_budget = over_;
  s.last_ns = last_;
  s.max_ns = max_;
  s.budget_ns = budget_ns_;
  if (window_count_ == 0) return s;
  try {
    std::vector<std::int64_t> v(window_.begin(), window_.begin() + static_cast<std::ptrdiff_t>(window_count_));
    std::int64_t sum = 0;
    for (const auto x : v) sum += x;
    s.avg_ns = sum / static_cast<std::int64_t>(v.size());
    const auto pick = [&v](double q) {
      const auto idx = static_cast<std::size_t>(q * static_cast<double>(v.size() - 1) + 0.5);
      std::nth_element(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(idx), v.end());
      return v[idx];
    };
    s.p50_ns = pick(0.50);
    s.p95_ns = pick(0.95);
  } catch (...) {
    // allocation failure: leave percentiles at 0
  }
  return s;
}

void FrameBudget::reset_window() noexcept {
  window_count_ = 0;
  window_head_ = 0;
  max_ = 0;
}

}  // namespace x4mp::host
