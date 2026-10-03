#pragma once
// host/platform: everything the host needs from the loader, behind an interface so ModHost (and the features) stay
// SDK-free and testable. The real implementation (game/x4_platform.cpp) wraps X4NativeAPI; tests use a fake.

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>

#include "core/log/log.h"

namespace x4mp::host {

class IPlatform {
 public:
  virtual ~IPlatform() = default;

  [[nodiscard]] virtual std::string extension_path() const = 0;     // abs path of the x4mp extension folder
  [[nodiscard]] virtual std::string game_version() const = 0;       // X4Native get_game_version(), "9.00"
  [[nodiscard]] virtual std::string x4native_version() const = 0;
  [[nodiscard]] virtual int game_types_build() const = 0;           // 900
  // Named export lookup (X4NativeAPI::get_game_function). nullptr when the function does not exist.
  [[nodiscard]] virtual void* get_game_function(const char* name) const = 0;
  // Writes to the X4Native extension log (x4n::log). Any level; used for the init banner and mirrored errors.
  virtual void native_log(log::Level level, std::string_view message) = 0;

  // In-process stash (survives extension reload, dies with the game). Keys are scoped to the extension.
  virtual bool stash_set(const char* key, const void* data, std::uint32_t size) = 0;
  // Pointer valid until the next set/remove of the same key; nullptr when absent.
  [[nodiscard]] virtual const void* stash_get(const char* key, std::uint32_t* size) = 0;
  virtual bool stash_remove(const char* key) = 0;

  // ---- Lua bridge (M2-06). Not pure: a platform without a Lua side (most test fakes) keeps the defaults. ----
  // Lua -> native verb: Lua calls __X4NATIVE_API.raise_event("<event>", "<json>") and X4Native raises the C++ event of the same
  // name with the text as data. `handler` gets that text; it runs synchronously inside the Lua call (the UI thread in the game,
  // but a feature must not rely on that: copy the text and act on it in on_frame). Returns false when unsupported.
  using LuaVerbHandler = std::function<void(std::string_view payload)>;
  virtual bool on_lua_verb(const char* event, LuaVerbHandler handler) {
    (void)event;
    (void)handler;
    return false;
  }
  // Drops every on_lua_verb subscription (host shutdown). Handlers never run after this returns.
  virtual void clear_lua_verbs() {}
  // native -> Lua: raises the Lua event `event` (Lua: RegisterEvent) with a text parameter. UI (frame) thread only.
  // 0 = ok, non-zero = failed or unsupported.
  virtual int raise_lua(const char* event, std::string_view param) {
    (void)event;
    (void)param;
    return -1;
  }
};

}  // namespace x4mp::host
