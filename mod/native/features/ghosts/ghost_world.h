#pragma once
// features/ghosts: IGhostWorld over the real game (M3-10): game/ghosts_api for the native calls, the loader's Lua bridge for the two
// things only MD can do (dress: name + minimum hull + known; velocity hint), the sector map for index -> local sector id.
//
//   native -> Lua  x4mp.ghost_dress    {"v":1,"id":"<UniverseID>","name":"[MP] Pia","minhull":100}   -> MD control "dress"
//                  x4mp.ghost_velocity "<id>,<vx>,<vy>,<vz>;<id>,..."  (m/s, one batch per frame, 5 Hz per ghost)        -> MD control "velocity"
//   The sector index -> local sector id comes from the selfship feature's GalaxyMap (selfship_hub().map()).
//   (ui/x4mp_ghosts.lua is the Lua half, md/x4mp_ghosts.xml the MD half.)

#include <string>

#include "features/ghosts/ghost_driver.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "features/teams/team_hub.h"
#include "game/ghosts_api.h"
#include "host/platform.h"

namespace x4mp::features::ghosts {

class GameGhostWorld final : public IGhostWorld {
 public:
  GameGhostWorld(game::GhostsApi& api, host::IPlatform& platform) : api_(api), platform_(platform) {}

  std::uint64_t sector_id(std::uint16_t index) override {
    const auto* map = selfship::selfship_hub().map();
    return map ? map->universe_id_of(index) : 0;
  }
  std::uint64_t spawn(std::string_view macro, std::uint64_t sector, const Pose& pose, std::string_view owner) override;
  void make_inert(std::uint64_t id) override { api_.make_inert(id); }
  void place(std::uint64_t id, std::uint64_t sector, const Pose& pose) override;
  void dress(std::uint64_t id, std::string_view label, int min_hull_percent) override;
  void hint_velocities(std::span<const VelocityHint> hints) override;
  void set_owner(std::uint64_t id, std::string_view owner) override;
  bool valid(std::uint64_t id) override { return api_.valid(id); }
  bool wrecked(std::uint64_t id) override { return api_.wrecked(id); }
  RemoveOutcome remove(std::uint64_t id) override;
  std::string id_code(std::uint64_t id) override { return api_.id_code(id); }
  std::string name(std::uint64_t id) override { return api_.name(id); }
  std::optional<std::uint64_t> find_ghost_by_idcode(std::string_view idcode) override;
  FactionPresence faction(std::string_view faction) override;
  bool local_ship_within(std::uint64_t sector, const Vec3& pos, double radius) override;

  [[nodiscard]] static game::GhostPose to_game(const Pose& p) noexcept;

 private:
  game::GhostsApi& api_;
  host::IPlatform& platform_;
  std::string velocity_buf_;
};

}  // namespace x4mp::features::ghosts
