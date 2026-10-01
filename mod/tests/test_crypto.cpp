// core/crypto: SHA-256 and the handshake HMAC over BCrypt. The proof vectors were produced by the C# reference
// (server/src/X4MP.Protocol/HandshakeAuth.cs, HandshakeAuth.ComputeProof / HashPassword) from
//   nonce = 00 01 02 .. 1F, player_key = A0 A1 A2 .. BF
// so a pass here means the C++ client and the C# server agree byte for byte.

#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/crypto/crypto.h"

using namespace x4mp::crypto;

namespace {

std::vector<std::uint8_t> seq_bytes(int first) {
  std::vector<std::uint8_t> v(32);
  for (int i = 0; i < 32; ++i) v[static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(first + i);
  return v;
}

std::string hex(const Sha256Digest& d) { return to_hex(d); }
template <class A>
std::string hex_of(const A& a) {
  return to_hex(std::span<const std::uint8_t>(a.data(), a.size()));
}

struct Vec {
  const char* password;
  const char* hash;
  const char* proof;
};
// Generated with the C# HandshakeAuth (see the file header).
const Vec kVectors[] = {
    {"hunter2", "f52fbd32b2b3b86ff88ef6c490628285f482af15ddcb29541f94bcf526a3f6c7",
     "c3cb6b4cfbb31d47c803d4a410a467c348458e75ad7c44a4be2be59070be8914"},
    // "p<a umlaut>ssw<o umlaut>rd <check mark>" as explicit UTF-8 bytes
    {"p\xC3\xA4ssw\xC3\xB6rd \xE2\x9C\x93", "f754375ccc77531a8b1ff6b2e42646a035fe6a5e88c66a2a0842768628bc3af8",
     "1b8eac97b7f9eaebebf04d07ca5a5029dde3bac96a0583191ba50e34aab9fe25"},
    {"", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
     "908cb9223982d17f540a3c51aa7fc3d640b76047ab337efbe16f6c5f3393d651"},
};

}  // namespace

TEST_CASE("crypto: SHA-256 known answers", "[crypto]") {
  const auto empty = sha256({});
  REQUIRE(empty);
  CHECK(hex(*empty) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
  const std::string abc = "abc";
  const auto d = sha256(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(abc.data()), abc.size()));
  REQUIRE(d);
  CHECK(hex(*d) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
}

TEST_CASE("crypto: incremental hashing equals one-shot, in any slicing", "[crypto]") {
  std::vector<std::uint8_t> data(100000);
  for (std::size_t i = 0; i < data.size(); ++i) data[i] = static_cast<std::uint8_t>(i * 31 + 7);
  const auto whole = sha256(data);
  REQUIRE(whole);
  Sha256Hasher h;
  REQUIRE(h.ok());
  std::size_t off = 0;
  for (const std::size_t n : {1u, 63u, 64u, 65u, 4096u, 50000u}) {
    h.update(std::span<const std::uint8_t>(data).subspan(off, n));
    off += n;
  }
  h.update(std::span<const std::uint8_t>(data).subspan(off));
  const auto inc = h.finish();
  REQUIRE(inc);
  CHECK(*inc == *whole);
  CHECK_FALSE(h.finish().has_value());  // spent
}

TEST_CASE("crypto: sha256_file matches in-memory hash", "[crypto]") {
  const auto path = std::filesystem::temp_directory_path() / "x4mp_test_sha_file.bin";
  std::vector<std::uint8_t> data(3 * (1u << 20) + 17);
  for (std::size_t i = 0; i < data.size(); ++i) data[i] = static_cast<std::uint8_t>(i ^ (i >> 8));
  {
    std::ofstream out(path, std::ios::binary);
    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
  }
  const auto f = sha256_file(path);
  const auto m = sha256(data);
  std::filesystem::remove(path);
  REQUIRE(f);
  REQUIRE(m);
  CHECK(*f == *m);
  CHECK_FALSE(sha256_file(path).has_value());  // gone
}

TEST_CASE("crypto: HMAC-SHA256 RFC 4231 test case 2", "[crypto][hmac]") {
  const std::string key = "Jefe";
  const std::string msg = "what do ya want for nothing?";
  x4mp::wire::HmacSha256Tag tag{};
  BcryptHmacSha256().compute(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(key.data()), key.size()),
                             std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(msg.data()), msg.size()), tag);
  CHECK(hex_of(tag) == "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");
}

TEST_CASE("crypto: handshake proof matches the C# HandshakeAuth vectors", "[crypto][hmac][interop]") {
  const auto nonce = seq_bytes(0);
  const auto key = seq_bytes(0xA0);
  for (const auto& v : kVectors) {
    CAPTURE(v.password);
    const auto h = hash_password(v.password);
    REQUIRE(h);
    CHECK(hex(*h) == v.hash);
    const auto proof = compute_proof(std::string_view(v.password), nonce, key);
    REQUIRE(proof);
    CHECK(hex_of(*proof) == v.proof);
    // The same through the stored-hash overload (what the server and a resumed client use).
    const auto proof2 = compute_proof(std::span<const std::uint8_t>(*h), nonce, key);
    REQUIRE(proof2);
    CHECK(*proof2 == *proof);
  }
}

TEST_CASE("crypto: wire::HmacSha256::verify accepts the right tag and rejects others", "[crypto][hmac]") {
  const BcryptHmacSha256 hmac;
  const auto key = seq_bytes(1);
  const auto msg = seq_bytes(9);
  x4mp::wire::HmacSha256Tag tag{};
  hmac.compute(key, msg, tag);
  CHECK(hmac.verify(key, msg, tag));
  tag[5] ^= 1;
  CHECK_FALSE(hmac.verify(key, msg, tag));
}

TEST_CASE("crypto: hex round trip and CSPRNG", "[crypto]") {
  std::vector<std::uint8_t> out;
  REQUIRE(from_hex("00ff7Fa0", out));
  CHECK(to_hex(out) == "00ff7fa0");
  CHECK_FALSE(from_hex("abc", out));
  CHECK_FALSE(from_hex("zz", out));
  std::array<std::uint8_t, 32> a{};
  std::array<std::uint8_t, 32> b{};
  REQUIRE(random_bytes(a));
  REQUIRE(random_bytes(b));
  CHECK(a != b);
}
