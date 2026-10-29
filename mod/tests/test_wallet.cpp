// M5 W6: the wallet read (M5-22; docs/m5-plan.md W6, docs/mod-design.md 12.2).
//
// The game function table has no money getter (x4_game_func_list.inc:40 has AddPlayerMoney only), so the balance arrives from Lua as
// x4mp.money {"v":1,"value":N}. What a test can pin without a game is that body's parsing and the observation rules the 12.2
// reconciliation is built on: a first reading is a baseline, an unchanged reading is quiet, a change we asked for is an echo and not
// income, and a change we did not ask for is the player's. The Lua half is tests/lua/test_money.lua; the in-game numbers are session 8.
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <limits>
#include <span>
#include <string>
#include <vector>

#include "features/wallet/wallet_hub.h"
#include "features/wallet/wallet_wire.h"

namespace W = x4mp::features::wallet;
namespace P = X4MP::Proto;

namespace {

// One WalletUpdate as the server builds it: a list of balances plus the ack of this node's deltas. Real generated writers, so the
// reader is tested against the actual wire format rather than a hand-rolled blob.
std::vector<std::uint8_t> make_update(std::int64_t balance, std::uint64_t version, std::uint64_t ack_seq,
                                      P::WalletKind kind = P::WalletKind::Player, std::uint16_t owner = 7,
                                      P::EffectiveCreditMode mode = P::EffectiveCreditMode::PerPlayer) {
  flatbuffers::FlatBufferBuilder fbb;
  const P::WalletRef ref(kind, owner);
  const std::vector<flatbuffers::Offset<P::WalletBalance>> rows{P::CreateWalletBalance(fbb, &ref, balance, version)};
  const auto update = P::CreateWalletUpdate(fbb, fbb.CreateVector(rows), P::LedgerReason::GameIncome, nullptr, mode, ack_seq);
  fbb.Finish(update);  // WalletUpdate is not a root_type in economy.fbs, so there is no generated FinishWalletUpdateBuffer
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

constexpr std::uint16_t kPlayer = 7;
constexpr std::uint16_t kTeam = 3;

}  // namespace

TEST_CASE("M5-22: a money body is a JSON object with an integral value", "[wallet][m522]") {
  // The bridge prints an integral number as an integer literal, so this is the shape that actually arrives.
  CHECK(W::parse_money_value(R"({"v":1,"value":1234})") == 1234);
  CHECK(W::parse_money_value(R"({"value":-50,"v":1})") == -50);  // key order is not part of the contract
  CHECK(W::parse_money_value(R"({"v":1,"value":0})") == 0);      // a real zero is a balance, not a missing one
  CHECK(W::parse_money_value(R"({"v":1,"value":7,"x":"y"})") == 7);  // extra fields are ignored
  CHECK(W::parse_money_value(R"({"v":1,"value":9223372036854775807})") == std::numeric_limits<std::int64_t>::max());
  CHECK(W::parse_money_value(R"({"v":1,"value":18446744073709551615})").has_value() == false);  // outside int64 is not a balance

  // Everything that is not a reading is refused, never turned into 0.
  CHECK_FALSE(W::parse_money_value("").has_value());
  CHECK_FALSE(W::parse_money_value("nope").has_value());
  CHECK_FALSE(W::parse_money_value("[1,2]").has_value());
  CHECK_FALSE(W::parse_money_value("12").has_value());
  CHECK_FALSE(W::parse_money_value(R"({"v":1})").has_value());
  CHECK_FALSE(W::parse_money_value(R"({"v":1,"value":1.5})").has_value());
  CHECK_FALSE(W::parse_money_value(R"({"v":1,"value":"12"})").has_value());
  CHECK_FALSE(W::parse_money_value(R"({"v":1,"value":null})").has_value());
  CHECK_FALSE(W::parse_money_value(std::string(300, '7')).has_value());  // not one of ours: too big to be a money body
}

TEST_CASE("M5-22: the first reading is a baseline, our own write is an echo, the rest is the player's", "[wallet][m522]") {
  W::WalletHub hub;
  CHECK_FALSE(hub.has_balance());
  CHECK(hub.local_balance() == 0);

  CHECK(hub.observe(1000, 10) == W::Observation::Baseline);  // nothing to compare with yet
  CHECK(hub.has_balance());
  CHECK(hub.local_balance() == 1000);
  CHECK(hub.observed_at_us() == 10);
  CHECK(hub.last_change() == 0);

  CHECK(hub.observe(1000, 20) == W::Observation::Quiet);
  CHECK(hub.observe(1500, 30) == W::Observation::Change);
  CHECK(hub.last_change() == 500);
  CHECK(hub.local_balance() == 1500);

  // A write of ours: the game applies it and the next poll sees it come back. That is not income and must not be booked as any.
  hub.note_apply(200);
  CHECK(hub.expected_adjust() == 200);
  CHECK(hub.observe(1700, 40) == W::Observation::Echo);
  CHECK(hub.expected_adjust() == 0);
  CHECK(hub.last_change() == 200);

  // Two writes before one poll are recognised as their sum.
  hub.note_apply(-300);
  hub.note_apply(-100);
  CHECK(hub.expected_adjust() == -400);
  CHECK(hub.observe(1300, 50) == W::Observation::Echo);
  CHECK(hub.expected_adjust() == 0);

  // A real change is never eaten by an outstanding expectation, and the expectation survives for its own echo.
  hub.note_apply(75);
  CHECK(hub.observe(1800, 60) == W::Observation::Change);  // the player's own +500
  CHECK(hub.expected_adjust() == 75);
  CHECK(hub.observe(1875, 70) == W::Observation::Echo);

  // A zero write is counted but expects nothing.
  hub.note_apply(0);
  CHECK(hub.expected_adjust() == 0);
  CHECK(hub.observe(1875, 80) == W::Observation::Quiet);

  // A game that allows debt reports a negative balance and it is a change like any other (design 12.3).
  CHECK(hub.observe(-250, 90) == W::Observation::Change);
  CHECK(hub.local_balance() == -250);
  CHECK(hub.last_change() == -2125);

  const W::WalletStats& s = hub.stats();
  CHECK(s.polls == 9);
  CHECK(s.baseline == 1);
  CHECK(s.quiet == 2);
  CHECK(s.changes == 3);
  CHECK(s.echoes == 3);
  CHECK(s.applies == 5);
  CHECK(s.bad == 0);

  // A bad body is counted by the feature, and a new universe drops everything.
  hub.note_bad();
  CHECK(hub.stats().bad == 1);
  hub.reset();
  CHECK_FALSE(hub.has_balance());
  CHECK(hub.local_balance() == 0);
  CHECK(hub.expected_adjust() == 0);
  CHECK(hub.stats().polls == 0);
  CHECK(hub.observe(5, 1) == W::Observation::Baseline);  // a fresh baseline, even at 5
}

TEST_CASE("M5-22: a zero balance at start is a baseline, not a quiet frame", "[wallet][m522]") {
  W::WalletHub hub;
  CHECK(hub.observe(0, 1) == W::Observation::Baseline);
  CHECK(hub.has_balance());
  CHECK(hub.observe(0, 2) == W::Observation::Quiet);
  CHECK(hub.stats().baseline == 1);
}

TEST_CASE("M5-23: a WalletUpdate names this node's wallet, and junk is refused", "[wallet][m523]") {
  const auto bytes = make_update(1500, 42, 3);
  W::WalletUpdateView view;
  REQUIRE(W::decode_wallet_update(bytes, view));
  CHECK(view.ack_seq == 3);
  CHECK(view.effective_mode == static_cast<std::uint8_t>(P::EffectiveCreditMode::PerPlayer));
  CHECK(view.reason == static_cast<std::uint8_t>(P::LedgerReason::GameIncome));
  REQUIRE(view.balances.size() == 1);
  CHECK(view.balances[0].kind == static_cast<std::uint8_t>(P::WalletKind::Player));
  CHECK(view.balances[0].owner == kPlayer);
  CHECK(view.balances[0].balance == 1500);
  CHECK(view.balances[0].version == 42);

  // Bytes that are not a WalletUpdate: short, and long enough but not a verified buffer.
  CHECK_FALSE(W::decode_wallet_update(std::span<const std::uint8_t>(), view));
  const std::vector<std::uint8_t> junk(64, 0xFF);
  CHECK_FALSE(W::decode_wallet_update(junk, view));
  CHECK(view.balances.empty());  // a refused body leaves nothing behind

  // The hub takes the row of OUR wallet only: another player's balance is not ours to converge to.
  W::WalletHub hub;
  CHECK_FALSE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::WalletUpdate), make_update(1500, 42, 3), 9, kTeam));
  CHECK(hub.stats().no_wallet == 1);
  CHECK_FALSE(hub.server_known());

  // A client with no player id yet (not in a session) has no wallet to converge.
  CHECK_FALSE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::WalletUpdate), make_update(1500, 42, 3), 0, 0));
  CHECK(hub.stats().no_wallet == 2);

  // Anything that is not a WalletUpdate at all is counted, not applied.
  CHECK_FALSE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::EntityCargo), make_update(1500, 42, 3), kPlayer, kTeam));
  CHECK_FALSE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::WalletUpdate), junk, kPlayer, kTeam));
  CHECK(hub.stats().bad_update == 1);
}

