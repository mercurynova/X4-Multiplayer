#pragma once
// Test-only: writes a gzip file in "stored" (uncompressed) deflate blocks, so a save fixture of any size needs no deflate
// library. The content is an X4-style <savegame> document (what the server's SaveSniffer accepts) padded with an XML comment
// of letters; `seed` makes every fixture hash differently.

#include <algorithm>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

namespace x4mp::testing {

inline std::uint32_t crc32_update(std::uint32_t crc, const std::uint8_t* p, std::size_t n) {
  static const auto table = [] {
    std::vector<std::uint32_t> t(256);
    for (std::uint32_t i = 0; i < 256; ++i) {
      std::uint32_t c = i;
      for (int k = 0; k < 8; ++k) c = (c & 1u) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
      t[i] = c;
    }
    return t;
  }();
  crc = ~crc;
  for (std::size_t i = 0; i < n; ++i) crc = table[(crc ^ p[i]) & 0xFFu] ^ (crc >> 8);
  return ~crc;
}

// Returns the uncompressed size written (>= min_bytes). False on an I/O error.
inline bool write_stored_gzip(const std::string& path, std::uint64_t min_bytes, std::uint64_t seed, std::uint64_t* bytes_out = nullptr) {
  std::string xml =
      "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame><info><save name=\"X4MP fixture " + std::to_string(seed) +
      "\" date=\"1760000000\"/><game id=\"x4mp-fake\" version=\"900\" build=\"611726\"/><player name=\"Fixture\" location=\"{galaxy}\" "
      "money=\"1000\"/></info><universe seed=\"1\"><!--";
  const std::string tail = "--></universe></savegame>";
  std::uint64_t x = seed * 0x9E3779B97F4A7C15ull + 0x1234567ull;
  while (xml.size() + tail.size() < min_bytes) {
    x ^= x << 13;
    x ^= x >> 7;
    x ^= x << 17;
    xml.push_back(static_cast<char>('a' + (x % 26)));
  }
  xml += tail;
  std::ofstream out(path, std::ios::binary | std::ios::trunc);
  if (!out) return false;
  const std::uint8_t header[10] = {0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF};
  out.write(reinterpret_cast<const char*>(header), sizeof(header));
  const auto* data = reinterpret_cast<const std::uint8_t*>(xml.data());
  std::size_t at = 0;
  while (at < xml.size()) {
    const std::size_t n = std::min<std::size_t>(65535, xml.size() - at);
    const std::uint8_t b[5] = {static_cast<std::uint8_t>(at + n == xml.size() ? 1 : 0), static_cast<std::uint8_t>(n & 0xFF),
                               static_cast<std::uint8_t>(n >> 8), static_cast<std::uint8_t>(~n & 0xFF), static_cast<std::uint8_t>((~n >> 8) & 0xFF)};
    out.write(reinterpret_cast<const char*>(b), sizeof(b));
    out.write(reinterpret_cast<const char*>(data + at), static_cast<std::streamsize>(n));
    at += n;
  }
  const std::uint32_t crc = crc32_update(0, data, xml.size());
  const std::uint32_t isize = static_cast<std::uint32_t>(xml.size());
  const std::uint8_t trailer[8] = {static_cast<std::uint8_t>(crc), static_cast<std::uint8_t>(crc >> 8), static_cast<std::uint8_t>(crc >> 16),
                                   static_cast<std::uint8_t>(crc >> 24), static_cast<std::uint8_t>(isize), static_cast<std::uint8_t>(isize >> 8),
                                   static_cast<std::uint8_t>(isize >> 16), static_cast<std::uint8_t>(isize >> 24)};
  out.write(reinterpret_cast<const char*>(trailer), sizeof(trailer));
  if (bytes_out != nullptr) *bytes_out = xml.size();
  return static_cast<bool>(out);
}

}  // namespace x4mp::testing
