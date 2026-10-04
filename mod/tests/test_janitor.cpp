// M3-13 save hygiene v1: the janitor (removal on both roles, waits, protections) and the authority's checkpoint check.
// Driven through a real ModHost + FakePlatform with a fake universe (a list of objects behind the faction-ship exports); no SDK.

#include <algorithm>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "features/avatars/avatar_hub.h"
#include "features/diag/diag_hub.h"
#include "features/ghosts/ghost_core.h"
#include "features/ghosts/ghost_hub.h"
#include "features/janitor/janitor_feature.h"
#include "features/janitor/janitor_plan.h"
#include "game/main_thread.h"
#include "game/safe_remove.h"
#include "host/mod_host.h"
#include "host_fakes.h"

using namespace x4mp;
using namespace x4mp::features;
using x4mp::game::UniverseId;

namespace {

struct JObj {
  UniverseId id = 0;
  std::string name;
  std::string faction;
  std::string idcode;
  bool station = false;
};

std::vector<JObj> g_world;
std::vector<std::string> g_defined;        // the factions the fake game lists
std::vector<std::string> g_asked;          // every faction a list export was asked about
std::vector<UniverseId> g_removed;         // what reached the game's removal backend
bool g_erase_on_remove = true;             // false: the game keeps listing a removed object (deferred destruction)
UniverseId g_occupied = 0;                 // the player's ship (the guard)
std::int64_t g_clock_ns = 1'000'000'000;
double g_time = 100.0;

std::int64_t fk_clock() noexcept { return g_clock_ns; }
double fk_time() { return g_time; }
bool fk_paused() { return false; }
game::GameVersionPod fk_version() { return {9, 0}; }
const char* fk_suffix() { return "611726"; }
UniverseId fk_occupied() { return g_occupied; }
UniverseId fk_player() { return 9000; }

std::vector<const JObj*> select(const char* faction, bool station) {
  std::vector<const JObj*> out;
  for (const auto& o : g_world) {
    if (o.station == station && o.faction == faction) out.push_back(&o);
  }
  return out;
}
std::uint32_t jw_num_ships(const char* f) {
  g_asked.emplace_back(f);
  return static_cast<std::uint32_t>(select(f, false).size());
}
std::uint32_t jw_num_stations(const char* f) {
  g_asked.emplace_back(f);
  return static_cast<std::uint32_t>(select(f, true).size());
}
std::uint32_t jw_list(UniverseId* r, std::uint32_t n, const char* f, bool station) {
  const auto v = select(f, station);
  const std::uint32_t got = std::min<std::uint32_t>(n, static_cast<std::uint32_t>(v.size()));
  for (std::uint32_t i = 0; i < got; ++i) r[i] = v[i]->id;
  return got;
}
std::uint32_t jw_ships(UniverseId* r, std::uint32_t n, const char* f) { return jw_list(r, n, f, false); }
std::uint32_t jw_stations(UniverseId* r, std::uint32_t n, const char* f) { return jw_list(r, n, f, true); }
const char* jw_name(UniverseId id) {
  for (const auto& o : g_world) {
    if (o.id == id) return o.name.c_str();
  }
  return "";
}
const char* jw_idcode(UniverseId id) {
  for (const auto& o : g_world) {
    if (o.id == id) return o.idcode.c_str();
  }
  return "";
}
std::uint32_t jw_num_factions(bool) { return static_cast<std::uint32_t>(g_defined.size()); }
std::uint32_t jw_factions(const char** r, std::uint32_t n, bool) {
  const std::uint32_t got = std::min<std::uint32_t>(n, static_cast<std::uint32_t>(g_defined.size()));
  for (std::uint32_t i = 0; i < got; ++i) r[i] = g_defined[i].c_str();
  return got;
}
void jw_remove(game::ComponentId id) {
  g_removed.push_back(id);
  if (g_erase_on_remove) std::erase_if(g_world, [id](const JObj& o) { return o.id == id; });
}

struct Fixture {
  test::TempDir dir;
  test::FakePlatform platform{dir.path};
  std::vector<std::pair<log::Level, std::string>> forwarded;
  bool backend = true;  // the game's removal export (ModHost::init installs its own, so Rig puts ours back afterwards)
  Fixture() {
    platform.functions["GetGameVersion"] = reinterpret_cast<void*>(&fk_version);
    platform.functions["GetBuildVersionSuffix"] = reinterpret_cast<void*>(&fk_suffix);
    platform.functions["GetCurrentGameTime"] = reinterpret_cast<void*>(&fk_time);
    platform.functions["IsGamePaused"] = reinterpret_cast<void*>(&fk_paused);
    platform.functions["GetPlayerOccupiedShipID"] = reinterpret_cast<void*>(&fk_occupied);
    platform.functions["GetPlayerID"] = reinterpret_cast<void*>(&fk_player);
    game::main_thread().reset();
    game::set_player_guard({});
    diag_hub().reset();
    avatars::avatar_hub().reset();
    ghosts::ghost_hub().detach();
    g_world.clear();
    g_defined = {"argon", "player", "x4mp_team_1", "x4mp_team_2"};
    g_asked.clear();
    g_removed.clear();
    g_erase_on_remove = true;
    g_occupied = 0;
    g_clock_ns = 1'000'000'000;
    g_time = 100.0;
    game::set_remove_backend(&jw_remove);
    diag_hub().set_log_sender([this](log::Level l, std::string_view t) {
      forwarded.emplace_back(l, std::string(t));
      return true;
    });
  }
  ~Fixture() {
    diag_hub().reset();
    avatars::avatar_hub().reset();
    ghosts::ghost_hub().detach();
    game::main_thread().reset();
    game::set_player_guard({});
    game::set_remove_backend(nullptr);
  }
  void exports(bool factions = true) {
    platform.functions["GetNumAllFactionShips"] = reinterpret_cast<void*>(&jw_num_ships);
    platform.functions["GetNumAllFactionStations"] = reinterpret_cast<void*>(&jw_num_stations);
    platform.functions["GetAllFactionShips"] = reinterpret_cast<void*>(&jw_ships);
    platform.functions["GetAllFactionStations"] = reinterpret_cast<void*>(&jw_stations);
    platform.functions["GetComponentName"] = reinterpret_cast<void*>(&jw_name);
    platform.functions["GetObjectIDCode"] = reinterpret_cast<void*>(&jw_idcode);
    if (factions) {
      platform.functions["GetNumAllFactions"] = reinterpret_cast<void*>(&jw_num_factions);
      platform.functions["GetAllFactions"] = reinterpret_cast<void*>(&jw_factions);
    }
  }
  [[nodiscard]] bool forwarded_contains(const std::string& s) const {
    return std::ranges::any_of(forwarded, [&](const auto& p) { return p.second.find(s) != std::string::npos; });
  }
};

// A host with the janitor only and a controllable frame clock (every frame_s() call advances the clock).
struct Rig {
  Fixture& f;
  JanitorFeature* jan = nullptr;
  std::unique_ptr<host::ModHost> host;
  host::PreviousRun prev;
  [[nodiscard]] host::HostContext ctx() {
    return host::HostContext{host->config(), host->game(), *host->log(), f.platform, const_cast<host::FrameBudget&>(host->budget()), host->gates(), prev, "", &host->paths()};
  }
  explicit Rig(Fixture& fx) : f(fx) {
    host::HostOptions o;
    o.clock = &fk_clock;
    o.register_features = [this](host::FeatureRegistry& r) {
      auto j = std::make_unique<JanitorFeature>();
      jan = j.get();
      r.add(std::move(j));
    };
    host = std::make_unique<host::ModHost>(f.platform, o);
    REQUIRE(host->init() == host::InitResult::Started);
    game::set_remove_backend(f.backend ? &jw_remove : nullptr);
  }
  void frames(int n, double each_s = 1.0) {
    for (int i = 0; i < n; ++i) {
      g_clock_ns += static_cast<std::int64_t>(each_s * 1e9);
      host->on_frame();
    }
  }
  // a save load: game loaded, universe ready (a REAL one)
  void load_save() {
    host->on_game_loaded();
    host->on_universe_ready();
  }
  void sweep_after_grace() { frames(static_cast<int>(JanitorFeature::kStandaloneGraceS) + 3); }
};

bool removed(UniverseId id) { return std::ranges::find(g_removed, id) != g_removed.end(); }
}  // namespace

