#pragma once
// core/net: UDP realtime lane types (M3-03, docs/m3-plan.md 6.2, protocol.md 3.3). Plain data only: no sockets, no FlatBuffers,
// so net.h can include it and NetStatus stays trivially copyable.

#include <cstdint>
#include <optional>
#include <string_view>

namespace x4mp::net {

// Auto   : use UDP when the server offers it (Welcome.udp_port != 0); fall back to TCP after 3 s without a server ack and
//          re-probe every 30 s. The default.
// Force  : test mode. Realtime frames travel ONLY over UDP: no TCP fallback (frames are dropped while the lane is not
//          bound; the lane keeps re-binding). Proves in a live test that the datagrams really flow.
// Off    : never bind; the capability UdpRealtime is not advertised; Realtime frames use TCP.
enum class UdpMode : std::uint8_t { Auto = 0, Force, Off };

[[nodiscard]] constexpr const char* to_string(UdpMode m) noexcept {
  switch (m) {
    case UdpMode::Auto: return "auto";
    case UdpMode::Force: return "force";
    case UdpMode::Off: return "off";
  }
  return "?";
}

[[nodiscard]] constexpr std::optional<UdpMode> parse_udp_mode(std::string_view s) noexcept {
  if (s == "auto") return UdpMode::Auto;
  if (s == "force") return UdpMode::Force;
  if (s == "off") return UdpMode::Off;
  return std::nullopt;
}

struct UdpOptions {
  UdpMode mode = UdpMode::Auto;
  // Failure injection (headless --udp-loss / --udp-block): share of datagrams dropped in EACH direction before they are
  // processed or sent, and a switch that drops all of them (a firewall). `block` can also be flipped at runtime through
  // NetClient::set_udp_block.
  double loss_pct = 0.0;
  bool block = false;
  std::uint64_t seed = 0;  // 0 = derived from the clock
  // Timers (protocol.md 3.3). Tests shrink them.
  std::int64_t hello_interval_ms = 250;   // UdpHello repeat while binding
  std::int64_t bind_timeout_ms = 3000;    // no UdpHelloAck for this long: the Realtime lane runs over TCP
  std::int64_t ack_timeout_ms = 3000;     // bound, but the server acknowledges none of our datagrams for this long: TCP
  std::int64_t reprobe_ms = 30000;        // after a fallback, try to bind again this often
  std::int64_t keepalive_ms = 1000;       // nothing sent for this long while bound: a UdpHello (keeps the NAT mapping, probes the path)
  std::int64_t ack_only_ms = 50;          // unacked inbound for this long and nothing to send: bare-header ack datagram
};

enum class UdpState : std::uint8_t {
  Off = 0,    // no lane: disabled, or the server offered none (Welcome.udp_port == 0), or no connection
  Binding,    // UdpHello being repeated; Realtime frames still go over TCP (Auto)
  Active,     // bound and acknowledged; Realtime frames go over UDP
  Fallback,   // no ack in time; Realtime frames go over TCP; re-probe pending
};

[[nodiscard]] constexpr const char* to_string(UdpState s) noexcept {
  switch (s) {
    case UdpState::Off: return "off";
    case UdpState::Binding: return "binding";
    case UdpState::Active: return "active";
    case UdpState::Fallback: return "fallback";
  }
  return "?";
}

struct UdpStats {
  UdpState state = UdpState::Off;
  std::uint64_t datagrams_out = 0;   // handed to the socket (simulated drops excluded)
  std::uint64_t datagrams_in = 0;    // accepted
  std::uint64_t bytes_out = 0;
  std::uint64_t bytes_in = 0;
  std::uint64_t frames_out = 0;      // sub-messages other than hello sent (Realtime frames carried by UDP)
  std::uint64_t frames_in = 0;       // sub-messages other than hello-ack delivered
  std::uint64_t sim_drops_tx = 0;    // failure injection
  std::uint64_t sim_drops_rx = 0;
  std::uint64_t malformed = 0;       // not a valid datagram / wrong conn_id / a sub-message the Verifier refused
  std::uint64_t duplicates = 0;      // refused by the sequence window
  std::uint64_t force_dropped = 0;   // UdpMode::Force: frames dropped because the lane was not bound
  std::uint32_t binds = 0;           // times the lane became Active
  std::uint32_t fallbacks = 0;       // times it dropped to TCP
  std::uint32_t reprobes = 0;        // re-probe attempts after a fallback
  std::uint32_t acked_seq = 0;       // highest of our datagram sequence numbers the server acknowledged
  float rx_loss_pct = 0.0f;          // loss the received sequence numbers show (what NodeStats.udp_rx_loss_pct reports)
};

}  // namespace x4mp::net
