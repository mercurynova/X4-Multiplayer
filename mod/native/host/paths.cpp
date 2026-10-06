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

std::filesystem::path local_app_data_dir() {
  PWSTR raw = nullptr;
  std::filesystem::path out;
  if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &raw)) && raw) out = raw;
  if (raw) CoTaskMemFree(raw);
  return out;
}

std::string machine_guid() {
  HKEY key = nullptr;
  if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Cryptography", 0, KEY_READ | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS) return {};
  wchar_t buf[128] = {};
  DWORD size = sizeof buf - sizeof(wchar_t);
  DWORD type = 0;
  const LONG rc = RegQueryValueExW(key, L"MachineGuid", nullptr, &type, reinterpret_cast<LPBYTE>(buf), &size);
  RegCloseKey(key);
  if (rc != ERROR_SUCCESS || type != REG_SZ) return {};
  std::string out;
  for (const wchar_t* c = buf; *c; ++c) out.push_back(*c < 128 ? static_cast<char>(*c) : '?');
  return out;
}

ConfigPaths resolve_config_paths(const std::filesystem::path& extension_dir, const std::filesystem::path& documents,
                                 const std::filesystem::path& local_app_data) {
  ConfigPaths p;
  std::error_code ec;
  const bool portable = !extension_dir.empty() && (std::filesystem::exists(extension_dir / "x4mp.json", ec) ||
                                                   std::filesystem::exists(extension_dir / "x4mp.portable", ec));
  std::filesystem::path docs = documents.empty() ? documents_dir() : documents;
  const std::filesystem::path local = (portable || !local_app_data.empty()) ? local_app_data : local_app_data_dir();
  const std::filesystem::path old_dir = docs.empty() ? std::filesystem::path{} : docs / "Egosoft" / "X4" / "x4mp";
  if (portable) {
    p.dir = extension_dir;
    p.portable = true;
  } else if (!local.empty()) {
    p.dir = local / "X4MP";
    p.legacy_dir = old_dir;
  } else if (!old_dir.empty()) {
    p.dir = old_dir;  // no LocalAppData (should not happen): behave as before M3-24
  } else {
    p.dir = extension_dir;
    p.portable = true;
  }
  p.user_file = p.dir / "x4mp.json";
  p.launch_file = p.dir / "launch.json";
  p.default_log_file = p.dir / "logs" / "x4mp.log";
  return p;
}

bool migrate_user_config(const ConfigPaths& paths) {
  if (paths.portable || paths.legacy_dir.empty() || paths.legacy_dir == paths.dir) return false;
  std::error_code ec;
  const auto from = paths.legacy_dir / "x4mp.json";
  if (std::filesystem::exists(paths.user_file, ec) || !std::filesystem::exists(from, ec)) return false;
  std::filesystem::create_directories(paths.dir, ec);
  std::filesystem::copy_file(from, paths.user_file, std::filesystem::copy_options::skip_existing, ec);
  return !ec;
}

}  // namespace x4mp::host
