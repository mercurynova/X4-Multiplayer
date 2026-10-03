#pragma once
// core/net: the UDP realtime lane state machine (M3-03, docs/m3-plan.md 6.2 and Q4, protocol.md 3.3).
//
// Pure logic, header-only, no sockets and no clocks: the net thread feeds it `now_us`, received datagrams, and a `Transmit`
// callback that writes one datagram to the socket. That makes the whole thing testable with a fake clock and a loopback pair
// (tests/test_udp_lane.cpp). The datagram codec itself is x4mp/wire.h (golden-tested against the C# DatagramCodec).
//
//   Off ---start()---> Binding --UdpHelloAck--> Active --no ack for ack_timeout--> Fallback --reprobe_ms--> Binding ...
//                        |                                                           ^
//                        +------------- no UdpHelloAck for bind_timeout --------------+
//
//   * Binding: UdpHello{conn_id, udp_token} every 250 ms (the Welcome numbers). Realtime frames stay on TCP.
//   * Active : Realtime frames are batched into datagrams (<= 1200 bytes, one message never spans two); every datagram
//              carries our ack/ack_bits for the server's datagrams (that drives the server's replication baselines); a bare
//              header goes out after 50 ms of silence with unacked inbound; a UdpHello goes out after 1 s of silence as a
//              keepalive (NAT mapping, path probe). "Alive" means the server's ack field keeps advancing: if something
//              we sent that expects an answer is unacknowledged and the ack field has not advanced for ack_timeout (3 s),
//              the lane drops to TCP, so a path that dies is noticed within 3 s whatever the traffic pattern. No
//              disconnect: the TCP connection never noticed.
//   * Fallback: Realtime frames use TCP; datagrams from the server are still accepted and acked; every 30 s one more
//              binding attempt (3 s of hellos), then back to Fallback if it fails.
//   * Mode Force (tests): never uses TCP for Realtime; a Realtime frame offered while not Active is dropped and counted.
//
// Failure injection (UdpOptions::loss_pct / block): applied to whole datagrams in both directions, before the datagram is
// sent / before it is processed, so the sequence numbers and the ack logic see real loss.
//
// Allocation: start() builds the hello payload once. Everything else uses fixed buffers; no per-frame allocation.
// Not thread-safe: the net thread owns it.

#include <array>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <span>
#include <vector>

#include "control_generated.h"
#include "core/net/udp_types.h"
#include "message_ids_generated.h"
#include "x4mp/wire.h"

namespace x4mp::net {

// Receive side of the datagram sequence numbers (port of the server's DatagramReceiveWindow): remembers which of the last 64
// sequence numbers arrived, refuses duplicates and anything older than highest - 64, and produces ack / ack_bits.
class UdpReceiveWindow {
 public:
  static constexpr int kWindow = 64;

  [[nodiscard]] bool has_received() const noexcept { return any_; }
  [[nodiscard]] std::uint32_t ack() const noexcept { return any_ ? highest_ : 0; }
  // bit i = ack - 1 - i also arrived
  [[nodiscard]] std::uint32_t ack_bits() const noexcept { return any_ ? static_cast<std::uint32_t>(bits_ >> 1) : 0; }
  [[nodiscard]] std::uint64_t accepted() const noexcept { return accepted_; }
  [[nodiscard]] std::uint64_t missing() const noexcept {
    if (!any_) return 0;
    const std::uint64_t span = static_cast<std::uint64_t>(static_cast<std::uint32_t>(highest_ - first_)) + 1;
    return span > accepted_ ? span - accepted_ : 0;
  }
  [[nodiscard]] float loss_percent() const noexcept {
    const auto m = missing();
    return (any_ && accepted_ + m > 0) ? 100.0f * static_cast<float>(m) / static_cast<float>(accepted_ + m) : 0.0f;
  }
  void reset() noexcept { *this = UdpReceiveWindow{}; }

  enum class Result : std::uint8_t { Accepted, Duplicate, TooOld };

  [[nodiscard]] Result accept(std::uint32_t seq) noexcept {
    if (!any_) {
      any_ = true;
      first_ = highest_ = seq;
      bits_ = 1;
      ++accepted_;
      return Result::Accepted;
    }
    const std::int32_t diff = static_cast<std::int32_t>(seq - highest_);
    if (diff > 0) {
      bits_ = diff >= kWindow ? 0 : bits_ << diff;
      bits_ |= 1;
      highest_ = seq;
      ++accepted_;
      return Result::Accepted;
    }
    const std::int32_t back = -diff;
    if (back >= kWindow) return Result::TooOld;
    const std::uint64_t bit = std::uint64_t{1} << back;
    if ((bits_ & bit) != 0) return Result::Duplicate;
    bits_ |= bit;
    ++accepted_;
    return Result::Accepted;
  }

