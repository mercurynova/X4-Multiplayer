#include "features/authority/authority_flow.h"

#include <algorithm>
#include <cstring>
#include <fstream>

#include <nlohmann/json.hpp>

#include "core/authority/checkpoint_messages.h"
#include "core/authority/entity_spawn.h"
#include "core/crypto/crypto.h"
#include "features/authority/capture_set_wire.h"
#include "features/avatars/avatar_hub.h"
#include "features/chat/chat_json.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "features/settlement/settlement_feature.h"
#include "features/stats/frame_timing.h"
#include "session_generated.h"
#include "world_generated.h"
#include "features/janitor/janitor_feature.h"
#include "common_generated.h"
#include "message_ids_generated.h"
#include "events_generated.h"
#include "features/events/event_wire.h"
#include "features/economy/economy_hub.h"
#include "features/statics/statics_feature.h"
#include "features/statics/statics_hub.h"
#include "features/teams/team_hub.h"
#include "features/onfoot/onfoot_types.h"
#include "x4mp/wire.h"
#include "game/safe_remove.h"

namespace x4mp::features::auth {

namespace {
using host::Cat;
using host::Level;
namespace fs = std::filesystem;
namespace P = X4MP::Proto;
using std::chrono::milliseconds;
using std::chrono::seconds;
constexpr std::uint16_t T(P::MsgType t) { return static_cast<std::uint16_t>(t); }

constexpr auto kCollectTimeout = seconds(20);
constexpr auto kShipGrace = milliseconds(1500);  // after the end marker: how long to wait for the ship record
constexpr auto kShipAnswerTimeout = seconds(5);  // MD answers within a frame or two; no answer = counted like "no ship"
constexpr int kAskBackoffMin = 20;               // frames: first re-ask when MD says "no ship" while the game says the player sits
constexpr int kAskBackoffMax = 600;
constexpr auto kSaveReplyTimeout = seconds(20);
constexpr auto kFileTimeout = seconds(180);
constexpr auto kFilePollEvery = milliseconds(250);
constexpr std::size_t kKeepCheckpointSaves = 2;
constexpr std::size_t kMaxInbox = 64;

std::string hex12(const std::vector<std::uint8_t>& v) {
  return crypto::to_hex(std::span<const std::uint8_t>(v).first(std::min<std::size_t>(6, v.size())));
}

std::string json_string(const std::string& text, const char* key) {
  const auto doc = nlohmann::json::parse(text, nullptr, false);
  if (!doc.is_object()) return {};
  const auto it = doc.find(key);
  return (it != doc.end() && it->is_string()) ? it->get<std::string>() : std::string{};
}

// M5-21: the team a player belongs to, as the session's own TeamTable lists it (0 = unknown). The mod never asks a client
// which team it is on: a capture or a build must not be able to claim another team's faction.
std::uint16_t team_of_player(std::uint16_t player_id) {
  if (player_id == 0) return 0;
  for (const auto& [id, entry] : teams::team_hub().model().teams()) {
    if (std::find(entry.members.begin(), entry.members.end(), player_id) != entry.members.end()) return id;
  }
  return 0;
}
}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
void AuthInbox::push(std::string verb, std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= kMaxInbox) items.erase(items.begin());
  items.emplace_back(std::move(verb), std::move(text));
}
std::vector<std::pair<std::string, std::string>> AuthInbox::take() {
  const std::lock_guard lock(mutex);
  return std::exchange(items, {});
}

std::shared_ptr<AuthInbox> subscribe_authority_verbs(host::IPlatform& platform) {
  auto inbox = std::make_shared<AuthInbox>();
  for (const char* verb : {"auth_md", "auth_saved", "capture_scan"}) {
    const std::string v = verb;
    std::shared_ptr<AuthInbox> in = inbox;
    (void)platform.subscribe_event(("x4mp." + v).c_str(), [in, v](std::string_view text) {
      if (text.size() <= 256 * 1024) in->push(v, std::string(text));
    });
  }
  return inbox;
}

// ---------------------------------------------------------------------------------------------------------------------
std::vector<std::uint8_t> AuthorityFlow::stored_loaded_sha(const session::IStash& stash) {
  if (const auto text = stash.get(kStashKey)) return AuthorityState::from_json(*text).loaded_sha;
  return {};
}
void AuthorityFlow::forget_loaded_sha(session::IStash& stash) {
  if (const auto text = stash.get(kStashKey)) {
    auto s = AuthorityState::from_json(*text);
    s.loaded_sha.clear();
    stash.put(kStashKey, s.to_json());
  }
}

AuthorityFlow::AuthorityFlow(host::HostContext& ctx, session::Session& session, session::IStash& stash, std::shared_ptr<AuthInbox> inbox)
    : session_(session), stash_(stash), inbox_(std::move(inbox)),
      // M4-06..11: the streamer reads every pose through the game adapter (the scan carries ids only) and sends through this flow's two lanes.
      stream_(
          [&game = ctx.game](std::uint64_t component, TrackPose& out) {
            const auto pose = game.object_position(component);
            if (!pose) return false;
            out = TrackPose{pose->x, pose->y, pose->z, pose->yaw, pose->pitch, pose->roll};
            return true;
          },
          EntityStream::Sink{
              [this](std::uint16_t type, std::span<const std::uint8_t> payload) {
                return session_.send(wire::Lane::Control, type, payload) == net::SendResult::Ok;
              },
              [this](std::uint16_t type, std::span<const std::uint8_t> payload) {
                return selfship::selfship_hub().send_realtime(type, payload);
              }},
          StreamConfig{}) {
  if (const auto text = stash_.get(kStashKey)) state_ = AuthorityState::from_json(*text);
  if (const auto dir = ctx.game.save_folder_path()) save_dir_ = fs::path(*dir);
  if (ctx.paths != nullptr && !ctx.paths->dir.empty()) {
    work_dir_ = ctx.paths->dir / "authority";
    ledger_ = ctx.paths->dir / "authority-saves.json";
  }
  // M3-11: the avatars take their net ids and string refs from this flow and send through its control lane.
  avatars::AuthorityServices svc;
  svc.connected = [this] { return uploader_.connected(); };
  svc.alloc_net_id = [this] { return allocate_net_id(); };
  svc.reserve_net_ids_above = [this](std::uint32_t id) { reserve_net_ids_above(id); };
  svc.string_ref = [this](avatars::StrKind kind, const std::string& value) {
    return string_ref(static_cast<std::uint8_t>(kind == avatars::StrKind::Faction ? P::StringKind::Faction : P::StringKind::Macro), value);
  };
  svc.send_control = [this](std::uint16_t type, std::vector<std::uint8_t> payload) { return send_control(type, payload); };
  avatars::avatar_hub().set_services(std::move(svc));
}

std::uint32_t AuthorityFlow::allocate_net_id() {
  const std::uint32_t id = state_.next_net_id++;
  persist();
  return id;
}

void AuthorityFlow::reserve_net_ids_above(std::uint32_t id) noexcept {
  if (state_.next_net_id <= id && id != 0xFFFFFFFEu) {
    state_.next_net_id = id + 1;
    persist();
  }
}

// The server's string table index of (kind, value): one already known, else appended (StringTableAdd with one entry). 0 = could not.
std::uint32_t AuthorityFlow::string_ref(std::uint8_t kind, const std::string& value) {
  if (value.empty()) return 0;
  for (const auto& k : known_strings_) {
    if (k.kind == kind && k.value == value) return k.index;
  }
  if (!uploader_.connected()) return 0;
  const std::uint32_t index = state_.string_count + 1;
  const std::vector<x4mp::authority::StringDesc> add = {{index, kind, value}};
  if (!send_control(T(P::MsgType::StringTableAdd), x4mp::authority::encode_string_table_add(add))) return 0;
  state_.string_count = index;
  known_strings_.push_back({kind, value, index});
  persist();
  return index;
}

AuthorityFlow::~AuthorityFlow() {
  avatars::avatar_hub().clear_services();
  if (prepared_.valid()) prepared_.wait();
}

void AuthorityFlow::persist() const { stash_.put(kStashKey, state_.to_json()); }

