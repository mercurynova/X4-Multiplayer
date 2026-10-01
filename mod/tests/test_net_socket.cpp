// Loopback socket tests for core/net (M1-N2). A tiny C++ server lives in this file: it accepts, sends frames
// (whole, byte by byte, oversized, garbage), reads what the client sends, and can stop reading to exercise the
// write cap. No X4 SDK, no real server.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "control_generated.h"
#include "core/net/net.h"
#include "message_ids_generated.h"
#include "x4mp/wire.h"

using namespace x4mp::net;
using namespace std::chrono_literals;
using X4MP::Proto::MsgType;

namespace {

struct WsaInit {
  WsaInit() {
    WSADATA d{};
    ::WSAStartup(MAKEWORD(2, 2), &d);
  }
  ~WsaInit() { ::WSACleanup(); }
};
const WsaInit g_wsa;

std::uint16_t T(MsgType t) { return static_cast<std::uint16_t>(t); }

bool wait_until(const std::function<bool()>& pred, std::chrono::milliseconds timeout = 5000ms) {
  const auto end = std::chrono::steady_clock::now() + timeout;
  while (std::chrono::steady_clock::now() < end) {
    if (pred()) return true;
    std::this_thread::sleep_for(2ms);
  }
  return pred();
}

std::vector<std::uint8_t> frame_bytes(std::uint16_t type, Lane lane, const std::vector<std::uint8_t>& payload) {
  std::vector<std::uint8_t> out(x4mp::wire::kFrameHeaderSize + payload.size());
  const auto r = x4mp::wire::encode_frame(out, type, lane, payload);
  REQUIRE(r.has_value());
  return out;
}

std::vector<std::uint8_t> to_vec(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::vector<std::uint8_t> hello_frame() {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(X4MP::Proto::CreateServerHelloDirect(fbb, 0, 1, "test-server", "loopback"));
  return frame_bytes(T(MsgType::ServerHello), Lane::Control, to_vec(fbb));
}

std::vector<std::uint8_t> ping_frame(std::uint32_t seq, std::uint64_t t) {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(X4MP::Proto::CreatePing(fbb, seq, t));
  return frame_bytes(T(MsgType::Ping), Lane::Control, to_vec(fbb));
}

std::vector<std::uint8_t> pong_frame(std::uint32_t seq, std::uint64_t echo, std::uint64_t recv, std::uint64_t reply) {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(X4MP::Proto::CreatePong(fbb, seq, echo, recv, reply));
  return frame_bytes(T(MsgType::Pong), Lane::Control, to_vec(fbb));
}

std::vector<std::uint8_t> disconnect_frame(std::uint16_t code, std::uint32_t retry_ms = 0) {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(X4MP::Proto::CreateDisconnectDirect(fbb, static_cast<X4MP::Proto::DisconnectCode>(code), "bye", "",
                                                 retry_ms));
  return frame_bytes(T(MsgType::Disconnect), Lane::Control, to_vec(fbb));
}

// A connection as seen by the test server. Blocking socket with a receive timeout.
class Conn {
 public:
  explicit Conn(SOCKET s = INVALID_SOCKET) : s_(s) {
    if (s_ != INVALID_SOCKET) {
      const DWORD ms = 2000;
      ::setsockopt(s_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&ms), sizeof(ms));
      const BOOL nd = TRUE;
      ::setsockopt(s_, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&nd), sizeof(nd));
    }
  }
  Conn(Conn&& o) noexcept : s_(o.s_), buf_(std::move(o.buf_)) { o.s_ = INVALID_SOCKET; }
  Conn& operator=(Conn&& o) noexcept {
    close();
    s_ = o.s_;
    buf_ = std::move(o.buf_);
    o.s_ = INVALID_SOCKET;
    return *this;
  }
  Conn(const Conn&) = delete;
  Conn& operator=(const Conn&) = delete;
  ~Conn() { close(); }
  void close() {
    if (s_ != INVALID_SOCKET) ::closesocket(s_);
    s_ = INVALID_SOCKET;
  }
  [[nodiscard]] bool valid() const { return s_ != INVALID_SOCKET; }
  [[nodiscard]] bool readable(int ms) const {
    WSAPOLLFD p{};
    p.fd = s_;
    p.events = POLLRDNORM;
    return ::WSAPoll(&p, 1, ms) > 0;
  }

