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
//
// M5-24 closes the loop upward: a change this node did not cause is a delta the ledger has not booked yet, so it is booked
// (book_upward: a per-node seq and a request key, both fixed once) and sent as CreditDelta. The reason travels with it - the
// events feature reports every MD event here, and a trade or a finished build seen in the same frame window names the cause; a
// change with no such event is CreditSource::Other. Nothing is lost on a reconnect: the pending rows stay until a WalletUpdate
// acks their seq, and resend_all() hands every unsent row back to the sender with the very same key and seq, which is what makes
// the server book it once.

#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

#include "economy_generated.h"
#include "features/events/event_types.h"

namespace x4mp::features::wallet {

namespace P = X4MP::Proto;

// The WalletKind values a node can spend from (economy.fbs, mod-design 12.1: wallet:<player> in PerPlayer, pool:<team> in Shared).
// TeamShared is not one of the two spendable accounts the design names, so it is never picked.
inline constexpr std::uint8_t kKindPlayer = static_cast<std::uint8_t>(P::WalletKind::Player);
inline constexpr std::uint8_t kKindTeamPool = static_cast<std::uint8_t>(P::WalletKind::TeamPool);

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
  // M5-24
  std::uint64_t booked = 0;        // deltas this node booked upward (a change it did not cause)
  std::uint64_t sent = 0;          // CreditDeltas the link took
  std::uint64_t send_failed = 0;   // CreditDeltas the link refused (no session yet, a closed link)
  std::uint64_t resent = 0;        // pending rows armed again after the link came back
  std::uint64_t reasons = 0;       // changes that were booked with a named cause (a trade, a build)
  // M5-25
  std::uint64_t switched = 0;  // the spendable wallet changed: the session moved between PerPlayer and Shared
  std::uint64_t probes = 0;    // zero-amount CreditDeltas sent to make the ledger report what it has booked
  // M5-26
  std::uint64_t clamped = 0;  // the ledger went below zero and the local money was held at 0 (a team debt)
};

// M5-23: one of this node's own deltas that the ledger has not booked yet. `seq` is per node and strictly monotonic
// (economy.fbs:54) and comes back as WalletUpdate.acked_delta_seq, so a resend after a reconnect is booked once.
struct PendingDelta {
  std::uint64_t seq = 0;
  std::int64_t delta = 0;
  std::uint8_t reason = 0;  // LedgerReason as the wire carries it; history only, never part of the arithmetic (mod-design 12.2)
  // M5-24: what the CreditDelta carries. The key and the seq are fixed when the delta is booked, so a resend is the same request
  // and the server books it once.
  std::uint64_t key_lo = 0;
  std::uint64_t key_hi = 0;         // the sending player, so two nodes' keys cannot collide
  std::uint8_t source = 0;          // CreditSource: Trade, Build, ... Other when no event named a cause
  std::uint64_t ref_event_seq = 0;  // this node's running event count, 0 when the change came from no event at all
  bool sent = false;                // the link took a send of it; a reconnect clears this (resend_all)
};

// Far more than a node can accumulate while the server answers: past this the oldest is dropped and counted.
inline constexpr std::size_t kMaxPending = 256;

// M5-24: why the change happened, as the events of the same frame window said. `frame` is the host frame index, the one clock every
// feature shares, so "the same frame" needs no timestamp: a trade event and the poll that shows its money land in the same frame or
// the next one.
struct ReasonHint {
  std::uint8_t source = static_cast<std::uint8_t>(P::CreditSource::Other);
  std::uint64_t ref_event_seq = 0;
  std::uint64_t frame = 0;
  [[nodiscard]] bool named() const noexcept { return ref_event_seq != 0; }
};

