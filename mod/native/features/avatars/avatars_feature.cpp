#include "features/avatars/avatars_feature.h"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <cstdlib>
#include <fstream>
#include <mutex>
#include <set>
#include <sstream>

#include <nlohmann/json.hpp>

#include "core/ghost/registry.h"
#include "features/avatars/avatar_director.h"
#include "features/avatars/avatar_hub.h"
#include "features/avatars/avatars_client.h"
#include "features/diag/diag_hub.h"
#include "features/join/platform_stash.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "features/teams/team_hub.h"
#include "game/avatars_api.h"
#include "game/safe_remove.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::features {

using host::Cat;
using host::Level;
namespace av = avatars;
namespace P = X4MP::Proto;

namespace {
constexpr const char* kRecordsKey = "avatars.records";
constexpr const char* kRecordsFile = "avatar-records.txt";
constexpr std::size_t kInboxCap = 32;

game::PosRotPod to_pod(const av::Pose& p) {
  game::PosRotPod o;
  o.x = static_cast<float>(p.x);
  o.y = static_cast<float>(p.y);
  o.z = static_cast<float>(p.z);
  o.yaw = static_cast<float>(p.yaw);  // radians, as sampled (S13.4)
  o.pitch = static_cast<float>(p.pitch);
  o.roll = static_cast<float>(p.roll);
  return o;
}
av::Pose from_pod(const game::PosRotPod& o) { return {o.x, o.y, o.z, o.yaw, o.pitch, o.roll}; }
std::string id_text(std::uint64_t id) { return std::to_string(id); }

std::vector<std::string_view> split(std::string_view s, char sep) {
  std::vector<std::string_view> out;
  std::size_t pos = 0;
  while (true) {
    const auto i = s.find(sep, pos);
    if (i == std::string_view::npos) {
      out.push_back(s.substr(pos));
      break;
    }
    out.push_back(s.substr(pos, i - pos));
    pos = i + 1;
  }
  return out;
}
bool to_double(std::string_view s, double& out) {
  const std::string t(s);
  char* end = nullptr;
  out = std::strtod(t.c_str(), &end);
  return !t.empty() && end != nullptr && *end == '\0';
}
bool to_u32(std::string_view s, std::uint32_t& out) {
  const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
  return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}
}  // namespace

// ---------------------------------------------------------------------------------------------------------------------------
struct AvatarsFeature::Impl final : av::IAvatarEnv {
  host::HostContext* ctx = nullptr;
  std::unique_ptr<game::AvatarsApi> api;
  std::unique_ptr<join::PlatformStash> reg_stash, rec_stash;
  std::filesystem::path records_file;
  std::unique_ptr<av::AvatarDirector> dir;
  std::unique_ptr<av::ClientTakeover> client;  // M3-12: the client half (takeover)
  av::AvatarSettings settings;
  double now_s = 0;
  double next_stats_s = 5;
  av::DirectorStats last_stats{};
  bool inited = false;
  bool lost_authority_logged = false;

  std::mutex inbox_m;
  std::vector<std::string> inbox;

  // ---- IAvatarEnv: game ----
  bool game_ready() override {
    if (!ctx->gates.universe_ready || !api || !api->available()) return false;
    const auto* m = selfship::selfship_hub().map();
    return m != nullptr && m->ready();
  }
  bool factions_ready() override { return teams::team_hub().factions_ready(); }
  std::string faction_of_team(std::uint16_t team) override { return teams::team_hub().faction_of_team(team); }

