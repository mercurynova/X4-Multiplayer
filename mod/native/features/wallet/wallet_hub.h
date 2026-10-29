#pragma once
// features/wallet/wallet_hub: what this node knows about its own money (M5-22; docs/m5-plan.md W6, docs/mod-design.md 12.1-12.3).
//
// The game function table has AddPlayerMoney and no getter (mod/third_party/x4native/.../x4_game_func_list.inc:40), so native cannot
// read a balance at all. It arrives from Lua instead: ui/x4mp_money.lua polls GetPlayerMoney() every frame and pushes
// x4mp.money {"v":1,"value":<credits>} when the value changes. This hub is what native remembers of that stream: the last balance,
// when it was read, and what the last change was.
//
// The second half is the M5-22 part of the 12.2 reconciliation. Native CAN write money (AddPlayerMoney), and the change it writes comes
// back as an ordinary change on the next poll. note_apply() records what was asked for and observe() recognises the echo, so a write of
// ours is never mistaken for the player's own income (M5-23 builds the pending[] ledger on top of this).
//
// M5-23 adds the ledger half (mod-design 12.2): the node remembers the last WalletUpdate (serverBalance, its ledger version, acked seq)
// and its own deltas the ledger has not booked yet (pending[]). The money it should have is serverBalance + sum(pending), and
// take_convergence() says what to hand AddPlayerMoney to get there. Deltas are commutative and each (node, seq) is booked once, so a
// reconnect that resends an old seq changes nothing; an update older than the last one seen is dropped, not applied.
//
// Pure logic: no SDK, no game calls, no IO. Main thread only (the feature drains the inbox in on_frame).
//
//   Lua x4mp.money -> WalletFeature inbox -> observe()            (what the game actually says)
//   server WalletUpdate -> on_frame_message -> note_server_balance (what the ledger says)
//   both -> take_convergence() -> AddPlayerMoney                  (make the first equal the second)

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

namespace x4mp::features::wallet {

// A money body is one small object. Anything bigger is not one of ours.
inline constexpr std::size_t kMaxMoneyBody = 256;

// What an observation meant. Bad bodies never get here: parse_money_value() refuses them first.
enum class Observation {
  Baseline,  // the first reading of a session: there is nothing to compare with
  Quiet,     // the same balance as the last reading
  Echo,      // exactly the change note_apply() asked the game for
  Change,    // a change this node did not cause: the player's trade, a refund, a repair bill
};

struct WalletStats {
  std::uint64_t polls = 0;     // readings that carried a number
  std::uint64_t bad = 0;       // bodies that were not a money reading
  std::uint64_t baseline = 0;  // first reading of a session
  std::uint64_t quiet = 0;     // unchanged readings
  std::uint64_t changes = 0;   // real changes (the player's, not our echo)
  std::uint64_t echoes = 0;    // changes that were exactly what note_apply() asked for
  std::uint64_t applies = 0;   // note_apply() calls
  // M5-23
  std::uint64_t updates = 0;        // WalletUpdates taken (a newer ledger version for our spendable wallet)
  std::uint64_t stale = 0;          // updates dropped as older than the one already seen
  std::uint64_t no_wallet = 0;      // updates that named no wallet of ours
  std::uint64_t bad_update = 0;     // payloads that were not a WalletUpdate at all
  std::uint64_t converged = 0;      // updates whose target the local money already matched
  std::uint64_t writes = 0;         // AddPlayerMoney calls the hub asked for and the game took
  std::uint64_t write_failed = 0;   // AddPlayerMoney calls that could not be made
  std::uint64_t pending_acked = 0;  // our own deltas the ledger booked
  std::uint64_t pending_dropped = 0;  // deltas dropped because too many stayed unbooked
};

// M5-23: one of this node's own deltas that the ledger has not booked yet. `seq` is per node and strictly monotonic
// (economy.fbs:54) and comes back as WalletUpdate.acked_delta_seq, so a resend after a reconnect is booked once.
struct PendingDelta {
  std::uint64_t seq = 0;
  std::int64_t delta = 0;
  std::uint8_t reason = 0;  // LedgerReason as the wire carries it; history only, never part of the arithmetic (mod-design 12.2)
};

// Far more than a node can accumulate while the server answers: past this the oldest is dropped and counted.
inline constexpr std::size_t kMaxPending = 256;

// The balance out of {"v":1,"value":N}: an object with an integral `value`, any key order, extra fields ignored. A float, a string, a
// missing field, a body that is not JSON or one that is too big is not a reading and returns nullopt (never 0: a fabricated zero looks
// exactly like a player going broke).
[[nodiscard]] std::optional<std::int64_t> parse_money_value(std::string_view text);

class WalletHub {
 public:
  // One poll's worth of truth. `now_us` is the frame clock, kept only so a later task can age the reading.
  Observation observe(std::int64_t local_money, std::uint64_t now_us);
  // Money this node asked the game to add (AddPlayerMoney) and has not seen come back yet. Accumulates: two writes before one poll are
  // recognised as their sum.
  void note_apply(std::int64_t delta);
  // A body that was not a money reading at all (the feature counts it and moves on).
  void note_bad() noexcept { ++stats_.bad; }

