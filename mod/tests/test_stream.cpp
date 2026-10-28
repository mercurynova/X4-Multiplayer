// M4 wave 2: the capture scheduler, dead reckoning, the world-stream encoders and the streamer itself.
// See docs/m4-plan.md W2 (M4-06..M4-11).

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <map>
#include <span>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <flatbuffers/flatbuffers.h>
#include <x4mp/wire.h>

#include "core/authority/entity_spawn.h"
#include "features/authority/capture_scheduler.h"
#include "features/authority/capture_set.h"
#include "features/authority/dead_reckoning.h"
#include "features/authority/entity_stream.h"
#include "features/authority/entity_wire.h"
#include "features/authority/universe_index.h"
#include "common_generated.h"
#include "message_ids_generated.h"
#include "world_generated.h"

using namespace x4mp::features::auth;
using Catch::Approx;
namespace P = X4MP::Proto;
namespace wire = x4mp::wire;

namespace {

constexpr std::uint16_t T(P::MsgType t) { return static_cast<std::uint16_t>(t); }

struct Recorder {
  std::vector<std::pair<std::uint16_t, std::vector<std::uint8_t>>> control;
  std::vector<std::pair<std::uint16_t, std::vector<std::uint8_t>>> realtime;
  bool fail = false;

  EntityStream::Sink sink() {
    return EntityStream::Sink{
        [this](std::uint16_t type, std::span<const std::uint8_t> payload) {
          control.emplace_back(type, std::vector<std::uint8_t>(payload.begin(), payload.end()));
          return !fail;
        },
        [this](std::uint16_t type, std::span<const std::uint8_t> payload) {
          realtime.emplace_back(type, std::vector<std::uint8_t>(payload.begin(), payload.end()));
          return !fail;
        }};
  }
  std::size_t count(std::uint16_t type) const {
    std::size_t n = 0;
    for (const auto& [t, p] : control) n += t == type ? 1 : 0;
    for (const auto& [t, p] : realtime) n += t == type ? 1 : 0;
    return n;
  }
  const std::vector<std::uint8_t>& first(std::uint16_t type) const {
    for (const auto& [t, p] : control) {
      if (t == type) return p;
    }
    for (const auto& [t, p] : realtime) {
      if (t == type) return p;
    }
    static const std::vector<std::uint8_t> empty;
    return empty;
  }
  void clear() {
    control.clear();
    realtime.clear();
  }
};

struct World {
  std::unordered_map<std::uint64_t, TrackPose> poses;
  UniverseIndex index;
  std::uint32_t next_net = 1;