TEST_CASE("M5-23: the node converges to serverBalance + its own unbooked deltas", "[wallet][m523]") {
  W::WalletHub hub;
  const auto type = static_cast<std::uint16_t>(P::MsgType::WalletUpdate);

  // No WalletUpdate yet: nothing to converge to, even with a reading in hand.
  CHECK(hub.observe(1000, 1) == W::Observation::Baseline);
  CHECK_FALSE(hub.take_convergence().has_value());

  // The ledger says 1200. The local money is 1000, so the node asks the game for 200.
  REQUIRE(hub.on_frame_message(type, make_update(1200, 5, 0), kPlayer, kTeam));
  REQUIRE(hub.take_convergence().has_value());
  CHECK(*hub.take_convergence() == 200);
  CHECK(hub.target_balance() == 1200);
  CHECK(hub.effective_local() == 1000);

  // The write is made and the game takes it: the same 200 must not be asked for again before the poll reads it back.
  hub.note_write(200);
  CHECK(hub.stats().writes == 1);
  CHECK(hub.effective_local() == 1200);
  CHECK_FALSE(hub.take_convergence().has_value());

  // The next poll shows exactly that change: it is our echo, never the player's income (M5-22's rule, now with a ledger behind it).
  CHECK(hub.observe(1200, 2) == W::Observation::Echo);
  CHECK(hub.stats().changes == 0);
  CHECK(hub.stats().echoes == 1);

  // A forced local edit (a game refund, a debug AddPlayerMoney) is corrected on the next update: the AC's "within 30 s".
  CHECK(hub.observe(5000, 3) == W::Observation::Change);
  REQUIRE(hub.on_frame_message(type, make_update(1200, 6, 0), kPlayer, kTeam));
  REQUIRE(hub.take_convergence().has_value());
  CHECK(*hub.take_convergence() == -3800);

  // An update older than the one already seen must not undo it (economy.fbs:23).
  CHECK_FALSE(hub.on_frame_message(type, make_update(9999, 6, 0), kPlayer, kTeam));
  CHECK_FALSE(hub.on_frame_message(type, make_update(9999, 5, 0), kPlayer, kTeam));
  CHECK(hub.stats().stale == 2);
  CHECK(hub.server_balance() == 1200);
  CHECK(hub.server_version() == 6);
}

