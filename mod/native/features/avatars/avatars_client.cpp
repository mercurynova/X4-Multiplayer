#include "features/avatars/avatars_client.h"

#include <array>
#include <fstream>
#include <iterator>
#include <sstream>

#include <nlohmann/json.hpp>

#include "core/crypto/crypto.h"
#include "features/avatars/avatar_takeover.h"
#include "features/diag/knowledge_feature.h"
#include "features/ghosts/ghost_hub.h"
#include "features/join/platform_stash.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "features/teams/team_hub.h"
#include "game/safe_remove.h"
#include "message_ids_generated.h"

namespace x4mp::features::avatars {

using host::Cat;
using host::Level;
namespace P = X4MP::Proto;

namespace {
constexpr const char* kRecordKey = "avatars.takeover";
constexpr const char* kManifestKey = "avatars.manifest_file";

game::PosRotPod to_pod(const Pose& p) {
  game::PosRotPod o;
  o.x = static_cast<float>(p.x);
  o.y = static_cast<float>(p.y);
  o.z = static_cast<float>(p.z);
  o.yaw = static_cast<float>(p.yaw);
  o.pitch = static_cast<float>(p.pitch);
  o.roll = static_cast<float>(p.roll);
  return o;
}
}  // namespace

struct ClientTakeover::Impl final : ITakeoverEnv {
  host::HostContext* ctx = nullptr;
  std::unique_ptr<join::PlatformStash> stash;
  std::function<std::optional<std::vector<Candidate>>()> candidates;
  std::unique_ptr<AvatarTakeover> machine;
  AvatarSettings settings;
  AvatarTakeover::Stage last_stage = AvatarTakeover::Stage::Done;
  bool stage_announced = false;
  std::uint32_t last_refusals = 0;

  // ---- node ----
  bool ready() override {
    if (!ctx->gates.universe_ready || !avatar_hub().client_linked()) return false;
    const auto* m = selfship::selfship_hub().map();
    return m != nullptr && m->ready();
  }
  std::uint16_t own_player_id() override {
    const auto& l = avatar_hub().client_link();
    return l.player_id ? l.player_id() : 0;
  }
  std::uint64_t own_ship() override { return selfship::selfship_hub().status().ship; }
  std::uint64_t seated_ship() override {
    const auto occ = ctx->game.player_occupied_ship();
    return occ != 0 ? occ : ctx->game.player_controlled_ship();
  }
  std::uint32_t sit_downs() override { return selfship::selfship_hub().status().sit_downs; }
  // ---- game ----
  bool valid(std::uint64_t id) override { return id != 0 && ctx->game.is_valid_component(id) && !ctx->game.component_wrecked(id); }
  std::string idcode(std::uint64_t id) override { return ctx->game.object_id_code(id).value_or(std::string{}); }
  std::optional<std::vector<Candidate>> team_candidates() override {
    auto all = candidates ? candidates() : std::nullopt;
    if (all) std::erase_if(*all, [](const Candidate& c) { return ghosts::ghost_hub().is_ghost_local(c.id); });  // ghosts are not avatar copies
    return all;
  }
  std::string faction_of_team(std::uint16_t team) override { return teams::team_hub().faction_of_team(team); }
  std::uint64_t sector_id_of_index(std::uint16_t index) override {
    const auto* m = selfship::selfship_hub().map();
    return m ? m->universe_id_of(index) : 0;
  }
  std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const Pose& pose, const std::string& owner) override {
    return ctx->game.spawn_object(macro.c_str(), sector, to_pod(pose), owner.c_str());
  }
  bool set_owner(std::uint64_t id, const std::string& faction) override { return ctx->game.set_component_owner(id, faction.c_str()); }
  void activate(std::uint64_t id, bool active) override { ctx->game.activate_object(id, active); }
  std::optional<std::string> can_teleport(std::uint64_t id) override { return ctx->game.can_teleport_player_to(id, true, true); }
  bool teleport(std::uint64_t id) override { return ctx->game.teleport_player_to(id, true, true, true); }
  bool remove(std::uint64_t id) override {
    if (ghosts::ghost_hub().is_ghost_local(id)) return false;  // a ghost is never a copy to remove (belt and braces: the hold keeps them apart)
    return game::safe_remove(id) == game::RemoveResult::Removed;
  }
  // ---- session ----
  bool send_request(std::uint64_t host_ship) override {
    const auto& link = avatar_hub().client_link();
    if (!link.send_control) return false;
    PlayerShipReq r;
    r.name = ctx->game.component_name(host_ship).value_or(std::string{});
    r.idcode = idcode(host_ship);
    r.sector = selfship::selfship_hub().status().sector;
    if (const auto p = ctx->game.object_position(host_ship)) r.pose = {p->x, p->y, p->z, p->yaw, p->pitch, p->roll};
    std::array<std::uint8_t, 16> key{};
    std::uint64_t lo = 0, hi = 0;
    if (crypto::random_bytes(key)) {
      for (int i = 0; i < 8; ++i) {
        lo = (lo << 8) | key[static_cast<std::size_t>(i)];
        hi = (hi << 8) | key[static_cast<std::size_t>(8 + i)];
      }
    }
    return link.send_control(static_cast<std::uint16_t>(P::MsgType::PlayerShip), encode_player_ship(r, lo, hi, host_ship));
  }
  void set_own_net_id(std::uint32_t net_id) override { selfship::selfship_hub().set_own_net_id(net_id); }
  void hold_states(bool hold) override { selfship::selfship_hub().set_state_hold(hold); }
  void hold_ghosts(bool hold) override { ghosts::ghost_hub().set_spawn_hold(hold); }
  // ---- UI, persistence, log ----
  void show_hint(bool show, const std::string& text) override {
    nlohmann::json j;
    j["v"] = 1;
    j["id"] = "takeover";
    j["show"] = show;
    j["text"] = text;
    (void)ctx->platform.raise_lua("x4mp.hint", j.dump());
  }
  void probe(const std::string& tag) override { knowledge_probe(tag); }  // M3-23
  void save_record(const std::string& text) override {
    if (stash) stash->put(kRecordKey, text);
  }
  void log(LogLevel level, const std::string& text) override {
    const Level l = level == LogLevel::Error ? Level::Error : level == LogLevel::Warn ? Level::Warn : level == LogLevel::Info ? Level::Info : Level::Debug;
    ctx->log.raw(Cat::Ghost, l, text);
  }

