#pragma once
// features/avatars: the authority's avatars in game (M3-11; docs/m3-plan.md 4.3, section 6.3 row M3-11).
//
// The feature is only plumbing around AvatarDirector (avatar_director.h): it implements IAvatarEnv over the real GameApi, the platform (Lua
// bridge, stash), the team hub, the selfship hub (sector map, server clock) and the avatar hub (net ids, string table and Control-lane send
// that the authority flow owns). Everything runs in on_frame on the main thread; the Lua verb callback only copies text into an inbox.
//
// Bridge (ui/x4mp_avatars.lua, md/x4mp_avatars.xml):
//   native -> Lua  x4mp.avatars_safepos {"v":1,"seq","sector":"<id>","x","y","z","radius"}   MD get_safe_pos around the wanted spot
//   native -> Lua  x4mp.avatars_dress   {"v":1,"seq","id":"<id>","name","min_hull","macro","loadout","basic":bool}   name, min hull, loadout, radar
//   native -> Lua  x4mp.avatars_vel     {"v":1,"h":[["<id>",vx,vy,vz],...]}   MD set_object_velocity (5 Hz, spike S13.2 mode c)
//   Lua -> native  x4mp.avatars_md      {"v":1,"data":"P;<seq>;<ok 0|1>;x;y;z" | "D;<seq>;<ok 0|1>;<detail>"}
// Ids go as decimal strings (UniverseID is 64 bit). Records persist in the stash (key avatars.records) and in <config dir>/avatar-records.txt.

#include <memory>
#include <string>

#include "host/feature.h"

namespace x4mp::features {

class AvatarsFeature final : public host::IFeature {
 public:
  AvatarsFeature();
  ~AvatarsFeature() override;
  [[nodiscard]] std::string_view name() const noexcept override { return "avatars"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace x4mp::features
