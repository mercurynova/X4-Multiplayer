// GhostCore: the wire side of the ghost feature (M3-10): StringTableAdd / EntitySpawn / EntityChange / EntityDespawn / Replication
// decoded into driver calls, the string table text format.
#include <random>

#include <catch2/catch_test_macros.hpp>

#include "core/authority/entity_spawn.h"
#include "features/ghosts/ghost_core.h"
#include "message_ids_generated.h"
#include "rig.h"
#include "session_generated.h"
#include "world_generated.h"

using namespace x4mp;
using namespace x4mp::test;
namespace P = X4MP::Proto;

namespace {
constexpr std::uint16_t kSelf = 1;

std::uint16_t type_of(P::MsgType t) { return static_cast<std::uint16_t>(t); }

std::vector<std::uint8_t> strings_payload(const std::vector<std::tuple<std::uint32_t, P::StringKind, std::string>>& rows) {
  flatbuffers::FlatBufferBuilder fbb;
  std::vector<flatbuffers::Offset<P::StringEntry>> es;
  for (const auto& [i, k, v] : rows) es.push_back(P::CreateStringEntryDirect(fbb, i, k, v.c_str()));
  fbb.Finish(P::CreateStringTableAddDirect(fbb, &es));
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}

std::vector<std::uint8_t> spawn_payload(std::uint32_t net, std::uint16_t controller, std::uint16_t owner_player, std::uint16_t team, std::uint32_t macro_ref,
                                        std::uint32_t owner_ref, const std::string& name, authority::SpawnOrigin origin = authority::SpawnOrigin::PlayerShip,
                                        std::uint16_t sector = 1) {
  authority::EntitySpawnBuilder b(1234.5);
  authority::SpawnEntity e;
  e.net_id = net;
  e.kind = authority::SpawnKind::ShipS;
  e.origin = origin;
  e.macro_ref = macro_ref;
  e.owner_ref = owner_ref;
  e.owner_team = team;
  e.owner_player = owner_player;
  e.controller_player = controller;
  e.name = name;
  e.sector = sector;
  e.px = 64 * 100;
  e.py = 0;
  e.pz = 64 * 200;
  REQUIRE(b.add(e));
  auto r = b.build();
  REQUIRE(r.has_value());
  return *r;
}

std::vector<std::uint8_t> change_payload(std::uint32_t net, P::ChangeField fields, std::uint16_t controller = 0, const char* name = nullptr,
                                         std::uint32_t owner_ref = 0, std::uint16_t team = 0) {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(P::CreateEntityChangeDirect(fbb, 0, net, fields, owner_ref, team, 0, name, 0, 0, P::EntityKind::Unknown, controller));
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}

std::vector<std::uint8_t> despawn_payload(std::uint32_t net, P::DespawnReason reason) {
  flatbuffers::FlatBufferBuilder fbb;
  std::vector<P::DespawnEntry> v{P::DespawnEntry(net, 0, reason)};
  fbb.Finish(P::CreateEntityDespawnDirect(fbb, 0, &v));
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}

std::vector<std::uint8_t> replication_payload(std::uint64_t server_time_us, const std::vector<wire::ReplicationEntry>& entries) {
  std::vector<std::uint8_t> bytes;
  for (const auto& e : entries) {
    std::array<std::uint8_t, 64> buf{};
    const std::size_t n = *wire::write_replication_entry(wire::MutableByteSpan(buf.data(), buf.size()), e);
    bytes.insert(bytes.end(), buf.begin(), buf.begin() + static_cast<std::ptrdiff_t>(n));
  }
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(P::CreateReplicationDirect(fbb, 1, server_time_us, 0.0, static_cast<std::uint16_t>(entries.size()), &bytes));
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}

struct CoreRig {
  FakeWorld world;
  std::vector<std::string> logs;
  GhostCore core;
  std::int64_t now = 5'000'000;
  CoreRig() : core({}, [this](int, const std::string& s) { logs.push_back(s); }) {
    core.driver.bind_world(&world);
    feed(type_of(P::MsgType::StringTableAdd),
         strings_payload({{1, P::StringKind::Macro, "ship_arg_s_fighter_01_a_macro"}, {2, P::StringKind::Faction, "x4mp_team_1"}, {3, P::StringKind::Faction, "player"}}));
  }
  void feed(std::uint16_t type, const std::vector<std::uint8_t>& p) { core.on_frame_message(type, std::span<const std::uint8_t>(p), kSelf, now); }
};
}  // namespace

