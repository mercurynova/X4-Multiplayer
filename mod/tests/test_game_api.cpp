#include <algorithm>
#include <array>
#include <atomic>
#include <optional>
#include <string>
#include <thread>

#include <catch2/catch_test_macros.hpp>

#include "game/game_api.h"
#include "game/main_thread.h"
#include "game/player_guard.h"
#include "game/safe_remove.h"

using namespace x4mp::game;

namespace {
// ---- fake game exports ----
double fk_time() { return 1234.5; }
const char* fk_save_path() { return "C:/fake/saves"; }
bool fk_list_complete() { return true; }
bool fk_save_valid(const char* f) { return std::string(f) == "good.xml.gz"; }
int g_reloads = 0;
void fk_reload() { ++g_reloads; }
bool g_paused = true;
bool fk_paused() { return g_paused; }
GameVersionPod fk_version() { return {9, 0}; }
const char* fk_suffix() { return "611726"; }

UniverseId g_occupied = 0, g_controlled = 0, g_player = 0, g_object = 0, g_container = 0;
UniverseId fk_occupied() { return g_occupied; }
UniverseId fk_controlled() { return g_controlled; }
UniverseId fk_player() { return g_player; }
UniverseId fk_object() { return g_object; }
UniverseId fk_container() { return g_container; }
UniverseId fk_context(UniverseId id, const char* cls, bool) {
  if (std::string(cls) == "ship" && id == 50) return 500;  // the player object sits in ship 500
  return 0;
}
bool fk_valid(UniverseId id) { return id != 666; }

// M3 fakes
PosRotPod g_set_pos;
UniverseId g_set_sector = 0;
UniverseId fk_spawn(const char* macro, UniverseId sector, PosRotPod pos, const char* owner) {
  g_set_pos = pos;
  g_set_sector = sector;
  return std::string(macro) == "bad" || std::string(owner) == "nobody" ? 0 : 9001;
}
bool g_active = true, g_radar = false, g_seta = true, g_docked = true;
void fk_activate(UniverseId, bool a) { g_active = a; }
void fk_setpos(UniverseId, UniverseId sector, PosRotPod pos) {
  g_set_sector = sector;
  g_set_pos = pos;
}
PosRotPod fk_getpos(UniverseId) { return g_set_pos; }
bool fk_teleport(UniverseId id, bool, bool, bool) { return id == 9001; }
const char* fk_canteleport(UniverseId id, bool, bool) { return id == 9001 ? "granted" : "no such ship"; }
std::string g_owner;
void fk_setowner(UniverseId, const char* f) { g_owner = f; }
void fk_radar(UniverseId, bool v) { g_radar = v; }
bool fk_seta() { return g_seta; }
const char* fk_idcode(UniverseId) { return "ABC-123"; }
const char* fk_cname(UniverseId) { return nullptr; }
bool fk_wrecked(UniverseId id) { return id == 13; }
bool fk_docked() { return g_docked; }

void* lookup_all(const char* name) {
  const std::string n = name;
  if (n == "GetCurrentGameTime") return reinterpret_cast<void*>(&fk_time);
  if (n == "GetSaveFolderPath") return reinterpret_cast<void*>(&fk_save_path);
  if (n == "IsSaveListLoadingComplete") return reinterpret_cast<void*>(&fk_list_complete);
  if (n == "IsSaveValid") return reinterpret_cast<void*>(&fk_save_valid);
  if (n == "ReloadSaveList") return reinterpret_cast<void*>(&fk_reload);
  if (n == "IsGamePaused") return reinterpret_cast<void*>(&fk_paused);
  if (n == "GetGameVersion") return reinterpret_cast<void*>(&fk_version);
  if (n == "GetBuildVersionSuffix") return reinterpret_cast<void*>(&fk_suffix);
  if (n == "GetPlayerOccupiedShipID") return reinterpret_cast<void*>(&fk_occupied);
  if (n == "GetPlayerControlledShipID") return reinterpret_cast<void*>(&fk_controlled);
  if (n == "GetPlayerID") return reinterpret_cast<void*>(&fk_player);
  if (n == "GetPlayerObjectID") return reinterpret_cast<void*>(&fk_object);
  if (n == "GetPlayerContainerID") return reinterpret_cast<void*>(&fk_container);
  if (n == "GetContextByClass") return reinterpret_cast<void*>(&fk_context);
  if (n == "IsValidComponent") return reinterpret_cast<void*>(&fk_valid);
  if (n == "SpawnObjectAtPos2") return reinterpret_cast<void*>(&fk_spawn);
  if (n == "ActivateObject") return reinterpret_cast<void*>(&fk_activate);
  if (n == "SetObjectSectorPos") return reinterpret_cast<void*>(&fk_setpos);
  if (n == "GetObjectPositionInSector") return reinterpret_cast<void*>(&fk_getpos);
  if (n == "TeleportPlayerTo") return reinterpret_cast<void*>(&fk_teleport);
  if (n == "CanTeleportPlayerTo") return reinterpret_cast<void*>(&fk_canteleport);
  if (n == "SetComponentOwner") return reinterpret_cast<void*>(&fk_setowner);
  if (n == "SetObjectForcedRadarVisible") return reinterpret_cast<void*>(&fk_radar);
  if (n == "IsSetaActive") return reinterpret_cast<void*>(&fk_seta);
  if (n == "GetObjectIDCode") return reinterpret_cast<void*>(&fk_idcode);
  if (n == "GetComponentName") return reinterpret_cast<void*>(&fk_cname);
  if (n == "IsComponentWrecked") return reinterpret_cast<void*>(&fk_wrecked);
  if (n == "IsPlayerOccupiedShipDocked") return reinterpret_cast<void*>(&fk_docked);
  return nullptr;
}
void* lookup_none(const char*) { return nullptr; }

struct ResetMainThread {
  ResetMainThread() {
    main_thread().reset();
    main_thread().set_definition(MainThreadDefinition::FrameUpdateThread);
  }
  ~ResetMainThread() {
    main_thread().reset();
    main_thread().set_definition(MainThreadDefinition::FrameUpdateThread);
  }
};
}  // namespace

