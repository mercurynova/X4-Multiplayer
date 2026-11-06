// M3b: features/onfoot, the pure parts (docs/research/on-foot-presence.md 3.1-3.3, ADR-046): the room-context parser MD pushes over the
// bridge, the client-matched container id, the send-rate tracker (20 Hz moving, 1 Hz idle, immediate on a room change) and the hub's decoding
// of the relay frames. The live round trip (two nodes, real avatars) is covered by the hostsim kit and by the in-game spikes S10.*.
#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <map>
#include <string>
#include <string_view>
#include <vector>

#include "features/onfoot/onfoot_actors.h"
#include "features/onfoot/onfoot_hub.h"
#include "features/onfoot/onfoot_presence.h"
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
  ctx.room_macro = "room_arg_corridor_01_macro";
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

TEST_CASE("onfoot: an abrupt stop goes out at once, not one idle interval later") {
  FootSim sim;
  const auto ctx = walking_context();
  sim.step(ctx, 0, 0, 0);
  float z = 0;
  for (int i = 0; i < 10; ++i) {  // 10 fps, 0.2 m per call: every call is a moving one
    sim.now += 100'000;
    z -= 0.2f;
    sim.step(ctx, 0, 0, z);
  }
  const auto before = sim.sends.size();
  // The first call that brings no movement at all must send at once; 16.7 ms is far inside the 100 ms moving interval, so only the
  // moving -> still transition can be the reason.
  sim.now += 16'667;
  const auto stop = sim.step(ctx, 0, 0, z);
  CHECK(stop.send);
  CHECK(sim.sends.size() == before + 1);
  // ... and the very next frame is silent again, so a stop does not turn into a 60 Hz stream.
  sim.now += 16'667;
  CHECK_FALSE(sim.step(ctx, 0, 0, z).send);
}

TEST_CASE("onfoot: walking sends at 20 Hz") {
  FootSim sim;
  const auto ctx = walking_context();
  sim.step(ctx, 0, 0, 0);
  const auto t0 = sim.now;
  sim.run(1.0, 100, [&](double sec) { sim.step(ctx, static_cast<float>(sec * 2.0), 0, 0); });  // 2 m/s, 10 ms steps
  const auto n = sim.count(t0, t0 + 1'000'000);
  CHECK(n >= 18);
  CHECK(n <= 22);
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

TEST_CASE("onfoot: a body of another type sent as an appearance is not a look, an interaction frame is only counted") {
  OnFootHub hub;
  hub.set_own_player_id(1);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PlayerAppearance), presence_roster(true, {}), 1);
  CHECK(hub.appearances().empty());  // a roster body is not a look: nothing is stored under any player id
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PlayerInteraction), {}, 1);
  CHECK(hub.counters().bad_frames == 1);  // an interaction needs a body; an empty one is not a wave
  CHECK(hub.take_interactions().empty());
  CHECK(hub.remotes().empty());
}

TEST_CASE("onfoot: a wave goes out on the control lane and comes back as an interaction") {
  OnFootHub hub;
  hub.set_own_player_id(1);

  std::vector<std::uint8_t> wire;
  std::uint16_t lane_type = 0;
  OnFootLink link;
  link.send_control = [&wire, &lane_type](std::uint16_t type, std::span<const std::uint8_t> payload) {
    lane_type = type;
    wire.assign(payload.begin(), payload.end());
    return true;
  };
  hub.set_link(std::move(link));

  REQUIRE(hub.send_interaction(7, static_cast<std::uint8_t>(P::InteractionKind::Wave)));
  CHECK(lane_type == static_cast<std::uint16_t>(P::MsgType::PlayerInteraction));  // Control, not Realtime
  const auto* out = flatbuffers::GetRoot<P::PlayerInteraction>(wire.data());
  REQUIRE(out != nullptr);
  CHECK(out->from_player() == 0);  // the relay stamps the sender
  CHECK(out->to_player() == 7);
  CHECK(out->kind() == P::InteractionKind::Wave);
  CHECK(hub.counters().interactions == 0);  // sending is not receiving

  flatbuffers::FlatBufferBuilder fbb;
  const auto msg = P::CreatePlayerInteraction(fbb, 7, 1, P::InteractionKind::Wave, 0, 0);
  fbb.Finish(msg);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PlayerInteraction), {fbb.GetBufferPointer(), fbb.GetSize()}, 1);
  const auto got = hub.take_interactions();
  REQUIRE(got.size() == 1);
  CHECK(got[0].from == 7);
  CHECK(got[0].kind == 0);
  CHECK(hub.take_interactions().empty());  // drained, so a second frame does not replay it

  flatbuffers::FlatBufferBuilder own;
  const auto self = P::CreatePlayerInteraction(own, 1, 1, P::InteractionKind::Wave, 0, 0);
  own.Finish(self);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::PlayerInteraction), {own.GetBufferPointer(), own.GetSize()}, 1);
  CHECK(hub.take_interactions().empty());  // the relay never sends a wave from this very player

  hub.clear_link();
  CHECK_FALSE(hub.send_interaction(7, 0));
  CHECK_FALSE(hub.send_interaction(0, 0));
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