void AuthorityFlow::on_welcome(host::HostContext& ctx, bool resumed) {
  const auto generation = uploader_.new_connection(resumed);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: connection generation {} ({})", generation, resumed ? "resumed" : "fresh join");
  if (!resumed) {
    step_ = Step::Idle;
    spawn_due_ = false;
    ship_wait_ = ship_pending_ = late_ship_ = false;
    sent_macros_.clear();
    known_strings_.clear();
    state_.spawned = false;
    state_.host_net_id = 0;
    state_.strings_sent = false;
    state_.string_count = 0;
    state_.next_net_id = 1;
    state_.checkpoints = 0;
    // M4: a fresh join means a new server world (no mirror to resume). The index and the stream state are caches of the old
    // session; clearing them makes the new CaptureSet ask MD for a scan and spawn every entity again under the restarted ids.
    index_.clear();
    stream_.on_disconnect();
    persist();
  }
  // M3-11: the avatars announce themselves again; ids already given to avatars stay reserved (a fresh join restarts the counter at 1).
  avatars::avatar_hub().on_welcome();
  reserve_net_ids_above(avatars::avatar_hub().max_net_id());
}

void AuthorityFlow::on_net_disconnected() {
  uploader_.connection_lost();
  // A new session brings a new CaptureSet with its own epoch; keeping the old one would refuse the first set of the new server if its epoch
  // counter starts lower. The index itself stays: net ids must survive a reconnect.
  capture_.clear();
  scan_asked_.clear();  // the next session asks for its sectors again
}

void AuthorityFlow::note_loaded_save(const std::vector<std::uint8_t>& sha) {
  if (sha.size() != 32) return;
  state_.loaded_sha = sha;
  persist();
}

// M5-07: a kill claim forwarded by the server. The claim names a net id this authority assigned; the authority is the only node
// allowed to destroy an entity, and it answers with exactly one IntentResult either way.
void AuthorityFlow::on_intent(host::HostContext& ctx, std::span<const std::uint8_t> payload) {
  events::IntentView intent;
  if (!events::decode_intent(payload, intent)) {
    ++intent_bad_;
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Warn, "authority: an Intent frame did not decode; ignored");
    return;
  }

  const auto reject = [&](P::RejectReason reason, const char* detail, std::uint32_t target) {
    const std::vector<std::uint8_t> reply =
        events::encode_intent_result(intent.request, P::IntentStatus::Rejected, reason, detail, 0);
    (void)send_frame(T(P::MsgType::IntentResult), reply);
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "authority: intent for net {} rejected: {}", target, detail);
  };

  if (intent.type == P::IntentBody::PlayerDeath) {
    // M5-11: the client's own ship died. The game owns the death and the ship; the mod despawns the avatar everywhere, drops the
    // record so a reloaded client cannot resurrect a stale ghost, and answers with exactly one IntentResult.
    const P::PlayerDeath* death = events::player_death_of(payload);
    const std::uint32_t killer_net = death != nullptr ? death->killer() : 0;
    const std::uint16_t player_id = intent.request.player_id;
    const std::uint32_t net_id = avatars::avatar_hub().net_id_of_player(player_id);
    if (net_id != 0) {
      index_.erase(net_id);
      stream_.on_player_left(net_id);
      avatars::avatar_hub().forget_player(player_id);
      ++players_died_;
    }
    const std::vector<std::uint8_t> reply =
        events::encode_intent_result(intent.request, P::IntentStatus::Accepted, P::RejectReason::None, "", net_id);
    (void)send_frame(T(P::MsgType::IntentResult), reply);
    const std::vector<std::uint8_t> event =
        events::encode_player_died_event(player_id, killer_net, player_id, 0, 0, intent.request.game_time, 0);
    (void)send_frame(T(P::MsgType::GameEvent), event);
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Info,
              "authority: player {} died (killer net {}); avatar net {} despawned as PlayerLeft, record dropped", static_cast<int>(player_id),
              killer_net, net_id);
    return;
  }
  if (intent.type == P::IntentBody::TradeReport) {
    on_trade_report(ctx, intent, payload);
    return;
  }
  if (intent.type == P::IntentBody::StationBuildRequest) {
    on_station_build(ctx, intent, payload);
    return;
  }
  if (intent.type == P::IntentBody::CaptureReport) {
    on_capture_report(ctx, intent, payload);
    return;
  }
  if (intent.type != P::IntentBody::KillClaim) {
    ++intent_unsupported_;
    reject(P::RejectReason::Unsupported, "the authority does not apply this intent", 0);
    return;
  }

  const P::KillClaim* claim = events::kill_claim_of(payload);
  if (claim == nullptr) {
    ++intent_bad_;
    reject(P::RejectReason::InvalidParameters, "the kill claim has no body", 0);
    return;
  }

  const std::uint32_t target = claim->target();
  const std::uint32_t killer = claim->killer();

  IndexEntry* row = index_.find(target);
  if (row == nullptr) {
    ++intent_unknown_;
    reject(P::RejectReason::UnknownEntity, "unknown net id", target);
    return;
  }
  if (row->component == 0) {
    ++intent_failed_;
    reject(P::RejectReason::InvalidParameters, "the row has no component", target);
    return;
  }
  if (!capture_.contains(row->sector)) {
    ++intent_out_of_interest_;
    reject(P::RejectReason::NotInInterest, "the sector is not in the capture set", target);
    return;
  }

  const std::uint64_t component = row->component;
  const std::uint16_t sector = row->sector;
  const std::uint32_t macro_ref = row->macro_ref;
  const std::uint32_t owner_ref = row->owner_ref;
  const std::uint8_t kind = row->kind;

  // The only removal path in the mod. The player guard is what keeps a claim from ever destroying a player's own ship.
  const game::RemoveResult removed = game::safe_remove(component);
  if (removed == game::RemoveResult::BlockedPlayerGuard) {
    ++intent_player_;
    reject(P::RejectReason::NotPermitted, "the target is player-guarded", target);
    return;
  }
  if (removed == game::RemoveResult::BlockedInvalidId || removed == game::RemoveResult::BlockedWrongThread) {
    ++intent_failed_;
    reject(P::RejectReason::Conflict, "the target could not be removed", target);
    return;
  }

  index_.erase(target);
  stream_.on_killed(target, killer);
  ++kills_applied_;

  const std::vector<std::uint8_t> accepted =
      events::encode_intent_result(intent.request, P::IntentStatus::Accepted, P::RejectReason::None, "", target);
  (void)send_frame(T(P::MsgType::IntentResult), accepted);

  // The feed event. The server stamps event_seq and server_time_us itself, so zeros here are correct.
  events::KillEventData event;
  event.victim = target;
  event.victim_macro_ref = macro_ref;
  event.victim_owner_ref = owner_ref;
  event.victim_kind = static_cast<P::EntityKind>(kind);
  event.killer = killer;
  event.killer_player = intent.request.player_id;
  (void)send_frame(T(P::MsgType::GameEvent), events::encode_kill_event(event, 0, 0, intent.request.game_time, sector));

  X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "authority: net {} killed by player {} (net {}), despawn queued", target,
            static_cast<int>(intent.request.player_id), killer);
}

// M5-14/M5-15: the client saw a trade at a station. The station's stock is the authority's to change; the report names the station by
// the id the client matched, which this node resolves through its own statics scan (statics_hub().station_component).
void AuthorityFlow::on_trade_report(host::HostContext& ctx, const events::IntentView& intent, std::span<const std::uint8_t> payload) {
  const auto reject = [&](P::RejectReason reason, const char* detail, std::uint32_t target) {
    const std::vector<std::uint8_t> reply =
        events::encode_intent_result(intent.request, P::IntentStatus::Rejected, reason, detail, 0);
    (void)send_frame(T(P::MsgType::IntentResult), reply);
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "authority: trade for net {} rejected: {}", target, detail);
  };

  const P::TradeReport* report = events::trade_report_of(payload);
  if (report == nullptr) {
    ++intent_bad_;
    reject(P::RejectReason::InvalidParameters, "the trade report has no body", 0);
    return;
  }
  const std::uint32_t station = report->station();
  const std::uint32_t ship = report->ship();
  const std::int32_t amount = report->amount();
  const std::string ware = report->ware() != nullptr ? report->ware()->str() : std::string();
  if (station == 0 || amount == 0 || ware.empty()) {
    ++intent_failed_;
    reject(P::RejectReason::InvalidParameters, "the trade report is incomplete", station);
    return;
  }

  const std::uint64_t component = statics::statics_hub().station_component(station);
  if (component != 0) {
    apply_trade(ctx, intent.request, station, ship, component, ware, amount, report->unit_price());
    return;
  }

  // The station scan has not bound this id yet. Ask for the reporting ship's sector and keep the report for kTradeRetryUs: the client is
  // owed exactly one IntentResult, so none is sent here.
  ++trades_deferred_;
  if (const IndexEntry* row = index_.find(ship); row != nullptr && row->sector != 0) x4mp::features::request_station_scan(row->sector);
  if (pending_trades_.size() >= kMaxPendingTrades) {
    ++trades_dropped_;
    reject(P::RejectReason::Conflict, "too many trades are waiting for a station scan", station);
    return;
  }
  const std::int64_t now_us = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();
  pending_trades_.push_back(PendingTrade{intent.request, station, ship, ware, amount, report->unit_price(), now_us});
  X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "authority: trade at net {} deferred, the station scan has not bound it yet", station);
}

