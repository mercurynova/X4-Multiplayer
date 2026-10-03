// x4mp_probe S13 tests (M3-001): the pure helpers, the config keys, and a smoke that loads EVERY S13 block against a tiny
// fake world behind the stub X4NativeAPI (no game). The blocks run with 1-second durations.

#include <catch2/catch_test_macros.hpp>
#include <catch2/catch_approx.hpp>

#include <chrono>
#include <cmath>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <sstream>
#include <thread>

#include "../src/probe_config.h"
#include "../src/s13.h"
#include "stub_host.h"
#include <x4n_core.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace fs = std::filesystem;
using namespace x4mp_probe;
using namespace x4mp_probe::s13;
using namespace std::chrono_literals;

// ---------------------------------------------------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------------------------------------------------
TEST_CASE("s13: block name parsing", "[s13][pure]") {
  CHECK(parse_block("ghost_motion a").name == "ghost_motion");
  CHECK(parse_block("ghost_motion a").arg == "a");
  CHECK(parse_block("  GHOST_MOTION   B ").arg == "b");
  CHECK(parse_block("ghost_motion_c").name == "ghost_motion");
  CHECK(parse_block("ghost_motion_c").arg == "c");
  CHECK(parse_block("takeover_docked").name == "takeover_docked");
  CHECK(parse_block("takeover_docked").arg.empty());
  for (const char* b : {"ghost_spawn", "ghost_motion", "ghost_xsector", "sample", "seat", "takeover", "takeover_docked", "persist_spawn", "persist_check", "seta",
                        "pause_move", "cleanup"})
    CHECK(is_s13_block(b));
  CHECK_FALSE(is_s13_block("money"));
  CHECK_FALSE(is_s13_block("dress"));  // M3-002's blocks go to Lua
  CHECK_FALSE(is_s13_block("saves1"));
}

TEST_CASE("s13: scratch slot comparison", "[s13][pure]") {
  CHECK(normalize_save_name(" SAVE_007.xml.gz ") == "save_007");
  CHECK(normalize_save_name("save_007.xml") == "save_007");
  CHECK(is_scratch_slot("save_007", "save_007"));
  CHECK(is_scratch_slot("Save_007.xml.gz", "save_007"));
  CHECK_FALSE(is_scratch_slot("save_001", "save_007"));
  CHECK_FALSE(is_scratch_slot("save_007", ""));  // unconfigured: always refuse
  CHECK_FALSE(is_scratch_slot("", ""));
  CHECK_FALSE(is_scratch_slot("quicksave", "save_007"));
}

TEST_CASE("s13: the removal gate never allows 0 or a guarded id", "[s13][pure][guard]") {
  RemoveGate gate;
  gate.guard = {10, 11, 12};
  CHECK(gate.check(0, true) == RemoveVerdict::BlockedInvalidId);
  CHECK(gate.check(10, true) == RemoveVerdict::BlockedGuard);
  CHECK(gate.check(12, false) == RemoveVerdict::BlockedGuard);
  CHECK(gate.check(99, false) == RemoveVerdict::BlockedNotValid);
  CHECK(gate.check(99, true) == RemoveVerdict::Allowed);
}

TEST_CASE("s13: paths, velocity and interpolation", "[s13][pure]") {
  Path c;
  c.kind = Path::Kind::Circle;
  c.center = {100, 5, -50};
  c.radius = 1000;
  c.speed = 300;
  const Vec3 p0 = path_position(c, 0);
  CHECK(distance(p0, c.center) == Catch::Approx(1000.0));
  CHECK(length(path_velocity(c, 3.7, 20)) == Catch::Approx(300.0));
  // velocity is the derivative of position
  const double h = 1e-4;
  const Vec3 num = (path_position(c, 2 + h) - path_position(c, 2 - h)) * (1.0 / (2 * h));
  CHECK(distance(num, path_velocity(c, 2, 20)) < 0.05);
  Path l;
  l.kind = Path::Kind::Line;
  l.speed = 3000;
  l.dir = {1, 0, 0};
  l.center = {-30000, 0, 1000};
  CHECK(path_position(l, 20).x == Catch::Approx(30000.0));
  CHECK(length(path_velocity(l, 5, 20)) == Catch::Approx(3000.0));

  std::vector<Vec3> keys{{0, 0, 0}, {10, 0, 0}, {20, 0, 0}};
  CHECK(interpolate_keys(keys, 0.05, 0.025).x == Catch::Approx(5.0));
  CHECK(interpolate_keys(keys, 0.05, 0.075).x == Catch::Approx(15.0));
  CHECK(interpolate_keys(keys, 0.05, 99).x == Catch::Approx(20.0));
  CHECK(interpolate_keys(keys, 0.05, -1).x == Catch::Approx(0.0));

  const Vec3 f = forward_from_angles(0, 0, 1);
  CHECK(f.z == Catch::Approx(1.0));
  const Vec3 f90 = forward_from_angles(kPi / 2, 0, 1);
  CHECK(f90.x == Catch::Approx(1.0));
  CHECK(forward_from_angles(0, 0.5, -1).y == Catch::Approx(-std::sin(0.5)));
}

