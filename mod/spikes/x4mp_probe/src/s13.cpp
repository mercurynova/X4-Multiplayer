// x4mp_probe S13 blocks (M3-001). See s13.h and ../README.md. Throwaway: clarity and logging over polish.
//
// Rules this file keeps (session 2/3 ground truth): game calls only from tick() (on_frame_update); never pause or sleep;
// no hooks, no pinning; removals only through remove_checked() (a copy of the product guard); the player's own ship and
// anything in the guard set is never removed.

#include "s13.h"

#include <x4n_core.h>
#include <x4n_events.h>

#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cstdio>
#include <format>
#include <fstream>
#include <memory>
#include <mutex>
#include <nlohmann/json.hpp>
#include <optional>
#include <sstream>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace x4mp_probe::s13 {

// =====================================================================================================================
// Pure helpers
// =====================================================================================================================
Vec3 forward_from_angles(double yaw, double pitch, int pitch_sign) {
  const double cp = std::cos(pitch);
  return {std::sin(yaw) * cp, std::sin(pitch) * (pitch_sign < 0 ? -1.0 : 1.0), std::cos(yaw) * cp};
}
Vec3 right_from_yaw(double yaw) { return {std::cos(yaw), 0.0, -std::sin(yaw)}; }

Vec3 path_position(const Path& p, double t) {
  if (p.kind == Path::Kind::Circle) {
    const double w = p.speed / p.radius;  // rad/s
    return {p.center.x + p.radius * std::cos(w * t), p.center.y, p.center.z + p.radius * std::sin(w * t)};
  }
  return p.center + p.dir * (p.speed * t);  // line: t is measured from the line's own start (see path_velocity)
}
Vec3 path_velocity(const Path& p, double t, double) {
  if (p.kind == Path::Kind::Circle) {
    const double w = p.speed / p.radius;
    return {-p.speed * std::sin(w * t), 0.0, p.speed * std::cos(w * t)};
  }
  return p.dir * p.speed;
}

BlockName parse_block(std::string_view s) {
  std::string t;
  for (const char ch : s) t.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
  while (!t.empty() && std::isspace(static_cast<unsigned char>(t.front()))) t.erase(t.begin());
  while (!t.empty() && std::isspace(static_cast<unsigned char>(t.back()))) t.pop_back();
  BlockName r;
  const auto sp = t.find_first_of(" \t");
  if (sp != std::string::npos) {
    r.name = t.substr(0, sp);
    r.arg = t.substr(t.find_first_not_of(" \t", sp));
  } else {
    r.name = t;
  }
  // "ghost_motion_a" style alias.
  if (r.arg.empty() && r.name.size() > 13 && r.name.starts_with("ghost_motion_")) {
    r.arg = r.name.substr(13);
    r.name = "ghost_motion";
  }
  return r;
}

namespace {
constexpr std::array<std::string_view, 15> kBlocks = {"ghost_spawn", "ghost_motion", "ghost_xsector", "sample", "seat", "takeover", "takeover_docked",
                                                      "persist_spawn", "persist_check", "seta", "pause_move", "cleanup", "s13_check",
                                                      "s13_stop", "s13_status"};
}
bool is_s13_block(std::string_view name) { return std::find(kBlocks.begin(), kBlocks.end(), name) != kBlocks.end(); }

std::string normalize_save_name(std::string_view s) {
  std::string t;
  for (const char ch : s) t.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
  while (!t.empty() && std::isspace(static_cast<unsigned char>(t.front()))) t.erase(t.begin());
  while (!t.empty() && std::isspace(static_cast<unsigned char>(t.back()))) t.pop_back();
  for (const std::string_view ext : {".xml.gz", ".xml"}) {
    if (t.size() > ext.size() && t.compare(t.size() - ext.size(), ext.size(), ext) == 0) {
      t.resize(t.size() - ext.size());
      break;
    }
  }
  return t;
}
bool is_scratch_slot(std::string_view current, std::string_view configured) {
  const std::string c = normalize_save_name(configured);
  return !c.empty() && normalize_save_name(current) == c;
}

RemoveVerdict RemoveGate::check(std::uint64_t id, bool game_says_valid) const {
  if (id == 0) return RemoveVerdict::BlockedInvalidId;
  if (std::find(guard.begin(), guard.end(), id) != guard.end()) return RemoveVerdict::BlockedGuard;
  if (!game_says_valid) return RemoveVerdict::BlockedNotValid;
  return RemoveVerdict::Allowed;
}
const char* to_string(RemoveVerdict v) {
  switch (v) {
    case RemoveVerdict::Allowed: return "allowed";
    case RemoveVerdict::BlockedInvalidId: return "blocked_invalid_id";
    case RemoveVerdict::BlockedGuard: return "blocked_by_guard";
    case RemoveVerdict::BlockedNotValid: return "blocked_not_valid";
  }
  return "?";
}

Vec3 interpolate_keys(const std::vector<Vec3>& keys, double key_dt, double t) {
  if (keys.empty()) return {};
  if (t <= 0.0 || keys.size() == 1) return keys.front();
  const double f = t / key_dt;
  const auto i = static_cast<std::size_t>(f);
  if (i + 1 >= keys.size()) return keys.back();
  return lerp(keys[i], keys[i + 1], f - static_cast<double>(i));
}

