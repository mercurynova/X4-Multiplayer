// M5 W1: the event capture ring, the bus and the wire (docs/m5-plan.md W1, M5-01..M5-05).
//
// What is pinned here is the half that a test can pin without a game: the MD line format (so the MD half and the parser cannot
// drift), the dedupe window (so a hook and its fallback cannot double-report), the queue bounds (so a flood cannot grow the
// heap or the frame), the `md_hook` block (so a dead MD half is visible in the 30 s line), and the flatbuffer bodies the mod
// sends (so an encode the server rejects is caught here, not in session 8).
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "events_generated.h"
#include "features/events/event_bus.h"
#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "features/events/hook_state.h"
#include "features/events/kill_report.h"

using namespace x4mp::features::events;

namespace {

MdEvent event_of(EventKind kind, std::uint64_t component, std::int64_t at_ms) {
  MdEvent event;
  event.kind = kind;
  event.component = component;
  event.at_ms = at_ms;
  return event;
}

}  // namespace

TEST_CASE("M5-01: every MD line kind parses, and a bad line is refused rather than half-read", "[events][m501]") {
  MdEvent event;
  REQUIRE(parse_event_line("H;9", event));
  CHECK(event.kind == EventKind::HookHello);
  CHECK(event.amount == 9);

  REQUIRE(parse_event_line("K;0x1b1d;ship_spl_s_heavyfighter_01_a_macro;split;0x1b20;weapon_laser", event));
  CHECK(event.kind == EventKind::Kill);
  CHECK(event.component == 0x1b1d);
  CHECK(event.macro == "ship_spl_s_heavyfighter_01_a_macro");
  CHECK(event.owner == "split");
  CHECK(event.other == 0x1b20);

  REQUIRE(parse_event_line("X;18197", event));  // decimal, as MD prints it when the object is not a hex component
  CHECK(event.kind == EventKind::Destroyed);
  CHECK(event.component == 18197);

  REQUIRE(parse_event_line("P;", event));
  CHECK(event.kind == EventKind::PlayerDeath);

  REQUIRE(parse_event_line("T;0x1b1d;ware_hullparts;-500;120", event));
  CHECK(event.kind == EventKind::Trade);
  CHECK(event.component == 0x1b1d);
  CHECK(event.macro == "ware_hullparts");
  CHECK(event.amount == -500);
  CHECK(event.price == 120);

  REQUIRE(parse_event_line("B;0x1b1d;station_factory;cluster_409_sector001_macro", event));
  CHECK(event.kind == EventKind::Build);
  CHECK(event.macro == "station_factory");
  CHECK(event.sector == "cluster_409_sector001_macro");

  REQUIRE(parse_event_line("C;0x1b1d;argon;player", event));
  CHECK(event.kind == EventKind::Capture);
  CHECK(event.other == 0);  // "argon" is not an id: the field is left alone rather than failing the line

  REQUIRE(parse_event_line("M;1234567", event));
  CHECK(event.kind == EventKind::Money);
  CHECK(event.amount == 1234567);

  REQUIRE(parse_event_line("S;0x1b1d", event));
  CHECK(event.kind == EventKind::BoardingStart);
  REQUIRE(parse_event_line("E;0x1b1d", event));
  CHECK(event.kind == EventKind::BoardingEnd);

  // Trailing fields from a newer MD half are ignored; a missing required field, an unknown letter and a bad id are refused.
  CHECK(parse_event_line("K;0x1b1d;macro;owner;0x1b20;weapon;extra;more", event));
  CHECK_FALSE(parse_event_line("K;0x1b1d;macro;owner", event));
  CHECK_FALSE(parse_event_line("Z;1", event));
  CHECK_FALSE(parse_event_line("K;zzz;macro;owner;0", event));
  CHECK_FALSE(parse_event_line("", event));
  CHECK_FALSE(parse_event_line(";", event));
  CHECK_FALSE(parse_event_line("T;0x1;ware;five;120", event));
}

TEST_CASE("M5-01: parse_id takes what MD prints, and nothing else", "[events][m501]") {
  std::uint64_t value = 0;
  CHECK(parse_id("0x1b1d", value));
  CHECK(value == 0x1b1d);
  CHECK(parse_id("0X1B1D", value));
  CHECK(value == 0x1b1d);
  CHECK(parse_id("18197", value));
  CHECK(value == 18197);
  CHECK_FALSE(parse_id("", value));
  CHECK_FALSE(parse_id("0x", value));
  CHECK_FALSE(parse_id("12a4", value));
  CHECK_FALSE(parse_id("-5", value));
  CHECK_FALSE(parse_id("1234567890123456789012", value));
}

