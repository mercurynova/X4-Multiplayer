#pragma once
// features/stats: NodeStats every 2 s while connected, a perf log line every 5 s (M2-12; docs/mod-design.md 2.7) and the aggregated
// 30 s budget line (M4-30; docs/m4-plan.md W7).
//
// Per frame: the frame interval (FPS, frame_ms_p95) and the mod's own main-thread cost so far this frame (the host FrameBudget's
// elapsed time; registered last so it covers every earlier feature). Every 2 s of frame time the window is closed; while the join
// feature's diag-hub link says "welcomed" the summary plus rtt / byte rates from the session's counters go out as one NodeStats
// (Control lane). Every 5 s one line goes to the log (category perf) whether connected or not. Windows are measured in the frames' own
// delta_s, so there is no clock and no timer thread here.
//
// Every 30 s the budget line adds what the acceptance numbers are quoted from: the authority flow's own step cost (p50/p95/max, read
// from stats::timing_hub()), the roster's client count, the ghost count, the suppression total and the per-client byte rate. It is the
// same shape whether the node is a client or the authority - a client simply reports authority_ms=0/0/0 and suppressed=0.

#include <cstdint>

#include "features/stats/stats_collector.h"
#include "host/feature.h"

namespace x4mp::features {

class StatsFeature final : public host::IFeature {
 public:
  static constexpr double kSendIntervalS = 2.0;
  static constexpr double kLogIntervalS = 5.0;
  static constexpr double kBudgetIntervalS = 30.0;

  [[nodiscard]] std::string_view name() const noexcept override { return "stats"; }
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;

 private:
  stats::StatsCollector collector_;
  stats::StatsCollector log_collector_;     // its own window: the 5 s perf line does not disturb the 2 s NodeStats window
  stats::StatsCollector budget_collector_;  // and a third for the 30 s budget line (M4-30)
  std::uint64_t prev_in_ = 0;
  std::uint64_t prev_out_ = 0;
  bool have_prev_ = false;
  double since_log_s_ = 0.0;
  double since_budget_s_ = 0.0;
  std::uint64_t sent_ = 0;
  float last_rtt_ms_ = 0.0f;
  std::uint32_t last_rx_ = 0;
  std::uint32_t last_tx_ = 0;
};

}  // namespace x4mp::features
