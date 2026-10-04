// GhostDriver over a fake game (M3-10): spawn + dress + inert, movement against the analytic track, velocity hints, hide / show, sector
// changes (a cross-sector move lands within 1 m), parked label, pushed ghosts, validity, throttles, clearance, removal rules, adoption
// after a reload (no duplicates), 20 reloads.
#include <algorithm>
#include <cmath>
#include <tuple>

#include <catch2/catch_test_macros.hpp>

#include "rig.h"

using namespace x4mp;
using namespace x4mp::test;

namespace {
double dist(const Vec3& a, const Vec3& b) { return ghost::distance(a, b); }
}  // namespace

TEST_CASE("a player ship becomes a dressed, inert ghost under the team faction", "[ghosts][driver]") {
  Rig r;
  r.add(11, circle_track(1000, 150), Rig::info(11, 2, "Pax", 2));
  r.run(1.0);
  REQUIRE(r.world.objs.size() == 1);
  const FakeObj& o = r.world.objs.begin()->second;
  CHECK(o.owner == "x4mp_team_1");
  CHECK(o.macro == "ship_arg_s_fighter_01_a_macro");
  CHECK(o.name == "[MP] Pax");  // dressed (MD) with the label
  CHECK(o.min_hull == 100);     // Q11: indestructible locally
  CHECK(o.inert);
  CHECK(o.sector == 9001);
  // registry (stash side) knows it
  REQUIRE(r.driver->registries().ghosts.size() == 1);
  const auto* e = r.driver->registries().ghosts.find(11);
  REQUIRE(e != nullptr);
  CHECK(e->local_id == o.id);
  const auto* n = r.driver->registries().netmap.find_net(11);
  REQUIRE(n != nullptr);
  CHECK(n->kind == ghost::NetKind::Ghost);
  CHECK(r.driver->counters().spawned == 1);
}

TEST_CASE("the ghost follows the analytic track (path error well under 2 m)", "[ghosts][driver]") {
  for (const auto& [name, track, bound] : {std::tuple{"circle", circle_track(1500, 250), 0.5}, std::tuple{"line", line_track(300), 0.5},
                                           std::tuple{"accel", accel_track(20, 450), 0.5}}) {
    CAPTURE(name);
    Rig r;
    r.add(11, track, Rig::info(11, 2, "Pax", 2));
    r.run(3.0);  // warm-up
    REQUIRE(r.world.objs.size() == 1);
    double worst = 0;
    int n = 0;
    for (int i = 0; i < 600; ++i) {
      (void)r.step();
      const auto v = r.driver->views();
      REQUIRE(v.size() == 1);
      const FakeObj& o = r.world.objs.begin()->second;
      const TrackPoint tp = track(r.track_time_s(v[0].represented_t_us));
      worst = std::max(worst, dist(o.pose.pos, tp.pos));
      ++n;
    }
    CHECK(worst < bound);
    CHECK(n == 600);
  }
}

TEST_CASE("velocity hints go out at 5 Hz with the speed of the path (S13.2 mode c)", "[ghosts][driver]") {
  Rig r;
  r.add(11, line_track(300), Rig::info(11, 2, "Pax", 2));
  r.run(2.0);
  const int before = r.world.hints_total;
  r.run(10.0);
  const int sent = r.world.hints_total - before;
  CHECK(sent >= 45);
  CHECK(sent <= 55);
  REQUIRE(r.world.only() != nullptr);
  REQUIRE(r.world.only()->has_velocity);
  CHECK(std::abs(ghost::length(r.world.only()->velocity) - 300.0) < 3.0);
  // one batch per frame at most
  for (const auto& b : r.world.hint_log) CHECK(b.size() <= 1);
}

TEST_CASE("a parked ghost gets one zero hint, then a rare re-assert", "[ghosts][driver]") {
  Rig r;
  r.add(11, [](double) { return TrackPoint{}; }, Rig::info(11, 2, "Pax", 2));
  r.run(12.0);
  CHECK(r.world.hints_total <= 8);
}

