// Unit tests for the hand-written parts of the library that the golden vectors do not reach.

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <numbers>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "x4mp/registry.h"
#include "x4mp/wire.h"

using namespace x4mp::wire;

namespace {

std::array<std::uint8_t, 8> header(std::uint32_t len, std::uint16_t type, std::uint8_t flags, std::uint8_t lane) {
  std::array<std::uint8_t, 8> h{};
  detail::wr32(h.data(), len);
  detail::wr16(h.data() + 4, type);
  h[6] = flags;
  h[7] = lane;
  return h;
}

}  // namespace

TEST_CASE("frame header validation order and categories", "[wire][frame]") {
  CHECK(parse_frame_header(header(4, 5, 0, 0)).has_value());
  CHECK(parse_frame_header(ByteSpan{}).error() == ViolationCode::TruncatedFrame);
  CHECK(parse_frame_header(header(0, 5, 0, 0)).error() == ViolationCode::ZeroLengthFrame);
  CHECK(parse_frame_header(header(kDefaultMaxFrameBytes, 5, 0, 0)).has_value());
  CHECK(parse_frame_header(header(kDefaultMaxFrameBytes + 1, 5, 0, 0)).error() == ViolationCode::FrameTooLarge);
  CHECK(parse_frame_header(header(0xFFFFFFFFu, 5, 0, 0)).error() == ViolationCode::FrameTooLarge);
  CHECK(parse_frame_header(header(4, 5, 0x02, 0)).error() == ViolationCode::ReservedFlags);
  CHECK(parse_frame_header(header(4, 5, 0x03, 0)).error() == ViolationCode::ReservedFlags);  // reserved wins
  CHECK(parse_frame_header(header(4, 5, 0x01, 0)).error() == ViolationCode::CompressedNotSupported);
  CHECK(parse_frame_header(header(4, 5, 0, 3)).error() == ViolationCode::InvalidLane);
  CHECK(parse_frame_header(header(100, 5, 0, 0), 99).error() == ViolationCode::FrameTooLarge);
}

TEST_CASE("MaxFrameBytes is enforced from the header alone", "[wire][frame]") {
  // Only the 8 header bytes are present: an oversized length must be rejected right away rather than
  // reported as "need more bytes", so a peer cannot make us wait for (or reserve) a huge payload.
  const auto h = header(kDefaultMaxFrameBytes + 1, 0x0107, 0, 2);
  auto r = try_decode_frame(h);
  REQUIRE_FALSE(r.has_value());
  CHECK(r.error() == ViolationCode::FrameTooLarge);

  // A valid header with a partial payload needs more bytes.
  auto partial = header(100, 0x0107, 0, 2);
  auto need = try_decode_frame(partial);
  REQUIRE(need.has_value());
  CHECK_FALSE(need->has_value());
  CHECK_FALSE(try_decode_frame(ByteSpan(partial.data(), 3))->has_value());
}

TEST_CASE("encode_frame errors and exact fit", "[wire][frame]") {
  std::array<std::uint8_t, 16> buf{};
  const std::array<std::uint8_t, 4> payload{1, 2, 3, 4};
  CHECK(encode_frame(buf, 5, Lane::Control, ByteSpan{}).error() == ViolationCode::ZeroLengthFrame);
  CHECK(encode_frame(buf, 5, Lane::Control, payload, 3).error() == ViolationCode::FrameTooLarge);
  CHECK(encode_frame(MutableByteSpan(buf).first(11), 5, Lane::Control, payload).error() == ViolationCode::TruncatedFrame);
  auto n = encode_frame(MutableByteSpan(buf).first(12), 5, Lane::Realtime, payload);
  REQUIRE(n.has_value());
  CHECK(*n == 12);
  auto d = try_decode_frame(ByteSpan(buf.data(), 12));
  REQUIRE(d.has_value());
  REQUIRE(d->has_value());
  CHECK((*d)->header.lane == Lane::Realtime);
  CHECK((*d)->payload.size() == 4);
}

TEST_CASE("two frames back to back are consumed one at a time", "[wire][frame]") {
  std::array<std::uint8_t, 40> buf{};
  const std::array<std::uint8_t, 4> p1{1, 2, 3, 4};
  const std::array<std::uint8_t, 8> p2{9, 8, 7, 6, 5, 4, 3, 2};
  auto n1 = encode_frame(buf, 5, Lane::Control, p1);
  REQUIRE(n1.has_value());
  auto n2 = encode_frame(MutableByteSpan(buf).subspan(*n1), 6, Lane::Control, p2);
  REQUIRE(n2.has_value());
  ByteSpan rest(buf.data(), *n1 + *n2);
  auto a = try_decode_frame(rest);
  REQUIRE((a.has_value() && a->has_value()));
  CHECK((*a)->header.type == 5);
  auto b = try_decode_frame(rest.subspan((*a)->consumed));
  REQUIRE((b.has_value() && b->has_value()));
  CHECK((*b)->header.type == 6);
  CHECK((*b)->consumed == rest.size() - (*a)->consumed);
}

