#include "features/onfoot/onfoot_feature.h"

#include <algorithm>
#include <cmath>
#include <mutex>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

#include "message_ids_generated.h"
#include "world_generated.h"
#include "x4mp/wire.h"

namespace x4mp::features {

namespace P = X4MP::Proto;
using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 16;
constexpr std::size_t kMaxReportBytes = 512;
constexpr float kPi = 3.14159265358979323846f;

std::int64_t steady_us() {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
}  // namespace

struct OnFootFeature::Inbox {
  std::mutex m;
  std::vector<std::string> q;
  void push(std::string_view text) {
    if (text.size() > kMaxReportBytes) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(text);
  }
  std::vector<std::string> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

OnFootFeature::OnFootFeature() : inbox_(std::make_shared<Inbox>()) {}
OnFootFeature::~OnFootFeature() = default;

void OnFootFeature::on_init(host::HostContext& ctx) {
  auto inbox = inbox_;
  // The MD room report, forwarded by ui/x4mp_onfoot.lua. May arrive on any thread: copy only.
  const bool ok = ctx.platform.subscribe_event("x4mp.onfoot_room", [inbox](std::string_view text) { inbox->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: bridge verb x4mp.onfoot_room {}", ok ? "subscribed" : "not available");
}

void OnFootFeature::on_game_loaded(host::HostContext& ctx) {
  // A load puts the player somewhere else and MD re-reports the room; drop the stale context until it does.
  onfoot::onfoot_hub().set_room_context({});
  tracker_.reset();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: game loaded, waiting for the room report");
}

void OnFootFeature::drain_inbox(host::HostContext& ctx) {
  auto& hub = onfoot::onfoot_hub();
  for (const auto& raw : inbox_->take()) {
    // {"v":1,"data":"mode=1;cont=...;skey=...;rmacro=...;ax=..;ay=..;az=..;kind=..;roomtype=.."}
    std::string text;
    try {
      const auto j = nlohmann::json::parse(raw);
      text = j.value("data", std::string{});
    } catch (const std::exception&) {
      ++bad_reports_;
      continue;
    }
    std::string why;
    const auto parsed = onfoot::parse_room_context(text, &why);
    if (!parsed) {
      ++bad_reports_;
      X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "onfoot: bad room report (field {}), ignored: {}", why, text);
      continue;
    }
    ++reports_;
    const auto& before = hub.room();
    const bool changed = !(before == *parsed);
    hub.set_room_context(*parsed);
    if (changed) {
      X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
                "onfoot: room mode={} container={} net={:#x} room={:#x} anchor_dm=({},{},{}) kind={} roomtype={}",
                static_cast<int>(parsed->mode), parsed->container, parsed->container_net_id, parsed->room_macro_ref, parsed->anchor_x,
                parsed->anchor_y, parsed->anchor_z, static_cast<int>(parsed->kind), static_cast<int>(parsed->roomtype));
    }
  }
}

void OnFootFeature::send_state(host::HostContext& /*ctx*/, const onfoot::OnFootTracker::Step& step, const onfoot::RoomContext& room,
                               float cx, float cy, float cz, float yaw, float pitch) {
  auto& hub = onfoot::onfoot_hub();
  std::int64_t server_now = 0;
  if (!hub.server_now(server_now)) {
    ++no_clock_;
    return;
  }

  // cx/cy/cz are container-local metres at 1/64 m (wire::quantize_position). The room-local position (1/1024 m) is the same point
  // relative to the room origin MD reported, in decimetres.
  const auto qx = wire::quantize_position(cx);
  const auto qy = wire::quantize_position(cy);
  const auto qz = wire::quantize_position(cz);
  const auto qyaw = wire::quantize_rotation(yaw);
  if (!qx || !qy || !qz || !qyaw) {
    ++send_failed_;
    return;
  }
  const auto room_x = static_cast<std::int32_t>(std::lround((cx - static_cast<float>(room.anchor_x) / 10.0f) * 1024.0f));
  const auto room_y = static_cast<std::int32_t>(std::lround((cy - static_cast<float>(room.anchor_y) / 10.0f) * 1024.0f));
  const auto room_z = static_cast<std::int32_t>(std::lround((cz - static_cast<float>(room.anchor_z) / 10.0f) * 1024.0f));
  const auto look = static_cast<std::uint8_t>(std::clamp<std::int32_t>(
      static_cast<std::int32_t>(std::lround((pitch + kPi / 2.0f) / kPi * 255.0f)), 0, 255));

  std::uint8_t flags = 0;
  if (step.teleport) flags |= 1u << 0;
  if (step.keyframe) flags |= 1u << 1;

  P::RoomKey key(room.room_macro_ref, room.anchor_x, room.anchor_y, room.anchor_z, room.path_hash,
                 static_cast<P::RoomKind>(room.kind), room.roomtype);

  fbb_.Clear();
  const auto root = P::CreateOnFootState(fbb_, hub.own_player_id(), ++seq_, static_cast<std::uint64_t>(server_now),
                                         static_cast<P::OnFootMode>(room.mode), room.container_net_id, room.outer_net_id, &key,
                                         room_x, room_y, room_z, *qx, *qy, *qz, *qyaw, look,
                                         static_cast<P::OnFootAnim>(step.anim), 0, flags);
  fbb_.Finish(root);
  if (hub.send_realtime(static_cast<std::uint16_t>(P::MsgType::OnFootState),
                        std::span<const std::uint8_t>(fbb_.GetBufferPointer(), fbb_.GetSize()))) {
    ++sent_states_;
  } else {
    ++send_failed_;
  }
}

void OnFootFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  auto& hub = onfoot::onfoot_hub();
  drain_inbox(ctx);

