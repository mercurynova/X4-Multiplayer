#include "game/game_api.h"

#include <limits>
#include <string>
#include <utility>
#include <vector>

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
  bind(get, "GetPlayerZoneID", f.GetPlayerZoneID);
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
  bind(get, "GetCameraRotation", f.GetCameraRotation);
  bind(get, "GetPositionalOffset", f.GetPositionalOffset);
  bind(get, "SetPositionalOffset", f.SetPositionalOffset);
  bind(get, "GetNumCargo", f.GetNumCargo);
  bind(get, "GetCargo", f.GetCargo);
  bind(get, "SpawnStationAtPos", f.SpawnStationAtPos);
  bind(get, "GetOwnerDetails", f.GetOwnerDetails);
  bind(get, "AddPlayerMoney", f.AddPlayerMoney);
  bind(get, "GetInstalledPaintMod", f.GetInstalledPaintMod);
  bind(get, "InstallPaintMod", f.InstallPaintMod);
  bind(get, "GetCurrentFleetLogo", f.GetCurrentFleetLogo);
  bind(get, "SetFleetLogo", f.SetFleetLogo);
  bind(get, "GetInstalledShipMod2", f.GetInstalledShipMod2);
  bind(get, "InstallShipMod", f.InstallShipMod);
  bind(get, "DismantleShipMod", f.DismantleShipMod);
  bind(get, "GetInstalledEngineMod2", f.GetInstalledEngineMod2);
  bind(get, "InstallEngineMod", f.InstallEngineMod);
  bind(get, "DismantleEngineMod", f.DismantleEngineMod);
  bind(get, "GetNumShieldGroups", f.GetNumShieldGroups);
  bind(get, "GetShieldGroups", f.GetShieldGroups);
  bind(get, "GetInstalledShieldMod", f.GetInstalledShieldMod);
  bind(get, "InstallShieldMod", f.InstallShieldMod);
  bind(get, "DismantleShieldMod", f.DismantleShieldMod);
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
  check(reinterpret_cast<const void*>(f.GetPlayerZoneID), "GetPlayerZoneID");
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
  check(reinterpret_cast<const void*>(f.GetCameraRotation), "GetCameraRotation");
  check(reinterpret_cast<const void*>(f.GetPositionalOffset), "GetPositionalOffset");
  check(reinterpret_cast<const void*>(f.SetPositionalOffset), "SetPositionalOffset");
  check(reinterpret_cast<const void*>(f.GetNumCargo), "GetNumCargo");
  check(reinterpret_cast<const void*>(f.GetCargo), "GetCargo");
  check(reinterpret_cast<const void*>(f.SpawnStationAtPos), "SpawnStationAtPos");
  check(reinterpret_cast<const void*>(f.GetOwnerDetails), "GetOwnerDetails");
  check(reinterpret_cast<const void*>(f.AddPlayerMoney), "AddPlayerMoney");
  check(reinterpret_cast<const void*>(f.GetInstalledPaintMod), "GetInstalledPaintMod");
  check(reinterpret_cast<const void*>(f.InstallPaintMod), "InstallPaintMod");
  check(reinterpret_cast<const void*>(f.GetCurrentFleetLogo), "GetCurrentFleetLogo");
  check(reinterpret_cast<const void*>(f.SetFleetLogo), "SetFleetLogo");
  check(reinterpret_cast<const void*>(f.GetInstalledShipMod2), "GetInstalledShipMod2");
  check(reinterpret_cast<const void*>(f.InstallShipMod), "InstallShipMod");
  check(reinterpret_cast<const void*>(f.DismantleShipMod), "DismantleShipMod");
  check(reinterpret_cast<const void*>(f.GetInstalledEngineMod2), "GetInstalledEngineMod2");
  check(reinterpret_cast<const void*>(f.InstallEngineMod), "InstallEngineMod");
  check(reinterpret_cast<const void*>(f.DismantleEngineMod), "DismantleEngineMod");
  check(reinterpret_cast<const void*>(f.GetNumShieldGroups), "GetNumShieldGroups");
  check(reinterpret_cast<const void*>(f.GetShieldGroups), "GetShieldGroups");
  check(reinterpret_cast<const void*>(f.GetInstalledShieldMod), "GetInstalledShieldMod");
  check(reinterpret_cast<const void*>(f.InstallShieldMod), "InstallShieldMod");
  check(reinterpret_cast<const void*>(f.DismantleShieldMod), "DismantleShieldMod");
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
UniverseId GameApi::player_zone() const noexcept {
  return (fns_.GetPlayerZoneID && assert_main_thread("GetPlayerZoneID")) ? fns_.GetPlayerZoneID() : 0;
}
std::optional<RotationPod> GameApi::camera_rotation() const noexcept {
  if (!fns_.GetCameraRotation || !assert_main_thread("GetCameraRotation")) return std::nullopt;
  return fns_.GetCameraRotation();
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
  return fns_.SpawnObjectAtPos2(macro, sector, pos_rot_to_game_write(pos), owner ? owner : "");  // pos: radians in, degrees out
}
// M5-19: a station, not a ship. Same unit rule as spawn_object; the plan id is passed through verbatim ("" = macro + modules).
UniverseId GameApi::spawn_station(const char* macro, UniverseId sector, const PosRotPod& pos, const char* plan,
                                  const char* owner) const noexcept {
  if (!fns_.SpawnStationAtPos || !macro || !*macro || sector == 0 || !assert_main_thread("SpawnStationAtPos")) return 0;
  return fns_.SpawnStationAtPos(macro, sector, pos_rot_to_game_write(pos), plan ? plan : "", owner ? owner : "");
}
bool GameApi::activate_object(UniverseId id, bool active) const noexcept {
  if (id == 0 || !fns_.ActivateObject || !assert_main_thread("ActivateObject")) return false;
  fns_.ActivateObject(id, active);
  return true;
}
bool GameApi::set_object_sector_pos(UniverseId id, UniverseId sector, const PosRotPod& pos) const noexcept {
  if (id == 0 || sector == 0 || !fns_.SetObjectSectorPos || !assert_main_thread("SetObjectSectorPos")) return false;
  fns_.SetObjectSectorPos(id, sector, pos_rot_to_game_write(pos));  // pos: radians in, degrees out
  return true;
}
std::optional<PosRotPod> GameApi::object_position(UniverseId id) const noexcept {
  if (id == 0 || !fns_.GetObjectPositionInSector || !assert_main_thread("GetObjectPositionInSector")) return std::nullopt;
  return fns_.GetObjectPositionInSector(id);
}
std::optional<PosRotPod> GameApi::positional_offset(UniverseId id, UniverseId space) const noexcept {
  // `space` 0 is the documented form for "relative to the object's direct parent" (docs/research/on-foot-presence.md 2.2:
  // `GetPositionalOffset(id, 0)`, sdk/x4_game_func_list.inc:1019). A walking player's parent is the room, so that call returns
  // the ROOM-local pose at eye height, which is exactly what add_actor_to_room/SetPositionalOffset take. Any other id asks for
  // the offset in that component's space.
  if (id == 0 || !fns_.GetPositionalOffset || !assert_main_thread("GetPositionalOffset")) return std::nullopt;
  return fns_.GetPositionalOffset(id, space);
}
bool GameApi::set_positional_offset(UniverseId id, const PosRotPod& pos) const noexcept {
  if (id == 0 || !fns_.SetPositionalOffset || !assert_main_thread("SetPositionalOffset")) return false;
  fns_.SetPositionalOffset(id, pos_rot_to_game_write(pos));  // degrees on the write side (measured 2026-11-05); reads give radians
  return true;
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
std::vector<CargoPod> GameApi::cargo(UniverseId container) const {
  std::vector<CargoPod> out;
  if (container == 0 || !fns_.GetNumCargo || !fns_.GetCargo || !assert_main_thread("GetCargo")) return out;
  const std::uint32_t n = fns_.GetNumCargo(container, "");
  if (n == 0) return out;
  const std::uint32_t want = n > kMaxCargoLines ? kMaxCargoLines : n;
  std::vector<WareInfoPod> raw(want);
  const std::uint32_t got = fns_.GetCargo(raw.data(), want, container, "");
  out.reserve(got);
  for (std::uint32_t i = 0; i < got && i < want; ++i) {
    const WareInfoPod& w = raw[i];
    if (!w.ware || !*w.ware || w.amount == 0) continue;
    out.push_back(CargoPod{std::string(w.ware), w.amount});
  }
  return out;
}

std::string GameApi::owner_faction(UniverseId component) const noexcept {
  if (component == 0 || !fns_.GetOwnerDetails || !assert_main_thread("GetOwnerDetails")) return {};
  const OwnerFactionPod details = fns_.GetOwnerDetails(component);
  if (!details.faction_id || !*details.faction_id) return {};
  return std::string(details.faction_id);
}

bool GameApi::add_player_money(std::int64_t delta) const noexcept {
  if (delta == 0 || !fns_.AddPlayerMoney || !assert_main_thread("AddPlayerMoney")) return false;
  // Credits in, cents out: the export's unit is not the one GetPlayerMoney reports (decisions.md:519). A delta that would
  // overflow the conversion is refused rather than wrapped into a nonsense write.
  constexpr std::int64_t kMaxCredits = std::numeric_limits<std::int64_t>::max() / kCentsPerCredit;
  if (delta > kMaxCredits || delta < -kMaxCredits) return false;
  fns_.AddPlayerMoney(delta * kCentsPerCredit);
  return true;
}

std::string GameApi::installed_paint(UniverseId id) const {
  if (id == 0 || !fns_.GetInstalledPaintMod || !assert_main_thread("GetInstalledPaintMod")) return {};
  PaintModPod p;
  if (!fns_.GetInstalledPaintMod(id, &p) || !p.ware || !*p.ware) return {};
  return std::string(p.ware);
}

bool GameApi::install_paint(UniverseId id, std::string_view ware) const noexcept {
  if (id == 0 || ware.empty() || !fns_.InstallPaintMod || !assert_main_thread("InstallPaintMod")) return false;
  try {
    const std::string w(ware);  // the export wants a NUL-terminated string
    fns_.InstallPaintMod(id, w.c_str(), /*useinventory*/ false);
    return true;
  } catch (...) {
    return false;
  }
}

std::string GameApi::fleet_logo(UniverseId id) const {
  if (id == 0 || !fns_.GetCurrentFleetLogo || !assert_main_thread("GetCurrentFleetLogo")) return {};
  LogoPod logo{};
  try {
    logo = fns_.GetCurrentFleetLogo(id);
  } catch (...) {
    return {};
  }
  if (logo.file == nullptr || *logo.file == '\0') return {};
  std::string out(logo.file);
  out.push_back('|');
  if (logo.icon != nullptr) out += logo.icon;
  return out;
}

bool GameApi::set_fleet_logo(UniverseId id, std::string_view spec) const noexcept {
  if (id == 0 || spec.empty() || !fns_.SetFleetLogo || !assert_main_thread("SetFleetLogo")) return false;
  const std::size_t bar = spec.rfind('|');
  if (bar == 0) return false;  // "|icon" names no file
  const std::string file(spec.substr(0, bar == std::string_view::npos ? spec.size() : bar));
  const std::string icon(bar == std::string_view::npos ? std::string_view{} : spec.substr(bar + 1));
  if (file.empty()) return false;
  try {
    LogoPod logo{};
    logo.file = file.c_str();
    logo.icon = icon.c_str();
    logo.personal = true;  // a logo the mod relays came from a player's own library, not the standard set
    fns_.SetFleetLogo(id, logo);
    return true;
  } catch (...) {
    return false;
  }
}

namespace {

// M18: "a;b;c" -> its non-empty parts.
std::vector<std::string_view> split_parts(std::string_view s, char sep) {
  std::vector<std::string_view> out;
  std::size_t at = 0;
  while (at <= s.size()) {
    const std::size_t end = s.find(sep, at);
    const std::string_view part = s.substr(at, end == std::string_view::npos ? std::string_view::npos : end - at);
    if (!part.empty()) out.push_back(part);
    if (end == std::string_view::npos) break;
    at = end + 1;
  }
  return out;
}

// M18: keep only what can be a ware or group id, so the wire string never carries separators or spaces.
std::string sanitise_id(std::string_view s) {
  std::string out;
  for (const char c : s) {
    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-') out.push_back(c);
  }
  return out;
}

struct ShieldSlot {
  UniverseId context = 0;
  std::string group;
  std::string ware;  // "" = no mod in this slot
};

// M18: the shield groups of a ship and the mod installed in each. `group` is the portable key; the context id is local.
std::vector<ShieldSlot> read_shield_slots(const GameFns& f, UniverseId ship) {
  std::vector<ShieldSlot> out;
  if (ship == 0 || !f.GetNumShieldGroups || !f.GetShieldGroups || !f.GetInstalledShieldMod) return out;
  const std::uint32_t n = f.GetNumShieldGroups(ship);
  if (n == 0 || n > 256) return out;
  std::vector<ShieldGroupPod> groups(n);
  const std::uint32_t got = f.GetShieldGroups(groups.data(), n, ship);
  for (std::uint32_t i = 0; i < n && i < got; ++i) {
    if (groups[i].group == nullptr || *groups[i].group == '\0') continue;
    ShieldSlot slot;
    slot.context = groups[i].context;
    slot.group = sanitise_id(groups[i].group);
    if (slot.group.empty()) continue;
    ShieldModPod mod{};
    try {
      if (f.GetInstalledShieldMod(ship, slot.context, groups[i].group, &mod) && mod.ware != nullptr) slot.ware = sanitise_id(mod.ware);
    } catch (...) {
      slot.ware.clear();
    }
    out.push_back(std::move(slot));
  }
  return out;
}

}  // namespace

std::string GameApi::equipment_mods(UniverseId ship) const {
  if (ship == 0 || !assert_main_thread("GetInstalledShipMod2")) return {};
  std::string out;
  const auto add = [&out](std::string_view piece) {
    if (!out.empty()) out.push_back(';');
    out += piece;
  };
  if (fns_.GetInstalledShipMod2) {
    ShipModPod mod{};
    try {
      if (fns_.GetInstalledShipMod2(ship, &mod) && mod.ware != nullptr && *mod.ware != '\0') add("s:" + sanitise_id(mod.ware));
    } catch (...) {
      // an unreadable ship mod is "none", never a bogus ware
    }
  }
  if (fns_.GetInstalledEngineMod2) {
    EngineModPod mod{};
    try {
      if (fns_.GetInstalledEngineMod2(ship, &mod) && mod.ware != nullptr && *mod.ware != '\0') add("e:" + sanitise_id(mod.ware));
    } catch (...) {
    }
  }
  for (const ShieldSlot& slot : read_shield_slots(fns_, ship)) {
    if (slot.ware.empty()) continue;
    add("g:" + slot.group + "=" + slot.ware);
  }
  return out;
}

bool GameApi::set_equipment_mods(UniverseId ship, std::string_view spec, int* refused) const noexcept {
  if (refused != nullptr) *refused = 0;
  if (ship == 0 || !assert_main_thread("InstallShipMod")) return false;
  const auto fail = [refused]() {
    if (refused != nullptr) ++*refused;
  };

  std::string want_ship;
  std::string want_engine;
  std::vector<std::pair<std::string, std::string>> want_groups;  // group -> ware ("" removes)
  for (const std::string_view part : split_parts(spec, ';')) {
    if (part.rfind("s:", 0) == 0) {
      want_ship = sanitise_id(part.substr(2));
    } else if (part.rfind("e:", 0) == 0) {
      want_engine = sanitise_id(part.substr(2));
    } else if (part.rfind("g:", 0) == 0) {
      const std::string_view body = part.substr(2);
      const std::size_t eq = body.find('=');
      const std::string group = sanitise_id(body.substr(0, eq));
      const std::string ware = eq == std::string_view::npos ? std::string{} : sanitise_id(body.substr(eq + 1));
      if (!group.empty()) want_groups.emplace_back(group, ware);
    }
    // "w:"/"t:" (weapon/turret mods) have no addressable slot in the SDK and are ignored.
  }

  bool ok = true;
  // Ship mod: install when absent here, dismantle when the spec has none.
  {
    std::string have;
    if (fns_.GetInstalledShipMod2) {
      ShipModPod mod{};
      try {
        if (fns_.GetInstalledShipMod2(ship, &mod) && mod.ware != nullptr) have = sanitise_id(mod.ware);
      } catch (...) {
      }
    }
    if (!want_ship.empty() && want_ship != have) {
      if (!fns_.InstallShipMod) {
        fail();
        ok = false;
      } else {
        try {
          const std::string ware(want_ship);
          if (!fns_.InstallShipMod(ship, ware.c_str())) {
            fail();
            ok = false;
          }
        } catch (...) {
          fail();
          ok = false;
        }
      }
    } else if (want_ship.empty() && !have.empty()) {
      if (!fns_.DismantleShipMod) {
        fail();
        ok = false;
      } else {
        try {
          fns_.DismantleShipMod(ship);
        } catch (...) {
          fail();
          ok = false;
        }
      }
    }
  }
  // Engine mod: same rule, addressed by the ship id.
  {
    std::string have;
    if (fns_.GetInstalledEngineMod2) {
      EngineModPod mod{};
      try {
        if (fns_.GetInstalledEngineMod2(ship, &mod) && mod.ware != nullptr) have = sanitise_id(mod.ware);
      } catch (...) {
      }
    }
    if (!want_engine.empty() && want_engine != have) {
      if (!fns_.InstallEngineMod) {
        fail();
        ok = false;
      } else {
        try {
          const std::string ware(want_engine);
          if (!fns_.InstallEngineMod(ship, ware.c_str())) {
            fail();
            ok = false;
          }
        } catch (...) {
          fail();
          ok = false;
        }
      }
    } else if (want_engine.empty() && !have.empty()) {
      if (!fns_.DismantleEngineMod) {
        fail();
        ok = false;
      } else {
        try {
          fns_.DismantleEngineMod(ship);
        } catch (...) {
          fail();
          ok = false;
        }
      }
    }
  }
  // Shield mods: one per group, matched by the portable group name. A group the spec names but this ship does not have
  // cannot be addressed at all (InstallShieldMod needs the local context id), so it is a logged failure, not a guess.
  for (const ShieldSlot& slot : read_shield_slots(fns_, ship)) {
    std::string want;
    bool named = false;
    for (const auto& [group, ware] : want_groups) {
      if (group == slot.group) {
        want = ware;
        named = true;
        break;
      }
    }
    if (!named && !want_groups.empty()) continue;  // the spec says nothing about this slot; leave it alone
    if (want == slot.ware) continue;
    if (want.empty()) {
      if (!fns_.DismantleShieldMod) {
        fail();
        ok = false;
      } else {
        try {
          fns_.DismantleShieldMod(ship, slot.context, slot.group.c_str());
        } catch (...) {
          fail();
          ok = false;
        }
      }
    } else {
      if (!fns_.InstallShieldMod) {
        fail();
        ok = false;
      } else {
        try {
          if (!fns_.InstallShieldMod(ship, slot.context, slot.group.c_str(), want.c_str())) {
            fail();
            ok = false;
          }
        } catch (...) {
          fail();
          ok = false;
        }
      }
    }
  }
  return ok;
}

}  // namespace x4mp::game
