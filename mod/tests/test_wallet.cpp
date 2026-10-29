// M5 W6: the wallet read (M5-22; docs/m5-plan.md W6, docs/mod-design.md 12.2).
//
// The game function table has no money getter (x4_game_func_list.inc:40 has AddPlayerMoney only), so the balance arrives from Lua as
// x4mp.money {"v":1,"value":N}. What a test can pin without a game is that body's parsing and the observation rules the 12.2
// reconciliation is built on: a first reading is a baseline, an unchanged reading is quiet, a change we asked for is an echo and not
// income, and a change we did not ask for is the player's. The Lua half is tests/lua/test_money.lua; the in-game numbers are session 8.
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <limits>
#include <string>

#include "features/wallet/wallet_hub.h"

namespace W = x4mp::features::wallet;

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
