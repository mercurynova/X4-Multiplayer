#pragma once
// host/player_key (M3-24): the player's identity key file (player.key) and its machine tag.
//
//   line 1: 64 hex chars (the 32 byte key)
//   line 2: machine=<sha256 hex of the Windows MachineGuid>      (absent in files written before M3-24)
//
// The key is the player's identity on the server, so two PCs must never end up with the same one. A key file that is not
// tagged with THIS machine (no tag, or another machine's tag) is never adopted: a fresh key replaces it. Without a machine id
// (registry unreadable) no judgement is possible: any valid key is kept and new files are written untagged.
// Neither the raw machine id nor the key is ever logged.

#include <array>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>

namespace x4mp::host {

enum class KeyOrigin {
  Kept,              // valid key tagged with this machine (or no machine id available)
  CreatedNew,        // no usable file
  ReplacedUntagged,  // a key file without a machine tag was found and NOT adopted
  ReplacedForeign,   // a key file tagged with another machine was found and NOT adopted
};

struct KeyLoad {
  bool ok = false;
  KeyOrigin origin = KeyOrigin::CreatedNew;
};

// sha256 hex of the id; empty when the id is empty.
[[nodiscard]] std::string machine_tag(std::string_view machine_id);

// Reads `file` per the rules above, or creates it. An empty `file` gives a fresh in-memory key (nothing stored).
[[nodiscard]] KeyLoad load_or_create_player_key(const std::filesystem::path& file, std::string_view tag, std::array<std::uint8_t, 32>& key);

// One log line for a non-Kept origin (no secrets), empty for Kept.
[[nodiscard]] std::string describe_key_origin(KeyOrigin origin);

}  // namespace x4mp::host