TEST_CASE("s13: config keys, defaults and clamping", "[s13][config]") {
  const auto d = parse_config("{}");
  REQUIRE(d.ok);
  CHECK(d.config.s13.ghost_macro_s == "ship_arg_s_fighter_01_a_macro");
  CHECK(d.config.s13.ghost_faction == "x4mp_team_2");
  CHECK(d.config.s13.scratch_slot.empty());
  CHECK(d.config.s13.sample_seconds == 120);
  CHECK(d.config.s13.sample_hz == 20);
  const auto r = parse_config(R"({"scratch_slot":"save_007","sample_seconds":5,"sample_hz":9999,"pitch_sign":-3,"ghost_faction":"x4mp_team_1","spawn_distance_m":1})");
  REQUIRE(r.ok);
  CHECK(r.config.s13.scratch_slot == "save_007");
  CHECK(r.config.s13.sample_seconds == 5);
  CHECK(r.config.s13.sample_hz == 120);
  CHECK(r.config.s13.pitch_sign == -1);
  CHECK(r.config.s13.spawn_distance_m == 50);
  CHECK(r.config.s13.ghost_faction == "x4mp_team_1");
  CHECK(r.notes.empty());
}

// ---------------------------------------------------------------------------------------------------------------------
// Fake world behind the stub game table
// ---------------------------------------------------------------------------------------------------------------------
namespace {
struct Obj {
  std::string macro, owner, idcode, name, cls = "ship_s";
  std::uint64_t sector = 0;
  float x = 0, y = 0, z = 0, a0 = 0, a1 = 0, a2 = 0;
  bool active = true;
};
struct World {
  std::map<std::uint64_t, Obj> objs;
  std::uint64_t next_id = 1000;
  std::uint64_t occupied = 100, controlled = 100, container = 0;
  std::uint64_t player_obj = 50, player = 51;
  bool seta = false;
  double time = 0;
  bool time_runs = true;
  std::string save = "save_007";
  std::vector<std::uint64_t> removed;
  int teleport_calls = 0;
  std::string last_save_filename, last_save_name;
  static inline World* self = nullptr;
};
std::vector<std::string> g_strs;  // keeps returned const char* alive
const char* keep(const std::string& s) {
  g_strs.push_back(s);
  return g_strs.back().c_str();
}

void install(stub::Host& h, World& w) {
  World::self = &w;
  auto* g = h.game.get();
  g->GetPlayerOccupiedShipID = [] { return World::self->occupied; };
  g->GetPlayerControlledShipID = [] { return World::self->controlled; };
  g->GetPlayerObjectID = [] { return World::self->player_obj; };
  g->GetPlayerContainerID = [] { return World::self->container; };
  g->GetPlayerID = [] { return World::self->player; };
  g->IsValidComponent = [](UniverseID id) { return id == World::self->player || id == World::self->player_obj || World::self->objs.contains(id); };
  g->GetContextByClass = [](UniverseID id, const char* cls, bool self) -> UniverseID {
    auto it = World::self->objs.find(id);
    if (it == World::self->objs.end()) return 0;
    const std::string c = cls;
    const std::uint64_t sec = id <= 4 ? id : it->second.sector;  // ids 1..4 are the sectors themselves
    if (c == "sector") return sec;
    if (c == "cluster") return sec == 0 ? 0 : 900 + (sec + 1) / 2;
    if (c == "ship") return self ? id : 0;
    return 0;
  };
  g->GetObjectPositionInSector = [](UniverseID id) {
    UIPosRot r{};
    auto it = World::self->objs.find(id);
    if (it != World::self->objs.end()) r = {it->second.x, it->second.y, it->second.z, it->second.a0, it->second.a1, it->second.a2};
    return r;
  };
  g->SetObjectSectorPos = [](UniverseID id, UniverseID sec, UIPosRot p) {
    auto it = World::self->objs.find(id);
    if (it == World::self->objs.end()) return;
    it->second.sector = sec;
    it->second.x = p.x, it->second.y = p.y, it->second.z = p.z, it->second.a0 = p.yaw, it->second.a1 = p.pitch, it->second.a2 = p.roll;
  };
  g->SpawnObjectAtPos2 = [](const char* macro, UniverseID sec, UIPosRot p, const char* owner) -> UniverseID {
    Obj o;
    o.macro = macro;
    o.owner = owner;
    o.sector = sec;
    o.x = p.x, o.y = p.y, o.z = p.z, o.a0 = p.yaw, o.a1 = p.pitch, o.a2 = p.roll;
    const auto id = ++World::self->next_id;
    o.idcode = "TST-" + std::to_string(id);
    World::self->objs[id] = o;
    return id;
  };
  g->FindMacro = [](const char*) { return true; };
  g->ActivateObject = [](UniverseID id, bool a) {
    auto it = World::self->objs.find(id);
    if (it != World::self->objs.end()) it->second.active = a;
  };
  g->GetObjectIDCode = [](UniverseID id) -> const char* {
    auto it = World::self->objs.find(id);
    return it == World::self->objs.end() ? "" : keep(it->second.idcode);
  };
  g->GetComponentName = [](UniverseID id) -> const char* {
    auto it = World::self->objs.find(id);
    if (it != World::self->objs.end()) return keep(it->second.name.empty() ? it->second.idcode : it->second.name);
    return keep("Sector " + std::to_string(id));
  };
  g->GetComponentClass = [](UniverseID id) -> const char* {
    auto it = World::self->objs.find(id);
    return it == World::self->objs.end() ? "sector" : "ship_s";
  };
  g->GetOwnerDetails = [](UniverseID id) {
    FactionDetails d{};
    auto it = World::self->objs.find(id);
    d.factionID = it == World::self->objs.end() ? "" : keep(it->second.owner);
    return d;
  };
  g->GetNumOrders = [](UniverseID) -> uint32_t { return 0; };
  g->GetAllFactions = [](const char** r, uint32_t n, bool) -> uint32_t {
    const char* f[] = {"argon", "x4mp_team_1", "x4mp_team_2", "player"};
    for (uint32_t i = 0; i < 4 && i < n; ++i) r[i] = f[i];
    return 4;
  };
  g->GetNumAllFactionShips = [](const char* owner) -> uint32_t {
    uint32_t n = 0;
    for (auto& [id, o] : World::self->objs) n += o.owner == owner;
    return n;
  };
  g->GetAllFactionShips = [](UniverseID* r, uint32_t n, const char* owner) -> uint32_t {
    uint32_t i = 0;
    for (auto& [id, o] : World::self->objs)
      if (o.owner == owner && i < n) r[i++] = id;
    return i;
  };
  g->GetSectorsByOwner = [](UniverseID* r, uint32_t n, const char* owner) -> uint32_t {
    if (std::string(owner) != "argon" || n < 4) return 0;
    r[0] = 1, r[1] = 2, r[2] = 3, r[3] = 4;
    return 4;
  };
  g->IsSetaActive = [] { return World::self->seta; };
  g->GetCurrentGameTime = [] { return World::self->time; };
  g->IsGamePaused = [] { return !World::self->time_runs; };
  g->IsPlayerOccupiedShipDocked = [] { return false; };
  g->IsComponentOperational = [](UniverseID) { return true; };
  g->RemoveComponent = [](UniverseID id) {
    World::self->removed.push_back(id);
    World::self->objs.erase(id);
    if (World::self->occupied == id) World::self->occupied = 0;  // would be a Game Over: tests assert it never happens
  };
  g->CanTeleportPlayerTo = [](UniverseID, bool, bool) -> const char* { return ""; };
  g->TeleportPlayerTo = [](UniverseID id, bool, bool, bool) {
    World::self->teleport_calls++;
    World::self->occupied = id;
    World::self->controlled = id;
    return true;
  };
  g->GetLastSaveInfo = [] {
    UISaveInfo i{};
    i.filename = keep(World::self->save + ".xml.gz");
    i.name = keep(World::self->save);
    return i;
  };
}

struct Fixture {
  stub::Host host;
  World w;
  fs::path dir;
  std::mutex mu;
  std::vector<std::string> lines;
  static inline Fixture* self = nullptr;
  Config cfg;