// ---- the pure rule ------------------------------------------------------------------------------------------------------------

TEST_CASE("janitor rule: ours = '[MP] ' name, or team-owned on a client; alive wins over leftover", "[janitor][m313]") {
  JanitorProtection p;
  const std::function<std::string()> none;
  CHECK(janitor_decide(1, "Argon Elite", false, none, p) == JanitorVerdict::NotOurs);
  CHECK(janitor_decide(2, "[MP] Bob", false, none, p) == JanitorVerdict::Remove);
  CHECK(janitor_decide(3, "Plain", true, none, p) == JanitorVerdict::NotOurs);  // team-owned without the prefix: only a client removes it
  p.remove_team_owned = true;
  CHECK(janitor_decide(3, "Plain", true, none, p) == JanitorVerdict::Remove);
  p.is_ghost = [](std::uint64_t id) { return id == 2; };
  CHECK(janitor_decide(2, "[MP] Bob", false, none, p) == JanitorVerdict::KeepGhost);
  p.own_ids = {4};
  CHECK(janitor_decide(4, "[MP] Me", false, none, p) == JanitorVerdict::KeepOwn);
  p.avatar_ids = {5};
  p.avatar_idcodes = {"ABC-123"};
  CHECK(janitor_decide(5, "[MP] A", false, none, p) == JanitorVerdict::KeepAvatar);
  CHECK(janitor_decide(6, "[MP] B", false, [] { return std::string("ABC-123"); }, p) == JanitorVerdict::KeepAvatar);
  CHECK(janitor_decide(7, "[MP] C", false, [] { return std::string("ZZZ-999"); }, p) == JanitorVerdict::Remove);
  p.is_guarded = [](std::uint64_t id) { return id == 8; };
  CHECK(janitor_decide(8, "[MP] D", false, none, p) == JanitorVerdict::KeepGuarded);
}