  // FIN instead of RST: stop sending, drain what the peer already sent (closing with unread data resets the
  // connection and Windows then discards data the peer has not read yet), then close.
  void close_gracefully() {
    if (s_ == INVALID_SOCKET) return;
    ::shutdown(s_, SD_SEND);
    std::uint8_t tmp[1024];
    const DWORD ms = 300;
    ::setsockopt(s_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&ms), sizeof(ms));
    while (::recv(s_, reinterpret_cast<char*>(tmp), sizeof(tmp), 0) > 0) {
    }
    close();
  }

  void send(const std::vector<std::uint8_t>& bytes) {
    std::size_t off = 0;
    while (off < bytes.size()) {
      const int n = ::send(s_, reinterpret_cast<const char*>(bytes.data() + off), static_cast<int>(bytes.size() - off), 0);
      if (n <= 0) return;
      off += static_cast<std::size_t>(n);
    }
  }

  struct Frame {
    std::uint16_t type = 0;
    Lane lane = Lane::Control;
    std::vector<std::uint8_t> payload;
  };

  // Reads one frame (blocking up to the receive timeout per recv). nullopt on EOF/timeout.
  std::optional<Frame> read_frame() {
    for (;;) {
      const auto d = x4mp::wire::try_decode_frame(buf_);
      if (d && d->has_value()) {
        Frame f;
        f.type = (*d)->header.type;
        f.lane = (*d)->header.lane;
        f.payload.assign((*d)->payload.begin(), (*d)->payload.end());
        buf_.erase(buf_.begin(), buf_.begin() + static_cast<std::ptrdiff_t>((*d)->consumed));
        return f;
      }
      if (!d) return std::nullopt;
      std::uint8_t tmp[4096];
      const int n = ::recv(s_, reinterpret_cast<char*>(tmp), sizeof(tmp), 0);
      if (n <= 0) return std::nullopt;
      buf_.insert(buf_.end(), tmp, tmp + n);
    }
  }

  // Reads until a frame of `type` shows up (skipping others, e.g. heartbeat pings).
  std::optional<Frame> read_frame_of(std::uint16_t type) {
    for (int i = 0; i < 20; ++i) {
      auto f = read_frame();
      if (!f) return std::nullopt;
      if (f->type == type) return f;
    }
    return std::nullopt;
  }

 private:
  SOCKET s_;
  std::vector<std::uint8_t> buf_;
};

class LoopServer {
 public:
  explicit LoopServer(int rcvbuf = 0) {
    s_ = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    REQUIRE(s_ != INVALID_SOCKET);
    if (rcvbuf > 0) ::setsockopt(s_, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvbuf), sizeof(rcvbuf));
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = 0;
    REQUIRE(::bind(s_, reinterpret_cast<sockaddr*>(&a), sizeof(a)) == 0);
    REQUIRE(::listen(s_, 8) == 0);
    int len = sizeof(a);
    REQUIRE(::getsockname(s_, reinterpret_cast<sockaddr*>(&a), &len) == 0);
    port_ = ntohs(a.sin_port);
  }
  ~LoopServer() { close(); }
  LoopServer(const LoopServer&) = delete;
  LoopServer& operator=(const LoopServer&) = delete;
  void close() {
    if (s_ != INVALID_SOCKET) ::closesocket(s_);
    s_ = INVALID_SOCKET;
  }
  [[nodiscard]] std::uint16_t port() const { return port_; }
  [[nodiscard]] Endpoint endpoint() const { return Endpoint{"127.0.0.1", port_}; }

  Conn accept(std::chrono::milliseconds timeout = 5000ms) {
    WSAPOLLFD p{};
    p.fd = s_;
    p.events = POLLRDNORM;
    if (::WSAPoll(&p, 1, static_cast<INT>(timeout.count())) <= 0) return Conn{};
    return Conn(::accept(s_, nullptr, nullptr));
  }

 private:
  SOCKET s_ = INVALID_SOCKET;
  std::uint16_t port_ = 0;
};

