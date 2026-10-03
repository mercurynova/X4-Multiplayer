#include "game/game_api.h"

#include "game/main_thread.h"

namespace x4mp::game {

namespace {
template <class Fn>
void bind(const GetFunctionFn& get, const char* name, Fn& out) {
  out = get ? reinterpret_cast<Fn>(get(name)) : nullptr;
}
}  // namespace

GameFns resolve_game_fns(const GetFunctionFn& get) {
  GameFns f;
  bind(get, "GetCurrentGameTime", f.GetCurrentGameTime);
  bind(get, "GetSaveFolderPath", f.GetSaveFolderPath);
  bind(get, "IsSaveListLoadingComplete", f.IsSaveListLoadingComplete);
  bind(get, "IsSaveValid", f.IsSaveValid);
  bind(get, "ReloadSaveList", f.ReloadSaveList);
  bind(get, "IsGamePaused", f.IsGamePaused);
  bind(get, "GetGameVersion", f.GetGameVersion);
  bind(get, "GetBuildVersionSuffix", f.GetBuildVersionSuffix);
  bind(get, "GetPlayerOccupiedShipID", f.GetPlayerOccupiedShipID);
  bind(get, "GetPlayerControlledShipID", f.GetPlayerControlledShipID);
  bind(get, "GetPlayerID", f.GetPlayerID);
  bind(get, "GetPlayerObjectID", f.GetPlayerObjectID);
  bind(get, "GetPlayerContainerID", f.GetPlayerContainerID);
  bind(get, "GetContextByClass", f.GetContextByClass);
  bind(get, "IsValidComponent", f.IsValidComponent);
  bind(get, "SpawnObjectAtPos2", f.SpawnObjectAtPos2);
  bind(get, "ActivateObject", f.ActivateObject);
  bind(get, "SetObjectSectorPos", f.SetObjectSectorPos);
  bind(get, "GetObjectPositionInSector", f.GetObjectPositionInSector);
  bind(get, "TeleportPlayerTo", f.TeleportPlayerTo);
  bind(get, "CanTeleportPlayerTo", f.CanTeleportPlayerTo);
  bind(get, "SetComponentOwner", f.SetComponentOwner);
  bind(get, "SetObjectForcedRadarVisible", f.SetObjectForcedRadarVisible);
  bind(get, "IsSetaActive", f.IsSetaActive);
  bind(get, "GetObjectIDCode", f.GetObjectIDCode);
  bind(get, "GetComponentName", f.GetComponentName);
  bind(get, "IsComponentWrecked", f.IsComponentWrecked);
  bind(get, "IsPlayerOccupiedShipDocked", f.IsPlayerOccupiedShipDocked);
  return f;
}

std::vector<std::string> missing_exports(const GameFns& f) {
  std::vector<std::string> out;
  const auto check = [&out](const void* p, const char* name) {
    if (!p) out.emplace_back(name);
  };
  check(reinterpret_cast<const void*>(f.GetCurrentGameTime), "GetCurrentGameTime");
  check(reinterpret_cast<const void*>(f.GetSaveFolderPath), "GetSaveFolderPath");
  check(reinterpret_cast<const void*>(f.IsSaveListLoadingComplete), "IsSaveListLoadingComplete");
  check(reinterpret_cast<const void*>(f.IsSaveValid), "IsSaveValid");
  check(reinterpret_cast<const void*>(f.ReloadSaveList), "ReloadSaveList");
  check(reinterpret_cast<const void*>(f.IsGamePaused), "IsGamePaused");
  check(reinterpret_cast<const void*>(f.GetGameVersion), "GetGameVersion");
  check(reinterpret_cast<const void*>(f.GetBuildVersionSuffix), "GetBuildVersionSuffix");
  check(reinterpret_cast<const void*>(f.GetPlayerOccupiedShipID), "GetPlayerOccupiedShipID");
  check(reinterpret_cast<const void*>(f.GetPlayerControlledShipID), "GetPlayerControlledShipID");
  check(reinterpret_cast<const void*>(f.GetPlayerID), "GetPlayerID");
  check(reinterpret_cast<const void*>(f.GetPlayerObjectID), "GetPlayerObjectID");
  check(reinterpret_cast<const void*>(f.GetPlayerContainerID), "GetPlayerContainerID");
  check(reinterpret_cast<const void*>(f.GetContextByClass), "GetContextByClass");
  check(reinterpret_cast<const void*>(f.IsValidComponent), "IsValidComponent");
  check(reinterpret_cast<const void*>(f.SpawnObjectAtPos2), "SpawnObjectAtPos2");
  check(reinterpret_cast<const void*>(f.ActivateObject), "ActivateObject");
  check(reinterpret_cast<const void*>(f.SetObjectSectorPos), "SetObjectSectorPos");
  check(reinterpret_cast<const void*>(f.GetObjectPositionInSector), "GetObjectPositionInSector");
  check(reinterpret_cast<const void*>(f.TeleportPlayerTo), "TeleportPlayerTo");
  check(reinterpret_cast<const void*>(f.CanTeleportPlayerTo), "CanTeleportPlayerTo");
  check(reinterpret_cast<const void*>(f.SetComponentOwner), "SetComponentOwner");
  check(reinterpret_cast<const void*>(f.SetObjectForcedRadarVisible), "SetObjectForcedRadarVisible");
  check(reinterpret_cast<const void*>(f.IsSetaActive), "IsSetaActive");
  check(reinterpret_cast<const void*>(f.GetObjectIDCode), "GetObjectIDCode");
  check(reinterpret_cast<const void*>(f.GetComponentName), "GetComponentName");
  check(reinterpret_cast<const void*>(f.IsComponentWrecked), "IsComponentWrecked");
  check(reinterpret_cast<const void*>(f.IsPlayerOccupiedShipDocked), "IsPlayerOccupiedShipDocked");
  return out;
}

std::optional<double> GameApi::game_time() const noexcept {
  if (!fns_.GetCurrentGameTime || !assert_main_thread("GetCurrentGameTime")) return std::nullopt;
  return fns_.GetCurrentGameTime();
}

std::optional<std::string> GameApi::save_folder_path() const {
  if (!fns_.GetSaveFolderPath || !assert_main_thread("GetSaveFolderPath")) return std::nullopt;
  const char* p = fns_.GetSaveFolderPath();
  if (!p) return std::nullopt;
  return std::string(p);
}

bool GameApi::save_list_loading_complete() const noexcept {
  if (!fns_.IsSaveListLoadingComplete || !assert_main_thread("IsSaveListLoadingComplete")) return false;
  return fns_.IsSaveListLoadingComplete();
}

bool GameApi::save_valid(std::string_view filename) const noexcept {
  if (!fns_.IsSaveValid || filename.empty() || !assert_main_thread("IsSaveValid")) return false;
  try {
    const std::string name(filename);  // the export wants a NUL-terminated string
    return fns_.IsSaveValid(name.c_str());
  } catch (...) {
    return false;
  }
}

bool GameApi::reload_save_list() const noexcept {
  if (!fns_.ReloadSaveList || !assert_main_thread("ReloadSaveList")) return false;
  fns_.ReloadSaveList();
  return true;
}

bool GameApi::game_paused() const noexcept {
  if (!fns_.IsGamePaused || !assert_main_thread("IsGamePaused")) return false;
  return fns_.IsGamePaused();
}

std::optional<GameVersionPod> GameApi::game_version_struct() const noexcept {
  if (!fns_.GetGameVersion || !assert_main_thread("GetGameVersion")) return std::nullopt;
  return fns_.GetGameVersion();
}

std::optional<std::string> GameApi::build_version_suffix() const {
  if (!fns_.GetBuildVersionSuffix || !assert_main_thread("GetBuildVersionSuffix")) return std::nullopt;
  const char* p = fns_.GetBuildVersionSuffix();
  if (!p) return std::nullopt;
  return std::string(p);
}

UniverseId GameApi::player_occupied_ship() const noexcept {
  return (fns_.GetPlayerOccupiedShipID && assert_main_thread("GetPlayerOccupiedShipID")) ? fns_.GetPlayerOccupiedShipID() : 0;
}
UniverseId GameApi::player_controlled_ship() const noexcept {
  return (fns_.GetPlayerControlledShipID && assert_main_thread("GetPlayerControlledShipID")) ? fns_.GetPlayerControlledShipID() : 0;
}
UniverseId GameApi::player_id() const noexcept {
  return (fns_.GetPlayerID && assert_main_thread("GetPlayerID")) ? fns_.GetPlayerID() : 0;
}
UniverseId GameApi::player_object() const noexcept {
  return (fns_.GetPlayerObjectID && assert_main_thread("GetPlayerObjectID")) ? fns_.GetPlayerObjectID() : 0;
}
UniverseId GameApi::player_container() const noexcept {
  return (fns_.GetPlayerContainerID && assert_main_thread("GetPlayerContainerID")) ? fns_.GetPlayerContainerID() : 0;
}
UniverseId GameApi::context_by_class(UniverseId id, const char* classname, bool include_self) const noexcept {
  if (id == 0 || !fns_.GetContextByClass || !assert_main_thread("GetContextByClass")) return 0;
  return fns_.GetContextByClass(id, classname, include_self);
}
bool GameApi::is_valid_component(UniverseId id) const noexcept {
  if (!fns_.IsValidComponent) return true;
  if (!assert_main_thread("IsValidComponent")) return false;
  return fns_.IsValidComponent(id);
}

// ---- M3 exports ----
UniverseId GameApi::spawn_object(const char* macro, UniverseId sector, const PosRotPod& pos, const char* owner) const noexcept {
  if (!fns_.SpawnObjectAtPos2 || !macro || !*macro || sector == 0 || !assert_main_thread("SpawnObjectAtPos2")) return 0;
  return fns_.SpawnObjectAtPos2(macro, sector, pos, owner ? owner : "");
}
bool GameApi::activate_object(UniverseId id, bool active) const noexcept {
  if (id == 0 || !fns_.ActivateObject || !assert_main_thread("ActivateObject")) return false;
  fns_.ActivateObject(id, active);
  return true;
}
bool GameApi::set_object_sector_pos(UniverseId id, UniverseId sector, const PosRotPod& pos) const noexcept {
  if (id == 0 || sector == 0 || !fns_.SetObjectSectorPos || !assert_main_thread("SetObjectSectorPos")) return false;
  fns_.SetObjectSectorPos(id, sector, pos);
  return true;
}
std::optional<PosRotPod> GameApi::object_position(UniverseId id) const noexcept {
  if (id == 0 || !fns_.GetObjectPositionInSector || !assert_main_thread("GetObjectPositionInSector")) return std::nullopt;
  return fns_.GetObjectPositionInSector(id);
}
bool GameApi::teleport_player_to(UniverseId id, bool allow_controlling, bool instant, bool force) const noexcept {
  if (id == 0 || !fns_.TeleportPlayerTo || !assert_main_thread("TeleportPlayerTo")) return false;
  return fns_.TeleportPlayerTo(id, allow_controlling, instant, force);
}
std::optional<std::string> GameApi::can_teleport_player_to(UniverseId id, bool allow_controlling, bool force) const {
  if (id == 0 || !fns_.CanTeleportPlayerTo || !assert_main_thread("CanTeleportPlayerTo")) return std::nullopt;
  const char* p = fns_.CanTeleportPlayerTo(id, allow_controlling, force);
  return std::string(p ? p : "");
}
bool GameApi::set_component_owner(UniverseId id, const char* faction) const noexcept {
  if (id == 0 || !faction || !fns_.SetComponentOwner || !assert_main_thread("SetComponentOwner")) return false;
  fns_.SetComponentOwner(id, faction);
  return true;
}
bool GameApi::set_object_forced_radar_visible(UniverseId id, bool value) const noexcept {
  if (id == 0 || !fns_.SetObjectForcedRadarVisible || !assert_main_thread("SetObjectForcedRadarVisible")) return false;
  fns_.SetObjectForcedRadarVisible(id, value);
  return true;
}
bool GameApi::seta_active() const noexcept {
  return fns_.IsSetaActive && assert_main_thread("IsSetaActive") && fns_.IsSetaActive();
}
std::optional<std::string> GameApi::object_id_code(UniverseId id) const {
  if (id == 0 || !fns_.GetObjectIDCode || !assert_main_thread("GetObjectIDCode")) return std::nullopt;
  const char* p = fns_.GetObjectIDCode(id);
  return std::string(p ? p : "");
}
std::optional<std::string> GameApi::component_name(UniverseId id) const {
  if (id == 0 || !fns_.GetComponentName || !assert_main_thread("GetComponentName")) return std::nullopt;
  const char* p = fns_.GetComponentName(id);
  return std::string(p ? p : "");
}
bool GameApi::component_wrecked(UniverseId id) const noexcept {
  return id != 0 && fns_.IsComponentWrecked && assert_main_thread("IsComponentWrecked") && fns_.IsComponentWrecked(id);
}
bool GameApi::player_ship_docked() const noexcept {
  return fns_.IsPlayerOccupiedShipDocked && assert_main_thread("IsPlayerOccupiedShipDocked") && fns_.IsPlayerOccupiedShipDocked();
}

}  // namespace x4mp::game