TEST_CASE("GameApi wraps every M2 export", "[game][api]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{"9.00", "9.0.0", 900});
  CHECK(missing_exports(api.fns()).empty());
  CHECK(api.game_time() == 1234.5);
  CHECK(api.save_folder_path() == "C:/fake/saves");
  CHECK(api.save_list_loading_complete());
  CHECK(api.save_valid("good.xml.gz"));
  CHECK_FALSE(api.save_valid("bad.xml.gz"));
  CHECK_FALSE(api.save_valid(""));
  const int before = g_reloads;
  CHECK(api.reload_save_list());
  CHECK(g_reloads == before + 1);
  g_paused = true;
  CHECK(api.game_paused());
  const auto v = api.game_version_struct();
  REQUIRE(v);
  CHECK(v->major == 9);
  CHECK(v->minor == 0);
  CHECK(api.build_version_suffix() == "611726");
  CHECK(api.info().game_version == "9.00");
}

TEST_CASE("GameApi is null-safe when exports are missing", "[game][api]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_none), GameInfo{});
  CHECK(missing_exports(api.fns()).size() == 28);
  CHECK_FALSE(api.game_time());
  CHECK_FALSE(api.save_folder_path());
  CHECK_FALSE(api.save_list_loading_complete());
  CHECK_FALSE(api.save_valid("x"));
  CHECK_FALSE(api.reload_save_list());
  CHECK_FALSE(api.game_paused());
  CHECK_FALSE(api.game_version_struct());
  CHECK_FALSE(api.build_version_suffix());
  CHECK(api.player_occupied_ship() == 0);
  CHECK(api.player_object() == 0);
  CHECK(api.context_by_class(5, "ship", true) == 0);
  CHECK(api.is_valid_component(5));  // cannot filter: treated as valid

  // A default-constructed adapter (no table at all) and a null lookup behave the same.
  GameApi blank;
  CHECK_FALSE(blank.game_time());
  CHECK(missing_exports(resolve_game_fns(nullptr)).size() == 28);
}

TEST_CASE("partially available exports resolve individually", "[game][api]") {
  ResetMainThread guard;
  const auto get = [](const char* n) -> void* { return std::string(n) == "IsGamePaused" ? reinterpret_cast<void*>(&fk_paused) : nullptr; };
  GameApi api(resolve_game_fns(get), GameInfo{});
  g_paused = true;
  CHECK(api.game_paused());
  CHECK_FALSE(api.game_time());
  CHECK(missing_exports(api.fns()).size() == 27);
}