// A port nobody listens on.
std::uint16_t dead_port() {
  LoopServer s;
  const auto p = s.port();
  s.close();
  return p;
}

// Collects inbox events across polls.
struct Events {
  std::vector<InboundEvent> all;
  void pump(NetClient& c) { c.poll_inbox(all); }
  [[nodiscard]] std::size_t count(InboundEvent::Kind k) const {
    std::size_t n = 0;
    for (const auto& e : all) n += e.kind == k ? 1 : 0;
    return n;
  }
  [[nodiscard]] const InboundEvent* find_frame(std::uint16_t type) const {
    for (const auto& e : all) {
      if (e.kind == InboundEvent::Kind::Frame && e.type == type) return &e;
    }
    return nullptr;
  }
  [[nodiscard]] const InboundEvent* find(InboundEvent::Kind k) const {
    for (const auto& e : all) {
      if (e.kind == k) return &e;
    }
    return nullptr;
  }
};

bool wait_events(NetClient& c, Events& ev, const std::function<bool()>& pred, std::chrono::milliseconds t = 5000ms) {
  return wait_until(
      [&] {
        ev.pump(c);
        return pred();
      },
      t);
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------

TEST_CASE("net: connects through the non-blocking WSAEWOULDBLOCK path and receives a frame", "[net][socket]") {
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  conn.send(hello_frame());

  Events ev;
  REQUIRE(wait_events(client, ev, [&] { return ev.find_frame(T(MsgType::ServerHello)) != nullptr; }));
  CHECK(ev.count(InboundEvent::Kind::Connected) == 1);
  // Connected came before the frame.
  CHECK(ev.all.front().kind == InboundEvent::Kind::Connected);
  const auto* hello = ev.find_frame(T(MsgType::ServerHello));
  const auto* h = flatbuffers::GetRoot<X4MP::Proto::ServerHello>(hello->payload.data());
  CHECK(h->server_name()->str() == "loopback");

  const NetStatus st = client.status();
  CHECK(st.state == ConnState::Connected);
  CHECK(st.connections == 1);
  CHECK(st.connect_attempt == 1);
  // PIT-022: on Windows a non-blocking connect() reports WSAEWOULDBLOCK and that must count as "in progress".
  CHECK(st.connect_wouldblock >= 1);
  client.stop();
  CHECK(client.status().state == ConnState::Stopped);
}

TEST_CASE("net: a frame split byte by byte arrives as one frame", "[net][socket][framing]") {
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());

  const auto bytes = hello_frame();
  // Two back-to-back frames, drip-fed one byte at a time.
  std::vector<std::uint8_t> both = bytes;
  both.insert(both.end(), bytes.begin(), bytes.end());
  std::thread feeder([&] {
    for (std::size_t i = 0; i < both.size(); ++i) {
      conn.send({both[i]});
      if (i < 24 || i % 16 == 0) std::this_thread::sleep_for(1ms);
    }
  });
  Events ev;
  const bool ok = wait_events(client, ev, [&] {
    std::size_t n = 0;
    for (const auto& e : ev.all) n += (e.kind == InboundEvent::Kind::Frame) ? 1 : 0;
    return n >= 2;
  });
  feeder.join();
  REQUIRE(ok);
  CHECK(client.status().state == ConnState::Connected);
  CHECK(client.status().frames_in == 2);
  for (const auto& e : ev.all) {
    if (e.kind == InboundEvent::Kind::Frame) CHECK(e.type == T(MsgType::ServerHello));
  }
  client.stop();
}

