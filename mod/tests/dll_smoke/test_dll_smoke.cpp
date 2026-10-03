// Smoke test of the REAL x4mp.dll through a minimal fake X4NativeAPI (M2-04). It pins the DLL lifecycle contract that the
// full hostsim (M2-01) builds on: exports, subscribed events, init / frame / gates / shutdown, reload with the stash kept,
// and the inert refusal on an unsupported build. This is the only test target (besides the DLL) with the SDK on its
// include path; it never includes anything from core/ or host/.

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <random>
#include <sstream>
#include <string>
#include <vector>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <catch2/catch_test_macros.hpp>

#include <x4native_extension.h>

#ifndef X4MP_DLL_PATH
#error "X4MP_DLL_PATH must be defined by the build"
#endif

namespace {
struct Sub {
  int id;
  std::string name;
  X4NativeEventCallback cb;
  void* ud;
};

struct VersionPod {
  int major;
  int minor;
};

// The fake loader state, reached through a global because the API uses plain C function pointers.
struct Fake {
  std::vector<Sub> subs;
  int next_id = 1;
  std::map<std::string, std::vector<std::uint8_t>> stash;
  std::vector<std::string> logs;
  std::string build_suffix = "611726";
} g;

int f_subscribe(const char* name, X4NativeEventCallback cb, void* ud, void*) {
  g.subs.push_back({g.next_id, name, cb, ud});
  return g.next_id++;
}
void f_unsubscribe(int id) {
  for (auto it = g.subs.begin(); it != g.subs.end(); ++it) {
    if (it->id == id) {
      g.subs.erase(it);
      return;
    }
  }
}
void f_raise(const char*, void*) {}
void f_log(int, const char* msg) { g.logs.emplace_back(msg); }
const char* f_game_version() { return "9.00"; }
const char* f_x4n_version() { return "9.0.0"; }

double fk_time() { return 7.0; }
bool fk_paused() { return false; }
VersionPod fk_version() { return {9, 0}; }
const char* fk_suffix() { return g.build_suffix.c_str(); }
std::uint64_t fk_zero() { return 0; }

void* f_get_game_function(const char* name) {
  const std::string n = name;
  if (n == "GetCurrentGameTime") return reinterpret_cast<void*>(&fk_time);
  if (n == "IsGamePaused") return reinterpret_cast<void*>(&fk_paused);
  if (n == "GetGameVersion") return reinterpret_cast<void*>(&fk_version);
  if (n == "GetBuildVersionSuffix") return reinterpret_cast<void*>(&fk_suffix);
  if (n == "GetPlayerObjectID") return reinterpret_cast<void*>(&fk_zero);
  return nullptr;  // everything else is "missing": the DLL must cope
}

int f_stash_set(const char*, const char* key, const void* data, std::uint32_t size) {
  g.stash[key].assign(static_cast<const std::uint8_t*>(data), static_cast<const std::uint8_t*>(data) + size);
  return 1;
}
const void* f_stash_get(const char*, const char* key, std::uint32_t* size) {
  const auto it = g.stash.find(key);
  if (it == g.stash.end()) return nullptr;
  if (size) *size = static_cast<std::uint32_t>(it->second.size());
  return it->second.data();
}
int f_stash_remove(const char*, const char* key) { return static_cast<int>(g.stash.erase(key)); }

struct Loaded {
  HMODULE dll = nullptr;
  int (*api_version)() = nullptr;
  int (*init)(X4NativeAPI*) = nullptr;
  void (*shutdown)() = nullptr;
  ~Loaded() {
    if (dll) FreeLibrary(dll);
  }
};

struct TempExt {
  std::filesystem::path dir;
  std::string dir_str;
  TempExt() {
    std::random_device rd;
    dir = std::filesystem::temp_directory_path() / ("x4mp_dll_smoke_" + std::to_string(rd()));
    std::filesystem::create_directories(dir);
    std::ofstream(dir / "x4mp.portable").put('\n');
    dir_str = dir.string();
  }
  ~TempExt() {
    std::error_code ec;
    std::filesystem::remove_all(dir, ec);
  }
  [[nodiscard]] std::string log() const {
    std::ifstream in(dir / "logs" / "x4mp.log", std::ios::binary);
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
  }
};

X4NativeAPI make_api(const TempExt& ext) {
  X4NativeAPI api;
  std::memset(&api, 0, sizeof api);
  api.api_version = X4NATIVE_API_VERSION;
  api.subscribe = &f_subscribe;
  api.unsubscribe = &f_unsubscribe;
  api.raise_event = &f_raise;
  api.log = &f_log;
  api.get_game_version = &f_game_version;
  api.get_x4native_version = &f_x4n_version;
  api.extension_path = ext.dir_str.c_str();
  api.get_game_function = &f_get_game_function;
  api.game_types_build = 900;
  api.stash_set = &f_stash_set;
  api.stash_get = &f_stash_get;
  api.stash_remove = &f_stash_remove;
  api._ext_id = "x4mp";
  api._ext_display_name = "x4mp";
  return api;
}

void fire(const char* name) {
  const auto subs = g.subs;  // callbacks may (un)subscribe
  for (const auto& s : subs) {
    if (s.name == name) s.cb(name, nullptr, s.ud);
  }
}

bool subscribed(const char* name) {
  for (const auto& s : g.subs) {
    if (s.name == name) return true;
  }
  return false;
}

Loaded load() {
  Loaded l;
  l.dll = LoadLibraryA(X4MP_DLL_PATH);
  REQUIRE(l.dll != nullptr);
  l.api_version = reinterpret_cast<int (*)()>(GetProcAddress(l.dll, "x4native_api_version"));
  l.init = reinterpret_cast<int (*)(X4NativeAPI*)>(GetProcAddress(l.dll, "x4native_init"));
  l.shutdown = reinterpret_cast<void (*)()>(GetProcAddress(l.dll, "x4native_shutdown"));
  REQUIRE(l.api_version);
  REQUIRE(l.init);
  REQUIRE(l.shutdown);
  return l;
}

void reset_fake() {
  g = Fake{};
}
}  // namespace