// ---------------------------------------------------------------------------------------------------------------------------------------
// W4: the room key and the look on the avatar, and the remote-avatar pipeline
// ---------------------------------------------------------------------------------------------------------------------------------------

namespace {

constexpr std::uint16_t kSelf = 1;

// A frame from a player who stands in the room walking_context() describes, so same_room() is true.
Buf onfoot_state_in_room(std::uint16_t player_id, int px, int py, int pz, std::int16_t yaw = 0, std::uint32_t seq = 1,
                         P::OnFootMode mode = P::OnFootMode::Walking) {
  flatbuffers::FlatBufferBuilder fbb;
  const P::RoomKey key(7u, -20598, -11760, 2, 0xbeefu, P::RoomKind::Dynamic, 4);
  const auto root = P::CreateOnFootState(fbb, player_id, seq, 1000, mode, walking_context().container_net_id, 0, &key, px, py, pz, 0,
                                         0, 0, yaw, 127, P::OnFootAnim::Idle, 0, 0);
  fbb.Finish(root);
  return finish(fbb);
}

Buf appearance_frame(std::uint16_t player_id, std::uint32_t revision, const char* race) {
  flatbuffers::FlatBufferBuilder fbb;
  const auto root = P::CreatePlayerAppearance(fbb, player_id, revision, fbb.CreateString(race), fbb.CreateString("male_a"), 0,
                                              false, 0);
  fbb.Finish(root);
  return finish(fbb);
}

// The pipeline with both side effects captured instead of performed.
struct Stage {
  struct Place {
    std::uint64_t actor;
    float x, y, z, yaw;
  };
  ActorRegistry reg;
  std::vector<std::string> batches;
  std::vector<Place> places;
  std::map<std::uint16_t, std::string> names;
  struct Diag {
    std::uint16_t pid;
    float lag_m, speed_mps;
    std::int64_t age_us;
    float dist_m;
  };
  std::vector<Diag> diags;
  struct Walk {
    std::uint16_t pid;
    float speed_mps;
  };
  std::vector<Walk> walks;
  std::vector<std::uint16_t> stops;
  // The room catalogue the feature keeps from MD (M3c): hash -> macro id. Empty unless a test seeds it.
  std::map<std::uint32_t, std::string> roomIds;

  // The look the hub has for each peer (ADR-051). Every normal session sends one before/with the first state, so `step` models that;
  // a test that wants the spawn/appearance race calls step_bare().
  static std::map<std::uint16_t, Appearance> known_looks(const std::map<std::uint16_t, RemoteAvatar>& remotes) {
    std::map<std::uint16_t, Appearance> looks;
    for (const auto& [pid, avatar] : remotes) {
      if (avatar.mode == Mode::None) continue;
      looks[pid].race = "teladi";
      looks[pid].character_macro = "character_teladi_male_generic_01_macro";
    }
    return looks;
  }

  Stage() {
    ActorCallbacks cb;
    cb.raise_lua = [this](std::string_view json) { batches.emplace_back(json); };
    cb.place = [this](std::uint64_t actor, float x, float y, float z, float yaw) { places.push_back({actor, x, y, z, yaw}); };
    cb.name = [this](std::uint16_t pid) {
      const auto it = names.find(pid);
      return it == names.end() ? std::string() : it->second;
    };
    cb.room_id = [this](std::uint32_t macro_ref) {
      const auto it = roomIds.find(macro_ref);
      return it == roomIds.end() ? std::string() : it->second;
    };
    cb.diag = [this](std::uint16_t pid, float lag_m, float speed_mps, std::int64_t age_us, float dist_m) {
      diags.push_back({pid, lag_m, speed_mps, age_us, dist_m});
    };
    cb.walk = [this](std::uint16_t pid, float speed_mps) { walks.push_back({pid, speed_mps}); };
    cb.stop_walk = [this](std::uint16_t pid) { stops.push_back(pid); };
    reg.set_callbacks(std::move(cb));
  }