TEST_CASE("net: oversized frame disconnects with a reason and tells the peer", "[net][socket][framing]") {
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());

  // Header only: payload_len = 2 MiB (> the 1 MiB default), type ServerHello, lane Control. The client must
  // reject on the header alone, without waiting for 2 MiB of payload.
  std::vector<std::uint8_t> hdr(x4mp::wire::kFrameHeaderSize);
  x4mp::wire::write_frame_header(std::span<std::uint8_t, x4mp::wire::kFrameHeaderSize>(hdr), 2u << 20,
                                 T(MsgType::ServerHello), 0, Lane::Control);
  conn.send(hdr);

  Events ev;
  REQUIRE(wait_events(client, ev, [&] { return ev.find(InboundEvent::Kind::Disconnected) != nullptr; }));
  const auto* d = ev.find(InboundEvent::Kind::Disconnected);
  CHECK(d->reason == DisconnectReason::FrameTooLarge);
  CHECK(d->text.find("FrameTooLarge") != std::string::npos);
  // Best-effort Disconnect(MalformedMessage) reached the peer.
  const auto f = conn.read_frame_of(T(MsgType::Disconnect));
  REQUIRE(f.has_value());
  const auto* dc = flatbuffers::GetRoot<X4MP::Proto::Disconnect>(f->payload.data());
  CHECK(static_cast<std::uint16_t>(dc->code()) == kDisconnectMalformedMessage);
  CHECK(client.status().state == ConnState::Backoff);
  client.stop();
}

TEST_CASE("net: frames that fail validation disconnect", "[net][socket][framing]") {
  struct Case {
    const char* name;
    std::vector<std::uint8_t> bytes;
  };
  std::vector<Case> cases;
  // Garbage payload for a known type: the FlatBuffers Verifier must reject it.
  cases.push_back({"verifier", frame_bytes(T(MsgType::ServerHello), Lane::Control, std::vector<std::uint8_t>(24, 0xFF))});
  // Unknown message type.
  cases.push_back({"unknown type", frame_bytes(0x7777, Lane::Control, std::vector<std::uint8_t>(16, 0))});
  // Lane differs from the catalog lane (ServerHello is Control).
  {
    flatbuffers::FlatBufferBuilder fbb;
    fbb.Finish(X4MP::Proto::CreateServerHelloDirect(fbb, 0, 1, "x", "y"));
    cases.push_back({"lane mismatch", frame_bytes(T(MsgType::ServerHello), Lane::Bulk, to_vec(fbb))});
  }
  // Reserved flag bit set.
  {
    auto f = hello_frame();
    f[6] = 0x80;
    cases.push_back({"reserved flags", f});
  }
  // Zero-length payload.
  {
    std::vector<std::uint8_t> hdr(x4mp::wire::kFrameHeaderSize);
    x4mp::wire::write_frame_header(std::span<std::uint8_t, x4mp::wire::kFrameHeaderSize>(hdr), 0, T(MsgType::Ping), 0,
                                   Lane::Control);
    cases.push_back({"zero length", hdr});
  }

  for (const auto& c : cases) {
    DYNAMIC_SECTION(c.name) {
      LoopServer server;
      NetClient client;
      REQUIRE(client.start(server.endpoint()));
      Conn conn = server.accept();
      REQUIRE(conn.valid());
      conn.send(c.bytes);
      Events ev;
      REQUIRE(wait_events(client, ev, [&] { return ev.find(InboundEvent::Kind::Disconnected) != nullptr; }));
      CHECK(ev.find(InboundEvent::Kind::Disconnected)->reason == DisconnectReason::MalformedFrame);
      CHECK(ev.find_frame(T(MsgType::ServerHello)) == nullptr);  // nothing invalid reached the main thread
      client.stop();
    }
  }
}

