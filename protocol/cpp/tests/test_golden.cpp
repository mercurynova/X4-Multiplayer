// Cross-language golden-vector tests (task M0-07, ADR-041).
//
// Reads protocol/testdata/index.json, written by the C# generator, and checks that this C++ library
//   * accepts every frame vector: header, catalog, lane, Verifier, and every decoded field equal to the
//     value C# recorded (index.json "fields"; unions walked in full);
//   * rejects every reject vector with the expected error category;
//   * decodes and re-encodes Replication entries and UDP datagrams byte-identically;
//   * reproduces every quantisation case bit for bit.
// Parsing uses nlohmann-json (vcpkg).

#include <bit>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <map>
#include <set>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include "field_dump.h"
#include "flatbuffers/flatbuffers.h"
#include "world_generated.h"
#include "x4mp/registry.h"
#include "x4mp/wire.h"

namespace {

using nlohmann::json;
using namespace x4mp::wire;

std::vector<std::uint8_t> read_file(const std::string& path) {
  std::ifstream in(path, std::ios::binary);
  REQUIRE(in.good());
  return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
}

const json& index() {
  static const json j = [] {
    std::ifstream in(std::string(X4MP_TESTDATA_DIR) + "/index.json");
    REQUIRE(in.good());
    return json::parse(in);
  }();
  return j;
}

std::vector<std::uint8_t> vector_bytes(const json& v) {
  auto bytes = read_file(std::string(X4MP_TESTDATA_DIR) + "/" + v.at("file").get<std::string>());
  CHECK(bytes.size() == v.at("size").get<std::size_t>());
  return bytes;
}

std::vector<const json*> vectors_of(const char* kind) {
  std::vector<const json*> r;
  for (const auto& v : index().at("vectors"))
    if (v.at("kind") == kind) r.push_back(&v);
  return r;
}

const x4mp::test::Schema& schema() {
  static const x4mp::test::Schema s = x4mp::test::Schema::load(X4MP_BFBS_PATH);
  return s;
}

ByteSpan span_of(const std::vector<std::uint8_t>& v) { return ByteSpan(v.data(), v.size()); }

// Decodes entries into a vector (ext spans point into `bytes`) and re-encodes them. Returns the
// re-encoded bytes via `reencoded`.
Result<void> roundtrip_replication(ByteSpan bytes, std::size_t count, std::vector<ReplicationEntry>& entries,
                                   std::vector<std::uint8_t>& reencoded) {
  entries.clear();
  auto r = decode_replication(bytes, count, [&](const ReplicationEntry& e) { entries.push_back(e); });
  if (!r) return r;
  reencoded.assign(bytes.size(), 0xEE);  // dirty buffer: every byte must be written
  std::size_t p = 0;
  for (const auto& e : entries) {
    auto w = write_replication_entry(MutableByteSpan(reencoded).subspan(p), e);
    if (!w) return std::unexpected(w.error());
    p += *w;
  }
  if (p != bytes.size()) return std::unexpected(ViolationCode::MalformedReplication);
  return {};
}

}  // namespace

TEST_CASE("index.json agrees with the C++ constants", "[golden]") {
  const auto& j = index();
  CHECK(j.at("schemaVersion") == 1);
  CHECK(j.at("protocol").at("major") == kProtocolMajor);
  CHECK(j.at("protocol").at("minor") == kProtocolMinor);
  CHECK(j.at("maxFrameBytes") == kDefaultMaxFrameBytes);
  CHECK(j.at("frameHeaderSize") == kFrameHeaderSize);
  CHECK(j.at("datagramHeaderSize") == kDatagramHeaderSize);
  CHECK(j.at("maxDatagramBytes") == kMaxDatagramBytes);
}

