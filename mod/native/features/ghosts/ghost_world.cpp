#include "features/ghosts/ghost_world.h"

#include <charconv>
#include <cmath>
#include <cstdio>

#include <nlohmann/json.hpp>

#include "game/safe_remove.h"

namespace x4mp::features::ghosts {

game::GhostPose GameGhostWorld::to_game(const Pose& p) noexcept {
  game::GhostPose g;
  g.x = p.pos.x;
  g.y = p.pos.y;
  g.z = p.pos.z;
  g.yaw = p.rot.yaw;
  g.pitch = p.rot.pitch;
  g.roll = p.rot.roll;
  return g;
}

std::uint64_t GameGhostWorld::spawn(std::string_view macro, std::uint64_t sector, const Pose& pose, std::string_view owner) {
  return api_.spawn(macro, sector, to_game(pose), owner);
}

void GameGhostWorld::place(std::uint64_t id, std::uint64_t sector, const Pose& pose) { api_.place(id, sector, to_game(pose)); }

void GameGhostWorld::dress(std::uint64_t id, std::string_view label, int min_hull_percent) {
  nlohmann::json j = {{"v", 1}, {"id", std::to_string(id)}, {"name", std::string(label)}, {"minhull", min_hull_percent}};
  if (skip_radar_known_) j["skip_radar_known"] = true;
  platform_.raise_lua("x4mp.ghost_dress", j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace));
}

void GameGhostWorld::hint_velocities(std::span<const VelocityHint> hints) {
  if (hints.empty()) return;
  velocity_buf_.clear();
  char buf[96];
  for (const VelocityHint& h : hints) {
    const int n = std::snprintf(buf, sizeof buf, "%llu,%.2f,%.2f,%.2f", static_cast<unsigned long long>(h.id), h.vx, h.vy, h.vz);
    if (n <= 0) continue;
    if (!velocity_buf_.empty()) velocity_buf_ += ';';
    velocity_buf_.append(buf, static_cast<std::size_t>(n));
  }
  platform_.raise_lua("x4mp.ghost_velocity", velocity_buf_);
}

void GameGhostWorld::set_owner(std::uint64_t id, std::string_view owner) {
  const std::string o(owner);
  api_.api().set_component_owner(id, o.c_str());
}

RemoveOutcome GameGhostWorld::remove(std::uint64_t id) {
  switch (game::safe_remove(id)) {
    case game::RemoveResult::Removed: return RemoveOutcome::Removed;
    case game::RemoveResult::BlockedPlayerGuard:
    case game::RemoveResult::BlockedInvalidId: return RemoveOutcome::Blocked;
    default: return RemoveOutcome::Failed;
  }
}

std::optional<std::uint64_t> GameGhostWorld::find_ghost_by_idcode(std::string_view idcode) {
  if (idcode.empty()) return std::nullopt;
  for (int team = 1; team <= 8; ++team) {
    const std::string faction = "x4mp_team_" + std::to_string(team);
    if (api_.faction_state(faction) == game::FactionState::Missing) continue;
    for (const std::uint64_t id : api_.ships_of(faction)) {
      if (api_.id_code(id) != idcode) continue;
      if (api_.name(id).rfind(kNamePrefix, 0) == 0) return id;
    }
  }
  return std::nullopt;
}

FactionPresence GameGhostWorld::faction(std::string_view faction) {
  if (!teams::team_hub().factions_ready()) return FactionPresence::NotReady;  // M3-08: spawn only once the team factions are set up
  game::FactionState s = api_.faction_state(faction);
  if (s == game::FactionState::Missing) s = api_.faction_state(faction, true);  // it may have appeared since the list was read
  switch (s) {
    case game::FactionState::Present: return FactionPresence::Present;
    case game::FactionState::Missing: return FactionPresence::Missing;
    default: return FactionPresence::Unknown;
  }
}

bool GameGhostWorld::local_ship_within(std::uint64_t sector, const Vec3& pos, double radius) {
  const std::uint64_t ship = api_.local_ship();
  if (ship == 0 || api_.sector_of(ship) != sector) return false;
  const auto p = api_.pose(ship);
  if (!p) return false;
  const double dx = p->x - pos.x, dy = p->y - pos.y, dz = p->z - pos.z;
  return std::sqrt(dx * dx + dy * dy + dz * dz) <= radius;
}

}  // namespace x4mp::features::ghosts