  void step(const std::map<std::uint16_t, RemoteAvatar>& remotes, std::int64_t now_us,
            const std::map<std::uint16_t, Appearance>& l = {}) {
    auto looks = known_looks(remotes);
    for (const auto& [pid, look] : l) looks[pid] = look;
    reg.update(walking_context(), remotes, looks, now_us);
  }
  // The peer's look is not here yet (the spawn/appearance race): no appearance at all.
  void step_bare(const std::map<std::uint16_t, RemoteAvatar>& remotes, std::int64_t now_us) {
    reg.update(walking_context(), remotes, {}, now_us);
  }
};

// px/py/pz are room-local metres; the room is the one walking_context() describes (anchor -2059.8 / -1176.0 / 0.2 m).
RemoteAvatar avatar_in_room(float px, float py, float pz, std::uint32_t seq = 1, std::int64_t at_us = 1'000'000) {
  RemoteAvatar a;
  a.mode = Mode::Walking;
  a.container = walking_context().container_net_id;
  a.room = RoomRef{7u, -20598, -11760, 2, 0xbeefu, 2, 4};
  a.px = px;
  a.py = py;
  a.pz = pz;
  a.anim = Anim::Idle;
  a.seq = seq;
  a.at_us = at_us;
  return a;
}

}  // namespace

TEST_CASE("onfoot: the hub keeps the room key and the room-local position of a remote avatar") {
  OnFootHub hub;
  hub.set_own_player_id(kSelf);
  hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::OnFootState), onfoot_state_in_room(7, 1024, -512, 256, 16384), kSelf);
  REQUIRE(hub.remotes().count(7) == 1);
  const auto& a = hub.remotes().at(7);
  CHECK(a.room.macro_ref == 7u);
  CHECK(a.room.anchor_x == -20598);
  CHECK(a.room.anchor_y == -11760);
  CHECK(a.room.anchor_z == 2);
  CHECK(a.room.path_hash == 0xbeefu);
  CHECK(a.room.kind == 2);
  CHECK(a.room.roomtype == 4);
  CHECK(a.px == Approx(1.0f));
  CHECK(a.py == Approx(-0.5f));
  CHECK(a.pz == Approx(0.25f));
  CHECK(same_room(a, walking_context()));
}

TEST_CASE("onfoot: same_room needs the container, the room macro and the anchor to agree") {
  const auto a = avatar_in_room(0, 0, 0);
  CHECK(same_room(a, walking_context()));

  auto other = a;
  other.room.anchor_x = -20599;  // the same macro, a second copy of the room in the same station
  CHECK_FALSE(same_room(other, walking_context()));

  other = a;
  other.room.macro_ref = 8u;
  CHECK_FALSE(same_room(other, walking_context()));

  other = a;
  other.container = 0x80000002u;
  CHECK_FALSE(same_room(other, walking_context()));

  other = a;
  other.room = RoomRef{};
  CHECK_FALSE(same_room(other, walking_context()));

  auto local = walking_context();
  local.mode = Mode::None;
  CHECK_FALSE(same_room(a, local));
}

TEST_CASE("onfoot: the look table keeps the newest appearance and drops an older revision") {
  OnFootHub hub;
  hub.set_own_player_id(kSelf);
  const auto type = static_cast<std::uint16_t>(P::MsgType::PlayerAppearance);
  hub.on_frame_message(type, appearance_frame(7, 5, "teladi"), kSelf);
  hub.on_frame_message(type, appearance_frame(7, 3, "argon"), kSelf);  // late frame of an older look
  REQUIRE(hub.appearance(7) != nullptr);
  CHECK(hub.appearance(7)->race == "teladi");
  CHECK(hub.appearance(7)->preset == "male_a");
  CHECK(hub.appearance(7)->revision == 5);
  CHECK(hub.counters().appearances == 1);  // the stale one is not counted

  hub.on_frame_message(type, appearance_frame(7, 6, "split"), kSelf);
  CHECK(hub.appearance(7)->race == "split");

  hub.on_frame_message(type, appearance_frame(kSelf, 9, "argon"), kSelf);
  CHECK(hub.appearance(kSelf) == nullptr);  // the relay never echoes our own look back at us
}

