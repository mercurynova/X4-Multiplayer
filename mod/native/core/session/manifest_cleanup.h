#pragma once
// core/session: removal of the session manifests this mod left in the game's save folder (close-out A item 7).
//
// Every join downloads the session save "x4mp_<12 hex of the save sha>.xml.gz" and its manifest "x4mp_<12 hex of the MANIFEST sha>.x4mf" into
// the save folder. A new checkpoint is a new save and a new manifest, so the manifests piled up there (one per join). The manifest's name derives
// from the manifest's own hash, so it cannot be matched to a save file by name: the rule is "only the manifest of the current session save
// is kept". Only files named exactly like the ones this mod creates are ever touched; the player's saves, other mods' files, directories,
// symlinks and "<name>.x4mf.part" files of a transfer in progress are never deleted.

#include <cstddef>
#include <filesystem>
#include <string>
#include <string_view>

namespace x4mp::session {

// "x4mp_" + exactly 12 lowercase hex digits + ".x4mf"
[[nodiscard]] bool is_manifest_file_name(std::string_view file_name) noexcept;

// Deletes every file in `save_dir` for which is_manifest_file_name() holds, except the one named `keep_file_name` (empty = keep none).
// Best effort, never throws. Returns the number of files removed.
std::size_t prune_old_manifests(const std::filesystem::path& save_dir, std::string_view keep_file_name) noexcept;

}  // namespace x4mp::session
