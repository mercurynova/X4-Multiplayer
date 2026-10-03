#pragma once
// host/paths: where x4mp.json, launch.json and the log live (mod-design 2.6 / 2.7). No environment variables.
//
//   Normal:    %Documents%\Egosoft\X4\x4mp\{x4mp.json, launch.json, logs\x4mp.log}   (known-folder API)
//   Portable:  <extension folder>\{x4mp.json, launch.json, logs\x4mp.log}, selected when the extension folder contains
//              x4mp.json or an (empty) x4mp.portable marker. Used by hostsim and CI so they never touch Documents.

#include <filesystem>

namespace x4mp::host {

struct ConfigPaths {
  std::filesystem::path dir;
  std::filesystem::path user_file;         // x4mp.json (may not exist)
  std::filesystem::path launch_file;       // launch.json (one-shot, consumed by config::load)
  std::filesystem::path default_log_file;  // used when the config has no log_file
  bool portable = false;
};

// The user's Documents folder via SHGetKnownFolderPath; empty when unavailable.
[[nodiscard]] std::filesystem::path documents_dir();

// `documents` empty => documents_dir(). When both the portable markers and the documents folder are missing the
// extension folder is used as a last resort.
[[nodiscard]] ConfigPaths resolve_config_paths(const std::filesystem::path& extension_dir,
                                               const std::filesystem::path& documents = {});

}  // namespace x4mp::host
