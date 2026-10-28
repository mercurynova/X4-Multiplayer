#pragma once
// features/economy: trade detection and station stock (M5-13..M5-17; docs/m5-plan.md W4, docs/mod-design.md 5.3, ADR-034).
//
// The client half:
//   detection   the events feature already parses the MD T line (event_player_trade_completed) into an MdEvent; on_frame drains the
//               events ring and hands every Trade event to TradeReporter, which sends one Intent{TradeReport} (M5-13/M5-14)
//   own cargo   the player's own ship is reported upward as EntityCargo at most once a second, on change only (M5-14)
//   fallback    a 2 s cargo diff while docked. It cannot name a station without the hook, so it never fabricates a report: it
//               counts a trade the hook missed and logs it (M5-13: a failed read reports nothing, because an empty read is not a
//               change)
//
// The shared half (M5-15/M5-16): an EntityCargo for a station is compared with this node's own copy and the difference is queued
// for the actions shim; the authority does the same from a TradeReport instead. One AddUITriggeredEvent per frame carries every
// queued line, under a per-frame ware budget, so a 500 unit trade cannot stall a frame.

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "features/economy/economy_hub.h"
#include "features/economy/trade_report.h"
#include "host/feature.h"

namespace x4mp::features {

class EconomyFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "economy"; }

  void on_init(host::HostContext& ctx) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  void apply_snapshots(host::HostContext& ctx);
  void report_own_cargo(host::HostContext& ctx, std::int64_t now_us);
  void cargo_diff_fallback(host::HostContext& ctx, std::int64_t now_us);
  void flush(host::HostContext& ctx);
  void report(host::HostContext& ctx, bool force);

  std::unique_ptr<economy::TradeReporter> reporter_;
  std::uint64_t seen_trades_ = 0;      // reporter_->stats().sent as of the last events drain
  std::uint64_t applied_snapshots_ = 0;
  std::uint64_t bad_snapshots_ = 0;
  std::uint64_t budget_hits_ = 0;
  std::int64_t own_read_us_ = 0;
  std::int64_t diff_read_us_ = 0;
  std::int64_t last_log_us_ = 0;
  std::uint64_t own_hash_ = 0;         // a cheap signature of the own ship's hold (ware count + amounts)
  bool own_hash_valid_ = false;
  std::uint64_t docked_hash_ = 0;      // the same signature for the 2 s docked fallback
  bool docked_hash_valid_ = false;
  bool on_authority_ = false;
  bool universe_ready_ = false;
};

}  // namespace x4mp::features
