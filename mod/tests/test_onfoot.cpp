// M3b: features/onfoot, the pure parts (docs/research/on-foot-presence.md 3.1-3.3, ADR-046): the room-context parser MD pushes over the
// bridge, the client-matched container id, the send-rate tracker (10 Hz moving, 1 Hz idle, immediate on a room change) and the hub's decoding
// of the relay frames. The live round trip (two nodes, real avatars) is covered by the hostsim kit and by the in-game spikes S10.*.
#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "features/onfoot/onfoot_hub.h"
#include "features/onfoot/onfoot_tracker.h"
#include "features/onfoot/onfoot_types.h"
#include "message_ids_generated.h"
#include "world_generated.h"

using namespace x4mp::features::onfoot;
using Catch::Approx;
namespace P = X4MP::Proto;

namespace {

using Buf = std::vector<std::uint8_t>;

Buf finish(flatbuffers::FlatBufferBuilder& fbb) { return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()}; }

constexpr const char* kStationKey = "S|c3_macro|1200,3400,-50";

std::string full_report(Mode mode = Mode::Walking) {
  return "mode=" + std::to_string(static_cast<int>(mode)) +
         ";cont=500213;outer=500212"
         ";skey=" + kStationKey +
         ";oskey=S|z_macro|0,0,0"
         ";rmacro=room_arg_corridor_01_macro;chain=station_gen_factory_base_01_macro|room_arg_corridor_01_macro"
         ";ax=-2059.785;ay=-1176.006;az=0.1658;kind=2;roomtype=4";
}

RoomContext walking_context(std::uint32_t room_macro_ref = 7, std::int16_t anchor_x = -20598) {
  RoomContext ctx;
  ctx.valid = true;
  ctx.mode = Mode::Walking;
  ctx.container = 500213;
  ctx.outer = 500212;
  ctx.container_net_id = matched_container_id(kStationKey);
  ctx.outer_net_id = matched_container_id("S|z_macro|0,0,0");
  ctx.room_macro_ref = room_macro_ref;
  ctx.path_hash = 0xbeef;
  ctx.anchor_x = anchor_x;
  ctx.anchor_y = -11760;
  ctx.anchor_z = 2;
  ctx.kind = 2;
  ctx.roomtype = 4;
  return ctx;
}

// --- frames, built the way the relay builds them -------------------------------------------------------

Buf onfoot_state(std::uint16_t player_id, P::OnFootMode mode, std::uint32_t container, int cx, int cy, int cz, std::int16_t yaw,
                 std::uint8_t pitch, P::OnFootAnim anim, std::uint32_t seq = 1) {
  flatbuffers::FlatBufferBuilder fbb;
  const P::RoomKey key(0x1234u, -1, 2, 3, 0xbeefu, P::RoomKind::Dynamic, 4);
  const auto root = P::CreateOnFootState(fbb, player_id, seq, 1000, mode, container, 0, &key, 0, 0, 0, cx, cy, cz, yaw, pitch, anim, 0, 0);
  fbb.Finish(root);
  return finish(fbb);
}