TEST_CASE("x4mp.dll: exports, init, frames, gates, shutdown", "[dll]") {
  reset_fake();
  TempExt ext;
  X4NativeAPI api = make_api(ext);
  Loaded dll = load();

  CHECK(dll.api_version() == X4NATIVE_API_VERSION);
  REQUIRE(dll.init(&api) == X4NATIVE_OK);
  CHECK(subscribed("on_frame_update"));
  CHECK(subscribed("on_game_loaded"));
  CHECK(subscribed("on_universe_ready"));
  CHECK(subscribed("on_ui_reload"));
  CHECK(g.subs.size() == 4);

  for (int i = 0; i < 600; ++i) fire("on_frame_update");
  fire("on_game_loaded");
  fire("on_universe_ready");
  for (int i = 0; i < 60; ++i) fire("on_frame_update");
  fire("on_ui_reload");

  dll.shutdown();
  CHECK(g.subs.empty());                            // everything unsubscribed
  CHECK(g.stash.count("host.state") == 1);          // reload state saved

  const std::string log = ext.log();
  CHECK(log.find("host started") != std::string::npos);
  CHECK(log.find("universe ready") != std::string::npos);
  CHECK(log.find("shutdown after 660 frame(s)") != std::string::npos);
  CHECK(log.find("build check: supported") != std::string::npos);

  dll.shutdown();  // idempotent
}

TEST_CASE("x4mp.dll: reload = shutdown + init with the stash kept", "[dll]") {
  reset_fake();
  TempExt ext;
  X4NativeAPI api = make_api(ext);
  Loaded dll = load();

  REQUIRE(dll.init(&api) == X4NATIVE_OK);
  fire("on_game_loaded");
  fire("on_universe_ready");
  fire("on_frame_update");
  dll.shutdown();
  REQUIRE(g.stash.count("host.state") == 1);

  REQUIRE(dll.init(&api) == X4NATIVE_OK);  // same DLL, same stash
  CHECK(g.subs.size() == 4);
  fire("on_frame_update");
  dll.shutdown();

  const std::string log = ext.log();
  CHECK(log.find("previous run: reload_count=1") != std::string::npos);
}

TEST_CASE("x4mp.dll: an unsupported build stays inert but returns OK", "[dll]") {
  reset_fake();
  g.build_suffix = "123456";
  TempExt ext;
  X4NativeAPI api = make_api(ext);
  Loaded dll = load();

  REQUIRE(dll.init(&api) == X4NATIVE_OK);
  CHECK(g.subs.empty());  // subscribed to nothing
  dll.shutdown();

  const std::string log = ext.log();
  CHECK(log.find("REFUSING TO START") != std::string::npos);
  CHECK(log.find("Unsupported X4 build") != std::string::npos);
  bool mirrored = false;
  for (const auto& l : g.logs) mirrored = mirrored || l.find("REFUSING TO START") != std::string::npos;
  CHECK(mirrored);
}

TEST_CASE("x4mp.dll: tolerates a loader that provides no game functions", "[dll]") {
  reset_fake();
  TempExt ext;
  X4NativeAPI api = make_api(ext);
  api.get_game_function = [](const char*) -> void* { return nullptr; };
  Loaded dll = load();

  REQUIRE(dll.init(&api) == X4NATIVE_OK);  // version unreadable via struct, falls back to the string; build unverified
  for (int i = 0; i < 10; ++i) fire("on_frame_update");
  fire("on_game_loaded");
  fire("on_universe_ready");
  fire("on_frame_update");
  dll.shutdown();
  CHECK(ext.log().find("build not verified") != std::string::npos);
}