  // Adds one row and its pose. Returns the net id.
  std::uint32_t add(std::uint16_t sector, std::uint64_t component, const TrackPose& pose, std::uint32_t owner = 2) {
    const std::uint32_t net = next_net++;
    poses[component] = pose;
    index.upsert(net, sector, 1, owner, static_cast<std::uint8_t>(x4mp::authority::SpawnKind::Other), 0, component, 0);
    return net;
  }
  EntityStream::PoseReader reader() {
    return [this](std::uint64_t component, TrackPose& out) {
      const auto it = poses.find(component);
      if (it == poses.end()) return false;
      out = it->second;
      return true;
    };
  }
};

std::vector<P::EntityState> states_of(const std::vector<std::uint8_t>& payload) {
  std::vector<P::EntityState> out;
  const auto* update = flatbuffers::GetRoot<P::WorldUpdate>(payload.data());
  if (update->states() == nullptr) return out;
  for (const auto* s : *update->states()) out.push_back(*s);
  return out;
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
// M4-06: the scheduler

TEST_CASE("stream: a 200-ship sector at 1 Hz sweeps over 20 ticks, never in one burst", "[authority][stream][scheduler]") {
  CaptureScheduler scheduler;
  scheduler.set_rate(12, 1);
  REQUIRE(scheduler.active(12));
  CHECK(scheduler.rate_of(12) == 1);

  std::vector<int> visits(200, 0);
  std::size_t max_slice = 0;
  for (int tick = 0; tick < 20; ++tick) {
    const auto slice = scheduler.next(12, 200);
    CHECK(slice.count == 10);
    max_slice = std::max(max_slice, slice.count);
    for (std::size_t i = 0; i < slice.count; ++i) ++visits[(slice.begin + i) % 200];
  }
  CHECK(max_slice == 10);
  for (const int v : visits) CHECK(v == 1);  // every ship exactly once per second
}

TEST_CASE("stream: a 5 Hz sector visits five times as many entities per tick as a 1 Hz one", "[authority][stream][scheduler]") {
  CaptureScheduler scheduler;
  scheduler.set_rate(12, 1);
  scheduler.set_rate(44, 5);
  scheduler.set_rate(45, 20);

  const auto one_hz = scheduler.next(12, 200);
  const auto five_hz = scheduler.next(44, 200);
  const auto twenty_hz = scheduler.next(45, 200);
  CHECK(one_hz.count == 10);
  CHECK(five_hz.count == 50);
  CHECK(twenty_hz.count == 200);  // everything, every tick

  // A 5 Hz sector completes its sweep in 4 ticks; a 1 Hz one needs 20.
  std::vector<int> visits(200, 0);
  for (int tick = 0; tick < 4; ++tick) {
    const auto slice = scheduler.next(44, 200);
    for (std::size_t i = 0; i < slice.count; ++i) ++visits[(slice.begin + i) % 200];
  }
  for (const int v : visits) CHECK(v == 1);
}

TEST_CASE("stream: a tiny sector still visits one entity per tick, and a rate change keeps the cursor", "[authority][stream][scheduler]") {
  CaptureScheduler scheduler;
  scheduler.set_rate(12, 1);
  const auto slice = scheduler.next(12, 3);
  CHECK(slice.count == 1);
  CHECK(scheduler.cursor_of(12) == 1);

  scheduler.set_rate(12, 20);
  CHECK(scheduler.cursor_of(12) == 1);  // no restart
  const auto fast = scheduler.next(12, 3);
  CHECK(fast.begin == 1);
  CHECK(fast.count == 3);

  scheduler.set_rate(12, 0);
  CHECK_FALSE(scheduler.active(12));
  CHECK(scheduler.next(12, 3).count == 0);
  CHECK(scheduler.next(99, 3).count == 0);
}

// ---------------------------------------------------------------------------------------------------------------------
// M4-07: dead reckoning

TEST_CASE("stream: epsilon grows with distance, rotation wraps, velocity is a finite difference", "[authority][stream][dr]") {
  DeadReckoningConfig cfg;
  CHECK(position_epsilon(0.0, cfg) == Approx(0.5));
  CHECK(position_epsilon(2000.0, cfg) == Approx(0.5));
  CHECK(position_epsilon(26000.0, cfg) == Approx(10.25));
  CHECK(position_epsilon(50000.0, cfg) == Approx(20.0));
  CHECK(position_epsilon(1.0e7, cfg) == Approx(20.0));

  CHECK(angle_delta(3.14159265358979, -3.14159265358979) == Approx(0.0).margin(1e-9));
  CHECK(angle_delta(0.0, 0.1) == Approx(-0.1));

  const TrackPose a{0, 0, 0, 0, 0, 0};
  const TrackPose b{15, 0, 0, 0, 0, 0};
  const auto v = finite_difference(a, 0.0, b, 0.05, cfg);
  REQUIRE(v);
  CHECK(v->x == Approx(300.0));
  CHECK_FALSE(finite_difference(a, 0.0, b, 0.0, cfg));
  CHECK_FALSE(finite_difference(a, 0.0, b, 3.0, cfg));  // a long gap is not a velocity
}

TEST_CASE("stream: a ship flying straight is not resent, a turning one is", "[authority][stream][dr]") {
  DeadReckoningConfig cfg;
  DrInput in;
  in.sent = TrackPose{0, 0, 0, 0, 0, 0};
  in.velocity = TrackVelocity{300, 0, 0};
  in.sent_at_s = 0.0;
  in.observer_distance_m = 1000.0;

  // 20 ticks of straight flight: the client's prediction is exact, so nothing is sent until the 1 s keepalive.
  int sends = 0;
  for (int tick = 1; tick <= 20; ++tick) {
    const double t = tick * 0.05;
    in.now_s = t;
    in.now = TrackPose{300.0 * t, 0, 0, 0, 0, 0};
    const auto reason = classify(in, cfg);
    if (reason != UpdateReason::None) ++sends;
    if (reason == UpdateReason::Keepalive) break;
  }
  CHECK(sends == 1);

  // A turn of 2 degrees is above the 0.5 degree Near threshold.
  in.now_s = 0.05;
  in.now = TrackPose{0, 0, 0, 2.0 * 3.14159265358979 / 180.0, 0, 0};
  CHECK(classify(in, cfg) == UpdateReason::Rotation);
  // A 0.6 degree turn is under the 1 degree Far threshold.
  in.observer_distance_m = 100000.0;
  in.now.yaw = 0.6 * 3.14159265358979 / 180.0;
  CHECK(classify(in, cfg) == UpdateReason::None);

  // A stationary ship sends the keepalive at 1 s Near and 5 s Far.
  in.velocity = TrackVelocity{};
  in.now = in.sent;
  in.now_s = 0.9;
  in.observer_distance_m = 100.0;
  CHECK(classify(in, cfg) == UpdateReason::None);
  in.now_s = 1.0;
  CHECK(classify(in, cfg) == UpdateReason::Keepalive);
  in.observer_distance_m = 100000.0;
  in.now_s = 4.0;
  CHECK(classify(in, cfg) == UpdateReason::None);
  in.now_s = 5.0;
  CHECK(classify(in, cfg) == UpdateReason::Keepalive);

  // A sector or flag change is a discontinuity, always sent.
  in.sent_sector = 12;
  in.now_sector = 44;
  CHECK(classify(in, cfg) == UpdateReason::Sector);
  in.now_sector = 12;
  in.sent_flags = 0;
  in.now_flags = 4;
  CHECK(classify(in, cfg) == UpdateReason::Flags);
}

// ---------------------------------------------------------------------------------------------------------------------
// M4-08: the wire

TEST_CASE("stream: 50 moving ships survive the quantised replication codec byte-exactly", "[authority][stream][wire]") {
  std::vector<StateSample> samples;
  for (std::uint32_t i = 0; i < 50; ++i) {
    StateSample s;
    s.net_id = 1000 + i;
    s.sector = 12;
    s.flags = 0;
    s.px = static_cast<std::int32_t>(i * 640);
    s.py = -static_cast<std::int32_t>(i * 320);
    s.pz = 64;
    s.yaw = static_cast<std::int16_t>(i * 100);
    s.pitch = -50;
    s.roll = 25;
    s.vx = 1200;
    s.vy = -300;
    s.vz = 0;
    samples.push_back(s);
  }

  std::vector<std::uint8_t> entries;
  std::vector<wire::ReplicationEntry> written;
  for (const auto& s : samples) {
    wire::ReplicationEntry e;
    e.net_id = s.net_id;
    e.mask = wire::kRepSector | wire::kRepPos | wire::kRepRot | wire::kRepVel | wire::kRepFlags;
    e.sector = s.sector;
    e.pos_x = s.px;
    e.pos_y = s.py;
    e.pos_z = s.pz;
    e.yaw = s.yaw;
    e.pitch = s.pitch;
    e.roll = s.roll;
    e.vel_x = s.vx;
    e.vel_y = s.vy;
    e.vel_z = s.vz;
    e.state_flags = s.flags;
    written.push_back(e);
    std::vector<std::uint8_t> buf(wire::replication_entry_size(e));
    const auto n = wire::write_replication_entry(buf, e);
    REQUIRE(n);
    entries.insert(entries.end(), buf.begin(), buf.end());
  }

  std::vector<wire::ReplicationEntry> decoded;
  const auto r = wire::decode_replication(entries, samples.size(), [&](const wire::ReplicationEntry& e) { decoded.push_back(e); });
  REQUIRE(r.has_value());
  REQUIRE(decoded.size() == samples.size());
  for (std::size_t i = 0; i < samples.size(); ++i) {
    CHECK(decoded[i].net_id == samples[i].net_id);
    CHECK(decoded[i].sector == samples[i].sector);
    CHECK(decoded[i].pos_x == samples[i].px);
    CHECK(decoded[i].pos_y == samples[i].py);
    CHECK(decoded[i].pos_z == samples[i].pz);
    CHECK(decoded[i].yaw == samples[i].yaw);
    CHECK(decoded[i].vel_x == samples[i].vx);
    CHECK(decoded[i].state_flags == samples[i].flags);
  }
}

TEST_CASE("stream: the four authority messages encode and decode", "[authority][stream][wire]") {
  SECTION("WorldUpdate") {
    std::vector<StateSample> samples(32);
    for (std::size_t i = 0; i < samples.size(); ++i) {
      samples[i].net_id = static_cast<std::uint32_t>(i + 1);
      samples[i].sector = 12;
      samples[i].px = static_cast<std::int32_t>(i);
    }
    const auto payload = encode_world_update(7, 123456, 42.5, samples);
    const auto* update = flatbuffers::GetRoot<P::WorldUpdate>(payload.data());
    CHECK(update->authority_tick() == 7);
    CHECK(update->capture_time_us() == 123456);
    CHECK(update->game_time() == Approx(42.5));
    REQUIRE(update->states() != nullptr);
    CHECK(update->states()->size() == 32);
    CHECK(update->states()->Get(31)->net_id() == 32);
    CHECK(payload.size() <= 1100);  // one datagram once the UDP lane lands (M4-25)
  }
  SECTION("EntityDespawn") {
    const std::vector<DespawnSample> entries{{11, 0, static_cast<std::uint8_t>(DespawnCause::OutOfInterest)},
                                             {12, 9, static_cast<std::uint8_t>(DespawnCause::Destroyed)}};
    const auto payload = encode_entity_despawn(entries);
    const auto* despawn = flatbuffers::GetRoot<P::EntityDespawn>(payload.data());
    REQUIRE(despawn->entries() != nullptr);
    REQUIRE(despawn->entries()->size() == 2);
    CHECK(despawn->entries()->Get(0)->net_id() == 11);
    CHECK(despawn->entries()->Get(0)->reason() == P::DespawnReason::OutOfInterest);
    CHECK(despawn->entries()->Get(1)->killer_net_id() == 9);
    CHECK(despawn->entries()->Get(1)->reason() == P::DespawnReason::Destroyed);
  }
  SECTION("EntityChange") {
    ChangeSample change;
    change.net_id = 77;
    change.fields = 1;  // Owner
    change.owner_ref = 9;
    const auto payload = encode_entity_change(change);
    const auto* decoded = flatbuffers::GetRoot<P::EntityChange>(payload.data());
    CHECK(decoded->net_id() == 77);
    CHECK(decoded->fields() == P::ChangeField::Owner);
    CHECK(decoded->owner_ref() == 9);
  }
  SECTION("EntityStatusBatch") {
    const std::vector<StatusSample> statuses{{1, 100, 200, 0}, {2, 255, 0, 0}};
    const auto payload = encode_entity_status_batch(999, statuses);
    const auto* batch = flatbuffers::GetRoot<P::EntityStatusBatch>(payload.data());
    CHECK(batch->capture_time_us() == 999);
    REQUIRE(batch->statuses() != nullptr);
    REQUIRE(batch->statuses()->size() == 2);
    CHECK(batch->statuses()->Get(0)->hull() == 100);
    CHECK(batch->statuses()->Get(1)->shield() == 0);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// M4-09..11: the streamer

TEST_CASE("stream: a scan pass spawns every entity once, in chunks, before SectorComplete", "[authority][stream][spawn]") {
  World world;
  for (int i = 0; i < 40; ++i) world.add(12, 1000 + i, TrackPose{double(i), 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(3, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);

  stream.step(1.0, 1000, 0.0, set, world.index);

  // 40 records at 32 per message -> two EntitySpawn frames, then the marker.
  CHECK(recorder.count(T(P::MsgType::EntitySpawn)) == 2);
  CHECK(recorder.count(T(P::MsgType::SectorComplete)) == 1);
  std::vector<std::uint32_t> ids;
  for (const auto& [type, payload] : recorder.control) {
    if (type != T(P::MsgType::EntitySpawn)) continue;
    const auto* spawn = flatbuffers::GetRoot<P::EntitySpawn>(payload.data());
    CHECK(spawn->game_time() == Approx(1.0));
    REQUIRE(spawn->entities() != nullptr);
    for (const auto* record : *spawn->entities()) ids.push_back(record->net_id());
  }
  CHECK(ids.size() == 40);
  std::sort(ids.begin(), ids.end());
  CHECK(std::adjacent_find(ids.begin(), ids.end()) == ids.end());  // exactly once
  CHECK(stream.track_count() == 40);

  const auto* complete = flatbuffers::GetRoot<P::SectorComplete>(recorder.first(T(P::MsgType::SectorComplete)).data());
  CHECK(complete->sector() == 12);
  CHECK(complete->epoch() == 3);
  CHECK(complete->entity_count() == 40);

  // A second pass spawns only the entity that is new.
  const auto late = world.add(12, 5000, TrackPose{0, 0, 0, 0, 0, 0});
  (void)late;
  recorder.clear();
  stream.on_scan_complete(12);
  stream.step(1.0, 1050, 0.05, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntitySpawn)) == 1);
  const auto* spawn = flatbuffers::GetRoot<P::EntitySpawn>(recorder.first(T(P::MsgType::EntitySpawn)).data());
  REQUIRE(spawn->entities() != nullptr);
  CHECK(spawn->entities()->size() == 1);
  CHECK(recorder.count(T(P::MsgType::SectorComplete)) == 1);
}

TEST_CASE("stream: every tick emits one WorldUpdate, even with nothing to say", "[authority][stream][clock]") {
  World world;
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {}, {}));
  stream.on_capture_set(set);
  stream.step(2.0, 555, 0.0, set, world.index);
  stream.step(2.05, 1055, 0.05, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::WorldUpdate)) == 2);
  CHECK(states_of(recorder.realtime[0].second).empty());
  CHECK(recorder.realtime[0].second.size() < 100);
}

