// X4Native extension entry point for x4mp.dll. The only translation unit (besides game/) that sees the X4Native SDK.

#include <x4n_core.h>
#include <x4n_log.h>

#include "core/version/version.h"

X4N_EXTENSION {
  // Logs to <profile>\x4native\x4mp.log (the framework opens the per-extension log before this runs).
  x4n::log::info("{}", x4mp::version::hello_line());
}

X4N_SHUTDOWN {
  x4n::log::info("x4mp shutdown");
}