// =====================================================================================================================
// Game access (UI thread only). Every call is SEH-guarded and logs a missing function once.
// =====================================================================================================================
namespace {

using u64 = std::uint64_t;
namespace fs = std::filesystem;

std::int64_t qpc() {
  LARGE_INTEGER v;
  QueryPerformanceCounter(&v);
  return v.QuadPart;
}
double qpc_freq() {
  static const double f = [] {
    LARGE_INTEGER v;
    QueryPerformanceFrequency(&v);
    return static_cast<double>(v.QuadPart);
  }();
  return f;
}
double ms_between(std::int64_t a, std::int64_t b) { return 1000.0 * static_cast<double>(b - a) / qpc_freq(); }
double us_between(std::int64_t a, std::int64_t b) { return 1.0e6 * static_cast<double>(b - a) / qpc_freq(); }

struct Ghost {
  u64 id = 0;
  std::string idcode, kind, macro, owner;
};

struct State {
  Env env;
  Config cfg;
  std::string tag = "S13";
  std::string block_name;
  std::vector<Ghost> reg;
  bool reg_loaded = false;
  std::vector<std::string> missing_logged;
  std::atomic<std::uint64_t> native_frames{0};
  std::int64_t last_tick = 0;
  std::int64_t last_tick_prev = 0;
};
State g;

struct Block;
std::unique_ptr<Block> g_blk;

// ---- logging ----
void out(int lvl, const std::string& body) {
  if (g.env.log) g.env.log(lvl, body);
}
// verdict: PASS | FAIL | INFO | WARN | REFUSED
void say(const char* verdict, const std::string& msg) {
  const int lvl = (std::string_view(verdict) == "FAIL" || std::string_view(verdict) == "WARN" || std::string_view(verdict) == "REFUSED") ? 2 : 1;
  out(lvl, std::format("{} block={} {} {}", g.tag, g.block_name, verdict, msg));
}

// ---- SEH ----
bool seh_run(void (*fn)(void*), void* ctx, unsigned long* code) {
  __try {
    fn(ctx);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    *code = GetExceptionCode();
    return false;
  }
}
template <class F>
bool guarded(const char* what, F&& f) {
  struct Ctx { F* f; } c{&f};
  unsigned long code = 0;
  const bool ok = seh_run([](void* p) { (*static_cast<Ctx*>(p)->f)(); }, &c, &code);
  if (!ok) say("WARN", std::format("seh call={} exception_code=0x{:08x}", what, code));
  return ok;
}

X4GameFunctions* G() { return x4n::game(); }
#define HAVE(name) (G() != nullptr && G()->name != nullptr)

void missing(const char* name) {
  if (std::find(g.missing_logged.begin(), g.missing_logged.end(), name) != g.missing_logged.end()) return;
  g.missing_logged.emplace_back(name);
  say("FAIL", std::format("MISSING native function={} (not in the X4Native game table for this build)", name));
}

template <class R, class F>
R gcall(const char* name, bool present, R def, F&& f) {
  if (!present) {
    missing(name);
    return def;
  }
  R r = def;
  if (!guarded(name, [&] { r = f(); })) return def;
  return r;
}

u64 occupied() { return gcall("GetPlayerOccupiedShipID", HAVE(GetPlayerOccupiedShipID), u64{0}, [] { return G()->GetPlayerOccupiedShipID(); }); }
u64 controlled() { return gcall("GetPlayerControlledShipID", HAVE(GetPlayerControlledShipID), u64{0}, [] { return G()->GetPlayerControlledShipID(); }); }
u64 player_object() { return gcall("GetPlayerObjectID", HAVE(GetPlayerObjectID), u64{0}, [] { return G()->GetPlayerObjectID(); }); }
u64 player_id() { return gcall("GetPlayerID", HAVE(GetPlayerID), u64{0}, [] { return G()->GetPlayerID(); }); }
u64 container() { return gcall("GetPlayerContainerID", HAVE(GetPlayerContainerID), u64{0}, [] { return G()->GetPlayerContainerID(); }); }
u64 ctx(u64 id, const char* cls, bool self) {
  if (id == 0) return 0;
  return gcall("GetContextByClass", HAVE(GetContextByClass), u64{0}, [&] { return G()->GetContextByClass(id, cls, self); });
}
bool valid(u64 id) {
  if (id == 0) return false;
  return gcall("IsValidComponent", HAVE(IsValidComponent), false, [&] { return G()->IsValidComponent(id); });
}
bool seta() { return gcall("IsSetaActive", HAVE(IsSetaActive), false, [] { return G()->IsSetaActive(); }); }
bool game_paused() { return gcall("IsGamePaused", HAVE(IsGamePaused), false, [] { return G()->IsGamePaused(); }); }
bool occupied_docked() { return gcall("IsPlayerOccupiedShipDocked", HAVE(IsPlayerOccupiedShipDocked), false, [] { return G()->IsPlayerOccupiedShipDocked(); }); }
double game_time() { return gcall("GetCurrentGameTime", HAVE(GetCurrentGameTime), -1.0, [] { return G()->GetCurrentGameTime(); }); }
std::string idcode_of(u64 id) {
  return gcall("GetObjectIDCode", HAVE(GetObjectIDCode), std::string(), [&] {
    const char* p = G()->GetObjectIDCode(id);
    return std::string(p ? p : "");  // the game returns a shared buffer: copy at once
  });
}
std::string name_of(u64 id) {
  return gcall("GetComponentName", HAVE(GetComponentName), std::string(), [&] {
    const char* p = G()->GetComponentName(id);
    return std::string(p ? p : "");
  });
}
std::string class_of(u64 id) {
  return gcall("GetComponentClass", HAVE(GetComponentClass), std::string(), [&] {
    const char* p = G()->GetComponentClass(id);
    return std::string(p ? p : "");
  });
}
std::string owner_of(u64 id) {
  return gcall("GetOwnerDetails", HAVE(GetOwnerDetails), std::string(), [&] {
    const FactionDetails d = G()->GetOwnerDetails(id);
    return std::string(d.factionID ? d.factionID : "");
  });
}
std::int64_t num_orders(u64 id) {
  return gcall("GetNumOrders", HAVE(GetNumOrders), std::int64_t{-1}, [&] { return static_cast<std::int64_t>(G()->GetNumOrders(id)); });
}
// GetComponentData fields through the framework's Lua accessor (UI thread). nullopt when the field is not readable.
std::optional<std::int64_t> field_int(u64 id, const char* field) {
  auto* api = x4n::detail::g_api;
  if (!api || !api->get_lua_property) return std::nullopt;
  X4nLuaKey k{};
  k.type = X4N_KEY_UINT64;
  k.v.u = id;
  std::int64_t v = 0;
  bool ok = false;
  if (!guarded("get_lua_property", [&] { ok = api->get_lua_property("GetComponentData", k, field, X4N_VAL_INT64, &v); })) return std::nullopt;
  if (!ok) return std::nullopt;
  return v;
}
std::string operational(u64 id) {
  if (!HAVE(IsComponentOperational)) return "n/a";
  bool r = false;
  if (!guarded("IsComponentOperational", [&] { r = G()->IsComponentOperational(id); })) return "n/a";
  return r ? "1" : "0";
}
std::string last_save_name() {
  return gcall("GetLastSaveInfo", HAVE(GetLastSaveInfo), std::string(), [] {
    const UISaveInfo i = G()->GetLastSaveInfo();
    // Raw values are logged by the caller (name vs filename: which one is the slot is part of the spike).
    return std::string(i.filename ? i.filename : "") + "|" + std::string(i.name ? i.name : "");
  });
}

// `saved` = "filename|name" from last_save_name(): either part may be the slot name.
bool scratch_ok(const std::string& saved) {
  const auto bar = saved.find('|');
  const std::string a = saved.substr(0, bar), b = bar == std::string::npos ? std::string() : saved.substr(bar + 1);
  return is_scratch_slot(a, g.cfg.s13.scratch_slot) || is_scratch_slot(b, g.cfg.s13.scratch_slot);
}

struct Pose {
  bool ok = false;
  Vec3 p;
  double a0 = 0, a1 = 0, a2 = 0;  // raw yaw, pitch, roll as the game returns them
};
Pose read_pose(u64 id) {
  Pose r;
  if (id == 0) return r;
  if (!HAVE(GetObjectPositionInSector)) {
    missing("GetObjectPositionInSector");
    return r;
  }
  UIPosRot u{};
  if (!guarded("GetObjectPositionInSector", [&] { u = G()->GetObjectPositionInSector(id); })) return r;
  r.ok = true;
  r.p = {u.x, u.y, u.z};
  r.a0 = u.yaw;
  r.a1 = u.pitch;
  r.a2 = u.roll;
  return r;
}
double to_rad(double raw) { return g.cfg.s13.angles_in_radians ? raw : raw / kRadToDeg; }
double to_deg(double raw) { return g.cfg.s13.angles_in_radians ? raw * kRadToDeg : raw; }

UIPosRot make_pos(Vec3 p, double yaw_deg, double pitch_deg, double roll_deg) {
  UIPosRot u{};
  u.x = static_cast<float>(p.x);
  u.y = static_cast<float>(p.y);
  u.z = static_cast<float>(p.z);
  u.yaw = static_cast<float>(yaw_deg);
  u.pitch = static_cast<float>(pitch_deg);
  u.roll = static_cast<float>(roll_deg);
  return u;
}
// Returns the call cost in microseconds, or -1 when the call is unavailable / failed.
double set_pos(u64 id, u64 sector, Vec3 p, double yaw_deg, double pitch_deg, double roll_deg) {
  if (!HAVE(SetObjectSectorPos)) {
    missing("SetObjectSectorPos");
    return -1.0;
  }
  const UIPosRot u = make_pos(p, yaw_deg, pitch_deg, roll_deg);
  const auto t0 = qpc();
  if (!guarded("SetObjectSectorPos", [&] { G()->SetObjectSectorPos(id, sector, u); })) return -1.0;
  return us_between(t0, qpc());
}
void activate(u64 id, bool on) {
  if (!HAVE(ActivateObject)) {
    missing("ActivateObject");
    return;
  }
  guarded("ActivateObject", [&] { G()->ActivateObject(id, on); });
}

std::vector<std::string> all_factions() {
  std::vector<std::string> out_v;
  if (!HAVE(GetAllFactions)) {
    missing("GetAllFactions");
    return out_v;
  }
  std::array<const char*, 160> buf{};
  std::uint32_t n = 0;
  if (!guarded("GetAllFactions", [&] { n = G()->GetAllFactions(buf.data(), static_cast<std::uint32_t>(buf.size()), true); })) return out_v;
  for (std::uint32_t i = 0; i < n && i < buf.size(); ++i)
    if (buf[i]) out_v.emplace_back(buf[i]);
  return out_v;
}

std::string fmt3(Vec3 v) { return std::format("({:.2f},{:.2f},{:.2f})", v.x, v.y, v.z); }

// ---- player reference object ----
struct PlayerRef {
  u64 id = 0;
  const char* via = "none";
};
PlayerRef player_ref() {
  if (const u64 o = occupied(); o) return {o, "occupied"};
  if (const u64 c = controlled(); c) return {c, "controlled"};
  if (const u64 k = container(); k) return {k, "container"};
  if (const u64 p = player_object(); p) return {p, "player_object"};
  return {};
}

// ---- guard (copy of the product logic: roots + the ship/station context of each root) ----
std::vector<u64> collect_guard() {
  std::vector<u64> ids;
  const auto add = [&](u64 id) {
    if (id == 0 || std::find(ids.begin(), ids.end(), id) != ids.end()) return;
    if (!valid(id)) return;
    ids.push_back(id);
  };
  const u64 roots[] = {occupied(), controlled(), player_id(), player_object(), container()};
  for (const u64 r : roots) add(r);
  for (const u64 r : roots) {
    if (r == 0) continue;
    add(ctx(r, "ship", true));
    add(ctx(r, "station", true));
  }
  return ids;
}

// The ONLY place the probe calls RemoveComponent. Returns the verdict; Allowed means the call was made.
RemoveVerdict remove_checked(u64 id, const char* why) {
  RemoveGate gate;
  gate.guard = collect_guard();
  RemoveVerdict v = gate.check(id, valid(id));
  say(v == RemoveVerdict::Allowed ? "INFO" : "WARN", std::format("remove_check id={} why={} guard_size={} verdict={}", id, why, gate.guard.size(), to_string(v)));
  if (v != RemoveVerdict::Allowed) return v;
  if (!HAVE(RemoveComponent)) {
    missing("RemoveComponent");
    return RemoveVerdict::BlockedNotValid;
  }
  guarded("RemoveComponent", [&] { G()->RemoveComponent(id); });
  return v;
}

// ---- registry of everything the probe spawned (file: x4mp_probe_s13_registry.json) ----
fs::path registry_path() { return g.env.dir / "x4mp_probe_s13_registry.json"; }
void registry_save() {
  nlohmann::json j = nlohmann::json::array();
  for (const auto& r : g.reg) j.push_back({{"id", r.id}, {"idcode", r.idcode}, {"kind", r.kind}, {"macro", r.macro}, {"owner", r.owner}});
  std::error_code ec;
  fs::create_directories(g.env.dir, ec);
  std::ofstream o(registry_path(), std::ios::binary | std::ios::trunc);
  o << j.dump(2);
}
void registry_load() {
  if (g.reg_loaded) return;
  g.reg_loaded = true;
  std::ifstream in(registry_path(), std::ios::binary);
  if (!in) return;
  std::ostringstream ss;
  ss << in.rdbuf();
  const auto j = nlohmann::json::parse(ss.str(), nullptr, false);
  if (j.is_discarded() || !j.is_array()) return;
  for (const auto& e : j) {
    if (!e.is_object()) continue;
    Ghost r;
    r.id = e.value("id", u64{0});
    r.idcode = e.value("idcode", std::string());
    r.kind = e.value("kind", std::string());
    r.macro = e.value("macro", std::string());
    r.owner = e.value("owner", std::string());
    if (r.id) g.reg.push_back(r);
  }
}
void registry_add(const Ghost& r) {
  registry_load();
  g.reg.push_back(r);
  registry_save();
}
void registry_erase(u64 id) {
  std::erase_if(g.reg, [&](const Ghost& r) { return r.id == id; });
  registry_save();
}
// A registry entry is live when the id is valid and its idcode still matches (ids are not stable across save loads).
bool entry_live(const Ghost& r) {
  if (!valid(r.id)) return false;
  const std::string c = idcode_of(r.id);
  return r.idcode.empty() || c.empty() || c == r.idcode;
}
std::optional<Ghost> find_live(std::string_view kind) {
  registry_load();
  for (const auto& r : g.reg)
    if (r.kind == kind && entry_live(r)) return r;
  return std::nullopt;
}

// ---- description / spawn ----
std::string describe(u64 id) {
  const auto pilot = field_int(id, "pilot");
  const Pose p = read_pose(id);
  return std::format("id={} idcode={} name='{}' class={} owner={} orders={} pilot={} operational={} sector={} pos={}", id, idcode_of(id), name_of(id), class_of(id),
                     owner_of(id), num_orders(id), pilot ? std::to_string(*pilot) : std::string("n/a"),
                     operational(id), ctx(id, "sector", false),
                     p.ok ? fmt3(p.p) : std::string("n/a"));
}

std::string choose_ghost_owner() {
  const auto f = all_factions();
  const std::string& want = g.cfg.s13.ghost_faction;
  if (std::find(f.begin(), f.end(), want) != f.end()) {
    say("INFO", std::format("owner faction '{}' exists ({} factions listed)", want, f.size()));
    return want;
  }
  say("WARN", std::format("owner faction '{}' does NOT exist ({} factions listed): falling back to '{}'", want, f.size(), g.cfg.s13.ghost_fallback_faction));
  return g.cfg.s13.ghost_fallback_faction;
}

struct SpawnReq {
  std::string macro, owner, kind, name;
  u64 sector = 0;
  Vec3 pos;
  double yaw_deg = 0, pitch_deg = 0, roll_deg = 0;
  bool inert = true;
  bool dress = true;
};
std::optional<Ghost> spawn_one(const SpawnReq& rq) {
  if (!HAVE(SpawnObjectAtPos2)) {
    missing("SpawnObjectAtPos2");
    return std::nullopt;
  }
  if (HAVE(FindMacro)) {
    bool found = false;
    guarded("FindMacro", [&] { found = G()->FindMacro(rq.macro.c_str()); });
    if (!found) say("WARN", std::format("FindMacro({}) = false: the macro may not exist in this install", rq.macro));
  }
  const UIPosRot u = make_pos(rq.pos, rq.yaw_deg, rq.pitch_deg, rq.roll_deg);
  u64 id = 0;
  const auto t0 = qpc();
  if (!guarded("SpawnObjectAtPos2", [&] { id = G()->SpawnObjectAtPos2(rq.macro.c_str(), rq.sector, u, rq.owner.c_str()); })) return std::nullopt;
  const double cost = us_between(t0, qpc());
  if (id == 0) {
    say("FAIL", std::format("SpawnObjectAtPos2 returned 0 macro={} owner={} sector={} pos={} cost_us={:.0f}", rq.macro, rq.owner, rq.sector, fmt3(rq.pos), cost));
    return std::nullopt;
  }
  Ghost gh;
  gh.id = id;
  gh.idcode = idcode_of(id);
  gh.kind = rq.kind;
  gh.macro = rq.macro;
  gh.owner = rq.owner;
  say("INFO", std::format("spawned kind={} macro={} owner={} sector={} want_pos={} spawn_cost_us={:.0f} valid={}", rq.kind, rq.macro, rq.owner, rq.sector,
                          fmt3(rq.pos), cost, valid(id)));
  if (rq.inert) activate(id, false);
  registry_add(gh);
  say("INFO", "after_spawn " + describe(id));
  if (rq.dress && !rq.name.empty()) {
    const std::string payload = std::format("{}|{}", id, rq.name);
    const int rc = x4n::raise_lua("x4mp.spike_dress", payload.c_str());
    say("INFO", std::format("raised Lua event x4mp.spike_dress payload={} rc={}", payload, rc));
  }
  return gh;
}

// Spawns relative to the player's ship: along its forward vector (+ lateral offset along its right vector).
struct Placement {
  bool ok = false;
  u64 sector = 0;
  PlayerRef ref;
  Pose pose;
  Vec3 fwd, right;
};
Placement place_near_player() {
  Placement p;
  p.ref = player_ref();
  if (!p.ref.id) {
    say("FAIL", "no player reference object (occupied/controlled/container/object all 0)");
    return p;
  }
  p.pose = read_pose(p.ref.id);
  p.sector = ctx(p.ref.id, "sector", true);
  if (!p.pose.ok || !p.sector) {
    say("FAIL", std::format("cannot place: ref={} id={} pose_ok={} sector={}", p.ref.via, p.ref.id, p.pose.ok, p.sector));
    return p;
  }
  const double yaw = to_rad(p.pose.a0), pitch = to_rad(p.pose.a1);
  p.fwd = forward_from_angles(yaw, pitch, g.cfg.s13.pitch_sign);
  p.right = right_from_yaw(yaw);
  p.ok = true;
  say("INFO", std::format("player_ref via={} id={} sector={} pos={} raw_angles=({:.4f},{:.4f},{:.4f}) angles_in_radians={} fwd={}", p.ref.via, p.ref.id, p.sector,
                          fmt3(p.pose.p), p.pose.a0, p.pose.a1, p.pose.a2, g.cfg.s13.angles_in_radians, fmt3(p.fwd)));
  return p;
}

std::optional<Ghost> spawn_ghost(const Placement& pl, const std::string& macro, const std::string& kind, const std::string& owner, double lateral_m) {
  SpawnReq rq;
  rq.macro = macro;
  rq.owner = owner;
  rq.kind = kind;
  rq.name = kind == "ghost_s" ? "S13 Ghost S" : "S13 Ghost M";
  rq.sector = pl.sector;
  rq.pos = pl.pose.p + pl.fwd * static_cast<double>(g.cfg.s13.spawn_distance_m) + pl.right * lateral_m;
  rq.yaw_deg = to_deg(pl.pose.a0);
  rq.pitch_deg = 0;
  rq.roll_deg = 0;
  return spawn_one(rq);
}

// Finds a live ghost of `kind`, or spawns one (S: ghost_macro_s, M: ghost_macro_m). nullopt on failure.
std::optional<Ghost> ensure_ghost(const char* kind) {
  if (auto e = find_live(kind)) return e;
  const Placement pl = place_near_player();
  if (!pl.ok) return std::nullopt;
  const bool s = std::string_view(kind) == "ghost_s";
  say("INFO", std::format("no live {} registered: spawning one", kind));
  return spawn_ghost(pl, s ? g.cfg.s13.ghost_macro_s : g.cfg.s13.ghost_macro_m, kind, choose_ghost_owner(), s ? -100.0 : 100.0);
}

// Removes every live ghost-ish registry entry of the given kinds (all when kinds is empty). Guard-checked.
int cleanup_registry(const std::vector<std::string>& kinds, const char* why) {
  registry_load();
  int removed = 0;
  const auto copy = g.reg;
  for (const auto& r : copy) {
    if (!kinds.empty() && std::find(kinds.begin(), kinds.end(), r.kind) == kinds.end()) continue;
    if (!entry_live(r)) {
      say("INFO", std::format("registry entry id={} idcode={} kind={} is no longer live (stale after a load?): dropped", r.id, r.idcode, r.kind));
      registry_erase(r.id);
      continue;
    }
    const RemoveVerdict v = remove_checked(r.id, why);
    if (v == RemoveVerdict::Allowed) {
      ++removed;
      registry_erase(r.id);
    }
  }
  return removed;
}

// ---- stats ----
struct Stats {
  std::vector<double> v;
  void add(double x) { v.push_back(x); }
  [[nodiscard]] std::string summary(const char* unit) const {
    if (v.empty()) return "n=0";
    return std::format("n={} p50={:.2f}{} p95={:.2f}{} max={:.2f}{}", v.size(), percentile(v, 50), unit, percentile(v, 95), unit,
                       *std::max_element(v.begin(), v.end()), unit);
  }
};

// =====================================================================================================================
// Block base + blocks
// =====================================================================================================================
struct Block {
  virtual ~Block() = default;
  virtual bool begin() = 0;                 // true = keep running (step() will be called every frame)
  virtual bool step() = 0;                  // true = finished
  virtual void abort(const char* why) { say("INFO", std::format("aborted: {}", why)); }
  std::int64_t t0 = 0;
  [[nodiscard]] double elapsed_s() const { return ms_between(t0, qpc()) / 1000.0; }
};

void selfcheck(bool always) {
  static bool done = false;
  if (done && !always) return;
  done = true;
  const char* names[] = {"SpawnObjectAtPos2", "FindMacro", "ActivateObject", "SetObjectSectorPos", "GetObjectPositionInSector", "GetPlayerOccupiedShipID",
                         "GetPlayerControlledShipID", "GetPlayerObjectID", "GetPlayerContainerID", "GetPlayerID", "GetContextByClass", "TeleportPlayerTo",
                         "CanTeleportPlayerTo", "IsSetaActive", "GetCurrentGameTime", "IsGamePaused", "IsPlayerOccupiedShipDocked", "IsValidComponent",
                         "RemoveComponent", "GetObjectIDCode", "GetComponentName", "GetComponentClass", "GetOwnerDetails", "GetNumOrders", "GetAllFactions",
                         "GetAllFactionShips", "GetNumAllFactionShips", "GetSectorsByOwner", "GetLastSaveInfo"};
  auto* f = G();
  std::string present, absent;
  const void* ptrs[] = {
      f ? reinterpret_cast<const void*>(f->SpawnObjectAtPos2) : nullptr,
      f ? reinterpret_cast<const void*>(f->FindMacro) : nullptr,
      f ? reinterpret_cast<const void*>(f->ActivateObject) : nullptr,
      f ? reinterpret_cast<const void*>(f->SetObjectSectorPos) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetObjectPositionInSector) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetPlayerOccupiedShipID) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetPlayerControlledShipID) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetPlayerObjectID) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetPlayerContainerID) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetPlayerID) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetContextByClass) : nullptr,
      f ? reinterpret_cast<const void*>(f->TeleportPlayerTo) : nullptr,
      f ? reinterpret_cast<const void*>(f->CanTeleportPlayerTo) : nullptr,
      f ? reinterpret_cast<const void*>(f->IsSetaActive) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetCurrentGameTime) : nullptr,
      f ? reinterpret_cast<const void*>(f->IsGamePaused) : nullptr,
      f ? reinterpret_cast<const void*>(f->IsPlayerOccupiedShipDocked) : nullptr,
      f ? reinterpret_cast<const void*>(f->IsValidComponent) : nullptr,
      f ? reinterpret_cast<const void*>(f->RemoveComponent) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetObjectIDCode) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetComponentName) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetComponentClass) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetOwnerDetails) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetNumOrders) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetAllFactions) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetAllFactionShips) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetNumAllFactionShips) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetSectorsByOwner) : nullptr,
      f ? reinterpret_cast<const void*>(f->GetLastSaveInfo) : nullptr};
  static_assert(sizeof(ptrs) / sizeof(ptrs[0]) == 29);
  for (std::size_t i = 0; i < 29; ++i) (ptrs[i] ? present : absent) += std::string(names[i]) + " ";
  const bool has_lua = x4n::detail::g_api && x4n::detail::g_api->get_lua_property;
  const bool has_raise = x4n::detail::g_api && x4n::detail::g_api->raise_lua_event;
  say(absent.empty() ? "PASS" : "FAIL", std::format("native functions present=[{}] MISSING=[{}] get_lua_property={} raise_lua_event={} game_version={}", present,
                                                    absent.empty() ? "none" : absent, has_lua, has_raise, x4n::game_version() ? x4n::game_version() : "?"));
}