TEST_CASE("stream: a 200-ship 1 Hz sector is polled once per ship per second, never twice in a tick", "[authority][stream][roundrobin]") {
  World world;
  for (int i = 0; i < 200; ++i) world.add(12, 2000 + i, TrackPose{0, 0, 0, 0, 0, 0});
  Recorder recorder;
  std::unordered_map<std::uint64_t, int> reads;
  auto reader = [&](std::uint64_t component, TrackPose& out) {
    ++reads[component];
    out = world.poses[component];
    return true;
  };
  EntityStream stream(reader, recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);  // spawn pass reads every entity once

  reads.clear();
  for (int tick = 1; tick <= 20; ++tick) {
    reads.clear();
    stream.step(1.0 + tick * 0.05, tick * 50000, tick * 0.05, set, world.index);
    for (const auto& [component, n] : reads) CHECK(n == 1);
    CHECK(reads.size() == 10);
  }
}

TEST_CASE("stream: a ship flying straight is polled but not streamed; a stationary one keyframes", "[authority][stream][updates]") {
  World world;
  const auto mover = world.add(12, 3000, TrackPose{0, 0, 0, 0, 0, 0});
  const auto still = world.add(12, 3001, TrackPose{500, 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  stream.set_observer(12, 0.0, 0.0, 0.0);
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 20}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  const auto polls_after_spawn = stream.stats().polls;
  recorder.clear();

  for (int tick = 1; tick <= 20; ++tick) {
    const double t = tick * 0.05;
    world.poses[3000] = TrackPose{300.0 * t, 0, 0, 0, 0, 0};  // 300 m/s, straight
    stream.step(1.0 + t, tick * 50000, t, set, world.index);
  }

  // 20 ticks of straight flight: the mover is predicted exactly and only its keepalive goes out; the stationary ship
  // keyframes at 1 s. Nothing else is streamed.
  std::size_t entries = 0;
  bool saw_mover = false, saw_still = false;
  for (const auto& [type, payload] : recorder.realtime) {
    if (type != T(P::MsgType::WorldUpdate)) continue;
    for (const auto& state : states_of(payload)) {
      ++entries;
      saw_mover |= state.net_id() == mover;
      saw_still |= state.net_id() == still;
    }
  }
  CHECK(entries <= 4);  // two keepalives, plus the mover's first velocity sample
  CHECK(saw_mover);
  CHECK(saw_still);
  CHECK(stream.stats().polls - polls_after_spawn == 40);  // 2 ships x 20 ticks: polling is cheap, sending is not
}

TEST_CASE("stream: a sector that leaves interest despawns after the 60 s grace, and re-entry cancels it", "[authority][stream][despawn]") {
  World world;
  const auto a = world.add(12, 4000, TrackPose{0, 0, 0, 0, 0, 0});
  const auto b = world.add(12, 4001, TrackPose{1, 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  recorder.clear();

  CaptureSet other;
  REQUIRE(other.apply(2, {{44, 1}}, {}));
  stream.on_capture_set(other);
  world.index.drop_sector(12);  // what AuthorityFlow does when the sector leaves the set

  stream.step(1.0, 50000, 10.0, other, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);  // the grace is running
  CHECK(stream.pending_despawns() == 2);

  // Re-entry inside the grace: no churn.
  stream.on_capture_set(set);
  stream.step(1.0, 60000, 12.0, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);
  CHECK(stream.pending_despawns() == 0);

  // Leave again and wait the grace out.
  stream.on_capture_set(other);
  stream.step(1.0, 70000, 20.0, other, world.index);
  stream.step(1.0, 80000, 79.0, other, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);
  stream.step(1.0, 90000, 81.0, other, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntityDespawn)) == 1);
  const auto* despawn = flatbuffers::GetRoot<P::EntityDespawn>(recorder.first(T(P::MsgType::EntityDespawn)).data());
  REQUIRE(despawn->entries() != nullptr);
  CHECK(despawn->entries()->size() == 2);
  std::vector<std::uint32_t> ids{despawn->entries()->Get(0)->net_id(), despawn->entries()->Get(1)->net_id()};
  std::sort(ids.begin(), ids.end());
  CHECK(ids == std::vector<std::uint32_t>{a, b});
  CHECK(despawn->entries()->Get(0)->reason() == P::DespawnReason::OutOfInterest);
  CHECK(stream.track_count() == 0);

  // Once only.
  recorder.clear();
  stream.step(1.0, 100000, 82.0, other, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);
}

TEST_CASE("stream: a row the scan no longer sees despawns once, with the right reason", "[authority][stream][despawn]") {
  World world;
  const auto gone = world.add(12, 4100, TrackPose{0, 0, 0, 0, 0, 0});
  const auto moved = world.add(12, 4101, TrackPose{1, 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  recorder.clear();

  stream.on_row_removed(gone, true);   // the component no longer exists -> Destroyed
  stream.on_row_removed(moved, false); // still exists, left the sector -> Removed
  stream.step(1.0, 50000, 0.05, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntityDespawn)) == 1);
  const auto* despawn = flatbuffers::GetRoot<P::EntityDespawn>(recorder.first(T(P::MsgType::EntityDespawn)).data());
  REQUIRE(despawn->entries() != nullptr);
  REQUIRE(despawn->entries()->size() == 2);
  std::map<std::uint32_t, P::DespawnReason> reasons;
  for (const auto* entry : *despawn->entries()) reasons[entry->net_id()] = entry->reason();
  CHECK(reasons[gone] == P::DespawnReason::Destroyed);
  CHECK(reasons[moved] == P::DespawnReason::Removed);

  recorder.clear();
  stream.step(1.0, 100000, 0.10, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);
}

TEST_CASE("stream (M5-07): a kill despawns once as Destroyed and carries the killer", "[authority][stream][despawn][m507]") {
  World world;
  const auto victim = world.add(12, 4300, TrackPose{0, 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  recorder.clear();

  stream.on_killed(victim, 777);
  stream.step(1.0, 50000, 0.05, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntityDespawn)) == 1);
  const auto* despawn = flatbuffers::GetRoot<P::EntityDespawn>(recorder.first(T(P::MsgType::EntityDespawn)).data());
  REQUIRE(despawn->entries() != nullptr);
  REQUIRE(despawn->entries()->size() == 1);
  const auto* entry = despawn->entries()->Get(0);
  CHECK(entry->net_id() == victim);
  CHECK(entry->reason() == P::DespawnReason::Destroyed);
  CHECK(entry->killer_net_id() == 777);

  // A kill of net id 0 is a no-op, and the same kill twice is never sent twice by the streamer itself.
  recorder.clear();
  stream.on_killed(0, 777);
  stream.step(1.0, 100000, 0.10, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);
}

TEST_CASE("stream (M5-11): a player's death despawns the avatar once as PlayerLeft", "[authority][stream][despawn][m511]") {
  World world;
  const auto victim = world.add(12, 4300, TrackPose{0, 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  recorder.clear();

  stream.on_player_left(victim);
  stream.step(1.0, 50000, 0.05, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntityDespawn)) == 1);
  const auto* despawn = flatbuffers::GetRoot<P::EntityDespawn>(recorder.first(T(P::MsgType::EntityDespawn)).data());
  REQUIRE(despawn->entries() != nullptr);
  REQUIRE(despawn->entries()->size() == 1);
  const auto* entry = despawn->entries()->Get(0);
  CHECK(entry->net_id() == victim);
  CHECK(entry->reason() == P::DespawnReason::PlayerLeft);
  CHECK(entry->killer_net_id() == 0);

  // Net id 0 is not a player we know: nothing is queued, so a client that never had an avatar sends nothing.
  recorder.clear();
  stream.on_player_left(0);
  stream.step(1.0, 100000, 0.10, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityDespawn)) == 0);
}

