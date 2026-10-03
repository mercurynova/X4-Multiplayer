// M3-03: the UDP lane's per-frame path must not allocate (send_realtime + flush + tick + on_datagram), docs/m3-plan.md 4.12.
// Own executable (x4mp_udp_alloc_tests): it links ghost/alloc_counter.cpp, which replaces the global operator new.
#include <array>
#include <cstdint>

#include <catch2/catch_test_macros.hpp>

#include "alloc_counter.h"
#include "core/net/udp_lane.h"
#include "message_ids_generated.h"
#include "x4mp/wire.h"

using namespace x4mp;
using namespace x4mp::net;
using X4MP::Proto::MsgType;

TEST_CASE("udp lane: no allocation per frame once bound", "[udp][hot]") {
  std::array<std::array<std::uint8_t, wire::kMaxDatagramBytes>, 64> sink{};
  std::array<std::size_t, 64> sink_len{};
  std::size_t n_sent = 0;
  UdpLane lane(UdpOptions{}, [&](wire::ByteSpan d) {
    sink[n_sent % 64] = {};
    for (std::size_t i = 0; i < d.size(); ++i) sink[n_sent % 64][i] = d[i];
    sink_len[n_sent % 64] = d.size();
    ++n_sent;
    return true;
  });
  std::int64_t now = 0;
  lane.start(7, 99, now);
  lane.tick(now);

  // a HelloAck binds it
  std::array<std::uint8_t, wire::kMaxDatagramBytes> in{};
  const auto make_in = [&](std::uint32_t seq, std::uint16_t type, std::size_t len, std::uint32_t ack) -> wire::ByteSpan {
    std::array<std::uint8_t, 64> payload{};
    wire::DatagramWriter w(in, wire::DatagramHeader{static_cast<std::uint8_t>(wire::kProtocolMajor), 7, seq, ack, 0});
    (void)w.try_add(type, wire::ByteSpan(payload.data(), len));
    return w.bytes();
  };
  std::uint64_t got = 0;
  const auto visit = [&](std::uint16_t, wire::ByteSpan) { ++got; };
  now += 10'000;
  lane.on_datagram(make_in(1, static_cast<std::uint16_t>(MsgType::UdpHelloAck), 8, 1), now, visit);
  REQUIRE(lane.state() == UdpState::Active);

  const std::array<std::uint8_t, 48> state_payload{};
  std::uint32_t seq = 2;
  test::alloc_counter_arm();
  for (int i = 0; i < 2000; ++i) {
    now += 50'000;
    (void)lane.send_realtime(static_cast<std::uint16_t>(MsgType::PlayerState), state_payload, now);
    lane.flush(now);
    lane.tick(now);
    lane.on_datagram(make_in(seq++, static_cast<std::uint16_t>(MsgType::Replication), 40, static_cast<std::uint32_t>(n_sent)), now, visit);  // every datagram sent got a sequence number: ack them all
  }
  const std::uint64_t allocs = test::alloc_counter_disarm();
  CHECK(allocs == 0);
  CHECK(got == 2000);
  CHECK(lane.state() == UdpState::Active);
}
