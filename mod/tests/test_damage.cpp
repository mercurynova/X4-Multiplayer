// M15: the damage relay's two testable halves - the wire body the client sends and the queue the feature applies through MD.
//
// What a test can pin without a game: the hit-report accessor (so an encode the authority misreads is caught here, not in
// session 8), the clamp (a client must not be able to kill a ship with one line), and the hub's coalescing and bounds (so a
// burst of hits cannot grow the heap or wedge the frame budget).
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <limits>
#include <vector>

#include "features/damage/damage_hub.h"
#include "features/events/event_wire.h"

using namespace x4mp;
using namespace x4mp::features;

TEST_CASE("M15: a hit report survives the wire and a foreign body is refused", "[events][m15]") {
  events::IntentRequest request;
  request.key_lo = 0x0102030405060708ull;
  request.key_hi = 0x1112131415161718ull;
  request.request_id = 9;
  request.player_id = 2;
  request.game_time = 77.5;

  const std::vector<events::HitEntryData> hits = {{11, 0.5f, 0.0f}, {12, 0.0f, 1.5f}};
  const std::vector<std::uint8_t> bytes = events::encode_hit_report(request, hits);

  const X4MP::Proto::HitReport* report = events::hit_report_of(bytes);
  REQUIRE(report != nullptr);
  REQUIRE(report->hits() != nullptr);
  REQUIRE(report->hits()->size() == 2);
  CHECK(report->hits()->Get(0)->target() == 11);
  CHECK(report->hits()->Get(0)->hull_damage() == 0.5f);
  CHECK(report->hits()->Get(1)->target() == 12);
  CHECK(report->hits()->Get(1)->shield_damage() == 1.5f);

  // The intent header still decodes as one, so the authority's dispatch sees the same body type.
  events::IntentView view;
  REQUIRE(events::decode_intent(bytes, view));
  CHECK(view.type == X4MP::Proto::IntentBody::HitReport);

  CHECK(events::hit_report_of(std::span<const std::uint8_t>()) == nullptr);

  events::KillClaimData claim;
  claim.target = 3;
  claim.killer = 4;
  CHECK(events::hit_report_of(events::encode_kill_claim(request, claim)) == nullptr);
}

TEST_CASE("M15: a hit is clamped to what MD can apply, and an empty one is refused", "[damage][m15]") {
  CHECK_FALSE(damage::valid(damage::DamageOp{0, 1.0, 0.0}));       // no component
  CHECK_FALSE(damage::valid(damage::DamageOp{5, 0.0, 0.0}));       // nothing to apply
  CHECK_FALSE(damage::valid(damage::DamageOp{5, -1.0, 0.0}));      // negative damage
  CHECK_FALSE(damage::valid(damage::DamageOp{5, 1.0, std::numeric_limits<double>::quiet_NaN()}));
  CHECK(damage::valid(damage::DamageOp{5, 0.0, 2.0}));
  CHECK(damage::valid(damage::DamageOp{5, 2.0, 0.0}));

  const damage::DamageOp a = damage::clamp_hit(7, 5.0, 2.0);
  CHECK(a.component == 7);
  CHECK(a.hull == 5.0);
  CHECK(a.shield == 2.0);

  // One report can never take a value below zero or move it by more than the whole bar; a kill is a KillClaim.
  const damage::DamageOp big = damage::clamp_hit(8, 4000.0, 100.0);
  CHECK(big.hull == 100.0);
  CHECK(big.shield == 100.0);
  const damage::DamageOp junk = damage::clamp_hit(9, -5.0, std::numeric_limits<double>::infinity());
  CHECK(junk.hull == 0.0);
  CHECK(junk.shield == 0.0);
  CHECK_FALSE(damage::valid(junk));
}

TEST_CASE("M15: the damage queue coalesces one ship's hits, drops junk and stays bounded", "[damage][m15]") {
  damage::DamageHub& hub = damage::damage_hub();
  hub.reset();

  hub.queue(damage::DamageOp{1, 1.0, 0.0});
  hub.queue(damage::DamageOp{1, 2.0, 0.5});
  CHECK(hub.queued() == 1);
  CHECK(hub.stats().ops_queued == 2);  // both counted, one line kept

  std::vector<damage::DamageOp> ops = hub.take_ops(8);
  REQUIRE(ops.size() == 1);
  CHECK(ops[0].component == 1);
  CHECK(ops[0].hull == 3.0);
  CHECK(ops[0].shield == 0.5);
  CHECK(hub.queued() == 0);

  // Junk never reaches MD.
  hub.queue(damage::DamageOp{0, 5.0, 0.0});
  hub.queue(damage::DamageOp{2, 0.0, 0.0});
  CHECK(hub.queued() == 0);
  CHECK(hub.stats().ops_dropped == 2);

  // A burst is bounded, and a partial take leaves the rest for the next frame.
  for (std::size_t i = 0; i < damage::DamageHub::kMaxOps + 10; ++i) {
    hub.queue(damage::DamageOp{1000 + i, 0.5, 0.0});
  }
  CHECK(hub.queued() == damage::DamageHub::kMaxOps);
  CHECK(hub.stats().ops_dropped == 12);
  const std::vector<damage::DamageOp> first = hub.take_ops(10);
  CHECK(first.size() == 10);
  CHECK(first[0].component == 1000);
  CHECK(hub.queued() == damage::DamageHub::kMaxOps - 10);
  CHECK(hub.take_ops(0).empty());

  hub.reset();
  CHECK(hub.queued() == 0);
  CHECK(hub.stats().ops_queued == 0);
}

TEST_CASE("M15: a ghost's percentage drop is the damage, and a repair is not", "[damage][m15]") {
  // The probe answers in percent of maximum; only a DROP is a hit. A rise (a repair, a shield recharging) is not damage, and an
  // unreadable side (-1) must never look like a full kill.
  const damage::Percent before{80.0, 40.0};
  const damage::Percent after{72.5, 10.0};
  const damage::Percent hit = damage::percent_delta(before, after);
  CHECK(hit.hull == 7.5);
  CHECK(hit.shield == 30.0);

  const damage::Percent repaired = damage::percent_delta(after, before);
  CHECK(repaired.hull == 0.0);
  CHECK(repaired.shield == 0.0);

  const damage::Percent gone = damage::percent_delta(before, {100.0, -1.0});
  CHECK(gone.hull == 0.0);
  CHECK(gone.shield == 0.0);
  const damage::Percent unknownBefore = damage::percent_delta({-1.0, -1.0}, after);
  CHECK(unknownBefore.hull == 0.0);
  CHECK(unknownBefore.shield == 0.0);

  // Full to empty in one reading is still one report, and clamp_hit keeps it inside a single MD apply.
  const damage::Percent dead = damage::percent_delta({100.0, 100.0}, {0.0, 0.0});
  CHECK(dead.hull == 100.0);
  CHECK(damage::clamp_hit(3, dead.hull, dead.shield).hull == 100.0);
}
