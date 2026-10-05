#pragma once
// features/ghosts: the client-side ghost feature (M3-10; docs/m3-plan.md 4.2, 4.8, 4.12, row M3-10).
//
// Other players' ships (PlayerShip origin) appear as `[MP] <name>` ghosts: spawned natively (SpawnObjectAtPos2 + ActivateObject(false)),
// dressed by MD (name, minimum hull, known), moved every frame from the interpolator with a 5 Hz velocity hint, hidden / shown on the
// Hidden flag, labelled `(offline)` while parked, kept in the stash across /reloadui and save loads, removed only through SafeRemove.
//
//   frames in  join feature -> ghost_hub().on_frame_message  (StringTableAdd, EntitySpawn, EntityChange, EntityDespawn, Replication)
//   per frame  on_frame: sector map (MD, once per universe), clock, GhostDriver::frame, stash save (<= 1 / s when dirty), [sync] every 5 s
//   Lua half   ui/x4mp_ghosts.lua (events x4mp.ghost_dress / ghost_velocity), MD half md/x4mp_ghosts.xml; the sector map is the selfship feature's
//
// Does nothing on the authority node (no ghosts there) and before the universe is ready.

#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "core/ghost/sync_stats.h"
#include "features/ghosts/ghost_core.h"
#include "features/ghosts/ghost_world.h"
#include "host/feature.h"

namespace x4mp::features {

class GhostFeature final : public host::IFeature {
 public:
  GhostFeature();
  ~GhostFeature() override;
  [[nodiscard]] std::string_view name() const noexcept override { return "ghosts"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_shutdown(host::HostContext& ctx) override;

  [[nodiscard]] ghosts::GhostCore* core() noexcept { return core_.get(); }

 private:
  void save_state(host::HostContext& ctx);
  void report(host::HostContext& ctx, std::int64_t now_us);

  std::unique_ptr<ghosts::GhostCore> core_;
  std::unique_ptr<game::GhostsApi> api_;
  std::unique_ptr<ghosts::GameGhostWorld> world_;
  std::unique_ptr<session::IStash> stash_;

  bool universe_seen_ = false;
  std::uint64_t last_saved_strings_version_ = 0;
  std::int64_t last_save_us_ = 0;
  std::int64_t last_report_us_ = 0;
  bool warned_no_map_ = false;
  bool warned_diag_off_ = false;      // M3-23: diag.ghosts_off was logged
  bool first_spawn_probed_ = false;   // M3-23: the knowledge probe after the first ghost spawn was asked
  bool warned_no_clock_ = false;
  ghost::PercentileWindow<512> frame_cost_us_;
  double frame_cost_max_us_ = 0;
  int last_visible_ = 0;
};

}  // namespace x4mp::features
