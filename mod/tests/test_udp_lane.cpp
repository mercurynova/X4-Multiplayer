// M3-03: UDP realtime lane (core/net/udp_lane.h): datagram layout, ack bookkeeping, and the Binding / Active / Fallback state
// machine with a fake clock and a simulated server end. No sockets. The live test against the real server is
// tests/hostsim/udp_lane_run.ps1.
#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <vector>

#include "control_generated.h"
#include "core/net/udp_lane.h"
#include "message_ids_generated.h"
#include "x4mp/wire.h"

using namespace x4mp;
using namespace x4mp::net;
using X4MP::Proto::MsgType;

namespace {

constexpr std::int64_t kMs = 1000;
constexpr std::uint32_t kConn = 0xA1B2C3D4;
constexpr std::uint64_t kToken = 0x1122334455667788ull;
constexpr auto kPlayerState = static_cast<std::uint16_t>(MsgType::PlayerState);
constexpr auto kReplication = static_cast<std::uint16_t>(MsgType::Replication);
constexpr auto kHello = static_cast<std::uint16_t>(MsgType::UdpHello);
constexpr auto kHelloAck = static_cast<std::uint16_t>(MsgType::UdpHelloAck);

using Bytes = std::vector<std::uint8_t>;

std::vector<std::uint8_t> payload_of(std::size_t n, std::uint8_t fill = 0x5A) { return Bytes(n, fill); }

// The server end: answers hellos with UdpHelloAck, acks data, can send data frames, can be "blocked" (drops what it receives
// and sends nothing), and records what it received.
struct SimServer {
  std::uint32_t conn_id = kConn;
  std::uint64_t token = kToken;
  UdpReceiveWindow rx;
  std::uint32_t seq = 0;
  bool blocked = false;
  bool answer_hello = true;
  std::uint64_t hellos = 0;
  std::uint64_t data_datagrams = 0;
  std::uint64_t data_frames = 0;
  std::uint64_t datagrams_seen = 0;
  std::vector<Bytes> out;  // datagrams for the client

  void on_datagram(wire::ByteSpan d) {
    if (blocked) return;
    const auto h = wire::read_datagram_header(d);
    REQUIRE(h.has_value());
    REQUIRE(h->conn_id == conn_id);
    ++datagrams_seen;
    if (rx.accept(h->seq) != UdpReceiveWindow::Result::Accepted) return;
    bool carries = false;
    bool hello = false;
    REQUIRE(wire::for_each_sub_message(d, [&](const wire::SubMessage& m) {
      carries = true;
      if (m.type == kHello) {
        hello = true;
        ++hellos;
        const auto* hl = flatbuffers::GetRoot<X4MP::Proto::UdpHello>(m.payload.data());
        CHECK(hl->conn_id() == conn_id);
        CHECK(hl->udp_token() == token);
      } else {
        ++data_frames;
      }
    }));
    if (carries && !hello) ++data_datagrams;
    if (hello && answer_hello) {
      flatbuffers::FlatBufferBuilder fbb(32);
      fbb.Finish(X4MP::Proto::CreateUdpHelloAck(fbb, conn_id));
      send(kHelloAck, wire::ByteSpan(fbb.GetBufferPointer(), fbb.GetSize()));
    } else if (carries) {
      send_bare_ack();  // the real server acks after 50 ms; the test model is instantaneous
    }
  }

  void send_bare_ack() {
    Bytes b(wire::kDatagramHeaderSize);
    wire::write_datagram_header(std::span<std::uint8_t, wire::kDatagramHeaderSize>(b), header());
    out.push_back(std::move(b));
  }

  void send(std::uint16_t type, wire::ByteSpan payload) {
    Bytes b(wire::kMaxDatagramBytes);
    wire::DatagramWriter w(b, header());
    REQUIRE(w.try_add(type, payload).value());
    b.resize(w.size());
    out.push_back(std::move(b));
  }

  wire::DatagramHeader header() {
    return wire::DatagramHeader{static_cast<std::uint8_t>(wire::kProtocolMajor), conn_id, ++seq, rx.ack(), rx.ack_bits()};
  }
};

// Client lane + server end + a fake clock.
struct Rig {
  explicit Rig(UdpOptions o = {}) : lane(o, [this](wire::ByteSpan d) {
    sent.emplace_back(d.begin(), d.end());
    return true;
  }) {}

  UdpLane lane;
  SimServer server;
  std::vector<Bytes> sent;  // datagrams the lane wrote to its socket
  std::int64_t now = 0;
  std::uint64_t delivered_frames = 0;
  std::vector<std::uint16_t> delivered_types;