  explicit Fixture(const char* tag) {
    self = this;
    dir = fs::temp_directory_path() / (std::string("x4mp_s13_test_") + tag + "_" + std::to_string(GetCurrentProcessId()));
    fs::remove_all(dir);
    fs::create_directories(dir);
    install(host, w);
    Obj ship;
    ship.macro = "ship_player";
    ship.owner = "player";
    ship.idcode = "PLAYER-SHIP";
    ship.sector = 1;
    ship.x = 1000, ship.y = 0, ship.z = 2000, ship.a0 = 0.5f;
    w.objs[100] = ship;
    for (std::uint64_t s = 1; s <= 4; ++s) {
      Obj sec;
      sec.cls = "sector";
      w.objs[s] = sec;  // sectors are "objects" only so GetContextByClass works on them; their ids are small
    }
    w.objs[100].sector = 1;
    x4n::detail::g_api = &host.api;
    Env env;
    env.log = +[](int, const std::string& b) {
      std::lock_guard lk(self->mu);
      self->lines.push_back(b);
    };
    env.dir = dir;
    set_env(env);
    cfg.s13.scratch_slot = "save_007";
    cfg.s13.drift_seconds = 1;
    cfg.s13.motion_seconds = 1;
    cfg.s13.sample_seconds = 1;
    cfg.s13.sample_hz = 50;
    cfg.s13.seat_seconds = 1;
    cfg.s13.seta_seconds = 3;
    cfg.s13.pause_wait_seconds = 4;
    cfg.s13.xsector_hold_seconds = 0;
  }
  ~Fixture() {
    shutdown();
    x4n::detail::g_api = nullptr;
    World::self = nullptr;
    self = nullptr;
    fs::remove_all(dir);
  }
  // Runs a block to completion (frames of ~2 ms). `each` is called every frame (e.g. to change the world).
  template <class F>
  bool run(const std::string& block, F&& each, std::chrono::seconds limit = 20s) {
    REQUIRE(start(cfg, block));
    const auto dl = std::chrono::steady_clock::now() + limit;
    while (active() && std::chrono::steady_clock::now() < dl) {
      each();
      native_tick();
      tick();
      std::this_thread::sleep_for(2ms);
    }
    return !active();
  }
  bool run(const std::string& block, std::chrono::seconds limit = 20s) { return run(block, [] {}, limit); }
  [[nodiscard]] bool has(const std::string& needle) {
    std::lock_guard lk(mu);
    for (const auto& l : lines)
      if (l.find(needle) != std::string::npos) return true;
    return false;
  }
  [[nodiscard]] int count(const std::string& needle) {
    std::lock_guard lk(mu);
    int n = 0;
    for (const auto& l : lines) n += l.find(needle) != std::string::npos;
    return n;
  }
  [[nodiscard]] bool lua_event(const std::string& name) {
    std::lock_guard lk(host.mu);
    for (const auto& [n, p] : host.lua_events)
      if (n == name) return true;
    return false;
  }
  [[nodiscard]] int ghosts() {
    int n = 0;
    for (const auto& [id, o] : w.objs) n += o.owner == "x4mp_team_2";
    return n;
  }
};
}  // namespace

