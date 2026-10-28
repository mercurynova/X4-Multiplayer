// M5 W5: a finished build becomes one Intent{StationBuildRequest}, and the authority's result pins the station id (docs/m5-plan.md
// W5, M5-18/M5-19).
//
// What a test can pin without a game: the reporter's one-request-per-build rule, the dedupe, the "wait, never guess" pending list with
// its 10 s expiry, the request bytes as the authority decodes them, and the statics hub's pinned id that a later scan must not take
// back. The spawn itself and the MD B line are session 8.
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "features/builds/build_report.h"
#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "features/statics/statics_hub.h"
#include "message_ids_generated.h"

using namespace x4mp;
using namespace x4mp::features;
using namespace x4mp::features::builds;

namespace {

events::MdEvent build_event(std::uint64_t anchor, std::string macro) {
  events::MdEvent event;
  event.kind = events::EventKind::Build;
  event.component = anchor;
  event.macro = std::move(macro);
  event.at_ms = 5'000;
  return event;
}

struct Sent {
  std::uint16_t type = 0;
  std::vector<std::uint8_t> bytes;
};

struct Harness {
  std::vector<Sent> sent;
  bool send_ok = true;
  bool describe_ok = true;
  std::uint64_t clock_ms = 1'000;

  BuildReporter make() {
    BuildReportDeps deps;
    deps.describe = [this](std::uint64_t component, BuildDescribe& out) {
      if (!describe_ok || component == 0) return false;
      out.sector = 7;
      out.position[0] = 1200.5f;
      out.position[1] = -40.0f;
      out.position[2] = 88.25f;
      out.rotation[0] = 1.5f;
      return true;
    };
    deps.self_player_id = [] { return std::uint16_t{3}; };
    deps.send_control = [this](X4MP::Proto::MsgType type, std::span<const std::uint8_t> payload) {
      if (!send_ok) return false;
      sent.push_back(Sent{static_cast<std::uint16_t>(type), std::vector<std::uint8_t>(payload.begin(), payload.end())});
      return true;
    };
    deps.now_ms = [this] { return clock_ms; };
    return BuildReporter(std::move(deps));
  }
};

}  // namespace

TEST_CASE("M5-18: a finished build becomes one request, and the same build twice is deduped", "[builds][m518]") {
  Harness harness;
  auto reporter = harness.make();

  CHECK(reporter.on_event(build_event(0xABC, "station_pla_trade_01_macro")));
  REQUIRE(harness.sent.size() == 1);
  CHECK(harness.sent[0].type == static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent));

  // The bytes are what the authority will decode: the macro, the sector index, the pose in radians and the local component.
  const auto* build = events::station_build_of(harness.sent[0].bytes);
  REQUIRE(build != nullptr);
  CHECK(build->macro()->str() == "station_pla_trade_01_macro");
  CHECK(build->construction_plan()->str().empty());  // M5 Q7: the macro plus modules first
  CHECK(build->sector() == 7);
  CHECK(build->local_component_id() == 0xABC);
  CHECK(build->position()->x() == 1200.5f);
  CHECK(build->rotation()->yaw() == 1.5f);

  // A second event for the same build (a double-fired cue, a replayed frame) produces nothing.
  CHECK(reporter.on_event(build_event(0xABC, "station_pla_trade_01_macro")));
  CHECK(harness.sent.size() == 1);
  CHECK(reporter.stats().sent == 1);
  CHECK(reporter.stats().deduped == 1);

  // A different station is its own request.
  CHECK(reporter.on_event(build_event(0xDEF, "station_pla_defence_01_macro")));
  CHECK(harness.sent.size() == 2);

  // A line with no component or no macro is counted, never sent.
  CHECK(reporter.on_event(build_event(0, "station_x")));
  CHECK(reporter.on_event(build_event(0x123, "")));
  CHECK(harness.sent.size() == 2);
  CHECK(reporter.stats().bad == 2);
}

