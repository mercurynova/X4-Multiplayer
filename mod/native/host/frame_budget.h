#pragma once
// host/frame_budget: per-frame time accounting for the main-thread frame loop (M2-04, mod-design 2.4).
//
// begin_frame() ... end_frame() brackets one on_frame_update. Features cannot be pre-empted, so the budget is
// cooperative: FrameInfo/HostContext expose remaining_ns() and a feature slices its work to it (cursor-based jobs).
// The accounting records the frame's elapsed time, counts frames over budget and keeps a rolling window of the last
// kWindow frames for p50/p95. The clock is injectable (tests) and defaults to steady_clock, which on MSVC is
// QueryPerformanceCounter based.

#include <array>
#include <cstddef>
#include <cstdint>

namespace x4mp::host {

using ClockNs = std::int64_t (*)() noexcept;
[[nodiscard]] std::int64_t qpc_now_ns() noexcept;  // monotonic nanoseconds (default clock)

struct FrameStats {
  std::uint64_t frames = 0;
  std::uint64_t over_budget = 0;
  std::int64_t last_ns = 0;
  std::int64_t max_ns = 0;        // since the last reset_window()
  std::int64_t p50_ns = 0;        // over the rolling window
  std::int64_t p95_ns = 0;
  std::int64_t avg_ns = 0;        // over the rolling window
  std::int64_t budget_ns = 0;
};

class FrameBudget {
 public:
  static constexpr std::size_t kWindow = 512;

  explicit FrameBudget(std::int64_t budget_us, ClockNs clock = nullptr) noexcept;

  void set_budget_us(std::int64_t budget_us) noexcept;
  [[nodiscard]] std::int64_t budget_ns() const noexcept { return budget_ns_; }

  void begin_frame() noexcept;
  // Elapsed since begin_frame (0 when no frame is open).
  [[nodiscard]] std::int64_t elapsed_ns() const noexcept;
  // Budget minus elapsed, never negative.
  [[nodiscard]] std::int64_t remaining_ns() const noexcept;
  [[nodiscard]] bool over_budget() const noexcept { return elapsed_ns() > budget_ns_; }
  // Records the frame; returns its elapsed time. No-op (returns 0) when no frame is open.
  std::int64_t end_frame() noexcept;

  [[nodiscard]] FrameStats stats() const noexcept;
  // Clears the rolling window and the max; keeps the lifetime counters. Called after each perf log line.
  void reset_window() noexcept;

 private:
  std::int64_t now() const noexcept;

  ClockNs clock_;
  std::int64_t budget_ns_;
  std::int64_t frame_start_ = 0;
  bool open_ = false;
  std::uint64_t frames_ = 0;
  std::uint64_t over_ = 0;
  std::int64_t last_ = 0;
  std::int64_t max_ = 0;
  std::array<std::int64_t, kWindow> window_{};
  std::size_t window_count_ = 0;  // valid samples (<= kWindow)
  std::size_t window_head_ = 0;
};

}  // namespace x4mp::host