TEST_CASE("frame vectors: verify, decode every field, re-encode the header", "[golden][frame]") {
  const auto frames = vectors_of("frame");
  REQUIRE(frames.size() == 126);
  std::set<std::string> union_variants_walked;
  std::set<std::uint16_t> types_seen;

  for (const json* vp : frames) {
    const json& v = *vp;
    const std::string file = v.at("file");
    INFO("vector " << file);
    const auto bytes = vector_bytes(v);
    const std::string name = v.at("msgName");
    const auto type = v.at("msgType").get<std::uint16_t>();

    // Frame header: parse, compare against index.json, re-encode byte-exactly.
    auto decoded = try_decode_frame(span_of(bytes));
    REQUIRE(decoded.has_value());
    REQUIRE(decoded->has_value());
    const FrameView& f = **decoded;
    CHECK(f.consumed == bytes.size());
    CHECK(f.header.type == type);
    CHECK(static_cast<int>(f.header.lane) == v.at("lane").get<int>());
    CHECK(f.header.payload_length == v.at("payloadLength").get<std::uint32_t>());
    CHECK(v.at("payloadOffset") == kFrameHeaderSize);
    CHECK(f.header.flags == 0);

    std::array<std::uint8_t, kFrameHeaderSize> hdr{};
    write_frame_header(hdr, f.header.payload_length, f.header.type, f.header.flags, f.header.lane);
    CHECK(std::memcmp(hdr.data(), bytes.data(), kFrameHeaderSize) == 0);
    std::vector<std::uint8_t> again(bytes.size(), 0xEE);
    auto enc = encode_frame(again, f.header.type, f.header.lane, f.payload);
    REQUIRE(enc.has_value());
    CHECK(again == bytes);

    // Catalog + lane + FlatBuffers Verifier (always, ADR-041).
    const MessageDescriptor* d = find_message(type);
    REQUIRE(d != nullptr);
    CHECK(std::string(d->name) == name);
    CHECK(static_cast<int>(d->lane) == v.at("lane").get<int>());
    auto valid = validate_frame(f);
    REQUIRE(valid.has_value());
    types_seen.insert(type);

    // Full walk of every field (unions included), compared with what C# decoded.
    const json actual = x4mp::test::dump_message(schema().get(), name, f.payload);
    const json& expected = v.at("fields");
    if (actual != expected) {
      INFO("diff (C# -> C++): " << json::diff(expected, actual).dump(2));
      FAIL_CHECK("decoded fields differ from index.json for " << file);
    }
    if (const std::string variant = v.at("variant"); !variant.empty()) {
      const auto& body = actual.at("Body");
      CHECK(body.contains("value"));
      union_variants_walked.insert(name + "." + variant);
    }

    // A Replication message's packed entries must also decode and re-encode byte-identically.
    if (name == "Replication") {
      const auto* rep = flatbuffers::GetRoot<X4MP::Proto::Replication>(f.payload.data());
      std::vector<ReplicationEntry> entries;
      std::vector<std::uint8_t> reenc;
      const ByteSpan packed(rep->entries()->data(), rep->entries()->size());
      auto r = roundtrip_replication(packed, rep->entry_count(), entries, reenc);
      REQUIRE(r.has_value());
      CHECK(std::vector<std::uint8_t>(packed.begin(), packed.end()) == reenc);
    }
  }

  // Every catalogued message type has a vector, and the four union messages were walked per variant.
  CHECK(types_seen.size() == message_catalog().size());
  CHECK(union_variants_walked.size() >= 9 + 11 + 19);
  INFO("WorldCatchUp has no variant name: checked through its mutation union vector below");
  for (const json* vp : frames) {
    if (vp->at("msgName") != "WorldCatchUp") continue;
    std::set<int> mutation_types;
    for (const auto& e : vp->at("fields").at("Entries")) mutation_types.insert(e.at("Body").at("type").get<int>());
    CHECK(mutation_types == std::set<int>{1, 2, 3, 4});
  }
}

TEST_CASE("reject vectors fail with the expected error category", "[golden][reject]") {
  int checked = 0;
  for (const json* vp : vectors_of("reject-frame")) {
    const json& v = *vp;
    INFO("vector " << v.at("file"));
    const auto bytes = vector_bytes(v);
    ViolationCode got = ViolationCode::MalformedPayload;
    bool rejected = false;
    auto d = try_decode_frame(span_of(bytes));
    if (!d) {
      got = d.error();
      rejected = true;
    } else if (!*d) {
      got = ViolationCode::TruncatedFrame;  // stream ended mid-frame
      rejected = true;
    } else if (auto ok = validate_frame(**d); !ok) {
      got = ok.error();
      rejected = true;
    }
    CHECK(rejected);
    CHECK(std::string(to_string(got)) == v.at("expect").get<std::string>());
    ++checked;
  }
  for (const json* vp : vectors_of("reject-replication-entries")) {
    const json& v = *vp;
    INFO("vector " << v.at("file"));
    const auto bytes = vector_bytes(v);
    auto r = decode_replication(span_of(bytes), v.at("entryCount").get<std::size_t>(), [](const ReplicationEntry&) {});
    REQUIRE_FALSE(r.has_value());
    CHECK(std::string(to_string(r.error())) == v.at("expect").get<std::string>());
    ++checked;
  }
  for (const json* vp : vectors_of("reject-datagram")) {
    const json& v = *vp;
    INFO("vector " << v.at("file"));
    const auto bytes = vector_bytes(v);
    ViolationCode got{};
    bool rejected = false;
    auto h = read_datagram_header(span_of(bytes));
    if (!h) {
      got = h.error();
      rejected = true;
    } else if (auto s = for_each_sub_message(span_of(bytes), [](const SubMessage&) {}); !s) {
      got = s.error();
      rejected = true;
    }
    CHECK(rejected);
    CHECK(std::string(to_string(got)) == v.at("expect").get<std::string>());
    ++checked;
  }
  CHECK(checked == 10 + 4 + 5);
}