  void deliver_sent_to_server() {
    auto batch = std::move(sent);
    sent.clear();
    for (const auto& d : batch) server.on_datagram(d);
  }
  void deliver_server_to_client() {
    auto batch = std::move(server.out);
    server.out.clear();
    for (const auto& d : batch) {
      lane.on_datagram(d, now, [&](std::uint16_t type, wire::ByteSpan) {
        ++delivered_frames;
        delivered_types.push_back(type);
      });
    }
  }
  // One simulated loop iteration at the current time.
  void step() {
    lane.tick(now);
    deliver_sent_to_server();
    deliver_server_to_client();
  }
  void advance_ms(std::int64_t ms, std::int64_t step_ms = 5) {
    for (std::int64_t t = 0; t < ms; t += step_ms) {
      now += step_ms * kMs;
      step();
    }
  }
  void start() { lane.start(kConn, kToken, now); }
  void bind() {
    start();
    advance_ms(20);
    REQUIRE(lane.state() == UdpState::Active);
  }
};

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------------
// receive window
// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("udp window: ack, ack_bits, duplicates, too old, loss", "[udp][window]") {
  UdpReceiveWindow w;
  CHECK(w.ack() == 0);
  CHECK(w.accept(10) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.ack() == 10);
  CHECK(w.ack_bits() == 0);
  CHECK(w.accept(12) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.ack() == 12);
  CHECK(w.ack_bits() == 0b10);  // 10 is ack-1-1 = bit 1; 11 (bit 0) is missing
  CHECK(w.accept(11) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.ack_bits() == 0b11);
  CHECK(w.accept(11) == UdpReceiveWindow::Result::Duplicate);
  CHECK(w.accept(12) == UdpReceiveWindow::Result::Duplicate);
  CHECK(w.accept(200) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.accept(100) == UdpReceiveWindow::Result::TooOld);  // 100 behind 200 (>= 64)
  CHECK(w.accept(150) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.missing() > 0);

  UdpReceiveWindow l;
  for (std::uint32_t s = 1; s <= 100; ++s) {
    if (s % 10 != 0) (void)l.accept(s);  // 10 % lost
  }
  CHECK(l.loss_percent() == Catch::Approx(10.0f).margin(1.5f));
}

TEST_CASE("udp window: sequence wrap", "[udp][window]") {
  UdpReceiveWindow w;
  CHECK(w.accept(0xFFFFFFFEu) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.accept(0xFFFFFFFFu) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.accept(0u) == UdpReceiveWindow::Result::Accepted);
  CHECK(w.ack() == 0u);
  CHECK(w.ack_bits() == 0b11);
  CHECK(w.accept(0xFFFFFFFFu) == UdpReceiveWindow::Result::Duplicate);
}

// ---------------------------------------------------------------------------------------------------------------------------
// datagram layout (protocol.md 3.3), as the server and FakeNode read it
// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("udp lane: the first datagram is a UdpHello with the Welcome numbers", "[udp][codec]") {
  Rig r;
  r.start();
  REQUIRE(r.lane.state() == UdpState::Binding);
  r.lane.tick(r.now);
  REQUIRE(r.sent.size() == 1);
  const Bytes& d = r.sent[0];
  CHECK(d[0] == 'X');
  CHECK(d[1] == 'M');
  CHECK(d[2] == wire::kProtocolMajor);
  CHECK(d[3] == 0);  // flags
  const auto h = wire::read_datagram_header(d);
  REQUIRE(h.has_value());
  CHECK(h->conn_id == kConn);
  CHECK(h->seq == 1);
  CHECK(h->ack == 0);
  CHECK(h->ack_bits == 0);
  CHECK(d.size() % 8 == 0);  // 24-byte header + a sub-message padded to 8
  int subs = 0;
  REQUIRE(wire::for_each_sub_message(d, [&](const wire::SubMessage& m) {
    ++subs;
    CHECK(m.type == kHello);
    const auto* hl = flatbuffers::GetRoot<X4MP::Proto::UdpHello>(m.payload.data());
    CHECK(hl->conn_id() == kConn);
    CHECK(hl->udp_token() == kToken);
  }));
  CHECK(subs == 1);
}

