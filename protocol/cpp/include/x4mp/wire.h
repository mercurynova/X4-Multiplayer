// X4MP wire protocol, C++ side (task M0-07). Spec: docs/protocol.md (sections 3, 10.2, 11).
//
// This header is the hand-specified part of the protocol: TCP frame header, UDP datagram header and
// sub-message list, the Replication entry codec, quantisation, and an abstract HMAC interface. It
// mirrors server/src/X4MP.Protocol (C#) bit for bit; the golden vectors in protocol/testdata keep the
// two honest.
//
// Design rules (docs/mod-design.md sections 3 and 6.3):
//   * No exceptions. Every fallible call returns std::expected<T, ViolationCode>. (Only an allocation in
//     the caller's own containers can throw; nothing in this header allocates.)
//   * No heap allocation, no locks, no global state. Encoders write into caller-provided buffers and
//     decoders return views into the caller's input, so the net thread can use a preallocated ring.
//   * Depends on the C++ standard library only. FlatBuffers table decoding lives in registry.h.
//   * Little-endian only (x64 Windows); the codecs write and read bytes explicitly, so they are also
//     alignment-safe.
#pragma once

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <expected>
#include <numbers>
#include <optional>
#include <span>
#include <type_traits>

namespace x4mp::wire {

// ---------------------------------------------------------------------------------------------
// Constants (ProtocolConstants.cs)
// ---------------------------------------------------------------------------------------------

inline constexpr std::uint16_t kProtocolMajor = 0;
inline constexpr std::uint16_t kProtocolMinor = 1;
inline constexpr int kDefaultTcpPort = 47780;
inline constexpr int kDefaultUdpPort = 47781;
inline constexpr int kDefaultHttpPort = 47790;

using ByteSpan = std::span<const std::uint8_t>;
using MutableByteSpan = std::span<std::uint8_t>;

// ---------------------------------------------------------------------------------------------
// Errors (ProtocolViolation.cs / ViolationCode)
// ---------------------------------------------------------------------------------------------

/// Why inbound bytes were rejected. Names and meaning match the C# ViolationCode enum.
enum class ViolationCode : std::uint8_t {
  FrameTooLarge,           ///< payload_len exceeds MaxFrameBytes (checked before touching the payload)
  ZeroLengthFrame,         ///< payload_len is 0
  TruncatedFrame,          ///< the buffer ended inside a header
  ReservedFlags,           ///< reserved flag bits (bit1..7) are set
  CompressedNotSupported,  ///< the Compressed flag is set (Lz4Frames is off in v1)
  InvalidLane,             ///< lane byte is not 0, 1 or 2
  LaneMismatch,            ///< lane byte differs from the catalog lane of the message type
  UnknownMessageType,      ///< msg_type is not in the catalog
  ReservedMessageType,     ///< msg_type is reserved (DamageReport 0x0403)
  MalformedPayload,        ///< the FlatBuffers Verifier rejected the payload
  InvalidValue,            ///< NaN, infinity or an out-of-range value in a float field / quantiser
  MalformedDatagram,       ///< UDP datagram header or sub-message list problem
  MalformedReplication,    ///< Replication entry stream problem
};

/// The ViolationCode name as spelled in C# and in index.json ("expect"). Never null.
[[nodiscard]] constexpr const char* to_string(ViolationCode c) noexcept {
  switch (c) {
    case ViolationCode::FrameTooLarge: return "FrameTooLarge";
    case ViolationCode::ZeroLengthFrame: return "ZeroLengthFrame";
    case ViolationCode::TruncatedFrame: return "TruncatedFrame";
    case ViolationCode::ReservedFlags: return "ReservedFlags";
    case ViolationCode::CompressedNotSupported: return "CompressedNotSupported";
    case ViolationCode::InvalidLane: return "InvalidLane";
    case ViolationCode::LaneMismatch: return "LaneMismatch";
    case ViolationCode::UnknownMessageType: return "UnknownMessageType";
    case ViolationCode::ReservedMessageType: return "ReservedMessageType";
    case ViolationCode::MalformedPayload: return "MalformedPayload";
    case ViolationCode::InvalidValue: return "InvalidValue";
    case ViolationCode::MalformedDatagram: return "MalformedDatagram";
    case ViolationCode::MalformedReplication: return "MalformedReplication";
  }
  return "?";
}

template <class T>
using Result = std::expected<T, ViolationCode>;

// ---------------------------------------------------------------------------------------------
// Little-endian helpers
// ---------------------------------------------------------------------------------------------

namespace detail {

[[nodiscard]] inline std::uint16_t rd16(const std::uint8_t* p) noexcept {
  return static_cast<std::uint16_t>(p[0] | (p[1] << 8));
}
[[nodiscard]] inline std::uint32_t rd32(const std::uint8_t* p) noexcept {
  return static_cast<std::uint32_t>(p[0]) | (static_cast<std::uint32_t>(p[1]) << 8) |
         (static_cast<std::uint32_t>(p[2]) << 16) | (static_cast<std::uint32_t>(p[3]) << 24);
}
inline void wr16(std::uint8_t* p, std::uint16_t v) noexcept {
  p[0] = static_cast<std::uint8_t>(v);
  p[1] = static_cast<std::uint8_t>(v >> 8);
}
inline void wr32(std::uint8_t* p, std::uint32_t v) noexcept {
  p[0] = static_cast<std::uint8_t>(v);
  p[1] = static_cast<std::uint8_t>(v >> 8);
  p[2] = static_cast<std::uint8_t>(v >> 16);
  p[3] = static_cast<std::uint8_t>(v >> 24);
}

}  // namespace detail

// ---------------------------------------------------------------------------------------------
// TCP framing (FrameCodec.cs, protocol.md 3.2): u32 payload_len | u16 msg_type | u8 flags | u8 lane
// ---------------------------------------------------------------------------------------------

inline constexpr std::size_t kFrameHeaderSize = 8;
/// Default MaxFrameBytes (1 MiB). Chunked content always fits under it.
inline constexpr std::uint32_t kDefaultMaxFrameBytes = 1u << 20;

enum class Lane : std::uint8_t { Control = 0, Realtime = 1, Bulk = 2 };

/// Frame header flags byte. Bits 1..7 are reserved and must be zero.
inline constexpr std::uint8_t kFrameFlagCompressed = 0x01;

struct FrameHeader {
  std::uint32_t payload_length = 0;
  std::uint16_t type = 0;  ///< raw msg_type; may be unknown, see registry.h
  std::uint8_t flags = 0;
  Lane lane = Lane::Control;
};

/// One complete frame inside the caller's buffer. `payload` is exactly the FlatBuffers buffer.
struct FrameView {
  FrameHeader header;
  ByteSpan payload;
  std::size_t consumed = 0;  ///< header + payload bytes
};

/// Parses and structurally validates an 8-byte header: truncation, zero length, length over
/// `max_frame_bytes` (before any payload is touched), reserved flag bits, Compressed (unsupported
/// in v1) and the lane range. Does not look at the message catalog (see registry.h validate_frame).
/// Check order matches C# FrameCodec.ParseHeader.
[[nodiscard]] inline Result<FrameHeader> parse_frame_header(
    ByteSpan header, std::uint32_t max_frame_bytes = kDefaultMaxFrameBytes) noexcept {
  if (header.size() < kFrameHeaderSize) return std::unexpected(ViolationCode::TruncatedFrame);
  FrameHeader h;
  h.payload_length = detail::rd32(header.data());
  h.type = detail::rd16(header.data() + 4);
  h.flags = header[6];
  const std::uint8_t lane = header[7];
  if (h.payload_length == 0) return std::unexpected(ViolationCode::ZeroLengthFrame);
  if (h.payload_length > max_frame_bytes) return std::unexpected(ViolationCode::FrameTooLarge);
  if ((h.flags & static_cast<std::uint8_t>(~kFrameFlagCompressed)) != 0)
    return std::unexpected(ViolationCode::ReservedFlags);
  if ((h.flags & kFrameFlagCompressed) != 0) return std::unexpected(ViolationCode::CompressedNotSupported);
  if (lane > static_cast<std::uint8_t>(Lane::Bulk)) return std::unexpected(ViolationCode::InvalidLane);
  h.lane = static_cast<Lane>(lane);
  return h;
}

/// Writes an 8-byte header.
inline void write_frame_header(std::span<std::uint8_t, kFrameHeaderSize> dst, std::uint32_t payload_length,
                               std::uint16_t type, std::uint8_t flags, Lane lane) noexcept {
  detail::wr32(dst.data(), payload_length);
  detail::wr16(dst.data() + 4, type);
  dst[6] = flags;
  dst[7] = static_cast<std::uint8_t>(lane);
}

/// Encodes header + payload into `dst`; returns the frame size. Fails with ZeroLengthFrame (empty
/// payload), FrameTooLarge (payload over `max_frame_bytes`) or TruncatedFrame (`dst` too small).
[[nodiscard]] inline Result<std::size_t> encode_frame(MutableByteSpan dst, std::uint16_t type, Lane lane,
                                                      ByteSpan payload,
                                                      std::uint32_t max_frame_bytes = kDefaultMaxFrameBytes) noexcept {
  if (payload.empty()) return std::unexpected(ViolationCode::ZeroLengthFrame);
  if (payload.size() > max_frame_bytes) return std::unexpected(ViolationCode::FrameTooLarge);
  const std::size_t total = kFrameHeaderSize + payload.size();
  if (dst.size() < total) return std::unexpected(ViolationCode::TruncatedFrame);
  write_frame_header(dst.first<kFrameHeaderSize>(), static_cast<std::uint32_t>(payload.size()), type, 0, lane);
  for (std::size_t i = 0; i < payload.size(); ++i) dst[kFrameHeaderSize + i] = payload[i];
  return total;
}

/// Tries to read one complete frame from the start of `data`. Returns an empty optional when more
/// bytes are needed (the header is validated first, so an oversized length is rejected immediately
/// instead of waiting for the bytes). Never allocates: the payload is a view into `data`.
[[nodiscard]] inline Result<std::optional<FrameView>> try_decode_frame(
    ByteSpan data, std::uint32_t max_frame_bytes = kDefaultMaxFrameBytes) noexcept {
  if (data.size() < kFrameHeaderSize) return std::optional<FrameView>{};
  auto h = parse_frame_header(data, max_frame_bytes);
  if (!h) return std::unexpected(h.error());
  const std::size_t total = kFrameHeaderSize + h->payload_length;
  if (data.size() < total) return std::optional<FrameView>{};
  return std::optional<FrameView>{FrameView{*h, data.subspan(kFrameHeaderSize, h->payload_length), total}};
}

// ---------------------------------------------------------------------------------------------
// UDP datagram (DatagramCodec.cs, protocol.md 3.3)
//   magic u16 0x4D58 | proto_major u8 | flags u8 (=0) | conn_id u32 | seq u32 | ack u32 |
//   ack_bits u32 | reserved u32 (=0), then sub-messages: msg_type u16 | len u16 | payload | pad to 8
// ---------------------------------------------------------------------------------------------

inline constexpr std::size_t kDatagramHeaderSize = 24;
inline constexpr std::size_t kMaxDatagramBytes = 1200;
inline constexpr std::uint16_t kDatagramMagic = 0x4D58;  // bytes 'X','M'
inline constexpr std::size_t kSubMessageHeaderSize = 4;

struct DatagramHeader {
  std::uint8_t proto_major = 0;
  std::uint32_t conn_id = 0;
  std::uint32_t seq = 0;       ///< sender's datagram sequence (per direction)
  std::uint32_t ack = 0;       ///< highest seq received from the peer
  std::uint32_t ack_bits = 0;  ///< bit i = (ack - 1 - i) also received (32-packet window)