TEST_CASE("M5-02: a hook and its fallback report one event, and the window is one second", "[events][m502]") {
  EventBus bus;
  CHECK(bus.push(event_of(EventKind::Kill, 0x1b1d, 1000)));
  CHECK_FALSE(bus.push(event_of(EventKind::Kill, 0x1b1d, 1000)));  // the same kill seen twice
  CHECK(bus.stats().deduped == 1);
  CHECK(bus.push(event_of(EventKind::Kill, 0x1b1d, 2000)));  // a second later it is a different occurrence
  CHECK(bus.stats().parsed == 3);
  CHECK(bus.stats().by_kind[static_cast<std::size_t>(EventKind::Kill)] == 2);

  // Different subject, different amount and different kind are all different events, even inside one second.
  CHECK(bus.push(event_of(EventKind::Kill, 0x1b1e, 2000)));
  MdEvent trade = event_of(EventKind::Trade, 0x1b1d, 2000);
  trade.amount = -500;
  CHECK(bus.push(trade));
  trade.amount = -300;
  CHECK(bus.push(trade));
  CHECK(bus.push(event_of(EventKind::Destroyed, 0x1b1d, 2000)));
  CHECK(bus.stats().deduped == 1);
}

TEST_CASE("M5-02: the queue is bounded, drained by budget, and keeps a ring for inspection", "[events][m502]") {
  EventBus bus;
  // The cap accepts exactly kMaxQueued events and then refuses more: the overflow counter is the signal, and the ring holds
  // only what was accepted.
  for (std::uint64_t i = 0; i < EventBus::kMaxQueued; ++i) {
    CHECK(bus.push(event_of(EventKind::Trade, i + 1, static_cast<std::int64_t>(i))));
  }
  CHECK(bus.queued() == EventBus::kMaxQueued);
  CHECK(bus.stats().overflow == 0);
  for (std::uint64_t i = EventBus::kMaxQueued; i < EventBus::kMaxQueued + 8; ++i) {
    CHECK_FALSE(bus.push(event_of(EventKind::Trade, i + 1, static_cast<std::int64_t>(i))));
  }
  CHECK(bus.queued() == EventBus::kMaxQueued);
  CHECK(bus.stats().overflow == 8);

  const std::vector<MdEvent> first = bus.take(EventBus::kDefaultPerFrame);
  CHECK(first.size() == EventBus::kDefaultPerFrame);
  CHECK(bus.queued() == EventBus::kMaxQueued - EventBus::kDefaultPerFrame);
  CHECK(bus.stats().taken == EventBus::kDefaultPerFrame);
  CHECK(bus.take(0).empty());

  CHECK(bus.ring().size() == EventBus::kRingSlots);
  CHECK(bus.ring().back().component == EventBus::kMaxQueued);  // newest accepted, not the rejected overflow

  bus.reset();
  CHECK(bus.queued() == 0);
  CHECK(bus.ring().empty());
  CHECK(bus.stats().parsed == 0);
  CHECK(bus.push(event_of(EventKind::Kill, 0x1b1d, 1000)));  // the dedupe window is cleared too
}

TEST_CASE("M5-03: the md_hook block says dead until the MD half says hello", "[events][m503]") {
  HookState state;
  CHECK(format_hooks(state) == "hooks=dead md=0 fired=0 age=none fb=0 dup=0 queued=0 claims=0 unmapped=0");

  state.alive = true;
  state.hooks_armed = 9;
  state.fired = 41;
  state.fallbacks = 2;
  state.deduped = 1;
  state.queued = 3;
  state.last_age_ms = 1250;
  CHECK(format_hooks(state) == "hooks=alive md=9 fired=41 age=1.2s fb=2 dup=1 queued=3 claims=0 unmapped=0");

  // A dead MD half with a live fallback is the interesting case: fired=0 with fb>0 says the hooks are not running.
  HookState dead;
  dead.fallbacks = 7;
  CHECK(format_hooks(dead).find("fired=0 age=none fb=7") != std::string::npos);
}