// ------------------------------------------------------------------------------------------------ S13.1 ghost_spawn
struct GhostSpawn final : Block {
  struct Tracked {
    Ghost gh;
    Pose base;
    u64 sector = 0;
    double max_drift = 0, last_drift = 0;
    std::int64_t orders0 = 0;
    bool orders_changed = false;
  };
  std::vector<Tracked> t;
  std::vector<double> marks{1, 5, 10, 20, 30, 45, 60};
  std::size_t next_mark = 0;
  double dur = 60;

  bool begin() override {
    g.tag = "S13.1";
    selfcheck(false);
    dur = g.cfg.s13.drift_seconds;
    const int removed = cleanup_registry({"ghost_s", "ghost_m"}, "ghost_spawn re-run");
    if (removed) say("INFO", std::format("idempotent re-run: removed {} previous ghosts first", removed));
    const Placement pl = place_near_player();
    if (!pl.ok) return false;
    const std::string owner = choose_ghost_owner();
    const auto s = spawn_ghost(pl, g.cfg.s13.ghost_macro_s, "ghost_s", owner, -100.0);
    const auto m = spawn_ghost(pl, g.cfg.s13.ghost_macro_m, "ghost_m", owner, 100.0);
    say(s ? "PASS" : "FAIL", std::format("spawn S ghost from native macro={} id={}", g.cfg.s13.ghost_macro_s, s ? s->id : 0));
    say(m ? "PASS" : "FAIL", std::format("spawn M ghost from native macro={} id={}", g.cfg.s13.ghost_macro_m, m ? m->id : 0));
    for (const auto& gh : {s, m}) {
      if (!gh) continue;
      Tracked tr;
      tr.gh = *gh;
      tr.base = read_pose(gh->id);
      tr.sector = ctx(gh->id, "sector", false);
      tr.orders0 = num_orders(gh->id);
      const auto pilot = field_int(gh->id, "pilot");
      say(tr.orders0 <= 0 ? "PASS" : "FAIL",
          std::format("{} no orders: GetNumOrders={} pilot={} (n/a = field not readable) inert via ActivateObject(false)", gh->kind, tr.orders0,
                      pilot ? std::to_string(*pilot) : std::string("n/a")));
      t.push_back(tr);
    }
    if (t.empty()) return false;
    say("INFO", std::format("look at the ghosts now: name, colour, radar, target them, open the map. Drift is sampled for {} s. ids: {}", static_cast<int>(dur),
                            t.size()));
    return true;
  }
  bool step() override {
    const double e = elapsed_s();
    bool log_now = false;
    if (next_mark < marks.size() && e >= marks[next_mark] && marks[next_mark] <= dur) {
      log_now = true;
      ++next_mark;
    }
    for (auto& tr : t) {
      const Pose p = read_pose(tr.gh.id);
      if (!p.ok || !valid(tr.gh.id)) {
        if (log_now) say("WARN", std::format("{} id={} no longer readable", tr.gh.kind, tr.gh.id));
        continue;
      }
      const u64 sec = ctx(tr.gh.id, "sector", false);
      const double d = sec == tr.sector ? distance(p.p, tr.base.p) : 1.0e9;
      tr.last_drift = d;
      tr.max_drift = std::max(tr.max_drift, d);
      const std::int64_t o = num_orders(tr.gh.id);
      if (o != tr.orders0 && !tr.orders_changed) {
        tr.orders_changed = true;
        say("WARN", std::format("{} orders changed {} -> {} at t={:.1f}s", tr.gh.kind, tr.orders0, o, e));
      }
      if (log_now) say("INFO", std::format("drift t={:.1f}s {} id={} drift_m={:.3f} max_m={:.3f} sector={} orders={}", e, tr.gh.kind, tr.gh.id, d, tr.max_drift, sec, o));
    }
    if (e < dur) return false;
    for (const auto& tr : t)
      say(tr.max_drift < 1.0 && !tr.orders_changed ? "PASS" : "FAIL",
          std::format("drift {} id={} over {:.0f}s max_drift_m={:.3f} final_m={:.3f} orders_changed={} (criterion: < 1 m)", tr.gh.kind, tr.gh.id, dur, tr.max_drift,
                      tr.last_drift, tr.orders_changed));
    say("INFO", "ghost_spawn done; ghosts stay for ghost_motion / ghost_xsector / pause_move; run cleanup to remove them");
    return true;
  }
};

