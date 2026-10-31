#pragma once
// features/economy_client/economy_client_hub: the node's view of the player-facing economy (M5-29, M5-30; docs/m5-plan.md W7,
// docs/mod-design.md 12.5-12.7).
//
// The server owns the books. This hub does three things and decides nothing:
//   1. turns a Lua request (send credits, pool deposit/withdraw, loan repay/forgive, trade accept/cancel) into the frame the
//      server expects, with a request key that is unique for this node;
//   2. keeps the last answer to each request key plus every LoanStatus and TradeStatus the server sent, so the screen can show
//      live data without asking anybody;
//   3. renders that state as one JSON snapshot for the Lua screen (topic x4mp.economy).
//
// Nothing here touches the universe or the game's money: a transfer moves ledger money, and the wallet feature brings the
// balance back (wallet_hub, M5-23). That separation is why this file has no host dependency and is unit-tested directly.
//
// Ids cross the Lua boundary as "lo:hi" in hex (Id128Value::hex), because a 128-bit decimal number is unreadable in a chat
// line or a log.

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "features/economy_client/economy_wire.h"

namespace x4mp::features::economy_client {

enum class Op : std::uint8_t { Send = 0, Deposit, Withdraw, Repay, Forgive, Accept, Cancel, Ledger };

[[nodiscard]] const char* op_name(Op op) noexcept;
// "send", "deposit", ... -> Op. False on anything else.
[[nodiscard]] bool parse_op(std::string_view name, Op& out) noexcept;

struct RequestParams {
  std::uint16_t to_player = 0;       // Send
  std::int64_t amount = 0;           // Send, Deposit, Withdraw, Repay, Forgive
  Id128Value id;                     // Repay, Forgive, Accept, Cancel
  std::uint32_t version = 0;         // Accept: the version the player saw
  std::uint32_t receive_into_asset = 0;  // Accept: 0 = the default destination
  std::string memo;                  // Send
  std::uint16_t limit = 0;           // Ledger: 0 = the server default (50)
  std::string before;                // Ledger: ULID cursor; empty = from the newest
};

// One frame waiting for the link: what to send and the key it was booked under (for the log line and the retry order).
struct OutFrame {
  std::uint16_t type = 0;
  std::vector<std::uint8_t> bytes;
  Id128Value key;
  Op op = Op::Send;
};

struct EconomyStats {
  std::uint64_t results = 0;       // EconomyResult taken
  std::uint64_t rejected = 0;      // ... of which status Rejected
  std::uint64_t loans = 0;         // LoanStatus taken
  std::uint64_t trades = 0;        // TradeStatus taken
  std::uint64_t trade_results = 0; // TradeResult taken
  std::uint64_t ledger_pages = 0;  // LedgerPage taken
  std::uint64_t sent = 0;
  std::uint64_t send_failed = 0;
  std::uint64_t bad = 0;           // payloads that were not an economy frame, or a request the hub refused
  std::uint64_t requests = 0;      // requests the Lua side made (accepted ones)
  std::uint64_t notices = 0;       // incoming offers the player was told about (M5-30)
};

// M5-30: one thing that arrived and the player has not been told about yet. The hub only says what is NEW, so a resent status
// or a later update of the same trade never rings twice; the feature turns each notice into one chat line and one toast, and the
// HUD counts what is still open from the snapshot.
enum class NoticeKind : std::uint8_t { LoanOffer = 0, TradeOffer };

struct Notice {
  NoticeKind kind = NoticeKind::LoanOffer;
  Id128Value id;
  std::uint16_t from = 0;   // the player on the other side
  std::int64_t amount = 0;  // loan principal, or the credits the other side puts on the table (0 when there are none)
  std::uint32_t version = 0;
};

class EconomyClientHub {
 public:
  static constexpr std::size_t kMaxRows = 64;

  void reset();

  // The node's own player id, used as key_hi and to tell "mine" from "theirs" in the snapshot.
  void set_self(std::uint16_t player) noexcept { self_ = player; }
  [[nodiscard]] std::uint16_t self() const noexcept { return self_; }
  // Seeds the request counter from the wall clock, so a restart does not reuse (1, player) with a different payload
  // (EconomyReject::RequestIdReuse, economy.fbs:65).
  void seed_key(std::uint64_t value) noexcept { key_ = value; }

  // Server -> node. False when the type is not one of ours (the caller ignores it).
  bool on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload);

  // Lua -> node. Builds one frame; `error` says why when it returns false.
  bool request(Op op, const RequestParams& params, std::string& error);

  [[nodiscard]] std::vector<OutFrame> take_unsent(std::size_t max);
  void note_send_failed();

  // Incoming offers that have not been announced yet, oldest first. Taking them is what marks them announced.
  [[nodiscard]] std::vector<Notice> take_notices();

  // The screen asks for a snapshot only when something changed; this flag is cleared by clear_dirty().
  [[nodiscard]] bool dirty() const noexcept { return dirty_; }
  void clear_dirty() noexcept { dirty_ = false; }

  [[nodiscard]] std::string snapshot_json() const;

  [[nodiscard]] const EconomyStats& stats() const noexcept { return stats_; }
  [[nodiscard]] const std::optional<EconomyResultView>& last_result() const noexcept { return last_; }
  [[nodiscard]] const std::vector<LoanRowView>& loans() const noexcept { return loans_; }
  [[nodiscard]] const std::vector<TradeRowView>& trades() const noexcept { return trades_; }
  [[nodiscard]] const std::optional<TradeResultView>& last_trade_result() const noexcept { return last_trade_; }
  // The ledger rows this node has been shown, newest first: the last page replaces them, a cursor page appends the older
  // rows. `more` says the server has at least one older row this node may see.
  [[nodiscard]] const std::vector<LedgerRowView>& ledger() const noexcept { return ledger_; }
  [[nodiscard]] bool ledger_more() const noexcept { return ledger_more_; }
  // True while a page is on its way. A second request is refused, because the answer could not be told apart from the
  // first one's and the list would jump.
  [[nodiscard]] bool ledger_pending() const noexcept { return ledger_pending_; }

 private:
  Id128Value next_key() noexcept;
  void put_loan(const LoanRowView& row);
  void put_trade(const TradeRowView& row);
  void put_ledger_page(const LedgerPageView& page);

  std::uint16_t self_ = 0;
  std::uint64_t key_ = 0;
  EconomyStats stats_;
  bool dirty_ = false;
  std::optional<EconomyResultView> last_;
  std::optional<TradeResultView> last_trade_;
  std::vector<LoanRowView> loans_;
  std::vector<TradeRowView> trades_;
  std::vector<LedgerRowView> ledger_;
  bool ledger_more_ = false;
  bool ledger_pending_ = false;
  std::string ledger_before_;  // the cursor of the page in flight; empty = the request from the newest row
  std::vector<OutFrame> unsent_;
  std::vector<Notice> notices_;
};

// Process-wide instance, like wallet_hub() and team_hub(): the inbound dispatcher and the feature share one state.
EconomyClientHub& economy_client_hub();

}  // namespace x4mp::features::economy_client
