#include "host/paths.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <knownfolders.h>
#include <shlobj.h>

namespace x4mp::host {

std::filesystem::path documents_dir() {
  PWSTR raw = nullptr;
  std::filesystem::path out;
  if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Documents, KF_FLAG_DEFAULT, nullptr, &raw)) && raw) out = raw;
  if (raw) CoTaskMemFree(raw);
  return out;
}

ConfigPaths resolve_config_paths(const std::filesystem::path& extension_dir, const std::filesystem::path& documents) {
  ConfigPaths p;
  std::error_code ec;
  const bool portable = !extension_dir.empty() && (std::filesystem::exists(extension_dir / "x4mp.json", ec) ||
                                                   std::filesystem::exists(extension_dir / "x4mp.portable", ec));
  std::filesystem::path docs = documents.empty() ? documents_dir() : documents;
  if (portable || docs.empty()) {
    p.dir = extension_dir;
    p.portable = true;
  } else {
    p.dir = docs / "Egosoft" / "X4" / "x4mp";
  }
  p.user_file = p.dir / "x4mp.json";
  p.launch_file = p.dir / "launch.json";
  p.default_log_file = p.dir / "logs" / "x4mp.log";
  return p;
}

}  // namespace x4mp::host
