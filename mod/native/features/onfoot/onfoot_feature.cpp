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

#include "features/chat/chat_json.h"
#include "features/onfoot/onfoot_presence.h"

namespace x4mp::features {

namespace P = X4MP::Proto;
using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 16;
constexpr std::size_t kMaxReportBytes = 512;
// The room catalogue lists every room of the container (27 on a small factory, hundreds on a shipyard), so it needs its own cap: a
// station with 400 rooms of 30 characters is 12 KB, and dropping it would silently disable Tier 2 placement.
constexpr std::size_t kMaxRoomsBytes = 64 * 1024;
constexpr float kPi = 3.14159265358979323846f;

std::int64_t steady_us() {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
}  // namespace

struct OnFootFeature::Inbox {
  explicit Inbox(std::size_t max_bytes = kMaxReportBytes) : max_bytes_(max_bytes) {}
  std::mutex m;
  std::vector<std::string> q;
  std::size_t max_bytes_ = kMaxReportBytes;
  void push(std::string_view text) {
    if (text.size() > max_bytes_) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(text);
  }
  std::vector<std::string> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

OnFootFeature::OnFootFeature()
    : inbox_(std::make_shared<Inbox>()), replies_(std::make_shared<Inbox>()), talk_(std::make_shared<Inbox>()),
      rooms_(std::make_shared<Inbox>(kMaxRoomsBytes)) {}
OnFootFeature::~OnFootFeature() = default;

void OnFootFeature::on_init(host::HostContext& ctx) {
  auto inbox = inbox_;
  // The MD room report, forwarded by ui/x4mp_onfoot.lua. May arrive on any thread: copy only.
  const bool ok = ctx.platform.subscribe_event("x4mp.onfoot_room", [inbox](std::string_view text) { inbox->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: bridge verb x4mp.onfoot_room {}", ok ? "subscribed" : "not available");

  auto replies = replies_;
  // MD's answers to the actor commands: "spawn;<pid>;<actor id>" | "gone;<pid>".
  const bool ok2 = ctx.platform.subscribe_event("x4mp.onfoot_actor", [replies](std::string_view text) { replies->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: bridge verb x4mp.onfoot_actor {}", ok2 ? "subscribed" : "not available");

  auto talk = talk_;
  // The Talk choice MD added to the conversation: {"v":1,"to":<pid>,"kind":"wave"}.
  const bool ok3 = ctx.platform.subscribe_event("x4mp.onfoot_talk", [talk](std::string_view text) { talk->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: bridge verb x4mp.onfoot_talk {}", ok3 ? "subscribed" : "not available");

  auto rooms = rooms_;
  // The room macro ids of the current container: {"v":1,"data":"id;id;..."} (M3c).
  const bool ok4 = ctx.platform.subscribe_event("x4mp.onfoot_rooms", [rooms](std::string_view text) { rooms->push(text); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: bridge verb x4mp.onfoot_rooms {}", ok4 ? "subscribed" : "not available");
  install_actor_callbacks(ctx);
}

void OnFootFeature::install_actor_callbacks(host::HostContext& ctx) {
  // The registry is a process-wide singleton and outlives no host object here, so it gets pointers, not captures of ctx.
  platform_ = &ctx.platform;
  game_ = &ctx.game;
  log_ = &ctx.log;
  onfoot::ActorCallbacks cb;
  cb.raise_lua = [this](std::string_view json) {
    if (platform_ == nullptr) return;
    if (!platform_->raise_lua("x4mp.onfoot_actors", json)) ++actor_raise_failed_;
  };
  cb.place = [this](std::uint64_t actor, float x, float y, float z, float yaw) {
    if (game_ == nullptr) return;
    // S10.4: the per-frame override is what makes the avatar follow; MD cannot animate a teleported actor, so v1 keeps the idle pose.
    game_->set_positional_offset(actor, game::PosRotPod{x, y, z, yaw, 0.0f, 0.0f});
  };
  cb.name = [](std::uint16_t pid) -> std::string {
    const auto* row = chat::chat_hub().roster().find(pid);
    return row != nullptr ? row->name : std::string();
  };
  cb.room_id = [this](std::uint32_t macro_ref) -> std::string {
    // M3c: an avatar in another room needs that room's macro id, because MD matches $Rm.macro.id and cannot hash. The catalogue
    // comes from our own MD (drain_rooms); an unknown hash returns "" and the registry sends no spawn for that player.
    const auto it = room_ids_.find(macro_ref);
    return it != room_ids_.end() ? it->second : std::string();
  };
  cb.diag = [this](std::uint16_t pid, float lag_m, float speed_mps, std::int64_t age_us, float dist_m) {
    // One line per avatar per 5 s. A session reads `lag` to check the M3b "error < 0.5 m" criterion: it is how far behind the
    // newest sample the actor is drawn, so a standing avatar reads 0.00 m and a walking one about speed x 90 ms.
    // `dist` is how far the local player stands from that avatar in room space: a few metres means they really share the room.
    if (log_ == nullptr) return;
    if (dist_m < 0.0f) {
      X4MP_CLOG(*log_, Cat::Md, Level::Info, "onfoot: avatar pid={} lag={:.2f}m speed={:.2f}m/s age={}ms dist=n/a", pid,
                static_cast<double>(lag_m), static_cast<double>(speed_mps), age_us / 1000);
      return;
    }
    X4MP_CLOG(*log_, Cat::Md, Level::Info, "onfoot: avatar pid={} lag={:.2f}m speed={:.2f}m/s age={}ms dist={:.2f}m", pid,
              static_cast<double>(lag_m), static_cast<double>(speed_mps), age_us / 1000, static_cast<double>(dist_m));
  };
  onfoot::onfoot_actors().set_callbacks(std::move(cb));
}

void OnFootFeature::on_game_loaded(host::HostContext& ctx) {
  // A load puts the player somewhere else and MD re-reports the room; drop the stale context until it does.
  onfoot::onfoot_hub().set_room_context({});
  onfoot::onfoot_actors().clear();
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

void OnFootFeature::drain_replies(host::HostContext& /*ctx*/) {
  auto& registry = onfoot::onfoot_actors();
  for (const auto& raw : replies_->take()) {
    // {"v":1,"data":"spawn;<pid>;<actor id>"} or {"v":1,"data":"gone;<pid>"}
    std::string text;
    try {
      const auto j = nlohmann::json::parse(raw);
      text = j.value("data", std::string{});
    } catch (const std::exception&) {
      ++bad_reports_;
      continue;
    }
    if (!text.empty()) registry.on_reply(text);
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

  // cx/cy/cz are ROOM-local metres at EYE height (both add_actor_to_room and SetPositionalOffset take an offset relative to
  // the actor's parent, which is the room), so they go on the wire unchanged: quantize_position is 1/64 m, room_x/y/z 1/1024 m.
  const auto qx = wire::quantize_position(cx);
  const auto qy = wire::quantize_position(cy);
  const auto qz = wire::quantize_position(cz);
  const auto qyaw = wire::quantize_rotation(yaw);
  if (!qx || !qy || !qz || !qyaw) {
    ++send_failed_;
    return;
  }
  const auto room_x = static_cast<std::int32_t>(std::lround(cx * 1024.0f));
  const auto room_y = static_cast<std::int32_t>(std::lround(cy * 1024.0f));
  const auto room_z = static_cast<std::int32_t>(std::lround(cz * 1024.0f));
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

void OnFootFeature::drain_talk(host::HostContext& ctx) {
  auto& hub = onfoot::onfoot_hub();
  for (const auto& raw : talk_->take()) {
    // {"v":1,"to":<pid>,"kind":"wave"} — the Talk choice MD added to the conversation, forwarded by ui/x4mp_onfoot.lua.
    std::uint16_t to = 0;
    std::string kind;
    try {
      const auto j = nlohmann::json::parse(raw);
      to = static_cast<std::uint16_t>(j.value("to", 0));
      kind = j.value("kind", std::string{});
    } catch (const std::exception&) {
      ++bad_talk_;
      continue;
    }
    if (kind != "wave") {
      ++bad_talk_;
      X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "onfoot: unknown interaction '{}', ignored", kind);
      continue;
    }
    // Control lane, like the appearance: the relay forwards it to the target only.
    if (hub.send_interaction(to, static_cast<std::uint8_t>(P::InteractionKind::Wave))) {
      ++waves_sent_;
    } else {
      ++send_failed_;
    }
  }
}

void OnFootFeature::drain_rooms(host::HostContext& ctx) {
  for (const auto& raw : rooms_->take()) {
    // {"v":1,"data":"room_arg_bar_01_macro;room_arg_corridor_01_macro;..."} — every room of the container the player is in, pushed by
    // our own MD because the SDK has no room accessor and MD cannot hash. Ids are stable per macro, so the table never needs to be
    // cleared: the same id always hashes to the same key.
    std::string data;
    try {
      const auto j = nlohmann::json::parse(raw);
      data = j.value("data", std::string{});
    } catch (const std::exception&) {
      ++bad_reports_;
      continue;
    }
    std::size_t added = 0;
    std::size_t total = 0;
    std::size_t pos = 0;
    while (pos < data.size()) {
      const auto end = data.find(';', pos);
      const std::string_view id(data.data() + pos, (end == std::string::npos ? data.size() : end) - pos);
      pos = end == std::string::npos ? data.size() : end + 1;
      if (id.empty()) continue;
      ++total;
      const auto hash = onfoot::fnv1a32(id);
      if (hash == 0) continue;
      if (room_ids_.emplace(hash, std::string(id)).second) ++added;
    }
    if (total > 0) {
      ++rooms_pushed_;
      X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "onfoot: room catalogue {} ids, {} new, {} known", total, added, room_ids_.size());
    }
  }
}

void OnFootFeature::raise_interactions() {
  if (platform_ == nullptr) return;
  for (const auto& it : onfoot::onfoot_hub().take_interactions()) {
    // A wave to this player: Lua plays it on that player's actor and says who it came from.
    nlohmann::json j;
    j["v"] = 1;
    j["from"] = it.from;
    j["kind"] = it.kind == static_cast<std::uint8_t>(P::InteractionKind::Wave) ? "wave" : "other";
    const auto* row = chat::chat_hub().roster().find(it.from);
    j["name"] = row != nullptr ? row->name : std::string();
    if (!platform_->raise_lua("x4mp.onfoot_talk_in", j.dump())) ++talk_raise_failed_;
  }
}

void OnFootFeature::raise_presence() {
  if (platform_ == nullptr) return;
  const auto& hub = onfoot::onfoot_hub();
  const auto& roster = hub.roster();
  const auto& room = hub.room();
  const auto container = room.valid ? room.container_net_id : 0u;

  // A compact text of what the HUD would draw. The roster is a handful of rows, so rebuilding it every frame is cheap; comparing it is
  // what keeps the bridge quiet.
  const auto sig = onfoot::presence_signature(roster, container, room.valid ? room.mode : onfoot::Mode::None);
  const auto now_us = steady_us();
  if (sig == presence_sig_ && presence_raised_) return;
  // At most twice a second: the relay rebuilds the roster on any change, and a peer crossing a doorway is not worth a redraw each time.
  if (presence_raised_ && now_us - last_presence_us_ < 500'000) return;
  presence_sig_ = sig;
  presence_raised_ = true;
  last_presence_us_ = now_us;

  nlohmann::json j;
  j["v"] = 1;
  j["self"] = hub.own_player_id();
  j["container"] = container;
  j["mode"] = static_cast<int>(room.valid ? room.mode : onfoot::Mode::None);
  auto entries = nlohmann::json::array();
  for (const auto& row : roster) {
    nlohmann::json e;
    e["id"] = row.player_id;
    e["mode"] = static_cast<int>(row.mode);
    // "here" is the only place we can put a peer on the map: the roster carries a container id and nothing else, and the client has no
    // name for a container it is not in (the server's station mirror is M4). So the HUD says "here" or says nothing about the place.
    e["here"] = onfoot::presence_here(row, container);
    const auto* chat_row = chat::chat_hub().roster().find(row.player_id);
    e["name"] = chat_row != nullptr ? chat_row->name : std::string();
    entries.push_back(std::move(e));
  }
  j["entries"] = std::move(entries);
  if (!platform_->raise_lua("x4mp.presence", j.dump())) ++presence_raise_failed_;
}

void OnFootFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  auto& hub = onfoot::onfoot_hub();
  drain_inbox(ctx);
  drain_replies(ctx);
  drain_rooms(ctx);
  drain_talk(ctx);
  raise_interactions();
  raise_presence();

  if (!info.universe_ready || !ctx.gates.universe_ready) return;

  const bool linked = hub.linked();
  if (linked && !was_linked_) tracker_.force_resend();
  was_linked_ = linked;

  const auto now_us = steady_us();
  if (now_us - last_log_us_ >= 30'000'000) {
    last_log_us_ = now_us;
    const auto& c = hub.counters();
    const auto& a = onfoot::onfoot_actors().counters();
    if (c.states_in || c.rosters || reports_ || send_failed_ || bad_reports_ || a.spawns || a.places ||
        a.bad_replies || a.no_room) {
      X4MP_CLOG(ctx.log, Cat::Sess, Level::Info,
                "onfoot: linked={} reports={} bad={} out={} failed={} no_clock={} in={} roster={} remotes={} actors={}/{} spawn={} "
                "despawn={} place={} answers={} bad_answers={} rooms={} no_room={}",
                linked, reports_, bad_reports_, sent_states_, send_failed_, no_clock_, c.states_in, c.rosters, hub.remotes().size(),
                onfoot::onfoot_actors().live(), onfoot::onfoot_actors().tracked(), a.spawns, a.despawns, a.places, a.replies,
                a.bad_replies, room_ids_.size(), a.no_room);
    }
  }
  if (!linked) {
    onfoot::onfoot_actors().clear();  // no session: the avatars must not outlive it, and MD has to drop whatever it still has
    return;
  }

  // Remote avatars. The registry's interpolator compares the remote's sample_time_us with `now`, so the clock has to come from the
  // same source as the stream; without a server clock there is nothing to interpolate against, so the avatars are removed instead.
  std::int64_t server_now = 0;
  if (hub.server_now(server_now)) {
    onfoot::onfoot_actors().update(hub.room(), hub.remotes(), hub.appearances(), server_now);
  } else {
    onfoot::onfoot_actors().clear();
  }

  const auto& room = hub.room();
  if (!room.valid || room.mode == onfoot::Mode::None) {
    // Still report the leave state, so the peers drop the avatar instead of freezing it.
    onfoot::onfoot_actors().set_self(false, 0.0, 0.0, 0.0);
    onfoot::OnFootTracker::Sample sample;
    sample.ctx = room;
    const auto step = tracker_.update(sample, now_us);
    if (step.send) send_state(ctx, step, room, 0, 0, 0, 0, 0);
    return;
  }

  // The walking position comes from the ACTOR (GetPlayerID), read relative to its direct parent (`space == 0`, the room). Two
  // measured reasons: GetPlayerObjectID returns the avatar entity and answers (0,0,0) on foot, and the room key stores the anchor in
  // decimetres as int16, so subtracting it breaks as soon as a room origin exceeds 3276.7 m (a shipyard interior at 4.6 km came out
  // 1.4 km too far on z). The parent-relative pose needs no anchor at all and is already at eye height.
  const auto actor = ctx.game.player_id();
  const auto pos = ctx.game.positional_offset(actor, 0);
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
  // The local player in room space, the same frame the states use: the diag line measures the distance to each avatar from here.
  onfoot::onfoot_actors().set_self(true, static_cast<double>(pos->x), static_cast<double>(pos->y), static_cast<double>(pos->z));
  if (step.send) send_state(ctx, step, room, pos->x, pos->y, pos->z, sample.yaw, sample.pitch);
}

}  // namespace x4mp::features
