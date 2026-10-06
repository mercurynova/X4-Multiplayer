// M3-09: the own-ship feature's pure parts (docs/m3-plan.md 4.4, 4.5, 6.3): galaxy map, tracker (seat edges, rates, flags, teleports), SETA
// guard, and the SDK-free game adapter over fake exports. The live behaviour is covered by tests/hostsim/selfship*.hostsim.
#include <cmath>
#include <functional>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/ghost/sample.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/own_ship_tracker.h"
#include "features/selfship/seta_guard.h"
#include "game/game_api.h"
#include "game/main_thread.h"
#include "game/selfship_api.h"

using namespace x4mp::features::selfship;
namespace gh = x4mp::ghost;

namespace {
GalaxyMap make_map() {
  GalaxyMap m;
  REQUIRE(m.add_message("S;c3_macro|3003;c1_macro|1001"));
  REQUIRE(m.add_message("S;c2_macro|2002"));
  REQUIRE(m.add_message("E;3"));
  return m;
}

constexpr std::int64_t kMs = 1000;

struct Sim {
  GalaxyMap map = make_map();
  OwnShipTracker tracker;
  std::int64_t now = 1'000'000;
  std::vector<std::pair<std::int64_t, Tick>> sends;  // time, tick
  Observation o;

  Sim() {
    o.occupied = 7;
    o.sector = 1001;
    o.pose_valid = true;
    o.pos = {100, 0, 0};
  }
  Tick step() {
    o.now_us = now;
    const auto t = tracker.update(o, map);
    if (t.out.send) sends.emplace_back(now, t);
    return t;
  }
  // `seconds` of frames at `fps`; `move(t_s)` updates the observation before each frame
  void run(double seconds, int fps, const std::function<void(double)>& move = {}) {
    const std::int64_t dt = 1'000'000 / fps;
    const std::int64_t end = now + static_cast<std::int64_t>(seconds * 1e6);
    const std::int64_t start = now;
    while (now < end) {
      if (move) move(static_cast<double>(now - start) / 1e6);
      step();
      now += dt;
    }
  }
  [[nodiscard]] std::size_t sends_between(std::int64_t from, std::int64_t to) const {
    std::size_t n = 0;
    for (const auto& s : sends) n += (s.first >= from && s.first < to) ? 1 : 0;
    return n;
  }
};
}  // namespace

// ---------------------------------------------------------------------------------------------------------------------------------------
TEST_CASE("galaxy map: indices are the rank of the sorted macros, whatever the order they arrived in", "[selfship][map]") {
  const GalaxyMap m = make_map();
  CHECK(m.ready());
  CHECK(m.size() == 3);
  CHECK(m.index_of(1001) == 1);
  CHECK(m.index_of(2002) == 2);
  CHECK(m.index_of(3003) == 3);
  CHECK(m.index_of(9999) == 0);
  CHECK(m.index_of(0) == 0);
  CHECK(m.universe_id_of(3) == 3003);
  CHECK(m.universe_id_of(4) == 0);
  CHECK(m.macro_of(2) == "c2_macro");
  CHECK(m.index_of_macro("c3_macro") == 3);
  CHECK(m.index_of_macro("nope") == 0);
}

TEST_CASE("galaxy map: not ready until the end marker's count matches; bad records are dropped; a new collection starts over", "[selfship][map]") {
  GalaxyMap m;
  CHECK_FALSE(m.add_message(""));
  CHECK_FALSE(m.add_message("X;1"));
  CHECK_FALSE(m.add_message("E;abc"));
  CHECK(m.add_message("S;a|11;broken;b|zz;c|0;d|44"));
  CHECK(m.dropped_records() == 3);
  CHECK(m.add_message("E;3"));
  CHECK_FALSE(m.ready());  // 2 good records, 3 announced
  CHECK(m.index_of(11) == 0);
  CHECK(m.add_message("E;2"));
  CHECK(m.ready());
  CHECK(m.index_of(44) == 2);
  // a new "S;" after a completed map starts over
  CHECK(m.add_message("S;z|5"));
  CHECK_FALSE(m.ready());
  CHECK(m.add_message("E;1"));
  CHECK(m.ready());
  CHECK(m.size() == 1);
  CHECK(m.index_of(44) == 0);
  CHECK(m.index_of(5) == 1);
}

