#pragma once
// core/net: the network thread (M1-N2, docs/mod-design.md 2.2/2.3, docs/protocol.md 3, 7, 18).
//
// NetClient owns ONE background thread that does all socket work: non-blocking Winsock connect (WSAEWOULDBLOCK is
// "in progress", PIT-022), WSAPoll, TCP_NODELAY, read buffering with partial frames, validate_frame (the FlatBuffers
// Verifier) before anything is handed over, Ping/Pong, clock sync, an 8 MB write-buffer cap, reconnect backoff.
//
// The main thread talks to it ONLY through core/queue types and never blocks:
//   inbox   SpscRing<InboundEvent>          net -> main   (poll_inbox)
//   outbox  ReliableOutbox<OutFrame>        main -> net   (send)
//   status  LatestWinsSlot<NetStatus>       net -> main   (status). The net thread is its only writer.
// All NetClient member functions are for the one main/session thread (not thread-safe against each other).
//
// Hooks for the layers above:
//   * Session (M1-N3): ServerHello arrives as a Frame event; reply with send(Lane::Control, ClientHello, ...) from
//     the main thread, or install NetOptions::frame_hook to answer on the net thread (HMAC etc.) with
//     HookContext::send. The hook sees every inbound frame except Ping/Pong, before it is queued to the inbox.
//   * UDP realtime (later): the seam is Transport below; today only Tcp exists and Realtime frames travel over TCP.
//
// No exceptions cross this API. core/ includes no X4 SDK headers.

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string>
#include <vector>

#include "core/net/backoff.h"
#include "core/net/clock_sync.h"
#include "x4mp/wire.h"

namespace x4mp::net {

[[nodiscard]] std::string protocol_version_string();  // "0.1"
[[nodiscard]] int default_tcp_port() noexcept;        // 47780

using wire::Lane;

struct Endpoint {
  std::string host = "127.0.0.1";
  std::uint16_t port = 47780;
};

// Which carrier a lane uses. Seam for the later UDP realtime path: Realtime maps to Udp once bound.
enum class Transport : std::uint8_t { Tcp, Udp };
[[nodiscard]] constexpr Transport transport_for(Lane lane, bool udp_active) noexcept {
  return (lane == Lane::Realtime && udp_active) ? Transport::Udp : Transport::Tcp;
}

// DisconnectCode values (common.fbs) the net layer sends or cares about.
inline constexpr std::uint16_t kDisconnectClientQuit = 1;
inline constexpr std::uint16_t kDisconnectMalformedMessage = 31;
inline constexpr std::uint16_t kDisconnectHeartbeatTimeout = 30;
// True for server Disconnect codes after which reconnecting is pointless (Kicked, Banned, Superseded, the
// *Mismatch handshake rejections). The net layer then parks in ConnState::Halted until reconnect_now()/stop().
[[nodiscard]] constexpr bool is_no_retry_code(std::uint16_t code) noexcept {
  return code == 3 || code == 4 || code == 5 || (code >= 10 && code <= 13);
}

// Why a connection ended / a connect attempt failed (local view).
enum class DisconnectReason : std::uint8_t {
  None = 0,
  ConnectFailed,      // refused, unreachable, resolve failure
  ConnectTimeout,
  RemoteClosed,       // orderly EOF
  ServerDisconnect,   // a Disconnect frame arrived first (code in InboundEvent::remote_code)
  SocketError,
  HeartbeatTimeout,
  MalformedFrame,     // invalid header / unknown type / lane mismatch / Verifier rejected the payload
  FrameTooLarge,      // header length over the cap
  WriteBufferOverflow,  // the 8 MB write cap (SlowConsumer, local)
  Requested,          // stop()
};
[[nodiscard]] const char* to_string(DisconnectReason reason) noexcept;

enum class ConnState : std::uint8_t { Stopped = 0, Connecting, Connected, Backoff, Halted };
[[nodiscard]] const char* to_string(ConnState state) noexcept;

// What the net thread publishes. Trivially copyable (LatestWinsSlot).
struct NetStatus {
  ConnState state = ConnState::Stopped;
  DisconnectReason last_reason = DisconnectReason::None;
  std::uint16_t last_remote_code = 0;       // code of the last server Disconnect frame
  std::uint32_t last_retry_after_ms = 0;    // its retry_after_ms
  std::uint32_t connect_attempt = 0;        // attempts since start (including the current one)
  std::uint32_t connect_wouldblock = 0;     // connect() calls that returned WSAEWOULDBLOCK (the normal Windows path)
  std::uint32_t connections = 0;            // successful connects since start
  std::int64_t next_retry_us = 0;           // Backoff: clock value at which the next attempt starts
  std::int64_t rtt_us = 0;                  // smoothed (EWMA 1/8); 0 until the first Pong
  std::int64_t rtt_last_us = 0;
  std::int64_t rtt_min_us = 0;              // lowest of the 16-sample window
  std::int64_t clock_offset_us = 0;         // applied (slewed) server - local offset; valid if clock_valid
  std::uint8_t clock_valid = 0;
  std::uint64_t frames_in = 0;
  std::uint64_t frames_out = 0;
  std::uint64_t bytes_in = 0;
  std::uint64_t bytes_out = 0;
  std::uint64_t realtime_dropped_in = 0;    // inbound Realtime frames dropped because the inbox was full
  std::uint64_t outbox_dropped = 0;         // frames discarded (not connected, or evicted by the outbox cap)
  std::uint64_t write_buffer_bytes = 0;     // currently queued to the socket
};

// One thing the net thread hands to the main thread.
struct InboundEvent {
  enum class Kind : std::uint8_t {
    Connected,     // TCP established; the server's ServerHello follows as a Frame
    Disconnected,  // connection (or connect attempt) ended: reason/text/remote_code
    Frame,         // a validated frame: type, lane, payload
  };
  Kind kind = Kind::Frame;
  Lane lane = Lane::Control;
  DisconnectReason reason = DisconnectReason::None;
  std::uint16_t type = 0;
  std::uint16_t remote_code = 0;
  std::vector<std::uint8_t> payload;  // FlatBuffers buffer of a Frame (already verified)
  std::string text;                   // Disconnected: human-readable detail
};

// Passed to NetOptions::frame_hook. Runs on the net thread: answer instantly, never block, no game calls.
class HookContext {
 public:
  virtual ~HookContext() = default;
  // Queue a frame for sending (net-thread side; same framing as NetClient::send). False if it cannot be queued.
  virtual bool send(Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) = 0;
};

struct NetOptions {
  std::uint32_t max_frame_bytes = wire::kDefaultMaxFrameBytes;
  std::size_t write_cap_bytes = 8u * 1024u * 1024u;  // outbound buffer cap; over it => WriteBufferOverflow
  std::int64_t heartbeat_interval_ms = 1000;
  std::int64_t heartbeat_timeout_ms = 10000;         // no inbound bytes for this long => HeartbeatTimeout
  std::int64_t connect_timeout_ms = 5000;
  std::int64_t backoff_first_ms = 1000;              // 1, 2, 4, 8, then cap
  std::int64_t backoff_cap_ms = 10000;
  std::int64_t backoff_reset_after_ms = 5000;        // a connection that lasted this long resets the schedule
  int send_buffer_bytes = 0;                         // SO_SNDBUF (0 = system default); a test knob
  std::int64_t poll_timeout_ms = 5;
  // Monotonic microseconds. nullptr = steady_clock. Injectable so tests control backoff and heartbeat timing.
  std::function<std::int64_t()> clock;
  // Optional net-thread hook (see header comment). Must not throw.
  std::function<void(std::uint16_t type, Lane lane, std::span<const std::uint8_t> payload, HookContext& ctx)>
      frame_hook;
};

enum class SendResult : std::uint8_t {
  Ok,
  NotRunning,
  EmptyPayload,
  TooLarge,   // payload over max_frame_bytes
  OverCap,    // accepted, but the outbox byte cap was exceeded by non-droppable data: the net thread will drop
              // the connection (SlowConsumer) rather than lose reliable data silently
  InvalidLane,
};

class NetClient {
 public:
  NetClient();
  ~NetClient();  // stop()
  NetClient(const NetClient&) = delete;
  NetClient& operator=(const NetClient&) = delete;