  std::optional<av::HostPlace> host_place() override {
    const auto& g = ctx->game;
    const std::uint64_t cands[] = {g.player_occupied_ship(), g.player_controlled_ship(), selfship::selfship_hub().status().ship, g.player_container(),
                                   g.player_object()};
    for (const auto id : cands) {
      if (id == 0) continue;
      const auto sector = g.context_by_class(id, "sector", false);
      const auto pose = g.object_position(id);
      if (sector == 0 || !pose) continue;
      return av::HostPlace{sector, from_pod(*pose)};
    }
    return std::nullopt;
  }
  std::uint64_t sector_id_of_index(std::uint16_t index) override {
    const auto* m = selfship::selfship_hub().map();
    return m ? m->universe_id_of(index) : 0;
  }
  std::uint64_t sector_id_of_macro(const std::string& macro) override {
    const auto* m = selfship::selfship_hub().map();
    return m ? m->universe_id_of(m->index_of_macro(macro)) : 0;
  }
  std::string sector_macro_of_id(std::uint64_t id) override {
    const auto* m = selfship::selfship_hub().map();
    return m ? std::string(m->macro_of(m->index_of(id))) : std::string{};
  }
  std::uint16_t sector_index_of_macro(const std::string& macro) override {
    const auto* m = selfship::selfship_hub().map();
    return m ? m->index_of_macro(macro) : 0;
  }
  std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const av::Pose& pose, const std::string& owner) override {
    return ctx->game.spawn_object(macro.c_str(), sector, to_pod(pose), owner.c_str());
  }
  void activate(std::uint64_t id, bool active) override { ctx->game.activate_object(id, active); }
  bool valid(std::uint64_t id) override { return id != 0 && ctx->game.is_valid_component(id) && !ctx->game.component_wrecked(id); }
  std::string idcode(std::uint64_t id) override { return ctx->game.object_id_code(id).value_or(std::string{}); }
  bool read_pose(std::uint64_t id, std::uint64_t& sector, av::Pose& pose) override {
    const auto p = ctx->game.object_position(id);
    if (!p) return false;
    pose = from_pod(*p);
    sector = ctx->game.context_by_class(id, "sector", false);
    return sector != 0;
  }
  bool set_pose(std::uint64_t id, std::uint64_t sector, const av::Pose& pose) override {
    return ctx->game.set_object_sector_pos(id, sector, to_pod(pose));
  }
  bool remove(std::uint64_t id) override { return game::safe_remove(id) == game::RemoveResult::Removed; }
  std::optional<std::vector<av::Candidate>> team_candidates() override {
    const auto ships = api ? api->team_ships() : std::nullopt;
    if (!ships) return std::nullopt;
    std::vector<av::Candidate> out;
    std::set<std::uint64_t> seen;
    for (const auto& s : *ships) {
      if (!seen.insert(s.id).second) continue;
      av::Candidate c;
      c.id = s.id;
      c.owner = s.faction;
      c.name = ctx->game.component_name(s.id).value_or(std::string{});
      c.idcode = ctx->game.object_id_code(s.id).value_or(std::string{});
      if (const auto p = ctx->game.object_position(s.id)) c.pose = from_pod(*p);
      out.push_back(std::move(c));
    }
    return out;
  }

  // ---- MD through Lua ----
  void ask_safepos(std::uint32_t seq, std::uint64_t sector, const av::Pose& w, double radius) override {
    nlohmann::json j;
    j["v"] = 1;
    j["seq"] = seq;
    j["sector"] = id_text(sector);
    j["x"] = w.x;
    j["y"] = w.y;
    j["z"] = w.z;
    j["radius"] = radius;
    if (!ctx->platform.raise_lua("x4mp.avatars_safepos", j.dump())) ctx->log.raw(Cat::Md, Level::Warn, "avatars: x4mp.avatars_safepos could not be raised (Lua bridge)");
  }
  void ask_dress(std::uint32_t seq, std::uint64_t id, const std::string& name, const av::StarterSpec& st, int min_hull) override {
    nlohmann::json j;
    j["v"] = 1;
    j["seq"] = seq;
    j["id"] = id_text(id);
    j["name"] = name;
    j["min_hull"] = min_hull;
    j["macro"] = st.macro;
    j["loadout"] = st.loadout;
    j["basic"] = st.basic_loadout;
    if (!ctx->platform.raise_lua("x4mp.avatars_dress", j.dump())) ctx->log.raw(Cat::Md, Level::Warn, "avatars: x4mp.avatars_dress could not be raised (Lua bridge)");
  }
  void send_velocity(const std::vector<av::VelHint>& hints) override {
    nlohmann::json j;
    j["v"] = 1;
    auto& h = j["h"] = nlohmann::json::array();
    for (const auto& x : hints) h.push_back(nlohmann::json::array({id_text(x.id), x.vx, x.vy, x.vz}));
    (void)ctx->platform.raise_lua("x4mp.avatars_vel", j.dump());
  }

  // ---- session ----
  bool net_ready() override {
    const auto& hub = av::avatar_hub();
    return hub.authority() && hub.services().connected && hub.services().connected();
  }
  std::uint32_t alloc_net_id() override { return av::avatar_hub().services().alloc_net_id(); }
  std::uint32_t string_ref(av::StrKind kind, const std::string& value) override { return av::avatar_hub().services().string_ref(kind, value); }
  double game_time() override { return ctx->game.game_time().value_or(0.0); }
  bool send_spawn(const std::vector<authority::SpawnEntity>& entities, double gt) override {
    authority::EntitySpawnBuilder b(gt);
    for (const auto& e : entities) (void)b.add(e);
    const auto payload = b.build();
    if (!payload) return false;
    return av::avatar_hub().services().send_control(static_cast<std::uint16_t>(P::MsgType::EntitySpawn), *payload);
  }
  bool send_controller(std::uint32_t net_id, std::uint16_t player) override {
    return av::avatar_hub().services().send_control(static_cast<std::uint16_t>(P::MsgType::EntityChange), av::encode_controller_change(net_id, player));
  }

  // ---- persistence, log ----
  void save_records(const std::string& text) override {
    if (rec_stash) rec_stash->put(kRecordsKey, text);
    if (!records_file.empty()) {
      const auto tmp = records_file;
      auto t = tmp;
      t += ".tmp";
      std::error_code ec;
      {
        std::ofstream f(t, std::ios::binary | std::ios::trunc);
        f << text;
      }
      std::filesystem::rename(t, records_file, ec);
      if (ec) {  // rename over an existing file can fail on Windows: fall back to a direct write
        std::ofstream f(records_file, std::ios::binary | std::ios::trunc);
        f << text;
      }
    }
    if (reg_stash && dir) {  // the AvatarRegistry / NetMap mirror (core/ghost) for same-universe adoption
      ghost::Registries regs;
      for (const auto& v : dir->views()) {
        if (v.rec.net_id == 0 || v.local_id == 0) continue;
        regs.netmap.bind(v.rec.net_id, v.local_id, ghost::NetKind::Avatar, v.rec.player_id);
        regs.avatars.add({v.rec.player_id, v.rec.net_id, v.local_id});
      }
      regs.save(*reg_stash, 0);
    }
  }
  void log(av::LogLevel level, const std::string& text) override {
    const Level l = level == av::LogLevel::Error ? Level::Error : level == av::LogLevel::Warn ? Level::Warn : level == av::LogLevel::Info ? Level::Info : Level::Debug;
    ctx->log.raw(Cat::Ghost, l, text);
  }

  // ---- the services the authority flow provides ----
  void apply_inputs(const av::HubInputs& in) {
    for (const auto& s : in.settings) {
      for (const auto& [k, v] : s.entries) settings.apply(k, v);
    }
    if (!in.settings.empty()) dir->set_settings(settings);
    if (in.welcomed) dir->on_welcome();
    if (in.session_ended) lost_authority_logged = false;
    for (const auto& r : in.rosters) dir->on_roster(r);
    for (const auto& s : in.ships) dir->on_player_ship(s);
    for (const auto& d : in.despawns) dir->on_despawn(d);
  }

  void handle_md(const std::string& raw) {
    std::string data;
    const auto j = nlohmann::json::parse(raw, nullptr, false);
    if (j.is_object() && j.contains("data") && j["data"].is_string()) data = j["data"].get<std::string>();
    const auto f = split(data, ';');
    std::uint32_t seq = 0;
    if (f.size() < 3 || !to_u32(f[1], seq)) return;
    if (f[0] == "P") {  // P;seq;ok;x;y;z
      av::Pose p;
      const bool ok = f[2] == "1" && f.size() >= 6 && to_double(f[3], p.x) && to_double(f[4], p.y) && to_double(f[5], p.z);
      dir->on_safepos(seq, ok, p);
    } else if (f[0] == "D") {  // D;seq;ok;detail
      dir->on_dress(seq, f[2] == "1", f.size() > 3 ? std::string(f[3]) : std::string{});
    }
  }
};