TEST_CASE("net: write cap overflow when the peer stops reading", "[net][socket][cap]") {
  LoopServer server(4096);  // tiny receive window, and the test never reads
  NetOptions opt;
  opt.write_cap_bytes = 256 * 1024;
  opt.send_buffer_bytes = 8 * 1024;
  NetClient client;
  REQUIRE(client.start(server.endpoint(), opt));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  Events ev;
  REQUIRE(wait_events(client, ev, [&] { return client.status().state == ConnState::Connected; }));

  const std::vector<std::uint8_t> payload(32 * 1024, 0x55);
  bool disconnected = false;
  for (int i = 0; i < 2000 && !disconnected; ++i) {
    (void)client.send(Lane::Control, T(MsgType::Welcome), payload);
    std::this_thread::sleep_for(1ms);
    ev.pump(client);
    disconnected = ev.find(InboundEvent::Kind::Disconnected) != nullptr;
  }
  REQUIRE(wait_events(client, ev, [&] { return ev.find(InboundEvent::Kind::Disconnected) != nullptr; }));
  CHECK(ev.find(InboundEvent::Kind::Disconnected)->reason == DisconnectReason::WriteBufferOverflow);
  CHECK(client.status().last_reason == DisconnectReason::WriteBufferOverflow);
  client.stop();
}

TEST_CASE("net: answers server pings with a pong", "[net][socket][ping]") {
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  conn.send(ping_frame(7, 123456));
  const auto f = conn.read_frame_of(T(MsgType::Pong));
  REQUIRE(f.has_value());
  const auto* p = flatbuffers::GetRoot<X4MP::Proto::Pong>(f->payload.data());
  CHECK(p->seq() == 7);
  CHECK(p->echo_send_time_us() == 123456);
  CHECK(p->reply_time_us() >= p->recv_time_us());
  // Pings are net-thread business: they never reach the inbox.
  Events ev;
  ev.pump(client);
  CHECK(ev.find_frame(T(MsgType::Ping)) == nullptr);
  client.stop();
}

TEST_CASE("net: own pings produce RTT and clock offset from the pong", "[net][socket][ping][clock]") {
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  Events ev0;
  REQUIRE(wait_events(client, ev0, [&] { return client.status().state == ConnState::Connected; }));
  client.enable_heartbeat();  // the session layer does this after ClientHello
  const auto f = conn.read_frame_of(T(MsgType::Ping));  // the first heartbeat goes out right away
  REQUIRE(f.has_value());
  const auto* ping = flatbuffers::GetRoot<X4MP::Proto::Ping>(f->payload.data());
  // The server clock runs 1 s ahead of the client's.
  const std::uint64_t recv = ping->send_time_us() + 1'000'000;
  conn.send(pong_frame(ping->seq(), ping->send_time_us(), recv, recv + 10));

  REQUIRE(wait_until([&] { return client.status().clock_valid == 1; }));
  const NetStatus st = client.status();
  CHECK(st.rtt_us >= 0);
  CHECK(st.rtt_us < 200'000);
  CHECK(st.rtt_min_us == st.rtt_last_us);
  CHECK(st.clock_offset_us > 1'000'000 - 100'000);
  CHECK(st.clock_offset_us <= 1'000'000);
  client.stop();
}

TEST_CASE("net: no own pings before enable_heartbeat; silence then times out", "[net][socket][ping]") {
  LoopServer server;
  NetOptions opt;
  opt.heartbeat_interval_ms = 20;
  opt.heartbeat_timeout_ms = 300;
  NetClient client;
  REQUIRE(client.start(server.endpoint(), opt));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  CHECK_FALSE(conn.readable(250));  // handshake phase: the client sends nothing on its own
  CHECK(client.status().state == ConnState::Connected);  // and the liveness timeout is not armed either

  client.enable_heartbeat();
  CHECK(conn.readable(500));  // pings flow now
  Events ev;
  REQUIRE(wait_events(client, ev, [&] { return ev.find(InboundEvent::Kind::Disconnected) != nullptr; }));
  CHECK(ev.find(InboundEvent::Kind::Disconnected)->reason == DisconnectReason::HeartbeatTimeout);
  client.stop();
}

TEST_CASE("net: refused connect backs off 1/2/4 s on the injected clock", "[net][socket][backoff]") {
  std::atomic<std::int64_t> now{1'000'000'000};
  NetOptions opt;
  opt.clock = [&now] { return now.load(); };
  NetClient client;
  REQUIRE(client.start(Endpoint{"127.0.0.1", dead_port()}, opt));

  // Attempt 1 fails (Windows takes a moment to report a refused loopback connect), then the client waits 1 s.
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }, 15000ms));
  NetStatus st = client.status();
  CHECK(st.connect_attempt == 1);
  CHECK(st.last_reason == DisconnectReason::ConnectFailed);
  CHECK(st.next_retry_us == 1'000'000'000 + 1'000'000);

  // Time stands still: no new attempt, however long we wait in real time.
  std::this_thread::sleep_for(100ms);
  CHECK(client.status().connect_attempt == 1);
  CHECK(client.status().state == ConnState::Backoff);

  // 0.9 s later: still waiting. 1.0 s later: attempt 2 starts, fails, and the next wait is 2 s.
  now += 900'000;
  std::this_thread::sleep_for(50ms);
  CHECK(client.status().connect_attempt == 1);
  now += 100'000;
  REQUIRE(wait_until([&] { return client.status().connect_attempt == 2; }));
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }, 15000ms));
  st = client.status();
  CHECK(st.next_retry_us == 1'000'000'000 + 1'000'000 + 2'000'000);

  now += 2'000'000;
  REQUIRE(wait_until([&] { return client.status().connect_attempt == 3; }));
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }, 15000ms));
  CHECK(client.status().next_retry_us == 1'000'000'000 + 3'000'000 + 4'000'000);

  // reconnect_now() resets the schedule and skips the wait.
  client.reconnect_now();
  REQUIRE(wait_until([&] { return client.status().connect_attempt == 4; }));
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }, 15000ms));
  CHECK(client.status().next_retry_us == now.load() + 1'000'000);  // back to 1 s
  client.stop();
}

