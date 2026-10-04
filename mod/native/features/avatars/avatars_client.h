#pragma once
// features/avatars: the real adapter of AvatarTakeover on a client node (M3-12; docs/m3-plan.md 4.3). Owned by AvatarsFeature, which feeds it the
// hub's inputs and the frame; it implements ITakeoverEnv over the GameApi, the selfship / team / avatar hubs, the Lua bridge and the stash.
//
// Bridge (ui/x4mp_bridge.lua):
//   native -> Lua  x4mp.hint      {"v":1,"id":"takeover","show":bool,"text":"Sit in the pilot seat to take over your ship"}   HUD hint (refusal fallback)
//   native -> Lua  x4mp.takeover  {"v":1,"stage":"...","net_id":N,"avatar":"<id>","hint":bool,"refusals":N,"removed":N,...}      on every stage change (tests, UI)

#include <functional>
#include <memory>
#include <optional>
#include <vector>

#include "features/avatars/avatar_hub.h"
#include "features/avatars/avatar_plan.h"
#include "host/feature.h"

namespace x4mp::features::avatars {

class ClientTakeover {
 public:
  ClientTakeover();
  ~ClientTakeover();
  ClientTakeover(const ClientTakeover&) = delete;
  ClientTakeover& operator=(const ClientTakeover&) = delete;

  // `team_candidates` lists the ships of the x4mp_team_* factions (nullopt = the enumeration is not available).
  void init(host::HostContext& ctx, std::function<std::optional<std::vector<Candidate>>()> team_candidates);
  void apply(const HubInputs& inputs, const AvatarSettings* settings);  // the hub's queued inputs (EntitySpawn avatars, session end) and new settings
  void frame(host::HostContext& ctx, double now_s);
  void game_loaded(host::HostContext& ctx);
  void shutdown(host::HostContext& ctx);

  [[nodiscard]] bool done() const noexcept;
  [[nodiscard]] const char* stage_name() const noexcept;

 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};

}  // namespace x4mp::features::avatars