TEST_CASE("galaxy map: large 64-bit ids", "[selfship][map]") {
  GalaxyMap m;
  CHECK(m.add_message("S;a|18446744073709551000;b|9007199254740993"));
  CHECK(m.add_message("E;2"));
  CHECK(m.index_of(18446744073709551000ull) == 1);
  CHECK(m.index_of(9007199254740993ull) == 2);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
TEST_CASE("tracker: nobody sits -> no state, no edge; the ship is known from the container while standing", "[selfship][tracker]") {
  Sim s;
  s.o.occupied = 0;
  s.o.standing_ship = 7;
  s.run(2.0, 60);
  CHECK(s.sends.empty());
  CHECK(s.tracker.counters().seat_edges == 0);
  CHECK(s.tracker.ship() == 7);
  CHECK_FALSE(s.tracker.seated());
}

TEST_CASE("tracker: sitting down gives an immediate Teleport state, then 20 Hz while moving", "[selfship][tracker]") {
  for (const int fps : {144, 60, 30}) {
    CAPTURE(fps);
    Sim s;
    s.o.occupied = 0;
    s.o.standing_ship = 7;
    s.run(0.5, fps);
    s.o.occupied = 7;
    s.o.standing_ship = 0;
    const auto t = s.step();
    CHECK(t.edge == SeatEdge::SatDown);
    REQUIRE(t.out.send);
    CHECK(t.out.immediate);
    CHECK((t.out.flags & gh::kTeleport) != 0);
    CHECK((t.out.flags & gh::kPlayerControlled) != 0);
    CHECK((t.out.flags & gh::kHidden) == 0);
    CHECK(t.out.sector == 1);
    s.sends.clear();
    const std::int64_t from = s.now;
    s.run(5.0, fps, [&](double ts) { s.o.pos = {100 + 200 * ts, 0, 0}; });  // 200 m/s
    const double hz = static_cast<double>(s.sends_between(from, from + 5'000'000)) / 5.0;
    CHECK(hz >= 19.0);
    CHECK(hz <= 21.0);
    for (const auto& snd : s.sends) CHECK((snd.second.out.flags & gh::kTeleport) == 0);
  }
}

TEST_CASE("tracker: 20 Hz frames still give 20 Hz (the schedule slack)", "[selfship][tracker]") {
  Sim s;
  s.step();
  s.sends.clear();
  const std::int64_t from = s.now;
  s.run(5.0, 20, [&](double ts) { s.o.pos = {100 + 200 * ts, 0, 0}; });
  const double hz = static_cast<double>(s.sends_between(from, from + 5'000'000)) / 5.0;
  CHECK(hz >= 19.0);
  CHECK(hz <= 21.0);
}

TEST_CASE("tracker: idle drops to 5 Hz after the 250 ms window and moving brings 20 Hz back at once", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 144);  // stationary from the start
  s.sends.clear();
  std::int64_t from = s.now;
  s.run(4.0, 144);
  double hz = static_cast<double>(s.sends_between(from, from + 4'000'000)) / 4.0;
  CHECK(hz >= 4.5);
  CHECK(hz <= 5.5);
  CHECK_FALSE(s.tracker.moving());
  // a slow drift under 1 m/s is still idle
  s.sends.clear();
  from = s.now;
  s.run(4.0, 144, [&](double ts) { s.o.pos = {100 + 0.5 * ts, 0, 0}; });
  hz = static_cast<double>(s.sends_between(from, from + 4'000'000)) / 4.0;
  CHECK(hz <= 5.5);
  // moving again: within 0.4 s the rate is back to 20 Hz
  const auto base = s.o.pos;
  s.run(0.4, 144, [&](double ts) { s.o.pos = {base.x + 50 * ts, 0, 0}; });
  s.sends.clear();
  from = s.now;
  s.run(3.0, 144, [&](double ts) { s.o.pos = {base.x + 20 + 50 * ts, 0, 0}; });
  hz = static_cast<double>(s.sends_between(from, from + 3'000'000)) / 3.0;
  CHECK(hz >= 19.0);
  CHECK(s.tracker.moving());
}

TEST_CASE("tracker: turning in place counts as moving", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 144);
  s.run(0.5, 144, [&](double ts) { s.o.rot.yaw = 0.5 * ts; });  // 0.5 rad/s: the 250 ms window notices within half a second
  s.sends.clear();
  const std::int64_t from = s.now;
  s.run(3.0, 144, [&](double ts) { s.o.rot.yaw = 0.25 + 0.5 * ts; });
  const double hz = static_cast<double>(s.sends_between(from, from + 3'000'000)) / 3.0;
  CHECK(hz >= 19.0);
}