TEST_CASE("onfoot: a remote player in the same room is spawned once, in room metres at foot height") {
  Stage st;
  st.names[7] = "Ann";
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(1.0f, 2.0f, 3.0f);
  std::map<std::uint16_t, Appearance> looks;
  looks[7].race = "teladi";
  st.step(remotes, 1'000'000, looks);

  REQUIRE(st.batches.size() == 1);
  const auto& j = st.batches.front();
  CHECK(j.find("\"pid\":7") != std::string::npos);
  CHECK(j.find("\"name\":\"Ann\"") != std::string::npos);
  CHECK(j.find("\"race\":\"teladi\"") != std::string::npos);
  CHECK(j.find("\"rmacro\":\"room_arg_corridor_01_macro\"") != std::string::npos);
  CHECK(j.find("\"ax\":-20598") != std::string::npos);
  // Room metres, no anchor added: add_actor_to_room positions inside the room (the anchor is metadata for the room lookup only).
  CHECK(j.find("\"x\":1.000") != std::string::npos);
  CHECK(j.find("\"y\":0.280") != std::string::npos);  // 2.0 m eye - 1.72 m eye height = feet
  CHECK(j.find("\"z\":3.000") != std::string::npos);
  CHECK(st.reg.counters().spawns == 1);
  CHECK(st.reg.tracked() == 1);
  CHECK(st.reg.live() == 0);  // asked, not confirmed
  CHECK(st.places.empty());
}

TEST_CASE("onfoot: a spawn waits briefly for the peer's look, so the character does not change on every load") {
  // Live symptom: the actor was created from the spawn batch before the appearance frame arrived, so MD picked a random crew NPC,
  // and the next respawn picked a different one. The spawn now waits for the look instead of racing it.
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step_bare(remotes, 1'000'000);  // no look yet
  CHECK(st.batches.empty());
  st.step_bare(remotes, 2'000'000);  // still inside kAppearanceWaitUs
  CHECK(st.batches.empty());

  st.step(remotes, 2'100'000);  // the look arrives (Stage's default): spawn at once, with it
  REQUIRE(st.batches.size() == 1);
  CHECK(st.batches.front().find("\"macro\":\"character_teladi_male_generic_01_macro\"") != std::string::npos);
  CHECK(st.batches.front().find("\"race\":\"teladi\"") != std::string::npos);
}

TEST_CASE("onfoot: a peer that never sends a look is still spawned, as a race-only actor") {
  // The wait must not hide a player forever: an appearance never arrives (a peer on an older build), so after the window the
  // spawn goes out anyway and MD falls back to a crew NPC of the announced race.
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step_bare(remotes, 1'000'000);
  st.step_bare(remotes, 4'500'000);  // past kAppearanceWaitUs
  REQUIRE(st.batches.size() == 1);
  CHECK(st.reg.counters().spawns == 1);
  CHECK(st.batches.front().find("\"macro\":\"\"") != std::string::npos);  // MD picks a crew NPC of the race
}

TEST_CASE("onfoot: an unanswered spawn is retried every two seconds, not every frame") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  REQUIRE(st.batches.size() == 1);
  for (std::int64_t t = 1'100'000; t < 2'900'000; t += 100'000) st.step(remotes, t);
  CHECK(st.batches.size() == 1);  // no answer yet: silence
  st.step(remotes, 3'000'000);
  CHECK(st.batches.size() == 2);
  CHECK(st.reg.counters().spawns == 2);
}

TEST_CASE("onfoot: MD's answer makes the actor live and the pose is written every frame") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(1.0f, 2.0f, 3.0f);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  CHECK(st.reg.live() == 1);

  st.step(remotes, 1'100'000);
  REQUIRE_FALSE(st.places.empty());
  const auto& p = st.places.back();
  CHECK(p.actor == 9001u);
  CHECK(p.x == Approx(1.0f).margin(0.01f));
  CHECK(p.y == Approx(0.28f).margin(0.01f));
  CHECK(p.z == Approx(3.0f).margin(0.01f));
  CHECK(p.yaw == Approx(0.0f).margin(1e-4f));

  st.step(remotes, 1'200'000);  // no new sample, the pose is still written
  CHECK(st.places.size() >= 2);
  CHECK(st.reg.counters().places >= 2);
}