// M5-19: a client finished a station. The authority spawns it with SpawnStationAtPos under the player's team faction, gives it the
// same net id every node computes from (sector, macro, 50 m) and announces it with one EntitySpawn, so the builder's local station and
// the authority's copy are the same station everywhere (docs/protocol.md 1195, ADR-009).
void AuthorityFlow::on_station_build(host::HostContext& ctx, const events::IntentView& intent, std::span<const std::uint8_t> payload) {
  const auto reject = [&](P::RejectReason reason, const char* detail) {
    ++stations_rejected_;
    const std::vector<std::uint8_t> reply = events::encode_intent_result(intent.request, P::IntentStatus::Rejected, reason, detail, 0);
    (void)send_frame(T(P::MsgType::IntentResult), reply);
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "authority: build request rejected: {}", detail);
  };

  const P::StationBuildRequest* build = events::station_build_of(payload);
  if (build == nullptr) {
    ++intent_bad_;
    reject(P::RejectReason::InvalidParameters, "the build request has no body");
    return;
  }
  const std::string macro = build->macro() != nullptr ? build->macro()->str() : std::string();
  const std::string plan = build->construction_plan() != nullptr ? build->construction_plan()->str() : std::string();
  if (macro.empty() || build->sector() == 0) {
    ++intent_failed_;
    reject(P::RejectReason::InvalidParameters, "the build request is incomplete");
    return;
  }

  // The sender's ship tells us which sector the build happened in: the client's sector index is the wire index, and the ship's own
  // component is in that sector, so GetContextByClass gives the UniverseID SpawnStationAtPos wants.
  const std::uint32_t ship = avatars::avatar_hub().net_id_of_player(intent.request.player_id);
  const IndexEntry* ship_row = ship != 0 ? index_.find(ship) : nullptr;
  if (ship_row == nullptr || ship_row->component == 0) {
    ++intent_failed_;
    reject(P::RejectReason::NotInInterest, "the reporting ship is not in an interest sector");
    return;
  }
  const game::UniverseId sector_id = ctx.game.context_by_class(ship_row->component, "sector", false);
  if (sector_id == 0) {
    ++intent_failed_;
    reject(P::RejectReason::Conflict, "the reporting ship has no sector");
    return;
  }
  const std::uint16_t builder_team = team_of_player(intent.request.player_id);
  const std::uint16_t owner_team = builder_team != 0 ? builder_team : session_.welcome().team_id;
  const std::string owner = teams::team_hub().faction_of_team(owner_team);
  if (owner.empty()) {
    ++intent_failed_;
    reject(P::RejectReason::AuthorityUnavailable, "the team faction is not ready");
    return;
  }

  const P::Vec3f* position = build->position();
  const P::Rot3f* rotation = build->rotation();
  game::PosRotPod pose;
  if (position != nullptr) {
    pose.x = position->x();
    pose.y = position->y();
    pose.z = position->z();
  }
  if (rotation != nullptr) {
    pose.yaw = rotation->yaw();
    pose.pitch = rotation->pitch();
    pose.roll = rotation->roll();
  }
  const game::UniverseId component = ctx.game.spawn_station(macro.c_str(), sector_id, pose, plan.c_str(), owner.c_str());
  if (component == 0) {
    ++intent_failed_;
    reject(P::RejectReason::Conflict, "SpawnStationAtPos refused the station");
    return;
  }

  // The id every node computes for this station: the M3 container hash over (sector macro, macro, 50 m bucket) - the same fallback the
  // statics scan uses while the manifest has no station rows (W4 notes). The builder is told the id in the IntentResult.
  const auto* map = selfship::selfship_hub().map();
  const std::string sector_macro = map != nullptr ? std::string(map->macro_of(build->sector())) : std::string();
  std::uint32_t net_id = onfoot::matched_container_id(onfoot::station_match_key(sector_macro, macro, pose.x, pose.y, pose.z));
  if (net_id == 0 || net_id == 0xFFFFFFFFu) net_id = allocate_net_id();
  statics::statics_hub().pin_station(build->sector(), component, net_id, sector_macro);
  index_.upsert(net_id, build->sector(), string_ref(static_cast<std::uint8_t>(P::StringKind::Macro), macro),
                string_ref(static_cast<std::uint8_t>(P::StringKind::Faction), owner), static_cast<std::uint8_t>(P::EntityKind::Station),
                0, component,
                std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count());
  ++stations_spawned_;

  const std::vector<std::uint8_t> accepted =
      events::encode_intent_result(intent.request, P::IntentStatus::Accepted, P::RejectReason::None, "", net_id);
  (void)send_frame(T(P::MsgType::IntentResult), accepted);

  // The announcement. game_time is the spawn time and is never 0 (the builder refuses it); the state is the station's own pose.
  double spawn_time = intent.request.game_time;
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(spawn_time)) {
    const auto now_gt = ctx.game.game_time();
    spawn_time = now_gt && x4mp::authority::EntitySpawnBuilder::is_valid_game_time(*now_gt) ? *now_gt : 0.0;
  }
  x4mp::authority::EntitySpawnBuilder builder(spawn_time);
  x4mp::authority::SpawnEntity entity;
  entity.net_id = net_id;
  entity.kind = x4mp::authority::SpawnKind::Station;
  entity.origin = x4mp::authority::SpawnOrigin::PlayerBuilt;
  entity.macro_ref = string_ref(static_cast<std::uint8_t>(P::StringKind::Macro), macro);
  entity.owner_ref = string_ref(static_cast<std::uint8_t>(P::StringKind::Faction), owner);
  entity.owner_team = owner_team;
  entity.owner_player = intent.request.player_id;
  entity.sector = build->sector();
  const auto qx = wire::quantize_position(pose.x), qy = wire::quantize_position(pose.y), qz = wire::quantize_position(pose.z);
  const auto ry = wire::quantize_rotation(pose.yaw), rp = wire::quantize_rotation(pose.pitch), rr = wire::quantize_rotation(pose.roll);
  if (qx && qy && qz && ry && rp && rr) {
    entity.px = *qx;
    entity.py = *qy;
    entity.pz = *qz;
    entity.yaw = *ry;
    entity.pitch = *rp;
    entity.roll = *rr;
  }
  (void)builder.add(std::move(entity));
  const auto spawn = builder.build();
  if (!spawn) {
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Warn, "authority: station net {} spawned but its EntitySpawn was refused: {}", net_id,
              x4mp::authority::to_string(spawn.error()));
  } else {
    (void)send_control(T(P::MsgType::EntitySpawn), *spawn);
  }

  X4MP_CLOG(ctx.log, Cat::Auth, Level::Info,
            "authority: station '{}' spawned at net {} (component {}, sector {}, owner {}) for player {} (spawned={} rejected={})", macro,
            net_id, component, sector_macro, owner, static_cast<int>(intent.request.player_id), stations_spawned_, stations_rejected_);
}

