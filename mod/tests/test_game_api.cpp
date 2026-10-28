#include <algorithm>
#include <array>
#include <atomic>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include "game/game_api.h"
#include "game/ghosts_api.h"
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
// Models the real game (sdk/x4n_math.h): writes take DEGREES, GetObjectPositionInSector returns RADIANS. g_written_raw = what the export
// received, g_set_pos = what a read returns.
PosRotPod g_set_pos, g_written_raw;
PosRotPod deg_to_rad(PosRotPod p) {
  p.yaw *= 3.14159265358979f / 180.0f;
  p.pitch *= 3.14159265358979f / 180.0f;
  p.roll *= 3.14159265358979f / 180.0f;
  return p;
}
UniverseId g_set_sector = 0;
UniverseId fk_spawn(const char* macro, UniverseId sector, PosRotPod pos, const char* owner) {
  g_written_raw = pos;
  g_set_pos = deg_to_rad(pos);
  g_set_sector = sector;
  return std::string(macro) == "bad" || std::string(owner) == "nobody" ? 0 : 9001;
}
bool g_active = true, g_radar = false, g_seta = true, g_docked = true;
void fk_activate(UniverseId, bool a) { g_active = a; }
void fk_setpos(UniverseId, UniverseId sector, PosRotPod pos) {
  g_set_sector = sector;
  g_written_raw = pos;
  g_set_pos = deg_to_rad(pos);
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
PosRotPod fk_posoffset(UniverseId id, UniverseId space) {
  // Mirrors the game: the offset of `id` inside `space`. The test only needs a distinguishable answer.
  if (space != 5001) return PosRotPod{};
  return PosRotPod{static_cast<float>(id), 2.0f, 3.0f, 1.5f, 0.0f, 0.0f};
}
// M3b: the offset writer receives the pose as-is (radians), so this fake must NOT convert.
PosRotPod g_written_offset;
UniverseId g_offset_id = 0;
void fk_setposoffset(UniverseId id, PosRotPod p) {
  g_offset_id = id;
  g_written_offset = p;
}

// M5-13: cargo fakes. g_cargo is what the game reports; g_cargo_tags records the tags argument (must be "", never
// nullptr) and g_cargo_id the container the export was asked about.
struct FakeWare {
  const char* ware;
  const char* macro;
  int amount;
};
std::vector<FakeWare> g_cargo;
std::string g_cargo_tags = "unset";
UniverseId g_cargo_id = 0;
std::uint32_t fk_numcargo(UniverseId id, const char* tags) {
  g_cargo_tags = tags ? tags : "<null>";
  g_cargo_id = id;
  return static_cast<std::uint32_t>(g_cargo.size());
}
std::uint32_t fk_cargo(WareInfoPod* out, std::uint32_t len, UniverseId id, const char* tags) {
  g_cargo_tags = tags ? tags : "<null>";
  g_cargo_id = id;
  if (!out) return 0;
  std::uint32_t n = 0;
  for (const auto& w : g_cargo) {
    if (n >= len) break;
    out[n].ware = w.ware;
    out[n].macro = w.macro;
    out[n].amount = w.amount;
    ++n;
  }
  return n;
}

// M5-19: the station spawn fake records what the wrapper passed (macro, sector, plan, owner) and what it returned.
UniverseId g_station_result = 0xABCDEF;
std::string g_station_macro, g_station_plan, g_station_owner;
UniverseId g_station_sector = 0;
PosRotPod g_station_pos{};
UniverseId fk_spawn_station(const char* macro, UniverseId sector, PosRotPod pos, const char* plan, const char* owner) {
  g_station_macro = macro ? macro : "<null>";
  g_station_sector = sector;
  g_station_pos = pos;
  g_station_plan = plan ? plan : "<null>";
  g_station_owner = owner ? owner : "<null>";
  return g_station_result;
}

// M5-20: the owner-details fake answers with the faction id the mod compares against "player".
std::string g_owner_faction = "player";
UniverseId g_owner_component = 0;
OwnerFactionPod fk_owner_details(UniverseId id) {
  g_owner_component = id;
  return OwnerFactionPod{g_owner_faction.c_str(), "Fake Faction", "icon"};
}

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
  if (n == "GetPositionalOffset") return reinterpret_cast<void*>(&fk_posoffset);
  if (n == "SetPositionalOffset") return reinterpret_cast<void*>(&fk_setposoffset);
  if (n == "GetNumCargo") return reinterpret_cast<void*>(&fk_numcargo);
  if (n == "GetCargo") return reinterpret_cast<void*>(&fk_cargo);
  if (n == "SpawnStationAtPos") return reinterpret_cast<void*>(&fk_spawn_station);
  if (n == "GetOwnerDetails") return reinterpret_cast<void*>(&fk_owner_details);
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
  CHECK(missing_exports(api.fns()).size() == 34);
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
  CHECK(missing_exports(resolve_game_fns(nullptr)).size() == 34);
}

