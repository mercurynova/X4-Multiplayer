#pragma once
// x4mp_probe: throwaway native probe for in-game session 2 (M2-005). See ../README.md for every log tag and key.
// The DLL entry points (X4N_EXTENSION / X4N_SHUTDOWN) live in dll_main.cpp and just call these two.

#include <filesystem>

namespace x4mp_probe {

// Test seam only (the shipped flow never calls it): use this directory instead of Documents\Egosoft\X4\x4mp.
void set_config_dir_override(const std::filesystem::path& dir);

// x4n::detail::g_api must already be set. Safe to call again after shutdown() (reload).
void init();
void shutdown();

}  // namespace x4mp_probe