TEST_CASE("a gate jump places the ghost in the new sector within 1 m", "[ghosts][driver][sector]") {
  Rig r;
  const auto track = gate_track(250, 12.0);
  r.add(11, track, Rig::info(11, 2, "Pax", 2));
  r.run(2.0);
  REQUIRE(r.world.objs.size() == 1);
  std::uint64_t last_sector = r.world.objs.begin()->second.sector;
  int changes = 0;
  int checked = 0;
  for (int i = 0; i < 60 * 40; ++i) {
    (void)r.step();
    const auto v = r.driver->views();
    const FakeObj& o = r.world.objs.begin()->second;
    const TrackPoint tp = track(r.track_time_s(v[0].represented_t_us));
    REQUIRE(r.world.objs.size() == 1);  // a sector change is a move, never a respawn
    if (o.sector != last_sector) {
      ++changes;
      last_sector = o.sector;
      CHECK(o.sector == 9000 + tp.sector);                 // the sector of the represented sample
      CHECK(dist(o.pose.pos, tp.pos) < 1.0);               // sector-LOCAL position, not a leftover of the old sector
    }
    // (the up-to-50 ms before a jump hold the last sample: the held pose is not the track at the render time)
    const double into = std::fmod(r.track_time_s(v[0].represented_t_us), 12.0);
    if (o.sector == 9000u + tp.sector && into < 11.9) {
      CHECK(dist(o.pose.pos, tp.pos) < 1.0);
      ++checked;
    }
  }
  CHECK(changes >= 2);  // 40 s of 12 s segments: jumps at ~12, 24, 36 s
  CHECK(checked > 2000);
  CHECK(r.driver->counters().spawned == 1);
  CHECK(r.driver->counters().respawned == 0);
  CHECK(r.driver->counters().sector_changes == static_cast<std::uint64_t>(changes));
}

TEST_CASE("the Hidden flag removes the ghost and the end of it shows it again", "[ghosts][driver][hide]") {
  Rig r;
  r.add(11, line_track(100), Rig::info(11, 2, "Pax", 2));
  r.flags_for = [](std::uint32_t, double t) -> std::uint16_t { return (t >= 5.0 && t < 8.0) ? ghost::kHidden : 0; };
  r.run(4.0);
  REQUIRE(r.world.objs.size() == 1);
  const std::uint64_t first_id = r.world.objs.begin()->first;
  r.run(3.0);  // the hidden window reaches the render time
  CHECK(r.world.objs.empty());
  CHECK(r.driver->registries().ghosts.size() == 0);
  CHECK(r.driver->size() == 1);  // the record stays
  CHECK(r.world.removed_ids.size() == 1);
  CHECK(r.world.removed_ids[0] == first_id);
  r.run(3.0);
  REQUIRE(r.world.objs.size() == 1);
  CHECK(r.world.objs.begin()->first != first_id);
  CHECK(r.world.objs.begin()->second.name == "[MP] Pax");
  CHECK(r.driver->counters().hidden == 1);
  CHECK(r.driver->counters().shown == 1);
  CHECK(r.driver->counters().respawned == 0);  // a deliberate hide is not a respawn
}

TEST_CASE("a parked ghost is labelled offline and relabelled when its player returns", "[ghosts][driver][label]") {
  Rig r;
  r.add(11, [](double) { return TrackPoint{}; }, Rig::info(11, 2, "[MP] Pax", 0));  // parked avatar: controller 0, name as the server sends it
  r.run(1.0);
  REQUIRE(r.world.only() != nullptr);
  CHECK(r.world.only()->name == "[MP] Pax (offline)");
  const std::uint64_t id = r.world.only()->id;
  r.driver->on_change(11, std::nullopt, std::uint16_t{2}, std::nullopt, std::nullopt);  // the player joined: controller = 2
  r.run(0.5);
  CHECK(r.world.only()->name == "[MP] Pax");
  CHECK(r.world.only()->id == id);  // renamed, not respawned
  r.driver->on_change(11, std::nullopt, std::uint16_t{0}, std::nullopt, std::nullopt);  // left again
  r.run(0.5);
  CHECK(r.world.only()->name == "[MP] Pax (offline)");
  CHECK(GhostDriver::make_label("", 7, 7) == "[MP] Player7");
  CHECK(GhostDriver::make_label("Pax (offline)", 3, 3) == "[MP] Pax");
}