TEST_CASE("datagram header and ack window", "[wire][datagram]") {
  std::array<std::uint8_t, kDatagramHeaderSize> raw{};
  const DatagramHeader h{0, 0xDEADBEEF, 7, 5, 0b1011};
  write_datagram_header(raw, h);
  CHECK(raw[0] == 0x58);  // 'X'
  CHECK(raw[1] == 0x4D);  // 'M'
  auto back = read_datagram_header(raw);
  REQUIRE(back.has_value());
  CHECK(*back == h);

  CHECK(h.acknowledges(5));
  CHECK(h.acknowledges(4));   // bit 0
  CHECK(h.acknowledges(3));   // bit 1
  CHECK_FALSE(h.acknowledges(2));
  CHECK(h.acknowledges(1));   // bit 3
  CHECK_FALSE(h.acknowledges(6));
  const DatagramHeader wrap{0, 1, 1, 2, 0b1};  // ack 2 covers 1 via bit 0; 0xFFFFFFFF is far behind
  CHECK(wrap.acknowledges(1));
  CHECK_FALSE(wrap.acknowledges(0xFFFFFFFFu));

  auto bad = raw;
  bad[3] = 1;
  CHECK(read_datagram_header(bad).error() == ViolationCode::MalformedDatagram);
  CHECK(read_datagram_header(raw, 1).error() == ViolationCode::MalformedDatagram);
  CHECK(read_datagram_header(ByteSpan(raw.data(), 23)).error() == ViolationCode::MalformedDatagram);
  std::vector<std::uint8_t> big(kMaxDatagramBytes + 1, 0);
  std::copy(raw.begin(), raw.end(), big.begin());
  CHECK(read_datagram_header(big).error() == ViolationCode::MalformedDatagram);
}

TEST_CASE("datagram writer limits", "[wire][datagram]") {
  std::vector<std::uint8_t> buf(kMaxDatagramBytes + 100, 0xEE);
  DatagramWriter w(buf, DatagramHeader{});
  CHECK(w.size() == kDatagramHeaderSize);
  const std::vector<std::uint8_t> payload(500, 0x11);
  CHECK(*w.try_add(0x0005, payload));  // 504 -> 24 + 504 = 528
  CHECK(*w.try_add(0x0005, payload));  // 1032
  CHECK(w.size() == kDatagramHeaderSize + 2 * sub_message_size(500));
  CHECK_FALSE(*w.try_add(0x0005, payload));  // would exceed 1200
  CHECK(w.try_add(0x0100, std::vector<std::uint8_t>(4, 1)).error() == ViolationCode::MalformedDatagram);  // not allowed on UDP
  CHECK(w.try_add(0x0005, ByteSpan{}).error() == ViolationCode::MalformedDatagram);
  CHECK(sub_message_size(1) == 8);
  CHECK(sub_message_size(4) == 8);
  CHECK(sub_message_size(5) == 16);
}

TEST_CASE("sub-message list: missing trailing pad is accepted, overruns are not", "[wire][datagram]") {
  std::vector<std::uint8_t> buf(kMaxDatagramBytes, 0);
  DatagramWriter w(buf, DatagramHeader{});
  REQUIRE(*w.try_add(0x0005, std::vector<std::uint8_t>(5, 7)));
  std::vector<std::uint8_t> dg(w.bytes().begin(), w.bytes().end());
  dg.resize(dg.size() - 3);  // drop the pad of the final sub-message
  int n = 0;
  CHECK(for_each_sub_message(dg, [&](const SubMessage&) { ++n; }).has_value());
  CHECK(n == 1);
  dg.resize(kDatagramHeaderSize + kSubMessageHeaderSize + 4);  // now the 5-byte payload itself is short
  CHECK(for_each_sub_message(dg, [](const SubMessage&) {}).error() == ViolationCode::MalformedDatagram);
}

TEST_CASE("replication entry validation", "[wire][replication]") {
  std::array<std::uint8_t, 300> buf{};
  ReplicationEntry e;
  e.net_id = 1;
  e.mask = kRepStatus;
  e.hull = 9;
  CHECK(*write_replication_entry(buf, e) == 7);

  e.net_id = 0;
  CHECK(write_replication_entry(buf, e).error() == ViolationCode::MalformedReplication);
  e.net_id = kReservedNetId;
  CHECK(write_replication_entry(buf, e).error() == ViolationCode::MalformedReplication);

  e.net_id = 2;
  const std::vector<std::uint8_t> ext(3, 0x5A);
  e.ext = ext;  // EXT bytes without the EXT bit
  CHECK(write_replication_entry(buf, e).error() == ViolationCode::MalformedReplication);
  e.mask = kRepExt;
  CHECK(*write_replication_entry(buf, e) == 5 + 1 + 3);
  const std::vector<std::uint8_t> huge(256, 1);
  e.ext = huge;
  CHECK(write_replication_entry(buf, e).error() == ViolationCode::MalformedReplication);
  const std::vector<std::uint8_t> max_ext(255, 1);
  e.ext = max_ext;
  CHECK(*write_replication_entry(buf, e) == 5 + 1 + 255);
  CHECK(write_replication_entry(MutableByteSpan(buf).first(10), e).error() == ViolationCode::MalformedReplication);

  // empty EXT: a keyframe is 37 bytes, 38 with an empty EXT.
  ReplicationEntry kf;
  kf.net_id = 3;
  kf.mask = kRepSector | kRepPos | kRepRot | kRepVel | kRepFlags | kRepStatus | kRepTime;
  CHECK(replication_entry_size(kf) == 37);
  kf.mask |= kRepExt;
  CHECK(replication_entry_size(kf) == 38);

  // entry_count that cannot fit is rejected before any entry is read.
  const std::array<std::uint8_t, 5> five{1, 0, 0, 0, 0};
  CHECK(decode_replication(five, 2, [](const ReplicationEntry&) {}).error() == ViolationCode::MalformedReplication);
  CHECK(decode_replication(five, 1, [](const ReplicationEntry&) {}).has_value());
}