AvatarsFeature::AvatarsFeature() : impl_(std::make_unique<Impl>()) {}
AvatarsFeature::~AvatarsFeature() = default;

void AvatarsFeature::on_init(host::HostContext& ctx) {
  auto& s = *impl_;
  s.ctx = &ctx;
  s.api = std::make_unique<game::AvatarsApi>([&ctx](const char* n) { return ctx.platform.get_game_function(n); });
  s.reg_stash = std::make_unique<join::PlatformStash>(ctx.platform, "");
  s.rec_stash = std::make_unique<join::PlatformStash>(ctx.platform, "");
  if (ctx.paths != nullptr && !ctx.paths->dir.empty()) s.records_file = ctx.paths->dir / kRecordsFile;
  s.dir = std::make_unique<av::AvatarDirector>(s);

  // the identity records of an earlier run: the stash first (survives reloads), else the file (survives a game restart)
  std::string text = s.rec_stash->get(kRecordsKey).value_or(std::string{});
  if (text.empty() && !s.records_file.empty()) {
    std::ifstream f(s.records_file, std::ios::binary);
    std::ostringstream ss;
    ss << f.rdbuf();
    text = ss.str();
  }
  std::vector<std::pair<std::uint16_t, std::uint64_t>> hints;
  ghost::Registries regs;
  const auto adopted = regs.adopt(*s.reg_stash, 0, [](ghost::LocalId) { return true; });  // validity is decided by the binder (idcode + name)
  if (adopted.found) {
    for (const auto& a : regs.avatars.entries()) hints.emplace_back(a.player_id, a.local_id);
  }
  const auto parsed = av::records_from_text(text);
  if (!parsed.records.empty()) {
    s.dir->load_records(parsed.records, hints);
    ctx.log.raw(Cat::Ghost, Level::Info, "avatars: " + std::to_string(parsed.records.size()) + " avatar record(s) restored (" + std::to_string(parsed.bad_lines) + " bad lines); the binder runs when the universe is ready");
  }
  s.client = std::make_unique<av::ClientTakeover>();
  s.client->init(ctx, [self = &s]() { return self->team_candidates(); });
  av::avatar_hub().set_max_net_id(s.dir->max_net_id());
  av::avatar_hub().set_snapshot_fn([&s]() { return s.dir->snapshot(); });
  // M3-13: the janitor and the checkpoint check never remove an avatar: the ids the binder bound and the idcodes of every record
  av::avatar_hub().set_protect_fn([&s]() {
    av::AvatarProtect p;
    if (!s.dir) return p;
    for (const auto& v : s.dir->views()) {
      if (v.local_id != 0) p.ids.push_back(v.local_id);
      if (!v.rec.idcode.empty()) p.idcodes.push_back(v.rec.idcode);
    }
    return p;
  });
  Impl* self = &s;
  (void)ctx.platform.subscribe_event("x4mp.avatars_md", [self](std::string_view t) {
    if (t.size() > 1024) return;
    const std::lock_guard lock(self->inbox_m);
    if (self->inbox.size() >= kInboxCap) self->inbox.erase(self->inbox.begin());
    self->inbox.emplace_back(t);
  });
  s.inited = true;
}