// ---- the sweep (standalone) ---------------------------------------------------------------------------------------------------

TEST_CASE("janitor: after a save load it removes only '[MP] ' objects, ships and stations, and logs the count", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_world = {{1, "My Ship", "player", ""},          {2, "[MP] Bob", "player", ""},        {3, "Other", "player", ""},
             {4, "[MP] Station", "x4mp_team_1", "", true}, {5, "[MP] Alice", "x4mp_team_2", ""}, {6, "Team trader", "x4mp_team_1", ""},
             {7, "[MP] Argon NPC", "argon", ""}};  // not a faction the janitor looks at
  Rig r(f);
  r.frames(2);
  CHECK_FALSE(r.jan->total().ran);  // no universe yet
  r.load_save();
  r.frames(2);
  CHECK_FALSE(r.jan->swept());  // standalone: still inside the grace period
  CHECK(g_removed.empty());
  r.sweep_after_grace();
  CHECK(r.jan->swept());
  CHECK(removed(2));
  CHECK(removed(4));
  CHECK(removed(5));
  CHECK(g_removed.size() == 3);  // not "My Ship", "Other", the unprefixed team ship (authority-side rule), the Argon NPC
  CHECK(r.jan->total().removed == 3);
  CHECK(r.jan->total().marked == 3);
  CHECK(r.jan->total().scanned == 6);
  CHECK(r.f.forwarded_contains("swept: 3 leftover"));
  const auto n = g_removed.size();
  r.frames(5);
  CHECK(g_removed.size() == n);  // one sweep per universe
}

TEST_CASE("janitor: a game that keeps listing a removed object does not make it act twice", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_erase_on_remove = false;
  g_world = {{2, "[MP] Bob", "player", ""}};
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  CHECK(g_removed == std::vector<UniverseId>{2});
  CHECK(r.jan->total().removed == 1);
}

TEST_CASE("janitor: never removes a guarded id (the player's own ship) or an object the game refuses", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_occupied = 2;  // the player sits in a ship that happens to carry the prefix (a client's own avatar copy)
  g_world = {{2, "[MP] Me", "player", ""}, {3, "[MP] Bob", "player", ""}};
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  CHECK_FALSE(removed(2));
  CHECK(removed(3));
  CHECK(r.jan->total().kept_guarded == 1);
}

TEST_CASE("janitor: a live ghost is never removed", "[janitor][m313]") {
  Fixture f;
  f.exports();
  ghosts::GhostCore core({}, {});
  core.driver.registries().ghosts.add({7, 1, 3, 1});  // net 7, player 1, local id 3, team 1
  ghosts::ghost_hub().attach(&core);
  g_world = {{3, "[MP] Bob", "x4mp_team_1", ""}, {4, "[MP] Stale", "x4mp_team_1", ""}};
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  CHECK_FALSE(removed(3));
  CHECK(removed(4));
  CHECK(r.jan->total().kept_ghost == 1);
}

