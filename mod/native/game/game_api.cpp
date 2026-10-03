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

}  // namespace x4mp::game
