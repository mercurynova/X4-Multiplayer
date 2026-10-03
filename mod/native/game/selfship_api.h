#pragma once
// game/selfship_api: the per-frame reads of the player's own ship (M3-09). SDK-free (it only uses GameApi); every call is main-thread
// checked by the GameApi wrappers and null-safe. The sampled calls were measured in session 4, sitting 0 (S13.4): the whole read costs
// a few microseconds. No allocation.
//
//   occupied      GetPlayerOccupiedShipID: the pilot-seat ship (0 while standing / on foot)  [S13.5: 0 -> ship at sit-down]
//   standing_ship GetPlayerContainerID when the player stands and the container is a ship     [S13.5: container = the ship]
//   sector        GetContextByClass(ship, "sector")   valid in every sample, superhighways included  [S13.4]
//   in_highway    GetContextByClass(ship, "highway")  local and super highways                [S13.4]
//   docked        IsPlayerOccupiedShipDocked
//   pose          GetObjectPositionInSector(ship): sector-local metres, angles RADIANS      [S13.4]

#include <cstdint>

#include "game/game_api.h"

namespace x4mp::game {

struct OwnShipRead {
  UniverseId occupied = 0;
  UniverseId standing_ship = 0;
  UniverseId sector = 0;
  bool in_highway = false;
  bool docked = false;
  bool pose_valid = false;
  PosRotPod pose{};
  [[nodiscard]] UniverseId ship() const noexcept { return occupied != 0 ? occupied : standing_ship; }
};

[[nodiscard]] OwnShipRead read_own_ship(const GameApi& api) noexcept;

}  // namespace x4mp::game