TEST_CASE("a pushed ghost is put back on its path", "[ghosts][driver][solid]") {
  Rig r;
  r.add(11, line_track(0.0), Rig::info(11, 2, "Pax", 2));  // parked pose on a line track (speed 0)
  r.run(2.0);
  REQUIRE(r.world.only() != nullptr);
  const std::uint64_t id = r.world.only()->id;
  const Vec3 home = r.world.only()->pose.pos;
  r.world.objs[id].pose.pos = home + Vec3{10, 0, 0};  // the player bumps it
  r.run(0.5);
  CHECK(dist(r.world.objs[id].pose.pos, home) < 0.01);  // back within parked_set_interval
  // a moving ghost is re-placed every frame
  Rig m;
  m.add(11, line_track(100), Rig::info(11, 2, "Pax", 2));
  m.run(2.0);
  const int places0 = m.world.only()->places;
  m.run(1.0);
  CHECK(m.world.only()->places - places0 >= 55);
}

TEST_CASE("a lost dress is repeated until the name is there", "[ghosts][driver][dress]") {
  Rig r;
  r.world.dress_applies = false;  // MD did not get the event
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(8.0);
  REQUIRE(r.world.only() != nullptr);
  CHECK(r.world.only()->name != "[MP] Pax");
  CHECK(r.driver->counters().redressed >= 1);
  r.world.dress_applies = true;
  r.run(7.0);
  CHECK(r.world.only()->name == "[MP] Pax");
}

TEST_CASE("an object the game lost respawns, and a ghost that keeps dying is throttled", "[ghosts][driver][valid]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(2.0);
  REQUIRE(r.world.only() != nullptr);
  r.world.objs.clear();  // destroyed by a local NPC despite the minimum hull: the id is no longer valid
  r.run(2.0);
  REQUIRE(r.world.only() != nullptr);
  CHECK(r.driver->counters().invalid_dropped == 1);
  CHECK(r.driver->counters().respawned == 1);
  for (int k = 0; k < 4; ++k) {
    if (const auto* e = r.driver->registries().ghosts.find(11)) r.world.objs[e->local_id].wrecked = true;  // the wreck stays behind, the ghost id is dropped
    r.run(1.5);
  }
  CHECK(r.driver->counters().respawned <= 3);  // respawn_burst = 3 per 30 s
  CHECK(r.driver->counters().invalid_dropped >= 3);
}

TEST_CASE("spawn failures back off, a missing faction is an error and never a null owner", "[ghosts][driver][spawn]") {
  Rig r;
  r.world.fail_spawns = 2;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(1.0);
  CHECK(r.world.objs.empty());
  CHECK(r.world.spawn_calls == 1);  // backing off for 2 s
  r.run(2.5);
  CHECK(r.world.spawn_calls == 2);
  r.run(2.5);
  REQUIRE(r.world.only() != nullptr);
  CHECK(r.driver->counters().spawn_failed == 2);

  Rig f;
  f.world.factions = {"player", "x4mp_team_2"};
  f.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2, "x4mp_team_1"));
  f.run(5.0);
  CHECK(f.world.objs.empty());
  CHECK(f.world.spawn_calls == 0);
  CHECK(f.driver->counters().faction_missing >= 2);
  f.world.factions.insert("x4mp_team_1");  // M3-08's library appears
  f.run(3.0);
  CHECK(f.world.objs.size() == 1);

  Rig e;
  e.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2, ""));  // no owner at all
  e.run(3.0);
  CHECK(e.world.objs.empty());
  CHECK(e.world.spawn_calls == 0);
}

TEST_CASE("nothing is spawned while the sector is not mapped", "[ghosts][driver][sector]") {
  Rig r;
  r.world.sectors.clear();
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(2.0);
  CHECK(r.world.objs.empty());
  CHECK(r.driver->counters().unmapped_frames > 50);
  r.world.sectors[1] = 9001;
  r.run(0.5);
  CHECK(r.world.objs.size() == 1);
}