// M5-21: a client captured an entity. The owner change belongs to the authority: the game gets SetComponentOwner with the
// capturer's team faction (node-relative), the index learns the new owner, and one EntityChange carries the team to every
// node. A ghost is never destroyed or removed here.
void AuthorityFlow::on_capture_report(host::HostContext& ctx, const events::IntentView& intent,
                                      std::span<const std::uint8_t> payload) {
  const auto reject = [&](P::RejectReason reason, std::string_view detail, std::uint32_t net_id) {
    ++captures_rejected_;
    const std::vector<std::uint8_t> reply =
        events::encode_intent_result(intent.request, P::IntentStatus::Rejected, reason, std::string(detail), net_id);
    (void)send_frame(T(P::MsgType::IntentResult), reply);
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Info, "authority: capture of net {} rejected: {} (rejected={})", net_id, detail,
              captures_rejected_);
  };

  const P::CaptureReport* report = events::capture_report_of(payload);
  if (report == nullptr) {
    ++intent_bad_;
    reject(P::RejectReason::InvalidParameters, "the capture report has no body", 0);
    return;
  }
  const std::uint32_t target = report->target();

  IndexEntry* row = index_.find(target);
  if (row == nullptr) {
    ++intent_unknown_;
    reject(P::RejectReason::UnknownEntity, "unknown net id", target);
    return;
  }
  if (row->component == 0) {
    ++intent_failed_;
    reject(P::RejectReason::InvalidParameters, "the row has no component", target);
    return;
  }
  if (!capture_.contains(row->sector)) {
    ++intent_out_of_interest_;
    reject(P::RejectReason::NotInInterest, "the sector is not in the capture set", target);
    return;
  }

  const std::uint64_t component = row->component;
  const std::uint16_t sector = row->sector;
  const std::uint32_t macro_ref = row->macro_ref;
  const std::uint32_t old_owner_ref = row->owner_ref;
  const std::uint8_t kind = row->kind;
  const auto flags = row->flags;

  if (game::is_player_guarded(component)) {
    ++intent_player_;
    reject(P::RejectReason::NotPermitted, "the target is player-guarded", target);
    return;
  }

  const std::uint16_t team = team_of_player(intent.request.player_id);
  if (team == 0) {
    ++intent_failed_;
    reject(P::RejectReason::AuthorityUnavailable, "the reporting player has no team in this session", target);
    return;
  }
  const std::string faction = teams::team_hub().faction_of_team(team);
  if (faction.empty()) {
    ++intent_failed_;
    reject(P::RejectReason::AuthorityUnavailable, "the team has no faction slot", target);
    return;
  }
  if (!ctx.game.set_component_owner(component, faction.c_str())) {
    ++intent_failed_;
    reject(P::RejectReason::Conflict, "the game refused the owner change", target);
    return;
  }

  const std::uint32_t owner_ref = string_ref(static_cast<std::uint8_t>(P::StringKind::Faction), faction);
  const auto now_us = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
  index_.upsert(target, sector, macro_ref, owner_ref != 0 ? owner_ref : old_owner_ref, kind, flags, component,
                static_cast<std::uint64_t>(now_us));
  ++captures_applied_;

  const std::vector<std::uint8_t> accepted =
      events::encode_intent_result(intent.request, P::IntentStatus::Accepted, P::RejectReason::None, "", target);
  (void)send_frame(T(P::MsgType::IntentResult), accepted);

  // The team is what every other node needs: it maps the team index to its own faction string (11.2).
  stream_.on_change(target, owner_ref, 0, team, intent.request.player_id);

  const std::vector<std::uint8_t> event =
      events::encode_capture_event(target, old_owner_ref, 0, team, intent.request.player_id, 0, 0, intent.request.game_time, sector);
  (void)send_frame(T(P::MsgType::GameEvent), event);

  X4MP_CLOG(ctx.log, Cat::Auth, Level::Info,
            "authority: net {} captured by player {} (component {}, sector {}, team {}, faction '{}') (applied={} rejected={})",
            target, static_cast<int>(intent.request.player_id), component, sector, team, faction, captures_applied_,
            captures_rejected_);
}

void AuthorityFlow::apply_trade(host::HostContext& ctx, const events::IntentRequest& request, std::uint32_t station, std::uint32_t ship,
                               std::uint64_t component, const std::string& ware, std::int32_t amount, std::int32_t unit_price) {
  // A buy (the client's amount is positive) takes the ware off the station, a sale puts it back. The shim applies it with exact= so the
  // number that lands is the number the trade moved (docs/spikes/session-1-results.md).
  economy::CargoOp op;
  op.component = component;
  op.ware = ware;
  op.amount = -amount;
  economy::economy_hub().queue(op);
  ++stock_ops_;
  ++trades_applied_;

  const std::vector<std::uint8_t> accepted =
      events::encode_intent_result(request, P::IntentStatus::Accepted, P::RejectReason::None, "", station);
  (void)send_frame(T(P::MsgType::IntentResult), accepted);

  // M5-16: the snapshot is read a frame or two after the shim ran, so every node ends up on the authority's number rather than on its own
  // optimistic guess.
  const std::int64_t now_us = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();
  snapshot_due_.push_back(SnapshotDue{station, component, now_us + kSnapshotDelayUs});

  // The feed event. Zeros for event_seq and server_time_us: the server stamps them, same rule as the kill event.
  (void)send_frame(T(P::MsgType::GameEvent),
                   events::encode_trade_event(station, ship, request.player_id, economy::ware_ref_of(ware), amount, unit_price, 0, 0,
                                              request.game_time, 0));

  // M5-17: the NPC side of the trade. The player's own money already moved inside the game; the station owner's faction is moved the
  // opposite way so the NPC economy does not leak (a buy pays the owner, a sale is paid by it - economy::trade_money_op decides).
  // The payer is the reporting player's team faction ("player" when they are on none), never a value the client sent; an owner the
  // scan could not read means no op at all, and that is counted.
  const std::uint16_t payer_team = team_of_player(request.player_id);
  const std::string payer = payer_team != 0 ? teams::team_hub().faction_of_team(payer_team) : std::string("player");
  if (const auto money =
          economy::trade_money_op(payer, statics::statics_hub().owner_of(component), amount, unit_price)) {
    economy::economy_hub().queue(*money);
    ++trades_paid_;
  }
  X4MP_CLOG(ctx.log, Cat::Auth, Level::Info,
            "authority: trade at net {} ware '{}' x{} applied to component {} (applied={} deferred={} dropped={} unknown={} stock ops={} "
            "snapshots={} paid={})",
            station, ware, amount, component, trades_applied_, trades_deferred_, trades_dropped_, trades_unknown_, stock_ops_,
            snapshots_sent_, trades_paid_);
}

void AuthorityFlow::send_station_cargo(host::HostContext& ctx, std::uint32_t station, std::uint64_t component) {
  const std::vector<game::CargoPod> cargo = ctx.game.cargo(component);
  if (cargo.empty()) {
    // An empty read is indistinguishable from a failed one (GetNumCargo answers 0 for both), so it never becomes an empty snapshot.
    ++trades_unknown_;
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Warn, "authority: station net {} gave an empty cargo read; no snapshot sent", station);
    return;
  }
  std::vector<events::CargoLine> lines;
  lines.reserve(cargo.size());
  for (const game::CargoPod& line : cargo) {
    const std::uint32_t ref = string_ref(static_cast<std::uint8_t>(P::StringKind::Ware), line.ware);
    if (ref == 0) continue;
    lines.push_back(events::CargoLine{ref, line.amount});
  }
  if (lines.empty()) {
    ++trades_unknown_;
    return;
  }
  if (send_frame(T(P::MsgType::EntityCargo), events::encode_entity_cargo(station, lines))) ++snapshots_sent_;
}

void AuthorityFlow::retry_trades(host::HostContext& ctx) {
  if (snapshot_due_.empty() && pending_trades_.empty()) return;
  const std::int64_t now_us = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();

  std::vector<SnapshotDue> still_due;
  for (const SnapshotDue& due : snapshot_due_) {
    if (due.at_us > now_us) {
      still_due.push_back(due);
      continue;
    }
    send_station_cargo(ctx, due.station, due.component);
  }
  snapshot_due_ = std::move(still_due);

  std::vector<PendingTrade> still_pending;
  for (PendingTrade& trade : pending_trades_) {
    const std::uint64_t component = statics::statics_hub().station_component(trade.station);
    if (component != 0) {
      apply_trade(ctx, trade.request, trade.station, trade.ship, component, trade.ware, trade.amount, trade.unit_price);
      continue;
    }
    if (now_us - trade.at_us < kTradeRetryUs) {
      still_pending.push_back(std::move(trade));
      continue;
    }
    ++trades_dropped_;
    const std::vector<std::uint8_t> reply = events::encode_intent_result(
        trade.request, P::IntentStatus::Rejected, P::RejectReason::Conflict, "the station scan did not bind the station in time", 0);
    (void)send_frame(T(P::MsgType::IntentResult), reply);
    X4MP_CLOG(ctx.log, Cat::Auth, Level::Warn, "authority: trade at net {} dropped, the station scan did not bind it in {} ms",
              trade.station, kTradeRetryUs / 1000);
  }
  pending_trades_ = std::move(still_pending);
}

