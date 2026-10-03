#pragma once
// game/avatars_api: the game exports the authority's avatars need on top of GameApi (M3-11; GameFns is frozen since M3-04, so they are resolved here
// by name through the same lookup). SDK-free; every call is main-thread checked and null-safe.
//
//   GetNumAllFactions / GetAllFactions            which x4mp_team_* factions the game lists (asking about an unknown faction writes an error line)
//   GetNumAllFactionShips / GetAllFactionShips    the ships a faction owns (the AvatarBinder's candidates after a save load)

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "game/game_api.h"

namespace x4mp::game {

struct OwnedShip {
  UniverseId id = 0;
  std::string faction;
};

class AvatarsApi {
 public:
  AvatarsApi() = default;
  explicit AvatarsApi(const GetFunctionFn& get);

  [[nodiscard]] bool available() const noexcept { return num_ships_ && ships_ && num_factions_ && factions_; }
  // Every ship owned by an existing x4mp_team_<1..8> faction. nullopt = the exports are missing or the main-thread check failed.
  [[nodiscard]] std::optional<std::vector<OwnedShip>> team_ships() const;

 private:
  using NumShipsFn = std::uint32_t (*)(const char*);
  using ShipsFn = std::uint32_t (*)(UniverseId*, std::uint32_t, const char*);
  using NumFactionsFn = std::uint32_t (*)(bool);
  using FactionsFn = std::uint32_t (*)(const char**, std::uint32_t, bool);
  NumShipsFn num_ships_ = nullptr;
  ShipsFn ships_ = nullptr;
  NumFactionsFn num_factions_ = nullptr;
  FactionsFn factions_ = nullptr;
};

}  // namespace x4mp::game