// M3-15: the mod's PosRotPod / GhostPose angles are radians; the game's write exports want degrees and the read export returns radians.
TEST_CASE("angles are written to the game in degrees and read back in radians", "[game][api][m3][units]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{});
  GhostsApi ghosts(api, nullptr);
  constexpr double kPi = 3.14159265358979;

  // place(): yaw = pi/2 rad reaches SetObjectSectorPos as 90 (deg); the read-back is pi/2 rad again
  GhostPose pose;
  pose.x = 10;
  pose.yaw = kPi / 2;
  pose.pitch = kPi / 4;
  pose.roll = -kPi / 6;
  CHECK(ghosts.place(9001, 77, pose));
  CHECK(g_written_raw.yaw == Catch::Approx(90.0f).margin(1e-3));
  CHECK(g_written_raw.pitch == Catch::Approx(45.0f).margin(1e-3));
  CHECK(g_written_raw.roll == Catch::Approx(-30.0f).margin(1e-3));
  CHECK(g_written_raw.x == 10.0f);  // positions are never scaled
  const auto back = ghosts.pose(9001);
  REQUIRE(back);
  CHECK(back->yaw == Catch::Approx(kPi / 2).margin(1e-5));
  CHECK(back->pitch == Catch::Approx(kPi / 4).margin(1e-5));
  CHECK(back->roll == Catch::Approx(-kPi / 6).margin(1e-5));

  // spawn(): same conversion on the spawn path
  CHECK(ghosts.spawn("ship_arg_s_fighter_01_a_macro", 77, pose, "x4mp_team_1") == 9001);
  CHECK(g_written_raw.yaw == Catch::Approx(90.0f).margin(1e-3));

  // the raw GameApi writers (avatar spawns, takeover copy, avatar driving) convert the same way
  CHECK(api.set_object_sector_pos(9001, 78, PosRotPod{0, 0, 0, static_cast<float>(kPi), 0, 0}));
  CHECK(g_written_raw.yaw == Catch::Approx(180.0f).margin(1e-3));
  CHECK(api.spawn_object("m", 78, PosRotPod{0, 0, 0, static_cast<float>(kPi / 2), 0, 0}, "x4mp_team_1") == 9001);
  CHECK(g_written_raw.yaw == Catch::Approx(90.0f).margin(1e-3));
  CHECK(api.object_position(9001)->yaw == Catch::Approx(kPi / 2).margin(1e-5));  // reads are not converted
}

TEST_CASE("partially available exports resolve individually", "[game][api]") {
  ResetMainThread guard;
  const auto get = [](const char* n) -> void* { return std::string(n) == "IsGamePaused" ? reinterpret_cast<void*>(&fk_paused) : nullptr; };
  GameApi api(resolve_game_fns(get), GameInfo{});
  g_paused = true;
  CHECK(api.game_paused());
  CHECK_FALSE(api.game_time());
  CHECK(missing_exports(api.fns()).size() == 33);
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

  // M3b: the on-foot offset writer is the one write that keeps radians (S10.4/S10.5 wrote GetPositionalOffset's own
  // radians and the avatar faced the right way), so the wrapper must not run pos_rot_to_game_write.
  CHECK(api.set_positional_offset(9002, PosRotPod{1, 2, 3, 1.5707964f, 0.25f, 0}));
  CHECK(g_offset_id == 9002);
  CHECK(g_written_offset.yaw == Catch::Approx(1.5707964f).margin(1e-5f));
  CHECK(g_written_offset.pitch == Catch::Approx(0.25f).margin(1e-6f));
  CHECK(g_written_offset.z == 3.0f);
  CHECK_FALSE(api.set_positional_offset(0, p));  // no id: refused before the call

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
  CHECK_FALSE(none.set_positional_offset(1, p));

  // off the main thread every wrapper refuses
  main_thread().capture_frame();
  bool refused = false;
  std::thread([&] { refused = api.spawn_object("m", 1, p, "o") == 0 && !api.activate_object(1, true) && !api.object_position(1); }).join();
  CHECK(refused);
}