TEST_CASE("udp lane: a Realtime frame becomes a sub-message with the padded layout", "[udp][codec]") {
  Rig r;
  r.bind();
  r.sent.clear();
  const auto p = payload_of(21);
  REQUIRE(r.lane.send_realtime(kPlayerState, p, r.now));
  CHECK(r.sent.empty());  // batched until the flush
  r.lane.flush(r.now);
  REQUIRE(r.sent.size() == 1);
  const Bytes& d = r.sent[0];
  CHECK(d.size() == wire::kDatagramHeaderSize + wire::sub_message_size(21));
  CHECK(d.size() % 8 == 0);
  CHECK(d[wire::kDatagramHeaderSize] == (kPlayerState & 0xFF));
  CHECK(d[wire::kDatagramHeaderSize + 1] == (kPlayerState >> 8));
  CHECK(d[wire::kDatagramHeaderSize + 2] == 21);
  CHECK(d[wire::kDatagramHeaderSize + 3] == 0);
  for (std::size_t i = wire::kDatagramHeaderSize + 4 + 21; i < d.size(); ++i) CHECK(d[i] == 0);  // zero pad
  CHECK(r.lane.stats().frames_out == 1);
}

TEST_CASE("udp lane: batching, the 1200-byte limit, disallowed and oversized messages", "[udp][codec]") {
  Rig r;
  r.bind();
  r.sent.clear();
  // 3 x 300 bytes fit (3 x 304 + 24 = 936); a fourth 300 would make 1240: it flushes the first datagram and starts a second.
  for (int i = 0; i < 4; ++i) REQUIRE(r.lane.send_realtime(kReplication, payload_of(300), r.now));
  r.lane.flush(r.now);
  REQUIRE(r.sent.size() == 2);
  for (const auto& d : r.sent) CHECK(d.size() <= wire::kMaxDatagramBytes);
  int frames = 0;
  for (const auto& d : r.sent) REQUIRE(wire::for_each_sub_message(d, [&](const wire::SubMessage&) { ++frames; }));
  CHECK(frames == 4);
  CHECK(wire::read_datagram_header(r.sent[1]).value().seq == wire::read_datagram_header(r.sent[0]).value().seq + 1);

  // a message that cannot fit one datagram goes over TCP (false), as does a type that may not use UDP
  CHECK_FALSE(r.lane.send_realtime(kReplication, payload_of(1173), r.now));
  CHECK(r.lane.send_realtime(kReplication, payload_of(1172), r.now));
  r.lane.flush(r.now);
  CHECK(r.sent.back().size() == wire::kMaxDatagramBytes);
  CHECK_FALSE(r.lane.send_realtime(static_cast<std::uint16_t>(MsgType::SaveChunk), payload_of(16), r.now));
  CHECK_FALSE(r.lane.send_realtime(kPlayerState, {}, r.now));
}

TEST_CASE("udp lane: malformed, foreign-connection and duplicate datagrams are refused", "[udp][codec]") {
  Rig r;
  r.bind();
  std::uint64_t got = 0;
  const auto visit = [&](std::uint16_t, wire::ByteSpan) { ++got; };

  const auto build = [&](std::uint32_t conn, std::uint32_t seq, std::uint16_t type, std::size_t len) {
    Bytes b(wire::kMaxDatagramBytes);
    wire::DatagramWriter w(b, wire::DatagramHeader{static_cast<std::uint8_t>(wire::kProtocolMajor), conn, seq, 0, 0});
    REQUIRE(w.try_add(type, payload_of(len)).value());
    b.resize(w.size());
    return b;
  };
  const auto ok = build(kConn, 100, kReplication, 20);
  r.lane.on_datagram(ok, r.now, visit);
  CHECK(got == 1);
  r.lane.on_datagram(ok, r.now, visit);  // duplicate
  CHECK(got == 1);
  CHECK(r.lane.stats().duplicates == 1);
  r.lane.on_datagram(build(kConn + 1, 101, kReplication, 20), r.now, visit);  // other connection
  CHECK(got == 1);
  Bytes bad = ok;
  bad[0] = 0;  // magic
  r.lane.on_datagram(bad, r.now, visit);
  Bytes trunc(ok.begin(), ok.begin() + 30);  // sub-message overruns
  r.lane.on_datagram(trunc, r.now, visit);
  Bytes disallowed = build(kConn, 102, kReplication, 20);
  disallowed[wire::kDatagramHeaderSize] = static_cast<std::uint8_t>(static_cast<std::uint16_t>(MsgType::SaveChunk) & 0xFF);
  disallowed[wire::kDatagramHeaderSize + 1] = static_cast<std::uint8_t>(static_cast<std::uint16_t>(MsgType::SaveChunk) >> 8);
  r.lane.on_datagram(disallowed, r.now, visit);  // a type that may not travel over UDP
  CHECK(got == 1);
  CHECK(r.lane.stats().malformed >= 4);
}