  void announce(double now_s) {
    (void)now_s;
    const auto& st = machine->stats();
    const auto stage = machine->stage();
    if (stage_announced && stage == last_stage && st.refusals == last_refusals) return;
    stage_announced = true;
    last_stage = stage;
    last_refusals = st.refusals;
    nlohmann::json j;
    j["v"] = 1;
    j["stage"] = AvatarTakeover::stage_name(stage);
    j["net_id"] = machine->net_id();
    j["avatar"] = std::to_string(machine->avatar_id());
    j["host"] = std::to_string(machine->host_id());
    j["hint"] = machine->hint_shown();
    j["requests"] = st.requests;
    j["bound"] = st.bound;
    j["spawned"] = st.spawned;
    j["refusals"] = st.refusals;
    j["teleports"] = st.teleports;
    j["removed"] = st.removed;
    (void)ctx->platform.raise_lua("x4mp.takeover", j.dump());
  }

  void read_manifest(const std::string& path) {
    if (path.empty()) return;
    std::ifstream f(path, std::ios::binary);
    if (!f) {
      ctx->log.raw(Cat::Ghost, Level::Info, "takeover: the checkpoint manifest is not readable (" + path + "); other avatar copies are found by their name only");
      return;
    }
    const std::vector<std::uint8_t> bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    const auto avatars = decode_manifest_avatars(bytes);
    if (!avatars) {
      ctx->log.raw(Cat::Ghost, Level::Warn, "takeover: the checkpoint manifest could not be read (not a verifiable manifest)");
      return;
    }
    machine->set_manifest_avatars(*avatars);
    ctx->log.raw(Cat::Ghost, Level::Info, "takeover: the checkpoint manifest names " + std::to_string(avatars->size()) + " avatar(s)");
  }
};

ClientTakeover::ClientTakeover() : impl_(std::make_unique<Impl>()) {}
ClientTakeover::~ClientTakeover() = default;

void ClientTakeover::init(host::HostContext& ctx, std::function<std::optional<std::vector<Candidate>>()> team_candidates) {
  auto& s = *impl_;
  s.ctx = &ctx;
  s.candidates = std::move(team_candidates);
  s.stash = std::make_unique<join::PlatformStash>(ctx.platform, "");
  s.machine = std::make_unique<AvatarTakeover>(s);
  if (const auto text = s.stash->get(kRecordKey)) {
    if (const auto rec = takeover_record_from_text(*text)) s.machine->load_record(*rec);
  }
  // the manifest of the session save this node downloaded (the join feature tells the hub; a /reloadui loses that, so the path is kept in the stash)
  auto& hub = avatar_hub();
  if (hub.manifest_file().empty()) {
    if (const auto p = s.stash->get(kManifestKey)) hub.set_manifest_file(*p);
  }
}

void ClientTakeover::apply(const HubInputs& in, const AvatarSettings* settings) {
  auto& s = *impl_;
  if (!s.machine) return;
  if (settings) {
    s.settings = *settings;
    s.machine->set_settings(s.settings);
  }
  if (in.session_ended) s.machine->session_ended();
  if (!in.avatar_spawns.empty()) s.machine->on_spawn_avatars(in.avatar_spawns);
}

void ClientTakeover::frame(host::HostContext& ctx, double now_s) {
  auto& s = *impl_;
  s.ctx = &ctx;
  if (!s.machine) return;
  auto& hub = avatar_hub();
  if (hub.take_manifest_dirty()) {
    if (s.stash) {
      if (hub.manifest_file().empty()) s.stash->erase(kManifestKey);
      else s.stash->put(kManifestKey, hub.manifest_file());
    }
    s.read_manifest(hub.manifest_file());
  }
  s.machine->set_diag(ctx.config.diag);  // M3-23
  s.machine->step(now_s);
  s.announce(now_s);
}

void ClientTakeover::game_loaded(host::HostContext& ctx) {
  auto& s = *impl_;
  s.ctx = &ctx;
  if (s.machine) {
    s.machine->restart();
    s.stage_announced = false;
  }
}

void ClientTakeover::shutdown(host::HostContext& ctx) {
  impl_->ctx = &ctx;
}

bool ClientTakeover::done() const noexcept { return impl_->machine && impl_->machine->done(); }
std::uint64_t ClientTakeover::avatar_id() const noexcept { return impl_->machine ? impl_->machine->avatar_id() : 0; }
std::uint64_t ClientTakeover::host_id() const noexcept { return impl_->machine ? impl_->machine->host_id() : 0; }
const char* ClientTakeover::stage_name() const noexcept { return impl_->machine ? AvatarTakeover::stage_name(impl_->machine->stage()) : "none"; }

}  // namespace x4mp::features::avatars