TEST_CASE("M5-23: a delta the ledger books leaves pending, so it is never counted twice", "[wallet][m523]") {
  W::WalletHub hub;
  const auto type = static_cast<std::uint16_t>(P::MsgType::WalletUpdate);
  hub.observe(1000, 1);

  // Two of this node's own deltas, each with its own seq (M5-24 sends these upward).
  CHECK(hub.book_pending(300, static_cast<std::uint8_t>(P::LedgerReason::GameIncome)) == 1);
  CHECK(hub.book_pending(-50, static_cast<std::uint8_t>(P::LedgerReason::GameSpend)) == 2);
  CHECK(hub.pending_count() == 2);
  CHECK(hub.pending_sum() == 250);
  CHECK(hub.book_pending(0, 0) == 3);  // a zero delta still consumes a seq, so an ack can never point at a gap
  CHECK(hub.pending_count() == 2);

  // The ledger has booked neither: the target carries both, and the local money already has them (they happened in the game).
  REQUIRE(hub.on_frame_message(type, make_update(1000, 7, 0), kPlayer, kTeam));
  CHECK(hub.target_balance() == 1250);
  CHECK(*hub.take_convergence() == 250);
  hub.note_write(250);  // the game took the adjust; local is 1250 as far as this node knows

  // Once acked, they are inside the balance: dropping them keeps the target where it was instead of doubling them.
  REQUIRE(hub.on_frame_message(type, make_update(1250, 8, 2), kPlayer, kTeam));
  CHECK(hub.pending_count() == 0);
  CHECK(hub.pending_sum() == 0);
  CHECK(hub.stats().pending_acked == 2);
  CHECK(hub.target_balance() == 1250);
  CHECK(hub.ack_seq() == 2);
  CHECK_FALSE(hub.take_convergence().has_value());  // 1250 local + 250 written is already the target

  // Pending is bounded: an unanswering server must not grow it without end.
  W::WalletHub capped;
  for (int i = 0; i < static_cast<int>(W::kMaxPending) + 5; ++i) capped.book_pending(1, 0);
  CHECK(capped.pending_count() == W::kMaxPending);
  CHECK(capped.stats().pending_dropped == 5);
  CHECK(capped.pending_sum() == static_cast<std::int64_t>(W::kMaxPending));
}

