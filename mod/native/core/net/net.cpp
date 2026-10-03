#include "core/net/net.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <deque>
#include <thread>
#include <utility>

#include "control_generated.h"
#include "core/log/log.h"
#include "core/net/udp_lane.h"
#include "core/queue/queue.h"
#include "message_ids_generated.h"
#include <memory>
#include "x4mp/registry.h"
#include "x4mp/wire.h"

namespace x4mp::net {

std::string protocol_version_string() {
  return std::to_string(wire::kProtocolMajor) + "." + std::to_string(wire::kProtocolMinor);
}

int default_tcp_port() noexcept { return wire::kDefaultTcpPort; }

const char* to_string(DisconnectReason reason) noexcept {
  switch (reason) {
    case DisconnectReason::None: return "None";
    case DisconnectReason::ConnectFailed: return "ConnectFailed";
    case DisconnectReason::ConnectTimeout: return "ConnectTimeout";
    case DisconnectReason::RemoteClosed: return "RemoteClosed";
    case DisconnectReason::ServerDisconnect: return "ServerDisconnect";
    case DisconnectReason::SocketError: return "SocketError";
    case DisconnectReason::HeartbeatTimeout: return "HeartbeatTimeout";
    case DisconnectReason::MalformedFrame: return "MalformedFrame";
    case DisconnectReason::FrameTooLarge: return "FrameTooLarge";
    case DisconnectReason::WriteBufferOverflow: return "WriteBufferOverflow";
    case DisconnectReason::Requested: return "Requested";
  }
  return "?";
}

const char* to_string(ConnState state) noexcept {
  switch (state) {
    case ConnState::Stopped: return "Stopped";
    case ConnState::Connecting: return "Connecting";
    case ConnState::Connected: return "Connected";
    case ConnState::Backoff: return "Backoff";
    case ConnState::Halted: return "Halted";
  }
  return "?";
}

namespace {

constexpr std::size_t kInboxCapacity = 4096;
// Events the net thread may hold back when the inbox ring is full before it stops reading the socket (reliable
// frames) or drops (Realtime frames). TCP flow control then pushes back on the server.
constexpr std::size_t kPendingLimit = 2 * kInboxCapacity;
constexpr std::size_t kReadChunk = 64 * 1024;
constexpr DWORD kSioUdpConnReset = static_cast<DWORD>(0x9800000C);  // SIO_UDP_CONNRESET (mstcpip.h) = _WSAIOW(IOC_VENDOR, 12)

std::int64_t steady_us() noexcept {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch())
      .count();
}

// Single owner of a SOCKET (PIT-028): closing happens in exactly one place.
class Socket {
 public:
  Socket() = default;
  explicit Socket(SOCKET s) noexcept : s_(s) {}
  Socket(const Socket&) = delete;
  Socket& operator=(const Socket&) = delete;
  Socket(Socket&& o) noexcept : s_(std::exchange(o.s_, INVALID_SOCKET)) {}
  Socket& operator=(Socket&& o) noexcept {
    if (this != &o) {
      close();
      s_ = std::exchange(o.s_, INVALID_SOCKET);
    }
    return *this;
  }
  ~Socket() { close(); }
  void close() noexcept {
    if (s_ != INVALID_SOCKET) {
      ::closesocket(s_);
      s_ = INVALID_SOCKET;
    }
  }
  [[nodiscard]] SOCKET get() const noexcept { return s_; }
  [[nodiscard]] bool valid() const noexcept { return s_ != INVALID_SOCKET; }

 private:
  SOCKET s_ = INVALID_SOCKET;
};

struct ResolvedAddr {
  int family = AF_INET;
  sockaddr_storage addr{};
  int len = 0;
};

}  // namespace

// A fully framed outbound message.
struct OutFrame {
  Lane lane = Lane::Control;
  std::vector<std::uint8_t> bytes;
};

struct NetClient::Impl final : HookContext {
  Impl(Endpoint endpoint, NetOptions options)
      : ep(std::move(endpoint)),
        opt(std::move(options)),
        clock(opt.clock ? opt.clock : std::function<std::int64_t()>(&steady_us)),
        outbox(make_outbox_options(*this)),
        backoff(opt.backoff_first_ms, opt.backoff_cap_ms),
        udp_lane(opt.udp, [this](wire::ByteSpan d) { return udp_send(d); }) {
    udp_block_flag.store(opt.udp.block);
  }

  static queue::ReliableOutbox<OutFrame>::Options make_outbox_options(Impl& self) {
    queue::ReliableOutbox<OutFrame>::Options o;
    o.byte_cap = self.opt.write_cap_bytes;
    o.droppable = [](const OutFrame& f) { return f.lane == Lane::Realtime; };
    o.on_drop = [&self](OutFrame&&, std::size_t) { self.outbox_evicted.fetch_add(1, std::memory_order_relaxed); };
    return o;
  }

