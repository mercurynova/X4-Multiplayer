#pragma once
// host/build_check: the supported-build gate (M2-04, mod-design 1.5 "pinned build, fail loudly").
//
// The pin is "<major*100+minor>-<build number>", for example "900-611726" (core/version game_build_pin()).
// Inputs come from X4Native (get_game_version() "9.00", game_types_build) and the game (GetGameVersion struct,
// GetBuildVersionSuffix string). The suffix format is not verified in game yet (in-game verification item): the build
// number is taken as the longest run of >= 5 digits in the suffix, so "(611726)", "611726" and "9.00 (611726)" all work.
//
//   Supported   version and build number both match the pin.
//   Unsupported the version or the build number is known and differs (the host refuses to start).
//   Unverified  the version matches but no build number could be read; the host starts with a WARN and logs the raw
//               values so the user can report them. (A wrong build can only be proven wrong, not right, without it;
//               X4Native's own version_db already refuses to resolve hooks on a build it does not know.)

#include <optional>
#include <string>
#include <string_view>

#include "game/game_api.h"

namespace x4mp::host {

enum class BuildStatus { Supported, Unsupported, Unverified };

struct BuildInfo {
  std::string game_version;                          // "9.00" (X4Native get_game_version), may be empty
  std::optional<game::GameVersionPod> version;       // GetGameVersion()
  std::optional<std::string> build_suffix;           // GetBuildVersionSuffix()
  int game_types_build = 0;                          // X4NativeAPI::game_types_build (900 for 9.00), 0 = unknown
};

struct BuildCheck {
  BuildStatus status = BuildStatus::Unsupported;
  std::string detected;  // "900-611726", "900-?" or "?-?" (what we could read)
  std::string reason;    // human readable, never empty; safe to show in the UI
};

[[nodiscard]] BuildCheck check_build(const BuildInfo& info, std::string_view pin);

// Helpers (exposed for tests).
[[nodiscard]] std::optional<int> parse_version_code(std::string_view text);          // "9.00" -> 900, "7.50" -> 750 (minor = its integer value)
[[nodiscard]] std::optional<std::string> extract_build_number(std::string_view text);  // longest digit run >= 5

}  // namespace x4mp::host
