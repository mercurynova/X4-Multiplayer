// hostsim: a fake X4Native host. Implements the X4NativeAPI contract (x4native_extension.h, X4Native v9.0.0-611726)
// well enough to run a real extension DLL without X4: events (subscribe / raise, the Lua->native bridge), the Lua
// event capture (raise_lua_event), a stash that lives in host memory (so it survives shutdown + FreeLibrary +
// reload, like the proxy-DLL-pinned stash in the game), settings, logging, and a zeroed X4GameFunctions table with
// fakes for the handful of game functions the mod uses.
#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <x4_game_func_table.h>
#include <x4native_extension.h>

#include "world.h"

namespace hostsim {

struct LuaEvent {
  std::string topic;
  std::string param;
};

class Host {
 public:
  Host(std::filesystem::path work_dir, std::string ext_id);
  ~Host();
  Host(const Host&) = delete;
  Host& operator=(const Host&) = delete;

  // The API struct handed to x4native_init(). Rebuilt in place; pointers stay valid for the Host's life.
  X4NativeAPI api{};

  // ---- configuration (script `set`), read by the fakes -------------------------------------------------------
  std::string game_version = "9.00";
  std::string x4native_version = "9.0.0-hostsim";
  std::string build_suffix = "611726";  // GetBuildVersionSuffix(): the mod takes the longest 5+ digit run as the build number
  int game_types_build = 900;
  std::filesystem::path save_dir;  // GetSaveFolderPath() (a temp dir by default, with a trailing separator)
  std::atomic<bool> paused{false};
  std::atomic<bool> save_list_complete{true};
  std::atomic<bool> save_valid{true};
  std::atomic<double> game_time{1.0};
  double speed = 1.0;
  std::map<std::string, std::string> settings;  // key -> text (typed on read)

  // The fake universe behind the M3 exports (objects, sectors, the player ship, seat / docked / SETA; world.h). Script thread only.
  World world;

  // ---- counters the scripts can assert on -------------------------------------------------------------------
  std::atomic<int> reload_save_list_calls{0};
  std::atomic<long long> money_delta{0};
  std::atomic<int> reloads{0};
  std::atomic<long long> frames{0};

  // ---- events ---------------------------------------------------------------------------------------------
  // Runs every subscriber of `name` on the calling thread. Returns false if a callback threw.
  bool fire(const std::string& name, void* data = nullptr);
  [[nodiscard]] int subscriber_count(const std::string& name);
  [[nodiscard]] bool has_bridge(const std::string& lua_event, std::string* cpp_event = nullptr);
  [[nodiscard]] std::size_t hook_count();
  // Drops subscriptions, bridges and hooks (what X4Native's auto-cleanup does when it unloads an extension).
  void clear_registrations();

  // ---- Lua event capture (native -> Lua: api.raise_lua_event) ------------------------------------------------------
  // Waits until an event with this topic (and, if given, accepted by `accept`) was captured; consumes it.
  bool wait_lua(const std::string& topic, std::chrono::milliseconds timeout, const std::function<bool(const LuaEvent&)>& accept,
                LuaEvent* out);
  std::size_t drop_lua(const std::string& topic);  // empty topic = all; returns how many were dropped
  [[nodiscard]] std::string lua_summary(std::size_t max_items = 12);
  [[nodiscard]] std::size_t lua_pending();
  [[nodiscard]] std::vector<LuaEvent> lua_all();  // every event ever captured (for secret scans)

  // ---- log ------------------------------------------------------------------------------------------------
  bool wait_log(const std::string& needle, std::chrono::milliseconds timeout, bool only_new);
  [[nodiscard]] bool log_contains(const std::string& needle);
  [[nodiscard]] std::vector<std::string> log_copy();
  void set_log_file(const std::filesystem::path& p);
  void note(const std::string& line);  // hostsim's own output line (stdout + log file)

  // ---- stash ----------------------------------------------------------------------------------------------
  [[nodiscard]] std::map<std::string, std::string> stash_copy();
  [[nodiscard]] bool stash_has(const std::string& key);  // key as "<ns>/<key>"
  bool stash_value(const std::string& key, std::string* out);

  // ---- misc -------------------------------------------------------------------------------------------------
  // Number of contract violations seen so far (callback threw, raise_lua_event / game function off the main thread).
  [[nodiscard]] int violations() const { return violation_count_.load(); }
  // Re-reads the `set` configuration into the API struct; call right before x4native_init().
  void prepare_init() {
    api.game_types_build = game_types_build;
    save_dir_str_ = save_dir.string() + "\\";
    std::filesystem::create_directories(save_dir);
  }
  [[nodiscard]] std::string ext_path_string() const { return ext_path_; }
  void set_ext_id(std::string id) { ext_id_ = std::move(id); api._ext_id = ext_id_.c_str(); }

 private:
  void build_api();
  void build_game();

  std::filesystem::path work_dir_;
  std::string ext_id_, ext_name_, ext_path_, save_dir_str_;
  std::string str_buf_;  // return buffer of the fake string exports (GetObjectIDCode ...), shared like the game's
  std::unique_ptr<X4GameFunctions> game_;
  std::unique_ptr<std::uint8_t[]> offsets_;
  std::map<std::string, void*> game_fns_;

  struct Sub {
    int id;
    std::string name;
    X4NativeEventCallback cb;
    void* ud;
  };
  std::mutex mu_;
  std::condition_variable cv_;
  std::vector<Sub> subs_;
  int next_id_ = 1;
  std::map<std::string, std::string> bridges_;
  std::map<int, std::string> hooks_;
  std::vector<std::string> logs_;
  std::size_t log_cursor_ = 0;
  std::vector<LuaEvent> lua_;
  std::vector<LuaEvent> lua_history_;
  std::map<std::string, std::string> stash_;
  std::atomic<int> violation_count_{0};
  FILE* log_file_ = nullptr;

  friend struct Thunks;
};

// The one Host that the C callbacks of X4NativeAPI talk to (the API has no user pointer on most entries).
Host* current_host();

}  // namespace hostsim