  // ---- shared (main thread <-> net thread) --------------------------------------------------------
  Endpoint ep;
  NetOptions opt;
  std::function<std::int64_t()> clock;
  queue::SpscRing<InboundEvent, kInboxCapacity> inbox;          // producer: net thread, consumer: main
  queue::ReliableOutbox<OutFrame> outbox;                        // producer: main, consumer: net thread
  queue::LatestWinsSlot<NetStatus> status_slot;                  // SINGLE writer: the net thread
  std::atomic<bool> stop_flag{false};
  std::atomic<bool> reconnect_flag{false};
  std::atomic<bool> outbox_overflow{false};
  std::atomic<bool> heartbeat_enabled{false};
  std::atomic<std::uint16_t> goodbye_code{kDisconnectClientQuit};
  std::atomic<std::uint64_t> outbox_evicted{0};
  std::atomic<bool> udp_block_flag{false};
  std::atomic<bool> started{false};
  std::thread thread;

  // ---- net-thread state (only touched by run() and what it calls) ---------------------------------
  ConnState phase = ConnState::Connecting;
  Socket sock;
  std::vector<ResolvedAddr> addrs;
  std::size_t addr_idx = 0;
  std::int64_t connect_deadline_us = 0;
  bool connect_timed_out = false;
  int last_connect_error = 0;
  std::int64_t retry_at_us = 0;
  Backoff backoff;
  std::int64_t connected_at_us = -1;
  UdpLane udp_lane;  // declared after backoff (constructor order); owned by the net thread
  Socket udp_sock;
  std::vector<std::uint8_t> udp_rx = std::vector<std::uint8_t>(2048);
  std::uint64_t udp_verify_failures = 0;

  std::vector<std::uint8_t> rbuf;
  std::size_t roff = 0;
  std::vector<std::uint8_t> read_tmp = std::vector<std::uint8_t>(kReadChunk);
  std::vector<std::uint8_t> wbuf;
  std::size_t woff = 0;
  std::deque<std::vector<std::uint8_t>> bulkq;
  std::size_t bulk_bytes = 0;
  std::deque<InboundEvent> pending;
  std::vector<OutFrame> drain_batch;
  // Reliable-Control retention (NetOptions::control_retention_*): only used in gated mode.
  std::deque<OutFrame> retained;
  std::size_t retained_bytes = 0;
  bool gate_open = true;
  bool consume_current = false;

  std::uint32_t ping_seq = 0;
  std::int64_t next_ping_us = 0;
  bool hb_active = false;  // own pings + liveness timeout armed (the main thread called enable_heartbeat)
  std::int64_t last_rx_us = 0;
  std::int64_t rx_stamp_us = 0;  // clock value right after the latest recv()
  ClockSync sync;
  bool got_remote_disconnect = false;
  std::uint16_t remote_code = 0;
  std::uint32_t remote_retry_ms = 0;

  NetStatus st;
  bool dirty = true;
  std::int64_t last_publish_us = 0;

  // =================================================================================================
  // helpers
  // =================================================================================================
  void publish(std::int64_t now) {
    st.state = phase;
    st.rtt_us = sync.smoothed_rtt_us();
    st.rtt_last_us = sync.last_rtt_us();
    if (const auto b = sync.best()) st.rtt_min_us = b->rtt_us;
    st.clock_valid = sync.has_offset() ? 1 : 0;
    st.clock_offset_us = sync.applied_offset_us();
    st.write_buffer_bytes = pending_write_bytes();
    st.control_retained_frames = static_cast<std::uint32_t>(retained.size());
    st.control_retained_bytes = retained_bytes;
    st.outbox_dropped = dropped_not_connected + outbox_evicted.load(std::memory_order_relaxed);
    st.udp = udp_lane.stats();
    st.udp.malformed += udp_verify_failures;
    status_slot.publish(st);
    dirty = false;
    last_publish_us = now;
  }
  std::uint64_t dropped_not_connected = 0;

  [[nodiscard]] std::size_t pending_write_bytes() const noexcept { return (wbuf.size() - woff) + bulk_bytes; }

  void flush_pending() {
    while (!pending.empty()) {
      if (!inbox.try_push(std::move(pending.front()))) break;  // a failed push leaves the element intact
      pending.pop_front();
    }
  }

  void push_event(InboundEvent&& e) { pending.push_back(std::move(e)); }

  // Appends a complete frame to the write path (net thread only).
  bool append_frame(OutFrame&& f) {
    ++st.frames_out;
    // Realtime frames ride the UDP lane while it is Active (batched into datagrams; flushed at the end of the drain).
    if (f.lane == Lane::Realtime && udp_lane.state() != UdpState::Off && f.bytes.size() > wire::kFrameHeaderSize) {
      const std::uint16_t type = static_cast<std::uint16_t>(f.bytes[4] | (f.bytes[5] << 8));
      if (udp_lane.send_realtime(type, wire::ByteSpan(f.bytes.data() + wire::kFrameHeaderSize, f.bytes.size() - wire::kFrameHeaderSize),
                             clock())) {
        return true;
      }
    }
    if (f.lane == Lane::Bulk) {
      bulk_bytes += f.bytes.size();
      bulkq.push_back(std::move(f.bytes));
    } else {
      wbuf.insert(wbuf.end(), f.bytes.begin(), f.bytes.end());
    }
    return true;
  }