// ---------------------------------------------------------------------------------------------------------------------------
// state machine
// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("udp lane: binds on UdpHelloAck, hello every 250 ms until then", "[udp][state]") {
  Rig r;
  r.server.answer_hello = false;
  r.start();
  r.advance_ms(1000);
  CHECK(r.lane.state() == UdpState::Binding);
  CHECK(r.server.hellos == 4);  // t = 0, 250, 500, 750 (advance_ms starts after t=0 so 5..1000: 250..1000)
  CHECK_FALSE(r.lane.send_realtime(kPlayerState, payload_of(20), r.now));  // still TCP

  r.server.answer_hello = true;
  r.advance_ms(300);
  CHECK(r.lane.state() == UdpState::Active);
  CHECK(r.lane.stats().binds == 1);
  CHECK(r.lane.send_realtime(kPlayerState, payload_of(20), r.now));
  r.lane.flush(r.now);
  r.step();
  CHECK(r.server.data_frames == 1);
}

TEST_CASE("udp lane: no ack within 3 s of binding -> TCP, no further traffic, re-probe after 30 s", "[udp][state]") {
  Rig r;
  r.server.blocked = true;  // UDP path pulled
  r.start();
  r.advance_ms(2900);
  CHECK(r.lane.state() == UdpState::Binding);
  r.advance_ms(200);
  CHECK(r.lane.state() == UdpState::Fallback);
  CHECK(r.lane.stats().fallbacks == 1);
  CHECK_FALSE(r.lane.send_realtime(kPlayerState, payload_of(20), r.now));  // TCP now

  const auto hellos_at_fallback = r.lane.stats().datagrams_out;
  r.advance_ms(20000);
  CHECK(r.lane.stats().datagrams_out == hellos_at_fallback);  // silent while in fallback
  CHECK(r.lane.state() == UdpState::Fallback);

  r.advance_ms(10000);  // 30 s after the fallback: one more binding attempt
  CHECK(r.lane.stats().reprobes == 1);
  CHECK(r.lane.state() == UdpState::Binding);
  r.advance_ms(3200);
  CHECK(r.lane.state() == UdpState::Fallback);
  CHECK(r.lane.stats().fallbacks == 2);

  // the path comes back: the next re-probe binds
  r.server.blocked = false;
  r.advance_ms(30100);
  CHECK(r.lane.state() == UdpState::Active);
  CHECK(r.lane.stats().binds == 1);
  CHECK(r.lane.send_realtime(kPlayerState, payload_of(20), r.now));
}

TEST_CASE("udp lane: bound, then the path dies -> TCP within 3 s", "[udp][state]") {
  Rig r;
  r.bind();
  // 20 Hz Realtime traffic, acked by the server
  for (int i = 0; i < 100; ++i) {
    REQUIRE(r.lane.send_realtime(kPlayerState, payload_of(24), r.now));
    r.lane.flush(r.now);
    r.advance_ms(50);
  }
  CHECK(r.lane.state() == UdpState::Active);
  CHECK(r.server.data_frames >= 100);

  r.server.blocked = true;  // the firewall rule appears
  const std::int64_t t_block = r.now;
  std::int64_t t_fallback = -1;
  for (int i = 0; i < 200 && t_fallback < 0; ++i) {
    if (r.lane.state() == UdpState::Active) {
      (void)r.lane.send_realtime(kPlayerState, payload_of(24), r.now);
      r.lane.flush(r.now);
    }
    r.advance_ms(50);
    if (r.lane.state() == UdpState::Fallback) t_fallback = r.now;
  }
  REQUIRE(t_fallback > 0);
  const auto took_ms = (t_fallback - t_block) / kMs;
  CHECK(took_ms >= 2900);
  CHECK(took_ms <= 3200);
  CHECK_FALSE(r.lane.send_realtime(kPlayerState, payload_of(24), r.now));
}

