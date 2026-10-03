#include "game/x4_platform.h"

#include <memory>
#include <string>

#include <x4n_core.h>
#include <x4n_events.h>
#include <x4n_log.h>
#include <x4n_stash.h>

namespace x4mp::game {

namespace {
bool api_ok() { return ::x4n::detail::g_api != nullptr; }
}  // namespace

std::string X4Platform::extension_path() const {
  if (!api_ok() || !::x4n::path()) return {};
  return ::x4n::path();
}

std::string X4Platform::game_version() const {
  if (!api_ok() || !::x4n::detail::g_api->get_game_version) return {};
  const char* v = ::x4n::game_version();
  return v ? v : "";
}

std::string X4Platform::x4native_version() const {
  if (!api_ok() || !::x4n::detail::g_api->get_x4native_version) return {};
  const char* v = ::x4n::version();
  return v ? v : "";
}

int X4Platform::game_types_build() const { return api_ok() ? ::x4n::detail::g_api->game_types_build : 0; }

void* X4Platform::get_game_function(const char* name) const {
  if (!api_ok() || !::x4n::detail::g_api->get_game_function || !name) return nullptr;
  return ::x4n::game_fn(name);
}

void X4Platform::native_log(log::Level level, std::string_view message) {
  if (!api_ok()) return;
  const std::lock_guard lock(log_mutex_);
  switch (level) {
    case log::Level::Debug: ::x4n::log::debug(message); break;
    case log::Level::Info: ::x4n::log::info(message); break;
    case log::Level::Warn: ::x4n::log::warn(message); break;
    case log::Level::Error: ::x4n::log::error(message); break;
  }
}

bool X4Platform::stash_set(const char* key, const void* data, std::uint32_t size) {
  return api_ok() && ::x4n::stash::set(key, data, size);
}

const void* X4Platform::stash_get(const char* key, std::uint32_t* size) {
  return api_ok() ? ::x4n::stash::get(key, size) : nullptr;
}

bool X4Platform::stash_remove(const char* key) { return api_ok() && ::x4n::stash::remove(key); }

// ---- Lua <-> native bridge (M2-10) ----
// The EventFn lives on the heap for as long as its subscription; X4Native hands it back as the userdata pointer.
namespace {
std::vector<std::unique_ptr<host::IPlatform::EventFn>>& event_fns() {
  static std::vector<std::unique_ptr<host::IPlatform::EventFn>> v;
  return v;
}
void event_trampoline(const char*, void* data, void* userdata) {
  auto* fn = static_cast<host::IPlatform::EventFn*>(userdata);
  if (!fn || !*fn) return;
  try {
    (*fn)(data ? std::string_view(static_cast<const char*>(data)) : std::string_view());
  } catch (...) {
    // never throw into X4Native
  }
}
}  // namespace

bool X4Platform::subscribe_event(const char* name, EventFn fn) {
  if (!api_ok() || !name || !fn || !::x4n::detail::g_api->subscribe) return false;
  auto owned = std::make_unique<EventFn>(std::move(fn));
  const int id = ::x4n::on(name, &event_trampoline, owned.get());
  if (id <= 0) return false;
  event_subscriptions_.push_back(id);
  event_fns().push_back(std::move(owned));
  return true;
}

bool X4Platform::raise_lua(const char* name, std::string_view text) {
  if (!api_ok() || !name || !::x4n::detail::g_api->raise_lua_event) return false;
  const std::string owned(text);
  return ::x4n::raise_lua(name, owned.c_str()) == 0;
}

X4Platform::~X4Platform() {
  if (api_ok() && ::x4n::detail::g_api->unsubscribe) {
    for (const int id : event_subscriptions_) ::x4n::off(id);
  }
  event_subscriptions_.clear();
  event_fns().clear();
}

}  // namespace x4mp::game