TEST_CASE("M5-23: in Shared mode the node converges to the team pool, not its own wallet", "[wallet][m523]") {
  W::WalletHub hub;
  const auto type = static_cast<std::uint16_t>(P::MsgType::WalletUpdate);
  hub.observe(100, 1);

  // mod-design 12.1: the spendable balance is pool:<team> in Shared mode, and TeamShared is not one of the two spendable accounts.
  const auto shared = make_update(900, 3, 0, P::WalletKind::TeamPool, kTeam, P::EffectiveCreditMode::Shared);
  REQUIRE(hub.on_frame_message(type, shared, kPlayer, kTeam));
  CHECK(hub.server_balance() == 900);
  CHECK(*hub.take_convergence() == 800);

  // The same update for a node on another team names no wallet of ours.
  W::WalletHub other;
  CHECK_FALSE(other.on_frame_message(type, shared, kPlayer, 4));
  CHECK(other.stats().no_wallet == 1);
  // A TeamShared row is not the pool either.
  CHECK_FALSE(other.on_frame_message(type, make_update(900, 3, 0, P::WalletKind::TeamShared, 4, P::EffectiveCreditMode::Shared), kPlayer, 4));

  // The mode comes from the update itself (the server resolves Auto -> PerPlayer|Shared), so the same node picks the pool row
  // when the server says Shared and its own wallet row when it says PerPlayer.
  W::WalletHub same_node;
  CHECK(same_node.on_frame_message(type, shared, kPlayer, kTeam));
  CHECK(same_node.server_balance() == 900);
  CHECK(same_node.on_frame_message(type, make_update(50, 4, 0, P::WalletKind::Player, kPlayer), kPlayer, kTeam));
  CHECK(same_node.server_balance() == 50);
}