TEST_CASE("quantisation helpers", "[wire][quantize]") {
  CHECK(*quantize_position(1.0) == 64);
  CHECK(*quantize_position(-0.0078125) == -1);  // half away from zero
  CHECK(*quantize_rotation(std::numbers::pi) == -32768);
  CHECK(*quantize_rotation(-std::numbers::pi) == -32768);
  CHECK(quantize_rotation(1e12).has_value());  // large angles wrap, no error
  CHECK(quantize_rotation(1e30).error() == ViolationCode::InvalidValue);
  CHECK(*quantize_fraction(-3.0) == 0);
  CHECK(*quantize_fraction(3.0) == 255);
  CHECK(*needs_coarse_velocity(8191.75, 0, 0) == false);
  CHECK(*needs_coarse_velocity(0, -8192, 0) == true);
  CHECK(needs_coarse_velocity(0, std::numeric_limits<double>::quiet_NaN(), 0).error() == ViolationCode::InvalidValue);
  CHECK(dequantize_velocity(4, true) == 16.0);
  CHECK(dequantize_position(64) == 1.0);
  CHECK(time_from_offset_ms(-20, 1000000) == 980000);
  CHECK(quantize_time_offset_ms(INT64_MAX, -1).error() == ViolationCode::InvalidValue);  // subtraction overflow
  CHECK(quantize_time_offset_ms(INT64_MIN, 1).error() == ViolationCode::InvalidValue);
}

namespace {

// A deliberately fake HMAC (not cryptographic): proves the interface and the constant-time compare.
class FakeHmac final : public HmacSha256 {
 public:
  void compute(ByteSpan key, ByteSpan message, HmacSha256Tag& out) const noexcept override {
    for (std::size_t i = 0; i < out.size(); ++i) {
      std::uint8_t b = static_cast<std::uint8_t>(i);
      if (!key.empty()) b = static_cast<std::uint8_t>(b ^ key[i % key.size()]);
      if (!message.empty()) b = static_cast<std::uint8_t>(b + message[i % message.size()]);
      out[i] = b;
    }
  }
};

}  // namespace

TEST_CASE("HMAC interface verify()", "[wire][hmac]") {
  const FakeHmac hmac;
  const std::array<std::uint8_t, 3> key{1, 2, 3};
  const std::array<std::uint8_t, 4> msg{9, 8, 7, 6};
  HmacSha256Tag tag{};
  hmac.compute(key, msg, tag);
  CHECK(hmac.verify(key, msg, tag));
  tag[31] ^= 1;
  CHECK_FALSE(hmac.verify(key, msg, tag));
  tag[31] ^= 1;
  CHECK_FALSE(hmac.verify(key, msg, ByteSpan(tag.data(), 31)));
  const HmacSha256& base = hmac;  // usable through the abstract interface
  CHECK(base.verify(key, msg, tag));
}

TEST_CASE("catalog lookups", "[wire][registry]") {
  CHECK(message_catalog().size() == 88);
  CHECK(find_message(0x0005)->lane == Lane::Control);
  CHECK(find_message(0x0208)->lane == Lane::Realtime);
  CHECK(find_message(0x0107)->lane == Lane::Bulk);
  CHECK(lookup_message(kDamageReportType).error() == ViolationCode::ReservedMessageType);
  CHECK(lookup_message(0x7F01).error() == ViolationCode::UnknownMessageType);
  CHECK(lookup_message(0).error() == ViolationCode::UnknownMessageType);
  CHECK(verify_payload(0x0005, ByteSpan{}).error() == ViolationCode::ZeroLengthFrame);
  const std::array<std::uint8_t, 8> garbage{0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0};
  CHECK(verify_payload(0x0005, garbage).error() == ViolationCode::MalformedPayload);
  for (std::size_t i = 1; i < message_catalog().size(); ++i)
    CHECK(message_catalog()[i - 1].type < message_catalog()[i].type);
}