// ----------------------------------------------------------------------------------------------- S13.2 ghost_motion
struct GhostMotion final : Block {
  char mode = 'a';
  Ghost gh;
  u64 sector = 0;
  Pose park;
  Vec3 center;
  struct Seg {
    const char* label;
    Path path;
  };
  std::vector<Seg> segs;
  std::size_t si = 0;
  std::int64_t seg_t0 = 0, last_frame = 0;
  std::vector<Vec3> keys;
  std::int64_t last_key_set = -1;
  double last_vel_ms = -1e9;
  Stats cost_us, frame_ms;
  int sets = 0;
  static constexpr double kKeyDt = 0.05;

  bool begin() override {
    g.tag = "S13.2";
    selfcheck(false);
    if (arg != "a" && arg != "b" && arg != "c") {
      say("FAIL", std::format("usage: ghost_motion <a|b|c> (a = per-frame + interpolation, b = raw 20 Hz sets, c = a + velocity hint to Lua at 5 Hz); got '{}'", arg));
      return false;
    }
    mode = arg[0];
    auto e = ensure_ghost("ghost_s");
    if (!e) {
      say("FAIL", "no S ghost available");
      return false;
    }
    gh = *e;
    sector = ctx(gh.id, "sector", false);
    park = read_pose(gh.id);
    const PlayerRef ref = player_ref();
    const Pose pp = read_pose(ref.id);
    const u64 psec = ctx(ref.id, "sector", true);
    if (!pp.ok || !park.ok || !sector) {
      say("FAIL", std::format("cannot read poses ghost_ok={} player_ok={} ghost_sector={}", park.ok, pp.ok, sector));
      return false;
    }
    if (psec != sector) say("WARN", std::format("ghost sector {} != player sector {}: the ghost will move in its own sector", sector, psec));
    center = pp.p;
    activate(gh.id, false);
    const double dur = g.cfg.s13.motion_seconds;
    for (const double sp : {100.0, 300.0, 600.0}) {
      Path p;
      p.kind = Path::Kind::Circle;
      p.center = center;
      p.radius = 1000.0;
      p.speed = sp;
      segs.push_back({sp == 100 ? "circle_100" : sp == 300 ? "circle_300" : "circle_600", p});
    }
    Path ln;
    ln.kind = Path::Kind::Line;
    ln.speed = 3000.0;
    ln.dir = {1, 0, 0};
    ln.center = center + Vec3{-ln.speed * dur / 2.0, 0, 1000.0};  // start point (t = 0); passes 1 km beside the player
    segs.push_back({"line_3000", ln});
    say("INFO", std::format("mode={} ghost={} sector={} centre={} segments=circle 100/300/600 m/s r=1000 m + line 3000 m/s, {} s each. WATCH the ghost and rate smoothness 1-5",
                            mode, gh.id, sector, fmt3(center), static_cast<int>(dur)));
    start_segment();
    return true;
  }
  std::string arg;

