#pragma once
// host/paths: where x4mp.json, launch.json and the log live (mod-design 2.6 / 2.7). No environment variables.
//
//   Normal:    %LocalAppData%\X4MP\{x4mp.json, launch.json, player.key, logs\x4mp.log, ...}   (known-folder API; M3-24:
//              per-machine on purpose, Documents is often OneDrive-redirected and shared by every PC of one user)
//   Legacy:    %Documents%\Egosoft\X4\x4mp\ (before M3-24). x4mp.json is copied once from there; player.key never is.
//   Portable:  <extension folder>\{x4mp.json, launch.json, logs\x4mp.log}, selected when the extension folder contains
//              x4mp.json or an (empty) x4mp.portable marker. Used by hostsim and CI so they never touch Documents.

#include <filesystem>
#include <string>

namespace x4mp::host {

struct ConfigPaths {
  std::filesystem::path dir;
  std::filesystem::path user_file;         // x4mp.json (may not exist)
  std::filesystem::path launch_file;       // launch.json (one-shot, consumed by config::load)
  std::filesystem::path default_log_file;  // used when the config has no log_file
  std::filesystem::path legacy_dir;        // old Documents\Egosoft\X4\x4mp (empty in portable mode); read-only, for the one-time migration
  bool portable = false;
};

// The user's Documents folder via SHGetKnownFolderPath; empty when unavailable.
[[nodiscard]] std::filesystem::path documents_dir();

// The per-machine LocalAppData folder via SHGetKnownFolderPath; empty when unavailable.
[[nodiscard]] std::filesystem::path local_app_data_dir();

// Stable per-machine id (Windows MachineGuid); empty when unavailable. NEVER log or print it: use machine_tag().
[[nodiscard]] std::string machine_guid();

// `documents` empty => documents_dir(); `local_app_data` empty => local_app_data_dir(). Portable markers win. Otherwise the
// config dir is <local_app_data>\X4MP. If LocalAppData is unavailable the old Documents dir is used, and if that is missing
// too the extension folder is used as a last resort.
[[nodiscard]] ConfigPaths resolve_config_paths(const std::filesystem::path& extension_dir,
                                               const std::filesystem::path& documents = {},
                                               const std::filesystem::path& local_app_data = {});

// One-time copy of x4mp.json from paths.legacy_dir to paths.dir when the new one does not exist (the old file is kept).
// Returns true when a copy was made. Never touches player.key.
[[nodiscard]] bool migrate_user_config(const ConfigPaths& paths);

}  // namespace x4mp::host
