#include <vector>

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include "core/ghost/replication.h"

using namespace x4mp::ghost;
using namespace x4mp;
using Catch::Approx;

namespace {
std::vector<std::uint8_t> encode(const std::vector<wire::ReplicationEntry>& es) {
  std::vector<std::uint8_t> buf;
  for (const auto& e : es) {
    std::uint8_t tmp[64];
    const auto n = wire::write_replication_entry(wire::MutableByteSpan(tmp, sizeof tmp), e);
    REQUIRE(n.has_value());
    buf.insert(buf.end(), tmp, tmp + *n);
  }
  return buf;
}
wire::ReplicationEntry full(std::uint32_t id, double x, std::int16_t time_ms, std::uint16_t flags = 0) {
  wire::ReplicationEntry e;
  e.net_id = id;
  e.mask = wire::kRepSector | wire::kRepPos | wire::kRepRot | wire::kRepVel | wire::kRepFlags | wire::kRepStatus | wire::kRepTime;
  e.sector = 3;
  e.pos_x = *wire::quantize_position(x);
  e.pos_y = *wire::quantize_position(-5.0);
  e.pos_z = *wire::quantize_position(100.0);
  e.yaw = *wire::quantize_rotation(0.5);
  e.pitch = *wire::quantize_rotation(0.1);
  e.roll = *wire::quantize_rotation(-0.2);
  e.vel_x = *wire::quantize_velocity(25.0, false);
  e.state_flags = flags;
  e.hull = 200;
  e.shield = 100;
  e.time_ms = time_ms;
  return e;
}
}  // namespace

TEST_CASE("decode: full entry -> pose sample in server time", "[ghost][replication]") {
  ReplicationDecoder dec;
  const auto buf = encode({full(11, 1000.0, -30)});
  std::vector<EntityUpdate> out;
  REQUIRE(dec.decode(5'000'000, wire::ByteSpan(buf), 1, [&](const EntityUpdate& u) { out.push_back(u); }).has_value());
  REQUIRE(out.size() == 1);
  const EntityUpdate& u = out[0];
  CHECK(u.net_id == 11);
  CHECK(u.has_pose);
  CHECK(u.sample.t_us == 5'000'000 - 30'000);
  CHECK(u.sample.sector == 3);
  CHECK(u.sample.pos.x == Approx(1000.0).margin(1.0 / 64));
  CHECK(u.sample.pos.y == Approx(-5.0).margin(1.0 / 64));
  CHECK(u.sample.rot.yaw == Approx(0.5).margin(1e-4));
  CHECK(u.sample.vel.x == Approx(25.0).margin(0.25));
  CHECK(u.sample.hull == 200);
}

TEST_CASE("decode: omitted fields come from the baseline; flags-only entries carry no pose", "[ghost][replication]") {
  ReplicationDecoder dec;
  std::vector<EntityUpdate> out;
  auto sink = [&](const EntityUpdate& u) { out.push_back(u); };
  const auto first = encode({full(11, 1000.0, 0)});
  REQUIRE(dec.decode(1'000'000, wire::ByteSpan(first), 1, sink).has_value());

  wire::ReplicationEntry pos_only;  // only POS and TIME
  pos_only.net_id = 11;
  pos_only.mask = wire::kRepPos | wire::kRepTime;
  pos_only.pos_x = *wire::quantize_position(1010.0);
  pos_only.pos_y = *wire::quantize_position(-5.0);
  pos_only.pos_z = *wire::quantize_position(100.0);
  pos_only.time_ms = 0;
  wire::ReplicationEntry flags_only;  // the D1 Hidden bit, no position
  flags_only.net_id = 11;
  flags_only.mask = wire::kRepFlags;
  flags_only.state_flags = kHidden;
  const auto second = encode({pos_only, flags_only});
  out.clear();
  REQUIRE(dec.decode(1'050'000, wire::ByteSpan(second), 2, sink).has_value());
  REQUIRE(out.size() == 2);
  CHECK(out[0].has_pose);
  CHECK(out[0].sample.pos.x == Approx(1010.0).margin(0.02));
  CHECK(out[0].sample.sector == 3);                    // baseline
  CHECK(out[0].sample.rot.yaw == Approx(0.5).margin(1e-4));  // baseline
  CHECK(out[0].sample.vel.x == Approx(25.0).margin(0.25));  // baseline
  CHECK_FALSE(out[1].has_pose);
  CHECK((out[1].sample.flags & kHidden) != 0);
  CHECK(out[1].sample.t_us == 1'050'000);

  dec.forget(11);
  CHECK(dec.tracked() == 0);
}

TEST_CASE("decode: velocity unit follows VelCoarse", "[ghost][replication]") {
  ReplicationDecoder dec;
  wire::ReplicationEntry e = full(12, 0.0, 0, kVelCoarse);
  e.vel_x = *wire::quantize_velocity(2000.0, true);
  const auto buf = encode({e});
  std::vector<EntityUpdate> out;
  REQUIRE(dec.decode(1'000'000, wire::ByteSpan(buf), 1, [&](const EntityUpdate& u) { out.push_back(u); }).has_value());
  CHECK(out[0].sample.vel.x == Approx(2000.0).margin(4.0));
}

TEST_CASE("decode: malformed input is an error, not a crash", "[ghost][replication]") {
  ReplicationDecoder dec;
  auto buf = encode({full(11, 0.0, 0)});
  auto sink = [](const EntityUpdate&) {};
  CHECK_FALSE(dec.decode(1, wire::ByteSpan(buf), 2, sink).has_value());  // count too high
  buf.pop_back();
  CHECK_FALSE(dec.decode(1, wire::ByteSpan(buf), 1, sink).has_value());  // truncated
  const std::vector<std::uint8_t> zero_id{0, 0, 0, 0, 0};
  CHECK_FALSE(dec.decode(1, wire::ByteSpan(zero_id), 1, sink).has_value());
}

TEST_CASE("StreamSet: replication drives an interpolator end to end", "[ghost][replication]") {
  StreamSet set;
  set.ensure(11);
  for (int i = 0; i < 6; ++i) {
    const auto buf = encode({full(11, 1000.0 + 5.0 * i, 0)});
    REQUIRE(set.ingest(1'000'000 + 50'000 * i, wire::ByteSpan(buf), 1, 1'020'000 + 50'000 * i).has_value());
  }
  // untracked ids are decoded but ignored by default
  const auto other = encode({full(99, 5.0, 0)});
  REQUIRE(set.ingest(1'300'000, wire::ByteSpan(other), 1, 1'320'000).has_value());
  CHECK(set.find(99) == nullptr);
  CHECK(set.size() == 1);
  Interpolator* in = set.find(11);
  REQUIRE(in != nullptr);
  CHECK(in->sample_count() == 6);
  const RenderPose p = in->render(1'275'000);  // t = 1.175 s: between samples 3 and 4 (x = 1015..1020)
  CHECK(p.state == PoseState::Interpolating);
  CHECK(p.pos.x == Approx(1017.5).margin(0.1));
  set.erase(11);
  CHECK(set.size() == 0);
  CHECK(set.decoder().tracked() == 1);  // only 99's baseline is left
}