TEST_CASE("s13 smoke: ghost_spawn, ghost_motion a/b/c, ghost_xsector, sample, seat, cleanup", "[s13][smoke]") {
  Fixture f("a");
  REQUIRE(f.run("s13_check"));
  CHECK(f.has("native functions present="));
  CHECK(f.has("MISSING=[none]"));

  REQUIRE(f.run("ghost_spawn"));
  CHECK(f.has("PASS spawn S ghost"));
  CHECK(f.has("PASS spawn M ghost"));
  CHECK(f.has("owner faction 'x4mp_team_2' exists"));
  CHECK(f.has("PASS drift ghost_s"));
  CHECK(f.has("PASS drift ghost_m"));
  CHECK(f.ghosts() == 2);
  CHECK(f.lua_event("x4mp.spike_dress"));
  // Idempotent: a second run leaves exactly one pair.
  REQUIRE(f.run("ghost_spawn"));
  CHECK(f.ghosts() == 2);
  CHECK(f.has("removed 2 previous ghosts"));

  for (const char* m : {"ghost_motion a", "ghost_motion b", "ghost_motion_c"}) {
    REQUIRE(f.run(m));
  }
  CHECK(f.count("PASS segment circle_100") == 3);
  CHECK(f.count("PASS segment line_3000") == 3);
  CHECK(f.has("SetObjectSectorPos cost n="));
  CHECK(f.lua_event("x4mp.spike_velocity"));
  CHECK(f.has("x4mp.spike_velocity payload="));
  REQUIRE(f.run("ghost_motion z"));
  CHECK(f.has("usage: ghost_motion"));

  REQUIRE(f.run("ghost_xsector"));
  CHECK(f.has("PASS context after"));
  CHECK(f.has("PASS return check"));

  REQUIRE(f.run("sample"));
  CHECK(f.has("PASS sample done"));
  CHECK(f.has("unit guess"));
  CHECK(f.has("sample k=0"));

  REQUIRE(f.run("seat"));
  CHECK(f.has("seat_edge frame=1"));
  CHECK(f.has("PASS seat watch done"));

  REQUIRE(f.run("cleanup"));
  CHECK(f.ghosts() == 0);
  for (const auto id : f.w.removed) CHECK(id != 100);  // never the player's ship
  CHECK(f.w.occupied == 100);
}

