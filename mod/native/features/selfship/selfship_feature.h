#pragma once
// features/selfship: the player's own ship on this node (M3-09; docs/m3-plan.md 4.4, 4.5, 6.3 row M3-09).
//
//   * every frame (once the universe is ready) the adapter game/selfship_api reads seat / ship / sector / highway / dock / pose and the
//     OwnShipTracker turns it into seat edges and PlayerState samples (20 Hz moving, 5 Hz idle, 1 Hz hidden, immediate on a sector
//     change / teleport / flag change); the frame is sent on the Realtime lane through the join feature's link (UDP when active);
//   * the sector index comes from a node-local GalaxyMap built from md/x4mp_galaxy.xml (control "map" -> ui/x4mp_authority.lua ->
//     verb x4mp.sector_map); it is asked for once the universe is ready and again every 10 s until it is complete;
//   * the seat state goes to the SelfShipHub; the authority flow uses it for the self-spawn (replaces the 6-ask retry of M2-09);
//   * SETA: the guard asks Lua/MD to block it while connected (x4mp.seta_block) and switches it off within 1 s when it is on anyway
//     (x4mp.seta_off), with the notification "SETA is disabled in multiplayer".
//
// Topics raised to Lua (all optional for the UI):  x4mp.sector_map_collect {"v":1}   x4mp.seta_block {"v":1,"on":bool}
//   x4mp.seta_off {"v":1}   x4mp.notify (SETA)   x4mp.selfship {"v":1,"seated","ship","sector","sent","rate_hz","immediate","teleports",
//   "seat_edges","map_ready","seta_detections"} (at most 1 Hz, immediately on a seat edge; the hostsim tests and the HUD read it).
// Verbs from Lua: x4mp.sector_map {"v":1,"data":"S;..."|"E;n"}, x4mp.seta_blocked {"v":1} (MD stopped SETA at the source).
// All Lua callbacks only copy text into an inbox that on_frame drains.

#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include <flatbuffers/flatbuffers.h>

#include "features/selfship/galaxy_map.h"
#include "features/selfship/own_ship_tracker.h"
#include "features/selfship/seta_guard.h"
#include "host/feature.h"

namespace x4mp::features {

class SelfShipFeature final : public host::IFeature {
 public:
  SelfShipFeature();
  ~SelfShipFeature() override;
  [[nodiscard]] std::string_view name() const noexcept override { return "selfship"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  struct Inbox;
  void drain_inbox(host::HostContext& ctx, std::int64_t now_us);
  void ask_map(host::HostContext& ctx, std::int64_t now_us);
  void update_seta(host::HostContext& ctx, std::int64_t now_us, bool connected);
  void send_state(host::HostContext& ctx, const selfship::StateOut& out);
  void publish_status(host::HostContext& ctx, std::int64_t now_us, bool force);
  void notify_seta(host::HostContext& ctx);

  std::shared_ptr<Inbox> inbox_;
  selfship::GalaxyMap map_;
  selfship::OwnShipTracker tracker_;
  selfship::SetaGuard seta_;
  flatbuffers::FlatBufferBuilder fbb_{256};
  std::uint32_t seq_ = 0;

  bool map_asked_ = false;
  std::int64_t map_asked_us_ = 0;
  bool map_logged_ready_ = false;
  bool was_linked_ = false;
  bool was_held_ = false;  // M3-12: the takeover held the PlayerState stream last frame
  bool block_sent_ = false;
  bool block_value_ = false;
  std::uint64_t block_epoch_ = 0;
  std::uint32_t sit_downs_ = 0;
  std::uint32_t edges_ = 0;

  // status / rate window
  std::int64_t window_start_us_ = 0;
  std::uint64_t window_sent_ = 0;
  double rate_hz_ = 0;
  std::int64_t last_status_us_ = 0;
  bool status_dirty_ = true;
  std::uint64_t no_clock_ = 0;
  std::uint64_t send_failed_ = 0;
  std::int64_t last_blocked_log_us_ = 0;
  selfship::Blocked last_blocked_ = selfship::Blocked::None;
};

}  // namespace x4mp::features
