#include "features/selfship/selfship_feature.h"

#include <chrono>
#include <mutex>
#include <string>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/diag/diag_hub.h"
#include "features/selfship/selfship_hub.h"
#include "game/selfship_api.h"
#include "message_ids_generated.h"
#include "world_generated.h"
#include "x4mp/wire.h"

namespace x4mp::features {

namespace {
using host::Cat;
using host::Level;
namespace P = X4MP::Proto;
using selfship::Blocked;
using selfship::SeatEdge;

constexpr std::int64_t kMapRetryUs = 10'000'000;
constexpr std::int64_t kStatusEveryUs = 1'000'000;
constexpr std::int64_t kBlockedLogEveryUs = 10'000'000;
constexpr std::size_t kMaxInbox = 64;

std::int64_t steady_us() noexcept {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

const char* blocked_name(Blocked b) noexcept {
  switch (b) {
    case Blocked::NoPose: return "the ship pose is not readable";
    case Blocked::MapNotReady: return "the sector map is not ready";
    case Blocked::UnknownSector: return "the ship's sector is not in the sector map";
    case Blocked::None: break;
  }
  return "";
}

std::string json_string(const std::string& text, const char* key) {
  const auto doc = nlohmann::json::parse(text, nullptr, false);
  if (!doc.is_object()) return {};
  const auto it = doc.find(key);
  return (it != doc.end() && it->is_string()) ? it->get<std::string>() : std::string{};
}

constexpr const char* kSetaNotice = R"({"v":1,"level":"warn","text":"SETA is disabled in multiplayer"})";
}  // namespace

struct SelfShipFeature::Inbox {
  std::mutex mutex;
  std::vector<std::pair<int, std::string>> items;  // 0 = sector_map data, 1 = seta_blocked
  void push(int kind, std::string text) {
    const std::lock_guard lock(mutex);
    if (items.size() >= kMaxInbox) items.erase(items.begin());
    items.emplace_back(kind, std::move(text));
  }
  std::vector<std::pair<int, std::string>> take() {
    const std::lock_guard lock(mutex);
    return std::exchange(items, {});
  }
};

SelfShipFeature::SelfShipFeature() : inbox_(std::make_shared<Inbox>()) {}
SelfShipFeature::~SelfShipFeature() = default;

void SelfShipFeature::on_init(host::HostContext& ctx) {
  selfship::selfship_hub().reset();
  selfship::selfship_hub().set_map(&map_);  // M3-11: the avatars read index <-> macro <-> id from it
  const auto in = inbox_;
  (void)ctx.platform.subscribe_event("x4mp.sector_map", [in](std::string_view text) {
    if (text.size() <= 64 * 1024) in->push(0, std::string(text));
  });
  (void)ctx.platform.subscribe_event("x4mp.seta_blocked", [in](std::string_view text) { in->push(1, std::string(text.substr(0, 256))); });
  X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: ready (20 Hz moving / 5 Hz idle / 1 Hz hidden, SETA blocked while connected)");
}

void SelfShipFeature::on_shutdown(host::HostContext&) {
  selfship::selfship_hub().clear_link();
  selfship::selfship_hub().set_map(nullptr);
}

void SelfShipFeature::on_game_loaded(host::HostContext&) {
  // a new universe: everything about the old one is stale
  map_.reset();
  tracker_.reset();
  map_asked_ = false;
  map_logged_ready_ = false;
  block_sent_ = false;
  status_dirty_ = true;
  selfship::selfship_hub().publish({});
}

void SelfShipFeature::ask_map(host::HostContext& ctx, std::int64_t now_us) {
  if (map_.ready()) return;
  if (map_asked_ && now_us - map_asked_us_ < kMapRetryUs) return;
  map_asked_ = true;
  map_asked_us_ = now_us;
  if (!ctx.platform.raise_lua("x4mp.sector_map_collect", R"({"v":1})")) {
    X4MP_CLOG(ctx.log, Cat::Client, Level::Warn, "selfship: the Lua bridge is not available, the sector map cannot be collected yet");
  } else {
    X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: sector map requested from MD");
  }
}

void SelfShipFeature::drain_inbox(host::HostContext& ctx, std::int64_t now_us) {
  for (auto& [kind, text] : inbox_->take()) {
    if (kind == 0) {
      const std::string data = json_string(text, "data");
      if (!map_.add_message(data)) {
        X4MP_CLOG(ctx.log, Cat::Client, Level::Warn, "selfship: unreadable sector map message ignored ({} bytes)", data.size());
      } else if (map_.ready() && !map_logged_ready_) {
        map_logged_ready_ = true;
        status_dirty_ = true;
        X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: sector map ready: {} sectors ({} records dropped)", map_.size(), map_.dropped_records());
      }
    } else if (seta_.blocked_at_source(now_us)) {
      X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: SETA was stopped at the source (MD)");
      notify_seta(ctx);
    } else {
      X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: SETA was stopped at the source (MD), notification suppressed (rate limit)");
    }
  }
}

void SelfShipFeature::notify_seta(host::HostContext& ctx) { (void)ctx.platform.raise_lua("x4mp.notify", kSetaNotice); }

void SelfShipFeature::update_seta(host::HostContext& ctx, std::int64_t now_us, bool connected) {
  // Layer 1: ask MD to block SETA at the source while connected (also again after every universe: the MD flag is reset on a game load).
  if (connected != block_value_ || !block_sent_ || block_epoch_ != ctx.gates.universe_epoch) {
    block_value_ = connected;
    block_sent_ = true;
    block_epoch_ = ctx.gates.universe_epoch;
    (void)ctx.platform.raise_lua("x4mp.seta_block", connected ? R"({"v":1,"on":true})" : R"({"v":1,"on":false})");
    X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: SETA {} at the source", connected ? "blocked" : "released");
  }
  // Layer 2: the safety net.
  const auto a = seta_.update(now_us, connected, connected && ctx.game.seta_active());
  if (a.request_off) {
    (void)ctx.platform.raise_lua("x4mp.seta_off", R"({"v":1})");
    X4MP_CLOG(ctx.log, Cat::Client, Level::Warn, "selfship: SETA is active while connected: switch-off requested (detection {})", seta_.detections());
    status_dirty_ = true;
  }
  if (a.notify) notify_seta(ctx);
  if (a.switched_off) {
    X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: SETA is off again after {} ms", a.off_after_us / 1000);
    status_dirty_ = true;
  }
}

void SelfShipFeature::send_state(host::HostContext& ctx, const selfship::StateOut& out) {
  auto& hub = selfship::selfship_hub();
  std::int64_t server_now = 0;
  if (!hub.server_now(server_now)) {
    ++no_clock_;
    return;
  }
  const auto px = wire::quantize_position(out.pos.x);
  const auto py = wire::quantize_position(out.pos.y);
  const auto pz = wire::quantize_position(out.pos.z);
  const auto yaw = wire::quantize_rotation(out.rot.yaw);
  const auto pitch = wire::quantize_rotation(out.rot.pitch);
  const auto roll = wire::quantize_rotation(out.rot.roll);
  if (!px || !py || !pz || !yaw || !pitch || !roll) {
    X4MP_CLOG(ctx.log, Cat::Client, Level::Warn, "selfship: a pose value is not finite, the state was not sent");
    return;
  }
  fbb_.Clear();  // keeps the buffer: no allocation after the first state
  const auto root = P::CreatePlayerState(fbb_, ++seq_, static_cast<std::uint64_t>(server_now), hub.own_net_id(), out.sector, out.flags, *px, *py, *pz, *yaw,
                                         *pitch, *roll);
  fbb_.Finish(root);
  if (hub.send_realtime(static_cast<std::uint16_t>(P::MsgType::PlayerState), std::span<const std::uint8_t>(fbb_.GetBufferPointer(), fbb_.GetSize()))) {
    ++window_sent_;
  } else {
    ++send_failed_;
  }
}

void SelfShipFeature::publish_status(host::HostContext& ctx, std::int64_t now_us, bool force) {
  if (window_start_us_ == 0) window_start_us_ = now_us;
  if (now_us - window_start_us_ >= kStatusEveryUs) {
    rate_hz_ = static_cast<double>(window_sent_) * 1e6 / static_cast<double>(now_us - window_start_us_);
    window_start_us_ = now_us;
    window_sent_ = 0;
    if (selfship::selfship_hub().linked()) status_dirty_ = true;
  }
  if (!force && !(status_dirty_ && now_us - last_status_us_ >= kStatusEveryUs)) return;
  status_dirty_ = false;
  last_status_us_ = now_us;
  const auto& c = tracker_.counters();
  nlohmann::json j;
  j["v"] = 1;
  j["seated"] = tracker_.seated();
  j["ship"] = tracker_.ship();
  j["sector"] = selfship::selfship_hub().status().sector;
  j["sent"] = c.sent;
  j["rate_hz"] = rate_hz_;
  j["immediate"] = c.immediate;
  j["teleports"] = c.teleports;
  j["seat_edges"] = c.seat_edges;
  j["flags"] = tracker_.last_flags();
  j["hidden"] = (tracker_.last_flags() & ghost::kHidden) != 0;
  j["docked"] = (tracker_.last_flags() & ghost::kDocked) != 0;
  j["map_ready"] = map_.ready();
  j["seta_detections"] = seta_.detections();
  j["seta_requests"] = seta_.requests();
  (void)ctx.platform.raise_lua("x4mp.selfship", j.dump());
}

void SelfShipFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  auto& hub = selfship::selfship_hub();
  const std::int64_t now_us = steady_us();
  drain_inbox(ctx, now_us);
  if (!info.universe_ready || !ctx.gates.universe_ready) return;  // universe objects are only touched once the universe is ready

