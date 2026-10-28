// M5 W4: trade detection, the station stock apply and the actions shim queue (docs/m5-plan.md W4, M5-13..M5-17).
//
// What is pinned here is the half a test can pin without a game: the cargo delta a client computes from a snapshot (so a repeated or
// late snapshot writes nothing), the trade reporter (one Intent per trade, the (station, ware, second) dedupe, and the four ways a
// trade is refused), the EntityCargo inbox, and the shim queue's folding and bounds. The MD half is checked by
// tools/validate-x4-xml.py and tests/lua/test_actions.lua; the in-game numbers are session 8.
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "features/economy/economy_hub.h"
#include "features/economy/stock_apply.h"
#include "features/economy/trade_report.h"
#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "game/game_api.h"

using namespace x4mp;
using namespace x4mp::features;
using namespace x4mp::features::economy;

namespace {

events::MdEvent trade_event(std::uint64_t station_component, std::string ware, std::int64_t amount, std::int64_t price,
                            std::int64_t at_ms = 5'000) {
  events::MdEvent event;
  event.kind = events::EventKind::Trade;
  event.component = station_component;
  event.macro = std::move(ware);
  event.amount = amount;
  event.price = price;
  event.at_ms = at_ms;
  return event;
}

struct Sent {
  std::uint16_t type = 0;
  std::vector<std::uint8_t> bytes;
};

TradeReportDeps deps_with(std::vector<Sent>& out, std::uint32_t station = 0x1234, std::uint32_t ship = 0x5678) {
  TradeReportDeps deps;
  deps.station_net_id = [station](std::uint64_t) { return station; };
  deps.self_ship_net_id = [ship] { return ship; };
  deps.self_player_id = [] { return std::uint16_t{3}; };
  deps.send_control = [&out](std::uint16_t type, std::span<const std::uint8_t> payload) {
    out.push_back(Sent{type, std::vector<std::uint8_t>(payload.begin(), payload.end())});
    return true;
  };
  deps.now_ms = nullptr;  // use the event's own timestamp, so the dedupe second is the trade's second
  return deps;
}

}  // namespace

TEST_CASE("M5-16: a client applies only the difference to its own copy of a station", "[economy][m516]") {
  const std::uint64_t component = 0x1b1d;
  const std::vector<game::CargoPod> local{{"energycells", 500}, {"hullparts", 10}};

  // In step: nothing to write, which is what makes a repeated snapshot harmless.
  const std::vector<NamedLine> same{{"energycells", 500}, {"hullparts", 10}};
  CHECK(cargo_delta(component, local, same).empty());

  // The authority sold 20 cells and dropped the hull parts: two ops, both signed as the shim wants them.
  const std::vector<NamedLine> after{{"energycells", 480}};
  const std::vector<CargoOp> ops = cargo_delta(component, local, after);
  REQUIRE(ops.size() == 2);
  CHECK(ops[0].component == component);
  CHECK(ops[0].ware == "energycells");
  CHECK(ops[0].amount == -20);
  CHECK(ops[1].ware == "hullparts");
  CHECK(ops[1].amount == -10);

  // A ware the local hold does not have yet is added, and a remote-only snapshot of an empty station empties the local hold.
  const std::vector<NamedLine> bought{{"energycells", 500}, {"hullparts", 10}, {"smartchips", 7}};
  const std::vector<CargoOp> added = cargo_delta(component, local, bought);
  REQUIRE(added.size() == 1);
  CHECK(added[0].ware == "smartchips");
  CHECK(added[0].amount == 7);
  const std::vector<CargoOp> emptied = cargo_delta(component, local, std::span<const NamedLine>());
  REQUIRE(emptied.size() == 2);
  CHECK(emptied[0].amount == -500);
  CHECK(emptied[1].amount == -10);

  // No component, no ops: a snapshot that did not resolve to a local station is never applied to a guess.
  CHECK(cargo_delta(0, local, after).empty());
}

