#include "host/extension_roots.h"

#include <array>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "host/paths.h"

namespace x4mp::host {

namespace fs = std::filesystem;

fs::path x4_install_dir() {
  std::array<wchar_t, 32768> buf{};
  const DWORD n = GetModuleFileNameW(nullptr, buf.data(), static_cast<DWORD>(buf.size()));
  if (n == 0 || n >= buf.size()) return {};
  return fs::path(std::wstring(buf.data(), n)).parent_path();
}

mods::ExtensionRoots make_extension_roots(const fs::path& x4_dir, const fs::path& documents) {
  mods::ExtensionRoots r;
  if (!x4_dir.empty()) {
    r.install_extensions = x4_dir / "extensions";
    // <steam library>\steamapps\common\<X4 folder>  ->  <steam library>\steamapps\workshop\content\392160
    const auto steamapps = x4_dir.parent_path().parent_path();
    if (!steamapps.empty()) r.workshop_roots.push_back(steamapps / "workshop" / "content" / "392160");
  }
  const auto docs = documents.empty() ? documents_dir() : documents;
  if (!docs.empty()) r.user_game_dirs.push_back(docs / "Egosoft" / "X4");
  return r;
}

}  // namespace x4mp::host
