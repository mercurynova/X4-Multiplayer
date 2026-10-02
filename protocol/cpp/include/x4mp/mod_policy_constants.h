// Generated from protocol/constants/mod_policy.json by X4MP.Protocol.Tests ModPolicyConstantsTests. Do not edit.
// Shared mod-policy constants (docs/mod-management.md); same values as C# X4MP.Protocol.ModPolicyConstants.
#pragma once

#include <array>
#include <string_view>

namespace x4mp::mod_policy {

inline constexpr std::array<std::string_view, 4> kClientOnlyLibraryIds{"kuerteeUIExtensionsAndHUD", "ws_3477279743", "ws_2042901274", "ws_3514258146"};
inline constexpr std::array<std::string_view, 2> kHashExcludedIds{"x4native", "x4mp"};
inline constexpr std::string_view kDlcIdPrefix = "ego_dlc_";
inline constexpr std::string_view kWorkshopIdPrefix = "ws_";
inline constexpr std::string_view kNexusUrlRegex = "^https://(www\\.)?nexusmods\\.com/x4foundations/mods/(\\d+)(/.*)?$";
inline constexpr std::string_view kNexusUrlFormat = "https://www.nexusmods.com/x4foundations/mods/{0}";
inline constexpr std::string_view kWorkshopUrlFormat = "https://steamcommunity.com/sharedfiles/filedetails/?id={0}";
inline constexpr std::string_view kWorkshopSteamUrlFormat = "steam://url/CommunityFilePage/{0}";
inline constexpr std::string_view kHashLineFormat = "{id}@{version}\n";
inline constexpr int kMaxExtensionEntries = 1000;

}  // namespace x4mp::mod_policy