  static bool build_frame(OutFrame& out, Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload,
                          std::uint32_t max_frame_bytes) {
    out.lane = lane;
    out.bytes.assign(wire::kFrameHeaderSize + payload.size(), 0);
    const auto r = wire::encode_frame(out.bytes, type, lane, payload, max_frame_bytes);
    return r.has_value();
  }

  // HookContext
  bool send(Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) override {
    OutFrame f;
    if (phase != ConnState::Connected || !build_frame(f, lane, type, payload, opt.max_frame_bytes)) return false;
    return append_frame(std::move(f));
  }

  [[nodiscard]] bool gating() const noexcept { return opt.gate_outbox_until_released; }

  // Keeps a Control frame for a later resume if the caps allow, else refuses it (counted).
  void retain(OutFrame&& f) {
    const std::size_t bytes = f.bytes.size();
    if (retained.size() >= opt.control_retention_frames || retained_bytes + bytes > opt.control_retention_bytes) {
      ++st.control_retention_overflow;
      ++dropped_not_connected;
      dirty = true;
      return;
    }
    retained_bytes += bytes;
    retained.push_back(std::move(f));
    dirty = true;
  }

  void discard_retained() {
    dropped_not_connected += retained.size();
    retained.clear();
    retained_bytes = 0;
  }

  // HookContext
  std::size_t release_outbox(bool replay_control) override {
    if (gate_open) return 0;
    gate_open = true;
    const std::size_t n = retained.size();
    if (replay_control) {
      for (auto& f : retained) (void)append_frame(std::move(f));
      st.control_replayed += n;
    } else {
      st.control_discarded_fresh += n;
    }
    retained.clear();
    retained_bytes = 0;
    dirty = true;
    return n;
  }
  void consume_frame() override { consume_current = true; }

  // ---- UDP realtime lane (M3-03) -----------------------------------------------------------------
  bool udp_send(wire::ByteSpan d) {
    if (!udp_sock.valid()) return false;
    const int n = ::send(udp_sock.get(), reinterpret_cast<const char*>(d.data()), static_cast<int>(d.size()), 0);
    return n == static_cast<int>(d.size());
  }

  void close_udp() {
    udp_lane.stop();
    udp_sock.close();
  }