TEST_CASE("s13 smoke: takeover removes the original only on the scratch slot and never a guarded id", "[s13][smoke][guard]") {
  {
    Fixture f("b1");
    f.w.save = "save_001";  // not the scratch slot
    REQUIRE(f.run("takeover"));
    CHECK(f.has("REFUSED"));
    CHECK(f.w.teleport_calls == 0);
    CHECK(f.w.removed.empty());
    REQUIRE(f.run("persist_spawn"));
    CHECK(f.has("REFUSED"));
  }
  {
    Fixture f("b2");
    f.cfg.s13.scratch_slot = "";  // unconfigured: refuses everywhere
    REQUIRE(f.run("takeover"));
    CHECK(f.has("REFUSED"));
    CHECK(f.w.removed.empty());
  }
  {
    Fixture f("b3");
    REQUIRE(f.run("takeover"));
    CHECK(f.w.teleport_calls == 1);
    CHECK(f.has("PASS player guard sees the new ship"));
    REQUIRE(f.w.removed.size() == 1);
    CHECK(f.w.removed[0] == 100);
    CHECK(f.w.occupied != 0);  // the player kept a ship
    CHECK(f.has("PASS 30 frames after the removal"));
    // cleanup refuses the player's current (guarded) ship.
    REQUIRE(f.run("cleanup"));
    CHECK(f.has("blocked_by_guard"));
    CHECK(f.w.objs.contains(f.w.occupied));
  }
  {
    Fixture f("b4");  // the teleport is never confirmed: the original must stay
    f.w.occupied = 0;
    f.w.controlled = 0;
    f.w.container = 100;  // standing in the ship after a load
    f.host.game->TeleportPlayerTo = [](UniverseID, bool, bool, bool) {
      World::self->teleport_calls++;
      return true;  // claims success but nothing changes
    };
    REQUIRE(f.run("takeover", std::chrono::seconds(30)));
    CHECK(f.has("teleport not confirmed"));
    CHECK(f.w.removed.empty());
    CHECK(f.w.objs.contains(100));
  }
  {
    Fixture f("b5");
    f.w.occupied = 0;
    f.w.controlled = 0;
    f.w.container = 100;
    REQUIRE(f.run("takeover_docked"));  // not docked (IsPlayerOccupiedShipDocked false): precondition fails, nothing happens
    CHECK(f.has("needs the player docked"));
    CHECK(f.w.teleport_calls == 0);
  }
}

