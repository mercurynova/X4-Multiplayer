#pragma once
// features/economy/economy_hub: the one hand-over point between the join feature and the trade/stock feature (M5-13..M5-17;
// docs/m5-plan.md W4, docs/mod-design.md 5.3, ADR-034).
//
// Two queues and one inbox, all on the main thread:
//   inbox   an EntityCargo frame from the server (any thread; JoinFeature hands it over here, the feature drains it in on_frame)
//   cargo   the actions shim lines (M5-15): one signed amount per (container, ware), flushed once per frame as ONE
//           AddUITriggeredEvent under a per-frame ware budget
//   money   the NPC counter-flow (M5-17): transfer_money to the station owner's faction, same flush
//
// Same shape as ghost_hub / statics_hub: a process-wide accessor, no lock held while the game is touched, and every counter
// greppable in one 30 s log line ("[economy] ...").

#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "message_ids_generated.h"

namespace x4mp::features::economy {

// FNV-1a 32 of a ware macro. The same hash the session string table uses for a string ref, so a WareAmount's ware_ref can be
// resolved through ghost_core's table, and a ware the local game reports can be matched against a remote ref without one.
[[nodiscard]] std::uint32_t ware_ref_of(std::string_view ware) noexcept;

// One line of the actions shim: change `component`'s stock of `ware` by `amount` (exact=, M5-15). `amount > 0` adds.
struct CargoOp {
  std::uint64_t component = 0;
  std::string ware;
  std::int32_t amount = 0;
};

// One NPC counter-flow line (M5-17): move `amount` Credits from `from` to `to` (faction ids as MD spells them).
struct MoneyOp {
  std::string from;
  std::string to;
  std::int64_t amount = 0;
};

// M5-17: the counter-flow of one trade. It is the opposite of the player's flow, so the NPC economy does not leak: a buy
// (amount > 0) credits the station owner's faction and debits the payer, a sale (amount < 0) debits the owner and credits the payer.
// The payer is the reporting player's faction, never a value a client sent, and an unknown side means no op at all: the mod does not
// guess an addressee (and does not move money when the station belongs to the payer's own faction).
[[nodiscard]] std::optional<MoneyOp> trade_money_op(std::string_view payer, std::string_view station_owner,
                                                    std::int32_t amount, std::int32_t unit_price);

struct EconomyStats {
  // M5-13/14, the client half
  std::uint64_t trades_sent = 0, trades_deduped = 0, trades_unmapped = 0, trades_failed = 0;
  std::uint64_t diff_reads = 0, diff_failed = 0, diff_missed = 0;
  std::uint64_t own_sent = 0, own_changes = 0, own_failed = 0;
  // M5-15/16, the shim
  std::uint64_t cargo_in = 0, cargo_out = 0, cargo_applied = 0, cargo_skipped = 0, cargo_bad = 0;
  std::uint64_t cargo_ops = 0, cargo_frames = 0, cargo_burst = 0;
  std::uint64_t money_ops = 0, money_skipped = 0;
  // M5-15, the authority half
  std::uint64_t stock_applied = 0, stock_deferred = 0, stock_dropped = 0, snapshots = 0;
};

class EconomyHub {
 public:
  // M5-16: an EntityCargo frame. Decoded here, applied by the feature in on_frame (any thread).
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload);
  [[nodiscard]] std::vector<events::CargoView> take_cargo();
  [[nodiscard]] std::size_t queued() const;

  void queue(const CargoOp& op);
  void queue(const MoneyOp& op);
  [[nodiscard]] std::vector<CargoOp> take_cargo_ops(std::size_t max);
  [[nodiscard]] std::vector<MoneyOp> take_money_ops(std::size_t max);

  [[nodiscard]] const EconomyStats& stats() const noexcept { return stats_; }
  [[nodiscard]] EconomyStats& stats() noexcept { return stats_; }
  void reset();

  // M5-13/14: the events feature hands every Trade event to the reporter installed here (main thread). False when nothing is
  // installed or the event was not a trade.
  void set_trade_sink(std::function<bool(const events::MdEvent&)> fn) { trade_sink_ = std::move(fn); }
  [[nodiscard]] bool on_event(const events::MdEvent& event) { return trade_sink_ ? trade_sink_(event) : false; }

 private:
  static constexpr std::size_t kMaxInbox = 64;   // a burst of snapshots is dropped, never grown
  static constexpr std::size_t kMaxOps = 1024;   // a runaway queue is dropped, never grown

  mutable std::mutex mutex_;
  std::vector<events::CargoView> inbox_;
  std::vector<CargoOp> cargo_ops_;
  std::vector<MoneyOp> money_ops_;
  EconomyStats stats_{};
  std::function<bool(const events::MdEvent&)> trade_sink_;
};

[[nodiscard]] EconomyHub& economy_hub();

}  // namespace x4mp::features::economy