TEST_CASE("janitor: an authority avatar (bound id or record idcode) survives, a stray does not", "[janitor][m313]") {
  Fixture f;
  f.exports();
  avatars::avatar_hub().set_protect_fn([] {
    avatars::AvatarProtect p;
    p.ids = {10};
    p.idcodes = {"KEEP-001"};
    return p;
  });
  g_world = {{10, "[MP] Alice", "x4mp_team_1", "NONE"},   // bound this load
             {11, "[MP] Bob", "x4mp_team_1", "KEEP-001"},  // not bound yet, known by idcode
             {12, "[MP] Ghost", "x4mp_team_2", "OLD-777"}};  // no record: a leftover
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  CHECK_FALSE(removed(10));
  CHECK_FALSE(removed(11));
  CHECK(removed(12));
  CHECK(r.jan->total().kept_avatar == 2);
}

TEST_CASE("janitor: the faction list is respected (no error lines for undefined team factions)", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_defined = {"argon", "player"};
  g_world = {{2, "[MP] Bob", "player", ""}};
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  CHECK(r.jan->last().factions_queried == 1);
  REQUIRE_FALSE(g_asked.empty());
  for (const auto& a : g_asked) CHECK(a == "player");
}

TEST_CASE("janitor: without the faction list exports only the player is scanned", "[janitor][m313]") {
  Fixture f;
  f.exports(false);
  g_world = {{2, "[MP] Bob", "player", ""}, {4, "[MP] Station", "x4mp_team_1", "", true}};
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  for (const auto& a : g_asked) CHECK(a == "player");
  CHECK(removed(2));
  CHECK_FALSE(removed(4));
}

TEST_CASE("janitor: skipped when the list exports are missing", "[janitor][m313]") {
  Fixture f;
  Rig r(f);
  r.load_save();
  r.sweep_after_grace();
  CHECK(r.jan->last().exports_missing);
  CHECK_FALSE(r.jan->last().ran);
  CHECK(r.jan->swept());
}

// ---- when it runs --------------------------------------------------------------------------------------------------------------

TEST_CASE("janitor: /reloadui is the same universe, nothing is swept", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_world = {{2, "[MP] Bob", "player", ""}};
  {
    Rig r(f);
    r.load_save();
    r.frames(2);  // inside the grace: still pending when the DLL is unloaded
    CHECK(r.jan->pending());
    r.host->shutdown();
  }
  // /reloadui: the DLL is re-initialised, X4Native replays on_game_loaded only; the host opens the gate itself
  Rig r2(f);
  r2.host->on_game_loaded();
  REQUIRE(r2.host->gates().universe_ready);
  CHECK(r2.host->gates().universe_ready_after_reload);
  CHECK(r2.jan->pending());  // a wait in progress is kept (stash), not restarted from nothing
  r2.sweep_after_grace();
  CHECK(removed(2));  // the carried wait finishes
  r2.host->shutdown();

  // a reload after a COMPLETED sweep sweeps nothing
  g_removed.clear();
  g_world = {{3, "[MP] Late", "player", ""}};
  Rig r3(f);
  r3.host->on_game_loaded();
  REQUIRE(r3.host->gates().universe_ready_after_reload);
  CHECK_FALSE(r3.jan->pending());
  r3.sweep_after_grace();
  CHECK(g_removed.empty());
  // ... but a real save load does
  r3.load_save();
  CHECK_FALSE(r3.host->gates().universe_ready_after_reload);
  r3.sweep_after_grace();
  CHECK(removed(3));
}

TEST_CASE("janitor: a pending session holds the sweep (a launch auto-connect is not raced)", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_world = {{2, "[MP] Bob", "player", ""}};
  Rig r(f);
  r.load_save();
  diag_hub().set_session_pending(true);
  r.frames(60);
  CHECK(g_removed.empty());
  CHECK_FALSE(r.jan->swept());
  diag_hub().set_session_pending(false);  // the session ended without a takeover: the grace starts again
  r.frames(5);
  CHECK(g_removed.empty());
  r.sweep_after_grace();
  CHECK(removed(2));
}

TEST_CASE("janitor: a client waits for the takeover to be Done and spares the takeover's own objects", "[janitor][m313]") {
  Fixture f;
  f.exports();
  // checkpoint avatars of everybody, the vacated host copy, a ghost, and one unprefixed team ship
  g_world = {{20, "[MP] Me", "x4mp_team_1", "AV-ME"}, {21, "[MP] Bob", "x4mp_team_1", "AV-BOB"}, {22, "[MP] Host copy", "player", ""},
             {23, "Renamed", "x4mp_team_2", ""},       {24, "[MP] Leftover", "player", ""}};
  Rig r(f);
  r.load_save();
  avatars::TakeoverStatus ts;
  ts.active = true;
  ts.done = false;
  ts.avatar_id = 20;
  ts.host_id = 22;
  avatars::avatar_hub().set_takeover_status(ts);
  r.frames(80);  // way past the standalone grace: a client in a takeover never sweeps early
  CHECK(g_removed.empty());
  CHECK(r.jan->pending());

  ts.done = true;
  avatars::avatar_hub().set_takeover_status(ts);
  r.frames(3);
  CHECK(r.jan->swept());
  CHECK_FALSE(removed(20));  // the own avatar copy
  CHECK_FALSE(removed(22));  // the vacated host copy, protected by id
  CHECK(removed(21));        // another player's avatar copy that the takeover missed
  CHECK(removed(23));        // client role: team-owned leftovers go even without the prefix
  CHECK(removed(24));
  CHECK(r.jan->total().kept_own == 2);
}