  /// True if this header acknowledges datagram `s` (wrap-safe).
  [[nodiscard]] constexpr bool acknowledges(std::uint32_t s) const noexcept {
    if (s == ack) return true;
    const std::uint32_t behind = ack - 1u - s;  // 0 => s == ack-1 => bit 0 (unsigned wrap is intended)
    return behind < 32 && (ack_bits & (1u << behind)) != 0;
  }

  friend constexpr bool operator==(const DatagramHeader&, const DatagramHeader&) = default;
};

/// Message types that may travel in a datagram (protocol.md 3.3): Ping, Pong, UdpHello, UdpHelloAck,
/// Replication, WorldUpdate, EntityStatusBatch, PlayerState. Others are dropped and counted.
[[nodiscard]] constexpr bool is_allowed_on_udp(std::uint16_t type) noexcept {
  switch (type) {
    case 0x0005:  // Ping
    case 0x0006:  // Pong
    case 0x0007:  // UdpHello
    case 0x0008:  // UdpHelloAck
    case 0x0208:  // Replication
    case 0x0202:  // WorldUpdate
    case 0x0203:  // EntityStatusBatch
    case 0x0300:  // PlayerState
      return true;
    default:
      return false;
  }
}

inline void write_datagram_header(std::span<std::uint8_t, kDatagramHeaderSize> dst, const DatagramHeader& h) noexcept {
  detail::wr16(dst.data(), kDatagramMagic);
  dst[2] = h.proto_major;
  dst[3] = 0;
  detail::wr32(dst.data() + 4, h.conn_id);
  detail::wr32(dst.data() + 8, h.seq);
  detail::wr32(dst.data() + 12, h.ack);
  detail::wr32(dst.data() + 16, h.ack_bits);
  detail::wr32(dst.data() + 20, 0);
}

/// Parses the header. MalformedDatagram on a short or oversized datagram, wrong magic, a proto_major
/// other than `expected_major`, or non-zero reserved bits (flags byte or the reserved u32).
[[nodiscard]] inline Result<DatagramHeader> read_datagram_header(
    ByteSpan datagram, std::uint8_t expected_major = static_cast<std::uint8_t>(kProtocolMajor)) noexcept {
  if (datagram.size() < kDatagramHeaderSize || datagram.size() > kMaxDatagramBytes)
    return std::unexpected(ViolationCode::MalformedDatagram);
  const std::uint8_t* p = datagram.data();
  if (detail::rd16(p) != kDatagramMagic) return std::unexpected(ViolationCode::MalformedDatagram);
  if (p[2] != expected_major) return std::unexpected(ViolationCode::MalformedDatagram);
  if (p[3] != 0 || detail::rd32(p + 20) != 0) return std::unexpected(ViolationCode::MalformedDatagram);
  return DatagramHeader{p[2], detail::rd32(p + 4), detail::rd32(p + 8), detail::rd32(p + 12), detail::rd32(p + 16)};
}

/// Bytes a sub-message with `payload_length` occupies, pad included.
[[nodiscard]] constexpr std::size_t sub_message_size(std::size_t payload_length) noexcept {
  return (kSubMessageHeaderSize + payload_length + 7) & ~static_cast<std::size_t>(7);
}

struct SubMessage {
  std::uint16_t type = 0;
  std::size_t offset = 0;  ///< payload offset from the start of the datagram
  ByteSpan payload;
};

/// Calls `visit(const SubMessage&)` for each sub-message of a datagram (the header must already be
/// valid; this does not re-check it). MalformedDatagram if a sub-message header or payload overruns the
/// datagram, or a length is zero. The pad of the final sub-message may be missing; pad bytes are
/// ignored on read. Allocation-free.
template <class Visitor>
[[nodiscard]] Result<void> for_each_sub_message(ByteSpan datagram, Visitor&& visit) {
  std::size_t p = kDatagramHeaderSize;
  while (p < datagram.size()) {
    if (datagram.size() - p < kSubMessageHeaderSize) return std::unexpected(ViolationCode::MalformedDatagram);
    const std::uint16_t type = detail::rd16(datagram.data() + p);
    const std::size_t len = detail::rd16(datagram.data() + p + 2);
    if (len == 0) return std::unexpected(ViolationCode::MalformedDatagram);
    if (datagram.size() - p - kSubMessageHeaderSize < len) return std::unexpected(ViolationCode::MalformedDatagram);
    visit(SubMessage{type, p + kSubMessageHeaderSize, datagram.subspan(p + kSubMessageHeaderSize, len)});
    p += sub_message_size(len);
  }
  return {};
}

/// Builds a datagram in a caller-provided buffer (header plus zero-padded sub-messages, never over
/// 1200 bytes). The buffer need not be zeroed: pad bytes are written explicitly.
class DatagramWriter {
 public:
  /// `buffer` must hold at least 24 bytes; usage is capped at min(buffer.size(), 1200).
  DatagramWriter(MutableByteSpan buffer, const DatagramHeader& header) noexcept
      : buf_(buffer.first(buffer.size() < kMaxDatagramBytes ? buffer.size() : kMaxDatagramBytes)) {
    if (buf_.size() >= kDatagramHeaderSize) {
      write_datagram_header(buf_.first<kDatagramHeaderSize>(), header);
      length_ = kDatagramHeaderSize;
    }
  }