Buf presence_roster(bool full, const std::vector<std::uint16_t>& entries, const std::vector<std::uint16_t>& removed = {}) {
  flatbuffers::FlatBufferBuilder fbb;
  std::vector<flatbuffers::Offset<P::PresenceEntry>> rows;
  for (const auto id : entries) {
    P::PresenceEntryBuilder b(fbb);
    b.add_player_id(id);
    b.add_mode(P::OnFootMode::Walking);
    b.add_container_net_id(0x80000001u);
    b.add_roomtype(4);
    rows.push_back(b.Finish());
  }
  const auto ev = fbb.CreateVector(rows);
  const auto rv = fbb.CreateVector(removed);
  P::PresenceRosterBuilder b(fbb);
  b.add_full(full);
  b.add_entries(ev);
  b.add_removed(rv);
  const auto root = b.Finish();
  fbb.Finish(root);
  return finish(fbb);
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------------------------
// the identity: FNV-1a 32 and the matched container id
// ---------------------------------------------------------------------------------------------------------------------------------------

TEST_CASE("onfoot: fnv1a32 matches the reference vectors") {
  CHECK(fnv1a32("") == 0x811c9dc5u);
  CHECK(fnv1a32("a") == 0xe40c292cu);
  CHECK(fnv1a32("foobar") == 0xbf9cf968u);
}

TEST_CASE("onfoot: a matched container id keeps the top bit and never collides with 'no container'") {
  CHECK(matched_container_id("") == 0);
  const auto id = matched_container_id(kStationKey);
  CHECK((id & 0x80000000u) != 0);
  CHECK(id == (0x80000000u | (fnv1a32(kStationKey) & 0x7fffffffu)));
  CHECK(matched_container_id(kStationKey) == id);  // deterministic: two clients must agree
  CHECK(matched_container_id("S|other_macro|0,0,0") != id);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
// the report MD pushes over the bridge
// ---------------------------------------------------------------------------------------------------------------------------------------

TEST_CASE("onfoot: a full room report parses into the wire context") {
  std::string why;
  const auto ctx = parse_room_context(full_report(), &why);
  REQUIRE(ctx.has_value());
  CHECK(why.empty());
  CHECK(ctx->valid);
  CHECK(ctx->mode == Mode::Walking);
  CHECK(ctx->container == 500213u);
  CHECK(ctx->outer == 500212u);
  CHECK(ctx->container_net_id == matched_container_id(kStationKey));
  CHECK(ctx->outer_net_id == matched_container_id("S|z_macro|0,0,0"));
  CHECK(ctx->room_macro_ref == fnv1a32("room_arg_corridor_01_macro"));
  CHECK(ctx->path_hash == static_cast<std::uint16_t>(fnv1a32("station_gen_factory_base_01_macro|room_arg_corridor_01_macro") & 0xffffu));
  CHECK(ctx->anchor_x == -20598);
  CHECK(ctx->anchor_y == -11760);
  CHECK(ctx->anchor_z == 2);
  CHECK(ctx->kind == 2);
  CHECK(ctx->roomtype == 4);
}

TEST_CASE("onfoot: the leave state needs no container") {
  const auto ctx = parse_room_context("mode=0");
  REQUIRE(ctx.has_value());
  CHECK(ctx->valid);
  CHECK(ctx->mode == Mode::None);
  CHECK(ctx->container == 0);
}

TEST_CASE("onfoot: the optional fields default to 'unknown'") {
  const auto ctx = parse_room_context("mode=1;cont=5;skey=k;rmacro=m;ax=0;ay=0;az=0");
  REQUIRE(ctx.has_value());
  CHECK(ctx->outer == 0);
  CHECK(ctx->outer_net_id == 0);
  CHECK(ctx->path_hash == 0);
  CHECK(ctx->kind == 0);
  CHECK(ctx->roomtype == 21);  // X4RoomType NONE: control rooms and dock areas report null
}

TEST_CASE("onfoot: a broken report names the field that failed") {
  std::string why;
  CHECK_FALSE(parse_room_context("", &why).has_value());
  CHECK(why == "mode");
  CHECK_FALSE(parse_room_context("mode=9", &why).has_value());
  CHECK(why == "mode");
  // cont is optional now, so the station key is the next thing a report must carry
  CHECK_FALSE(parse_room_context("mode=1", &why).has_value());
  CHECK(why == "sector");
  CHECK_FALSE(parse_room_context("mode=1;cont=abc", &why).has_value());
  CHECK(why == "cont");
  CHECK_FALSE(parse_room_context("mode=1;sector=sec", &why).has_value());
  CHECK(why == "smacro");
  CHECK_FALSE(parse_room_context("mode=1;sector=sec;smacro=st;sx=x;sy=0;sz=0", &why).has_value());
  CHECK(why == "sx");
  CHECK_FALSE(parse_room_context("mode=1;skey=k", &why).has_value());
  CHECK(why == "rmacro");
  CHECK_FALSE(parse_room_context("mode=1;skey=k;rmacro=m;ax=x;ay=0;az=0", &why).has_value());
  CHECK(why == "ax");
  CHECK_FALSE(parse_room_context("mode=1;skey=k;rmacro=m;ax=0;ay=0;az=0;kind=300", &why).has_value());
  CHECK(why == "kind");
}

TEST_CASE("onfoot: the station key is built from its parts with a 50 m bucket") {
  CHECK(station_match_key("sec_macro", "station_macro", 1200, 3400, -50) == "sec_macro|station_macro|1200,3400,-50");
  CHECK(station_match_key("sec_macro", "station_macro", 1224, 1226, 0) == "sec_macro|station_macro|1200,1250,0");
  CHECK(station_match_key("sec_macro", "station_macro", -1224, -1226, 0) == "sec_macro|station_macro|-1200,-1250,0");
  // sub-millimetre drift between two runs (S10.2) must not move the bucket
  CHECK(station_match_key("s", "t", 1200.0004, 3400.0004, -50.0004) == station_match_key("s", "t", 1200, 3400, -50));
}

TEST_CASE("onfoot: a report that sends the station parts instead of a composed key parses the same way") {
  std::string why;
  const auto ctx = parse_room_context("mode=1;sector=sec_macro;smacro=station_macro;sx=1200.4;sy=3400.2;sz=-50.1"
                                      ";rmacro=room_macro;ax=-2059.785;ay=-1176.006;az=0.1658;kind=1",
                                      &why);
  REQUIRE(ctx.has_value());
  CHECK(ctx->container == 0);  // omitted: the feature reads the player's container itself
  CHECK(ctx->container_net_id == matched_container_id(station_match_key("sec_macro", "station_macro", 1200.4, 3400.2, -50.1)));
  CHECK(ctx->container_net_id == matched_container_id("sec_macro|station_macro|1200,3400,-50"));
  CHECK(ctx->anchor_x == -20598);
  CHECK(ctx->anchor_y == -11760);
  CHECK(ctx->anchor_z == 2);
  CHECK(ctx->kind == 1);
}

TEST_CASE("onfoot: anchors are metres with decimals and clamp instead of failing") {
  const auto a = parse_room_context("mode=1;skey=k;rmacro=m;ax=-2059.785;ay=0.1658;az=12.5");
  REQUIRE(a.has_value());
  CHECK(a->anchor_x == -20598);
  CHECK(a->anchor_y == 2);
  CHECK(a->anchor_z == 125);
  // int16 decimetres reach +-3.27 km; a bigger station clamps so the report is still usable
  const auto b = parse_room_context("mode=1;skey=k;rmacro=m;ax=99999;ay=-99999;az=0");
  REQUIRE(b.has_value());
  CHECK(b->anchor_x == 32767);
  CHECK(b->anchor_y == -32768);
}

TEST_CASE("onfoot: field order in the report does not matter") {
  const auto a = parse_room_context("mode=1;cont=5;skey=k;rmacro=m;ax=1;ay=2;az=3");
  const auto b = parse_room_context("az=3;rmacro=m;cont=5;ay=2;mode=1;skey=k;ax=1");
  REQUIRE(a.has_value());
  REQUIRE(b.has_value());
  CHECK(*a == *b);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
// the send rate
// ---------------------------------------------------------------------------------------------------------------------------------------

namespace {

struct FootSim {
  OnFootTracker tracker;
  std::int64_t now = 1'000'000;
  std::vector<std::pair<std::int64_t, OnFootTracker::Step>> sends;

  OnFootTracker::Step step(const RoomContext& ctx, float x, float y, float z, float yaw = 0) {
    OnFootTracker::Sample s;
    s.ctx = ctx;
    s.cx = x;
    s.cy = y;
    s.cz = z;
    s.yaw = yaw;
    const auto t = tracker.update(s, now);
    if (t.send) sends.emplace_back(now, t);
    return t;
  }
  void run(double seconds, int fps, const std::function<void(double)>& move) {
    const std::int64_t dt = 1'000'000 / fps;
    const std::int64_t end = now + static_cast<std::int64_t>(seconds * 1e6);
    const std::int64_t start = now;
    while (now < end) {
      move(static_cast<double>(now - start) / 1e6);
      now += dt;
    }
  }
  [[nodiscard]] std::size_t count(std::int64_t from, std::int64_t to) const {
    std::size_t n = 0;
    for (const auto& s : sends) n += (s.first >= from && s.first < to) ? 1 : 0;
    return n;
  }
};

}  // namespace

TEST_CASE("onfoot: the first sample is sent at once and is a keyframe") {
  FootSim sim;
  const auto t = sim.step(walking_context(), 1, 0, 2);
  CHECK(t.send);
  CHECK(t.keyframe);
  CHECK(t.teleport);
  CHECK(sim.sends.size() == 1);
}

TEST_CASE("onfoot: standing still keeps a 1 Hz keepalive") {
  FootSim sim;
  sim.step(walking_context(), 1, 0, 2);
  const auto t0 = sim.now;
  for (int i = 0; i < 30; ++i) {
    sim.step(walking_context(), 1, 0, 2);
    sim.now += 100'000;  // 10 fps for 3 s
  }
  // 3 s of standing still: one keepalive per second, not ten (the interval is measured from the last send)
  const auto n = sim.count(t0, t0 + 3'000'000);
  CHECK(n >= 2);
  CHECK(n <= 4);
}

TEST_CASE("onfoot: walking sends at 10 Hz") {
  FootSim sim;
  const auto ctx = walking_context();
  sim.step(ctx, 0, 0, 0);
  const auto t0 = sim.now;
  sim.run(1.0, 60, [&](double sec) { sim.step(ctx, static_cast<float>(sec * 2.0), 0, 0); });  // 2 m/s
  const auto n = sim.count(t0, t0 + 1'000'000);
  CHECK(n >= 9);
  CHECK(n <= 11);
}

TEST_CASE("onfoot: the animation hint follows the mode and the speed") {
  FootSim sim;
  auto ctx = walking_context();
  sim.step(ctx, 0, 0, 0);
  sim.run(0.5, 60, [&](double sec) { sim.step(ctx, static_cast<float>(sec * 1.0), 0, 0); });
  CHECK(sim.sends.back().second.anim == Anim::Walk);

  FootSim fast;
  fast.step(ctx, 0, 0, 0);
  fast.run(0.5, 60, [&](double sec) { fast.step(ctx, static_cast<float>(sec * 8.0), 0, 0); });
  CHECK(fast.sends.back().second.anim == Anim::Run);

  ctx.mode = Mode::Seated;
  FootSim seated;
  seated.step(ctx, 0, 0, 0);
  CHECK(seated.sends.back().second.anim == Anim::Sit);

  ctx.mode = Mode::Conversation;
  FootSim talk;
  talk.step(ctx, 0, 0, 0);
  CHECK(talk.sends.back().second.anim == Anim::Talk);
}

TEST_CASE("onfoot: a room change goes out at once and is flagged as a teleport") {
  FootSim sim;
  sim.step(walking_context(), 1, 0, 2);
  const auto before = sim.sends.size();
  sim.now += 50'000;  // well inside the idle interval
  const auto t = sim.step(walking_context(8, -20500), 1, 0, 2);
  CHECK(t.send);
  CHECK(t.keyframe);
  CHECK(t.teleport);
  CHECK(sim.sends.size() == before + 1);
}

TEST_CASE("onfoot: moving to another container is a keyframe even when the room key matches") {
  FootSim sim;
  sim.step(walking_context(), 1, 0, 2);
  sim.now += 50'000;
  auto other = walking_context();
  other.container = 500543;
  other.container_net_id = matched_container_id("S|other|0,0,0");
  const auto t = sim.step(other, 1, 0, 2);
  CHECK(t.send);
  CHECK(t.keyframe);
}

TEST_CASE("onfoot: leaving the interior falls back to the rare repeat") {
  FootSim sim;
  sim.step(walking_context(), 1, 0, 2);
  RoomContext none;
  none.valid = true;
  none.mode = Mode::None;
  const auto t = sim.step(none, 0, 0, 0);
  CHECK(t.send);
  const auto t0 = sim.now;
  sim.now += 1'000'000;
  CHECK_FALSE(sim.step(none, 0, 0, 0).send);  // 1 Hz would have sent; None waits 5 s
  sim.now = t0 + 5'000'000;
  CHECK(sim.step(none, 0, 0, 0).send);
}

TEST_CASE("onfoot: force_resend makes the next sample a keyframe") {
  FootSim sim;
  sim.step(walking_context(), 1, 0, 2);
  sim.tracker.force_resend();
  const auto t = sim.step(walking_context(), 1, 0, 2);
  CHECK(t.send);
  CHECK(t.keyframe);
}

// ---------------------------------------------------------------------------------------------------------------------------------------
// the hub: what the relay frames turn into
// ---------------------------------------------------------------------------------------------------------------------------------------

TEST_CASE("onfoot: an OnFootState frame becomes a remote avatar in container metres") {
  OnFootHub hub;
  hub.set_own_player_id(1);
  const auto frame = onfoot_state(7, P::OnFootMode::Walking, 0x80000001u, /*cx=*/800, /*cy=*/-208, /*cz=*/32, /*yaw=*/16384, /*pitch=*/255,
                                  P::OnFootAnim::Walk);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), frame, 1);

  REQUIRE(hub.remotes().count(7) == 1);
  const auto& a = hub.remotes().at(7);
  CHECK(a.mode == Mode::Walking);
  CHECK(a.container == 0x80000001u);
  CHECK(a.cx == Approx(12.5f));
  CHECK(a.cy == Approx(-3.25f));
  CHECK(a.cz == Approx(0.5f));
  CHECK(a.yaw == Approx(1.5707964f));
  CHECK(a.pitch == Approx(1.5707964f));
  CHECK(a.anim == Anim::Walk);
  CHECK(a.seq == 1);
  CHECK(hub.counters().states_in == 1);
}

TEST_CASE("onfoot: our own sample and the leave state") {
  OnFootHub hub;
  hub.set_own_player_id(1);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), onfoot_state(1, P::OnFootMode::Walking, 9u, 0, 0, 0, 0, 127, P::OnFootAnim::Idle),
                       1);
  CHECK(hub.remotes().empty());  // never trust the relay: the echo of our own id is ignored

  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), onfoot_state(7, P::OnFootMode::Walking, 9u, 0, 0, 0, 0, 127, P::OnFootAnim::Idle),
                       1);
  REQUIRE(hub.remotes().count(7) == 1);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), onfoot_state(7, P::OnFootMode::None, 0u, 0, 0, 0, 0, 127, P::OnFootAnim::Idle),
                       1);
  CHECK(hub.remotes().empty());  // mode None is the leave state
}

