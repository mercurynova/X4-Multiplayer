#pragma once
// game/player_guard: per-frame refresh of the "never remove" id set (mod-design 6.1, CLAUDE.md hard rule).
//
// Gathers the player's occupied ship, controlled ship, player entity, player object, the container the player is
// docked in, plus the ship/station context of each of those (GetContextByClass), drops 0 and ids the game says are
// invalid, and hands the set to game::set_player_guard(). When the player is docked GetPlayerControlledShipID is 0,
// which is why the container and contexts are included. Main-thread only (uses GameApi); call it only once the
// universe is ready.

#include <vector>

#include "game/game_api.h"
#include "game/safe_remove.h"

namespace x4mp::game {

// Collects the ids without installing them (testable).
[[nodiscard]] std::vector<ComponentId> collect_player_guard_ids(const GameApi& api);

// collect + set_player_guard. Returns the number of guarded ids.
std::size_t refresh_player_guard(const GameApi& api);

}  // namespace x4mp::game