TEST_CASE("onfoot: a walking avatar is handed to MD, and placement resumes when it stops") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  auto a = avatar_in_room(1.0f, 2.0f, 3.0f);
  a.anim = Anim::Walk;
  remotes[7] = a;
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  REQUIRE(st.reg.live() == 1);

  // First live frame: the walk is issued and the pose is withheld, or the per-frame override would kill the animation.
  st.step(remotes, 1'100'000);
  REQUIRE(st.walks.size() == 1);
  CHECK(st.walks.front().pid == 7);
  CHECK(st.stops.empty());
  CHECK(st.places.empty());

  // Still inside the hold: no placement, no second command yet.
  st.step(remotes, 1'200'000);
  CHECK(st.walks.size() == 1);
  CHECK(st.places.empty());

  // The hold is longer than the command interval on purpose: placement must NOT resume between two legs (that snap at 2.5 Hz is
  // the visible jerk). 400 ms after the first command the walk is re-issued and the actor is left to the engine.
  st.step(remotes, 1'300'000);
  CHECK(st.walks.size() == 1);
  CHECK(st.places.empty());

  st.step(remotes, 1'500'000);
  CHECK(st.walks.size() == 2);
  CHECK(st.places.empty());
  CHECK(st.reg.counters().walks == 2);

  // Back to Idle: one stop, then normal per-frame placement (which is what snaps the actor home).
  auto idle = avatar_in_room(1.0f, 2.0f, 3.0f, 2, 2'000'000);
  remotes[7] = idle;
  st.step(remotes, 2'000'000);
  REQUIRE(st.stops.size() == 1);
  CHECK(st.stops.front() == 7);
  CHECK(st.places.size() == 1);
  CHECK(st.reg.counters().stops == 1);

  st.step(remotes, 2'100'000);
  CHECK(st.stops.size() == 1);  // a latch, not a per-frame command
}

TEST_CASE("onfoot: removing a walking avatar stops its walk as well") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  auto a = avatar_in_room(0, 0, 0);
  a.anim = Anim::Run;
  remotes[7] = a;
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  st.step(remotes, 1'100'000);
  REQUIRE(st.walks.size() == 1);

  remotes.clear();  // the player leaves the container
  st.step(remotes, 1'200'000);
  REQUIRE(st.stops.size() == 1);
  CHECK(st.reg.counters().despawns == 1);
}

TEST_CASE("onfoot: a refused walk ends the hold, so the avatar keeps moving") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  auto a = avatar_in_room(1.0f, 2.0f, 3.0f);
  a.anim = Anim::Walk;
  remotes[7] = a;
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  st.step(remotes, 1'100'000);
  REQUIRE(st.walks.size() == 1);
  REQUIRE(st.places.empty());

  // MD has no slot for this room (every corridor): it answers "failed;<pid>". The hold must end at once and placement resume,
  // otherwise the avatar would stand still for the whole walk instead of only losing the animation.
  st.reg.on_reply("failed;7");
  st.step(remotes, 1'200'000);
  CHECK(st.walks.size() == 1);
  CHECK(st.places.size() == 1);
  CHECK(st.reg.counters().walk_denied == 1);

  // Inside the backoff: no new command every 400 ms, but placement continues.
  st.step(remotes, 1'500'000);
  CHECK(st.walks.size() == 1);
  CHECK(st.places.size() == 2);

  // Backoff over while the sender is still walking: one more attempt.
  st.step(remotes, 3'300'000);
  CHECK(st.walks.size() == 2);
}

TEST_CASE("onfoot: another room of the same container is a spawn, not a despawn") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  REQUIRE(st.reg.live() == 1);
  REQUIRE(st.batches.size() == 1);

  SECTION("a room the catalogue knows: removed from the old room, asked for in the new one") {
    remotes[7].room.macro_ref = fnv1a32("room_arg_bar_01_macro");
    remotes[7].room.anchor_x = -1000;
    st.roomIds[fnv1a32("room_arg_bar_01_macro")] = "room_arg_bar_01_macro";
    st.step(remotes, 1'100'000);
    REQUIRE(st.reg.tracked() == 1);
    CHECK(st.reg.live() == 0);  // the actor of the old room is gone, a new one is asked for
    REQUIRE(st.batches.size() == 2);
    CHECK(st.batches.back().find("\"despawn\":[7]") != std::string::npos);
    CHECK(st.batches.back().find("\"rmacro\":\"room_arg_bar_01_macro\"") != std::string::npos);
    CHECK(st.batches.back().find("\"ax\":-1000") != std::string::npos);
    CHECK(st.reg.counters().despawns == 1);
    CHECK(st.reg.counters().spawns == 2);
  }

  SECTION("a room the catalogue does not know yet: no actor, counted, retried") {
    remotes[7].room.macro_ref = 8u;
    st.step(remotes, 1'100'000);
    CHECK(st.reg.counters().no_room == 1);
    CHECK(st.batches.back().find("\"rmacro\"") == std::string::npos);
    CHECK(st.reg.tracked() == 1);  // the player stays tracked, the presence list still names them
  }
}

