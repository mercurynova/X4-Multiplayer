#include "game/selfship_api.h"

namespace x4mp::game {

OwnShipRead read_own_ship(const GameApi& api) noexcept {
  OwnShipRead r;
  r.occupied = api.player_occupied_ship();
  if (r.occupied == 0) {
    // Standing: the container is the ship the player stands in (S13.5); a station or room is not a ship.
    const UniverseId container = api.player_container();
    if (container != 0 && api.context_by_class(container, "ship", true) == container) r.standing_ship = container;
  }
  const UniverseId ship = r.ship();
  if (ship == 0) return r;
  r.sector = api.context_by_class(ship, "sector", true);
  r.in_highway = api.context_by_class(ship, "highway", true) != 0;
  r.docked = r.occupied != 0 && api.player_ship_docked();
  if (const auto pose = api.object_position(ship)) {
    r.pose = *pose;
    r.pose_valid = true;
  }
  return r;
}

}  // namespace x4mp::game