TEST_CASE("M5-18: a build whose sector is not known waits, then expires without a guess", "[builds][m518]") {
  Harness harness;
  harness.describe_ok = false;
  auto reporter = harness.make();

  CHECK(reporter.on_event(build_event(0x111, "station_pla_trade_01_macro")));
  CHECK(harness.sent.empty());
  CHECK(reporter.pending() == 1);
  CHECK(reporter.stats().unresolved == 1);

  // Still not nameable: the request stays.
  harness.clock_ms += 5'000;
  reporter.retry(harness.clock_ms);
  CHECK(harness.sent.empty());
  CHECK(reporter.pending() == 1);

  // The galaxy map catches up inside the 10 s window: the request goes out exactly once.
  harness.describe_ok = true;
  harness.clock_ms += 2'000;
  reporter.retry(harness.clock_ms);
  CHECK(harness.sent.size() == 1);
  CHECK(reporter.pending() == 0);

  // A build that never becomes nameable is dropped and counted, not sent to a guessed sector.
  harness.describe_ok = false;
  CHECK(reporter.on_event(build_event(0x222, "station_pla_trade_01_macro")));
  harness.clock_ms += BuildReporter::kRetryMs + 1;
  reporter.retry(harness.clock_ms);
  CHECK(harness.sent.size() == 1);
  CHECK(reporter.pending() == 0);
  CHECK(reporter.stats().dropped == 1);
}

TEST_CASE("M5-19: the result of a build request is the one the reporter remembers", "[builds][m519]") {
  Harness harness;
  auto reporter = harness.make();
  CHECK(reporter.on_event(build_event(0xABC, "station_pla_trade_01_macro")));
  REQUIRE(harness.sent.size() == 1);

  const auto* build = events::station_build_of(harness.sent[0].bytes);
  REQUIRE(build != nullptr);
  // The request id the authority echoes is the one the reporter keeps: find it by trying the plausible ids.
  OutstandingBuild found;
  bool taken = false;
  for (std::uint32_t id = 1; id <= 4 && !taken; ++id) taken = reporter.take_outstanding(id, found);
  REQUIRE(taken);
  CHECK(found.component == 0xABC);
  CHECK(found.sector == 7);
  CHECK(found.macro == "station_pla_trade_01_macro");
  // Once only: a duplicate result must not re-bind anything.
  OutstandingBuild again;
  CHECK_FALSE(reporter.take_outstanding(found.request_id, again));

  // A send the link refused is counted and not retried (a retry would spawn the station twice).
  harness.send_ok = false;
  CHECK(reporter.on_event(build_event(0x333, "station_pla_trade_01_macro")));
  CHECK(reporter.stats().failed == 1);
  CHECK(reporter.pending() == 0);
}

TEST_CASE("M5-19: a pinned station id survives a later scan, and the sector macro comes with it", "[builds][m519]") {
  auto& hub = statics::statics_hub();
  hub.clear();

  // The scan names the station with its hash id and the sector it found it in.
  hub.note_station(7, 0x1000, 0x80000001u, "session_3_sector");
  CHECK(hub.station_net_id(0x1000) == 0x80000001u);
  CHECK(hub.sector_macro_of(0x1000) == "session_3_sector");

  // The authority spawns it and the client binds the id it was told.
  hub.pin_station(7, 0x1000, 0x4242, "session_3_sector");
  CHECK(hub.station_net_id(0x1000) == 0x4242);
  CHECK(hub.station_component(0x4242) == 0x1000);
  CHECK(hub.station_component(0x80000001u) == 0);  // the old id is gone, not aliased

  // The next scan pass still computes the hash: the pinned id must win, or the station would change identity every pass.
  hub.note_station(7, 0x1000, 0x80000001u, "session_3_sector");
  CHECK(hub.station_net_id(0x1000) == 0x4242);

  // A station the pass no longer lists is gone, pinned or not.
  CHECK(hub.prune_stations(7, {}) == 1);
  CHECK(hub.station_net_id(0x1000) == 0);
  CHECK(hub.station_component(0x4242) == 0);
  CHECK(hub.stations_known() == 0);
  hub.clear();
}
