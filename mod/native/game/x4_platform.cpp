#include "game/x4_platform.h"

#include <x4n_core.h>
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

}  // namespace x4mp::game