TEST_CASE("M5-24: a CreditDelta is this node's change, with the key and seq it was booked with", "[wallet][m524]") {
  W::CreditDeltaData data;
  data.key_lo = 41;
  data.key_hi = kPlayer;
  data.amount = -1250;
  data.source = static_cast<std::uint8_t>(P::CreditSource::Build);
  data.ref_event_seq = 9;
  data.game_time = 1234.5;
  data.seq = 3;

  const auto bytes = W::encode_credit_delta(data);
  REQUIRE(bytes.size() > 8);
  const P::CreditDelta* delta = flatbuffers::GetRoot<P::CreditDelta>(bytes.data());
  REQUIRE(delta != nullptr);
  REQUIRE(delta->request_key() != nullptr);
  CHECK(delta->request_key()->lo() == 41);
  CHECK(delta->request_key()->hi() == kPlayer);
  CHECK(delta->amount() == -1250);
  CHECK(delta->source() == P::CreditSource::Build);
  CHECK(delta->ref_event_seq() == 9);
  CHECK(delta->game_time() == 1234.5);
  CHECK(delta->seq() == 3);
  // economy.fbs:47-48: player_id/team_id are A->S only, a client leaves them 0 and the server stamps the sender.
  CHECK(delta->player_id() == 0);
  CHECK(delta->team_id() == 0);
}

TEST_CASE("M5-24: only a money event names the cause, and it pays for one change", "[wallet][m524]") {
  W::WalletHub hub;
  constexpr std::uint64_t frame = 100;

  // No event at all: the change is booked as CreditSource::Other and names no event.
  const auto bare = hub.take_reason(frame);
  CHECK_FALSE(bare.named());
  CHECK(bare.source == static_cast<std::uint8_t>(P::CreditSource::Other));

  // A trade in this frame is the cause of the change booked right after it.
  hub.note_event(x4mp::features::events::EventKind::Trade, frame);
  CHECK(hub.events_seen() == 1);
  const auto trade = hub.take_reason(frame);
  CHECK(trade.named());
  CHECK(trade.source == static_cast<std::uint8_t>(P::CreditSource::Trade));
  CHECK(trade.ref_event_seq == 1);
  // Consumed: one event never pays for two changes.
  CHECK_FALSE(hub.take_reason(frame).named());

  // A build is a cause too, and one frame of slack covers the order the two features run in.
  hub.note_event(x4mp::features::events::EventKind::Build, frame);
  const auto build = hub.take_reason(frame + W::kReasonFrames);
  CHECK(build.source == static_cast<std::uint8_t>(P::CreditSource::Build));

  // Too old: the event is a different change, not this one.
  hub.note_event(x4mp::features::events::EventKind::Trade, frame);
  CHECK_FALSE(hub.take_reason(frame + W::kReasonFrames + 1).named());

  // Kinds that do not move money still count as events (ref_event_seq is a count, not a reason), and never name a cause.
  hub.note_event(x4mp::features::events::EventKind::Kill, frame);
  hub.note_event(x4mp::features::events::EventKind::Money, frame);
  CHECK(hub.events_seen() == 5);
  CHECK_FALSE(hub.take_reason(frame).named());

  // The counter the delta names is the event's own number, so a late reader can find it in the ring.
  hub.note_event(x4mp::features::events::EventKind::Trade, frame);
  CHECK(hub.take_reason(frame).ref_event_seq == 6);
}