  // HookContext. Called from the Welcome on the net thread, so the TCP connection's address (addrs[addr_idx]) is known.
  void start_udp(std::uint32_t conn_id, std::uint16_t port, std::uint64_t token) override {
    close_udp();
    if (opt.udp.mode == UdpMode::Off || port == 0 || phase != ConnState::Connected || addr_idx >= addrs.size()) return;
    ResolvedAddr a = addrs[addr_idx];
    if (a.family == AF_INET) {
      reinterpret_cast<sockaddr_in*>(&a.addr)->sin_port = htons(port);
    } else if (a.family == AF_INET6) {
      reinterpret_cast<sockaddr_in6*>(&a.addr)->sin6_port = htons(port);
    } else {
      return;
    }
    Socket s(::socket(a.family, SOCK_DGRAM, IPPROTO_UDP));
    if (!s.valid()) {
      X4MP_LOGW("net: udp: socket() failed (winsock error {}); Realtime stays on TCP", ::WSAGetLastError());
      return;
    }
    u_long nonblocking = 1;
    (void)::ioctlsocket(s.get(), FIONBIO, &nonblocking);
    DWORD bytes = 0;
    BOOL no_reset = FALSE;  // SIO_UDP_CONNRESET off: an ICMP port-unreachable must not break receives
    (void)::WSAIoctl(s.get(), kSioUdpConnReset, &no_reset, sizeof(no_reset), nullptr, 0, &bytes, nullptr, nullptr);
    const int rcvbuf = 1 << 20;
    (void)::setsockopt(s.get(), SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvbuf), sizeof(rcvbuf));
    // Connected UDP socket: the kernel then drops datagrams from any other source.
    if (::connect(s.get(), reinterpret_cast<const sockaddr*>(&a.addr), a.len) != 0) {
      X4MP_LOGW("net: udp: connect() failed (winsock error {}); Realtime stays on TCP", ::WSAGetLastError());
      return;
    }
    udp_sock = std::move(s);
    udp_lane.start(conn_id, token, clock());
    X4MP_LOGI("net: udp: binding to {}:{} (mode {})", ep.host, port, to_string(opt.udp.mode));
    dirty = true;
  }

  void on_udp_message(std::uint16_t type, wire::ByteSpan payload) {
    const wire::MessageDescriptor* d = wire::find_message(type);
    if (d == nullptr) {
      ++udp_verify_failures;
      return;
    }
    wire::FrameView view;
    view.header.payload_length = static_cast<std::uint32_t>(payload.size());
    view.header.type = type;
    view.header.flags = 0;
    view.header.lane = d->lane;
    view.payload = payload;
    view.consumed = wire::kFrameHeaderSize + payload.size();
    if (!wire::validate_frame(view)) {  // the FlatBuffers Verifier runs before anything is read (ADR-041)
      ++udp_verify_failures;
      return;
    }
    ++st.frames_in;
    dirty = true;
    handle_frame(view);
  }

  void do_udp_read() {
    for (int i = 0; i < 64; ++i) {
      const int n = ::recv(udp_sock.get(), reinterpret_cast<char*>(udp_rx.data()), static_cast<int>(udp_rx.size()), 0);
      if (n < 0) {
        if (::WSAGetLastError() == WSAECONNRESET) continue;  // stray ICMP on some stacks
        break;                                               // WSAEWOULDBLOCK: drained
      }
      rx_stamp_us = clock();
      udp_lane.on_datagram(wire::ByteSpan(udp_rx.data(), static_cast<std::size_t>(n)), rx_stamp_us,
                       [this](std::uint16_t type, wire::ByteSpan payload) { on_udp_message(type, payload); });
      dirty = true;
    }
  }

  void send_disconnect_frame(std::uint16_t code, const std::string& message) {
    flatbuffers::FlatBufferBuilder fbb(128);
    fbb.Finish(X4MP::Proto::CreateDisconnectDirect(fbb, static_cast<X4MP::Proto::DisconnectCode>(code),
                                                   message.c_str()));
    (void)send(Lane::Control, static_cast<std::uint16_t>(X4MP::Proto::MsgType::Disconnect),
               std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
  }

  // Best effort, non-blocking: queue a Disconnect frame and give the socket one chance to take it.
  void goodbye(std::uint16_t code, const std::string& message) {
    if (phase != ConnState::Connected) return;
    send_disconnect_frame(code, message);
    flush_write_once();
  }

  // =================================================================================================
  // connect
  // =================================================================================================
  void resolve(std::int64_t now) {
    addrs.clear();
    addr_idx = 0;
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    addrinfo* res = nullptr;
    const std::string port = std::to_string(ep.port);
    const int rc = ::getaddrinfo(ep.host.c_str(), port.c_str(), &hints, &res);
    if (rc != 0 || res == nullptr) {
      last_connect_error = rc != 0 ? rc : WSAHOST_NOT_FOUND;
      return;
    }
    for (int pass = 0; pass < 2; ++pass) {  // IPv4 first: the server's default listener is IPv4
      for (const addrinfo* p = res; p != nullptr; p = p->ai_next) {
        const bool v4 = p->ai_family == AF_INET;
        if ((pass == 0) != v4) continue;
        if (p->ai_addrlen > sizeof(sockaddr_storage)) continue;
        ResolvedAddr a;
        a.family = p->ai_family;
        a.len = static_cast<int>(p->ai_addrlen);
        std::memcpy(&a.addr, p->ai_addr, p->ai_addrlen);
        addrs.push_back(a);
      }
    }
    ::freeaddrinfo(res);
    (void)now;
  }

  void begin_connect(std::int64_t now) {
    ++st.connect_attempt;
    phase = ConnState::Connecting;
    connect_timed_out = false;
    last_connect_error = 0;
    dirty = true;
    resolve(now);
    try_next_addr(now);
  }

  // Starts a non-blocking connect to the next candidate address. On Windows connect() on a non-blocking socket
  // returns SOCKET_ERROR/WSAEWOULDBLOCK: that is "in progress", NOT a failure (PIT-022).
  void try_next_addr(std::int64_t now) {
    while (addr_idx < addrs.size()) {
      const ResolvedAddr& a = addrs[addr_idx];
      Socket s(::socket(a.family, SOCK_STREAM, IPPROTO_TCP));
      if (!s.valid()) {
        last_connect_error = ::WSAGetLastError();
        ++addr_idx;
        continue;
      }
      u_long nonblocking = 1;
      if (::ioctlsocket(s.get(), FIONBIO, &nonblocking) != 0) {
        last_connect_error = ::WSAGetLastError();
        ++addr_idx;
        continue;
      }
      const BOOL nodelay = TRUE;
      (void)::setsockopt(s.get(), IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&nodelay), sizeof(nodelay));
      if (opt.send_buffer_bytes > 0) {
        (void)::setsockopt(s.get(), SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&opt.send_buffer_bytes),
                           sizeof(opt.send_buffer_bytes));
      }
      const int rc = ::connect(s.get(), reinterpret_cast<const sockaddr*>(&a.addr), a.len);
      if (rc == 0) {
        sock = std::move(s);
        on_connected(now);
        return;
      }
      const int err = ::WSAGetLastError();
      if (err == WSAEWOULDBLOCK || err == WSAEINPROGRESS) {
        if (err == WSAEWOULDBLOCK) ++st.connect_wouldblock;
        sock = std::move(s);
        connect_deadline_us = now + opt.connect_timeout_ms * 1000;
        return;
      }
      last_connect_error = err;
      ++addr_idx;
    }
    fail_connect();
  }

  void fail_connect() {
    const std::int64_t now = clock();
    std::string text = connect_timed_out ? "connect timed out" : "connect failed (winsock error " + std::to_string(last_connect_error) + ")";
    X4MP_LOGW("net: {} to {}:{}", text, ep.host, ep.port);
    finish_connection(connect_timed_out ? DisconnectReason::ConnectTimeout : DisconnectReason::ConnectFailed,
                      std::move(text), 0, now);
  }

  void poll_connect(std::int64_t now) {
    WSAPOLLFD pfd{};
    pfd.fd = sock.get();
    pfd.events = POLLWRNORM;
    const int rc = ::WSAPoll(&pfd, 1, static_cast<INT>(opt.poll_timeout_ms));
    if (rc > 0) {
      int err = 0;
      int len = sizeof(err);
      if (::getsockopt(sock.get(), SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&err), &len) != 0) {
        err = ::WSAGetLastError();
      }
      if (err == 0 && (pfd.revents & POLLWRNORM) != 0 && (pfd.revents & (POLLERR | POLLHUP)) == 0) {
        on_connected(now);
        return;
      }
      last_connect_error = err != 0 ? err : WSAECONNREFUSED;
      sock.close();
      ++addr_idx;
      try_next_addr(now);
    } else if (rc == SOCKET_ERROR) {
      last_connect_error = ::WSAGetLastError();
      sock.close();
      ++addr_idx;
      try_next_addr(now);
    } else if (now >= connect_deadline_us) {
      connect_timed_out = true;
      sock.close();
      ++addr_idx;
      try_next_addr(now);
    }
  }

  void on_connected(std::int64_t now) {
    phase = ConnState::Connected;
    ++st.connections;
    st.last_reason = DisconnectReason::None;
    connected_at_us = now;
    last_rx_us = now;
    next_ping_us = now;
    hb_active = false;
    heartbeat_enabled.store(false);  // a new connection starts a new handshake; pings wait for it
    rbuf.clear();
    roff = 0;
    wbuf.clear();
    woff = 0;
    bulkq.clear();
    bulk_bytes = 0;
    sync.reset();
    got_remote_disconnect = false;
    remote_code = 0;
    remote_retry_ms = 0;
    consume_current = false;
    if (gating()) {
      // Frames queued before this connection (retained, or still in the outbox) wait behind the closed gate until
      // the session layer decides between replay (resume) and discard (fresh join).
      gate_open = false;
    } else {
      // M1-N2 behaviour: anything queued before the connection existed belongs to a dead handshake.
      gate_open = true;
      drain_batch.clear();
      outbox.take_all(drain_batch);
      dropped_not_connected += drain_batch.size();
      drain_batch.clear();
    }
    outbox_overflow.store(false);
    InboundEvent ev;
    ev.kind = InboundEvent::Kind::Connected;
    push_event(std::move(ev));
    dirty = true;
    X4MP_LOGI("net: connected to {}:{}", ep.host, ep.port);
  }

  // Ends a connection or a failed connect attempt and decides what happens next.
  void finish_connection(DisconnectReason reason, std::string text, std::uint16_t code, std::int64_t now) {
    const bool was_connected = connected_at_us >= 0;
    close_udp();
    sock.close();
    addrs.clear();
    addr_idx = 0;
    wbuf.clear();
    woff = 0;
    bulkq.clear();
    bulk_bytes = 0;
    rbuf.clear();
    roff = 0;
    if (was_connected && now - connected_at_us >= opt.backoff_reset_after_ms * 1000) backoff.reset();
    connected_at_us = -1;
    st.last_reason = reason;
    if (reason == DisconnectReason::ServerDisconnect) {
      st.last_remote_code = code;
      st.last_retry_after_ms = remote_retry_ms;
    }
    X4MP_LOGI("net: disconnected ({}): {}", to_string(reason), text);
    InboundEvent ev;
    ev.kind = InboundEvent::Kind::Disconnected;
    ev.reason = reason;
    ev.remote_code = code;
    ev.text = std::move(text);
    push_event(std::move(ev));
    dirty = true;
    if (reason == DisconnectReason::Requested) {
      phase = ConnState::Stopped;
      return;
    }
    if (reason == DisconnectReason::ServerDisconnect && is_no_retry_code(code)) {
      phase = ConnState::Halted;
      return;
    }
    std::int64_t delay_ms = backoff.next_delay_ms();
    if (reason == DisconnectReason::ServerDisconnect && remote_retry_ms > 0) {
      delay_ms = std::max<std::int64_t>(delay_ms, remote_retry_ms);
    }
    retry_at_us = now + delay_ms * 1000;
    st.next_retry_us = retry_at_us;
    phase = ConnState::Backoff;
  }

  // Ends the live connection from our side, with a best-effort Disconnect frame first where it makes sense.
  void drop_connection(DisconnectReason reason, std::string text, std::uint16_t send_code = 0) {
    if (send_code != 0) goodbye(send_code, text);
    const std::uint16_t code = reason == DisconnectReason::ServerDisconnect ? remote_code : 0;
    finish_connection(reason, std::move(text), code, clock());
  }

  // =================================================================================================
  // connected: write, read, frames
  // =================================================================================================
  void drain_outbox(bool connected) {
    drain_batch.clear();
    outbox.take_all(drain_batch);
    for (auto& f : drain_batch) {
      if (connected && gate_open) {
        (void)append_frame(std::move(f));
      } else if (gating() && f.lane == Lane::Control) {
        retain(std::move(f));
      } else {
        ++dropped_not_connected;
      }
    }
    drain_batch.clear();
    if (connected && udp_lane.state() != UdpState::Off) udp_lane.flush(clock());
  }

  // Sends as much as the socket takes without blocking. Returns false if the connection was dropped.
  bool flush_write_once() {
    if (!sock.valid()) return false;
    for (;;) {
      if (woff == wbuf.size()) {
        wbuf.clear();
        woff = 0;
        if (bulkq.empty()) return true;
        // Bulk is drained only when the Control/Realtime path is empty (protocol.md 3.4).
        wbuf = std::move(bulkq.front());
        bulkq.pop_front();
        bulk_bytes -= wbuf.size();
      }
      const std::size_t remaining = wbuf.size() - woff;
      const int want = static_cast<int>(std::min<std::size_t>(remaining, 1u << 20));
      const int n = ::send(sock.get(), reinterpret_cast<const char*>(wbuf.data() + woff), want, 0);
      if (n == SOCKET_ERROR) {
        const int err = ::WSAGetLastError();
        if (err == WSAEWOULDBLOCK) break;
        drop_connection(DisconnectReason::SocketError, "send failed (winsock error " + std::to_string(err) + ")");
        return false;
      }
      woff += static_cast<std::size_t>(n);
      st.bytes_out += static_cast<std::uint64_t>(n);
      if (n < want) break;
    }
    if (woff > (1u << 20) && woff * 2 > wbuf.size()) {
      wbuf.erase(wbuf.begin(), wbuf.begin() + static_cast<std::ptrdiff_t>(woff));
      woff = 0;
    }
    return true;
  }

  void do_read() {
    bool eof = false;
    for (int iter = 0; iter < 16 && !eof; ++iter) {
      const int n = ::recv(sock.get(), reinterpret_cast<char*>(read_tmp.data()), static_cast<int>(read_tmp.size()), 0);
      if (n > 0) {
        rx_stamp_us = clock();
        last_rx_us = rx_stamp_us;
        st.bytes_in += static_cast<std::uint64_t>(n);
        rbuf.insert(rbuf.end(), read_tmp.begin(), read_tmp.begin() + n);
        if (static_cast<std::size_t>(n) < read_tmp.size()) break;
      } else if (n == 0) {
        eof = true;
      } else {
        const int err = ::WSAGetLastError();
        if (err == WSAEWOULDBLOCK) break;
        // Frames that arrived before the error are still good (e.g. a Disconnect followed by RST).
        if (!process_frames()) return;
        if (err == WSAECONNRESET || err == WSAECONNABORTED) {  // the peer closed with unread data: still a close
          drop_connection(got_remote_disconnect ? DisconnectReason::ServerDisconnect : DisconnectReason::RemoteClosed,
                          "connection reset by peer");
        } else {
          drop_connection(DisconnectReason::SocketError, "recv failed (winsock error " + std::to_string(err) + ")");
        }
        return;
      }
    }
    if (!process_frames()) return;
    if (eof) {
      if (got_remote_disconnect) {
        drop_connection(DisconnectReason::ServerDisconnect, "server sent Disconnect code " + std::to_string(remote_code));
      } else {
        drop_connection(DisconnectReason::RemoteClosed, "connection closed by peer");
      }
    }
  }

  // Returns false if the connection was dropped.
  bool process_frames() {
    for (;;) {
      const wire::ByteSpan view(rbuf.data() + roff, rbuf.size() - roff);
      const auto decoded = wire::try_decode_frame(view, opt.max_frame_bytes);
      if (!decoded) {
        reject(decoded.error());
        return false;
      }
      if (!decoded->has_value()) break;  // partial frame: wait for more bytes
      const wire::FrameView& frame = **decoded;
      if (frame.header.lane != Lane::Realtime && pending.size() >= kPendingLimit) break;  // backpressure
      const auto valid = wire::validate_frame(frame);
      if (!valid) {
        reject(valid.error());
        return false;
      }
      ++st.frames_in;
      dirty = true;
      handle_frame(frame);
      roff += frame.consumed;
      if (!sock.valid()) return false;
    }
    if (roff > 0 && (roff == rbuf.size() || roff > kReadChunk)) {
      rbuf.erase(rbuf.begin(), rbuf.begin() + static_cast<std::ptrdiff_t>(roff));
      roff = 0;
    }
    return true;
  }

  void reject(wire::ViolationCode code) {
    const std::string text = std::string("invalid inbound frame: ") + wire::to_string(code);
    X4MP_LOGW("net: {}", text);
    const bool too_big = code == wire::ViolationCode::FrameTooLarge;
    drop_connection(too_big ? DisconnectReason::FrameTooLarge : DisconnectReason::MalformedFrame, text,
                    kDisconnectMalformedMessage);
  }

  void handle_frame(const wire::FrameView& frame) {
    using X4MP::Proto::MsgType;
    const std::uint16_t type = frame.header.type;
    const std::uint8_t* data = frame.payload.data();
    if (type == static_cast<std::uint16_t>(MsgType::Ping)) {  // answered here, never queued to main
      const auto* ping = flatbuffers::GetRoot<X4MP::Proto::Ping>(data);
      flatbuffers::FlatBufferBuilder fbb(64);
      fbb.Finish(X4MP::Proto::CreatePong(fbb, ping->seq(), ping->send_time_us(),
                                         static_cast<std::uint64_t>(rx_stamp_us), static_cast<std::uint64_t>(clock())));
      (void)send(Lane::Control, static_cast<std::uint16_t>(MsgType::Pong),
                 std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
      return;
    }
    if (type == static_cast<std::uint16_t>(MsgType::Pong)) {
      const auto* pong = flatbuffers::GetRoot<X4MP::Proto::Pong>(data);
      (void)sync.add_pong(static_cast<std::int64_t>(pong->echo_send_time_us()),
                          static_cast<std::int64_t>(pong->recv_time_us()),
                          static_cast<std::int64_t>(pong->reply_time_us()), rx_stamp_us);
      sync.update(rx_stamp_us);
      return;
    }
    if (type == static_cast<std::uint16_t>(MsgType::Disconnect)) {
      const auto* d = flatbuffers::GetRoot<X4MP::Proto::Disconnect>(data);
      got_remote_disconnect = true;
      remote_code = static_cast<std::uint16_t>(d->code());
      remote_retry_ms = d->retry_after_ms();
    }
    consume_current = false;
    if (opt.frame_hook) opt.frame_hook(type, frame.header.lane, frame.payload, *this);
    if (consume_current) {
      consume_current = false;
      return;
    }
    if (frame.header.lane == Lane::Realtime && pending.size() >= kPendingLimit) {
      ++st.realtime_dropped_in;
      return;
    }
    InboundEvent ev;
    ev.kind = InboundEvent::Kind::Frame;
    ev.type = type;
    ev.lane = frame.header.lane;
    ev.payload.assign(frame.payload.begin(), frame.payload.end());
    push_event(std::move(ev));
  }

  void send_ping(std::int64_t now) {
    flatbuffers::FlatBufferBuilder fbb(48);
    fbb.Finish(X4MP::Proto::CreatePing(fbb, ++ping_seq, static_cast<std::uint64_t>(now)));
    (void)send(Lane::Control, static_cast<std::uint16_t>(X4MP::Proto::MsgType::Ping),
               std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
  }

  void connected_step(std::int64_t now) {
    drain_outbox(true);
    if (outbox_overflow.exchange(false)) {
      drop_connection(DisconnectReason::WriteBufferOverflow, "outbox over its byte cap");
      return;
    }
    if (!hb_active && heartbeat_enabled.load()) {
      hb_active = true;
      next_ping_us = now;
      last_rx_us = now;
    }
    if (hb_active && now >= next_ping_us) {
      send_ping(now);
      next_ping_us = now + opt.heartbeat_interval_ms * 1000;
    }
    if (hb_active && now - last_rx_us > opt.heartbeat_timeout_ms * 1000) {
      drop_connection(DisconnectReason::HeartbeatTimeout, "no data from the server", kDisconnectHeartbeatTimeout);
      return;
    }
    if (pending_write_bytes() > opt.write_cap_bytes) {
      // The peer is not reading: do not even try to say goodbye. SlowConsumer, local view.
      drop_connection(DisconnectReason::WriteBufferOverflow,
                      "write buffer over " + std::to_string(opt.write_cap_bytes) + " bytes (peer not reading)");
      return;
    }
    if (udp_lane.state() != UdpState::Off) {
      udp_lane.set_block(udp_block_flag.load(std::memory_order_relaxed));
      udp_lane.tick(now);
    }
    if (!flush_write_once()) return;

    const bool backpressured = pending.size() >= kPendingLimit;
    const bool want_write = pending_write_bytes() > 0;
    if (backpressured) {
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
      if (udp_sock.valid()) do_udp_read();
    } else {
      WSAPOLLFD fds[2]{};
      WSAPOLLFD& pfd = fds[0];
      pfd.fd = sock.get();
      pfd.events = static_cast<SHORT>(POLLRDNORM | (want_write ? POLLWRNORM : 0));
      const bool with_udp = udp_sock.valid();
      if (with_udp) {
        fds[1].fd = udp_sock.get();
        fds[1].events = POLLRDNORM;
      }
      const int rc = ::WSAPoll(fds, with_udp ? 2 : 1, static_cast<INT>(opt.poll_timeout_ms));
      if (rc == SOCKET_ERROR) {
        drop_connection(DisconnectReason::SocketError, "WSAPoll failed (winsock error " + std::to_string(::WSAGetLastError()) + ")");
        return;
      }
      if (rc > 0) {
        if ((pfd.revents & (POLLRDNORM | POLLHUP | POLLERR)) != 0) do_read();
        if (phase == ConnState::Connected && (pfd.revents & POLLWRNORM) != 0) (void)flush_write_once();
        if (with_udp && phase == ConnState::Connected && udp_sock.valid() && (fds[1].revents & (POLLRDNORM | POLLERR)) != 0) do_udp_read();
      }
    }
    if (phase == ConnState::Connected) {
      sync.update(clock());
    }
  }

  // =================================================================================================
  // thread body
  // =================================================================================================
  void run() {
    WSADATA wsa{};
    if (::WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
      X4MP_LOGE("net: WSAStartup failed");
      phase = ConnState::Stopped;
      publish(clock());
      return;
    }
    phase = ConnState::Connecting;
    {
      std::int64_t now = clock();
      begin_connect(now);
      publish(now);
    }
    while (!stop_flag.load(std::memory_order_acquire)) {
      const std::int64_t now = clock();
      if (reconnect_flag.exchange(false) && (phase == ConnState::Backoff || phase == ConnState::Halted)) {
        backoff.reset();
        retry_at_us = now;
        phase = ConnState::Backoff;
        dirty = true;
      }
      switch (phase) {
        case ConnState::Backoff:
          drain_outbox(false);
          if (now >= retry_at_us) {
            begin_connect(now);
          } else {
            std::this_thread::sleep_for(std::chrono::milliseconds(std::max<std::int64_t>(1, opt.poll_timeout_ms)));
          }
          break;
        case ConnState::Connecting:
          drain_outbox(false);
          if (!sock.valid()) {
            // begin_connect() ended without a pending socket and without failing: cannot happen, but never spin.
            fail_connect();
          } else {
            poll_connect(now);
          }
          break;
        case ConnState::Connected:
          connected_step(now);
          break;
        case ConnState::Halted:
          drain_outbox(false);
          std::this_thread::sleep_for(std::chrono::milliseconds(std::max<std::int64_t>(1, opt.poll_timeout_ms)));
          break;
        case ConnState::Stopped:
          stop_flag.store(true);
          break;
      }
      flush_pending();
      const std::int64_t after = clock();
      if (dirty || after - last_publish_us >= 100'000 || phase != st.state) publish(after);
    }
    // Shutdown: say goodbye on a live connection, then close. The socket is closed here and nowhere else.
    if (phase == ConnState::Connected) {
      goodbye(goodbye_code.load(), "client stopping");
      finish_connection(DisconnectReason::Requested, "stop requested", 0, clock());
    } else {
      sock.close();
    }
    discard_retained();
    phase = ConnState::Stopped;
    flush_pending();
    publish(clock());
    ::WSACleanup();
  }
};

// ---------------------------------------------------------------------------------------------------
// NetClient (main thread)
// ---------------------------------------------------------------------------------------------------
NetClient::NetClient() = default;
NetClient::~NetClient() { stop(); }

bool NetClient::start(Endpoint endpoint, NetOptions options) {
  if (impl_ && impl_->started.load()) return false;
  impl_ = std::make_unique<Impl>(std::move(endpoint), std::move(options));
  Impl* impl = impl_.get();
  try {
    impl->thread = std::thread([impl] { impl->run(); });
  } catch (...) {  // no exceptions across the API
    impl_.reset();
    return false;
  }
  impl->started.store(true);
  return true;
}

void NetClient::stop() {
  if (!impl_) return;
  impl_->stop_flag.store(true, std::memory_order_release);
  if (impl_->thread.joinable()) impl_->thread.join();
  impl_->started.store(false);
}

bool NetClient::running() const noexcept { return impl_ && impl_->started.load() && !impl_->stop_flag.load(); }

std::size_t NetClient::poll_inbox(std::vector<InboundEvent>& out, std::size_t max_events) {
  if (!impl_) return 0;
  std::size_t n = 0;
  while (n < max_events) {
    auto ev = impl_->inbox.try_pop();
    if (!ev) break;
    out.push_back(std::move(*ev));
    ++n;
  }
  return n;
}

SendResult NetClient::send(Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) {
  if (!running()) return SendResult::NotRunning;
  if (lane != Lane::Control && lane != Lane::Realtime && lane != Lane::Bulk) return SendResult::InvalidLane;
  if (payload.empty()) return SendResult::EmptyPayload;
  if (payload.size() > impl_->opt.max_frame_bytes) return SendResult::TooLarge;
  OutFrame f;
  if (!Impl::build_frame(f, lane, type, payload, impl_->opt.max_frame_bytes)) return SendResult::TooLarge;
  const std::size_t bytes = f.bytes.size();
  const auto r = impl_->outbox.push(std::move(f), bytes);
  if (r.over_cap) {
    impl_->outbox_overflow.store(true);
    return SendResult::OverCap;
  }
  return SendResult::Ok;
}

NetStatus NetClient::status() const noexcept {
  if (!impl_) return NetStatus{};
  return impl_->status_slot.read().value_or(NetStatus{});
}

void NetClient::enable_heartbeat() noexcept {
  if (impl_) impl_->heartbeat_enabled.store(true);
}

void NetClient::set_goodbye_code(std::uint16_t code) noexcept {
  if (impl_) impl_->goodbye_code.store(code);
}

void NetClient::set_udp_block(bool block) noexcept {
  if (impl_) impl_->udp_block_flag.store(block, std::memory_order_relaxed);
}

void NetClient::reconnect_now() noexcept {
  if (impl_) impl_->reconnect_flag.store(true);
}

}  // namespace x4mp::net
