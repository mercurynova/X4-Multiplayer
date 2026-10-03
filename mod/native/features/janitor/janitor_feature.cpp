#include "features/janitor/janitor_feature.h"

#include <algorithm>
#include <string>
#include <vector>

#include "game/game_api.h"
#include "game/main_thread.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
using NumFn = std::uint32_t (*)(const char* factionid);
using ListFn = std::uint32_t (*)(game::UniverseId* result, std::uint32_t resultlen, const char* factionid);
using NameFn = const char* (*)(game::UniverseId id);
using NumFactionsFn = std::uint32_t (*)(bool includehidden);
using FactionsFn = std::uint32_t (*)(const char** result, std::uint32_t resultlen, bool includehidden);

struct Exports {
  NumFn num_ships = nullptr, num_stations = nullptr;
  ListFn ships = nullptr, stations = nullptr;
  NameFn name = nullptr;
  NumFactionsFn num_factions = nullptr;
  FactionsFn factions = nullptr;
  [[nodiscard]] bool complete() const { return num_ships && num_stations && ships && stations && name; }
};

Exports resolve(host::IPlatform& p) {
  Exports e;
  e.num_ships = reinterpret_cast<NumFn>(p.get_game_function("GetNumAllFactionShips"));
  e.num_stations = reinterpret_cast<NumFn>(p.get_game_function("GetNumAllFactionStations"));
  e.ships = reinterpret_cast<ListFn>(p.get_game_function("GetAllFactionShips"));
  e.stations = reinterpret_cast<ListFn>(p.get_game_function("GetAllFactionStations"));
  e.name = reinterpret_cast<NameFn>(p.get_game_function("GetComponentName"));
  e.num_factions = reinterpret_cast<NumFactionsFn>(p.get_game_function("GetNumAllFactions"));
  e.factions = reinterpret_cast<FactionsFn>(p.get_game_function("GetAllFactions"));
  return e;
}

// Close-out A item 6: GetNumAllFaction{Ships,Stations}("x4mp_team_N") wrote "Failed to retrieve faction with ID" to the game log (16 lines at
// every load) because the team factions are not defined yet. Only ask about factions the game lists (GetAllFactions, hidden ones included).
std::vector<std::string> existing_factions(const Exports& e) {
  std::vector<std::string> out;
  if (!e.num_factions || !e.factions) return out;
  const std::uint32_t n = e.num_factions(true);
  if (n == 0) return out;
  std::vector<const char*> ids(n, nullptr);
  const std::uint32_t got = e.factions(ids.data(), n, true);
  for (std::uint32_t i = 0; i < got && i < n; ++i) {
    if (ids[i]) out.emplace_back(ids[i]);
  }
  return out;
}

void scan_list(const Exports& e, NumFn num, ListFn list, const std::string& faction, JanitorResult& r) {
  if (r.scanned >= JanitorFeature::kMaxObjects) return;
  const std::uint32_t n = num(faction.c_str());
  if (n == 0) return;
  std::vector<game::UniverseId> ids(n);
  const std::uint32_t got = list(ids.data(), n, faction.c_str());
  for (std::uint32_t i = 0; i < got && i < n && r.scanned < JanitorFeature::kMaxObjects; ++i) {
    const char* nm = e.name(ids[i]);  // valid until the next call: inspect immediately
    ++r.scanned;
    if (nm && std::string_view(nm).starts_with(kGhostNamePrefix)) ++r.marked;
  }
}
}  // namespace

void JanitorFeature::on_init(host::HostContext&) {}

void JanitorFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  if (!pending_.exchange(false)) return;
  if (!info.universe_ready) return;
  last_ = scan(ctx);
  if (last_.exports_missing) {
    ctx.log.raw(Cat::Ghost, Level::Info, "janitor: skipped (the game exports needed to list objects are not available)");
  } else {
    ctx.log.raw(Cat::Ghost, last_.marked ? Level::Warn : Level::Info,
                "janitor: " + std::to_string(last_.marked) + " objects with the '[MP] ' name prefix among " +
                    std::to_string(last_.scanned) + " scanned (nothing removed: skeleton)");
  }
}

JanitorResult JanitorFeature::scan(host::HostContext& ctx) {
  JanitorResult r;
  if (!game::assert_main_thread("janitor")) return r;
  const Exports e = resolve(ctx.platform);
  if (!e.complete()) {
    r.exports_missing = true;
    return r;
  }
  r.ran = true;
  std::vector<std::string> factions{"player"};
  const auto known = existing_factions(e);
  for (int i = 1; i <= 8; ++i) {
    const std::string team = "x4mp_team_" + std::to_string(i);
    if (std::find(known.begin(), known.end(), team) != known.end()) factions.push_back(team);
  }
  for (const auto& f : factions) {
    scan_list(e, e.num_ships, e.ships, f, r);
    scan_list(e, e.num_stations, e.stations, f, r);
    ++r.factions_queried;
  }
  return r;
}

}  // namespace x4mp::features
