// Stub X4Native extension used to test x4mp-hostsim itself (ctest hostsim.stub). It exercises every part of the host
// API hostsim fakes: subscribe, the Lua->native bridge, raise_lua_event, the stash across reloads, settings and the
// game functions. NOT part of the product build (target x4mp_hostsim_stub, tests only).

#include <x4n_core.h>
#include <x4n_events.h>
#include <x4n_log.h>
#include <x4n_settings.h>
#include <x4n_stash.h>

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
  x4n::raise_lua("x4stub.hello", ("{\"boots\":" + std::to_string(boots) + "}").c_str());
}

X4N_SHUTDOWN { x4n::log::info("stub shutdown frames={} native={}", g_frames, g_native_frames); }