  void start_segment() {
    seg_t0 = qpc();
    last_frame = seg_t0;
    keys.clear();
    last_key_set = -1;
    last_vel_ms = -1e9;
    cost_us = {};
    frame_ms = {};
    sets = 0;
    say("INFO", std::format("segment {} start mode={}", segs[si].label, mode));
  }
  [[nodiscard]] Vec3 path_pos(double t) const { return path_position(segs[si].path, t); }

  bool step() override {
    const auto now = qpc();
    frame_ms.add(ms_between(last_frame, now));
    last_frame = now;
    const double dt = ms_between(seg_t0, now) / 1000.0;
    const double dur = g.cfg.s13.motion_seconds;
    const Path& path = segs[si].path;
    // Keyframes at 20 Hz (what network updates look like), generated up to one key ahead of "now".
    while (keys.empty() || static_cast<double>(keys.size() - 1) * kKeyDt <= dt + kKeyDt) keys.push_back(path_pos(static_cast<double>(keys.size()) * kKeyDt));
    Vec3 pos;
    double vt = dt;
    bool do_set = false;
    if (mode == 'b') {
      const auto k = static_cast<std::int64_t>(dt / kKeyDt);
      if (k > last_key_set) {
        last_key_set = k;
        pos = keys[static_cast<std::size_t>(k)];
        vt = static_cast<double>(k) * kKeyDt;
        do_set = true;
      }
    } else {
      vt = std::max(0.0, dt - kKeyDt);  // render one key behind so the next key always exists
      pos = interpolate_keys(keys, kKeyDt, vt);
      do_set = true;
    }
    if (do_set) {
      const Vec3 v = path_velocity(path, vt, dur);
      const double yaw = std::atan2(v.x, v.z) * kRadToDeg;
      const double c = set_pos(gh.id, sector, pos, yaw, 0, 0);
      if (c < 0) {
        say("FAIL", "SetObjectSectorPos unavailable or raised: aborting block");
        return true;
      }
      cost_us.add(c);
      ++sets;
      if (mode == 'c' && dt * 1000.0 - last_vel_ms >= 200.0) {
        last_vel_ms = dt * 1000.0;
        const std::string payload = std::format("{}|{:.3f}|{:.3f}|{:.3f}", gh.id, v.x, v.y, v.z);
        const auto r0 = qpc();
        const int rc = x4n::raise_lua("x4mp.spike_velocity", payload.c_str());
        say("INFO", std::format("x4mp.spike_velocity payload={} rc={} raise_cost_us={:.0f}", payload, rc, us_between(r0, qpc())));
      }
    }
    if (dt < dur) return false;
    say("PASS", std::format("segment {} mode={} done: sets={} ({:.1f}/s) SetObjectSectorPos cost {} frame_dt {}", segs[si].label, mode, sets, sets / dur,
                            cost_us.summary("us"), frame_ms.summary("ms")));
    if (++si < segs.size()) {
      start_segment();
      return false;
    }
    set_pos(gh.id, sector, park.p, to_deg(park.a0), to_deg(park.a1), to_deg(park.a2));
    say("INFO", std::format("motion mode={} complete; ghost parked again at {}. Now fly SLOWLY into it and note the collision outcome (bounce / damage / pushed). Rate smoothness 1-5.",
                            mode, fmt3(park.p)));
    return true;
  }
};

// ---------------------------------------------------------------------------------------------- S13.3 ghost_xsector
struct GhostXSector final : Block {
  Ghost gh;
  u64 from_sector = 0, to_sector = 0;
  Pose before;
  int frames = 0;
  bool moved = false, returned = false;
  std::int64_t moved_at = 0;
  std::vector<int> marks{1, 5, 30};
  std::size_t mi = 0;

  bool begin() override {
    g.tag = "S13.3";
    selfcheck(false);
    auto e = ensure_ghost("ghost_s");
    if (!e) {
      say("FAIL", "no S ghost available");
      return false;
    }
    gh = *e;
    from_sector = ctx(gh.id, "sector", false);
    before = read_pose(gh.id);
    say("INFO", std::format("context before: sector={} ('{}') cluster={} zone={} pos={}", from_sector, name_of(from_sector), ctx(gh.id, "cluster", false),
                            ctx(gh.id, "zone", false), before.ok ? fmt3(before.p) : "n/a"));
    to_sector = pick_target(from_sector);
    if (!to_sector) {
      say("FAIL", "no other sector found (GetSectorsByOwner returned nothing different); set xsector_name in the config");
      return false;
    }
    say("INFO", std::format("target sector={} ('{}') cluster={} (same cluster: {})", to_sector, name_of(to_sector), ctx(to_sector, "cluster", true),
                            ctx(to_sector, "cluster", true) == ctx(from_sector, "cluster", true)));
    const double c = set_pos(gh.id, to_sector, {2000, 0, 2000}, 0, 0, 0);
    moved = true;
    moved_at = qpc();
    say(c >= 0 ? "INFO" : "FAIL", std::format("SetObjectSectorPos(ghost, other sector) cost_us={:.0f}", c));
    return c >= 0;
  }
  u64 pick_target(u64 from) {
    std::vector<u64> secs;
    if (HAVE(GetSectorsByOwner)) {
      std::array<u64, 256> buf{};
      std::vector<std::string> owners = all_factions();
      owners.push_back("ownerless");
      owners.push_back("player");
      for (const auto& o : owners) {
        std::uint32_t n = 0;
        if (!guarded("GetSectorsByOwner", [&] { n = G()->GetSectorsByOwner(buf.data(), static_cast<std::uint32_t>(buf.size()), o.c_str()); })) continue;
        for (std::uint32_t i = 0; i < n && i < buf.size(); ++i)
          if (buf[i] && buf[i] != from && std::find(secs.begin(), secs.end(), buf[i]) == secs.end()) secs.push_back(buf[i]);
      }
    } else {
      missing("GetSectorsByOwner");
    }
    say("INFO", std::format("{} candidate sectors enumerated", secs.size()));
    const std::string& want = g.cfg.s13.xsector_name;
    if (!want.empty()) {
      for (const u64 s : secs)
        if (name_of(s).find(want) != std::string::npos) return s;
      say("WARN", std::format("xsector_name '{}' matched no sector name; picking by cluster instead", want));
    }
    const u64 my_cluster = ctx(from, "cluster", true);
    for (const u64 s : secs)
      if (my_cluster && ctx(s, "cluster", true) == my_cluster) return s;
    return secs.empty() ? 0 : secs.front();
  }
  bool step() override {
    ++frames;
    if (moved && !returned) {
      if (mi < marks.size() && frames >= marks[mi]) {
        ++mi;
        const u64 now_sector = ctx(gh.id, "sector", false);
        const Pose p = read_pose(gh.id);
        say(now_sector == to_sector ? "PASS" : "FAIL",
            std::format("context after {} frames ({:.0f} ms): sector={} ('{}') expected={} cluster={} pos={} valid={}", frames, ms_between(moved_at, qpc()), now_sector,
                        name_of(now_sector), to_sector, ctx(gh.id, "cluster", false), p.ok ? fmt3(p.p) : "n/a", valid(gh.id)));
      }
      if (ms_between(moved_at, qpc()) / 1000.0 >= g.cfg.s13.xsector_hold_seconds && mi >= marks.size()) {
        say("INFO", std::format("held {} s in the other sector (find it on the map); moving back", g.cfg.s13.xsector_hold_seconds));
        const double c = set_pos(gh.id, from_sector, before.p, to_deg(before.a0), to_deg(before.a1), to_deg(before.a2));
        returned = true;
        moved_at = qpc();
        frames = 0;
        mi = 0;
        say(c >= 0 ? "INFO" : "FAIL", std::format("moved back cost_us={:.0f}", c));
      }
      return false;
    }
    if (returned && frames >= 5) {
      const u64 s = ctx(gh.id, "sector", false);
      const Pose p = read_pose(gh.id);
      say(s == from_sector && p.ok && distance(p.p, before.p) < 1.0 ? "PASS" : "FAIL",
          std::format("return check sector={} expected={} dist_m={:.3f}", s, from_sector, p.ok ? distance(p.p, before.p) : -1.0));
      return true;
    }
    return false;
  }
};

// ---------------------------------------------------------------------------------------------------- S13.4 sample
struct Sample final : Block {
  double dt_s = 0.05;
  std::int64_t last_sample = 0, prev_t = 0;
  Vec3 prev_pos;
  u64 prev_sector = 0;
  std::uint64_t k = 0;
  Stats pose_us, ctx_us, total_us, gap_ms;
  double amin[3] = {1e9, 1e9, 1e9}, amax[3] = {-1e9, -1e9, -1e9};
  std::uint64_t seta_on = 0, hw = 0, nosec = 0, docked_n = 0, onfoot_n = 0;
  std::string last_sector_name;
  u64 last_name_sector = ~u64{0};