TEST_CASE("replication vectors decode and re-encode byte-identically", "[golden][replication]") {
  const auto vecs = vectors_of("replication-entries");
  REQUIRE(vecs.size() == 8);
  for (const json* vp : vecs) {
    const json& v = *vp;
    INFO("vector " << v.at("file"));
    const auto bytes = vector_bytes(v);
    std::vector<ReplicationEntry> entries;
    std::vector<std::uint8_t> reenc;
    const auto count = v.at("entryCount").get<std::size_t>();
    auto r = roundtrip_replication(span_of(bytes), count, entries, reenc);
    REQUIRE(r.has_value());
    CHECK(reenc == bytes);

    const auto& meta = v.at("entries");
    REQUIRE(entries.size() == meta.size());
    std::size_t offset = 0;
    for (std::size_t i = 0; i < entries.size(); ++i) {
      CHECK(entries[i].net_id == meta[i].at("netId").get<std::uint32_t>());
      CHECK(entries[i].mask == meta[i].at("mask").get<std::uint8_t>());
      CHECK(meta[i].at("offset").get<std::size_t>() == offset);
      CHECK(replication_entry_size(entries[i]) == meta[i].at("length").get<std::size_t>());
      offset += replication_entry_size(entries[i]);
    }
  }

  // repl_all_masks: all 256 masks once each, in order.
  const auto all = read_file(std::string(X4MP_TESTDATA_DIR) + "/repl_all_masks.bin");
  std::vector<ReplicationEntry> entries;
  std::vector<std::uint8_t> reenc;
  REQUIRE(roundtrip_replication(span_of(all), 256, entries, reenc).has_value());
  for (std::size_t m = 0; m < 256; ++m) CHECK(entries[m].mask == m);

  // Spot-check decoded values against the generator's deterministic MaskEntry() (netId 1000 + mask).
  const auto& kf = entries[kRepSector | kRepPos | kRepRot | kRepVel | kRepFlags | kRepStatus | kRepTime];
  const std::uint32_t id = 1000 + 0x7F;
  CHECK(kf.net_id == id);
  CHECK(kf.sector == (0x1234 ^ id));
  CHECK(kf.pos_x == 64 + static_cast<std::int32_t>(id));
  CHECK(kf.pos_y == -64 - static_cast<std::int32_t>(id));
  CHECK(kf.pos_z == 1'000'000 + static_cast<std::int32_t>(id));
  CHECK(kf.yaw == static_cast<std::int16_t>(16384 + id));
  CHECK(kf.roll == INT16_MIN);
  CHECK(kf.vel_z == INT16_MAX);
  CHECK(kf.hull == 255);
  CHECK(kf.time_ms == static_cast<std::int16_t>(-20 - static_cast<int>(id)));
  CHECK(replication_entry_size(kf) == 37);  // protocol.md: a keyframe is 37 bytes
  const auto& ext = entries[kRepExt];
  REQUIRE(ext.ext.size() == 4);
  CHECK(ext.ext[0] == 0xAA);
  CHECK(replication_entry_size(entries[kRepExt]) == 5 + 1 + 4);
}

