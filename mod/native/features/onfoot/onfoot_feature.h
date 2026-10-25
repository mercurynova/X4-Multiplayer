#pragma once
// features/onfoot: the client half of M3b on-foot presence (ADR-046; docs/research/on-foot-presence.md 3.1-3.3).
//
//   md/x4mp_onfoot.xml sees the player enter a room and pushes the room key (container entity, station match key, room macro, anchor in
//   decimetres, kind, roomtype) -> ui/x4mp_onfoot.lua -> bridge verb x4mp.onfoot_room -> inbox here -> on_frame.
//   Native adds what only it can read (container-local position and yaw via GetPositionalOffset), rate-limits with OnFootTracker
//   (10 Hz moving / 1 Hz idle / at once on a room change) and sends OnFootState (0x0302) on the Realtime lane.
//   The relay fans it out to the peers in the same container; their OnFootState / PresenceRoster frames come back through the join
//   feature into onfoot_hub(). The actor work for remote players is MD's job (W4), driven from the hub's state.

#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>

#include <flatbuffers/flatbuffers.h>

#include "features/onfoot/onfoot_hub.h"
#include "features/onfoot/onfoot_tracker.h"
#include "host/feature.h"

namespace x4mp::features {

class OnFootFeature final : public host::IFeature {
 public:
  OnFootFeature();
  ~OnFootFeature() override;

  [[nodiscard]] std::string_view name() const noexcept override { return "onfoot"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_game_loaded(host::HostContext& ctx) override;

 private:
  struct Inbox;
  void drain_inbox(host::HostContext& ctx);
  void send_state(host::HostContext& ctx, const onfoot::OnFootTracker::Step& step, const onfoot::RoomContext& room,
                  float cx, float cy, float cz, float yaw, float pitch);

  std::shared_ptr<Inbox> inbox_;
  onfoot::OnFootTracker tracker_;
  flatbuffers::FlatBufferBuilder fbb_{256};
  std::uint32_t seq_ = 0;
  bool was_linked_ = false;
  std::uint64_t no_clock_ = 0;
  std::uint64_t send_failed_ = 0;
  std::uint64_t bad_reports_ = 0;
  std::uint64_t reports_ = 0;
  std::uint64_t sent_states_ = 0;
  std::uint64_t sample_failed_ = 0;
  std::int64_t last_log_us_ = 0;
};

}  // namespace x4mp::features