TEST_CASE("string table: add, replace, text round trip, damaged text", "[ghosts][strings]") {
  StringTable t;
  t.set(1, 1, "ship_arg_s_fighter_01_a_macro");
  t.set(2, 2, "x4mp_team_1");
  t.set(0, 1, "ignored");
  CHECK(t.size() == 2);
  const auto v = t.version();
  t.set(2, 2, "x4mp_team_1");  // unchanged: no version bump
  CHECK(t.version() == v);
  t.set(2, 2, "x4mp_team_2");
  CHECK(t.version() == v + 1);
  CHECK(t.value(2) == "x4mp_team_2");
  CHECK(t.value(9).empty());
  const std::string text = t.to_text();
  StringTable u;
  REQUIRE(u.from_text(text));
  CHECK(u.size() == 2);
  CHECK(u.value(1) == "ship_arg_s_fighter_01_a_macro");
  StringTable bad;
  CHECK_FALSE(bad.from_text("x4gs 1\nS\t1\t1\tx\n"));  // no end line
  CHECK_FALSE(bad.from_text("nonsense"));
  CHECK_FALSE(bad.from_text("x4gs 1\nS\tx\t1\tv\nend\t1\n"));
  CHECK(bad.size() == 0);
}

TEST_CASE("EntitySpawn of a player ship makes a ghost record with macro, owner and label", "[ghosts][messages]") {
  CoreRig r;
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(11, 2, 2, 1, 1, 2, "[MP] Pax"));
  CHECK(r.core.counters.spawn_player_ships == 1);
  REQUIRE(r.core.driver.size() == 1);
  const auto v = r.core.driver.views();
  CHECK(v[0].net_id == 11);
  CHECK(v[0].player_id == 2);
  CHECK(v[0].label == "[MP] Pax");
  // the sample-less ghost appears from the spawn state after the fallback delay (the Replication stream normally comes first)
  for (int i = 0; i < 120; ++i) {
    r.now += 16'667;
    (void)r.core.driver.frame(r.now);
  }
  REQUIRE(r.world.only() != nullptr);
  CHECK(r.world.only()->macro == "ship_arg_s_fighter_01_a_macro");
  CHECK(r.world.only()->owner == "x4mp_team_1");
  CHECK(r.world.only()->sector == 9001);
  CHECK(std::abs(r.world.only()->pose.pos.x - 100.0) < 0.1);
  CHECK(std::abs(r.world.only()->pose.pos.z - 200.0) < 0.1);
}

TEST_CASE("EntitySpawn: non-player entities, the own ship and unresolved strings", "[ghosts][messages]") {
  CoreRig r;
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(21, 0, 0, 0, 1, 2, "NPC", authority::SpawnOrigin::AuthorityRuntime));
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(22, 0, 0, 0, 1, 2, "Station", authority::SpawnOrigin::Manifest));
  CHECK(r.core.driver.size() == 0);
  CHECK(r.core.counters.spawn_ignored == 2);
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(23, kSelf, kSelf, 1, 1, 2, "[MP] Me"));      // my own avatar
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(24, 0, kSelf, 1, 1, 2, "[MP] Me (parked)"));  // my parked avatar
  CHECK(r.core.driver.size() == 0);
  // the faction string says `player`: never used as the ghost owner; the team faction is derived from owner_team
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(25, 3, 3, 1, 1, 3, "[MP] Pia"));
  REQUIRE(r.core.driver.size() == 1);
  // a macro the table does not know: the record exists, the spawn never happens (error logged), nothing crashes
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(26, 4, 4, 1, 99, 2, "[MP] Bob"));
  CHECK(r.core.counters.spawn_unresolved == 1);
  for (int i = 0; i < 400; ++i) {
    r.now += 16'667;
    (void)r.core.driver.frame(r.now);
  }
  CHECK(r.world.objs.size() == 1);  // only Pia (Bob has no macro)
  CHECK(r.world.only()->owner == "x4mp_team_1");
}