TEST_CASE("udp lane: an idle bound lane stays bound (keepalive hello, ack-only datagram)", "[udp][state]") {
  Rig r;
  r.bind();
  r.server.hellos = 0;
  r.advance_ms(20000);  // nothing to send for 20 s
  CHECK(r.lane.state() == UdpState::Active);
  CHECK(r.server.hellos >= 15);  // roughly one per second
  CHECK(r.lane.stats().fallbacks == 0);

  // server data -> a bare-header ack (24 bytes, acknowledging it) follows after ~50 ms of silence
  r.server.out.clear();
  r.sent.clear();
  r.server.send(kReplication, payload_of(40));
  const auto server_seq = r.server.seq;
  r.deliver_server_to_client();
  const std::int64_t t0 = r.now;
  r.lane.tick(t0 + 60 * kMs);
  REQUIRE(r.sent.size() == 1);
  CHECK(r.sent[0].size() == wire::kDatagramHeaderSize);
  const auto h = wire::read_datagram_header(r.sent[0]);
  REQUIRE(h.has_value());
  CHECK(h->ack == server_seq);
}

TEST_CASE("udp lane: inbound frames reach the visitor, hello acks do not", "[udp][state]") {
  Rig r;
  r.bind();
  r.delivered_frames = 0;
  r.delivered_types.clear();
  r.server.send(kReplication, payload_of(40));
  r.server.send(kHelloAck, payload_of(8));
  r.server.send(kPlayerState, payload_of(24));
  r.deliver_server_to_client();
  CHECK(r.delivered_frames == 2);
  REQUIRE(r.delivered_types.size() == 2);
  CHECK(r.delivered_types[0] == kReplication);
  CHECK(r.delivered_types[1] == kPlayerState);
  CHECK(r.lane.stats().frames_in >= 2);
}

TEST_CASE("udp lane: 5% loss in both directions is tolerated (stays on UDP)", "[udp][loss]") {
  UdpOptions o;
  o.loss_pct = 5.0;
  o.seed = 12345;
  Rig r(o);
  r.start();
  r.advance_ms(3000);  // hellos get through eventually
  REQUIRE(r.lane.state() == UdpState::Active);
  for (int i = 0; i < 20 * 60; ++i) {  // 60 s at 20 Hz
    REQUIRE((r.lane.send_realtime(kPlayerState, payload_of(24), r.now) || r.lane.state() != UdpState::Active));
    r.lane.flush(r.now);
    r.advance_ms(50);
    r.server.send(kReplication, payload_of(60));  // the server streams at the same rate
  }
  const auto s = r.lane.stats();
  CHECK(r.lane.state() == UdpState::Active);
  CHECK(s.fallbacks == 0);
  CHECK(s.sim_drops_tx > 20);
  CHECK(s.sim_drops_rx > 20);
  CHECK(s.rx_loss_pct > 1.0f);
  CHECK(s.rx_loss_pct < 12.0f);
  CHECK(s.acked_seq > 1000);  // the server's acks keep up
}

TEST_CASE("udp lane: Force never falls back and never uses TCP; Off never binds", "[udp][mode]") {
  {
    UdpOptions o;
    o.mode = UdpMode::Force;
    Rig r(o);
    r.server.blocked = true;
    r.start();
    r.advance_ms(40000);
    CHECK(r.lane.state() == UdpState::Binding);  // keeps trying, never Fallback
    CHECK(r.lane.stats().fallbacks == 0);
    CHECK(r.lane.send_realtime(kPlayerState, payload_of(20), r.now));  // dropped, not sent over TCP
    CHECK(r.lane.stats().force_dropped == 1);
    r.server.blocked = false;
    r.advance_ms(600);
    CHECK(r.lane.state() == UdpState::Active);
  }
  {
    UdpOptions o;
    o.mode = UdpMode::Off;
    Rig r(o);
    r.start();
    CHECK(r.lane.state() == UdpState::Off);
    r.advance_ms(1000);
    CHECK(r.sent.empty());
    CHECK_FALSE(r.lane.send_realtime(kPlayerState, payload_of(20), r.now));
  }
}

TEST_CASE("udp lane: block switch drops everything in both directions; stop() resets", "[udp][mode]") {
  Rig r;
  r.bind();
  r.lane.set_block(true);
  r.sent.clear();
  const auto before = r.lane.stats().datagrams_out;
  r.server.send(kReplication, payload_of(40));
  r.deliver_server_to_client();
  CHECK(r.lane.stats().sim_drops_rx == 1);
  (void)r.lane.send_realtime(kPlayerState, payload_of(20), r.now);
  r.lane.flush(r.now);
  CHECK(r.lane.stats().sim_drops_tx == 1);
  CHECK(r.lane.stats().datagrams_out == before);
  CHECK(r.sent.empty());

  r.lane.stop();
  CHECK(r.lane.state() == UdpState::Off);
  CHECK(r.lane.stats().datagrams_out == 0);
}
