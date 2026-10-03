#include "core/session/manifest_cleanup.h"

#include <algorithm>
#include <system_error>
#include <vector>

namespace x4mp::session {

namespace fs = std::filesystem;

bool is_manifest_file_name(std::string_view n) noexcept {
  constexpr std::string_view prefix = "x4mp_";
  constexpr std::string_view suffix = ".x4mf";
  if (n.size() != prefix.size() + 12 + suffix.size()) return false;
  if (n.substr(0, prefix.size()) != prefix || n.substr(n.size() - suffix.size()) != suffix) return false;
  return std::all_of(n.begin() + static_cast<std::ptrdiff_t>(prefix.size()), n.end() - static_cast<std::ptrdiff_t>(suffix.size()),
                     [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}

std::size_t prune_old_manifests(const fs::path& save_dir, std::string_view keep_file_name) noexcept {
  std::size_t removed = 0;
  try {
    std::error_code ec;
    if (save_dir.empty() || !fs::is_directory(save_dir, ec)) return 0;
    std::vector<fs::path> doomed;
    for (fs::directory_iterator it(save_dir, fs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec)) {
      const std::string name = it->path().filename().string();
      if (!is_manifest_file_name(name) || name == keep_file_name) continue;
      std::error_code tec;
      const auto st = it->symlink_status(tec);
      if (tec || !fs::is_regular_file(st)) continue;  // a regular file only: never a directory or a link
      doomed.push_back(it->path());
    }
    for (const auto& p : doomed) {
      std::error_code rec;
      if (fs::remove(p, rec) && !rec) ++removed;
    }
  } catch (...) {
  }
  return removed;
}

}  // namespace x4mp::session