TEST_CASE("onfoot: a player in another room of the station is spawned with the id from the catalogue") {
  Stage st;
  st.roomIds[fnv1a32("room_arg_bar_01_macro")] = "room_arg_bar_01_macro";
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(1.0f, 2.0f, 3.0f);
  remotes[7].room.macro_ref = fnv1a32("room_arg_bar_01_macro");
  remotes[7].room.anchor_x = -5000;
  remotes[7].room.anchor_y = 100;
  remotes[7].room.anchor_z = 40;
  st.step(remotes, 1'000'000);
  REQUIRE(st.batches.size() == 1);
  const auto& batch = st.batches.back();
  CHECK(batch.find("\"rmacro\":\"room_arg_bar_01_macro\"") != std::string::npos);
  CHECK(batch.find("\"ax\":-5000") != std::string::npos);
  // The anchor travels on the wire for the room lookup, but the spawn position stays room-local (add_actor_to_room is inside $Room);
  // y loses the 1.72 m between the eye and the floor.
  CHECK(batch.find("\"x\":1.000") != std::string::npos);
  CHECK(batch.find("\"y\":0.280") != std::string::npos);
  CHECK(batch.find("\"z\":3.000") != std::string::npos);
  CHECK(st.reg.counters().spawns == 1);
  CHECK(st.reg.counters().no_room == 0);
}

TEST_CASE("onfoot: same_container ignores the room but needs both sides on foot in one container") {
  const auto local = walking_context();
  auto a = avatar_in_room(0, 0, 0);
  CHECK(same_container(a, local));
  a.room = RoomRef{};  // an unknown room key does not matter for the container
  CHECK(same_container(a, local));
  a.container = 0x80000002u;
  CHECK_FALSE(same_container(a, local));
  a.container = 0;
  CHECK_FALSE(same_container(a, local));

  auto off = local;
  off.mode = Mode::None;
  CHECK_FALSE(same_container(avatar_in_room(0, 0, 0), off));
  off = local;
  off.container_net_id = 0;
  CHECK_FALSE(same_container(avatar_in_room(0, 0, 0), off));
  off = local;
  off.valid = false;
  CHECK_FALSE(same_container(avatar_in_room(0, 0, 0), off));
}

TEST_CASE("onfoot: another container, a cockpit seat or the leave state is despawned at once") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  REQUIRE(st.reg.live() == 1);

  SECTION("another container") {
    remotes[7].container = 0x80000002u;
    st.step(remotes, 1'100'000);
  }
  SECTION("seated in a cockpit") {
    remotes[7].mode = Mode::Seated;
    st.step(remotes, 1'100'000);
  }
  SECTION("the leave state") {
    remotes.clear();
    st.step(remotes, 1'100'000);
  }
  REQUIRE(st.reg.tracked() == 0);
  REQUIRE(st.reg.live() == 0);
  REQUIRE(st.batches.size() == 2);
  CHECK(st.batches.back().find("\"despawn\":[7]") != std::string::npos);
  CHECK(st.reg.counters().despawns == 1);
}

TEST_CASE("onfoot: a player who stops sending is dropped after five seconds") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  st.step(remotes, 1'100'000);
  REQUIRE(st.reg.live() == 1);
  st.step(remotes, 6'200'000);  // 5.1 s after the newest state
  CHECK(st.reg.tracked() == 0);
  CHECK(st.reg.counters().despawns == 1);
}

TEST_CASE("onfoot: nothing is spawned while the local player is not on foot") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  auto local = walking_context();
  local.mode = Mode::None;
  st.reg.update(local, remotes, {}, 1'000'000);
  CHECK(st.batches.empty());
  CHECK(st.reg.tracked() == 0);
  CHECK(st.reg.counters().ignored == 1);
}

TEST_CASE("onfoot: clear asks MD to remove every avatar it still has") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  remotes[8] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  st.batches.clear();
  st.reg.clear();
  REQUIRE(st.batches.size() == 1);
  CHECK(st.batches.front().find("\"despawn\":[7,8]") != std::string::npos);
  CHECK(st.reg.tracked() == 0);
  st.reg.clear();  // nothing left: no second batch
  CHECK(st.batches.size() == 1);
}