TEST_CASE("a ghost never spawns onto the local player's ship", "[ghosts][driver][clearance]") {
  Rig r;
  r.add(11, [](double) { return TrackPoint{}; }, Rig::info(11, 2, "Pax", 2));
  r.world.local_present = true;
  r.world.local_sector = 9001;
  r.world.local_pos = {10, 5, 0};  // 11 m from the ghost's pose (0,0,0)
  r.run(3.0);
  CHECK(r.world.objs.empty());
  CHECK(r.driver->counters().deferred_clearance >= 2);
  CHECK(r.world.remove_calls == 0);  // and the player's ship was not touched
  r.world.local_pos = {500, 0, 0};
  r.run(1.0);
  CHECK(r.world.objs.size() == 1);
}

TEST_CASE("removal rules: only ghosts we spawned, never a guarded id", "[ghosts][driver][remove]") {
  Rig r;
  FakeObj unrelated;  // an NPC ship that happens to exist: nobody may touch it
  unrelated.id = 77;
  unrelated.name = "NPC";
  unrelated.owner = "argon";
  r.world.objs[77] = unrelated;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.add(12, circle_track(1200, 100), Rig::info(12, 3, "Pia", 3));
  r.run(2.0);
  CHECK(r.world.objs.size() == 3);
  r.driver->on_despawn(11, DespawnKind::Remove);
  r.run(0.2);
  CHECK(r.world.objs.size() == 2);
  CHECK(r.driver->size() == 1);
  CHECK(r.driver->registries().ghosts.size() == 1);
  CHECK(r.world.objs.count(77) == 1);
  r.driver->remove_all();
  r.run(0.2);
  CHECK(r.world.objs.size() == 1);
  CHECK(r.world.objs.count(77) == 1);
  CHECK(r.driver->size() == 0);
  CHECK(r.driver->registries().empty());
  for (const std::uint64_t id : r.world.removed_ids) CHECK(id != 77);

  // the world refuses (the id is guarded): the driver forgets the id and does not retry
  Rig g;
  g.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  g.run(2.0);
  REQUIRE(g.world.only() != nullptr);
  g.world.guarded.insert(g.world.only()->id);
  g.flags_for = [](std::uint32_t, double) -> std::uint16_t { return ghost::kHidden; };
  g.run(3.0);
  CHECK(g.driver->counters().blocked_removals == 1);
  CHECK(g.world.remove_calls == 1);
}

TEST_CASE("the own ship is never a ghost", "[ghosts][driver]") {
  Rig r;
  CHECK_FALSE(r.driver->on_spawn(Rig::info(11, 1, "Me", 1), r.now));  // controller == self
  CHECK_FALSE(r.driver->on_spawn(Rig::info(12, 1, "Me", 0), r.now));  // my parked avatar
  CHECK(r.driver->size() == 0);
  CHECK(r.driver->on_spawn(Rig::info(13, 2, "Pax", 0), r.now));  // somebody else's parked avatar
  CHECK(r.driver->size() == 1);
}

TEST_CASE("a refreshed spawn does not duplicate the ghost", "[ghosts][driver]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(2.0);
  for (int i = 0; i < 5; ++i) r.driver->on_spawn(Rig::info(11, 2, "Pax", 2), r.now);  // a resume re-sends every spawn
  r.run(1.0);
  CHECK(r.world.objs.size() == 1);
  CHECK(r.driver->size() == 1);
  CHECK(r.world.spawn_calls == 1);
}

TEST_CASE("a silent ghost (parked, or a quiet link) stays where it is; the server removes ghosts, silence does not", "[ghosts][driver][stale]") {
  Rig r;
  r.add(11, line_track(50), Rig::info(11, 2, "Pax", 2));
  r.run(3.0);
  REQUIRE(r.world.only() != nullptr);
  r.drop_sample = [&](std::uint32_t, double t) { return t > 3.0 && t < 40.0; };
  r.run(36.0);  // > 30 s without a sample reaching the interpolator
  CHECK(r.world.objs.size() == 1);
  CHECK(r.driver->counters().hidden == 0);
  r.run(6.0);
  CHECK(r.world.objs.size() == 1);
  CHECK(r.driver->counters().respawned == 0);
}