TEST_CASE("M5-05: every intent body the mod sends survives a round trip", "[events][m505]") {
  IntentRequest request;
  request.key_lo = 0x1122334455667788ull;
  request.key_hi = 0x99aabbccddeeff00ull;
  request.request_id = 7;
  request.game_time = 1234.5;

  SECTION("KillClaim") {
    KillClaimData claim;
    claim.target = 42;
    claim.killer = 43;
    claim.weapon_macro_ref = 44;
    const std::vector<std::uint8_t> bytes = encode_kill_claim(request, claim);
    IntentView view;
    REQUIRE(decode_intent(bytes, view));
    CHECK(view.type == X4MP::Proto::IntentBody::KillClaim);
    CHECK(view.request.key_lo == request.key_lo);
    CHECK(view.request.key_hi == request.key_hi);
    CHECK(view.request.request_id == 7);
    const auto* body = flatbuffers::GetRoot<X4MP::Proto::Intent>(bytes.data())->body_as_KillClaim();
    REQUIRE(body != nullptr);
    CHECK(body->target() == 42);
    CHECK(body->killer() == 43);
    CHECK(body->weapon_macro_ref() == 44);
  }

  SECTION("PlayerDeath") {
    PlayerDeathData death;
    death.killer = 9;
    const std::vector<std::uint8_t> bytes = encode_player_death(request, death);
    IntentView view;
    REQUIRE(decode_intent(bytes, view));
    CHECK(view.type == X4MP::Proto::IntentBody::PlayerDeath);
    CHECK(flatbuffers::GetRoot<X4MP::Proto::Intent>(bytes.data())->body_as_PlayerDeath()->killer() == 9);
  }

  SECTION("TradeReport") {
    TradeReportData trade;
    trade.station = 100;
    trade.ship = 101;
    trade.ware_ref = 5;
    trade.amount = -500;
    trade.unit_price = 120;
    const std::vector<std::uint8_t> bytes = encode_trade_report(request, trade);
    IntentView view;
    REQUIRE(decode_intent(bytes, view));
    CHECK(view.type == X4MP::Proto::IntentBody::TradeReport);
    const auto* body = flatbuffers::GetRoot<X4MP::Proto::Intent>(bytes.data())->body_as_TradeReport();
    REQUIRE(body != nullptr);
    CHECK(body->station() == 100);
    CHECK(body->amount() == -500);
    CHECK(body->unit_price() == 120);
  }

  SECTION("StationBuildRequest") {
    StationBuildData build;
    build.macro = "station_arg_factory";
    build.construction_plan = "";
    build.sector = 12;
    build.position[0] = 1.5f;
    build.position[1] = -2.5f;
    build.position[2] = 3000.0f;
    build.local_component_id = 0x1b1d;
    const std::vector<std::uint8_t> bytes = encode_station_build(request, build);
    IntentView view;
    REQUIRE(decode_intent(bytes, view));
    CHECK(view.type == X4MP::Proto::IntentBody::StationBuildRequest);
    const auto* body = flatbuffers::GetRoot<X4MP::Proto::Intent>(bytes.data())->body_as_StationBuildRequest();
    REQUIRE(body != nullptr);
    REQUIRE(body->macro() != nullptr);
    CHECK(body->macro()->str() == "station_arg_factory");
    CHECK(body->sector() == 12);
    REQUIRE(body->position() != nullptr);
    CHECK(body->position()->x() == 1.5f);
    CHECK(body->position()->z() == 3000.0f);
    CHECK(body->local_component_id() == 0x1b1d);
  }

  SECTION("CaptureReport") {
    CaptureReportData capture;
    capture.target = 55;
    capture.new_owner_ref = 3;
    const std::vector<std::uint8_t> bytes = encode_capture_report(request, capture);
    IntentView view;
    REQUIRE(decode_intent(bytes, view));
    CHECK(view.type == X4MP::Proto::IntentBody::CaptureReport);
    CHECK(flatbuffers::GetRoot<X4MP::Proto::Intent>(bytes.data())->body_as_CaptureReport()->target() == 55);
  }

  SECTION("HitReport") {
    const std::vector<HitEntryData> hits = {{1, 0.25f, 0.0f}, {2, 0.0f, 0.5f}};
    const std::vector<std::uint8_t> bytes = encode_hit_report(request, hits);
    IntentView view;
    REQUIRE(decode_intent(bytes, view));
    CHECK(view.type == X4MP::Proto::IntentBody::HitReport);
    const auto* body = flatbuffers::GetRoot<X4MP::Proto::Intent>(bytes.data())->body_as_HitReport();
    REQUIRE(body != nullptr);
    REQUIRE(body->hits() != nullptr);
    REQUIRE(body->hits()->size() == 2);
    CHECK(body->hits()->Get(0)->target() == 1);
    CHECK(body->hits()->Get(1)->shield_damage() == 0.5f);
  }

  SECTION("a foreign buffer is refused, not misread") {
    const std::vector<std::uint8_t> junk = {1, 2, 3, 4, 5, 6, 7, 8};
    IntentView view;
    CHECK_FALSE(decode_intent(junk, view));
    IntentResultView result;
    CHECK_FALSE(decode_intent_result(junk, result));
    GameEventView event;
    CHECK_FALSE(decode_game_event(junk, event));
  }
}