TEST_CASE("datagram vectors decode and re-encode byte-identically", "[golden][datagram]") {
  const auto vecs = vectors_of("datagram");
  REQUIRE(vecs.size() == 2);
  for (const json* vp : vecs) {
    const json& v = *vp;
    INFO("vector " << v.at("file"));
    const auto bytes = vector_bytes(v);
    auto h = read_datagram_header(span_of(bytes));
    REQUIRE(h.has_value());
    CHECK(h->proto_major == v.at("protoMajor").get<int>());
    CHECK(h->conn_id == v.at("connId").get<std::uint32_t>());
    CHECK(h->seq == v.at("seq").get<std::uint32_t>());
    CHECK(h->ack == v.at("ack").get<std::uint32_t>());
    CHECK(h->ack_bits == v.at("ackBits").get<std::uint32_t>());

    // Decode sub-messages, compare with index.json, verify each payload, collect for re-encoding.
    const auto& meta = v.at("subMessages");
    std::vector<SubMessage> subs;
    auto r = for_each_sub_message(span_of(bytes), [&](const SubMessage& s) { subs.push_back(s); });
    REQUIRE(r.has_value());
    REQUIRE(subs.size() == meta.size());
    for (std::size_t i = 0; i < subs.size(); ++i) {
      CHECK(subs[i].type == meta[i].at("msgType").get<std::uint16_t>());
      CHECK(subs[i].offset == meta[i].at("offset").get<std::size_t>());
      CHECK(subs[i].payload.size() == meta[i].at("length").get<std::size_t>());
      CHECK(std::string(find_message(subs[i].type)->name) == meta[i].at("msgName").get<std::string>());
      CHECK(is_allowed_on_udp(subs[i].type));
      CHECK(verify_payload(subs[i].type, subs[i].payload).has_value());
    }

    std::vector<std::uint8_t> out(kMaxDatagramBytes, 0xEE);  // dirty buffer: pad must be written as zero
    DatagramWriter w(out, *h);
    for (const auto& s : subs) {
      auto added = w.try_add(s.type, s.payload);
      REQUIRE(added.has_value());
      REQUIRE(*added);
    }
    CHECK(std::vector<std::uint8_t>(w.bytes().begin(), w.bytes().end()) == bytes);

    // Header-only re-encode.
    std::array<std::uint8_t, kDatagramHeaderSize> hb{};
    write_datagram_header(hb, *h);
    CHECK(std::memcmp(hb.data(), bytes.data(), kDatagramHeaderSize) == 0);
  }
}

TEST_CASE("corrupting any payload byte never crashes the Verifier and is usually detected", "[golden][mutation]") {
  // Not a correctness oracle (flipping a padding byte is legitimately harmless), but a robustness check:
  // every single-byte corruption of every frame vector goes through the Verifier and must return cleanly.
  std::size_t total = 0, detected = 0;
  for (const json* vp : vectors_of("frame")) {
    const auto bytes = vector_bytes(*vp);
    const auto type = vp->at("msgType").get<std::uint16_t>();
    std::vector<std::uint8_t> payload(bytes.begin() + kFrameHeaderSize, bytes.end());
    for (std::size_t i = 0; i < payload.size(); ++i) {
      payload[i] ^= 0xFF;
      if (!verify_payload(type, payload)) ++detected;
      payload[i] ^= 0xFF;
      ++total;
    }
  }
  CHECK(total > 10000);
  CHECK(detected > total / 20);
}

TEST_CASE("quantisation cases match bit for bit", "[golden][quantize]") {
  const auto& cases = index().at("quantize").at("cases");
  REQUIRE(cases.size() > 50);
  std::map<std::string, int> per_op;

  for (const json& c : cases) {
    const std::string op = c.at("op");
    ++per_op[op];
    const bool reject = c.contains("expect");
    if (reject) CHECK(c.at("expect") == "reject");

    auto check = [&](auto result) {
      if (reject) {
        INFO("op " << op << " case " << c.dump());
        REQUIRE_FALSE(result.has_value());
        CHECK(result.error() == ViolationCode::InvalidValue);
      } else {
        INFO("op " << op << " case " << c.dump());
        REQUIRE(result.has_value());
        CHECK(static_cast<std::int64_t>(*result) == c.at("out").get<std::int64_t>());
      }
    };

    if (op == "time") {
      check(quantize_time_offset_ms(c.at("sampleUs").get<std::int64_t>(), c.at("referenceUs").get<std::int64_t>()));
      continue;
    }
    const double in = std::bit_cast<double>(std::stoull(c.at("inBits").get<std::string>(), nullptr, 16));
    if (op == "position") check(quantize_position(in));
    else if (op == "rotation") check(quantize_rotation(in));
    else if (op == "velocity") check(quantize_velocity(in, c.at("coarse").get<bool>()));
    else if (op == "fraction") check(quantize_fraction(in));
    else FAIL("unknown quantisation op " << op);
  }
  CHECK(per_op.size() == 5);
}
