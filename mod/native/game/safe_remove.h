#pragma once
// game/safe_remove: THE single guarded removal function (mod-design 6.1). Nothing else in mod/native may call
// the game's component-removal API; mod/tools/check-no-raw-remove.ps1 enforces that as a CTest guard.
//
// The guard logic is real. The game call is a pluggable backend (M2-04): install_game_backend() resolves the export by
// name through the X4Native lookup; without a backend (tests, hostsim without the export) an allowed removal returns
// NotImplemented and removes nothing. The player-guard set is refreshed every frame by game/player_guard.

#include <cstdint>
#include <functional>
#include <span>

namespace x4mp::game {

using ComponentId = std::uint64_t;  // X4 UniverseID

enum class RemoveResult {
  BlockedInvalidId,      // id 0
  BlockedPlayerGuard,    // id is (or is the context of) one of the player's guarded ids
  BlockedWrongThread,    // not on the main thread (game/main_thread.h); nothing was called
  NotImplemented,        // allowed by the guard but no backend installed; removed nothing
  Removed,               // allowed by the guard and handed to the backend
};

using RemoveBackend = void (*)(ComponentId id);
void set_remove_backend(RemoveBackend backend);
// Resolves the game's removal export by name via `get` (X4NativeAPI::get_game_function). Returns false (and keeps
// no backend) when it is missing. Null-safe.
bool install_game_backend(const std::function<void*(const char* name)>& get);

// Replaces the set of ids that must never be removed (player ship, controlled ship, occupied ship, docked
// container ...). Called every frame by the player-guard refresh.
void set_player_guard(std::span<const ComponentId> ids);

[[nodiscard]] bool is_player_guarded(ComponentId id) noexcept;

// Count of removals refused so far (logged as remove_blocked_by_guard by the caller).
[[nodiscard]] std::uint64_t blocked_removal_count() noexcept;

[[nodiscard]] RemoveResult safe_remove(ComponentId id);

}  // namespace x4mp::game