TEST_CASE("GameApi wraps the M3 exports and is null-safe without them", "[game][api][m3]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{});
  const PosRotPod p{1, 2, 3, 10, 20, 30};
  CHECK(api.spawn_object("ship_arg_s_fighter_01_a_macro", 77, p, "x4mp_team_1") == 9001);
  CHECK(g_set_sector == 77);
  CHECK(g_set_pos.z == 3.0f);
  CHECK(api.spawn_object("bad", 77, p, "x") == 0);
  CHECK(api.spawn_object("ok", 0, p, "x") == 0);  // no sector: refused before the call
  CHECK(api.spawn_object("", 77, p, "x") == 0);
  CHECK(api.activate_object(9001, false));
  CHECK_FALSE(g_active);
  CHECK_FALSE(api.activate_object(0, true));
  CHECK(api.set_object_sector_pos(9001, 78, PosRotPod{4, 5, 6, 0, 0, 0}));
  CHECK(g_set_sector == 78);
  const auto got = api.object_position(9001);
  REQUIRE(got);
  CHECK(got->x == 4.0f);
  CHECK_FALSE(api.object_position(0));
  CHECK(api.teleport_player_to(9001, true, true, true));
  CHECK_FALSE(api.teleport_player_to(5, true, true, true));
  CHECK(api.can_teleport_player_to(9001, true, false) == std::string("granted"));  // the real game says "granted" (S13.6), not ""
  CHECK(is_teleport_granted(api.can_teleport_player_to(9001, true, false)));
  CHECK(api.can_teleport_player_to(5, true, false) == std::string("no such ship"));
  CHECK_FALSE(is_teleport_granted(api.can_teleport_player_to(5, true, false)));
  CHECK_FALSE(is_teleport_granted(std::string()));  // "" is not an approval
  CHECK_FALSE(is_teleport_granted(std::nullopt));
  CHECK(api.set_component_owner(9001, "x4mp_team_2"));
  CHECK(g_owner == "x4mp_team_2");
  CHECK_FALSE(api.set_component_owner(9001, nullptr));
  CHECK(api.set_object_forced_radar_visible(9001, true));
  CHECK(g_radar);
  g_seta = true;
  CHECK(api.seta_active());
  CHECK(api.object_id_code(9001) == "ABC-123");
  CHECK(api.component_name(9001) == std::string());  // null from the game becomes ""
  CHECK(api.component_wrecked(13));
  CHECK_FALSE(api.component_wrecked(14));
  g_docked = true;
  CHECK(api.player_ship_docked());

  GameApi none(resolve_game_fns(lookup_none), GameInfo{});
  CHECK(none.spawn_object("m", 1, p, "o") == 0);
  CHECK_FALSE(none.activate_object(1, true));
  CHECK_FALSE(none.set_object_sector_pos(1, 1, p));
  CHECK_FALSE(none.object_position(1));
  CHECK_FALSE(none.teleport_player_to(1, true, true, true));
  CHECK_FALSE(none.can_teleport_player_to(1, true, true));
  CHECK_FALSE(none.set_component_owner(1, "f"));
  CHECK_FALSE(none.set_object_forced_radar_visible(1, true));
  CHECK_FALSE(none.seta_active());
  CHECK_FALSE(none.object_id_code(1));
  CHECK_FALSE(none.component_name(1));
  CHECK_FALSE(none.component_wrecked(1));
  CHECK_FALSE(none.player_ship_docked());

  // off the main thread every wrapper refuses
  main_thread().capture_frame();
  bool refused = false;
  std::thread([&] { refused = api.spawn_object("m", 1, p, "o") == 0 && !api.activate_object(1, true) && !api.object_position(1); }).join();
  CHECK(refused);
}

TEST_CASE("assert_main_thread: the first frame thread becomes main", "[game][thread]") {
  ResetMainThread guard;
  auto& mt = main_thread();
  // Before any capture every thread passes (init-time calls).
  CHECK(mt.is_main());
  mt.capture_frame();
  CHECK(mt.assert_main_thread("here"));

  bool other_ok = true;
  std::thread t([&] { other_ok = mt.assert_main_thread("worker"); });
  t.join();
  CHECK_FALSE(other_ok);
  CHECK(mt.violations() >= 1);

  // The adapter refuses game calls off the main thread.
  GameApi api(resolve_game_fns(lookup_all), GameInfo{});
  std::optional<double> t_val;
  std::thread t2([&] { t_val = api.game_time(); });
  t2.join();
  CHECK_FALSE(t_val);
  CHECK(api.game_time() == 1234.5);  // and still works on main
}

TEST_CASE("the frame thread follows the current on_frame_update call", "[game][thread]") {
  ResetMainThread guard;
  auto& mt = main_thread();
  std::thread([&] { mt.capture_frame(); }).join();  // thread A delivers a frame
  CHECK_FALSE(mt.is_main());                        // this thread is not main now ...
  mt.capture_frame();                               // ... but a later frame delivered on this thread makes it main (session 2: two tids)
  CHECK(mt.is_main());
  bool other = true;
  std::thread([&] { other = mt.is_main(); }).join();
  CHECK_FALSE(other);
}