TEST_CASE("net: reconnects after the server drops the connection", "[net][socket][backoff]") {
  std::atomic<std::int64_t> now{5'000'000};
  NetOptions opt;
  opt.clock = [&now] { return now.load(); };
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint(), opt));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  Events ev;
  REQUIRE(wait_events(client, ev, [&] { return client.status().state == ConnState::Connected; }));

  conn.close();  // server hangs up
  REQUIRE(wait_events(client, ev, [&] { return ev.find(InboundEvent::Kind::Disconnected) != nullptr; }));
  CHECK(ev.find(InboundEvent::Kind::Disconnected)->reason == DisconnectReason::RemoteClosed);
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }));
  CHECK(client.status().next_retry_us == 5'000'000 + 1'000'000);

  now += 1'000'000;
  Conn again = server.accept();
  REQUIRE(again.valid());
  again.send(hello_frame());
  REQUIRE(wait_events(client, ev, [&] { return ev.find_frame(T(MsgType::ServerHello)) != nullptr; }));
  CHECK(client.status().connections == 2);
  CHECK(client.status().state == ConnState::Connected);
  client.stop();
}

TEST_CASE("net: a server Disconnect is delivered, and no-retry codes halt reconnecting", "[net][socket]") {
  LoopServer server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  conn.send(disconnect_frame(3 /*Kicked*/));
  conn.close_gracefully();

  Events ev;
  REQUIRE(wait_events(client, ev, [&] { return ev.find(InboundEvent::Kind::Disconnected) != nullptr; }));
  CHECK(ev.find_frame(T(MsgType::Disconnect)) != nullptr);
  const auto* d = ev.find(InboundEvent::Kind::Disconnected);
  CHECK(d->reason == DisconnectReason::ServerDisconnect);
  CHECK(d->remote_code == 3);
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Halted; }));
  CHECK(client.status().last_remote_code == 3);
  std::this_thread::sleep_for(50ms);
  CHECK(client.status().connect_attempt == 1);  // not retrying

  client.reconnect_now();  // explicit user action: try again
  Conn again = server.accept();
  REQUIRE(again.valid());
  client.stop();
}