TEST_CASE("onfoot: MD's 'gone' forgets the actor and the throttle delays the re-ask") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  st.reg.on_reply("gone;7");
  CHECK(st.reg.live() == 0);
  CHECK(st.reg.tracked() == 1);  // the player is still here, only the actor is gone
  st.step(remotes, 1'100'000);
  CHECK(st.reg.counters().spawns == 1);  // no spawn/gone storm
  st.step(remotes, 3'100'000);
  CHECK(st.reg.counters().spawns == 2);  // asked again two seconds after the removal
}

// S10.6: the container's interiors are despawned, so MD destroys every avatar it held in one go and sends one 'gone' per player.
// The players stay (only their bodies died with the rooms) and the re-ask is throttled exactly like any other removal.
TEST_CASE("onfoot: an interiors teardown drops every avatar at once and the asks come back throttled") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  remotes[8] = avatar_in_room(0, 0, 0);
  remotes[9] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");
  st.reg.on_reply("spawn;8;9002");
  st.reg.on_reply("spawn;9;9003");
  REQUIRE(st.reg.live() == 3);

  st.reg.on_reply("gone;7");
  st.reg.on_reply("gone;8");
  st.reg.on_reply("gone;9");
  CHECK(st.reg.live() == 0);
  CHECK(st.reg.tracked() == 3);            // the players are still here, only the bodies are gone
  CHECK(st.reg.counters().bad_replies == 0);
  CHECK(st.reg.counters().spawns == 3);

  st.step(remotes, 1'100'000);
  CHECK(st.reg.counters().spawns == 3);  // no spawn/gone storm right after the teardown
  st.step(remotes, 3'100'000);
  CHECK(st.reg.counters().spawns == 6);  // every avatar is asked for again
  CHECK(st.reg.live() == 0);             // asked, not confirmed
}

TEST_CASE("onfoot: a malformed answer is counted, not trusted") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;notanumber");
  st.reg.on_reply("spawn;7");
  st.reg.on_reply("nonsense");
  st.reg.on_reply("spawn;99;9001");  // a player we never asked about
  CHECK(st.reg.counters().bad_replies == 3);
  CHECK(st.reg.live() == 0);
}

TEST_CASE("onfoot: an answer may name the actor in hex, as MD prints it") {
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0);
  st.step(remotes, 1'000'000);
  // MD builds the reply by concatenating the component into a string, and a component prints as "0x18ead5".
  st.reg.on_reply("spawn;7;0x18ead5");
  CHECK(st.reg.live() == 1);
  std::uint64_t actor = 0;
  REQUIRE(st.reg.actor_of(7, actor));
  CHECK(actor == 0x18ead5ull);
  CHECK(st.reg.counters().bad_replies == 0);

  // The decimal form still works, and a broken hex tail is still a bad answer.
  st.reg.on_reply("gone;7");
  CHECK(st.reg.live() == 0);
  st.reg.on_reply("spawn;7;1632981");
  REQUIRE(st.reg.actor_of(7, actor));
  CHECK(actor == 1632981ull);
  st.reg.on_reply("gone;7");
  st.reg.on_reply("spawn;7;0x");
  st.reg.on_reply("spawn;7;0xzz");
  CHECK(st.reg.counters().bad_replies == 2);
}

TEST_CASE("onfoot: a peer is here only while both share a container and the peer walks", "[onfoot][presence]") {
  PresenceRow row;
  row.player_id = 7;
  row.container = 4242;
  row.mode = Mode::Walking;

  CHECK(presence_here(row, 4242));
  CHECK_FALSE(presence_here(row, 0));     // the local player is not in an interior
  CHECK_FALSE(presence_here(row, 9999));  // another station
  row.container = 0;
  CHECK_FALSE(presence_here(row, 4242));  // a peer that reported no container

  row.container = 4242;
  row.mode = Mode::Seated;
  CHECK_FALSE(presence_here(row, 4242));  // in a cockpit, not standing here
  row.mode = Mode::InTransit;
  CHECK_FALSE(presence_here(row, 4242));  // in a lift or a doorway
  row.mode = Mode::Conversation;
  CHECK_FALSE(presence_here(row, 4242));
  row.mode = Mode::Menu;
  CHECK_FALSE(presence_here(row, 4242));
}