TEST_CASE("janitor: the authority sweeps without waiting for a grace once connected", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_world = {{2, "[MP] Old", "player", ""}, {30, "Team HQ ship", "x4mp_team_1", ""}};
  Rig r(f);
  r.load_save();
  diag_hub().set_session_pending(true);
  diag_hub().set_connection(NodeRole::Authority, true);
  r.frames(2);
  CHECK(removed(2));
  CHECK_FALSE(removed(30));  // the authority never removes a team ship for lacking the prefix
}

// ---- the checkpoint check -------------------------------------------------------------------------------------------------------

TEST_CASE("checkpoint check: clean authority universe -> ghosts_cleaned, avatars untouched", "[janitor][m313]") {
  Fixture f;
  f.exports();
  avatars::avatar_hub().set_protect_fn([] {
    avatars::AvatarProtect p;
    p.idcodes = {"AV-1"};
    return p;
  });
  g_world = {{1, "My Ship", "player", ""}, {2, "[MP] Alice", "x4mp_team_1", "AV-1"}};
  Rig r(f);
  r.load_save();
  r.frames(1);
  auto ctx = r.ctx();
  const auto h = JanitorFeature::checkpoint_check(ctx);
  CHECK(h.ran);
  CHECK(h.clean);
  CHECK(h.stale_found == 0);
  CHECK(h.avatars_kept == 1);
  CHECK(g_removed.empty());
}

TEST_CASE("checkpoint check: stale leftovers are removed and counted; the player's guarded ship is not stale", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_world = {{2, "[MP] Stale ghost", "x4mp_team_1", "G-1"}, {3, "[MP] Mine", "player", ""}, {4, "Plain", "player", ""}};
  g_occupied = 3;
  Rig r(f);
  r.load_save();
  r.frames(1);
  auto ctx = r.ctx();
  const auto h = JanitorFeature::checkpoint_check(ctx);
  CHECK(h.ran);
  CHECK(h.stale_found == 1);
  CHECK(h.removed == 1);
  CHECK(h.remaining == 0);
  CHECK(h.clean);
  CHECK(removed(2));
  CHECK_FALSE(removed(3));
  CHECK(f.forwarded_contains("ghosts_cleaned=true"));
}

TEST_CASE("checkpoint check: a leftover that cannot be removed flags ghosts_cleaned=false", "[janitor][m313]") {
  Fixture f;
  f.exports();
  g_world = {{2, "[MP] Stale", "x4mp_team_1", ""}, {3, "[MP] Stale 2", "player", ""}};
  f.backend = false;  // the game cannot remove: every attempt is refused
  Rig r(f);
  r.load_save();
  r.frames(1);
  auto ctx = r.ctx();
  const auto h = JanitorFeature::checkpoint_check(ctx);
  CHECK(h.stale_found == 2);
  CHECK(h.removed == 0);
  CHECK(h.remaining == 2);
  CHECK_FALSE(h.clean);
  CHECK(f.forwarded_contains("ghosts_cleaned=false"));
}

TEST_CASE("checkpoint check: a non-empty ghost registry on the authority is not clean; missing exports are unverifiable but clean", "[janitor][m313]") {
  Fixture f;
  f.exports();
  ghosts::GhostCore core({}, {});
  core.driver.registries().ghosts.add({7, 1, 99, 1});
  ghosts::ghost_hub().attach(&core);
  Rig r(f);
  r.load_save();
  r.frames(1);
  auto ctx = r.ctx();
  auto h = JanitorFeature::checkpoint_check(ctx);
  CHECK(h.ghost_registry == 1);
  CHECK_FALSE(h.clean);

  ghosts::ghost_hub().detach();
  f.platform.functions.erase("GetComponentName");  // exports incomplete
  h = JanitorFeature::checkpoint_check(ctx);
  CHECK_FALSE(h.ran);
  CHECK(h.clean);
}