TEST_CASE("net: send() reaches the peer as a framed message; the frame hook can answer on the net thread",
          "[net][socket][hook]") {
  LoopServer server;
  NetOptions opt;
  opt.frame_hook = [](std::uint16_t type, Lane, std::span<const std::uint8_t>, HookContext& ctx) {
    if (type == static_cast<std::uint16_t>(MsgType::ServerHello)) {
      const std::vector<std::uint8_t> reply{1, 2, 3, 4};
      ctx.send(Lane::Control, static_cast<std::uint16_t>(MsgType::ClientHello), reply);
    }
  };
  NetClient client;
  REQUIRE(client.start(server.endpoint(), opt));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  conn.send(hello_frame());

  const auto hooked = conn.read_frame_of(T(MsgType::ClientHello));
  REQUIRE(hooked.has_value());
  CHECK(hooked->payload == std::vector<std::uint8_t>{1, 2, 3, 4});

  // Main-thread send, Control and Bulk.
  REQUIRE(wait_until([&] { return client.status().state == ConnState::Connected; }));
  const std::vector<std::uint8_t> a(100, 0xAA);
  const std::vector<std::uint8_t> b(50, 0xBB);
  CHECK(client.send(Lane::Bulk, T(MsgType::SaveChunkAck), b) == SendResult::Ok);
  CHECK(client.send(Lane::Control, T(MsgType::SaveStarted), a) == SendResult::Ok);
  // Bulk waits for Control to drain, so either order on the wire is legal; both must arrive intact.
  std::optional<Conn::Frame> fa;
  std::optional<Conn::Frame> fb;
  for (int i = 0; i < 20 && !(fa && fb); ++i) {
    auto f = conn.read_frame();
    REQUIRE(f.has_value());
    if (f->type == T(MsgType::SaveStarted)) fa = std::move(f);
    else if (f->type == T(MsgType::SaveChunkAck)) fb = std::move(f);
  }
  REQUIRE(fa.has_value());
  REQUIRE(fb.has_value());
  CHECK(fa->payload == a);
  CHECK(fa->lane == Lane::Control);
  CHECK(fb->payload == b);
  CHECK(fb->lane == Lane::Bulk);

  CHECK(client.send(Lane::Control, T(MsgType::SaveStarted), {}) == SendResult::EmptyPayload);
  client.stop();
  CHECK(client.send(Lane::Control, T(MsgType::SaveStarted), a) == SendResult::NotRunning);
}

TEST_CASE("net: stop() joins within 200 ms (connected, connecting, backing off)", "[net][socket][stop]") {
  const auto timed_stop = [](NetClient& c) {
    const auto t0 = std::chrono::steady_clock::now();
    c.stop();
    return std::chrono::steady_clock::now() - t0;
  };
  SECTION("connected") {
    LoopServer server;
    NetClient client;
    REQUIRE(client.start(server.endpoint()));
    Conn conn = server.accept();
    REQUIRE(conn.valid());
    REQUIRE(wait_until([&] { return client.status().state == ConnState::Connected; }));
    CHECK(timed_stop(client) < 200ms);
    // The peer sees a Disconnect(ClientQuit) before the close.
    const auto f = conn.read_frame_of(T(MsgType::Disconnect));
    REQUIRE(f.has_value());
    CHECK(static_cast<std::uint16_t>(flatbuffers::GetRoot<X4MP::Proto::Disconnect>(f->payload.data())->code()) ==
          kDisconnectClientQuit);
  }
  SECTION("connection attempt in flight to a dead port") {
    NetClient client;
    REQUIRE(client.start(Endpoint{"127.0.0.1", dead_port()}));
    std::this_thread::sleep_for(20ms);
    CHECK(timed_stop(client) < 200ms);
  }
  SECTION("backing off") {
    NetClient client;
    REQUIRE(client.start(Endpoint{"127.0.0.1", dead_port()}));
    REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }, 15000ms));
    CHECK(timed_stop(client) < 200ms);
  }
  SECTION("stop twice and restart") {
    LoopServer server;
    NetClient client;
    REQUIRE(client.start(server.endpoint()));
    CHECK_FALSE(client.start(server.endpoint()));  // already running
    client.stop();
    client.stop();
    REQUIRE(client.start(server.endpoint()));
    Conn conn = server.accept();
    CHECK(conn.valid());
    client.stop();
  }
}