  bool begin() override {
    g.tag = "S13.4";
    selfcheck(false);
    dt_s = 1.0 / static_cast<double>(g.cfg.s13.sample_hz);
    say("INFO", std::format("sampling at {} Hz for {} s: perform the manoeuvres and write down the clock times. Columns: sec=sector id(0=none) ref=occ|ctl|cont|obj pos ang_raw=(yaw,pitch,roll as returned) "
                            "v=m/s from pos delta occ/ctl/cont/obj ids dock=IsPlayerOccupiedShipDocked foot=guess hw=highway ctx id seta", g.cfg.s13.sample_hz,
                            g.cfg.s13.sample_seconds));
    return true;
  }
  bool step() override {
    const auto now = qpc();
    const double e = elapsed_s();
    if (last_sample == 0 || ms_between(last_sample, now) / 1000.0 >= dt_s) take(now, e);
    if (e < g.cfg.s13.sample_seconds) return false;
    const bool rad = amax[0] <= 6.3 && amin[0] >= -6.3 && amax[1] <= 6.3 && amin[1] >= -6.3;
    say("PASS", std::format("sample done: n={} achieved_hz={:.1f} (requested {}) frame_gap {} pose_call {} ctx_call {} total_per_sample {}", k, static_cast<double>(k) / e,
                            g.cfg.s13.sample_hz, gap_ms.summary("ms"), pose_us.summary("us"), ctx_us.summary("us"), total_us.summary("us")));
    say("INFO", std::format("angle raw ranges yaw=[{:.3f},{:.3f}] pitch=[{:.3f},{:.3f}] roll=[{:.3f},{:.3f}] => unit guess: {} (config angles_in_radians={})", amin[0], amax[0],
                            amin[1], amax[1], amin[2], amax[2], rad ? "radians (all within +-2pi; turn through more than 180 deg to be sure)" : "degrees",
                            g.cfg.s13.angles_in_radians));
    say("INFO", std::format("flags: seta_samples={} highway_ctx_samples={} no_sector_samples={} docked_samples={} onfoot_guess_samples={}", seta_on, hw, nosec, docked_n, onfoot_n));
    return true;
  }
  void take(std::int64_t now, double e) {
    if (last_sample != 0) gap_ms.add(ms_between(last_sample, now));
    last_sample = now;
    const auto c0 = qpc();
    const u64 occ = occupied(), ctl = controlled(), obj = player_object(), cont = container();
    PlayerRef ref;
    if (occ) ref = {occ, "occ"};
    else if (ctl) ref = {ctl, "ctl"};
    else if (cont) ref = {cont, "cont"};
    else if (obj) ref = {obj, "obj"};
    const auto p0 = qpc();
    const Pose p = read_pose(ref.id);
    const auto p1 = qpc();
    const u64 sec = ctx(ref.id, "sector", true);
    const u64 hwy = ctx(ref.id, "highway", true);
    const auto p2 = qpc();
    const bool s = seta();
    const bool dock = occupied_docked();
    const bool foot = occ == 0 && ctl == 0 && obj != 0;
    pose_us.add(us_between(p0, p1));
    ctx_us.add(us_between(p1, p2));
    total_us.add(us_between(c0, qpc()));
    double v = -1;
    if (p.ok && prev_t != 0 && sec == prev_sector && sec != 0) {
      const double d = ms_between(prev_t, now) / 1000.0;
      if (d > 0) v = distance(p.p, prev_pos) / d;
    }
    prev_t = now;
    prev_pos = p.p;
    prev_sector = sec;
    if (p.ok) {
      const double a[3] = {p.a0, p.a1, p.a2};
      for (int i = 0; i < 3; ++i) {
        amin[i] = std::min(amin[i], a[i]);
        amax[i] = std::max(amax[i], a[i]);
      }
    }
    seta_on += s;
    hw += hwy != 0;
    nosec += sec == 0;
    docked_n += dock;
    onfoot_n += foot;
    if (sec != last_name_sector) {
      last_name_sector = sec;
      say("INFO", std::format("sector change id={} name='{}' cluster={}", sec, name_of(sec), ctx(sec, "cluster", true)));
    }
    say("INFO", std::format("sample k={} t={:.3f}s sec={} ref={} pos={} ang_raw=({:.4f},{:.4f},{:.4f}) v={:.1f} occ={} ctl={} cont={} obj={} dock={} foot={} hw={} seta={} pose_us={:.1f}",
                            k, e, sec, ref.via, p.ok ? fmt3(p.p) : "n/a", p.a0, p.a1, p.a2, v, occ, ctl, cont, obj, dock, foot, hwy, s, us_between(p0, p1)));
    ++k;
  }
};

// ------------------------------------------------------------------------------------------------------ S13.5 seat
struct Seat final : Block {
  u64 occ = ~u64{0}, ctl = ~u64{0}, obj = ~u64{0}, cont = ~u64{0}, pid = ~u64{0};
  std::uint64_t frame = 0, edges = 0;
  bool begin() override {
    g.tag = "S13.5";
    selfcheck(false);
    say("INFO", std::format("watching occupied/controlled/player object/container ids per frame for {} s: stand in the cockpit, sit down, stand up, walk out; note clock times", g.cfg.s13.seat_seconds));
    return true;
  }
  bool step() override {
    ++frame;
    const u64 o = occupied(), c = controlled(), ob = player_object(), k = container(), p = player_id();
    if (o != occ || c != ctl || ob != obj || k != cont || p != pid) {
      ++edges;
      say("INFO", std::format("seat_edge frame={} t={:.3f}s occupied {}->{} controlled {}->{} object {}->{} container {}->{} player_id {}->{} dock={} sector={}", frame, elapsed_s(),
                              occ, o, ctl, c, obj, ob, cont, k, pid, p, occupied_docked(), ctx(o ? o : c ? c : k, "sector", true)));
      occ = o;
      ctl = c;
      obj = ob;
      cont = k;
      pid = p;
    }
    if (elapsed_s() < g.cfg.s13.seat_seconds) return false;
    say("PASS", std::format("seat watch done: frames={} edges={} (an 'occupied 0->id' edge is the sit-down edge)", frame, edges));
    return true;
  }
};

// -------------------------------------------------------------------------------------------- S13.6 takeover(_docked)
struct Takeover final : Block {
  bool docked_variant = false;
  u64 orig = 0, fresh = 0;
  enum class Phase { Confirm, Settle, Done } phase = Phase::Confirm;
  int ok_frames = 0, settle_frames = 0;
  std::int64_t tp_at = 0;
  u64 last_occ = ~u64{0}, last_ctl = ~u64{0};
  bool removed = false;

  bool begin() override {
    g.tag = "S13.6";
    selfcheck(false);
    const std::string saved = last_save_name();
    const bool scratch = scratch_ok(saved);
    say("INFO", std::format("current save (GetLastSaveInfo filename|name) = '{}' scratch_slot='{}' match={}", saved, g.cfg.s13.scratch_slot, scratch));
    if (!scratch) {
      say("REFUSED", "the current save is not the configured scratch slot (or scratch_slot is not configured): nothing was spawned, teleported or removed");
      return false;
    }
    const u64 occ = occupied(), ctl = controlled(), cont = container(), obj = player_object();
    const bool dock = occupied_docked();
    say("INFO", std::format("pre-state occupied={} controlled={} container={} object={} docked_flag={} (standing in the cockpit after a load: occupied=0)", occ, ctl, cont, obj, dock));
    if (docked_variant && !dock && !(cont != 0 && occ == 0 && ctx(cont, "station", true) == cont)) {
      say("FAIL", "takeover_docked needs the player docked inside a station (load a scratch save that is docked); not docked: nothing done");
      return false;
    }
    const PlayerRef ref = player_ref();
    orig = ref.id;
    if (!orig) {
      say("FAIL", "no reference ship for the player");
      return false;
    }
    const Pose pp = read_pose(orig);
    const u64 sec = ctx(orig, "sector", true);
    if (!pp.ok || !sec) {
      say("FAIL", std::format("cannot read the reference pose via={} id={} sector={}", ref.via, orig, sec));
      return false;
    }
    say("INFO", std::format("original ship ref via={} id={} idcode={} sector={} pos={}", ref.via, orig, idcode_of(orig), sec, fmt3(pp.p)));
    SpawnReq rq;
    rq.macro = g.cfg.s13.takeover_macro;
    rq.owner = "player";
    rq.kind = "takeover";
    rq.sector = sec;
    rq.pos = pp.p + Vec3{static_cast<double>(g.cfg.s13.takeover_distance_m), 0, 0};
    rq.yaw_deg = to_deg(pp.a0);
    rq.inert = false;
    rq.dress = false;
    const auto n = spawn_one(rq);
    if (!n) {
      say("FAIL", "could not spawn the player-owned fighter");
      return false;
    }
    fresh = n->id;
    say("INFO", std::format("player-owned fighter {} ({:.0f} m away). Calling CanTeleportPlayerTo / TeleportPlayerTo(force)", describe(fresh), static_cast<double>(g.cfg.s13.takeover_distance_m)));
    if (HAVE(CanTeleportPlayerTo)) {
      std::string why;
      guarded("CanTeleportPlayerTo", [&] {
        const char* r = G()->CanTeleportPlayerTo(fresh, true, true);
        why = r ? r : "<null>";
      });
      say("INFO", std::format("CanTeleportPlayerTo(fresh,true,force) raw='{}' (empty = allowed)", why));
    } else {
      missing("CanTeleportPlayerTo");
    }
    bool rc = false;
    const auto t = qpc();
    if (!HAVE(TeleportPlayerTo)) {
      missing("TeleportPlayerTo");
      return false;
    }
    guarded("TeleportPlayerTo", [&] { rc = G()->TeleportPlayerTo(fresh, true, true, true); });
    tp_at = qpc();
    say(rc ? "INFO" : "FAIL", std::format("TeleportPlayerTo(fresh, allowcontrolling=1, instant=1, force=1) returned {} cost_us={:.0f}", rc, us_between(t, tp_at)));
    return true;
  }
  bool in_new() {
    const u64 o = occupied(), c = controlled();
    if (o != last_occ || c != last_ctl) {
      last_occ = o;
      last_ctl = c;
      say("INFO", std::format("edge after teleport: occupied={} controlled={} container={} object={} ({:.0f} ms)", o, c, container(), player_object(), ms_between(tp_at, qpc())));
    }
    return o == fresh || c == fresh;
  }
  bool step() override {
    if (phase == Phase::Confirm) {
      ok_frames = in_new() ? ok_frames + 1 : 0;
      if (ok_frames >= 10) {
        say("PASS", std::format("player guard sees the new ship for 10 consecutive frames ({:.0f} ms after the teleport)", ms_between(tp_at, qpc())));
        const RemoveVerdict v = remove_checked(orig, "takeover: vacated original");
        removed = v == RemoveVerdict::Allowed;
        say(removed ? "PASS" : "FAIL", std::format("removal of the vacated original id={} verdict={}", orig, to_string(v)));
        phase = Phase::Settle;
        return false;
      }
      if (ms_between(tp_at, qpc()) > 10000.0) {
        say("FAIL", std::format("teleport not confirmed within 10 s (occupied={} controlled={} fresh={}): the original was NOT removed; the new fighter stays (cleanup removes it when unguarded)",
                                occupied(), controlled(), fresh));
        return true;
      }
      return false;
    }
    if (++settle_frames < 30) return false;
    const bool gone = !valid(orig);
    say(removed && gone && in_new() ? "PASS" : "FAIL", std::format("30 frames after the removal: original valid={} player still in new ship={} (no Game Over if this line appears)", !gone, in_new()));
    return true;
  }
};