bool AuthorityFlow::send_control(std::uint16_t type, const std::vector<std::uint8_t>& payload) {
  return session_.send(wire::Lane::Control, type, std::span<const std::uint8_t>(payload)) == net::SendResult::Ok;
  return session_.send(wire::Lane::Control, type, std::span<const std::uint8_t>(payload)) == net::SendResult::Ok;
}

bool AuthorityFlow::on_frame(host::HostContext& ctx, std::uint16_t type, std::span<const std::uint8_t> payload) {
  if (type == T(P::MsgType::AuthorityAssign)) {
    // M3-25: the server rolled its world back to the save this game loads; ids from `next_net_id` up are free, every lower one may still be live
    // (the server's mirror, the clients' ghosts). Only ever raises the counter (a fresh join restarted it at 1).
    flatbuffers::Verifier v(payload.data(), payload.size());
    if (!payload.empty() && v.VerifyBuffer<P::AuthorityAssign>(nullptr)) {
      const auto* a = flatbuffers::GetRoot<P::AuthorityAssign>(payload.data());
      const std::uint32_t floor = a->next_net_id();
      if (floor != 0xFFFFFFFFu) {
        X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: AuthorityAssign: net ids continue from {} (counter was {})", floor, state_.next_net_id);
        if (floor > 1) reserve_net_ids_above(floor - 1);
      }
    }
    return true;
  }
  if (type == T(P::MsgType::CaptureSet)) {
    on_capture_set(ctx, payload);
    return true;
  }
  if (type == T(P::MsgType::RequestSave)) {
    const auto req = x4mp::authority::parse_request_save(payload);
    if (!req) return true;
    if (step_ != Step::Idle || uploader_.running() || uploader_.pending() || spawn_due_) {
      X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: RequestSave {} ignored, a checkpoint is already in progress", req->request_id);
      return true;
    }
    begin_request(ctx, req->request_id);
    return true;
  }
  if (type == T(P::MsgType::Intent)) {
    on_intent(ctx, payload);
    return true;
  }
  // M5-31: the ledger's asset side of an escrowed trade, and the server asking what became of an order it never heard about.
  // The hub owns the journal and the confirm outbox; this flow only routes.
  if (type == T(P::MsgType::AssetTransferOrder) || type == T(P::MsgType::TradeQuery)) {
    (void)settlement::settlement_hub().on_frame(ctx, type, payload);
    return true;
  }
  return uploader_.on_frame(type, payload);
}

// M4-01: the server's interest. It owns the set and resends it after any change; the authority only obeys. A resend of the same epoch is
// refused, because restarting capture would re-send EntitySpawn for every entity of every sector.
void AuthorityFlow::on_capture_set(host::HostContext& ctx, std::span<const std::uint8_t> payload) {
  std::string why;
  auto data = decode_capture_set(payload, &why);
  if (!data) {
    ++capture_bad_;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: CaptureSet ignored ({})", why);
    return;
  }
  const auto epoch = data->epoch;
  const CaptureSet before = capture_;
  if (!capture_.apply(epoch, std::move(data->sectors), std::move(data->focus))) {
    ++capture_stale_;
    return;
  }
  ++capture_sets_;
  // A sector that left the set is captured no more: keeping its rows would cost memory and schedule time for nothing.
  std::uint32_t dropped = 0;
  for (const auto& s : before.sectors()) {
    if (!capture_.contains(s.sector)) {
      dropped += static_cast<std::uint32_t>(index_.drop_sector(s.sector));
      scan_asked_.erase(s.sector);
    }
  }
  const auto change = capture_.describe_change(before);
  // M4-06: the scheduler and the focus classification follow the new set; a sector that left starts the OutOfInterest grace in the streamer.
  stream_.on_capture_set(capture_);
  std::string tail = change.empty() ? "unchanged" : change;
  if (dropped != 0) tail += ", " + std::to_string(dropped) + " rows dropped";

  // A sector that just entered the set has no rows yet: ask our own MD for its ship list (M4-03). MD cannot name the sector the way the wire
  // does - it knows the macro id, native owns the number - so the request carries the macro and MD answers with the same macro.
  std::uint32_t asked = 0;
  const auto now_us = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();
  for (const auto& s : capture_.sectors()) {
    const auto* rows = index_.ids_of(s.sector);
    if (rows != nullptr && !rows->empty()) continue;
    if (const auto it = scan_asked_.find(s.sector); it != scan_asked_.end() && now_us - it->second < kScanRetryUs) continue;
    const auto* map = selfship::selfship_hub().map();
    const auto macro = map != nullptr ? map->macro_of(s.sector) : std::string_view{};
    if (macro.empty()) {
      ++scan_no_sector_;  // the galaxy map has no row for this sector yet; the next CaptureSet tries again
      continue;
    }
    nlohmann::json j;
    j["v"] = 1;
    j["sector"] = std::string(macro);
    if (ctx.platform.raise_lua("x4mp.capture_request", j.dump())) {
      scan_asked_[s.sector] = now_us;
      ++asked;
    } else {
      ++scan_request_failed_;
    }
  }
  if (asked != 0) tail += ", asked MD for " + std::to_string(asked) + " sector scan(s)";
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: CaptureSet epoch {}: {} ({})", epoch, capture_.describe(), tail);
}

// M4-03: our own MD scanned one sector and pushed the ids. MD cannot produce the wire sector number (native owns it, galaxy_map.h), so it
// sends the sector macro id and this resolves it. Rows never carry positions: the streamer reads those natively.
void AuthorityFlow::on_scan_text(host::HostContext& ctx, std::string_view text) {
  auto batch = parse_scan_batch(text);
  if (!batch) {
    ++scan_bad_;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: unreadable sector scan ignored ({} bytes)", text.size());
    return;
  }
  const auto* map = selfship::selfship_hub().map();
  const auto sector = map != nullptr ? map->index_of_macro(batch->sector_macro) : std::uint16_t{0};
  if (sector == 0) {
    // Guessing a number would place ships in the wrong sector; without the map row there is nothing to index.
    ++scan_no_sector_;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: scan of unknown sector '{}' ignored ({} rows)", batch->sector_macro,
              batch->rows.size());
    return;
  }
  ++scan_batches_;
  if (!index_.scanning(sector)) index_.begin_scan(sector);
  const auto now_us = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();

  for (const auto& row : batch->rows) {
    auto net = index_.net_id_of_component(row.component);
    if (net == 0) {
      // First sight of this object: the net id comes from the session's one counter, so it can never collide with an avatar's.
      net = allocate_net_id();
      index_.bind_component(row.component, net);
    }
    // M4-11: the scan is also the only source of an owner/macro change we have today. Read the old row BEFORE the upsert (it may rehash).
    const auto* old = index_.find(net);
    const bool known = old != nullptr;
    const std::uint32_t old_owner = known ? old->owner_ref : 0;
    const std::uint32_t old_macro = known ? old->macro_ref : 0;
    const auto macro_ref = string_ref(static_cast<std::uint8_t>(P::StringKind::Macro), row.macro);
    const auto owner_ref = string_ref(static_cast<std::uint8_t>(P::StringKind::Faction), row.owner);
    // The kind stays Other until a later wave carries the ship class; a client spawns from the macro, not from the kind.
    index_.upsert(net, sector, macro_ref, owner_ref, static_cast<std::uint8_t>(x4mp::authority::SpawnKind::Other), 0, row.component, now_us);
    if (known && (owner_ref != old_owner || macro_ref != old_macro)) stream_.on_change(net, owner_ref, macro_ref);
  }

  if (!batch->end) return;
  index_.reserve(static_cast<std::size_t>(batch->count));
  std::vector<UniverseIndex::RemovedRow> removed_rows;
  const auto removed = index_.end_scan(sector, &removed_rows);
  scan_asked_.erase(sector);  // the pass is closed: a later CaptureSet may ask again (the sector's ship list can change)
  // M4-10: a ship the pass no longer sees is gone. If its component no longer exists in the game it was destroyed; otherwise it left the sector.
  for (const auto& row : removed_rows) {
    const bool destroyed = row.component != 0 && !ctx.game.is_valid_component(row.component);
    stream_.on_row_removed(row.net_id, destroyed);
  }
  // M4-09: the spawns and the SectorComplete marker go out on the next stream tick, in that order.
  stream_.on_scan_complete(sector);
  const auto* ids = index_.ids_of(sector);
  const auto count = ids != nullptr ? static_cast<std::uint32_t>(ids->size()) : 0u;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: sector {} scanned: {} ships, {} gone, spawns queued (epoch {})", sector, count, removed,
            capture_.epoch());
}

