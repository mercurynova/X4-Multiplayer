#include "game/safe_remove.h"

#include "game/main_thread.h"

#include <algorithm>
#include <mutex>
#include <vector>

namespace x4mp::game {

namespace {
std::mutex g_mutex;
std::vector<ComponentId> g_guard;
std::uint64_t g_blocked = 0;
RemoveBackend g_backend = nullptr;
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

void set_remove_backend(RemoveBackend backend) {
  const std::lock_guard lock(g_mutex);
  g_backend = backend;
}

bool install_game_backend(const std::function<void*(const char* name)>& get) {
  // The ONLY place the game's removal export is named (guard.no_raw_remove allows game/safe_remove.* only).
  void* fn = get ? get("RemoveComponent") : nullptr;
  const std::lock_guard lock(g_mutex);
  g_backend = reinterpret_cast<RemoveBackend>(fn);
  return g_backend != nullptr;
}

RemoveResult safe_remove(ComponentId id) {
  if (!assert_main_thread("safe_remove")) {
    const std::lock_guard lock(g_mutex);
    ++g_blocked;
    return RemoveResult::BlockedWrongThread;
  }
  const std::lock_guard lock(g_mutex);
  if (id == 0) {
    ++g_blocked;
    return RemoveResult::BlockedInvalidId;
  }
  if (std::ranges::find(g_guard, id) != g_guard.end()) {
    ++g_blocked;
    return RemoveResult::BlockedPlayerGuard;
  }
  if (!g_backend) return RemoveResult::NotImplemented;
  g_backend(id);
  return RemoveResult::Removed;
}

}  // namespace x4mp::game