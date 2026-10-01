#include "core/crypto/crypto.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <fstream>
#include <utility>

namespace x4mp::crypto {
namespace {

bool nt_ok(NTSTATUS s) noexcept { return s >= 0; }

// Process-wide providers, opened once (function-local statics are initialised thread-safely) and never closed:
// they live as long as the process, like the CRT.
BCRYPT_ALG_HANDLE sha_provider() noexcept {
  static const BCRYPT_ALG_HANDLE h = [] {
    BCRYPT_ALG_HANDLE out = nullptr;
    if (!nt_ok(::BCryptOpenAlgorithmProvider(&out, BCRYPT_SHA256_ALGORITHM, nullptr, 0))) return BCRYPT_ALG_HANDLE{};
    return out;
  }();
  return h;
}

BCRYPT_ALG_HANDLE hmac_provider() noexcept {
  static const BCRYPT_ALG_HANDLE h = [] {
    BCRYPT_ALG_HANDLE out = nullptr;
    if (!nt_ok(::BCryptOpenAlgorithmProvider(&out, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG))) {
      return BCRYPT_ALG_HANDLE{};
    }
    return out;
  }();
  return h;
}

}  // namespace

// ---- Sha256Hasher ---------------------------------------------------------------------------------------------
struct Sha256Hasher::Impl {
  BCRYPT_HASH_HANDLE h = nullptr;
  bool good = false;
  Impl() = default;
  Impl(const Impl&) = delete;
  Impl& operator=(const Impl&) = delete;
  ~Impl() {
    if (h != nullptr) ::BCryptDestroyHash(h);
  }
};

Sha256Hasher::Sha256Hasher() : impl_(std::make_unique<Impl>()) {
  if (const auto prov = sha_provider()) {
    impl_->good = nt_ok(::BCryptCreateHash(prov, &impl_->h, nullptr, 0, nullptr, 0, 0));
  }
}
Sha256Hasher::~Sha256Hasher() = default;
Sha256Hasher::Sha256Hasher(Sha256Hasher&&) noexcept = default;
Sha256Hasher& Sha256Hasher::operator=(Sha256Hasher&&) noexcept = default;

bool Sha256Hasher::ok() const noexcept { return impl_ && impl_->good; }

void Sha256Hasher::update(ByteSpan data) noexcept {
  if (!ok()) return;
  // BCryptHashData takes a ULONG length: feed big buffers in slices.
  while (!data.empty()) {
    const ULONG n = static_cast<ULONG>(std::min<std::size_t>(data.size(), 1u << 28));
    if (!nt_ok(::BCryptHashData(impl_->h, const_cast<PUCHAR>(data.data()), n, 0))) {
      impl_->good = false;
      return;
    }
    data = data.subspan(n);
  }
}

std::optional<Sha256Digest> Sha256Hasher::finish() noexcept {
  if (!ok()) return std::nullopt;
  Sha256Digest d{};
  const bool good = nt_ok(::BCryptFinishHash(impl_->h, d.data(), static_cast<ULONG>(d.size()), 0));
  impl_->good = false;  // spent
  if (!good) return std::nullopt;
  return d;
}

std::optional<Sha256Digest> sha256(ByteSpan data) noexcept {
  Sha256Hasher h;
  h.update(data);
  return h.finish();
}

std::optional<Sha256Digest> sha256_file(const std::filesystem::path& file) noexcept {
  std::ifstream in(file, std::ios::binary);
  if (!in) return std::nullopt;
  Sha256Hasher h;
  std::vector<std::uint8_t> buf(1u << 20);
  while (in) {
    in.read(reinterpret_cast<char*>(buf.data()), static_cast<std::streamsize>(buf.size()));
    const auto got = in.gcount();
    if (got > 0) h.update(ByteSpan(buf.data(), static_cast<std::size_t>(got)));
  }
  if (!in.eof()) return std::nullopt;
  return h.finish();
}

// ---- HMAC -----------------------------------------------------------------------------------------------------
void BcryptHmacSha256::compute(ByteSpan key, ByteSpan message, wire::HmacSha256Tag& out) const noexcept {
  out.fill(0);
  const auto prov = hmac_provider();
  if (prov == nullptr) return;
  // BCryptHash (Windows 10+): one-shot keyed hash with the HMAC-flagged provider.
  (void)::BCryptHash(prov, const_cast<PUCHAR>(key.data()), static_cast<ULONG>(key.size()),
                     const_cast<PUCHAR>(message.data()), static_cast<ULONG>(message.size()), out.data(),
                     static_cast<ULONG>(out.size()));
}

std::optional<Sha256Digest> hash_password(std::string_view password_utf8) noexcept {
  return sha256(ByteSpan(reinterpret_cast<const std::uint8_t*>(password_utf8.data()), password_utf8.size()));
}

std::optional<wire::HmacSha256Tag> compute_proof(ByteSpan password_hash, ByteSpan nonce, ByteSpan player_key) noexcept {
  if (hmac_provider() == nullptr) return std::nullopt;
  std::vector<std::uint8_t> msg;
  msg.reserve(nonce.size() + player_key.size());
  msg.insert(msg.end(), nonce.begin(), nonce.end());
  msg.insert(msg.end(), player_key.begin(), player_key.end());
  wire::HmacSha256Tag tag{};
  BcryptHmacSha256().compute(password_hash, msg, tag);
  return tag;
}

std::optional<wire::HmacSha256Tag> compute_proof(std::string_view password_utf8, ByteSpan nonce,
                                                 ByteSpan player_key) noexcept {
  const auto h = hash_password(password_utf8);
  if (!h) return std::nullopt;
  return compute_proof(ByteSpan(h->data(), h->size()), nonce, player_key);
}

bool random_bytes(std::span<std::uint8_t> out) noexcept {
  return nt_ok(::BCryptGenRandom(nullptr, out.data(), static_cast<ULONG>(out.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG));
}

// ---- hex ------------------------------------------------------------------------------------------------------
std::string to_hex(ByteSpan bytes) {
  static constexpr char kDigits[] = "0123456789abcdef";
  std::string s;
  s.reserve(bytes.size() * 2);
  for (const std::uint8_t b : bytes) {
    s.push_back(kDigits[b >> 4]);
    s.push_back(kDigits[b & 0xF]);
  }
  return s;
}

bool from_hex(std::string_view hex, std::vector<std::uint8_t>& out) {
  if (hex.size() % 2 != 0) return false;
  const auto nib = [](char c) -> int {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
  };
  std::vector<std::uint8_t> tmp;
  tmp.reserve(hex.size() / 2);
  for (std::size_t i = 0; i < hex.size(); i += 2) {
    const int a = nib(hex[i]);
    const int b = nib(hex[i + 1]);
    if (a < 0 || b < 0) return false;
    tmp.push_back(static_cast<std::uint8_t>((a << 4) | b));
  }
  out = std::move(tmp);
  return true;
}

}  // namespace x4mp::crypto