TEST_CASE("EntityChange: controller, name, owner; EntityDespawn: remove vs hide", "[ghosts][messages]") {
  CoreRig r;
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(11, 2, 2, 1, 1, 2, "[MP] Pax"));
  r.feed(type_of(P::MsgType::EntityChange), change_payload(11, P::ChangeField::Controller, 0));
  CHECK(r.core.driver.views()[0].label == "[MP] Pax (offline)");
  r.feed(type_of(P::MsgType::EntityChange), change_payload(11, P::ChangeField::Controller, 2));
  CHECK(r.core.driver.views()[0].label == "[MP] Pax");
  r.feed(type_of(P::MsgType::EntityChange), change_payload(11, P::ChangeField::Name, 0, "[MP] Paxton"));
  CHECK(r.core.driver.views()[0].label == "[MP] Paxton");
  r.feed(type_of(P::MsgType::EntityChange), change_payload(99, P::ChangeField::Name, 0, "[MP] Nobody"));  // unknown net_id: ignored
  CHECK(r.core.driver.size() == 1);
  r.feed(type_of(P::MsgType::EntityDespawn), despawn_payload(11, P::DespawnReason::DockedInside));
  CHECK(r.core.driver.size() == 1);  // hidden, not forgotten
  r.feed(type_of(P::MsgType::EntityDespawn), despawn_payload(11, P::DespawnReason::Removed));
  CHECK(r.core.driver.size() == 0);
  r.feed(type_of(P::MsgType::EntityDespawn), despawn_payload(11, P::DespawnReason::Removed));  // twice: harmless
  CHECK(r.core.counters.despawn == 3);
}

TEST_CASE("Replication reaches the interpolator of a tracked ghost only", "[ghosts][messages]") {
  CoreRig r;
  r.feed(type_of(P::MsgType::EntitySpawn), spawn_payload(11, 2, 2, 1, 1, 2, "[MP] Pax"));
  wire::ReplicationEntry e;
  e.net_id = 11;
  e.mask = wire::kRepSector | wire::kRepPos | wire::kRepFlags;
  e.sector = 3;
  e.pos_x = 64 * 10;
  e.state_flags = 0;
  wire::ReplicationEntry other = e;
  other.net_id = 12;  // not a ghost of ours
  r.feed(type_of(P::MsgType::Replication), replication_payload(static_cast<std::uint64_t>(r.now), {e, other}));
  CHECK(r.core.counters.replication == 1);
  CHECK(r.core.counters.replication_entries == 2);
  REQUIRE(r.core.driver.streams().find(11) != nullptr);
  CHECK(r.core.driver.streams().find(11)->sample_count() == 1);
  CHECK(r.core.driver.streams().find(12) == nullptr);
}

TEST_CASE("stream clock: the render clock follows the Replication time base, not the node's server clock", "[ghosts][messages][clock]") {
  CoreRig r;
  wire::ReplicationEntry e;
  e.net_id = 11;
  e.mask = wire::kRepSector | wire::kRepPos;
  e.sector = 1;
  // the authority's capture clock is 7.3 s BEHIND this node's server-clock estimate, latency 20..60 ms
  const std::int64_t skew = 7'300'000;
  for (int i = 0; i < 40; ++i) {
    r.now += 50'000;
    const std::int64_t latency = 20'000 + (i % 5) * 10'000;
    const std::int64_t arrival = r.now + latency;
    r.core.on_frame_message(type_of(P::MsgType::Replication), replication_payload(static_cast<std::uint64_t>(r.now - skew), {e}), kSelf, arrival);
  }
  // bias = min(arrival - reference) = skew + 20 ms
  CHECK(r.core.stream_clock.ready());
  CHECK(r.core.stream_clock.bias_us() == skew + 20'000);
  StreamClock empty;
  CHECK(empty.bias_us() == 0);
  CHECK_FALSE(empty.ready());
}

TEST_CASE("damaged frames never crash and are counted", "[ghosts][messages][fuzz]") {
  CoreRig r;
  std::mt19937 rng(7);
  const P::MsgType types[] = {P::MsgType::StringTableAdd, P::MsgType::EntitySpawn, P::MsgType::EntityChange, P::MsgType::EntityDespawn, P::MsgType::Replication};
  for (int i = 0; i < 2000; ++i) {
    std::vector<std::uint8_t> junk(static_cast<std::size_t>(rng() % 96));
    for (auto& b : junk) b = static_cast<std::uint8_t>(rng());
    r.feed(type_of(types[rng() % 5]), junk);
  }
  // truncations of a good message
  const auto good = spawn_payload(11, 2, 2, 1, 1, 2, "[MP] Pax");
  for (std::size_t n = 0; n < good.size(); ++n) r.feed(type_of(P::MsgType::EntitySpawn), std::vector<std::uint8_t>(good.begin(), good.begin() + static_cast<std::ptrdiff_t>(n)));
  CHECK(r.core.counters.bad_frames > 100);
  CHECK(GhostCore::wants(type_of(P::MsgType::Replication)));
  CHECK_FALSE(GhostCore::wants(type_of(P::MsgType::ChatMessage)));
  CHECK_FALSE(r.core.on_frame_message(type_of(P::MsgType::ChatMessage), {}, kSelf, r.now));
}