void AuthorityFlow::begin_request(host::HostContext& ctx, std::uint32_t request_id) {  request_id_ = request_id;
  collected_.reset();
  end_seen_at_.reset();
  plan_ = GalaxyPlan{};
  step_ = Step::Collecting;
  step_since_ = Clock::now();
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: RequestSave {}: collecting galaxy metadata from MD", request_id);
  if (!ctx.platform.raise_lua("x4mp.auth_collect", R"({"v":1})")) {
    fail(ctx, "the Lua bridge is not available (x4mp.auth_collect could not be raised)");
  }
}

void AuthorityFlow::fail(host::HostContext& ctx, const std::string& why) {
  X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "authority: checkpoint request {} failed: {}", request_id_, why);
  step_ = Step::Idle;
  watcher_.reset();
  if (prepared_.valid()) prepared_.wait();
  prepared_ = {};
}

void AuthorityFlow::drain_inbox(host::HostContext& ctx) {
  for (auto& [verb, text] : inbox_->take()) {
    if (verb == "auth_md") {
      if (step_ != Step::Collecting) {
        // the answer to a ship-only question (item 3): a P record, or N when MD has no ship either
        if (!ship_pending_ && !ship_wait_) continue;
        MdCollector answer;
        if (!answer.add(json_string(text, "data"))) continue;
        if (answer.ship()) {
          spawn_ship_ = answer.ship();
          ship_pending_ = false;
          ship_wait_ = false;
          late_ship_ = true;
          spawn_due_ = !state_.spawned;
          X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: MD now reports the player's ship ({}) after {} ask(s)", spawn_ship_->macro, ship_asks_);
        } else if (answer.no_ship_seen() && ship_pending_) {
          ship_pending_ = false;
          schedule_reask();
        }
        continue;
      }
      const std::string data = json_string(text, "data");
      if (!collected_.add(data)) {
        X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: unreadable galaxy message ignored ({} bytes)", data.size());
      } else if (collected_.end_seen() && !end_seen_at_) {
        end_seen_at_ = Clock::now();
      }
    } else if (verb == "capture_scan") {
      // M4-03: one sector scan batch from our own MD. Runs whether or not a checkpoint is in progress.
      on_scan_text(ctx, json_string(text, "data"));
    } else if (verb == "auth_saved") {
      if (step_ == Step::WaitSaveReply) {
        on_save_reply(ctx, text);
      } else {
        X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: SaveGame reply with no save pending, ignored");
      }
    }
  }
}

void AuthorityFlow::step(host::HostContext& ctx) {
  // M4-30: the authority's own per-frame cost, the number the M4 acceptance quotes ("< 3 ms/frame authority mod cost"). It covers
  // everything below - capture ingest, the scan answers, the 20 Hz world tick and the uploader pump - because that is what the
  // authority costs the frame. The stats feature takes the slot every 30 s (stats::timing_hub()).
  const Clock::time_point started = Clock::now();
  drain_inbox(ctx);
  retry_trades(ctx);
  switch (step_) {
    case Step::Collecting: step_collecting(ctx); break;
    case Step::WaitSaveReply:
      if (Clock::now() - step_since_ > kSaveReplyTimeout) fail(ctx, "Lua did not answer the SaveGame request in 20 s");
      break;
    case Step::WaitFile: step_wait_file(ctx); break;
    case Step::Hashing: step_hashing(ctx); break;
    case Step::Uploading: step_uploading(ctx); break;
    case Step::Idle: break;
  }
  (void)uploader_.pump([this](wire::Lane lane, std::uint16_t type, std::span<const std::uint8_t> payload) {
    return session_.send(lane, type, payload) == net::SendResult::Ok;
  });
  if (spawn_due_) maybe_spawn(ctx);
  if (ship_wait_ && step_ == Step::Idle && !spawn_due_) step_ship_wait(ctx);
  step_world_clock(ctx);
  stats::timing_hub().slot("authority").add(std::chrono::duration<double, std::milli>(Clock::now() - started).count());
}

// M3-14 (found by the two-DLL pair run): without a WorldUpdate the server never replicates anything (ReplicationModule: "no WorldUpdate yet: its game time is the
// reference every entry is stamped with"), so no client would see another player move. M4-08: the same 20 Hz tick now carries the captured world; an empty
// WorldUpdate is still the clock keepalive, so the M3 guarantee is unchanged.
void AuthorityFlow::step_world_clock(host::HostContext& ctx) {
  if (state_.checkpoints == 0 && stored_total_ == 0) return;
  const auto now = Clock::now();
  if (now < next_wu_) return;
  auto& hub = selfship::selfship_hub();
  std::int64_t server_now = 0;
  if (!hub.linked() || !hub.server_now(server_now)) return;
  const auto game_time = ctx.game.game_time();
  if (!game_time || !x4mp::authority::EntitySpawnBuilder::is_valid_game_time(*game_time)) return;
  next_wu_ = std::max(next_wu_ + std::chrono::milliseconds(50), now - std::chrono::milliseconds(50));  // 20 Hz, at most one extra send after a long frame

  // The dead-reckoning tiers are measured from the authority's own ship; without it every entity is "far".
  const auto& status = hub.status();
  const auto observer = status.ship != 0 ? ctx.game.object_position(status.ship) : std::nullopt;
  if (observer) {
    stream_.set_observer(status.sector, observer->x, observer->y, observer->z);
  } else {
    stream_.set_observer(0, 0.0, 0.0, 0.0);
  }
  const auto now_s = std::chrono::duration<double>(now.time_since_epoch()).count();
  stream_.step(*game_time, server_now, now_s, capture_, index_);

  // M2: the streamer's own counters. `skipped_no_pose` is the whole point: a docked or landed hull answers (0,0,0) from
  // GetObjectPositionInSector, so it is skipped instead of spawned at the sector centre, and without this line the only evidence
  // was a ghost that never appeared. One line per 10 s, and only when one of the counters moved.
  if (now - next_stream_log_ >= std::chrono::seconds(10)) {
    next_stream_log_ = now;
    const auto& s = stream_.stats();
    const std::uint32_t total = s.spawns + s.updates + s.skipped_no_pose + s.skipped_far + s.skipped_no_macro + s.encode_failed + s.send_failed;
    if (total != stream_logged_total_) {
      stream_logged_total_ = total;
      X4MP_CLOG(ctx.log, Cat::Save, Level::Info,
                "[sync] stream: spawns={} updates={} polls={} skipped_no_macro={} skipped_no_pose={} skipped_far={} encode_failed={} send_failed={} sectors_complete={}",
                s.spawns, s.updates, s.polls, s.skipped_no_macro, s.skipped_no_pose, s.skipped_far, s.encode_failed, s.send_failed,
                s.sectors_complete);
    }
  }

  if (!wu_logged_) {
    wu_logged_ = true;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info,
              "authority: world clock: WorldUpdate at 20 Hz started (game_time {:.3f}); the streamer follows the CaptureSet and the server replicates from now on",
              *game_time);
  }
}

void AuthorityFlow::step_collecting(host::HostContext& ctx) {
  const auto now = Clock::now();
  if (collected_.complete() && (collected_.ship() || (end_seen_at_ && now - *end_seen_at_ > kShipGrace))) {
    plan_ = build_plan(collected_);
    if (plan_.sectors.empty()) return fail(ctx, "MD reported no sectors");
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: galaxy collected: {} sectors, {} links, {} strings, ship {}", plan_.sectors.size(),
              plan_.links.size(), plan_.strings.size(), collected_.ship() ? collected_.ship()->macro : std::string("(none)"));
    request_save(ctx);
  } else if (now - step_since_ > kCollectTimeout) {
    fail(ctx, "MD did not deliver the galaxy metadata in 20 s (is md/x4mp_galaxy.xml loaded?)");
  }
}