  // ---- M5-23: the ledger half of 12.2 ----
  // One WalletUpdate for this node. `own_player`/`own_team` say which row is this node's spendable money (mod-design 12.1:
  // wallet:<player> in PerPlayer, pool:<team> in Shared). Returns true when it was taken; false for a payload that is not a
  // WalletUpdate, an update with no wallet of ours, or one older than the last version seen.
  bool on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t own_player, std::uint16_t own_team);
  // The balance the ledger reported, with the deltas it has already booked removed from pending.
  bool note_server_balance(std::int64_t balance, std::uint64_t version, std::uint64_t ack_seq);
  // A delta of this node to send upward: assigns the next seq and remembers it until WalletUpdate acks it. Returns the seq to
  // put in the CreditDelta (M5-24). `delta` 0 consumes the seq and books nothing.
  std::uint64_t book_pending(std::int64_t delta, std::uint8_t reason);
  // The adjust to hand AddPlayerMoney so the local money becomes serverBalance + sum(pending). nullopt when there is nothing to
  // do: no reading yet (the next poll supplies one), no WalletUpdate yet, or already equal.
  [[nodiscard]] std::optional<std::int64_t> take_convergence() const;
  // The game refused the write take_convergence() asked for (a missing export): count it, keep the target for the next frame.
  void note_write_failed() noexcept { ++stats_.write_failed; }
  // The game took the write: this is the same accounting observe() will later recognise as the echo.
  void note_write(std::int64_t delta);

  [[nodiscard]] bool has_balance() const noexcept { return has_balance_; }
  [[nodiscard]] std::int64_t local_balance() const noexcept { return local_balance_; }
  [[nodiscard]] std::int64_t last_change() const noexcept { return last_change_; }
  [[nodiscard]] std::uint64_t observed_at_us() const noexcept { return observed_at_us_; }
  [[nodiscard]] std::int64_t expected_adjust() const noexcept { return expected_adjust_; }
  [[nodiscard]] const WalletStats& stats() const noexcept { return stats_; }
  // M5-23
  [[nodiscard]] bool server_known() const noexcept { return server_known_; }
  [[nodiscard]] std::int64_t server_balance() const noexcept { return server_balance_; }
  [[nodiscard]] std::uint64_t server_version() const noexcept { return server_version_; }
  [[nodiscard]] std::uint64_t ack_seq() const noexcept { return ack_seq_; }
  [[nodiscard]] std::uint64_t last_seq() const noexcept { return seq_; }
  [[nodiscard]] std::int64_t pending_sum() const noexcept { return pending_sum_; }
  [[nodiscard]] std::size_t pending_count() const noexcept { return pending_.size(); }
  // serverBalance + sum(pending): what this node's money should be (0 when no update arrived yet).
  [[nodiscard]] std::int64_t target_balance() const noexcept { return server_balance_ + pending_sum_; }
  // The local money as this node believes it is: the last reading plus the writes not yet read back.
  [[nodiscard]] std::int64_t effective_local() const noexcept { return local_balance_ + expected_adjust_; }

  void reset();

 private:
  bool has_balance_ = false;
  std::int64_t local_balance_ = 0;
  std::int64_t last_change_ = 0;
  std::uint64_t observed_at_us_ = 0;
  std::int64_t expected_adjust_ = 0;
  // M5-23
  bool server_known_ = false;
  std::int64_t server_balance_ = 0;
  std::uint64_t server_version_ = 0;
  std::uint64_t ack_seq_ = 0;
  std::uint64_t seq_ = 0;
  std::int64_t pending_sum_ = 0;
  std::vector<PendingDelta> pending_;
  WalletStats stats_;
};

[[nodiscard]] WalletHub& wallet_hub();

}  // namespace x4mp::features::wallet