 private:
  bool any_ = false;
  std::uint32_t first_ = 0;
  std::uint32_t highest_ = 0;
  std::uint64_t bits_ = 0;  // bit i = (highest - i) arrived
  std::uint64_t accepted_ = 0;
};

class UdpLane {
 public:
  // Writes one datagram to the socket. Returns false if the socket refused it (counted as nothing; the datagram is lost, which
  // UDP allows).
  using Transmit = std::function<bool(wire::ByteSpan)>;

  UdpLane(UdpOptions options, Transmit transmit) : opt_(options), tx_(std::move(transmit)) {
    stats_.state = UdpState::Off;
  }

  UdpLane(const UdpLane&) = delete;  // batch_ holds a span into buf_
  UdpLane& operator=(const UdpLane&) = delete;

  [[nodiscard]] UdpState state() const noexcept { return state_; }
  [[nodiscard]] bool active() const noexcept { return state_ == UdpState::Active; }
  [[nodiscard]] const UdpOptions& options() const noexcept { return opt_; }
  [[nodiscard]] UdpStats stats() const noexcept {
    UdpStats s = stats_;
    s.state = state_;
    s.rx_loss_pct = rx_.loss_percent();
    return s;
  }

  void set_block(bool block) noexcept { opt_.block = block; }
  void set_loss_pct(double pct) noexcept { opt_.loss_pct = pct; }