void AuthorityFlow::request_save(host::HostContext& ctx) {
  std::array<std::uint8_t, 16> rnd{};
  if (!crypto::random_bytes(rnd)) return fail(ctx, "no random source for the checkpoint id");
  std::memcpy(&checkpoint_.lo, rnd.data(), 8);
  std::memcpy(&checkpoint_.hi, rnd.data() + 8, 8);
  if (checkpoint_.zero()) checkpoint_.lo = 1;
  save_name_ = checkpoint_save_name(checkpoint_.lo);
  if (save_dir_.empty()) return fail(ctx, "the game's save folder is not available");

  spawn_strings_fresh_ = false;
  if (!state_.strings_sent) {
    if (!send_control(T(P::MsgType::StringTableAdd), x4mp::authority::encode_string_table_add(plan_.strings))) return fail(ctx, "StringTableAdd not sent");
    state_.strings_sent = true;
    state_.string_count = static_cast<std::uint32_t>(plan_.strings.size());
    spawn_strings_fresh_ = true;
    sent_macros_.clear();
    known_strings_.clear();
    for (const auto& str : plan_.strings) {
      known_strings_.push_back({str.kind, str.value, str.index});
      if (str.kind == static_cast<std::uint8_t>(P::StringKind::Macro)) sent_macros_.emplace_back(str.value, str.index);
    }
    persist();
  }
  nlohmann::json j;
  j["v"] = 1;
  j["name"] = save_name_;
  j["request_id"] = request_id_;
  j["display"] = "X4MP checkpoint " + save_name_.substr(10, 8);
  // M3-13 save hygiene: the authority universe must hold no ghost / client-side leftovers when the game writes the checkpoint. The check removes
  // what it finds (never an avatar, never a guarded id); a leftover that cannot be removed flags the save ghosts_cleaned=false (the server stores
  // it but never makes it current, ADR-023).
  const auto hygiene = JanitorFeature::checkpoint_check(ctx);
  ghosts_cleaned_ = hygiene.clean;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: SaveGame requested as {} (request {})", save_name_, request_id_);
  step_ = Step::WaitSaveReply;
  step_since_ = Clock::now();
  if (!ctx.platform.raise_lua("x4mp.auth_save", j.dump())) fail(ctx, "x4mp.auth_save could not be raised");
}

void AuthorityFlow::on_save_reply(host::HostContext& ctx, const std::string& text) {
  const auto doc = nlohmann::json::parse(text, nullptr, false);
  if (!doc.is_object() || !doc.value("ok", false)) {
    return fail(ctx, "SaveGame failed in Lua: " + (doc.is_object() ? doc.value("error", std::string("unknown")) : std::string("bad reply")));
  }
  double gt = doc.value("game_time", 0.0);  // Lua GetCurrentGameTime() sampled right before SaveGame (== MD player.age)
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(gt)) {
    const auto native = ctx.game.game_time();
    gt = native.value_or(0.0);
  }
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(gt)) return fail(ctx, "no valid game time for SaveStarted");
  game_time_ = gt;
  manifest_avatars_ = avatars::avatar_hub().manifest_records();  // the avatars exactly as the game saves them now
  const auto started = x4mp::authority::encode_save_started(request_id_, checkpoint_, game_time_, state_.next_net_id);
  if (!started || !send_control(T(P::MsgType::SaveStarted), *started)) return fail(ctx, "SaveStarted not sent");
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: SaveStarted sent (game_time {:.3f}, next_net_id {}); waiting for {}.xml.gz to be complete", game_time_,
            state_.next_net_id, save_name_);
  watcher_.emplace(save_dir_ / (save_name_ + ".xml.gz"));
  next_poll_ = Clock::now();
  step_ = Step::WaitFile;
  step_since_ = Clock::now();
}

void AuthorityFlow::step_wait_file(host::HostContext& ctx) {
  const auto now = Clock::now();
  if (now - step_since_ > kFileTimeout) return fail(ctx, "the save file was not complete after 180 s");
  if (now < next_poll_) return;
  next_poll_ = now + kFilePollEvery;
  if (!watcher_->poll(now)) return;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: save file complete ({} bytes) after {} ms; hashing", watcher_->size(),
            std::chrono::duration_cast<milliseconds>(now - step_since_).count());
  const fs::path save_path = watcher_->file();
  const fs::path work = work_dir_.empty() ? fs::temp_directory_path() / "x4mp-authority" : work_dir_;
  const auto cp = checkpoint_;
  const auto gt = game_time_;
  const auto next_id = state_.next_net_id;
  auto strings = plan_.strings;
  const auto sectors = plan_.sectors;
  const std::string name = save_name_;
  // M3-11: the avatars become manifest entries; their macro / faction strings join the manifest's own table
  std::vector<x4mp::authority::ManifestAvatar> avs;
  {
    std::uint32_t top = 0;
    for (const auto& s : strings) top = std::max(top, s.index);
    const auto ref = [&](std::uint8_t kind, const std::string& value) -> std::uint32_t {
      for (const auto& s : strings) {
        if (s.kind == kind && s.value == value) return s.index;
      }
      strings.push_back({++top, kind, value});
      return top;
    };
    for (const auto& r : manifest_avatars_) {
      x4mp::authority::ManifestAvatar m;
      m.net_id = r.net_id;
      m.kind = avatars::ship_kind_of_macro(r.macro);
      m.macro_ref = ref(static_cast<std::uint8_t>(P::StringKind::Macro), r.macro);
      m.owner_ref = ref(static_cast<std::uint8_t>(P::StringKind::Faction), r.owner);
      m.owner_team = r.team;
      m.owner_player = r.player_id;
      for (const auto& sec : sectors) {
        if (sec.macro == r.sector_macro) m.sector = sec.index;
      }
      m.idcode = r.idcode;
      m.x = static_cast<float>(r.pose.x);
      m.y = static_cast<float>(r.pose.y);
      m.z = static_cast<float>(r.pose.z);
      m.controller_player = r.online ? r.player_id : 0;
      avs.push_back(std::move(m));
    }
    if (!avs.empty()) X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: manifest lists {} avatar(s)", avs.size());
  }
  prepared_ = std::async(std::launch::async, [=]() -> Prepared {
    Prepared out;
    auto save = x4mp::authority::describe_upload_file(session::TransferKind::Save, save_path, name);
    if (!save) {
      out.error = "cannot read the save file";
      return out;
    }
    const auto manifest = x4mp::authority::encode_manifest(cp, gt, next_id, strings, sectors, avs);
    if (!manifest) {
      out.error = "invalid game time for the manifest";
      return out;
    }
    std::error_code ec;
    fs::create_directories(work, ec);
    out.manifest_path = work / (name + ".x4mf");
    {
      std::ofstream f(out.manifest_path, std::ios::binary | std::ios::trunc);
      f.write(reinterpret_cast<const char*>(manifest->data()), static_cast<std::streamsize>(manifest->size()));
      if (!f) {
        out.error = "cannot write the manifest";
        return out;
      }
    }
    auto mf = x4mp::authority::describe_upload_file(session::TransferKind::Manifest, out.manifest_path, "manifest");
    if (!mf) {
      out.error = "cannot read the manifest back";
      return out;
    }
    out.save = std::move(*save);
    out.manifest = std::move(*mf);
    out.ok = true;
    return out;
  });
  step_ = Step::Hashing;
}

void AuthorityFlow::step_hashing(host::HostContext& ctx) {
  if (!prepared_.valid()) return fail(ctx, "internal: no hash job");
  if (prepared_.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return;
  ready_ = prepared_.get();
  if (!ready_.ok) return fail(ctx, ready_.error);
  if (!send_control(T(P::MsgType::GalaxyMetadata), x4mp::authority::encode_galaxy_metadata(ready_.save.sha256, plan_.sectors, plan_.links))) {
    return fail(ctx, "GalaxyMetadata not sent");
  }
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: upload {} ({} bytes) + manifest ({} bytes)", hex12(ready_.save.sha256), ready_.save.size,
            ready_.manifest.size);
  x4mp::authority::UploadSpec spec;
  spec.checkpoint = checkpoint_;
  spec.files = {ready_.save, ready_.manifest};
  spec.ghosts_cleaned = ghosts_cleaned_;
  spec.step_timeout = seconds(60);
  if (!uploader_.start(std::move(spec))) return fail(ctx, "the upload job could not start (not connected?)");
  spawn_ship_ = collected_.ship();
  late_ship_ = false;
  ship_wait_ = false;  // this checkpoint decides again below (a newer collection supersedes an older ship question)
  ship_pending_ = false;
  spawn_plan_ = plan_;
  step_ = Step::Uploading;
}