TEST_CASE("M5-14: one trade becomes one Intent with the station, the ship and the ware macro", "[economy][m514]") {
  std::vector<Sent> sent;
  TradeReporter reporter(deps_with(sent));
  REQUIRE(reporter.on_event(trade_event(0x1b1d, "energycells", -500, 22)));

  REQUIRE(sent.size() == 1);
  CHECK(sent[0].type == static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent));
  const X4MP::Proto::TradeReport* body = events::trade_report_of(sent[0].bytes);
  REQUIRE(body != nullptr);
  CHECK(body->station() == 0x1234);
  CHECK(body->ship() == 0x5678);
  CHECK(body->amount() == -500);
  CHECK(body->unit_price() == 22);
  REQUIRE(body->ware() != nullptr);
  CHECK(body->ware()->str() == "energycells");
  CHECK(body->ware_ref() == ware_ref_of("energycells"));
  CHECK(reporter.stats().sent == 1);

  // The hook and the cargo diff can see the same trade: the same (station, ware, second) is dropped, a later second is not.
  REQUIRE(reporter.on_event(trade_event(0x1b1d, "energycells", -500, 22)));
  CHECK(sent.size() == 1);
  CHECK(reporter.stats().deduped == 1);
  REQUIRE(reporter.on_event(trade_event(0x1b1d, "energycells", -500, 22, 6'100)));
  CHECK(sent.size() == 2);
}

TEST_CASE("M5-13: a trade that cannot be named is counted, never sent", "[economy][m513]") {
  std::vector<Sent> sent;
  TradeReporter unknown_station(deps_with(sent, 0, 0x5678));
  CHECK(unknown_station.on_event(trade_event(0x1b1d, "energycells", -5, 1)));
  CHECK(unknown_station.stats().unmapped_station == 1);

  TradeReporter no_ship(deps_with(sent, 0x1234, 0));
  CHECK(no_ship.on_event(trade_event(0x1b1d, "energycells", -5, 1)));
  CHECK(no_ship.stats().unmapped_ship == 1);

  TradeReporter zero(deps_with(sent));
  CHECK(zero.on_event(trade_event(0x1b1d, "energycells", 0, 1)));
  CHECK(zero.stats().bad_amount == 1);

  TradeReportDeps failing = deps_with(sent);
  failing.send_control = [](std::uint16_t, std::span<const std::uint8_t>) { return false; };
  TradeReporter refused(std::move(failing));
  CHECK(refused.on_event(trade_event(0x1b1d, "energycells", -5, 1)));
  CHECK(refused.stats().send_failed == 1);

  CHECK(sent.empty());
  // A non-trade event is not this reporter's business.
  events::MdEvent kill;
  kill.kind = events::EventKind::Kill;
  CHECK_FALSE(zero.on_event(kill));
}

TEST_CASE("M5-16: the EntityCargo inbox decodes a station snapshot and refuses a foreign frame", "[economy][m516]") {
  EconomyHub& hub = economy_hub();
  hub.reset();

  const std::vector<events::CargoLine> lines{{0x11, 500}, {0x22, 12}};
  hub.on_frame_message(static_cast<std::uint16_t>(X4MP::Proto::MsgType::EntityCargo),
                       events::encode_entity_cargo(0x1234, lines));
  const std::vector<events::CargoView> taken = hub.take_cargo();
  REQUIRE(taken.size() == 1);
  CHECK(taken[0].net_id == 0x1234);
  REQUIRE(taken[0].wares.size() == 2);
  CHECK(taken[0].wares[0].ware_ref == 0x11);
  CHECK(taken[0].wares[1].amount == 12);
  CHECK(hub.take_cargo().empty());

  // Another message type, and a buffer that is not a cargo frame, are both counted and dropped.
  hub.on_frame_message(static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent), events::encode_entity_cargo(0x1234, lines));
  hub.on_frame_message(static_cast<std::uint16_t>(X4MP::Proto::MsgType::EntityCargo), std::vector<std::uint8_t>{1, 2, 3});
  CHECK(hub.take_cargo().empty());
  CHECK(hub.stats().cargo_bad == 1);
  hub.reset();
}

TEST_CASE("M5-15: the shim queue folds one ware and keeps its bounds", "[economy][m515]") {
  EconomyHub& hub = economy_hub();
  hub.reset();

  hub.queue(CargoOp{0x1b1d, "energycells", -5});
  hub.queue(CargoOp{0x1b1d, "energycells", -7});
  hub.queue(CargoOp{0x1b1d, "hullparts", 3});
  hub.queue(CargoOp{0x1b20, "energycells", 1});
  CHECK(hub.queued() == 3);

  // The per-frame budget takes at most `max`, and a frame that leaves work behind is counted (M5-15's burst counter).
  const std::vector<CargoOp> first = hub.take_cargo_ops(2);
  REQUIRE(first.size() == 2);
  CHECK(first[0].ware == "energycells");
  CHECK(first[0].amount == -12);
  CHECK(first[1].ware == "hullparts");
  CHECK(hub.stats().cargo_burst == 1);
  const std::vector<CargoOp> rest = hub.take_cargo_ops(8);
  REQUIRE(rest.size() == 1);
  CHECK(rest[0].component == 0x1b20);

  // Money lines are only taken when they can actually be addressed.
  hub.queue(MoneyOp{"argon", "x4mp_team_1", 2500});
  hub.queue(MoneyOp{"argon", "argon", 10});
  hub.queue(MoneyOp{"", "x4mp_team_1", 10});
  hub.queue(MoneyOp{"argon", "x4mp_team_1", 0});
  const std::vector<MoneyOp> money = hub.take_money_ops(8);
  REQUIRE(money.size() == 1);
  CHECK(money[0].amount == 2500);
  hub.reset();
}

TEST_CASE("M5-14: a ware ref is stable and differs per ware", "[economy][m514]") {
  CHECK(ware_ref_of("energycells") == ware_ref_of("energycells"));
  CHECK(ware_ref_of("energycells") != ware_ref_of("hullparts"));
  CHECK(ware_ref_of("") == 2166136261u);  // the FNV-1a offset basis: an empty name is never a real ware
  CHECK(ware_ref_of("energycells") != 0);
}
