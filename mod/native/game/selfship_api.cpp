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
  if (ship == 0) {
    // On foot in a station there is no ship, but the relay keys interest on the sector of the last PlayerState and sends no
    // CaptureSet at all while it is 0 (M4). The sector of the BODY is the one the player is in, so it is read from the player
    // object first; the controlled hull is only a fallback (a taxi may have left it in another sector). No pose: the body's
    // coordinates are container-local and must never be written into a sector as a ship position.
    const UniverseId body = api.player_object();
    const UniverseId where = body != 0 ? body : api.player_controlled_ship();
    r.sector = api.context_by_class(where, "sector", true);
    return r;
  }
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