TEST_CASE("the main-thread definition is swappable", "[game][thread]") {
  ResetMainThread guard;
  auto& mt = main_thread();

  mt.set_definition(MainThreadDefinition::InitThread);
  mt.capture_init();
  bool worker = true;
  std::thread([&] { worker = mt.is_main(); }).join();
  CHECK_FALSE(worker);
  CHECK(mt.is_main());

  // capture_frame is irrelevant under InitThread.
  std::thread([&] { mt.capture_frame(); }).join();
  CHECK(mt.is_main());

  mt.set_definition(MainThreadDefinition::Any);
  worker = false;
  std::thread([&] { worker = mt.is_main(); }).join();
  CHECK(worker);

  mt.reset();
  mt.set_definition(MainThreadDefinition::FrameUpdateThread);
  std::thread([&] { mt.capture_frame(); }).join();  // a worker captured first: now the test thread is the intruder
  CHECK_FALSE(mt.is_main());
}

TEST_CASE("violation handler is invoked", "[game][thread]") {
  ResetMainThread guard;
  static std::atomic<int> calls{0};
  calls = 0;
  main_thread().set_violation_handler([](const char*) noexcept { ++calls; });
  main_thread().capture_frame();
  std::thread([&] { (void)main_thread().assert_main_thread("x"); }).join();
  CHECK(calls == 1);
  main_thread().set_violation_handler(nullptr);
}

TEST_CASE("player guard collects ids, contexts and drops invalid ones", "[game][guard]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{});

  // Docked: no controlled ship, but the container and the ship context are guarded.
  g_occupied = 0;
  g_controlled = 0;
  g_player = 10;
  g_object = 50;
  g_container = 777;
  const auto ids = collect_player_guard_ids(api);
  const auto has = [&](ComponentId id) { return std::find(ids.begin(), ids.end(), id) != ids.end(); };
  CHECK(has(10));
  CHECK(has(50));
  CHECK(has(777));
  CHECK(has(500));  // context ship of the player object
  CHECK_FALSE(has(0));
  CHECK(ids.size() == 4);

  // Invalid component ids are dropped; duplicates collapse.
  g_occupied = 666;
  g_controlled = 50;
  CHECK(collect_player_guard_ids(api).size() == 4);

  CHECK(refresh_player_guard(api) == 4);
  CHECK(is_player_guarded(500));
  CHECK(safe_remove(500) == RemoveResult::BlockedPlayerGuard);
  CHECK(safe_remove(777) == RemoveResult::BlockedPlayerGuard);

  // Guard follows the player: next frame the ids change.
  g_object = 0;
  g_player = 11;
  g_container = 0;
  g_controlled = 0;
  refresh_player_guard(api);
  CHECK_FALSE(is_player_guarded(777));
  CHECK(is_player_guarded(11));

  set_player_guard({});
  g_occupied = g_controlled = g_player = g_object = g_container = 0;
}

TEST_CASE("safe_remove backend: guard first, then the game call, main thread only", "[game][safe_remove]") {
  ResetMainThread guard;
  static ComponentId removed = 0;
  removed = 0;
  set_remove_backend([](ComponentId id) { removed = id; });
  const std::array<ComponentId, 1> g{42};
  set_player_guard(g);

  CHECK(safe_remove(42) == RemoveResult::BlockedPlayerGuard);
  CHECK(removed == 0);
  CHECK(safe_remove(0) == RemoveResult::BlockedInvalidId);
  CHECK(safe_remove(43) == RemoveResult::Removed);
  CHECK(removed == 43);

  main_thread().capture_frame();
  RemoveResult off_thread = RemoveResult::Removed;
  std::thread([&] { off_thread = safe_remove(44); }).join();
  CHECK(off_thread == RemoveResult::BlockedWrongThread);
  CHECK(removed == 43);  // not called

  set_remove_backend(nullptr);
  CHECK(safe_remove(45) == RemoveResult::NotImplemented);
  set_player_guard({});
}

TEST_CASE("install_game_backend resolves by name and is null-safe", "[game][safe_remove]") {
  ResetMainThread guard;
  CHECK_FALSE(install_game_backend([](const char*) -> void* { return nullptr; }));
  CHECK_FALSE(install_game_backend(nullptr));
  CHECK(safe_remove(9) == RemoveResult::NotImplemented);

  static ComponentId removed = 0;
  removed = 0;
  CHECK(install_game_backend([](const char*) -> void* { return reinterpret_cast<void*>(+[](ComponentId id) { removed = id; }); }));
  CHECK(safe_remove(9) == RemoveResult::Removed);
  CHECK(removed == 9);
  set_remove_backend(nullptr);
}
