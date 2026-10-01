#include "game/safe_remove.h"

#include <algorithm>
#include <mutex>
#include <vector>

namespace x4mp::game {

namespace {
std::mutex g_mutex;
std::vector<ComponentId> g_guard;
std::uint64_t g_blocked = 0;
}  // namespace

void set_player_guard(std::span<const ComponentId> ids) {
  const std::lock_guard lock(g_mutex);
  g_guard.assign(ids.begin(), ids.end());
}

bool is_player_guarded(ComponentId id) noexcept {
  const std::lock_guard lock(g_mutex);
  return std::ranges::find(g_guard, id) != g_guard.end();
}

std::uint64_t blocked_removal_count() noexcept {
  const std::lock_guard lock(g_mutex);
  return g_blocked;
}

RemoveResult safe_remove(ComponentId id) {
  const std::lock_guard lock(g_mutex);
  if (id == 0) {
    ++g_blocked;
    return RemoveResult::BlockedInvalidId;
  }
  if (std::ranges::find(g_guard, id) != g_guard.end()) {
    ++g_blocked;
    return RemoveResult::BlockedPlayerGuard;
  }
  // TODO(M1-N3): the one and only call to the game's RemoveComponent goes here (through X4Native).
  return RemoveResult::NotImplemented;
}

}  // namespace x4mp::game