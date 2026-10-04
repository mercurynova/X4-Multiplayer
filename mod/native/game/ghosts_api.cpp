#include "game/ghosts_api.h"

#include <algorithm>

#include "game/main_thread.h"

namespace x4mp::game {

namespace {
using NumFactionsFn = std::uint32_t (*)(bool includehidden);
using FactionsFn = std::uint32_t (*)(const char** result, std::uint32_t resultlen, bool includehidden);
using NumShipsFn = std::uint32_t (*)(const char* factionid);
using ShipsFn = std::uint32_t (*)(UniverseId* result, std::uint32_t resultlen, const char* factionid);

constexpr std::uint32_t kMaxShips = 2000;  // a team never owns more; bounds the call
}  // namespace

PosRotPod to_pos_rot(const GhostPose& p) noexcept {
  PosRotPod u;
  u.x = static_cast<float>(p.x);
  u.y = static_cast<float>(p.y);
  u.z = static_cast<float>(p.z);
  u.yaw = static_cast<float>(p.yaw * kYawSign);
  u.pitch = static_cast<float>(p.pitch * kPitchSign);
  u.roll = static_cast<float>(p.roll * kRollSign);
  return u;
}

GhostPose from_pos_rot(const PosRotPod& p) noexcept {
  GhostPose g;
  g.x = p.x;
  g.y = p.y;
  g.z = p.z;
  g.yaw = p.yaw * kYawSign;
  g.pitch = p.pitch * kPitchSign;
  g.roll = p.roll * kRollSign;
  return g;
}

GhostsApi::GhostsApi(const GameApi& api, GetFunctionFn get) : api_(api), get_(std::move(get)) {}

UniverseId GhostsApi::spawn(std::string_view macro, UniverseId sector, const GhostPose& pose, std::string_view owner) const {
  if (macro.empty() || sector == 0 || owner.empty()) return 0;  // an empty owner would make the game pick its own: never
  const std::string m(macro), o(owner);                        // NUL-terminated copies
  return api_.spawn_object(m.c_str(), sector, to_pos_rot(pose), o.c_str());
}

bool GhostsApi::make_inert(UniverseId id) const {
  const bool a = api_.activate_object(id, false);
  const bool r = api_.set_object_forced_radar_visible(id, true);
  return a || r;
}

bool GhostsApi::place(UniverseId id, UniverseId sector, const GhostPose& pose) const {
  return api_.set_object_sector_pos(id, sector, to_pos_rot(pose));
}

std::optional<GhostPose> GhostsApi::pose(UniverseId id) const {
  const auto p = api_.object_position(id);
  if (!p) return std::nullopt;
  return from_pos_rot(*p);
}

bool GhostsApi::valid(UniverseId id) const { return id != 0 && api_.is_valid_component(id); }

bool GhostsApi::wrecked(UniverseId id) const { return api_.component_wrecked(id); }

std::string GhostsApi::id_code(UniverseId id) const { return api_.object_id_code(id).value_or(std::string()); }

std::string GhostsApi::name(UniverseId id) const { return api_.component_name(id).value_or(std::string()); }

UniverseId GhostsApi::sector_of(UniverseId id) const { return api_.context_by_class(id, "sector", false); }

UniverseId GhostsApi::local_ship() const {
  if (const UniverseId s = api_.player_occupied_ship()) return s;
  if (const UniverseId s = api_.player_controlled_ship()) return s;
  return api_.player_object();
}

FactionState GhostsApi::faction_state(std::string_view faction, bool refresh) {
  if (!get_) return FactionState::Unknown;
  if (refresh || !factions_known_) {
    const auto num = reinterpret_cast<NumFactionsFn>(get_("GetNumAllFactions"));
    const auto list = reinterpret_cast<FactionsFn>(get_("GetAllFactions"));
    if (!num || !list || !assert_main_thread("GetAllFactions")) return FactionState::Unknown;
    factions_.clear();
    const std::uint32_t n = num(true);
    if (n > 0) {
      std::vector<const char*> ids(n, nullptr);
      const std::uint32_t got = list(ids.data(), n, true);
      for (std::uint32_t i = 0; i < got && i < n; ++i) {
        if (ids[i]) factions_.emplace_back(ids[i]);
      }
    }
    factions_known_ = true;
  }
  return std::ranges::find(factions_, faction) != factions_.end() ? FactionState::Present : FactionState::Missing;
}

std::vector<UniverseId> GhostsApi::ships_of(std::string_view faction) const {
  std::vector<UniverseId> out;
  if (!get_ || faction.empty() || !assert_main_thread("GetAllFactionShips")) return out;
  const auto num = reinterpret_cast<NumShipsFn>(get_("GetNumAllFactionShips"));
  const auto list = reinterpret_cast<ShipsFn>(get_("GetAllFactionShips"));
  if (!num || !list) return out;
  const std::string f(faction);
  std::uint32_t n = num(f.c_str());
  if (n == 0) return out;
  n = std::min(n, kMaxShips);
  out.resize(n);
  const std::uint32_t got = list(out.data(), n, f.c_str());
  out.resize(std::min(got, n));
  return out;
}

}  // namespace x4mp::game
