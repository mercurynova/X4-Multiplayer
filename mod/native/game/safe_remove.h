#pragma once
// game/safe_remove: THE single guarded removal function (mod-design 6.1). Nothing else in mod/native may call
// the game's component-removal API; mod/tools/check-no-raw-remove.ps1 enforces that as a CTest guard.
//
// STUB (M0-08): the guard logic is real, the actual game call is not wired yet. When the guard allows a
// removal, safe_remove returns NotImplemented and removes nothing.

#include <cstdint>
#include <span>

namespace x4mp::game {

using ComponentId = std::uint64_t;  // X4 UniverseID

enum class RemoveResult {
  BlockedInvalidId,      // id 0
  BlockedPlayerGuard,    // id is (or is the context of) one of the player's guarded ids
  NotImplemented,        // allowed by the guard; the real game call arrives with M1-N3
};

// Replaces the set of ids that must never be removed (player ship, controlled ship, occupied ship, docked
// container ...). Called every frame by the player-guard refresh.
void set_player_guard(std::span<const ComponentId> ids);

[[nodiscard]] bool is_player_guarded(ComponentId id) noexcept;

// Count of removals refused so far (logged as remove_blocked_by_guard by the caller).
[[nodiscard]] std::uint64_t blocked_removal_count() noexcept;

[[nodiscard]] RemoveResult safe_remove(ComponentId id);

}  // namespace x4mp::game