  /// Current datagram size (24 for an ack-only datagram, 0 if the buffer was too small).
  [[nodiscard]] std::size_t size() const noexcept { return length_; }
  [[nodiscard]] ByteSpan bytes() const noexcept { return ByteSpan(buf_.data(), length_); }

  /// Appends a sub-message if it fits (pad included). Returns false when it does not fit; an error for
  /// a message type not allowed on UDP or a payload of 0 or more than 65535 bytes.
  [[nodiscard]] Result<bool> try_add(std::uint16_t type, ByteSpan payload) noexcept {
    if (length_ == 0 || !is_allowed_on_udp(type) || payload.empty() || payload.size() > 0xFFFF)
      return std::unexpected(ViolationCode::MalformedDatagram);
    const std::size_t size = sub_message_size(payload.size());
    if (length_ + size > buf_.size()) return false;
    std::uint8_t* p = buf_.data() + length_;
    detail::wr16(p, type);
    detail::wr16(p + 2, static_cast<std::uint16_t>(payload.size()));
    for (std::size_t i = 0; i < payload.size(); ++i) p[kSubMessageHeaderSize + i] = payload[i];
    for (std::size_t i = kSubMessageHeaderSize + payload.size(); i < size; ++i) p[i] = 0;
    length_ += size;
    return true;
  }

