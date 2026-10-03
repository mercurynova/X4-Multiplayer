#pragma once
// Minimal stub X4NativeAPI for the probe tests (not the full hostsim, which is a separate task, M2-01).

#include <x4native_extension.h>
#include <x4_game_func_table.h>

#include <cstring>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace stub {

struct Sub {
  int id;
  std::string name;
  X4NativeEventCallback cb;
  void* ud;
};

class Host {
 public:
  X4NativeAPI api{};
  std::unique_ptr<X4GameFunctions> game = std::make_unique<X4GameFunctions>();
  std::mutex mu;
  std::vector<std::string> logs;
  std::vector<std::pair<std::string, std::string>> lua_events;  // raised via raise_lua_event
  std::vector<Sub> subs;
  std::map<std::string, std::string> stash;
  std::function<void(const std::string&, const std::string&)> on_lua;  // simulated Lua side
  int next_id = 1;
  std::string save_folder = "C:\\stub\\saves\\";
  bool paused = false;
  long long money = 1000000;  // "Lua credits" the stub reports

  static inline Host* self = nullptr;

  Host() {
    self = this;
    std::memset(game.get(), 0, sizeof(X4GameFunctions));
    game->GetSaveFolderPath = [] { return Host::self->save_folder.c_str(); };
    game->IsGamePaused = [] { return Host::self->paused; };
    game->ReloadSaveList = [] {};
    game->IsSaveListLoadingComplete = [] { return true; };
    game->IsSaveValid = [](const char*) { return true; };
    game->AddPlayerMoney = [](int64_t m) { Host::self->money += m; };
    game->GetCurrentGameTime = [] { return 1.0; };
    game->TriggerAutosave = [](bool) {};

    api.api_version = X4NATIVE_API_VERSION;
    api.subscribe = [](const char* n, X4NativeEventCallback cb, void* ud, void*) {
      std::lock_guard lk(self->mu);
      const int id = self->next_id++;
      self->subs.push_back({id, n, cb, ud});
      return id;
    };
    api.unsubscribe = [](int id) {
      std::lock_guard lk(self->mu);
      std::erase_if(self->subs, [id](const Sub& s) { return s.id == id; });
    };
    api.raise_event = [](const char* n, void* d) { self->fire(n, d); };
    api.raise_lua_event = [](const char* n, const char* p) {
      std::function<void(const std::string&, const std::string&)> h;
      {
        std::lock_guard lk(self->mu);
        self->lua_events.emplace_back(n, p ? p : "");
        h = self->on_lua;
      }
      if (h) h(n, p ? p : "");
      return 0;
    };
    api.register_lua_bridge = [](const char*, const char*) { return 0; };
    api.log = [](int, const char* m) {
      std::lock_guard lk(self->mu);
      self->logs.emplace_back(m);
    };
    api.get_game_version = [] { return "9.00"; };
    api.get_x4native_version = [] { return "9.0.0-stub"; };
    api.extension_path = "C:\\stub\\ext";
    api.game = game.get();
    api.get_game_function = [](const char*) -> void* { return nullptr; };
    api.game_func_count = 0;
    api.hook_before = [](const char*, X4HookCallback, void*, void*) { return 1; };
    api.hook_after = [](const char*, X4HookCallback, void*, void*) { return 2; };
    api.unhook = [](int) {};
    api._ensure_detour = [](const char*, void* d) { return d; };
    api.md_subscribe_before = [](uint32_t, X4NativeEventCallback, void*, void*) { return -1; };
    api.md_subscribe_after = [](uint32_t, X4NativeEventCallback cb, void* ud, void*) {
      std::lock_guard lk(self->mu);
      const int id = self->next_id++;
      self->subs.push_back({id, "md", cb, ud});
      return id;
    };
    api.stash_set = [](const char* ns, const char* k, const void* d, uint32_t n) {
      std::lock_guard lk(self->mu);
      self->stash[std::string(ns) + "/" + k] = std::string(static_cast<const char*>(d), n);
      return 1;
    };
    api.stash_get = [](const char* ns, const char* k, uint32_t* n) -> const void* {
      std::lock_guard lk(self->mu);
      const auto it = self->stash.find(std::string(ns) + "/" + k);
      if (it == self->stash.end()) return nullptr;
      if (n) *n = static_cast<uint32_t>(it->second.size());
      return it->second.data();
    };
    api.stash_remove = [](const char* ns, const char* k) {
      std::lock_guard lk(self->mu);
      return static_cast<int>(self->stash.erase(std::string(ns) + "/" + k));
    };
    api.stash_clear = [](const char*) {};
    api._ext_id = "x4mp_probe";
    api._ext_display_name = "X4MP probe";
  }
  ~Host() { self = nullptr; }

  // Fires every subscription with this name (callbacks run on the caller's thread, like Lua-driven events).
  void fire(const char* name, void* data = nullptr) {
    std::vector<Sub> copy;
    {
      std::lock_guard lk(mu);
      for (const auto& s : subs)
        if (s.name == name) copy.push_back(s);
    }
    for (const auto& s : copy) s.cb(name, data, s.ud);
  }
  void fire_str(const char* name, const std::string& value) { fire(name, const_cast<char*>(value.c_str())); }

  [[nodiscard]] std::vector<std::string> log_copy() {
    std::lock_guard lk(mu);
    return logs;
  }
  [[nodiscard]] bool log_contains(const std::string& needle) {
    std::lock_guard lk(mu);
    for (const auto& l : logs)
      if (l.find(needle) != std::string::npos) return true;
    return false;
  }
};

}  // namespace stub
