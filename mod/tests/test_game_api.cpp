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
  CHECK(missing_exports(api.fns()).size() == 15);
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
  CHECK(missing_exports(resolve_game_fns(nullptr)).size() == 15);
}

TEST_CASE("partially available exports resolve individually", "[game][api]") {
  ResetMainThread guard;
  const auto get = [](const char* n) -> void* { return std::string(n) == "IsGamePaused" ? reinterpret_cast<void*>(&fk_paused) : nullptr; };
  GameApi api(resolve_game_fns(get), GameInfo{});
  g_paused = true;
  CHECK(api.game_paused());
  CHECK_FALSE(api.game_time());
  CHECK(missing_exports(api.fns()).size() == 14);
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
