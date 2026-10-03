// Stub X4Native extension used to test x4mp-hostsim itself (ctest hostsim.stub). It exercises every part of the host
// API hostsim fakes: subscribe, the Lua->native bridge, raise_lua_event, the stash across reloads, settings and the
// game functions. NOT part of the product build (target x4mp_hostsim_stub, tests only).

#include <x4n_core.h>
#include <x4n_events.h>
#include <x4n_log.h>
#include <x4n_settings.h>
#include <x4n_stash.h>

#include <cstdio>
#include <string>

namespace {
int g_frames = 0;
int g_native_frames = 0;
}  // namespace

static void on_frame() {
  ++g_frames;
  if (g_frames == 1) {
    // Game calls are made on the frame thread, as the real mod must.
    const auto* g = x4n::game();
    x4n::log::info("stub: save dir '{}', time {:.2f}, paused {}, list complete {}, valid {}", g->GetSaveFolderPath(), g->GetCurrentGameTime(),
                   g->IsGamePaused(), g->IsSaveListLoadingComplete(), g->IsSaveValid("x.xml.gz"));
    g->ReloadSaveList();
  }
  if (g_frames % 100 == 0) x4n::raise_lua("x4stub.frames", std::to_string(g_frames).c_str());
}

static void on_native_frame(const X4NativeFrameUpdate* f) {
  ++g_native_frames;
  if (f->delta <= 0.0) x4n::log::error("stub: bad frame delta");
}

static void on_join(const char* payload) {
  // Echo what Lua sent as a status JSON (hostsim checks it with expect-lua ... json).
  std::string s = std::string("{\"state\":\"joining\",\"echo\":") + (payload ? payload : "null") + "}";
  x4n::raise_lua("x4stub.status", s.c_str());
}

// M3-04: drives the fake universe through the real SDK function table (so the signatures hostsim implements are
// checked against x4_game_func_table.h). `x4stub.world <step>`; results come back as x4stub.world_result JSON.
static unsigned long long g_obj = 0;
static void world_result(const char* step, const std::string& body) {
  x4n::raise_lua("x4stub.world_result", (std::string("{\"step\":\"") + step + "\"," + body + "}").c_str());
}
static std::string pos_json(UniverseID id) {
  const auto* g = x4n::game();
  const UIPosRot p = g->GetObjectPositionInSector(id);
  char b[200];
  std::snprintf(b, sizeof(b), "\"x\":%.3f,\"y\":%.3f,\"z\":%.3f,\"yaw\":%.3f,\"sector\":%llu", p.x, p.y, p.z, p.yaw,
                static_cast<unsigned long long>(g->GetContextByClass(id, "sector", true)));
  return b;
}
static void on_world(const char* payload) {
  const auto* g = x4n::game();
  const std::string step = payload ? payload : "";
  if (step == "spawn") {
    UIPosRot at{10, 20, 30, 45, 0, 0};
    g_obj = g->SpawnObjectAtPos2("ship_arg_s_fighter_01_a_macro", 100001, at, "x4mp_team_1");
    const std::string code = g->GetObjectIDCode(g_obj);
    const std::string name = g->GetComponentName(g_obj);
    world_result("spawn", "\"id\":" + std::to_string(g_obj) + ",\"idcode\":\"" + code + "\",\"name\":\"" + name + "\",\"valid\":" +
                              (g->IsValidComponent(g_obj) ? "true" : "false") + "," + pos_json(g_obj));
  } else if (step == "bad_sector") {
    UIPosRot at{};
    const auto id = g->SpawnObjectAtPos2("ship_arg_s_fighter_01_a_macro", 424242, at, "x4mp_team_1");
    world_result("bad_sector", "\"id\":" + std::to_string(id));
  } else if (step == "dress") {
    UIPosRot to{500, 0, 600, 90, 0, 0};
    g->SetObjectSectorPos(g_obj, 100002, to);
    g->ActivateObject(g_obj, false);
    g->SetObjectForcedRadarVisible(g_obj, true);
    g->SetComponentOwner(g_obj, "x4mp_team_2");
    world_result("dress", pos_json(g_obj) + ",\"wrecked\":" + (g->IsComponentWrecked(g_obj) ? "true" : "false"));
  } else if (step == "player") {
    const UniverseID occ = g->GetPlayerOccupiedShipID();
    const UniverseID ship = g->GetPlayerObjectID();
    world_result("player", "\"occupied\":" + std::to_string(occ) + ",\"controlled\":" + std::to_string(g->GetPlayerControlledShipID()) +
                               ",\"container\":" + std::to_string(g->GetPlayerContainerID()) + ",\"docked\":" + (g->IsPlayerOccupiedShipDocked() ? "true" : "false") +
                               ",\"seta\":" + (g->IsSetaActive() ? "true" : "false") + "," + pos_json(ship ? ship : occ));
  } else if (step == "teleport") {
    const std::string why = g->CanTeleportPlayerTo(g_obj, true, true);
    const bool ok = g->TeleportPlayerTo(g_obj, true, true, true);
    world_result("teleport", "\"why\":\"" + why + "\",\"ok\":" + (ok ? "true" : "false") + ",\"occupied\":" + std::to_string(g->GetPlayerOccupiedShipID()));
  }
}

static void on_loaded() { x4n::log::info("stub: on_game_loaded"); }
static void on_ready() {
  x4n::log::info("stub: on_universe_ready");
  x4n::raise_lua("x4stub.universe_ready", "{}");
}
static void on_ui_reload() { x4n::raise_lua("x4stub.ui_reloaded", "{}"); }
static void on_save() { x4n::log::info("stub: on_game_save"); }

X4N_EXTENSION {
  int boots = 0;
  x4n::stash::get("boots", &boots);
  ++boots;
  x4n::stash::set("boots", boots);
  x4n::stash::set_string("note", "kept");
  x4n::log::info("stub init boots={} game {} setting.greeting={}", boots, x4n::game_version(), x4n::settings::get_string("greeting", "none"));

  x4n::on("on_frame_update", on_frame);
  x4n::on("on_native_frame_update", on_native_frame);
  x4n::on("on_game_loaded", on_loaded);
  x4n::on("on_universe_ready", on_ready);
  x4n::on("on_ui_reload", on_ui_reload);
  x4n::on("on_game_save", on_save);
  x4n::bridge_lua_event("x4stub.join");
  x4n::on("x4stub.join", on_join);
  x4n::bridge_lua_event("x4stub.world");
  x4n::on("x4stub.world", on_world);
  x4n::raise_lua("x4stub.hello", ("{\"boots\":" + std::to_string(boots) + "}").c_str());
}

X4N_SHUTDOWN { x4n::log::info("stub shutdown frames={} native={}", g_frames, g_native_frames); }