TEST_CASE("onfoot: the presence signature changes with the roster and with the local container", "[onfoot][presence]") {
  std::vector<PresenceRow> rows;
  PresenceRow a;
  a.player_id = 7;
  a.mode = Mode::Walking;
  a.container = 4242;
  rows.push_back(a);

  const auto base = presence_signature(rows, 4242, Mode::Walking);
  CHECK(presence_signature(rows, 4242, Mode::Walking) == base);  // stable text, so nothing is raised twice
  CHECK(presence_signature(rows, 4242, Mode::Seated) != base);   // the local mode decides whether we are on foot at all
  CHECK(presence_signature(rows, 9999, Mode::Walking) != base);  // walking into another container changes who is here
  CHECK(presence_signature({}, 4242, Mode::Walking) != base);    // a peer that left

  auto b = a;
  b.mode = Mode::Seated;
  CHECK(presence_signature({b}, 4242, Mode::Walking) != base);

  auto c = a;
  c.player_id = 8;
  CHECK(presence_signature({c}, 4242, Mode::Walking) != base);  // a different player, same place
}

TEST_CASE("onfoot: a live avatar is measured once per five seconds", "[onfoot][diag]") {
  // The measurement line is what a session reads to check the M3b "error < 0.5 m" criterion, so its cadence and its inputs
  // (pid, how far behind the stream the avatar is drawn, its speed, the age of the newest sample) must be stable.
  Stage st;
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0, 1, 1'000'000);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");

  remotes[7] = avatar_in_room(0, 0, 0, 2, 1'100'000);
  st.step(remotes, 1'100'000);
  CHECK(st.reg.live() == 1);
  CHECK(st.diags.empty());  // the first line comes five seconds after the actor went live, not on every frame

  remotes[7] = avatar_in_room(0, 0, 0, 3, 4'900'000);
  st.step(remotes, 4'900'000);
  CHECK(st.diags.empty());

  remotes[7] = avatar_in_room(0, 0, 0, 4, 5'000'000);
  st.step(remotes, 5'000'000);
  REQUIRE(st.diags.size() == 1);
  CHECK(st.diags.front().pid == 7);
  CHECK(st.diags.front().lag_m >= 0.0f);
  CHECK(st.diags.front().speed_mps >= 0.0f);
  CHECK(st.diags.front().age_us == 0);  // now is the newest sample

  remotes[7] = avatar_in_room(0, 0, 0, 5, 5'100'000);
  st.step(remotes, 5'100'000);
  CHECK(st.diags.size() == 1);  // not again until five seconds later

  remotes[7] = avatar_in_room(0, 0, 0, 6, 10'000'000);
  st.step(remotes, 10'000'000);
  CHECK(st.diags.size() == 2);

  // A standing avatar is drawn where its newest sample says: no lag. A walking one is behind by speed x the display delay.
  CHECK(st.diags.back().speed_mps == Approx(0.0f).margin(0.01f));
  CHECK(st.diags.back().lag_m == Approx(0.0f).margin(0.01f));

  // No local position was ever set (the frame had no sample): the distance cannot be measured, and the line says so.
  CHECK(st.diags.back().dist_m < 0.0f);
}

TEST_CASE("onfoot: the measurement line says how far the local player stands from the avatar", "[onfoot][diag]") {
  // Two players in one room are a few metres apart even when the camera looks the other way: this is the number that proves the
  // avatar was drawn where the peer stands, without a screenshot.
  Stage st;
  st.reg.set_self(true, 0.0, 0.0, 5.0);  // the local player: 5 m along z from the avatar at the room origin
  std::map<std::uint16_t, RemoteAvatar> remotes;
  remotes[7] = avatar_in_room(0, 0, 0, 1, 1'000'000);
  st.step(remotes, 1'000'000);
  st.reg.on_reply("spawn;7;9001");

  remotes[7] = avatar_in_room(0, 0, 0, 2, 6'000'000);
  st.step(remotes, 6'000'000);
  REQUIRE(st.diags.size() == 1);
  CHECK(st.diags.front().dist_m == Approx(5.0f).margin(0.01f));

  // The other side of the room: 30 m away, still the same room (the distance is not a filter, only a measurement).
  st.reg.set_self(true, 0.0, 0.0, 30.0);
  remotes[7] = avatar_in_room(0, 0, 0, 3, 11'000'000);
  st.step(remotes, 11'000'000);
  REQUIRE(st.diags.size() == 2);
  CHECK(st.diags.back().dist_m == Approx(30.0f).margin(0.01f));
}