  ask_map(ctx, now_us);
  const bool linked = hub.linked();
  if (linked && !was_linked_) tracker_.force_resend();
  was_linked_ = linked;
  update_seta(ctx, now_us, diag_hub().connected());

  const auto read = game::read_own_ship(ctx.game);
  selfship::Observation obs;
  obs.now_us = now_us;
  obs.occupied = read.occupied;
  obs.standing_ship = read.standing_ship;
  obs.sector = read.sector;
  obs.in_highway = read.in_highway;
  obs.docked = read.docked;
  obs.pose_valid = read.pose_valid;
  obs.pos = {read.pose.x, read.pose.y, read.pose.z};
  obs.rot = {read.pose.yaw, read.pose.pitch, read.pose.roll};
  const auto tick = tracker_.update(obs, map_);

  selfship::SelfShipStatus st;
  st.seated = tick.seated;
  st.ship = tick.ship;
  st.sector = map_.index_of(read.sector);
  st.seat_edges = static_cast<std::uint32_t>(tracker_.counters().seat_edges);
  if (tick.edge == SeatEdge::SatDown) ++sit_downs_;
  st.sit_downs = sit_downs_;
  st.map_ready = map_.ready();
  st.seta_blocked = block_value_;
  hub.publish(st);

  bool force_status = false;
  if (tick.edge != SeatEdge::None) {
    ++edges_;
    force_status = true;
    const char* what = tick.edge == SeatEdge::SatDown ? "sat down" : tick.edge == SeatEdge::StoodUp ? "stood up" : "changed ship";
    X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: the player {} (ship {}, sector {})", what, tick.ship, st.sector);
  }
  if (tick.out.send) {
    if ((tick.out.flags & ghost::kTeleport) != 0) {
      X4MP_CLOG(ctx.log, Cat::Client, Level::Info, "selfship: teleport state, sector {}", tick.out.sector);
    }
    if (linked) send_state(ctx, tick.out);
  }
  if (tick.blocked != Blocked::None && (tick.blocked != last_blocked_ || now_us - last_blocked_log_us_ >= kBlockedLogEveryUs)) {
    last_blocked_ = tick.blocked;
    last_blocked_log_us_ = now_us;
    X4MP_CLOG(ctx.log, Cat::Client, Level::Warn, "selfship: no state is sent: {}", blocked_name(tick.blocked));
  } else if (tick.blocked == Blocked::None) {
    last_blocked_ = Blocked::None;
  }
  publish_status(ctx, now_us, force_status);
}

}  // namespace x4mp::features
