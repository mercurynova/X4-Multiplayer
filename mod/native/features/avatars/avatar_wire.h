#pragma once
// features/avatars: decode of the few inbound messages the authority's avatars react to, and the one outbound message the avatars add
// (EntityChange{Controller}). The EntitySpawn of an avatar goes through core/authority EntitySpawnBuilder (the only encoder of it).
// FlatBuffers headers stay in avatar_wire.cpp; everything here is plain C++.
//
//   PlayerShip            server -> authority: a player asks for its avatar (player_id is stamped by the server)
//   PlayerState           server -> authority: the relayed state of a client's ship (net_id = the avatar, stamped by the server)
//   EntityDespawn         server -> authority: an admin removed a player's avatar (reason Removed): remove the real ship
//   RosterUpdate          server -> everyone: names, teams, online flags, removals
//   ServerSettingsUpdate  server -> every node: the live settings flagged for nodes (Avatars.* among them)

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "features/avatars/avatar_plan.h"

namespace x4mp::features::avatars {

struct PlayerShipReq {
  std::uint16_t player_id = 0;
  std::string ship_macro, name, idcode;
  std::uint16_t sector = 0;
  Pose pose;
};

struct PlayerStateIn {
  std::uint32_t net_id = 0;
  std::uint16_t sector = 0;
  std::uint16_t flags = 0;
  std::int64_t sample_time_us = 0;  // server clock
  Pose pose;
  std::uint8_t hull = 255, shield = 255;
};

struct RosterRow {
  std::uint16_t id = 0;
  std::string name;
  std::uint16_t team = 0;
  std::uint8_t phase = 0;
  std::uint32_t ship_net_id = 0;
  bool online = true;
};
struct RosterIn {
  bool full = false;
  std::vector<RosterRow> players;
  std::vector<std::uint16_t> removed;
};

struct DespawnIn {
  std::vector<std::pair<std::uint32_t, std::uint8_t>> entries;  // net_id, DespawnReason
};
inline constexpr std::uint8_t kDespawnRemoved = 3;  // DespawnReason::Removed

struct SettingsIn {
  std::uint64_t version = 0;
  std::vector<std::pair<std::string, std::string>> entries;
};

[[nodiscard]] std::optional<PlayerShipReq> decode_player_ship(std::span<const std::uint8_t> payload);
[[nodiscard]] std::optional<PlayerStateIn> decode_player_state(std::span<const std::uint8_t> payload);
[[nodiscard]] std::optional<RosterIn> decode_roster(std::span<const std::uint8_t> payload);
[[nodiscard]] std::optional<DespawnIn> decode_despawn(std::span<const std::uint8_t> payload);
[[nodiscard]] std::optional<SettingsIn> decode_settings(std::span<const std::uint8_t> payload);

// EntityChange{fields = Controller, controller_player = player} for an avatar (player 0 = parked).
[[nodiscard]] std::vector<std::uint8_t> encode_controller_change(std::uint32_t net_id, std::uint16_t player);

}  // namespace x4mp::features::avatars