void AvatarsFeature::on_game_loaded(host::HostContext& ctx) {
  auto& s = *impl_;
  s.ctx = &ctx;
  if (s.dir) s.dir->new_universe();  // every local id is void
  if (s.client) s.client->game_loaded(ctx);
}

void AvatarsFeature::on_shutdown(host::HostContext& ctx) {
  auto& s = *impl_;
  s.ctx = &ctx;
  av::avatar_hub().set_snapshot_fn({});
  av::avatar_hub().set_protect_fn({});
  av::avatar_hub().set_takeover_status({});
  if (s.client) s.client->shutdown(ctx);
  if (s.dir && s.dir->size() > 0) s.dir->persist_now();
}

void AvatarsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  auto& s = *impl_;
  s.ctx = &ctx;
  if (!s.inited) return;
  s.now_s += info.delta_s;
  auto& hub = av::avatar_hub();
  auto in = hub.take_inputs();
  s.apply_inputs(in);
  std::vector<std::string> md;
  {
    const std::lock_guard lock(s.inbox_m);
    md.swap(s.inbox);
  }
  for (const auto& m : md) s.handle_md(m);
  if (!hub.authority()) {  // clients have no avatars of their own to drive: they take theirs over (M3-12)
    if (s.client) {
      s.client->apply(in, in.settings.empty() ? nullptr : &s.settings);
      s.client->frame(ctx, s.now_s);
      av::TakeoverStatus ts;  // M3-13: the janitor waits for Done and spares the takeover's own objects
      ts.active = hub.client_linked();
      ts.done = s.client->done();
      ts.avatar_id = s.client->avatar_id();
      ts.host_id = s.client->host_id();
      hub.set_takeover_status(ts);
    }
    return;
  }
  std::int64_t server_now = 0;
  bool have_clock = selfship::selfship_hub().server_now(server_now);
  if (!have_clock) {  // the same estimate from the net layer (local steady clock + the applied offset)
    NetSample ns;
    if (diag_hub().sample_net(ns) && ns.clock_offset_us != 0) {
      server_now = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count() + ns.clock_offset_us;
      have_clock = true;
    }
  }
  if (!have_clock && !in.states.empty()) server_now = in.states.back().sample_time_us + 100000;
  for (const auto& st : in.states) s.dir->on_player_state(st, have_clock ? server_now : st.sample_time_us + 100000);
  s.dir->step(s.now_s, server_now);
  if (s.now_s >= s.next_stats_s) {  // one line per 5 s while avatars exist and something changed (the [sync] numbers of the authority's half)
    s.next_stats_s = s.now_s + 5.0;
    const auto& st = s.dir->stats();
    if (s.dir->size() > 0 && (st.states_in != s.last_stats.states_in || st.set_pose_calls != s.last_stats.set_pose_calls)) {
      ctx.log.raw(Cat::Ghost, Level::Info,
                  "avatars: [drive] avatars=" + std::to_string(s.dir->size()) + " live=" + std::to_string(s.dir->live_count()) + " states_in=" + std::to_string(st.states_in) +
                      " unknown_net_id=" + std::to_string(st.states_unknown) + " set_pose=" + std::to_string(st.set_pose_calls) + " unmapped_sector=" +
                      std::to_string(st.unmapped_sector) + " vel_hints=" + std::to_string(st.vel_hints) + " repairs=" + std::to_string(st.repairs));
    }
    s.last_stats = st;
  }
  hub.set_max_net_id(s.dir->max_net_id());
  if (hub.services().reserve_net_ids_above) hub.services().reserve_net_ids_above(s.dir->max_net_id());
}

}  // namespace x4mp::features