TEST_CASE("the spawn state is used when no Replication sample ever arrives", "[ghosts][driver][fallback]") {
  Rig r;
  InitialState st;
  st.sector = 2;
  st.pos = {100, 0, 200};
  r.tracks.clear();
  r.driver->on_spawn(Rig::info(11, 2, "Pax", 0), r.now, st);  // parked avatar, no samples at all
  r.run(0.5);
  CHECK(r.world.objs.empty());  // not yet: Replication normally follows within a moment
  r.run(2.0);
  REQUIRE(r.world.only() != nullptr);
  CHECK(r.world.only()->sector == 9002);
  CHECK(dist(r.world.only()->pose.pos, {100, 0, 200}) < 0.1);
  CHECK(r.world.only()->name == "[MP] Pax (offline)");
}

TEST_CASE("at most two spawns per frame", "[ghosts][driver][budget]") {
  Rig r;
  for (std::uint32_t n = 1; n <= 7; ++n) r.add(n, circle_track(1000.0 + 50.0 * n, 150), Rig::info(n, static_cast<std::uint16_t>(n + 1), "P" + std::to_string(n), static_cast<std::uint16_t>(n + 1)));
  int max_spawns = 0;
  for (int i = 0; i < 400; ++i) max_spawns = std::max(max_spawns, r.step().spawns);
  CHECK(max_spawns <= 2);
  CHECK(r.world.objs.size() == 7);
}

TEST_CASE("an owner change from the server re-owns the object", "[ghosts][driver]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(1.0);
  r.driver->on_change(11, std::nullopt, std::nullopt, std::string("x4mp_team_2"), std::uint16_t{2});
  r.run(0.5);
  CHECK(r.world.only()->owner == "x4mp_team_2");
  CHECK(r.world.owner_calls == 1);
}

// ---------------------------------------------------------------------------------------------------------------------------
// reload / save-load adoption
// ---------------------------------------------------------------------------------------------------------------------------
namespace {
// What the feature does at shutdown + init: save the stash, build a new driver, restore, adopt (needs the universe: same world).
void reload(Rig& r) {
  r.driver->save(r.stash);
  r.make_driver();
  const auto meta = r.stash.get("ghost.meta");
  REQUIRE(meta.has_value());
  REQUIRE(r.driver->restore(*meta, r.now));
  // the Replication streams of the new incarnation start empty; the server's fresh delivery re-sends the spawns
  r.next_sample_s.clear();
  for (auto& [net, tr] : r.tracks) r.next_sample_s[net] = static_cast<double>(r.now - r.base_us) / 1e6;
  r.driver->adopt(r.stash, r.now);
}
}  // namespace

TEST_CASE("reloadui: the ghosts are adopted from the stash, no second spawn", "[ghosts][driver][adopt]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.add(12, line_track(120), Rig::info(12, 3, "Pia", 3));
  r.run(3.0);
  REQUIRE(r.world.objs.size() == 2);
  const auto ids = r.world.objs;
  reload(r);
  CHECK(r.driver->counters().adopted == 2);
  r.run(3.0);
  CHECK(r.world.objs.size() == 2);
  CHECK(r.world.spawn_calls == 2);
  CHECK(r.world.removed_ids.empty());
  for (const auto& [id, o] : ids) CHECK(r.world.objs.count(id) == 1);
  CHECK(r.driver->registries().ghosts.size() == 2);
  // the ghosts keep moving after the adoption
  const Vec3 p0 = r.world.objs.begin()->second.pose.pos;
  r.run(1.0);
  CHECK(dist(p0, r.world.objs.begin()->second.pose.pos) > 1.0);
}

TEST_CASE("20 reloads: 0 leaks, 0 duplicates", "[ghosts][driver][adopt][reload]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.add(12, line_track(120), Rig::info(12, 3, "Pia", 3));
  r.add(13, [](double) { return TrackPoint{}; }, Rig::info(13, 4, "Bob", 0));
  r.run(3.0);
  REQUIRE(r.world.objs.size() == 3);
  for (int i = 0; i < 20; ++i) {
    reload(r);
    r.run(1.0);
    REQUIRE(r.world.objs.size() == 3);
  }
  CHECK(r.world.spawn_calls == 3);
  CHECK(r.world.removed_ids.empty());
  CHECK(r.driver->registries().ghosts.size() == 3);
  CHECK(r.driver->size() == 3);
}