void AuthorityFlow::step_uploading(host::HostContext& ctx) {
  const auto st = uploader_.stats();
  if (counted_stored_ < st.checkpoints_stored) {
    counted_stored_ = st.checkpoints_stored;
    on_checkpoint_stored(ctx);
    return;
  }
  if (const auto& r = uploader_.last_result();
      r && r->state == x4mp::authority::UploadState::Failed && !uploader_.pending() && !uploader_.running()) {
    fail(ctx, std::string("upload failed: ") + x4mp::authority::to_string(r->error) + " " + r->detail);
  }
}

void AuthorityFlow::on_checkpoint_stored(host::HostContext& ctx) {
  ++stored_total_;
  ++state_.checkpoints;
  state_.loaded_sha = ready_.save.sha256;  // this game is the source of that checkpoint
  {  // M3-22: the checkpoint joins the avatar lineage ledger: a later load of exactly this save keeps the avatars its manifest lists
    std::vector<std::string> idcodes;
    for (const auto& r : manifest_avatars_) {
      if (!r.idcode.empty()) idcodes.push_back(r.idcode);
    }
    avatars::avatar_hub().note_checkpoint(crypto::to_hex(std::span<const std::uint8_t>(ready_.save.sha256)), std::move(idcodes));
  }
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: checkpoint {} stored (save {}, request {})", save_name_, hex12(ready_.save.sha256), request_id_);
  std::error_code ec;
  fs::remove(ready_.manifest_path, ec);  // our own temporary manifest file
  if (!ledger_.empty()) {
    const auto removed = record_and_trim(ledger_, save_dir_, save_name_, kKeepCheckpointSaves);
    if (!removed.empty()) X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: removed {} old checkpoint save(s)", removed.size());
  }
  persist();
  step_ = Step::Idle;
  spawn_due_ = !state_.spawned;
}

void AuthorityFlow::maybe_spawn(host::HostContext& ctx) {
  if (state_.spawned) {
    spawn_due_ = false;
    return;
  }
  if (!spawn_ship_) {
    // Close-out A item 3: MD had no player ship when the checkpoint finished (the first one, ~2 s after the universe is ready:
    // player.occupiedship was still null; or the player stands in the cockpit after a load). Not a final answer: the spawn waits for the
    // pilot seat (M3-09). step_ship_wait asks MD once when the player sits (at once if they already do).
    spawn_due_ = false;
    if (!ship_wait_) {
      ship_wait_ = true;
      ship_pending_ = false;
      ship_asks_ = 0;
      ask_wait_frames_ = 0;
      ask_backoff_frames_ = 0;
      X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: no player ship was reported by MD with the checkpoint; the self-spawn waits for the pilot seat ({})",
                seated_ ? "the player sits: asking MD now" : "the player stands");
    }
    return;
  }
  if (!uploader_.connected()) return;  // sent when the link is back
  // The spawn happened "now": a late ship carries the game time of this moment (never 0); a ship of the checkpoint itself keeps the
  // checkpoint's game time.
  double spawn_time = game_time_;
  if (late_ship_) {
    const auto now_gt = ctx.game.game_time();
    if (now_gt && x4mp::authority::EntitySpawnBuilder::is_valid_game_time(*now_gt)) spawn_time = *now_gt;
  }
  if (!x4mp::authority::EntitySpawnBuilder::is_valid_game_time(spawn_time)) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "authority: no valid game time for the self-spawn; not sent");
    spawn_due_ = false;
    return;
  }
  std::uint32_t macro_ref = spawn_plan_.ship_macro_ref;
  std::uint16_t ship_sector = spawn_plan_.ship_sector;
  if (!spawn_strings_fresh_ || late_ship_) {
    // the server's string table is the one sent with an earlier checkpoint: this plan's indices are not necessarily its indices
    macro_ref = late_ship_macro_ref(ctx, spawn_ship_->macro);
    ship_sector = 0;
    for (const auto& sec : spawn_plan_.sectors) {
      if (sec.macro == spawn_ship_->sector) ship_sector = sec.index;
    }
  }
  x4mp::authority::EntitySpawnBuilder builder(spawn_time);
  x4mp::authority::SpawnEntity self;
  self.net_id = state_.next_net_id;
  self.kind = spawn_kind_for_class(spawn_ship_->cls);
  // M3-14 (found by the two-DLL pair run): the host's ship is a PLAYER ship like every avatar. Without origin=PlayerShip and the controller the server
  // would not drive this entity from the host's PlayerState (the clients would see a frozen, unnamed ship, or none) and the roster would show
  // no ship for the host. The name is what the clients show as the ghost label.
  const std::uint16_t host_id = session_.welcome().player_id;
  std::string host_name;
  if (const auto* row = chat::chat_hub().roster().find(host_id)) host_name = row->name;
  self.origin = x4mp::authority::SpawnOrigin::PlayerShip;
  self.owner_player = host_id;
  self.controller_player = host_id;
  self.owner_team = session_.welcome().team_id;
  self.macro_ref = macro_ref;
  self.owner_ref = spawn_plan_.player_faction_ref;  // "player" is always the first string, index 1, in every table we send
  self.name = host_name.empty() ? spawn_ship_->name : "[MP] " + host_name;
  self.idcode = spawn_ship_->idcode;
  self.sector = ship_sector;
  (void)builder.add(std::move(self));
  const auto payload = builder.build();
  if (!payload) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Error, "authority: self-spawn refused: {}", x4mp::authority::to_string(payload.error()));
    spawn_due_ = false;
    return;
  }
  (void)send_control(T(P::MsgType::EntitySpawn), *payload);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: self-spawn sent net_id={} game_time={:.3f}{}", state_.next_net_id, spawn_time,
            late_ship_ ? " (late: the ship was not known at the checkpoint)" : "");
  state_.spawned = true;
  state_.host_net_id = state_.next_net_id;  // the host's PlayerState carries it from now on (JoinFeature copies it to the selfship hub every frame)
  ++state_.next_net_id;
  spawn_due_ = false;
  ship_wait_ = false;
  persist();
}

// The ship macro as a string index the server knows: reuse one already sent, else append one (StringTableAdd with a single entry).
std::uint32_t AuthorityFlow::late_ship_macro_ref(host::HostContext& ctx, const std::string& macro) {
  for (const auto& [value, index] : sent_macros_) {
    if (value == macro) return index;
  }
  const std::uint32_t index = state_.string_count + 1;
  const std::vector<x4mp::authority::StringDesc> add = {{index, static_cast<std::uint8_t>(P::StringKind::Macro), macro}};
  if (!send_control(T(P::MsgType::StringTableAdd), x4mp::authority::encode_string_table_add(add))) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: could not add the ship macro to the string table");
    return 0;
  }
  state_.string_count = index;
  sent_macros_.emplace_back(macro, index);
  persist();
  return index;
}

void AuthorityFlow::note_seat(bool seated, std::uint32_t edges) noexcept {
  if (edges != seat_edges_) {  // a sit-down, stand-up or ship change: ask at the next opportunity, no back-off
    seat_edges_ = edges;
    ask_wait_frames_ = 0;
    ask_backoff_frames_ = 0;
  }
  seated_ = seated;
}

void AuthorityFlow::schedule_reask() noexcept {
  ask_backoff_frames_ = ask_backoff_frames_ == 0 ? kAskBackoffMin : std::min(ask_backoff_frames_ * 2, kAskBackoffMax);
  ask_wait_frames_ = ask_backoff_frames_;
}

// Item 3 / M3-09: the checkpoint is stored but MD reported no player ship. The seat decides: while the player stands nothing is asked
// (the sit-down edge asks); while they sit MD is asked once, then again only after a frame-counted back-off if it still says "none".
void AuthorityFlow::step_ship_wait(host::HostContext& ctx) {
  if (state_.spawned) {
    ship_wait_ = false;
    return;
  }
  if (!seated_) return;
  const auto now = Clock::now();
  if (ship_pending_) {
    if (now - ship_asked_ <= kShipAnswerTimeout) return;
    ship_pending_ = false;  // no answer at all: count it as "no ship"
    schedule_reask();
  }
  if (ask_wait_frames_ > 0) {
    --ask_wait_frames_;
    return;
  }
  if (!uploader_.connected()) return;
  ++ship_asks_;
  ship_pending_ = true;
  ship_asked_ = now;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "authority: the player sits in the pilot seat: asking MD for the ship (ask {})", ship_asks_);
  if (!ctx.platform.raise_lua("x4mp.auth_collect", R"({"v":1,"ship_only":true})")) {
    ship_pending_ = false;
    ship_wait_ = false;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "authority: the ship question could not be raised (Lua bridge missing)");
  }
}

}  // namespace x4mp::features::auth
