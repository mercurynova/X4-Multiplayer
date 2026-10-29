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