TEST_CASE("M5-24: a booked change goes up once, and again only after the link comes back", "[wallet][m524]") {
  W::WalletHub hub;
  constexpr std::uint16_t player = 11;
  hub.observe(1000, 1);
  hub.note_server_balance(1000, 1, 0);

  // The player's own trade: +500, caused by event 1.
  hub.note_event(x4mp::features::events::EventKind::Trade, 5);
  const auto first = hub.book_upward(500, hub.take_reason(5), player);
  REQUIRE(first.has_value());
  CHECK(first->seq == 1);
  CHECK(first->delta == 500);
  CHECK(first->key_lo == 1);
  CHECK(first->key_hi == player);
  CHECK(first->source == static_cast<std::uint8_t>(P::CreditSource::Trade));
  CHECK(first->ref_event_seq == 1);
  CHECK(hub.pending_count() == 1);
  CHECK(hub.pending_sum() == 500);
  CHECK(hub.target_balance() == 1500);
  CHECK(hub.stats().booked == 1);
  CHECK(hub.stats().reasons == 1);

  // A change with no event is booked as Other, and a zero delta books nothing but still consumes a seq (an ack must never point
  // at a gap).
  CHECK_FALSE(hub.book_upward(0, hub.take_reason(5), player).has_value());
  CHECK(hub.last_seq() == 2);
  CHECK(hub.pending_count() == 1);

  // A spend: -200, no cause.
  const auto second = hub.book_upward(-200, hub.take_reason(6), player);
  REQUIRE(second.has_value());
  CHECK(second->seq == 3);
  CHECK(second->key_lo == 2);
  CHECK(second->source == static_cast<std::uint8_t>(P::CreditSource::Other));
  CHECK(second->ref_event_seq == 0);
  CHECK(hub.pending_sum() == 300);

  // Both rows are offered, oldest first, and each is offered only until the link takes it.
  auto unsent = hub.take_unsent(8);
  REQUIRE(unsent.size() == 2);
  CHECK(unsent[0].seq == 1);
  CHECK(unsent[1].seq == 3);
  hub.mark_sent(1);
  hub.mark_sent(1);  // idempotent: a second take of the same send is not a second send
  CHECK(hub.stats().sent == 1);
  CHECK(hub.pending_unsent() == 1);
  unsent = hub.take_unsent(8);
  REQUIRE(unsent.size() == 1);
  CHECK(unsent[0].seq == 3);
  // The cap is respected: a sender that can only afford one per frame gets one.
  CHECK(hub.take_unsent(0).empty());

  // The ledger booked seq 1: that delta is inside the balance now, so it leaves pending and is never sent again.
  CHECK(hub.note_server_balance(1500, 2, 1));
  CHECK(hub.pending_count() == 1);
  CHECK(hub.pending_sum() == -200);
  CHECK(hub.ack_seq() == 1);
  CHECK(hub.stats().pending_acked == 1);
  unsent = hub.take_unsent(8);
  REQUIRE(unsent.size() == 1);
  CHECK(unsent[0].seq == 3);

  // The link dropped: every sent row is armed again with the very same key and seq, which is what makes the server book it once.
  hub.mark_sent(3);
  CHECK(hub.pending_unsent() == 0);
  hub.resend_all();
  CHECK(hub.pending_unsent() == 1);
  CHECK(hub.stats().resent == 1);
  unsent = hub.take_unsent(8);
  REQUIRE(unsent.size() == 1);
  CHECK(unsent[0].seq == 3);
  CHECK(unsent[0].key_lo == 2);
  CHECK(unsent[0].key_hi == player);

  // A resend that arrives before the ack changes no arithmetic: the delta is still counted exactly once.
  CHECK(hub.pending_sum() == -200);
  CHECK(hub.target_balance() == 1300);
}

TEST_CASE("M5-24: pending cannot grow without a ledger, and a stale update changes nothing", "[wallet][m524]") {
  W::WalletHub hub;
  hub.observe(0, 1);
  hub.note_server_balance(0, 10, 0);
  for (std::size_t i = 0; i < W::kMaxPending; ++i) {
    REQUIRE(hub.book_upward(1, W::ReasonHint{}, kPlayer).has_value());
  }
  CHECK(hub.pending_count() == W::kMaxPending);
  CHECK(hub.pending_sum() == static_cast<std::int64_t>(W::kMaxPending));
  CHECK(hub.last_seq() == W::kMaxPending);

  // One more delta: the oldest row goes, and that is counted - a node that never hears an ack must not grow.
  const auto extra = hub.book_upward(1, W::ReasonHint{}, kPlayer);
  REQUIRE(extra.has_value());
  CHECK(hub.pending_count() == W::kMaxPending);
  CHECK(hub.pending_sum() == static_cast<std::int64_t>(W::kMaxPending));
  CHECK(hub.stats().pending_dropped == 1);
  CHECK(hub.take_unsent(1).front().seq == 2);  // seq 1 was the row that went

  // An update older than the one already seen is dropped, not applied: it would undo the balance the ledger just named.
  CHECK_FALSE(hub.note_server_balance(999, 9, 0));
  CHECK(hub.stats().stale == 1);
  CHECK(hub.server_balance() == 0);
  CHECK(hub.server_version() == 10);
}