TEST_CASE("onfoot: a presence roster replaces the list and honours the removed ids") {
  OnFootHub hub;
  hub.set_own_player_id(1);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), onfoot_state(7, P::OnFootMode::Walking, 9u, 0, 0, 0, 0, 127, P::OnFootAnim::Idle),
                       1);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PresenceRoster), presence_roster(true, {7, 8}, {7}), 1);
  CHECK(hub.roster_full());
  REQUIRE(hub.roster().size() == 1);
  CHECK(hub.roster().front().player_id == 8);
  CHECK(hub.roster().front().mode == Mode::Walking);
  CHECK(hub.roster().front().container == 0x80000001u);
  CHECK(hub.roster().front().roomtype == 4);
  CHECK(hub.remotes().empty());  // the removed id is dropped from the avatar list too
  CHECK(hub.counters().rosters == 1);
}

TEST_CASE("onfoot: appearance and interaction frames are counted but change nothing yet") {
  OnFootHub hub;
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PlayerAppearance), presence_roster(true, {}), 1);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PlayerInteraction), presence_roster(true, {}), 1);
  CHECK(hub.counters().appearances == 1);
  CHECK(hub.counters().interactions == 1);
  CHECK(hub.remotes().empty());
}

TEST_CASE("onfoot: an empty frame is counted as bad, not trusted") {
  OnFootHub hub;
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), {}, 1);
  CHECK(hub.counters().bad_frames == 1);
  CHECK(hub.remotes().empty());
}

TEST_CASE("onfoot: ending the session forgets the players but keeps the counters and the link") {
  OnFootHub hub;
  OnFootLink link;
  bool sent = false;
  link.send_realtime = [&sent](std::uint16_t, std::span<const std::uint8_t>) {
    sent = true;
    return true;
  };
  hub.set_link(std::move(link));
  hub.set_room_context(walking_context());
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), onfoot_state(7, P::OnFootMode::Walking, 9u, 0, 0, 0, 0, 127, P::OnFootAnim::Idle),
                       1);
  REQUIRE(hub.counters().states_in == 1);
  REQUIRE_FALSE(hub.room().container == 0);

  hub.session_ended();
  CHECK(hub.remotes().empty());
  CHECK(hub.roster().empty());
  CHECK_FALSE(hub.room().valid);
  CHECK(hub.counters().states_in == 1);  // the counters are for the whole run
  CHECK(hub.linked());                   // the join feature clears the link itself
  CHECK(hub.send_realtime(0, {}));       // ... so a late frame still goes out
  CHECK(sent);
}
