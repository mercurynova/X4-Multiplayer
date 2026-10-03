// x4mp_probe smoke test (M2-005): loads the real x4mp_probe.dll with a stub X4NativeAPI and checks its init line.
//   x4mp_probe_smoke <path-to-x4mp_probe.dll>
#include <windows.h>

#include <cstdio>
#include <filesystem>
#include <string>

#include "stub_host.h"

int main(int argc, char** argv) {
  if (argc < 2) return 2;
  const HMODULE dll = LoadLibraryA(argv[1]);
  if (!dll) { std::fprintf(stderr, "LoadLibrary failed (%lu)\n", GetLastError()); return 1; }
  using Init = int (*)(X4NativeAPI*);
  using Fn = void (*)();
  using Dir = void (*)(const wchar_t*);
  const auto init = reinterpret_cast<Init>(GetProcAddress(dll, "x4native_init"));
  const auto shut = reinterpret_cast<Fn>(GetProcAddress(dll, "x4native_shutdown"));
  const auto dir = reinterpret_cast<Dir>(GetProcAddress(dll, "x4mp_probe_set_config_dir"));
  if (!init || !shut || !dir) { std::fprintf(stderr, "missing exports\n"); return 1; }
  const auto tmp = std::filesystem::temp_directory_path() / "x4mp_probe_smoke_nocfg";  // no config file: nothing connects
  std::filesystem::create_directories(tmp);
  dir(tmp.c_str());
  stub::Host host;
  if (init(&host.api) != 0) return 1;
  host.fire("on_frame_update");
  const bool ok = host.log_contains("[X4MP-PROBE]") && host.log_contains("x4mp_probe init");
  shut();
  for (const auto& l : host.log_copy()) std::puts(l.c_str());
  FreeLibrary(dll);
  std::puts(ok ? "SMOKE OK" : "SMOKE FAILED");
  return ok ? 0 : 1;
}