// ------------------------------------------------------------------------------------------------------ S13.8 persist
fs::path persist_path() { return g.env.dir / "x4mp_probe_s13_persist.json"; }

struct PersistSpawn final : Block {
  bool begin() override {
    g.tag = "S13.8";
    selfcheck(false);
    const std::string saved = last_save_name();
    const bool scratch = scratch_ok(saved);
    say("INFO", std::format("current save (GetLastSaveInfo filename|name) = '{}' scratch_slot='{}' match={}", saved, g.cfg.s13.scratch_slot, scratch));
    if (!scratch) {
      say("REFUSED", "the current save is not the configured scratch slot: nothing spawned");
      return false;
    }
    cleanup_registry({"persist"}, "persist_spawn re-run");
    const Placement pl = place_near_player();
    if (!pl.ok) return false;
    SpawnReq rq;
    rq.macro = g.cfg.s13.ghost_macro_s;
    rq.owner = choose_ghost_owner();
    rq.kind = "persist";
    rq.name = "S13 Persist";
    rq.sector = pl.sector;
    rq.pos = pl.pose.p + pl.fwd * static_cast<double>(g.cfg.s13.spawn_distance_m);
    rq.yaw_deg = to_deg(pl.pose.a0);
    const auto s = spawn_one(rq);
    if (!s) {
      say("FAIL", "spawn failed");
      return false;
    }
    const Pose p = read_pose(s->id);
    nlohmann::json j = {{"id", s->id},          {"idcode", s->idcode},   {"owner", s->owner},       {"macro", s->macro},
                        {"sector_id", pl.sector}, {"sector_name", name_of(pl.sector)}, {"x", p.p.x}, {"y", p.p.y}, {"z", p.p.z},
                        {"a0", p.a0},           {"a1", p.a1},            {"a2", p.a2},              {"game_time", game_time()},
                        {"name", name_of(s->id)}, {"orders", num_orders(s->id)}};
    std::error_code ec;
    fs::create_directories(g.env.dir, ec);
    std::ofstream o(persist_path(), std::ios::binary | std::ios::trunc);
    o << j.dump(2);
    say(o.good() ? "PASS" : "FAIL", std::format("recorded id={} idcode={} pos={} sector={} in {}", s->id, s->idcode, fmt3(p.p), pl.sector, persist_path().filename().string()));
    say("INFO", "NOW: save to the scratch slot (save_007) with the in-game save, reload it, then run persist_check");
    return false;
  }
  bool step() override { return true; }
};

struct PersistCheck final : Block {
  Ghost found;
  Pose p0;
  std::int64_t found_at = 0;
  bool ok = false;
  nlohmann::json rec;

  bool begin() override {
    g.tag = "S13.8";
    selfcheck(false);
    std::ifstream in(persist_path(), std::ios::binary);
    std::ostringstream ss;
    ss << in.rdbuf();
    rec = nlohmann::json::parse(ss.str(), nullptr, false);
    if (rec.is_discarded() || !rec.is_object()) {
      say("FAIL", "no persist record file: run persist_spawn first");
      return false;
    }
    const std::string idcode = rec.value("idcode", std::string());
    const std::string owner = rec.value("owner", std::string());
    say("INFO", std::format("record idcode={} owner={} old_id={} sector='{}' pos=({:.2f},{:.2f},{:.2f})", idcode, owner, rec.value("id", u64{0}), rec.value("sector_name", std::string()),
                            rec.value("x", 0.0), rec.value("y", 0.0), rec.value("z", 0.0)));
    if (!HAVE(GetAllFactionShips) || !HAVE(GetNumAllFactionShips)) {
      missing("GetAllFactionShips");
      return false;
    }
    std::uint32_t n = 0;
    guarded("GetNumAllFactionShips", [&] { n = G()->GetNumAllFactionShips(owner.c_str()); });
    std::vector<u64> ships(n + 16);
    std::uint32_t got = 0;
    guarded("GetAllFactionShips", [&] { got = G()->GetAllFactionShips(ships.data(), static_cast<std::uint32_t>(ships.size()), owner.c_str()); });
    say("INFO", std::format("{} ships owned by '{}' enumerated (GetNumAllFactionShips={})", got, owner, n));
    for (std::uint32_t i = 0; i < got && i < ships.size(); ++i) {
      if (idcode_of(ships[i]) == idcode) {
        found.id = ships[i];
        break;
      }
    }
    if (!found.id) {
      say("FAIL", std::format("no ship with idcode {} found after the reload: binding by idcode does not work (or the ship was not saved)", idcode));
      return false;
    }
    found.idcode = idcode;
    found.kind = "persist";
    found.owner = owner;
    // Re-register so cleanup can remove it (new id after the load).
    registry_load();
    std::erase_if(g.reg, [&](const Ghost& r) { return r.idcode == idcode; });
    registry_add(found);
    p0 = read_pose(found.id);
    found_at = qpc();
    const u64 sec = ctx(found.id, "sector", false);
    const std::string sname = name_of(sec);
    const Vec3 old{rec.value("x", 0.0), rec.value("y", 0.0), rec.value("z", 0.0)};
    const bool same_sector = sname == rec.value("sector_name", std::string());
    const double delta = p0.ok ? distance(p0.p, old) : -1.0;
    say(same_sector && delta >= 0 && delta < 1.0 ? "PASS" : "FAIL",
        std::format("found by idcode: new_id={} (old {}) sector='{}' same_sector_name={} pos={} delta_m={:.3f} (criterion < 1 m) name_now='{}' name_then='{}' orders={} ", found.id,
                    rec.value("id", u64{0}), sname, same_sector, p0.ok ? fmt3(p0.p) : "n/a", delta, name_of(found.id), rec.value("name", std::string()), num_orders(found.id)));
    say("INFO", "state " + describe(found.id) + " -- sampling again in 5 s to see whether it moves (active) or stays (inert)");
    ok = true;
    return true;
  }
  bool step() override {
    if (ms_between(found_at, qpc()) < 5000.0) return false;
    const Pose p1 = read_pose(found.id);
    const double moved = p0.ok && p1.ok ? distance(p0.p, p1.p) : -1.0;
    say(moved >= 0 && moved < 1.0 ? "PASS" : "INFO", std::format("active-state check: moved {:.3f} m in 5 s (< 1 m = still inert/deactivated; more = it is active and moving)", moved));
    return true;
  }
};

// ------------------------------------------------------------------------------------------------------- S13.9 seta
struct Seta final : Block {
  bool prev = false, pending = false, first = true;
  std::int64_t raised_at = 0;
  std::uint64_t on_edges = 0, off_ok = 0, off_fail = 0;
  bool begin() override {
    g.tag = "S13.9";
    selfcheck(false);
    say("INFO", std::format("watching IsSetaActive for {} s: turn SETA on; on every rising edge Lua event x4mp.spike_seta_off is raised and the probe checks whether SETA is off within 1 s", g.cfg.s13.seta_seconds));
    return true;
  }
  bool step() override {
    const bool s = seta();
    const double e = elapsed_s();
    if (first || s != prev) {
      say("INFO", std::format("IsSetaActive edge t={:.3f}s {} -> {}", e, first ? std::string("?") : std::to_string(prev), s));
      if (s && !prev) {
        ++on_edges;
        const int rc = x4n::raise_lua("x4mp.spike_seta_off", "1");
        raised_at = qpc();
        pending = true;
        say("INFO", std::format("SETA is active: raised Lua event x4mp.spike_seta_off rc={}", rc));
      }
      if (!s && pending) {
        pending = false;
        ++off_ok;
        say("PASS", std::format("SETA went off {:.0f} ms after the event", ms_between(raised_at, qpc())));
      }
      prev = s;
      first = false;
    }
    if (pending && ms_between(raised_at, qpc()) > 1000.0) {
      pending = false;
      ++off_fail;
      say("FAIL", "SETA still active 1 s after x4mp.spike_seta_off (is the spike MD handler loaded?)");
    }
    if (e < g.cfg.s13.seta_seconds) return false;
    say(on_edges > 0 && off_fail == 0 ? "PASS" : "INFO", std::format("seta watch done: on_edges={} switched_off_within_1s={} not_switched_off={}", on_edges, off_ok, off_fail));
    return true;
  }
};