// How many frames an event can be older than the change it paid for. The poll is per frame and the MD event reaches the bus in the
// same frame, so one frame of slack covers the order the two features run in; more than that and the event is a different change.
inline constexpr std::uint64_t kReasonFrames = 2;

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
  // Which wallet this node spends from (mod-design 12.1). Set by on_frame_message before every balance; when the pair changes the
  // session switched credit mode, and the ledger version of the new wallet is its own counter, so the baseline is dropped.
  void select_wallet(std::uint8_t kind, std::uint16_t owner);
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
  //
  // M5-26 (mod-design 12.3): with one pool and several spenders the ledger can go below zero, and X4 has never been shown to hold
  // a negative player balance. So the write stops at 0 - the node asks for the part that brings it to zero and no more - and the
  // shortfall stays the ledger's debt (debt()), which the HUD shows. Once at 0 the call returns nullopt: a balance the game cannot
  // reach is not retried every frame. When the pool recovers, the next update converges from 0 as usual.
  [[nodiscard]] std::optional<std::int64_t> take_convergence();
  // M5-26: what the ledger says the wallet is short of, while the local money is held at 0. 0 when the ledger is not below zero.
  [[nodiscard]] std::int64_t debt() const noexcept;
  // The game refused the write take_convergence() asked for (a missing export): count it, keep the target for the next frame.
  void note_write_failed() noexcept { ++stats_.write_failed; }
  // The game took the write: this is the same accounting observe() will later recognise as the echo.
  void note_write(std::int64_t delta);

  // ---- M5-24: the delta goes upward ----
  // One MD event, as the events feature saw it. Only the kinds that move money name a cause (a trade, a finished build); every
  // other kind still bumps the event counter, because ref_event_seq is a count of events, not of reasons.
  void note_event(events::EventKind kind, std::uint64_t frame);
  // The cause of the change about to be booked: the newest money event if it is fresh, else CreditSource::Other. Consumed, so one
  // event never pays for two changes.
  [[nodiscard]] ReasonHint take_reason(std::uint64_t frame);
  // Book a change this node did not cause and hand back the row to send. nullopt for a zero delta (the seq is still consumed) and
  // when the change is one of ours after all.
  std::optional<PendingDelta> book_upward(std::int64_t delta, ReasonHint reason, std::uint16_t player_id);
  // The rows whose send the link has not taken yet, oldest first, at most `max` of them.
  [[nodiscard]] std::vector<PendingDelta> take_unsent(std::size_t max) const;
  // The link took the send of `seq` (a row the ledger has already booked is simply gone).
  void mark_sent(std::uint64_t seq);
  // The link refused it: the row stays unsent and goes again next frame, with the same key and seq.
  void note_send_failed() noexcept { ++stats_.send_failed; }
  // The link came back (a reconnect, a resume): every pending row is armed again and resent with its original key and seq, which is
  // what makes the server book it exactly once (economy.fbs:53-54).
  void resend_all();
  // M5-25: ask the ledger what it holds without moving money. A zero-amount CreditDelta is booked as nothing but still answers with
  // acked_delta_seq (server EconomyService.cs:440-453), which drains pending rows whose ack was lost - the periodic refresh of
  // mod-design 12.3. It carries no balances (EconomyModule.SendTo sends the changed ones only), so a *balance* refresh comes from
  // the next real change or from a reconnect, where the server sends every visible balance on attach.
  std::optional<PendingDelta> book_probe(std::uint16_t player_id);

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
  [[nodiscard]] std::size_t pending_unsent() const noexcept;
  [[nodiscard]] std::uint64_t events_seen() const noexcept { return event_seq_; }
  // M5-25: the spendable wallet as the last WalletUpdate named it (WalletKind: Player = our own wallet, TeamPool = the team pool).
  [[nodiscard]] bool wallet_known() const noexcept { return wallet_known_; }
  [[nodiscard]] std::uint8_t wallet_kind() const noexcept { return wallet_kind_; }
  [[nodiscard]] std::uint16_t wallet_owner() const noexcept { return wallet_owner_; }
  // serverBalance + sum(pending): what this node's money should be (0 when no update arrived yet).
  [[nodiscard]] std::int64_t target_balance() const noexcept { return server_balance_ + pending_sum_; }
  // The local money as this node believes it is: the last reading plus the writes not yet read back.
  [[nodiscard]] std::int64_t effective_local() const noexcept { return local_balance_ + expected_adjust_; }

  void reset();

 private:
  // Capacity of pending_: past kMaxPending the oldest row goes, and that is counted (a node that never hears an ack must not grow).
  void make_room();

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
  // M5-24
  std::uint64_t event_seq_ = 0;  // MD events seen, the ref_event_seq a delta names
  std::uint64_t key_ = 0;        // request keys this node has issued (key_hi is the player, so two nodes never collide)
  ReasonHint hint_{};
  // M5-25: which wallet the ledger balance belongs to. A ledger version counts per wallet, so it is only comparable while the pair
  // stays the same; a mode switch drops the baseline and takes the new wallet's first version as it is.
  bool wallet_known_ = false;
  std::uint8_t wallet_kind_ = 0;
  std::uint16_t wallet_owner_ = 0;
  // M5-26: the ledger is below zero and the local money is being held at 0. Counted once per descent, not once per frame.
  bool clamped_ = false;
  WalletStats stats_;
};

[[nodiscard]] WalletHub& wallet_hub();

}  // namespace x4mp::features::wallet