 private:
  MutableByteSpan buf_;
  std::size_t length_ = 0;
};

// ---------------------------------------------------------------------------------------------
// Quantisation (Quantize.cs, protocol.md 11)
//
// Rounding is round-half-away-from-zero in IEEE double (std::round). NaN and infinity, and values
// that do not fit the target integer, are InvalidValue. Build with /fp:precise (the default); do not
// use /fp:fast, which would change these results.
// ---------------------------------------------------------------------------------------------

inline constexpr double kPositionStepsPerMetre = 64.0;                                 ///< 1/64 m
inline constexpr double kRotationCountsPerRadian = 32768.0 / std::numbers::pi;         ///< 65536 = 2*pi
inline constexpr double kVelocityStepsPerMps = 4.0;                                    ///< 0.25 m/s
inline constexpr double kCoarseMpsPerStep = 4.0;                                       ///< 4 m/s
inline constexpr double kMaxFineVelocityMps = 32767.0 / kVelocityStepsPerMps;
inline constexpr std::int32_t kMaxTimeOffsetMs = 32767;

/// Metres (sector-relative) to i32 1/64 m.
[[nodiscard]] inline Result<std::int32_t> quantize_position(double metres) noexcept {
  if (!std::isfinite(metres)) return std::unexpected(ViolationCode::InvalidValue);
  const double q = std::round(metres * kPositionStepsPerMetre);
  if (q < -2147483648.0 || q > 2147483647.0) return std::unexpected(ViolationCode::InvalidValue);
  return static_cast<std::int32_t>(q);
}

[[nodiscard]] constexpr double dequantize_position(std::int32_t q) noexcept { return q / kPositionStepsPerMetre; }

/// Radians to i16 (rad * 32768/pi), wrapping modulo 2*pi: pi and -pi both give -32768, 2*pi gives 0.
[[nodiscard]] inline Result<std::int16_t> quantize_rotation(double radians) noexcept {
  if (!std::isfinite(radians)) return std::unexpected(ViolationCode::InvalidValue);
  const double q = std::round(radians * kRotationCountsPerRadian);
  if (std::fabs(q) >= 4.0e18) return std::unexpected(ViolationCode::InvalidValue);
  const auto wide = static_cast<std::int64_t>(q);
  return static_cast<std::int16_t>(static_cast<std::uint16_t>(static_cast<std::uint64_t>(wide)));
}

/// i16 back to radians in [-pi, pi).
[[nodiscard]] constexpr double dequantize_rotation(std::int16_t q) noexcept { return q / kRotationCountsPerRadian; }

/// m/s to i16 (fine: m/s * 4, coarse: m/s / 4). Out of range is InvalidValue.
[[nodiscard]] inline Result<std::int16_t> quantize_velocity(double mps, bool coarse) noexcept {
  if (!std::isfinite(mps)) return std::unexpected(ViolationCode::InvalidValue);
  const double q = std::round(coarse ? mps / kCoarseMpsPerStep : mps * kVelocityStepsPerMps);
  if (q < -32768.0 || q > 32767.0) return std::unexpected(ViolationCode::InvalidValue);
  return static_cast<std::int16_t>(q);
}

[[nodiscard]] constexpr double dequantize_velocity(std::int16_t q, bool coarse) noexcept {
  return coarse ? q * kCoarseMpsPerStep : q / kVelocityStepsPerMps;
}

/// True when any component exceeds the fine range, so the entity must set StateFlags.VelCoarse.
/// Non-finite input is InvalidValue.
[[nodiscard]] inline Result<bool> needs_coarse_velocity(double vx, double vy, double vz) noexcept {
  if (!std::isfinite(vx) || !std::isfinite(vy) || !std::isfinite(vz)) return std::unexpected(ViolationCode::InvalidValue);
  return std::fabs(vx) > kMaxFineVelocityMps || std::fabs(vy) > kMaxFineVelocityMps ||
         std::fabs(vz) > kMaxFineVelocityMps;
}

/// Fraction 0..1 (clamped; NaN/infinity are InvalidValue) to u8 0..255.
[[nodiscard]] inline Result<std::uint8_t> quantize_fraction(double fraction) noexcept {
  if (!std::isfinite(fraction)) return std::unexpected(ViolationCode::InvalidValue);
  const double c = fraction < 0.0 ? 0.0 : (fraction > 1.0 ? 1.0 : fraction);
  return static_cast<std::uint8_t>(std::round(c * 255.0));
}

[[nodiscard]] constexpr double dequantize_fraction(std::uint8_t q) noexcept { return q / 255.0; }

/// Replication TIME: (sample_us - reference_us) in whole milliseconds, half away from zero, as i16
/// (about +-32 s). InvalidValue if the subtraction overflows or the result does not fit.
[[nodiscard]] inline Result<std::int16_t> quantize_time_offset_ms(std::int64_t sample_us,
                                                                  std::int64_t reference_us) noexcept {
  constexpr std::int64_t kMax = INT64_MAX;
  constexpr std::int64_t kMin = INT64_MIN;
  if ((reference_us < 0 && sample_us > kMax + reference_us) || (reference_us > 0 && sample_us < kMin + reference_us))
    return std::unexpected(ViolationCode::InvalidValue);
  const std::int64_t d = sample_us - reference_us;
  // d == INT64_MIN cannot be negated; it is far out of range anyway.
  if (d == kMin) return std::unexpected(ViolationCode::InvalidValue);
  const std::int64_t ms = d >= 0 ? (d + 500) / 1000 : -((-d + 500) / 1000);
  if (d > kMax - 500 || ms < -32768 || ms > 32767) return std::unexpected(ViolationCode::InvalidValue);
  return static_cast<std::int16_t>(ms);
}

[[nodiscard]] constexpr std::int64_t time_from_offset_ms(std::int16_t offset_ms, std::int64_t reference_us) noexcept {
  return reference_us + static_cast<std::int64_t>(offset_ms) * 1000;
}

// ---------------------------------------------------------------------------------------------
// Replication entry codec (ReplicationCodec.cs, protocol.md 10.2):
//   net_id:u32 mask:u8 [fields in bit order], little-endian, unaligned, back to back in
//   Replication.entries.
// ---------------------------------------------------------------------------------------------

inline constexpr std::uint8_t kRepSector = 1u << 0;  ///< u16 sector index
inline constexpr std::uint8_t kRepPos = 1u << 1;     ///< 3 x i32, 1/64 m, sector-relative
inline constexpr std::uint8_t kRepRot = 1u << 2;     ///< 3 x i16 (yaw, pitch, roll)
inline constexpr std::uint8_t kRepVel = 1u << 3;     ///< 3 x i16, 0.25 m/s (4 m/s when VelCoarse)
inline constexpr std::uint8_t kRepFlags = 1u << 4;   ///< u16 StateFlags
inline constexpr std::uint8_t kRepStatus = 1u << 5;  ///< u8 hull, u8 shield
inline constexpr std::uint8_t kRepTime = 1u << 6;    ///< i16 sample-time offset (ms) from server_time_us
inline constexpr std::uint8_t kRepExt = 1u << 7;     ///< u8 len + len bytes; reserved, receivers skip

inline constexpr std::size_t kReplicationMinEntrySize = 5;  ///< net_id + mask
inline constexpr std::uint32_t kReservedNetId = 0xFFFFFFFFu;

/// One Replication entry. Only the fields named by `mask` are meaningful; the rest are zero.
struct ReplicationEntry {
  std::uint32_t net_id = 0;
  std::uint8_t mask = 0;
  std::uint16_t sector = 0;
  std::int32_t pos_x = 0, pos_y = 0, pos_z = 0;
  std::int16_t yaw = 0, pitch = 0, roll = 0;
  std::int16_t vel_x = 0, vel_y = 0, vel_z = 0;
  std::uint16_t state_flags = 0;
  std::uint8_t hull = 0, shield = 0;
  std::int16_t time_ms = 0;
  /// EXT payload (at most 255 bytes); meaningful iff kRepExt is set. On decode it is a view into the
  /// input buffer, so it is only valid as long as that buffer is.
  ByteSpan ext;
};

/// Encoded size of an entry (EXT bytes included).
[[nodiscard]] constexpr std::size_t replication_entry_size(const ReplicationEntry& e) noexcept {
  std::size_t n = kReplicationMinEntrySize;
  if (e.mask & kRepSector) n += 2;
  if (e.mask & kRepPos) n += 12;
  if (e.mask & kRepRot) n += 6;
  if (e.mask & kRepVel) n += 6;
  if (e.mask & kRepFlags) n += 2;
  if (e.mask & kRepStatus) n += 2;
  if (e.mask & kRepTime) n += 2;
  if (e.mask & kRepExt) n += 1 + e.ext.size();
  return n;
}

namespace detail {
inline void wr16s(std::uint8_t* p, std::int16_t v) noexcept { wr16(p, static_cast<std::uint16_t>(v)); }
inline void wr32s(std::uint8_t* p, std::int32_t v) noexcept { wr32(p, static_cast<std::uint32_t>(v)); }
[[nodiscard]] inline std::int16_t rd16s(const std::uint8_t* p) noexcept { return static_cast<std::int16_t>(rd16(p)); }
[[nodiscard]] inline std::int32_t rd32s(const std::uint8_t* p) noexcept { return static_cast<std::int32_t>(rd32(p)); }
}  // namespace detail

/// Writes one entry; returns bytes written. MalformedReplication for net_id 0 or 0xFFFFFFFF, EXT over
/// 255 bytes, EXT bytes without the EXT mask bit, or a destination that is too small.
[[nodiscard]] inline Result<std::size_t> write_replication_entry(MutableByteSpan dst, const ReplicationEntry& e) noexcept {
  using namespace detail;
  if (e.net_id == 0 || e.net_id == kReservedNetId) return std::unexpected(ViolationCode::MalformedReplication);
  if (e.mask & kRepExt) {
    if (e.ext.size() > 255) return std::unexpected(ViolationCode::MalformedReplication);
  } else if (!e.ext.empty()) {
    return std::unexpected(ViolationCode::MalformedReplication);
  }
  const std::size_t size = replication_entry_size(e);
  if (dst.size() < size) return std::unexpected(ViolationCode::MalformedReplication);

  std::uint8_t* p = dst.data();
  wr32(p, e.net_id);
  p[4] = e.mask;
  p += kReplicationMinEntrySize;
  if (e.mask & kRepSector) { wr16(p, e.sector); p += 2; }
  if (e.mask & kRepPos) { wr32s(p, e.pos_x); wr32s(p + 4, e.pos_y); wr32s(p + 8, e.pos_z); p += 12; }
  if (e.mask & kRepRot) { wr16s(p, e.yaw); wr16s(p + 2, e.pitch); wr16s(p + 4, e.roll); p += 6; }
  if (e.mask & kRepVel) { wr16s(p, e.vel_x); wr16s(p + 2, e.vel_y); wr16s(p + 4, e.vel_z); p += 6; }
  if (e.mask & kRepFlags) { wr16(p, e.state_flags); p += 2; }
  if (e.mask & kRepStatus) { p[0] = e.hull; p[1] = e.shield; p += 2; }
  if (e.mask & kRepTime) { wr16s(p, e.time_ms); p += 2; }
  if (e.mask & kRepExt) {
    *p++ = static_cast<std::uint8_t>(e.ext.size());
    for (std::size_t i = 0; i < e.ext.size(); ++i) p[i] = e.ext[i];
    p += e.ext.size();
  }
  return static_cast<std::size_t>(p - dst.data());
}

struct ReadEntry {
  ReplicationEntry entry;
  std::size_t consumed = 0;
};

/// Reads one entry from the start of `src`. MalformedReplication if the bytes end early, net_id is 0 or
/// reserved, or EXT overruns the buffer. EXT is returned as a view (receivers skip it semantically).
[[nodiscard]] inline Result<ReadEntry> read_replication_entry(ByteSpan src) noexcept {
  using namespace detail;
  if (src.size() < kReplicationMinEntrySize) return std::unexpected(ViolationCode::MalformedReplication);
  ReadEntry r;
  ReplicationEntry& e = r.entry;
  e.net_id = rd32(src.data());
  e.mask = src[4];
  if (e.net_id == 0 || e.net_id == kReservedNetId) return std::unexpected(ViolationCode::MalformedReplication);
  const std::size_t fixed_size = replication_entry_size(e);  // EXT counts as its length byte (ext empty)
  if (src.size() < fixed_size) return std::unexpected(ViolationCode::MalformedReplication);

  const std::uint8_t* p = src.data() + kReplicationMinEntrySize;
  if (e.mask & kRepSector) { e.sector = rd16(p); p += 2; }
  if (e.mask & kRepPos) { e.pos_x = rd32s(p); e.pos_y = rd32s(p + 4); e.pos_z = rd32s(p + 8); p += 12; }
  if (e.mask & kRepRot) { e.yaw = rd16s(p); e.pitch = rd16s(p + 2); e.roll = rd16s(p + 4); p += 6; }
  if (e.mask & kRepVel) { e.vel_x = rd16s(p); e.vel_y = rd16s(p + 2); e.vel_z = rd16s(p + 4); p += 6; }
  if (e.mask & kRepFlags) { e.state_flags = rd16(p); p += 2; }
  if (e.mask & kRepStatus) { e.hull = p[0]; e.shield = p[1]; p += 2; }
  if (e.mask & kRepTime) { e.time_ms = rd16s(p); p += 2; }
  if (e.mask & kRepExt) {
    const std::size_t len = *p++;
    if (static_cast<std::size_t>(src.data() + src.size() - p) < len) return std::unexpected(ViolationCode::MalformedReplication);
    e.ext = ByteSpan(p, len);
    p += len;
  }
  r.consumed = static_cast<std::size_t>(p - src.data());
  return r;
}

/// Decodes `Replication.entries`: exactly `entry_count` entries that consume the whole buffer, calling
/// `visit(const ReplicationEntry&)` for each. Anything else (count cannot fit, short, trailing bytes, bad
/// entry) is MalformedReplication. Allocation-free.
template <class Visitor>
[[nodiscard]] Result<void> decode_replication(ByteSpan entries, std::size_t entry_count, Visitor&& visit) {
  if (entry_count > entries.size() / kReplicationMinEntrySize) return std::unexpected(ViolationCode::MalformedReplication);
  std::size_t p = 0;
  for (std::size_t i = 0; i < entry_count; ++i) {
    auto r = read_replication_entry(entries.subspan(p));
    if (!r) return std::unexpected(r.error());
    visit(r->entry);
    p += r->consumed;
  }
  if (p != entries.size()) return std::unexpected(ViolationCode::MalformedReplication);
  return {};
}

// ---------------------------------------------------------------------------------------------
// HMAC interface (protocol.md 5, ClientHello auth_proof / admin_proof / team passwords)
//
// Interface only. The implementation (Windows CNG BCrypt, in the mod's platform layer) and any test
// double live elsewhere, so this library stays free of crypto and platform dependencies.
// ---------------------------------------------------------------------------------------------

inline constexpr std::size_t kHmacSha256Size = 32;
using HmacSha256Tag = std::array<std::uint8_t, kHmacSha256Size>;

class HmacSha256 {
 public:
  virtual ~HmacSha256() = default;

  /// Computes HMAC-SHA256(key, message) into `out`. Must not throw.
  virtual void compute(ByteSpan key, ByteSpan message, HmacSha256Tag& out) const noexcept = 0;

  /// Constant-time check of `tag` against HMAC-SHA256(key, message).
  [[nodiscard]] bool verify(ByteSpan key, ByteSpan message, ByteSpan tag) const noexcept {
    HmacSha256Tag expected{};
    compute(key, message, expected);
    if (tag.size() != kHmacSha256Size) return false;
    std::uint8_t diff = 0;
    for (std::size_t i = 0; i < kHmacSha256Size; ++i) diff = static_cast<std::uint8_t>(diff | (expected[i] ^ tag[i]));
    return diff == 0;
  }
};

}  // namespace x4mp::wire