// -------------------------------------------------------------------------------------------------- S13.10 pause_move
struct PauseMove final : Block {
  Ghost gh;
  u64 sector = 0;
  Pose base;
  double last_t = -1;
  int same = 0;
  std::int64_t change_qpc = 0, paused_at = 0, last_tick_q = 0, last_log = 0;
  bool paused = false, any_pause = false;
  std::uint64_t frames_paused = 0, native_at_pause = 0, native_gap0 = 0;
  double t_at_pause = 0, max_err = 0, max_gap_ms = 0;
  Stats pause_frame_ms;
  bool begin() override {
    g.tag = "S13.10";
    selfcheck(false);
    auto e = ensure_ghost("ghost_s");
    if (!e) {
      say("FAIL", "no S ghost available");
      return false;
    }
    gh = *e;
    sector = ctx(gh.id, "sector", false);
    base = read_pose(gh.id);
    if (!base.ok || !sector) {
      say("FAIL", "cannot read the ghost pose");
      return false;
    }
    activate(gh.id, false);
    last_t = game_time();
    change_qpc = qpc();
    last_tick_q = change_qpc;
    native_gap0 = g.native_frames.load();
    say("INFO", std::format("waiting up to {} s for a pause: open the Esc menu for ~20 s (pause = game time not advancing). The ghost is driven along +x at 100 m/s while paused.", g.cfg.s13.pause_wait_seconds));
    return true;
  }
  void finish_pause(const char* how) {
    const double secs = ms_between(paused_at, qpc()) / 1000.0;
    const bool follows = max_err < 1.0 && frames_paused > 0;
    say(frames_paused > 0 ? "PASS" : "INFO",
        std::format("pause ended ({}): lasted {:.1f}s game_time_advanced_during_pause=false ui_frames_during_pause={} ({:.1f}/s) native_frames_during_pause={} max_ui_gap_ms={:.0f} frame_dt {}",
                    how, secs, frames_paused, static_cast<double>(frames_paused) / std::max(secs, 0.001), g.native_frames.load() - native_at_pause, max_gap_ms,
                    pause_frame_ms.summary("ms")));
    say(follows ? "PASS" : "FAIL", std::format("SetObjectSectorPos while paused: read-back error max_m={:.3f} (< 1 m = the ghost DOES move while the game is paused)", max_err));
    paused = false;
  }
  bool step() override {
    const auto now = qpc();
    const double gap = ms_between(last_tick_q, now);
    last_tick_q = now;
    const double t = game_time();
    const bool flag = game_paused();
    if (t != last_t) {
      if (paused) {
        finish_pause("game time advances again");
        return true;
      }
      if (gap > 1000.0 && !any_pause)
        say("WARN", std::format("on_frame_update did not tick for {:.0f} ms (native frames in that gap: {}) and game time moved {:.3f} -> {:.3f}: if this was your Esc pause, UI frames do NOT tick while paused",
                                gap, g.native_frames.load() - native_gap0, last_t, t));
      last_t = t;
      same = 0;
      change_qpc = now;
      native_gap0 = g.native_frames.load();
    } else {
      ++same;
      if (!paused && same >= 5 && ms_between(change_qpc, now) >= 300.0) {
        paused = true;
        any_pause = true;
        paused_at = now;
        t_at_pause = t;
        native_at_pause = g.native_frames.load();
        frames_paused = 0;
        max_err = 0;
        max_gap_ms = 0;
        base = read_pose(gh.id);
        say("INFO", std::format("pause detected: game_time={:.3f} not advancing for {} frames / {:.0f} ms; IsGamePaused={}; on_frame_update IS ticking", t, same, ms_between(change_qpc, now), flag));
      }
    }
    if (paused) {
      ++frames_paused;
      pause_frame_ms.add(gap);
      max_gap_ms = std::max(max_gap_ms, gap);
      const double sec = ms_between(paused_at, now) / 1000.0;
      const Vec3 target = base.p + Vec3{100.0 * sec, 0, 0};
      const double c = set_pos(gh.id, sector, target, to_deg(base.a0), to_deg(base.a1), to_deg(base.a2));
      const Pose r = read_pose(gh.id);
      const double err = r.ok ? distance(r.p, target) : 1.0e9;
      max_err = std::max(max_err, err);
      if (ms_between(last_log, now) >= 1000.0) {
        last_log = now;
        say("INFO", std::format("paused t={:.1f}s frames={} set_cost_us={:.0f} set={} read={} err_m={:.3f} IsGamePaused={}", sec, frames_paused, c, fmt3(target),
                                r.ok ? fmt3(r.p) : "n/a", err, flag));
      }
    }
    if (!paused && elapsed_s() >= g.cfg.s13.pause_wait_seconds) {
      say(any_pause ? "INFO" : "FAIL", std::format("wait over: pause {} (game time did {}advance)", any_pause ? "was seen and ended" : "never detected", any_pause ? "" : "never stop to "));
      return true;
    }
    return false;
  }
  void abort(const char* why) override {
    if (paused) finish_pause(why);
    else say("INFO", std::format("aborted: {}", why));
  }
};

// ------------------------------------------------------------------------------------------------- cleanup / check
struct Cleanup final : Block {
  bool begin() override {
    g.tag = "S13.cleanup";
    registry_load();
    const std::size_t before = g.reg.size();
    const int removed = cleanup_registry({}, "cleanup");
    say(removed == static_cast<int>(before) ? "PASS" : "INFO", std::format("cleanup: registry entries={} removed={} remaining={} (a remaining entry is guarded or not valid)", before, removed, g.reg.size()));
    return false;
  }
  bool step() override { return true; }
};
struct Check final : Block {
  bool begin() override {
    g.tag = "S13.check";
    selfcheck(true);
    registry_load();
    say("INFO", std::format("registry={} entries; blocks: ghost_spawn ghost_motion <a|b|c> ghost_xsector sample seat takeover takeover_docked persist_spawn persist_check seta pause_move cleanup s13_stop s13_status", g.reg.size()));
    return false;
  }
  bool step() override { return true; }
};

std::unique_ptr<Block> make_block(const std::string& name, const std::string& arg) {
  if (name == "ghost_spawn") return std::make_unique<GhostSpawn>();
  if (name == "ghost_motion") {
    auto m = std::make_unique<GhostMotion>();
    m->arg = arg;
    return m;
  }
  if (name == "ghost_xsector") return std::make_unique<GhostXSector>();
  if (name == "sample") return std::make_unique<Sample>();
  if (name == "seat") return std::make_unique<Seat>();
  if (name == "takeover" || name == "takeover_docked") {
    auto t = std::make_unique<Takeover>();
    t->docked_variant = name == "takeover_docked";
    return t;
  }
  if (name == "persist_spawn") return std::make_unique<PersistSpawn>();
  if (name == "persist_check") return std::make_unique<PersistCheck>();
  if (name == "seta") return std::make_unique<Seta>();
  if (name == "pause_move") return std::make_unique<PauseMove>();
  if (name == "cleanup") return std::make_unique<Cleanup>();
  if (name == "s13_check") return std::make_unique<Check>();
  return nullptr;
}

}  // namespace

// =====================================================================================================================
// Interface
// =====================================================================================================================
void set_env(const Env& env) { g.env = env; }

bool handles(std::string_view block) { return is_s13_block(parse_block(block).name); }

bool active() { return g_blk != nullptr; }
std::string active_name() { return g_blk ? g.block_name : std::string(); }

bool start(const Config& cfg, std::string_view block) {
  const BlockName bn = parse_block(block);
  if (!is_s13_block(bn.name)) return false;
  g.cfg = cfg;
  g.tag = "S13";
  if (bn.name == "s13_stop") {
    if (g_blk) {
      g.block_name = bn.name;
      say("INFO", std::format("stopping active block '{}'", active_name()));
      const std::string was = active_name();
      g.block_name = was;
      g_blk->abort("s13_stop");
      g_blk.reset();
    } else {
      g.block_name = bn.name;
      say("INFO", "no active block");
    }
    return true;
  }
  if (bn.name == "s13_status") {
    g.block_name = bn.name;
    registry_load();
    say("INFO", std::format("active='{}' registry_entries={} native_frames={}", active_name(), g.reg.size(), g.native_frames.load()));
    return true;
  }
  if (g_blk) {
    say("WARN", std::format("block '{}' was still running: aborted for '{}'", g.block_name, bn.name));
    g_blk->abort("replaced by a new block");
    g_blk.reset();
  }
  g.block_name = bn.name;
  auto b = make_block(bn.name, bn.arg);
  if (!b) return true;
  b->t0 = qpc();
  say("INFO", std::format("START arg='{}'", bn.arg));
  bool keep = false;
  try {
    keep = b->begin();
  } catch (const std::exception& e) {
    say("FAIL", std::format("exception in begin: {}", e.what()));
  }
  if (keep) g_blk = std::move(b);
  else say("INFO", "END (finished in begin)");
  return true;
}

void tick() {
  g.last_tick_prev = g.last_tick;
  g.last_tick = qpc();
  if (!g_blk) return;
  bool done = false;
  try {
    done = g_blk->step();
  } catch (const std::exception& e) {
    say("FAIL", std::format("exception in step: {}", e.what()));
    done = true;
  }
  if (done) {
    say("INFO", std::format("END after {:.1f} s", g_blk->elapsed_s()));
    g_blk.reset();
  }
}

void native_tick() { g.native_frames.fetch_add(1, std::memory_order_relaxed); }

void shutdown() {
  if (g_blk) {
    say("WARN", std::format("DLL shutdown (save load / reloadui) while '{}' was running: abandoned", g.block_name));
    g_blk->abort("shutdown");
    g_blk.reset();
  }
}

}  // namespace x4mp_probe::s13
