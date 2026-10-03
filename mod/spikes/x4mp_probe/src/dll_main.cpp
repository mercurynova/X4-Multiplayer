// x4mp_probe.dll entry points (X4Native extension). All logic is in probe.cpp.

#include <x4n_core.h>

#include "probe.h"

X4N_EXTENSION { x4mp_probe::init(); }

X4N_SHUTDOWN { x4mp_probe::shutdown(); }

// Test seam for the smoke test (never called by X4Native): redirect the config directory away from Documents.
extern "C" __declspec(dllexport) void x4mp_probe_set_config_dir(const wchar_t* dir) {
  if (dir) x4mp_probe::set_config_dir_override(std::filesystem::path(dir));
}