TEST_CASE("s13 smoke: persist_spawn / persist_check find the ship by idcode after the ids change", "[s13][smoke]") {
  Fixture f("c");
  REQUIRE(f.run("persist_spawn"));
  CHECK(f.has("PASS recorded id="));
  REQUIRE(fs::exists(f.dir / "x4mp_probe_s13_persist.json"));
  // Simulate save + reload: the ship gets a new id, same idcode, same position.
  std::uint64_t old_id = 0;
  for (const auto& [id, o] : f.w.objs)
    if (o.owner == "x4mp_team_2") old_id = id;
  REQUIRE(old_id != 0);
  Obj o = f.w.objs[old_id];
  f.w.objs.erase(old_id);
  f.w.objs[5555] = o;
  REQUIRE(f.run("persist_check"));
  CHECK(f.has("found by idcode: new_id=5555"));
  CHECK(f.has("PASS found by idcode"));
  CHECK(f.has("PASS active-state check"));
  REQUIRE(f.run("cleanup"));
  CHECK(f.ghosts() == 0);
}

TEST_CASE("s13 smoke: seta and pause_move", "[s13][smoke]") {
  Fixture f("d");
  // seta: the user turns SETA on at ~0.3 s; the (simulated) MD handler turns it off when the Lua event arrives.
  int frames = 0;
  f.host.on_lua = [&f](const std::string& n, const std::string&) {
    if (n == "x4mp.spike_seta_off") f.w.seta = false;
  };
  REQUIRE(f.run("seta", [&] {
    if (++frames == 30) f.w.seta = true;
  }));
  CHECK(f.lua_event("x4mp.spike_seta_off"));
  CHECK(f.has("PASS SETA went off"));
  CHECK(f.has("on_edges=1"));

  // pause_move: game time stops for a while (paused) while frames keep ticking.
  frames = 0;
  REQUIRE(f.run("pause_move", [&] {
    ++frames;
    if (frames < 40) f.w.time += 0.002;
    else if (frames < 120) f.w.time_runs = false;  // time frozen
    else {
      f.w.time_runs = true;
      f.w.time += 0.002;
    }
  }));
  CHECK(f.has("pause detected"));
  CHECK(f.has("PASS pause ended"));
  CHECK(f.has("PASS SetObjectSectorPos while paused"));
  REQUIRE(f.run("cleanup"));
}

TEST_CASE("s13 smoke: a missing native function is logged, never crashes", "[s13][smoke]") {
  Fixture f("e");
  f.host.game->SpawnObjectAtPos2 = nullptr;
  f.host.game->TeleportPlayerTo = nullptr;
  REQUIRE(f.run("s13_check"));
  CHECK(f.has("MISSING=[SpawnObjectAtPos2 TeleportPlayerTo"));
  REQUIRE(f.run("ghost_spawn"));
  CHECK(f.has("MISSING native function=SpawnObjectAtPos2"));
  CHECK(f.has("FAIL"));
  CHECK(f.w.removed.empty());
}

TEST_CASE("s13: starting a block while another runs aborts the first; s13_stop ends it", "[s13][smoke]") {
  Fixture f("f");
  f.cfg.s13.sample_seconds = 60;
  REQUIRE(start(f.cfg, "sample"));
  tick();
  CHECK(active());
  CHECK(active_name() == "sample");
  REQUIRE(start(f.cfg, "seat"));
  CHECK(f.has("was still running: aborted for 'seat'"));
  CHECK(active_name() == "seat");
  REQUIRE(start(f.cfg, "s13_stop"));
  CHECK_FALSE(active());
  CHECK_FALSE(handles("money"));
  CHECK(handles("ghost_motion b"));
}