  // Starts the net thread connecting to `endpoint`; returns immediately. False if already running or the thread
  // cannot be created. Connection progress is reported through status() and inbox events.
  [[nodiscard]] bool start(Endpoint endpoint, NetOptions options = {});

  // Stops the thread (best-effort Disconnect(ClientQuit) on a live connection) and joins. Returns within
  // roughly one poll timeout; never waits on the network. Idempotent. Pending inbox events stay readable.
  void stop();

  [[nodiscard]] bool running() const noexcept;

  // Appends up to `max_events` inbox events to `out`; returns how many. Non-blocking.
  std::size_t poll_inbox(std::vector<InboundEvent>& out, std::size_t max_events = 256);

  // Frames `payload` as (type, lane) and queues it. Non-blocking. The payload must be a complete FlatBuffers
  // buffer; the net layer does not interpret it. While not connected, queued frames are discarded (counted in
  // NetStatus::outbox_dropped): a reconnect always starts a new handshake.
  SendResult send(Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload);

  // Latest published status (never torn). Before the net thread publishes anything: all defaults (Stopped).
  [[nodiscard]] NetStatus status() const noexcept;

  // Arms the client's own Ping heartbeat (every heartbeat_interval_ms) and the 10 s no-data liveness timeout for the
  // CURRENT connection. Call it once the handshake is under way (right after sending ClientHello): the server
  // treats any frame other than ClientHello as UnexpectedMessage before the handshake completes, so pings must not
  // start earlier. Reset on every new connection. Answering the server's Pings is always on.
  void enable_heartbeat() noexcept;

  // Skips the remaining backoff wait / leaves Halted and resets the schedule to 1 s: attempt now.
  void reconnect_now() noexcept;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace x4mp::net