TEST_CASE("stream: an owner change emits one EntityChange, a hull change one status entry", "[authority][stream][change]") {
  World world;
  const auto id = world.add(12, 4200, TrackPose{0, 0, 0, 0, 0, 0});
  Recorder recorder;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  recorder.clear();

  // A change for an entity that is not on the wire is dropped (its spawn carries the value).
  stream.on_change(999, 9, 0);
  stream.step(1.0, 50000, 0.05, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityChange)) == 0);

  stream.on_change(id, 9, 0);
  stream.step(1.0, 50000, 0.10, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntityChange)) == 1);
  const auto* change = flatbuffers::GetRoot<P::EntityChange>(recorder.first(T(P::MsgType::EntityChange)).data());
  CHECK(change->net_id() == id);
  CHECK(change->fields() == P::ChangeField::Owner);
  CHECK(change->owner_ref() == 9);

  // Status: the first sample waits for the 1 Hz window, the next identical one only for the 10 s keepalive.
  recorder.clear();
  stream.note_status(id, 254, 200);
  stream.step(1.0, 60000, 0.5, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityStatusBatch)) == 0);
  stream.step(1.0, 70000, 1.1, set, world.index);
  REQUIRE(recorder.count(T(P::MsgType::EntityStatusBatch)) == 1);
  const auto* batch = flatbuffers::GetRoot<P::EntityStatusBatch>(recorder.first(T(P::MsgType::EntityStatusBatch)).data());
  REQUIRE(batch->statuses() != nullptr);
  REQUIRE(batch->statuses()->size() == 1);
  CHECK(batch->statuses()->Get(0)->net_id() == id);
  CHECK(batch->statuses()->Get(0)->hull() == 254);
  CHECK(batch->statuses()->Get(0)->shield() == 200);

  recorder.clear();
  stream.step(1.0, 80000, 2.0, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityStatusBatch)) == 0);
  stream.step(1.0, 90000, 11.2, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntityStatusBatch)) == 1);
}