  // A new connection's Welcome: bind with conn_id / token. No-op (state stays Off) when the mode is Off.
  void start(std::uint32_t conn_id, std::uint64_t token, std::int64_t now_us) {
    stop();
    if (opt_.mode == UdpMode::Off) return;
    conn_id_ = conn_id;
    flatbuffers::FlatBufferBuilder fbb(32);
    fbb.Finish(X4MP::Proto::CreateUdpHello(fbb, conn_id, token));
    hello_.assign(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
    rng_ = opt_.seed != 0 ? opt_.seed : (static_cast<std::uint64_t>(now_us) * 0x9E3779B97F4A7C15ull) | 1u;
    begin_binding(now_us);
  }

  // Connection over: back to Off, everything reset (a new connection has a new conn_id and numbering).
  void stop() {
    state_ = UdpState::Off;
    stats_ = UdpStats{};
    rx_.reset();
    tx_seq_ = 0;
    acked_ = 0;
    last_elicit_seq_ = 0;
    last_progress_us_ = 0;
    ack_pending_ = false;
    batch_count_ = 0;
    batch_.reset();
    hello_.clear();
  }

  // Offers one Realtime frame (message type + FlatBuffers payload). True: the lane owns it (batched into the next datagram, or
  // dropped in Force mode). False: send it over TCP (lane not active, type not allowed on UDP, or too large for a datagram).
  [[nodiscard]] bool send_realtime(std::uint16_t type, wire::ByteSpan payload, std::int64_t now_us) {
    if (state_ == UdpState::Off || opt_.mode == UdpMode::Off) return false;
    if (!wire::is_allowed_on_udp(type) || payload.empty() ||
        wire::kDatagramHeaderSize + wire::sub_message_size(payload.size()) > wire::kMaxDatagramBytes) {
      return false;
    }
    if (state_ != UdpState::Active) {
      if (opt_.mode != UdpMode::Force) return false;
      ++stats_.force_dropped;
      return true;
    }
    if (!batch_) open_batch();
    auto added = batch_->try_add(type, payload);
    if (added && !*added) {  // does not fit next to what is already queued: send that, start a new datagram
      flush(now_us);
      open_batch();
      added = batch_->try_add(type, payload);
    }
    if (!added || !*added) return false;
    ++batch_count_;
    return true;
  }

  // Sends the batched datagram, if any. Call after a burst of send_realtime (end of the outbox drain).
  void flush(std::int64_t now_us) {
    if (!batch_ || batch_count_ == 0) {
      batch_.reset();
      return;
    }
    const std::uint32_t seq = ++tx_seq_;
    wire::write_datagram_header(std::span<std::uint8_t, wire::kDatagramHeaderSize>(buf_.data(), wire::kDatagramHeaderSize),
                                make_header(seq));
    stats_.frames_out += batch_count_;
    const wire::ByteSpan bytes = batch_->bytes();
    batch_count_ = 0;
    note_elicit(seq);
    emit(bytes, now_us);
    batch_.reset();
  }

  // Timers: hello repeats, bind timeout, ack-only datagram, keepalive, fallback, re-probe. Call every loop iteration.
  void tick(std::int64_t now_us) {
    if (state_ == UdpState::Off) return;
    const auto ms = [](std::int64_t v) { return v * 1000; };
    switch (state_) {
      case UdpState::Binding:
        if (now_us - bind_started_us_ >= ms(opt_.bind_timeout_ms)) {
          if (opt_.mode == UdpMode::Force) {
            bind_started_us_ = now_us;  // never gives up
          } else {
            enter_fallback(now_us);
            break;
          }
        }
        if (now_us >= next_hello_us_) {
          send_hello(now_us);
          next_hello_us_ = now_us + ms(opt_.hello_interval_ms);
        }
        break;
      case UdpState::Active:
        if (outstanding() && now_us - last_progress_us_ >= ms(opt_.ack_timeout_ms)) {
          if (opt_.mode == UdpMode::Force) {
            begin_binding(now_us);
          } else {
            enter_fallback(now_us);
          }
          break;
        }
        if (now_us - last_tx_us_ >= ms(opt_.keepalive_ms)) send_hello(now_us);
        break;
      case UdpState::Fallback:
        if (now_us - fallback_since_us_ >= ms(opt_.reprobe_ms)) {
          ++stats_.reprobes;
          begin_binding(now_us);
        }
        break;
      case UdpState::Off: break;
    }
    if (state_ != UdpState::Off && ack_pending_ && now_us - last_tx_us_ >= ms(opt_.ack_only_ms)) send_bare_ack(now_us);
  }

  // One datagram from the socket. `visit(type, payload)` is called for every delivered sub-message (never UdpHelloAck); the
  // payload is only valid during the call and has NOT been run through the FlatBuffers Verifier yet.
  template <class Visitor>
  void on_datagram(wire::ByteSpan datagram, std::int64_t now_us, Visitor&& visit) {
    if (state_ == UdpState::Off) return;
    const auto header = wire::read_datagram_header(datagram);
    if (!header || header->conn_id != conn_id_) {
      ++stats_.malformed;
      return;
    }
    if (drop_sim()) {
      ++stats_.sim_drops_rx;
      return;
    }
    if (!wire::for_each_sub_message(datagram, [](const wire::SubMessage&) {})) {
      ++stats_.malformed;
      return;
    }
    if (rx_.accept(header->seq) != UdpReceiveWindow::Result::Accepted) {
      ++stats_.duplicates;
      return;
    }
    ++stats_.datagrams_in;
    stats_.bytes_in += datagram.size();
    process_ack(*header, now_us);

    bool carries = false;
    bool hello_ack = false;
    (void)wire::for_each_sub_message(datagram, [&](const wire::SubMessage& m) {
      carries = true;
      if (m.type == kHelloAck) {
        hello_ack = true;
      } else if (m.type == kHello || !wire::is_allowed_on_udp(m.type)) {
        ++stats_.malformed;  // a client never receives UdpHello; other types are refused on UDP
      } else {
        ++stats_.frames_in;
        visit(m.type, m.payload);
      }
    });
    if (carries) ack_pending_ = true;
    if (hello_ack && (state_ == UdpState::Binding || state_ == UdpState::Fallback)) activate(now_us);
  }

 private:
  static constexpr std::uint16_t kHello = static_cast<std::uint16_t>(X4MP::Proto::MsgType::UdpHello);
  static constexpr std::uint16_t kHelloAck = static_cast<std::uint16_t>(X4MP::Proto::MsgType::UdpHelloAck);

  void begin_binding(std::int64_t now_us) {
    state_ = UdpState::Binding;
    bind_started_us_ = now_us;
    next_hello_us_ = now_us;
    last_tx_us_ = now_us;
  }

  void enter_fallback(std::int64_t now_us) {
    batch_.reset();
    batch_count_ = 0;
    state_ = UdpState::Fallback;
    fallback_since_us_ = now_us;
    ++stats_.fallbacks;
  }

  void activate(std::int64_t now_us) {
    state_ = UdpState::Active;
    ++stats_.binds;
    acked_ = last_elicit_seq_;  // hellos from the binding phase are settled; only what we send from now on is judged
    last_progress_us_ = now_us;
    last_tx_us_ = now_us;
  }

  [[nodiscard]] wire::DatagramHeader make_header(std::uint32_t seq) const noexcept {
    return wire::DatagramHeader{static_cast<std::uint8_t>(wire::kProtocolMajor), conn_id_, seq, rx_.ack(), rx_.ack_bits()};
  }

  void open_batch() {
    batch_.emplace(wire::MutableByteSpan(buf_.data(), buf_.size()), make_header(0));
    batch_count_ = 0;
  }

  // A datagram that the server answers (data, hello): remembered so a silent server is noticed.
  void note_elicit(std::uint32_t seq) noexcept { last_elicit_seq_ = seq; }

  // Something we sent that expects an answer is still unacknowledged.
  [[nodiscard]] bool outstanding() const noexcept { return static_cast<std::int32_t>(last_elicit_seq_ - acked_) > 0; }

  void process_ack(const wire::DatagramHeader& h, std::int64_t now_us) noexcept {
    if (static_cast<std::int32_t>(h.ack - tx_seq_) > 0) return;  // acknowledges something we never sent
    if (static_cast<std::int32_t>(h.ack - acked_) <= 0) return;
    acked_ = h.ack;
    stats_.acked_seq = acked_;
    last_progress_us_ = now_us;
  }

  void send_hello(std::int64_t now_us) {
    std::array<std::uint8_t, 64> out{};
    wire::DatagramWriter w(wire::MutableByteSpan(out.data(), out.size()), make_header(++tx_seq_));
    if (auto r = w.try_add(kHello, wire::ByteSpan(hello_.data(), hello_.size())); !r || !*r) return;
    note_elicit(tx_seq_);
    emit(w.bytes(), now_us);
  }

  void send_bare_ack(std::int64_t now_us) {
    std::array<std::uint8_t, wire::kDatagramHeaderSize> out{};
    wire::write_datagram_header(std::span<std::uint8_t, wire::kDatagramHeaderSize>(out), make_header(++tx_seq_));
    emit(wire::ByteSpan(out.data(), out.size()), now_us);
  }

  void emit(wire::ByteSpan datagram, std::int64_t now_us) {
    last_tx_us_ = now_us;
    ack_pending_ = false;  // every datagram carries the current ack state
    if (drop_sim()) {
      ++stats_.sim_drops_tx;
      return;
    }
    if (tx_ && tx_(datagram)) {
      ++stats_.datagrams_out;
      stats_.bytes_out += datagram.size();
    }
  }

  [[nodiscard]] bool drop_sim() noexcept {
    if (opt_.block) return true;
    if (opt_.loss_pct <= 0.0) return false;
    rng_ ^= rng_ >> 12;
    rng_ ^= rng_ << 25;
    rng_ ^= rng_ >> 27;
    const std::uint64_t r = rng_ * 0x2545F4914F6CDD1Dull;
    const double unit = static_cast<double>(r >> 11) / 9007199254740992.0;  // [0,1)
    return unit * 100.0 < opt_.loss_pct;
  }

  UdpOptions opt_;
  Transmit tx_;
  UdpState state_ = UdpState::Off;
  UdpStats stats_;
  UdpReceiveWindow rx_;
  std::uint32_t conn_id_ = 0;
  std::vector<std::uint8_t> hello_;
  std::uint64_t rng_ = 1;

  std::uint32_t tx_seq_ = 0;
  std::uint32_t acked_ = 0;             // highest of our seqs the server acknowledged
  std::uint32_t last_elicit_seq_ = 0;   // our newest datagram that the server answers with an ack
  std::int64_t last_progress_us_ = 0;   // when the server's ack field last advanced (or the lane became Active)
  std::int64_t bind_started_us_ = 0;
  std::int64_t next_hello_us_ = 0;
  std::int64_t fallback_since_us_ = 0;
  std::int64_t last_tx_us_ = 0;
  bool ack_pending_ = false;

  std::array<std::uint8_t, wire::kMaxDatagramBytes> buf_{};
  std::optional<wire::DatagramWriter> batch_;
  std::uint32_t batch_count_ = 0;
};

}  // namespace x4mp::net
