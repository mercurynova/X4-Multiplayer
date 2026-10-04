#include "game/avatars_api.h"

#include <algorithm>
#include <string_view>

#include "game/main_thread.h"

namespace x4mp::game {

AvatarsApi::AvatarsApi(const GetFunctionFn& get) {
  if (!get) return;
  num_ships_ = reinterpret_cast<NumShipsFn>(get("GetNumAllFactionShips"));
  ships_ = reinterpret_cast<ShipsFn>(get("GetAllFactionShips"));
  num_factions_ = reinterpret_cast<NumFactionsFn>(get("GetNumAllFactions"));
  factions_ = reinterpret_cast<FactionsFn>(get("GetAllFactions"));
}

std::optional<std::vector<OwnedShip>> AvatarsApi::team_ships() const {
  if (!available() || !assert_main_thread("avatars team_ships")) return std::nullopt;
  std::vector<std::string> teams;
  const std::uint32_t nf = num_factions_(true);
  if (nf > 0) {
    std::vector<const char*> ids(nf, nullptr);
    const std::uint32_t got = factions_(ids.data(), nf, true);
    for (std::uint32_t i = 0; i < got && i < nf; ++i) {
      if (ids[i] != nullptr && std::string_view(ids[i]).starts_with("x4mp_team_")) teams.emplace_back(ids[i]);
    }
  }
  std::sort(teams.begin(), teams.end());
  teams.erase(std::unique(teams.begin(), teams.end()), teams.end());
  std::vector<OwnedShip> out;
  for (const auto& team : teams) {
    const std::uint32_t n = num_ships_(team.c_str());
    if (n == 0) continue;
    std::vector<UniverseId> ids(n, 0);
    const std::uint32_t got = ships_(ids.data(), n, team.c_str());
    for (std::uint32_t i = 0; i < got && i < n; ++i) {
      if (ids[i] != 0) out.push_back({ids[i], team});
    }
  }
  return out;
}

}  // namespace x4mp::game