TEST_CASE("stream: an entity inside a focus sphere is polled every tick", "[authority][stream][focus]") {
  World world;
  const auto near = world.add(12, 4300, TrackPose{100, 0, 0, 0, 0, 0});
  const auto far = world.add(12, 4301, TrackPose{5000, 0, 0, 0, 0, 0});
  Recorder recorder;
  std::unordered_map<std::uint64_t, int> reads;
  auto reader = [&](std::uint64_t component, TrackPose& out) {
    ++reads[component];
    out = world.poses[component];
    return true;
  };
  EntityStream stream(reader, recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {{12, 0.0f, 0.0f, 0.0f, 500.0f, 20}}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);  // spawn + focus sweep
  const auto track = stream.find(near);
  REQUIRE(track != nullptr);
  CHECK(track->in_focus);
  const auto other = stream.find(far);
  REQUIRE(other != nullptr);
  CHECK_FALSE(other->in_focus);

  reads.clear();
  for (int tick = 1; tick <= 10; ++tick) {
    stream.step(1.0, tick * 50000, tick * 0.05, set, world.index);
  }
  CHECK(reads[4300] == 10);  // every tick
  CHECK(reads[4301] <= 6);   // only when the 1 Hz sector slice reaches it
}

TEST_CASE("stream: a failed send is retried, not counted as spawned", "[authority][stream][send]") {
  World world;
  world.add(12, 4400, TrackPose{0, 0, 0, 0, 0, 0});
  Recorder recorder;
  recorder.fail = true;
  EntityStream stream(world.reader(), recorder.sink());
  CaptureSet set;
  REQUIRE(set.apply(1, {{12, 1}}, {}));
  stream.on_capture_set(set);
  stream.on_scan_complete(12);
  stream.step(1.0, 0, 0.0, set, world.index);
  CHECK(stream.track_count() == 0);
  CHECK(stream.stats().send_failed > 0);

  recorder.fail = false;
  recorder.clear();
  stream.step(1.0, 50000, 0.05, set, world.index);
  CHECK(recorder.count(T(P::MsgType::EntitySpawn)) == 1);
  CHECK(recorder.count(T(P::MsgType::SectorComplete)) == 1);
  CHECK(stream.track_count() == 1);
}