TEST_CASE("tracker: a sector change is sent at once with Teleport (a gate jump has a 3.5 s frame gap)", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 60, [&](double ts) { s.o.pos = {100 + 200 * ts, 0, 0}; });
  s.sends.clear();
  s.now += 3'500 * kMs;  // the gap
  s.o.sector = 2002;
  s.o.pos = {0, 0, 50};
  const auto t = s.step();
  REQUIRE(t.out.send);
  CHECK(t.out.immediate);
  CHECK(t.out.sector == 2);
  CHECK((t.out.flags & gh::kTeleport) != 0);
  CHECK(s.tracker.counters().teleports >= 1);
  // the next one is a normal sample again
  s.now += 16 * kMs;
  s.o.pos = {0, 0, 53};
  s.run(0.2, 60);
  for (std::size_t i = 1; i < s.sends.size(); ++i) CHECK((s.sends[i].second.out.flags & gh::kTeleport) == 0);
}

TEST_CASE("tracker: a position jump inside one sector is a teleport; a fast but continuous flight is not", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 60, [&](double ts) { s.o.pos = {100 + 4000 * ts, 0, 0}; });  // 4 km/s (a highway)
  for (std::size_t i = 1; i < s.sends.size(); ++i) CHECK((s.sends[i].second.out.flags & gh::kTeleport) == 0);  // the first one is the sit-down
  s.sends.clear();
  s.o.pos = {s.o.pos.x + 30'000, 0, 0};
  s.now += 16 * kMs;
  const auto t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
}

TEST_CASE("tracker: docked and highway set Hidden, are sent at once, then 1 Hz", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 60, [&](double ts) { s.o.pos = {100 + 200 * ts, 0, 0}; });
  s.sends.clear();
  s.o.docked = true;
  auto t = s.step();
  REQUIRE(t.out.send);
  CHECK(t.out.immediate);
  CHECK((t.out.flags & gh::kHidden) != 0);
  CHECK((t.out.flags & gh::kDocked) != 0);
  s.sends.clear();
  const std::int64_t from = s.now;
  s.run(5.0, 60);
  const double hz = static_cast<double>(s.sends_between(from, from + 5'000'000)) / 5.0;
  CHECK(hz >= 0.8);
  CHECK(hz <= 1.2);
  // undock: visible again at once
  s.o.docked = false;
  t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kHidden) == 0);
  // highway
  s.o.in_highway = true;
  t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kHidden) != 0);
  CHECK((t.out.flags & gh::kInHighway) != 0);
  CHECK((t.out.flags & gh::kDocked) == 0);
}

TEST_CASE("tracker: standing up sends ONE Hidden state and then nothing, for minutes; sitting down again is a new edge", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 60, [&](double ts) { s.o.pos = {100 + 200 * ts, 0, 0}; });
  s.sends.clear();
  s.o.occupied = 0;
  s.o.standing_ship = 7;
  auto t = s.step();
  CHECK(t.edge == SeatEdge::StoodUp);
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kHidden) != 0);
  CHECK(t.out.sector == 1);
  CHECK(s.sends.size() == 1);
  s.run(180.0, 30);  // three minutes standing
  CHECK(s.sends.size() == 1);
  CHECK(s.tracker.ship() == 7);
  // sit down again
  s.o.occupied = 7;
  s.o.standing_ship = 0;
  t = s.step();
  CHECK(t.edge == SeatEdge::SatDown);
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
  CHECK((t.out.flags & gh::kHidden) == 0);
  CHECK(s.tracker.counters().seat_edges == 3);  // the initial sit-down, the stand-up, the second sit-down
}

TEST_CASE("tracker: a ship change while seated is an edge with Teleport", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 60);
  s.o.occupied = 8;
  const auto t = s.step();
  CHECK(t.edge == SeatEdge::ShipChanged);
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
  CHECK(s.tracker.ship() == 8);
}

TEST_CASE("tracker: no state without a sector map, an unknown sector or a pose; it says why", "[selfship][tracker]") {
  Sim s;
  GalaxyMap empty;
  s.o.now_us = s.now;
  auto t = s.tracker.update(s.o, empty);
  CHECK_FALSE(t.out.send);
  CHECK(t.blocked == Blocked::MapNotReady);
  s.o.sector = 424242;
  t = s.tracker.update(s.o, s.map);
  CHECK(t.blocked == Blocked::UnknownSector);
  s.o.sector = 1001;
  s.o.pose_valid = false;
  t = s.tracker.update(s.o, s.map);
  CHECK(t.blocked == Blocked::NoPose);
  CHECK_FALSE(t.out.send);
  // once everything is there the first state still goes out at once (the seat edge was seen while blocked)
  s.o.pose_valid = true;
  s.now += 16 * kMs;
  t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
  CHECK(s.tracker.counters().blocked_frames == 3);
}

