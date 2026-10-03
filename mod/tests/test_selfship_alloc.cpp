// M3-09: the own-ship per-frame path must not allocate (adapter read + tracker + map lookup, incl. seat edges and sector changes),
// docs/m3-plan.md 4.12. Own executable (x4mp_selfship_alloc_tests): it links ghost/alloc_counter.cpp, which replaces the global operator new.
#include <cstdint>
#include <string>

#include <catch2/catch_test_macros.hpp>

#include "alloc_counter.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/own_ship_tracker.h"
#include "features/selfship/seta_guard.h"
#include "game/game_api.h"
#include "game/main_thread.h"
#include "game/selfship_api.h"

using namespace x4mp;
using namespace x4mp::features::selfship;
using x4mp::game::UniverseId;

namespace {
UniverseId g_occ = 50, g_sector = 1001;
game::PosRotPod g_pose;
UniverseId fk_occ() { return g_occ; }
UniverseId fk_container() { return 50; }
UniverseId fk_ctx(UniverseId id, const char* cls, bool) {
  const char c = cls[0];
  if (c == 's' && cls[1] == 'e') return g_sector;  // sector
  if (c == 's' && cls[1] == 'h') return id;        // ship
  return 0;
}
bool fk_dock() { return false; }
game::PosRotPod fk_pos(UniverseId) { return g_pose; }
bool fk_seta() { return false; }
void* lookup(const char* name) {
  const std::string n = name;
  if (n == "GetPlayerOccupiedShipID") return reinterpret_cast<void*>(&fk_occ);
  if (n == "GetPlayerContainerID") return reinterpret_cast<void*>(&fk_container);
  if (n == "GetContextByClass") return reinterpret_cast<void*>(&fk_ctx);
  if (n == "IsPlayerOccupiedShipDocked") return reinterpret_cast<void*>(&fk_dock);
  if (n == "GetObjectPositionInSector") return reinterpret_cast<void*>(&fk_pos);
  if (n == "IsSetaActive") return reinterpret_cast<void*>(&fk_seta);
  return nullptr;
}
}  // namespace

TEST_CASE("selfship: no allocation per frame (read + tracker + map + seta guard)", "[selfship][hot]") {
  game::main_thread().reset();
  game::main_thread().set_definition(game::MainThreadDefinition::FrameUpdateThread);
  game::GameApi api(game::resolve_game_fns(lookup), game::GameInfo{});
  GalaxyMap map;
  REQUIRE(map.add_message("S;a_macro|1001;b_macro|1002;c_macro|1003"));
  REQUIRE(map.add_message("E;3"));
  OwnShipTracker tracker;
  SetaGuard seta;
  std::int64_t now = 1'000'000;
  std::uint64_t sent = 0;
  // warm up (first-use buffers, if any)
  for (int i = 0; i < 50; ++i) {
    now += 7'000;
    Observation o;
    o.now_us = now;
    const auto r = game::read_own_ship(api);
    o.occupied = r.occupied;
    o.sector = r.sector;
    o.pose_valid = r.pose_valid;
    (void)tracker.update(o, map);
  }
  test::alloc_counter_arm();
  for (int i = 0; i < 20'000; ++i) {
    now += 7'000;  // 144 fps
    g_pose.x = static_cast<float>(i) * 0.5f;
    g_sector = 1001 + static_cast<UniverseId>((i / 2000) % 3);  // a sector change every 2000 frames
    g_occ = (i % 5000) < 4000 ? 50 : 0;                         // sits / stands
    const auto r = game::read_own_ship(api);
    Observation o;
    o.now_us = now;
    o.occupied = r.occupied;
    o.standing_ship = r.standing_ship;
    o.sector = r.sector;
    o.in_highway = r.in_highway;
    o.docked = r.docked;
    o.pose_valid = r.pose_valid;
    o.pos = {r.pose.x, r.pose.y, r.pose.z};
    o.rot = {r.pose.yaw, r.pose.pitch, r.pose.roll};
    const auto t = tracker.update(o, map);
    sent += t.out.send ? 1 : 0;
    (void)seta.update(now, true, api.seta_active());
  }
  const auto allocations = test::alloc_counter_disarm();
  CHECK(allocations == 0);
  CHECK(sent > 1000);
}