TEST_CASE("save load: ids changed, the ghosts are found again by idcode", "[ghosts][driver][adopt][idcode]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(3.0);
  REQUIRE(r.world.objs.size() == 1);
  // the "save load": every object gets a new id (X4 renumbers components), idcode / name / owner stay
  FakeObj o = r.world.objs.begin()->second;
  r.world.objs.clear();
  o.id = 424242;
  r.world.objs[o.id] = o;
  // an unrelated object now sits at the OLD id: it must be left alone
  FakeObj other;
  other.id = 5000;
  other.name = "NPC freighter";
  other.owner = "argon";
  other.idcode = "ZZZ-999";
  r.world.objs[5000] = other;
  reload(r);
  CHECK(r.driver->counters().adopted == 1);
  r.run(2.0);
  CHECK(r.world.spawn_calls == 1);
  CHECK(r.world.objs.size() == 2);
  CHECK(r.world.objs.count(424242) == 1);
  CHECK(r.world.objs.count(5000) == 1);
  CHECK(r.world.objs[5000].name == "NPC freighter");  // never touched
  CHECK(r.world.removed_ids.empty());
  CHECK(r.driver->registries().ghosts.find(11)->local_id == 424242);
}

TEST_CASE("after a reload a vanished ghost respawns from its samples", "[ghosts][driver][adopt]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(3.0);
  REQUIRE(r.world.objs.size() == 1);
  r.world.objs.clear();  // the new universe has no such object
  reload(r);
  CHECK(r.driver->counters().adopted == 0);
  r.run(3.0);
  CHECK(r.world.objs.size() == 1);
  CHECK(r.world.spawn_calls == 2);
  // the string-table / macro data came back with the record
  CHECK(r.world.only()->macro == "ship_arg_s_fighter_01_a_macro");
  CHECK(r.world.only()->owner == "x4mp_team_1");
}

TEST_CASE("a corrupt stash adopts nothing and leaves foreign objects alone", "[ghosts][driver][adopt]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.run(3.0);
  r.driver->save(r.stash);
  r.stash.m["ghost.registries"] = "garbage";
  r.make_driver();
  REQUIRE(r.driver->restore(*r.stash.get("ghost.meta"), r.now));
  r.next_sample_s[11] = static_cast<double>(r.now - r.base_us) / 1e6;
  r.driver->adopt(r.stash, r.now);
  // the registry blob is unusable: the ghost is found by its idcode instead, no duplicate
  r.run(3.0);
  CHECK(r.world.objs.size() == 1);
  CHECK(r.world.spawn_calls == 1);

  Rig b;
  CHECK_FALSE(b.driver->restore("not a meta file", b.now));
  CHECK_FALSE(b.driver->restore("x4gm 1\nG\t1\t2\n", b.now));
  CHECK(b.driver->size() == 0);
}

TEST_CASE("session end removes every ghost and clears the registry", "[ghosts][driver][remove]") {
  Rig r;
  r.add(11, circle_track(1000, 100), Rig::info(11, 2, "Pax", 2));
  r.add(12, line_track(100), Rig::info(12, 3, "Pia", 3));
  r.run(2.0);
  REQUIRE(r.world.objs.size() == 2);
  r.driver->remove_all();
  r.run(0.1);
  CHECK(r.world.objs.empty());
  CHECK(r.driver->registries().empty());
  CHECK(r.driver->size() == 0);
  // the stash no longer lists them either
  r.driver->save(r.stash);
  Rig b;
  b.world = r.world;
  REQUIRE(b.driver->restore(*r.stash.get("ghost.meta"), b.now));
  CHECK(b.driver->size() == 0);
}

TEST_CASE("sync lines carry the player name and the path error", "[ghosts][driver][sync]") {
  Rig r;
  r.add(11, circle_track(1000, 150), Rig::info(11, 2, "Pax", 2));
  r.run(8.0);
  const auto lines = r.driver->take_sync_lines();
  REQUIRE(lines.size() == 1);
  CHECK(lines[0].rfind("[sync] player=Pax net=11 ", 0) == 0);
  CHECK(lines[0].find("err_p50/p95/max=") != std::string::npos);
  CHECK(lines[0].find("lat_p95=") != std::string::npos);
}