// M3-31 (Finding 18): a superhighway. The ship's sector is not in the map for the whole transit: one Hidden state at the last known place when it
// starts, nothing during it, then a teleport state in the exit sector (also when the exit is in the sector it entered from).
TEST_CASE("tracker: an unknown sector after a state sends one Hidden state, then a teleport state on the exit (M3-31)", "[selfship][tracker][m331]") {
  Sim s;
  s.run(1.0, 60, [&](double ts) { s.o.pos = {100 + 1500 * ts, 0, 0}; });
  s.sends.clear();
  const auto last_pos = s.o.pos;
  s.o.sector = 424242;  // inside the highway
  s.o.in_highway = true;
  s.now += 16 * kMs;
  auto t = s.step();
  CHECK(t.blocked == Blocked::UnknownSector);
  REQUIRE(t.out.send);
  CHECK(t.out.immediate);
  CHECK((t.out.flags & gh::kHidden) != 0);
  CHECK((t.out.flags & gh::kInHighway) != 0);
  CHECK((t.out.flags & gh::kTeleport) == 0);
  CHECK(t.out.sector == 1);
  CHECK(t.out.pos.x == last_pos.x);
  // 20 s inside: silence
  s.sends.clear();
  s.run(20.0, 60, [&](double ts) { s.o.pos = {9000 + 5000 * ts, 0, 0}; });
  CHECK(s.sends.empty());
  // the exit: another sector, visible
  s.o.sector = 2002;
  s.o.in_highway = false;
  s.o.pos = {-300, 0, 0};
  s.now += 16 * kMs;
  t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
  CHECK((t.out.flags & gh::kHidden) == 0);
  CHECK(t.out.sector == 2);
  // and when the exit is in the sector it entered from, close to the entry: still a teleport (the gap is blind)
  s.run(2.0, 60, [&](double ts) { s.o.pos = {-300 + 100 * ts, 0, 0}; });
  s.o.sector = 424242;
  s.o.in_highway = true;
  s.now += 16 * kMs;
  t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kHidden) != 0);
  s.o.sector = 2002;
  s.o.in_highway = false;
  s.now += 16 * kMs;
  t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
}

TEST_CASE("tracker: force_resend (the link came up) sends a full state at once", "[selfship][tracker]") {
  Sim s;
  s.run(2.0, 60);
  s.sends.clear();
  s.tracker.force_resend();
  s.now += 16 * kMs;
  const auto t = s.step();
  REQUIRE(t.out.send);
  CHECK((t.out.flags & gh::kTeleport) != 0);
}

