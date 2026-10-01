#pragma once
// core/crypto: SHA-256 and HMAC-SHA256 over Windows CNG (BCrypt), the OS crypto library (protocol.md 4.3).
//
//   proof = HMAC-SHA256(key = SHA256(utf8(password)), msg = nonce || player_key)   (HandshakeAuth.cs, byte for byte)
//
// No exceptions: failures are reported through return values (a BCrypt failure on a stock Windows install means
// the process is already in trouble, so callers treat it as a hard error). Thread-safe: the algorithm provider is
// a process-wide handle opened once; hash objects are per instance. core/ only; no X4 SDK.

#include <array>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "x4mp/wire.h"

namespace x4mp::crypto {

inline constexpr std::size_t kSha256Size = 32;
using Sha256Digest = std::array<std::uint8_t, kSha256Size>;
using ByteSpan = std::span<const std::uint8_t>;

// Incremental SHA-256 (BCryptCreateHash / HashData / FinishHash). Move-only. After finish() the object is spent.
class Sha256Hasher {
 public:
  Sha256Hasher();
  ~Sha256Hasher();
  Sha256Hasher(Sha256Hasher&&) noexcept;
  Sha256Hasher& operator=(Sha256Hasher&&) noexcept;
  Sha256Hasher(const Sha256Hasher&) = delete;
  Sha256Hasher& operator=(const Sha256Hasher&) = delete;

  [[nodiscard]] bool ok() const noexcept;  // false if the hash object could not be created or a call failed
  void update(ByteSpan data) noexcept;
  [[nodiscard]] std::optional<Sha256Digest> finish() noexcept;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

[[nodiscard]] std::optional<Sha256Digest> sha256(ByteSpan data) noexcept;
// Streams the whole file through SHA-256 (1 MiB reads). nullopt if it cannot be read.
[[nodiscard]] std::optional<Sha256Digest> sha256_file(const std::filesystem::path& file) noexcept;

// HMAC-SHA256 through BCrypt, as the wire.h interface (so it can be injected / replaced by a test double).
class BcryptHmacSha256 final : public wire::HmacSha256 {
 public:
  void compute(ByteSpan key, ByteSpan message, wire::HmacSha256Tag& out) const noexcept override;
};

// SHA-256 of the UTF-8 password: what the server stores and what keys the proof.
[[nodiscard]] std::optional<Sha256Digest> hash_password(std::string_view password_utf8) noexcept;
// HMAC-SHA256(key = password_hash, msg = nonce || player_key). nullopt only on a BCrypt failure.
[[nodiscard]] std::optional<wire::HmacSha256Tag> compute_proof(ByteSpan password_hash, ByteSpan nonce,
                                                               ByteSpan player_key) noexcept;
[[nodiscard]] std::optional<wire::HmacSha256Tag> compute_proof(std::string_view password_utf8, ByteSpan nonce,
                                                               ByteSpan player_key) noexcept;

// Fills `out` from the OS CSPRNG (BCryptGenRandom). False on failure.
[[nodiscard]] bool random_bytes(std::span<std::uint8_t> out) noexcept;

// Lower-case hex helpers (stash serialisation, logs, tests).
[[nodiscard]] std::string to_hex(ByteSpan bytes);
[[nodiscard]] bool from_hex(std::string_view hex, std::vector<std::uint8_t>& out);

}  // namespace x4mp::crypto