  if (!info.universe_ready || !ctx.gates.universe_ready) return;

  const bool linked = hub.linked();
  if (linked && !was_linked_) tracker_.force_resend();
  was_linked_ = linked;

  const auto now_us = steady_us();
  if (now_us - last_log_us_ >= 30'000'000) {
    last_log_us_ = now_us;
    const auto& c = hub.counters();
    if (c.states_in || c.rosters || reports_ || send_failed_ || bad_reports_) {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info,
                "onfoot: linked={} reports={} bad={} out={} failed={} no_clock={} in={} roster={} remotes={}", linked, reports_,
                bad_reports_, sent_states_, send_failed_, no_clock_, c.states_in, c.rosters, hub.remotes().size());
    }
  }
  if (!linked) return;

  const auto& room = hub.room();
  if (!room.valid || room.mode == onfoot::Mode::None) {
    // Still report the leave state, so the peers drop the avatar instead of freezing it.
    onfoot::OnFootTracker::Sample sample;
    sample.ctx = room;
    const auto step = tracker_.update(sample, now_us);
    if (step.send) send_state(ctx, step, room, 0, 0, 0, 0, 0);
    return;
  }

  const auto player = ctx.game.player_object();
  // MD may omit the container (it is only needed to read the position); the game knows it anyway.
  const auto container = room.container != 0 ? room.container : ctx.game.player_container();
  const auto pos = ctx.game.positional_offset(player, container);
  if (!pos) {
    // The interior may have just been torn down; MD reports the new room on the next room change.
    ++sample_failed_;
    return;
  }
  onfoot::OnFootTracker::Sample sample;
  sample.ctx = room;
  sample.cx = pos->x;
  sample.cy = pos->y;
  sample.cz = pos->z;
  sample.yaw = pos->yaw;
  sample.pitch = pos->pitch;
  const auto step = tracker_.update(sample, now_us);
  if (step.send) send_state(ctx, step, room, sample.cx, sample.cy, sample.cz, sample.yaw, sample.pitch);
}

}  // namespace x4mp::features