TEST_CASE("tracker: a long frame gap does not burst", "[selfship][tracker]") {
  Sim s;
  s.run(1.0, 60, [&](double ts) { s.o.pos = {100 + 200 * ts, 0, 0}; });
  s.sends.clear();
  s.now += 2'000 * kMs;
  s.o.pos = {s.o.pos.x + 400, 0, 0};
  s.step();
  s.now += 16 * kMs;
  s.o.pos.x += 3;
  s.step();
  s.now += 16 * kMs;
  s.o.pos.x += 3;
  s.step();
  CHECK(s.sends.size() <= 2);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
TEST_CASE("seta guard: detected -> switch-off requested at once, repeated every 500 ms, one notification per 5 s", "[selfship][seta]") {
  SetaGuard g;
  std::int64_t now = 0;
  auto a = g.update(now, true, false);
  CHECK_FALSE(a.request_off);
  a = g.update(now += 10 * kMs, true, true);
  CHECK(a.request_off);
  CHECK(a.notify);
  a = g.update(now += 100 * kMs, true, true);
  CHECK_FALSE(a.request_off);
  a = g.update(now += 450 * kMs, true, true);
  CHECK(a.request_off);
  CHECK_FALSE(a.notify);
  a = g.update(now += 100 * kMs, true, false);
  CHECK(a.switched_off);
  CHECK(a.off_after_us >= 600 * kMs);
  CHECK(g.detections() == 1);
  CHECK(g.requests() == 2);
  // on again within 5 s: request but no second notification
  a = g.update(now += 1000 * kMs, true, true);
  CHECK(a.request_off);
  CHECK_FALSE(a.notify);
  g.update(now += 10 * kMs, true, false);
  a = g.update(now += 6'000 * kMs, true, true);
  CHECK(a.notify);
}

TEST_CASE("seta guard: nothing while disconnected; the MD source block shares the notification rate limit", "[selfship][seta]") {
  SetaGuard g;
  auto a = g.update(0, false, true);
  CHECK_FALSE(a.request_off);
  CHECK_FALSE(a.notify);
  CHECK(g.blocked_at_source(1'000'000));
  CHECK_FALSE(g.blocked_at_source(2'000'000));
  CHECK(g.blocked_at_source(7'000'000));
  CHECK(g.source_blocks() == 3);
  a = g.update(7'100'000, true, true);  // detected 100 ms after a notification: no second one
  CHECK(a.request_off);
  CHECK_FALSE(a.notify);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
namespace {
using x4mp::game::UniverseId;
UniverseId g_occ = 0, g_cont = 0, g_sector = 0, g_highway = 0;
bool g_dock = false;
x4mp::game::PosRotPod g_pose;
UniverseId fk_occ() { return g_occ; }
UniverseId fk_container() { return g_cont; }
UniverseId fk_ctx(UniverseId id, const char* cls, bool) {
  const std::string c = cls;
  if (c == "sector") return id == 0 ? 0 : g_sector;
  if (c == "highway") return g_highway;
  if (c == "ship") return (id == 50 || id == 51) ? id : 0;  // 50 and 51 are ships; 900 is a station
  return 0;
}
bool fk_isdock() { return g_dock; }
x4mp::game::PosRotPod fk_pos(UniverseId) { return g_pose; }

void* lookup(const char* name) {
  const std::string n = name;
  if (n == "GetPlayerOccupiedShipID") return reinterpret_cast<void*>(&fk_occ);
  if (n == "GetPlayerContainerID") return reinterpret_cast<void*>(&fk_container);
  if (n == "GetContextByClass") return reinterpret_cast<void*>(&fk_ctx);
  if (n == "IsPlayerOccupiedShipDocked") return reinterpret_cast<void*>(&fk_isdock);
  if (n == "GetObjectPositionInSector") return reinterpret_cast<void*>(&fk_pos);
  return nullptr;
}
}  // namespace

TEST_CASE("selfship adapter: seat, container ship, sector, highway, dock and pose from the exports", "[selfship][adapter]") {
  x4mp::game::main_thread().reset();
  x4mp::game::main_thread().set_definition(x4mp::game::MainThreadDefinition::FrameUpdateThread);
  x4mp::game::GameApi api(x4mp::game::resolve_game_fns(lookup), x4mp::game::GameInfo{});
  g_sector = 1001;
  g_pose = {10, 20, 30, 0.5f, -0.25f, 0.125f};
  // seated
  g_occ = 50;
  g_cont = 50;
  auto r = x4mp::game::read_own_ship(api);
  CHECK(r.occupied == 50);
  CHECK(r.standing_ship == 0);
  CHECK(r.ship() == 50);
  CHECK(r.sector == 1001);
  CHECK(r.pose_valid);
  CHECK(r.pose.x == 10);
  CHECK(r.pose.yaw == 0.5f);
  CHECK_FALSE(r.in_highway);
  CHECK_FALSE(r.docked);
  g_highway = 77;
  g_dock = true;
  r = x4mp::game::read_own_ship(api);
  CHECK(r.in_highway);
  CHECK(r.docked);
  // standing in the cockpit: the container is the ship
  g_occ = 0;
  g_cont = 51;
  g_dock = true;
  r = x4mp::game::read_own_ship(api);
  CHECK(r.occupied == 0);
  CHECK(r.standing_ship == 51);
  CHECK(r.ship() == 51);
  CHECK_FALSE(r.docked);  // docked is the OCCUPIED ship's state
  // standing in a station: no ship
  g_cont = 900;
  r = x4mp::game::read_own_ship(api);
  CHECK(r.ship() == 0);
  CHECK_FALSE(r.pose_valid);
  CHECK(r.sector == 0);
  // exports missing: all zero
  x4mp::game::GameApi none(x4mp::game::resolve_game_fns([](const char*) -> void* { return nullptr; }), x4mp::game::GameInfo{});
  r = x4mp::game::read_own_ship(none);
  CHECK(r.ship() == 0);
  CHECK_FALSE(r.pose_valid);
  x4mp::game::main_thread().reset();
  x4mp::game::main_thread().set_definition(x4mp::game::MainThreadDefinition::FrameUpdateThread);
}
