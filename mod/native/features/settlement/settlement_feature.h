#pragma once
// features/settlement/settlement_feature: the authority side of an escrowed trade (M5-31; docs/m5-plan.md M5-31,
// docs/mod-design.md 12.6).
//
// The ledger escrows both sides, then hands the asset side here as one AssetTransferOrder. The authority is the only node that
// may change an owner in the running universe, so this is the only place a trade actually moves anything:
//   1. precheck EVERY line before touching the universe (the component exists, is not a wreck, is not a ship a player sits in,
//      the destination team has a faction) and remember the owner each asset had;
//   2. apply the lines in order;
//   3. on a failure undo the applied prefix in reverse order, which restores the exact owner the line found;
//   4. write the outcome to the journal BEFORE the confirm goes out, and answer TradeQuery from that journal after a restart.
//
// v1 settles OwnerChange lines only - a ship or a station for credits. A WareMove line is refused with a clear error, because
// the cargo shim reports no per-op result, so a ware move could be applied and never undone: refusing is the honest answer
// until the shim can say what happened (docs/mod-design.md 12.6, uncertainty 3.1). The refusal is a normal EconomyResult-side
// rollback: the server refunds both escrows and the trade ends RolledBack.
//
// Nothing here writes to the ledger or to a wallet. The server decides whether a trade may settle; this file only carries out
// the order it was given and reports exactly what happened.

#include <cstdint>
#include <filesystem>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "features/settlement/settlement_journal.h"
#include "host/feature.h"

namespace x4mp::features::settlement {

struct SettlementStats {
  std::uint64_t orders = 0;        // AssetTransferOrder taken
  std::uint64_t applied = 0;       // ... that changed the universe
  std::uint64_t compensated = 0;   // ... that were undone after a failure
  std::uint64_t refused = 0;       // ... refused at precheck (nothing was touched)
  std::uint64_t needs_admin = 0;   // ... whose undo failed: the residual is in the journal
  std::uint64_t queries = 0;       // TradeQuery answered
  std::uint64_t duplicates = 0;    // an order for a trade the journal already knows
  std::uint64_t confirm_failed = 0;
  std::uint64_t bad = 0;           // a payload that did not verify
};

class SettlementHub {
 public:
  void reset();
  // Loads the sidecar. A journal that does not parse is logged by the caller and the hub stays usable (empty).
  void open_journal(const std::filesystem::path& path);

  // Server -> authority. False when the type is not one of ours.
  bool on_frame(host::HostContext& ctx, std::uint16_t type, std::span<const std::uint8_t> payload);

  // Sends whatever confirm is still waiting for the link; called every frame.
  void step(host::HostContext& ctx);

  [[nodiscard]] Journal& journal() noexcept { return journal_; }
  [[nodiscard]] const Journal& journal() const noexcept { return journal_; }
  [[nodiscard]] const SettlementStats& stats() const noexcept { return stats_; }

 private:
  bool handle_order(host::HostContext& ctx, std::span<const std::uint8_t> payload);
  bool handle_query(host::HostContext& ctx, std::span<const std::uint8_t> payload);
  void queue_confirm(ConfirmData data);
  // The confirm for an entry the journal already holds, so a repeated order or a query after a restart gets the same answer.
  [[nodiscard]] static ConfirmData confirm_of(const JournalEntry& entry);

  Journal journal_;
  SettlementStats stats_;
  std::vector<ConfirmData> outbox_;
};

// Process-wide, like wallet_hub() and economy_client_hub(): the inbound router and the feature share one state.
SettlementHub& settlement_hub();

class SettlementFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "settlement"; }

  void on_init(host::HostContext& ctx) override;
  void on_universe_ready(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  void report(host::HostContext& ctx);

  std::int64_t last_log_us_ = 0;
};

}  // namespace x4mp::features::settlement
