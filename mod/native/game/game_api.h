#pragma once
// game/game_api: the adapter over the X4 game exports the mod uses (M2-04). SDK-free on purpose: the function
// pointers are plain C signatures resolved by NAME through a lookup callback (X4NativeAPI::get_game_function in the
// DLL, a fake in tests and in hostsim). Any function can be missing (nullptr); every wrapper is null-safe and says so
// in its return value instead of crashing.
//
// Threading: every wrapper first runs assert_main_thread() (game/main_thread.h) and refuses the call when it fails.
// Timing: the game functions need a loaded game; the host only calls the universe-dependent ones
// (player guard) after on_universe_ready. The save-list functions are start-menu safe.

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::game {

using UniverseId = std::uint64_t;  // X4 UniverseID

// Binary-compatible with the SDK's GameVersion {int major; int minor;} (returned by value from GetGameVersion).
struct GameVersionPod {
  int major = 0;
  int minor = 0;
};

// Binary-compatible with the SDK's UIPosRot {float x, y, z, yaw, pitch, roll;} (passed and returned by value by the
// M3 object exports). hostsim static_asserts the layout against the SDK type.
struct PosRotPod {
  float x = 0, y = 0, z = 0;
  float yaw = 0, pitch = 0, roll = 0;  // raw units as the game uses them (S13.2c settles degrees vs radians)
};

// Raw exports. Names match the X4 exports exactly (they are looked up by these strings).
struct GameFns {
  // M2 exports
  double (*GetCurrentGameTime)() = nullptr;
  const char* (*GetSaveFolderPath)() = nullptr;
  bool (*IsSaveListLoadingComplete)() = nullptr;
  bool (*IsSaveValid)(const char* filename) = nullptr;
  void (*ReloadSaveList)() = nullptr;
  bool (*IsGamePaused)() = nullptr;
  GameVersionPod (*GetGameVersion)() = nullptr;
  const char* (*GetBuildVersionSuffix)() = nullptr;
  // Player guard inputs (mod-design 6.1)
  UniverseId (*GetPlayerOccupiedShipID)() = nullptr;
  UniverseId (*GetPlayerControlledShipID)() = nullptr;
  UniverseId (*GetPlayerID)() = nullptr;
  UniverseId (*GetPlayerObjectID)() = nullptr;
  UniverseId (*GetPlayerContainerID)() = nullptr;
  UniverseId (*GetContextByClass)(UniverseId componentid, const char* classname, bool includeself) = nullptr;
  bool (*IsValidComponent)(UniverseId componentid) = nullptr;
  // M3 exports (M3-04): ghosts, avatars, own-ship tracking, takeover. Signatures verified against the X4Native 9.0.0-611726 table.
  UniverseId (*SpawnObjectAtPos2)(const char* macroname, UniverseId sectorid, PosRotPod offset, const char* ownerid) = nullptr;
  void (*ActivateObject)(UniverseId objectid, bool active) = nullptr;
  void (*SetObjectSectorPos)(UniverseId objectid, UniverseId sectorid, PosRotPod offset) = nullptr;
  PosRotPod (*GetObjectPositionInSector)(UniverseId objectid) = nullptr;
  bool (*TeleportPlayerTo)(UniverseId controllableid, bool allowcontrolling, bool instant, bool force) = nullptr;
  const char* (*CanTeleportPlayerTo)(UniverseId controllableid, bool allowcontrolling, bool force) = nullptr;  // "granted" = allowed (S13.6); else the reason
  void (*SetComponentOwner)(UniverseId componentid, const char* factionid) = nullptr;
  void (*SetObjectForcedRadarVisible)(UniverseId objectid, bool value) = nullptr;
  bool (*IsSetaActive)() = nullptr;
  const char* (*GetObjectIDCode)(UniverseId objectid) = nullptr;
  const char* (*GetComponentName)(UniverseId componentid) = nullptr;
  bool (*IsComponentWrecked)(UniverseId componentid) = nullptr;
  bool (*IsPlayerOccupiedShipDocked)() = nullptr;
};

// Number of exports in GameFns (keep in step with resolve_game_fns / missing_exports; the self-test reports resolved/total).
inline constexpr std::size_t kGameFnCount = 28;

using GetFunctionFn =std::function<void*(const char* name)>;  // X4NativeAPI::get_game_function or a fake

// Resolves every GameFns member by name through `get` (nullptr `get` => all missing).
[[nodiscard]] GameFns resolve_game_fns(const GetFunctionFn& get);

// Names of the M2 exports that are nullptr in `fns` (for the init log / self-test). Player-guard inputs included.
[[nodiscard]] std::vector<std::string> missing_exports(const GameFns& fns);

// Static facts about the running game/loader, copied at init (strings are owned here).
struct GameInfo {
  std::string game_version;       // X4Native get_game_version(), e.g. "9.00"
  std::string x4native_version;   // get_x4native_version()
  int game_types_build = 0;       // X4NativeAPI::game_types_build (e.g. 900)
};

// The text CanTeleportPlayerTo answers when the teleport is allowed (real game, spike S13.6).
inline constexpr const char* kTeleportGranted = "granted";
[[nodiscard]] inline bool is_teleport_granted(const std::optional<std::string>& answer) noexcept { return answer.has_value() && *answer == kTeleportGranted; }

class GameApi {
 public:
  GameApi() = default;
  GameApi(GameFns fns, GameInfo info) : fns_(fns), info_(std::move(info)) {}

  [[nodiscard]] const GameFns& fns() const noexcept { return fns_; }
  [[nodiscard]] const GameInfo& info() const noexcept { return info_; }

  // ---- M2 exports; nullopt / false when the export is missing or the main-thread check fails ----
  [[nodiscard]] std::optional<double> game_time() const noexcept;
  [[nodiscard]] std::optional<std::string> save_folder_path() const;
  [[nodiscard]] bool save_list_loading_complete() const noexcept;  // false when missing
  [[nodiscard]] bool save_valid(std::string_view filename) const noexcept;  // false when missing / empty name
  [[nodiscard]] bool reload_save_list() const noexcept;            // true when the call was made
  [[nodiscard]] bool game_paused() const noexcept;                 // false when missing
  [[nodiscard]] std::optional<GameVersionPod> game_version_struct() const noexcept;
  [[nodiscard]] std::optional<std::string> build_version_suffix() const;

  // ---- player guard inputs: 0 = unavailable / no such object ----
  [[nodiscard]] UniverseId player_occupied_ship() const noexcept;
  [[nodiscard]] UniverseId player_controlled_ship() const noexcept;
  [[nodiscard]] UniverseId player_id() const noexcept;
  [[nodiscard]] UniverseId player_object() const noexcept;
  [[nodiscard]] UniverseId player_container() const noexcept;
  [[nodiscard]] UniverseId context_by_class(UniverseId id, const char* classname, bool include_self) const noexcept;
  // true when the export is missing (cannot filter): callers treat "unknown" as valid.
  [[nodiscard]] bool is_valid_component(UniverseId id) const noexcept;

  // ---- M3 exports (M3-04). All null-safe: a missing export or a failed main-thread check gives the stated default. ----
  // 0 = missing / refused / the game returned 0. The owner is a faction id ("x4mp_team_1", "player").
  [[nodiscard]] UniverseId spawn_object(const char* macro, UniverseId sector, const PosRotPod& pos, const char* owner) const noexcept;
  bool activate_object(UniverseId id, bool active) const noexcept;                                      // true when the call was made
  bool set_object_sector_pos(UniverseId id, UniverseId sector, const PosRotPod& pos) const noexcept;    // true when the call was made
  [[nodiscard]] std::optional<PosRotPod> object_position(UniverseId id) const noexcept;                 // nullopt: missing / id 0
  bool teleport_player_to(UniverseId id, bool allow_controlling, bool instant, bool force) const noexcept;  // false when missing / refused
  // nullopt = the export is missing (unknown). Otherwise the game's own text: "granted" when allowed (session-4 S13.6; M3-12 fixed the wrapper, "" is NOT
  // "allowed"), else the reason. Use is_teleport_granted() to decide.
  [[nodiscard]] std::optional<std::string> can_teleport_player_to(UniverseId id, bool allow_controlling, bool force) const;
  bool set_component_owner(UniverseId id, const char* faction) const noexcept;                          // true when the call was made
  bool set_object_forced_radar_visible(UniverseId id, bool value) const noexcept;                       // true when the call was made
  [[nodiscard]] bool seta_active() const noexcept;                                                      // false when missing
  [[nodiscard]] std::optional<std::string> object_id_code(UniverseId id) const;                         // copied at once (the game reuses a buffer)
  [[nodiscard]] std::optional<std::string> component_name(UniverseId id) const;
  [[nodiscard]] bool component_wrecked(UniverseId id) const noexcept;                                   // false when missing
  [[nodiscard]] bool player_ship_docked() const noexcept;                                               // false when missing

 private:
  GameFns fns_{};
  GameInfo info_{};
};

}  // namespace x4mp::game
