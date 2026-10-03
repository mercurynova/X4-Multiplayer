#include "game/player_guard.h"

#include <algorithm>

namespace x4mp::game {

std::vector<ComponentId> collect_player_guard_ids(const GameApi& api) {
  std::vector<ComponentId> ids;
  ids.reserve(12);
  const auto add = [&](UniverseId id) {
    if (id == 0 || std::ranges::find(ids, id) != ids.end()) return;
    if (!api.is_valid_component(id)) return;
    ids.push_back(id);
  };
  const UniverseId roots[] = {api.player_occupied_ship(), api.player_controlled_ship(), api.player_id(),
                              api.player_object(), api.player_container()};
  for (const UniverseId r : roots) add(r);
  // Contexts: the ship and the station that contain a guarded id (snapshot of roots, not of the growing vector).
  for (const UniverseId r : roots) {
    if (r == 0) continue;
    add(api.context_by_class(r, "ship", true));
    add(api.context_by_class(r, "station", true));
  }
  return ids;
}

std::size_t refresh_player_guard(const GameApi& api) {
  const auto ids = collect_player_guard_ids(api);
  set_player_guard(ids);
  return ids.size();
}

}  // namespace x4mp::game