TEST_CASE("GameApi::cargo copies the game's cargo lines and refuses an empty read", "[game][api][m5][cargo]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{});
  g_cargo = {{"energycells", "energycells", 500}, {"water", "water", 0}, {"", "ore", 3}, {"ore", "ore", 12}};
  g_cargo_tags = "unset";
  g_cargo_id = 0;
  const auto got = api.cargo(7001);
  CHECK(g_cargo_id == 7001);
  CHECK(g_cargo_tags.empty());  // the export takes "", never nullptr
  REQUIRE(got.size() == 2);     // amount 0 and an empty ware are dropped
  CHECK(got[0].ware == "energycells");
  CHECK(got[0].amount == 500);
  CHECK(got[1].ware == "ore");
  CHECK(got[1].amount == 12);

  g_cargo.clear();  // GetNumCargo == 0: an empty read, not an empty hold (M5-13)
  CHECK(api.cargo(7001).empty());
  CHECK(api.cargo(0).empty());  // no id: refused before the call
  g_cargo = {{"energycells", "energycells", 500}};
  g_cargo_id = 0;
  CHECK(api.cargo(0).empty());
  CHECK(g_cargo_id == 0);  // the id-0 read never reached the game

  GameApi none(resolve_game_fns(lookup_none), GameInfo{});
  CHECK(none.cargo(7001).empty());

  main_thread().capture_frame();
  bool refused = false;
  std::thread([&] { refused = api.cargo(7001).empty(); }).join();
  CHECK(refused);  // off the main thread the wrapper refuses
}

TEST_CASE("GameApi::spawn_station passes the plan and owner and refuses a bad call", "[game][api][m5][spawn]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{});
  g_station_result = 0xABCDEF;
  g_station_macro.clear();
  g_station_plan = "unset";
  g_station_owner.clear();
  g_station_sector = 0;
  PosRotPod pos{};
  pos.x = 100.0f;
  pos.y = 0.5f;
  pos.z = -2.0f;
  pos.yaw = 1.0f;
  CHECK(api.spawn_station("station_arg_defence_01_macro", 8101, pos, "", "x4mp_team_1") == 0xABCDEF);
  CHECK(g_station_macro == "station_arg_defence_01_macro");
  CHECK(g_station_sector == 8101);
  CHECK(g_station_plan.empty());  // "" is the macro-plus-modules path, not a nullptr
  CHECK(g_station_owner == "x4mp_team_1");
  CHECK(g_station_pos.x == Catch::Approx(100.0f).margin(0.01));       // positions untouched
  CHECK(g_station_pos.z == Catch::Approx(-2.0f).margin(0.01));
  CHECK(g_station_pos.yaw == Catch::Approx(57.29578f).margin(0.01));  // radians in, degrees out
  CHECK(api.spawn_station("", 8101, pos, "", "x4mp_team_1") == 0);  // no macro: refused before the call
  CHECK(api.spawn_station("station_arg_defence_01_macro", 0, pos, "", "x4mp_team_1") == 0);  // no sector
  GameApi none(resolve_game_fns(lookup_none), GameInfo{});
  CHECK(none.spawn_station("station_arg_defence_01_macro", 8101, pos, "", "x4mp_team_1") == 0);
  main_thread().capture_frame();
  UniverseId off_thread = 1;
  std::thread([&] { off_thread = api.spawn_station("station_arg_defence_01_macro", 8101, pos, "", "x4mp_team_1"); }).join();
  CHECK(off_thread == 0);  // off the main thread the wrapper refuses
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

TEST_CASE("GameApi::owner_faction names the owner and refuses a bad read", "[game][api][m5][owner]") {
  ResetMainThread guard;
  GameApi api(resolve_game_fns(lookup_all), GameInfo{"9.00", "9.0.0", 900});

  g_owner_faction = "player";
  g_owner_component = 0;
  CHECK(api.owner_faction(0x77) == "player");
  CHECK(g_owner_component == 0x77);

  g_owner_faction = "";  // the game has no faction for the component
  CHECK(api.owner_faction(0x77).empty());
  CHECK(api.owner_faction(0).empty());  // id 0 never reaches the game
  CHECK(g_owner_component == 0x77);

  g_owner_faction = "player";
  GameApi none(resolve_game_fns(lookup_none), GameInfo{"9.00", "9.0.0", 900});
  CHECK(none.owner_faction(0x77).empty());  // export missing

  main_thread().capture_frame();  // off the game thread every wrapper refuses
  std::string off_thread = "x";
  std::thread([&] { off_thread = api.owner_faction(0x77); }).join();
  CHECK(off_thread.empty());
}
