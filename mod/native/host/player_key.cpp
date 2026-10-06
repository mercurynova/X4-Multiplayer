#include "host/player_key.h"

#include <algorithm>
#include <fstream>
#include <vector>

#include "core/crypto/crypto.h"

namespace x4mp::host {

namespace {
constexpr std::string_view kMachinePrefix = "machine=";

std::string trim(std::string s) {
  while (!s.empty() && (s.back() == '\r' || s.back() == '\n' || s.back() == ' ' || s.back() == '\t')) s.pop_back();
  return s;
}
}  // namespace

std::string machine_tag(std::string_view machine_id) {
  if (machine_id.empty()) return {};
  const auto* p = reinterpret_cast<const std::uint8_t*>(machine_id.data());
  const auto digest = crypto::sha256(crypto::ByteSpan(p, machine_id.size()));
  return digest ? crypto::to_hex(*digest) : std::string{};
}

KeyLoad load_or_create_player_key(const std::filesystem::path& file, std::string_view tag, std::array<std::uint8_t, 32>& key) {
  KeyLoad result;
  result.origin = KeyOrigin::CreatedNew;
  if (!file.empty()) {
    std::ifstream in(file);
    std::string hex;
    std::string second;
    std::vector<std::uint8_t> bytes;
    if (in && std::getline(in, hex) && crypto::from_hex(trim(hex), bytes) && bytes.size() == key.size()) {
      std::getline(in, second);
      second = trim(second);
      const bool tagged = second.rfind(kMachinePrefix, 0) == 0;
      const std::string_view file_tag = tagged ? std::string_view(second).substr(kMachinePrefix.size()) : std::string_view{};
      if (tag.empty() || (tagged && file_tag == tag)) {
        std::copy(bytes.begin(), bytes.end(), key.begin());
        result.ok = true;
        result.origin = KeyOrigin::Kept;
        return result;
      }
      result.origin = tagged ? KeyOrigin::ReplacedForeign : KeyOrigin::ReplacedUntagged;
    }
  }
  if (!crypto::random_bytes(key)) return result;
  result.ok = true;
  if (!file.empty()) {
    std::error_code ec;
    std::filesystem::create_directories(file.parent_path(), ec);
    std::ofstream out(file, std::ios::trunc);
    if (out) {
      out << crypto::to_hex(key) << "\n";  // the player's identity key: stays on this machine
      if (!tag.empty()) out << kMachinePrefix << tag << "\n";
    }
  }
  return result;
}

std::string describe_key_origin(KeyOrigin origin) {
  switch (origin) {
    case KeyOrigin::ReplacedUntagged:
      return "player.key without a machine tag was found and not adopted (it may be shared by several PCs): a new player identity was created for this machine";
    case KeyOrigin::ReplacedForeign:
      return "player.key tagged with another machine was found and not adopted: a new player identity was created for this machine";
    case KeyOrigin::CreatedNew:
      return "no player.key yet: a new player identity was created for this machine";
    case KeyOrigin::Kept:
      break;
  }
  return {};
}

}  // namespace x4mp::host
