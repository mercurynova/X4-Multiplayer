#include "features/janitor/janitor_feature.h"

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

struct Exports {
  NumFn num_ships = nullptr, num_stations = nullptr;
  ListFn ships = nullptr, stations = nullptr;
  NameFn name = nullptr;
  [[nodiscard]] bool complete() const { return num_ships && num_stations && ships && stations && name; }
};

Exports resolve(host::IPlatform& p) {
  Exports e;
  e.num_ships = reinterpret_cast<NumFn>(p.get_game_function("GetNumAllFactionShips"));
  e.num_stations = reinterpret_cast<NumFn>(p.get_game_function("GetNumAllFactionStations"));
  e.ships = reinterpret_cast<ListFn>(p.get_game_function("GetAllFactionShips"));
  e.stations = reinterpret_cast<ListFn>(p.get_game_function("GetAllFactionStations"));
  e.name = reinterpret_cast<NameFn>(p.get_game_function("GetComponentName"));
  return e;
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
  for (int i = 1; i <= 8; ++i) factions.push_back("x4mp_team_" + std::to_string(i));
  for (const auto& f : factions) {
    scan_list(e, e.num_ships, e.ships, f, r);
    scan_list(e, e.num_stations, e.stations, f, r);
  }
  return r;
}

}  // namespace x4mp::features