TEST_CASE("M5-05: an IntentResult and a GameEvent decode into what the mod acts on", "[events][m505]") {
  flatbuffers::FlatBufferBuilder fbb(256);
  const X4MP::Proto::Id128 key(11, 22);
  const auto detail = fbb.CreateString("not in interest");
  const auto result = X4MP::Proto::CreateIntentResult(fbb, &key, 7, 3, X4MP::Proto::IntentStatus::Rejected,
                                                     X4MP::Proto::RejectReason::NotInInterest, detail, 0);
  fbb.Finish(result);
  const std::vector<std::uint8_t> result_bytes(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
  IntentResultView view;
  REQUIRE(decode_intent_result(result_bytes, view));
  CHECK(view.request_id == 7);
  CHECK(view.status == X4MP::Proto::IntentStatus::Rejected);
  CHECK(view.reason == X4MP::Proto::RejectReason::NotInInterest);
  CHECK(view.detail == "not in interest");

  flatbuffers::FlatBufferBuilder gfb(256);
  const auto kill = X4MP::Proto::CreateKillEvent(gfb, 42, 0, 0, X4MP::Proto::EntityKind::ShipS, 0, 43, 0, 0);
  const auto game_event = X4MP::Proto::CreateGameEvent(gfb, 99, 1000, 12.5, 7, X4MP::Proto::GameEventBody::KillEvent, kill.Union());
  gfb.Finish(game_event);
  const std::vector<std::uint8_t> event_bytes(gfb.GetBufferPointer(), gfb.GetBufferPointer() + gfb.GetSize());
  GameEventView event;
  REQUIRE(decode_game_event(event_bytes, event));
  CHECK(event.type == X4MP::Proto::GameEventBody::KillEvent);
  CHECK(event.event_seq == 99);
  CHECK(event.sector == 7);
  CHECK(flatbuffers::GetRoot<X4MP::Proto::GameEvent>(event_bytes.data())->body_as_KillEvent()->victim() == 42);
}

TEST_CASE("M5-06: a ghost kill becomes exactly one KillClaim, and an unmapped victim none", "[events][m506]") {
  struct Sent {
    std::uint16_t type = 0;
    std::vector<std::uint8_t> bytes;
  };
  std::vector<Sent> sent;
  bool send_ok = true;
  KillReporter reporter(KillReportDeps{
      [](std::uint64_t local_id, std::uint32_t& net_id) {
        if (local_id != 0x1b1d) return false;
        net_id = 42;
        return true;
      },
      [&sent, &send_ok](std::uint16_t type, std::span<const std::uint8_t> payload) {
        sent.push_back(Sent{type, std::vector<std::uint8_t>(payload.begin(), payload.end())});
        return send_ok;
      },
      [] { return static_cast<std::uint16_t>(3); },
      [] { return static_cast<std::uint32_t>(99); },
  });

  MdEvent kill = event_of(EventKind::Kill, 0x1b1d, 5000);
  kill.other = 0x1b20;  // the MD killer field is informational; the claim names our own ship instead
  CHECK(reporter.on_event(kill));
  REQUIRE(sent.size() == 1);
  CHECK(sent[0].type == static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent));

  const auto* claim = kill_claim_of(sent[0].bytes);
  REQUIRE(claim != nullptr);
  CHECK(claim->target() == 42);
  CHECK(claim->killer() == 99);
  IntentView view;
  REQUIRE(decode_intent(sent[0].bytes, view));
  CHECK(view.type == X4MP::Proto::IntentBody::KillClaim);
  CHECK(view.request.key_lo == 1);
  CHECK(view.request.key_hi == 3);       // the player id, so two nodes never mint the same key
  CHECK(view.request.player_id == 3);
  CHECK(reporter.stats().sent == 1);

  // A victim this node does not hold is not claimed, and is counted so the budget line can say so.
  MdEvent stranger = event_of(EventKind::Kill, 0x9999, 6000);
  CHECK_FALSE(reporter.on_event(stranger));
  CHECK(reporter.stats().unmapped == 1);
  CHECK(sent.size() == 1);

  // A send that fails is counted, not retried in a loop; the server never saw it.
  send_ok = false;
  MdEvent second = event_of(EventKind::Kill, 0x1b1d, 7000);
  CHECK_FALSE(reporter.on_event(second));
  CHECK(reporter.stats().send_failed == 1);
  CHECK(reporter.stats().sent == 1);

  // Anything that is not a kill line is not the reporter's business.
  CHECK_FALSE(reporter.on_event(event_of(EventKind::Trade, 0x1b1d, 8000)));
  CHECK(reporter.stats().sent == 1);
}
