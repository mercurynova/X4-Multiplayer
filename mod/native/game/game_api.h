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
//
// UNITS (the one place that matters): inside the mod a PosRotPod always carries RADIANS, as GetObjectPositionInSector returns them
// (S13.4). The game's WRITE side takes DEGREES (vendored sdk/x4n_math.h: "GetObjectPositionInSector returns radians,
// SetObjectSectorPos expects degrees"; the sitting-0 probe wrote degrees and its ghosts faced right). GameApi::spawn_object and
// set_object_sector_pos therefore convert radians -> degrees (kRadToDeg) right before the export call; object_position does not.
// A raw PosRotPod handed to a GameFns pointer directly (bypassing GameApi) is in the game's units, not the mod's.
struct PosRotPod {
  float x = 0, y = 0, z = 0;
  float yaw = 0, pitch = 0, roll = 0;
};

// Binary-compatible with the SDK's Rotation {float yaw; float pitch; float roll;} (sdk/x4_game_types.h:1845). GetCameraRotation
// returns it in the same radians the rest of the mod uses; there is no write side, so no degree conversion is involved.
struct RotationPod {
  float yaw = 0, pitch = 0, roll = 0;
};

inline constexpr float kRadToDeg = 180.0f / 3.14159265358979f;  // same factor as x4n::math::RAD_TO_DEG
// The mod's radians pose -> the game's write units (degrees). Positions untouched.
[[nodiscard]] constexpr PosRotPod pos_rot_to_game_write(const PosRotPod& p) noexcept {
  PosRotPod o = p;
  o.yaw *= kRadToDeg;
  o.pitch *= kRadToDeg;
  o.roll *= kRadToDeg;
  return o;
}

// Binary-compatible with the SDK's UIWareInfo {const char* ware; const char* macro; int amount;} (M5-13). Only used as
// the output buffer of GetCargo: the strings belong to the game and stay valid until the next call.
struct WareInfoPod {
  const char* ware = nullptr;
  const char* macro = nullptr;
  std::int32_t amount = 0;
};

// Binary-compatible with the SDK's FactionDetails {const char* factionID; const char* factionName; const char* factionIcon;}
// (M5-20). The strings belong to the game; copy the id before the next call.
struct OwnerFactionPod {
  const char* faction_id = nullptr;
  const char* faction_name = nullptr;
  const char* faction_icon = nullptr;
};

// M16 (logo): binary-compatible with the SDK's UILogo {const char* file; const char* icon; bool ispersonal;}
// (sdk/x4_game_types.h). The strings belong to the game and are copied at once.
struct LogoPod {
  const char* file = nullptr;
  const char* icon = nullptr;
  bool personal = false;
};

// Binary-compatible with the SDK's UIPaintMod {const char* Name; const char* RawName; const char* Ware; uint32 Quality; uint32 Amount;}
// (sdk/x4_game_types.h). Only the output buffer of GetInstalledPaintMod: the strings belong to the game and stay valid until the next call.
struct PaintModPod {
  const char* name = nullptr;
  const char* raw_name = nullptr;
  const char* ware = nullptr;
  std::uint32_t quality = 0;
  std::uint32_t amount = 0;
};

// M18 (equipment mods): binary-compatible with the SDK's UIShipMod2 / UIEngineMod2 / UIShieldMod
// (sdk/x4_game_types.h). The game fills the WHOLE out-param, so these pods must keep every field in order even though
// only `ware` is read here: a short pod would let the export write past the end of it.
struct ShipModPod {
  const char* name = nullptr;
  const char* raw_name = nullptr;
  const char* ware = nullptr;
  std::uint32_t quality = 0;
  const char* property_type = nullptr;
  float mass_factor = 0.f;
  float drag_factor = 0.f;
  float max_hull_factor = 0.f;
  float radar_range_factor = 0.f;
  std::uint32_t added_unit_capacity = 0;
  std::uint32_t added_missile_capacity = 0;
  std::uint32_t added_countermeasure_capacity = 0;
  std::uint32_t added_deployable_capacity = 0;
  float radar_cloak_factor = 0.f;
  float region_damage_protection = 0.f;
  float hide_cargo_chance = 0.f;
};

struct EngineModPod {
  const char* name = nullptr;
  const char* raw_name = nullptr;
  const char* ware = nullptr;
  std::uint32_t quality = 0;
  const char* property_type = nullptr;
  float forward_thrust_factor = 0.f;
  float strafe_acc_factor = 0.f;
  float strafe_thrust_factor = 0.f;
  float rotation_thrust_factor = 0.f;
  float boost_acc_factor = 0.f;
  float boost_thrust_factor = 0.f;
  float boost_duration_factor = 0.f;
  float boost_attack_time_factor = 0.f;
  float boost_release_time_factor = 0.f;
  float boost_charge_time_factor = 0.f;
  float boost_recharge_time_factor = 0.f;
  float travel_thrust_factor = 0.f;
  float travel_start_thrust_factor = 0.f;
  float travel_attack_time_factor = 0.f;
  float travel_release_time_factor = 0.f;
  float travel_charge_time_factor = 0.f;
};

struct ShieldModPod {
  const char* name = nullptr;
  const char* raw_name = nullptr;
  const char* ware = nullptr;
  std::uint32_t quality = 0;
  const char* property_type = nullptr;
  float capacity_factor = 0.f;
  float recharge_delay_factor = 0.f;
  float recharge_rate_factor = 0.f;
};

// Binary-compatible with the SDK's ShieldGroup {UniverseID context; const char* group; UniverseID component;}: the
// `group` string is the only part that is portable between two games (`context`/`component` are local ids).
struct ShieldGroupPod {
  UniverseId context = 0;
  const char* group = nullptr;
  UniverseId component = 0;
};

// One cargo line copied out of the game, so callers can hold it after the export returned.
struct CargoPod {
  std::string ware;  // the ware id the game uses in cargo commands ("energycells"), NOT a macro ref
  std::int32_t amount = 0;
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
  // M4 guard: the zone the player stands in (walks the player chain for class 108). Used to tell a trustworthy engine read-back
  // (the player's own zone) from one taken in a low-attention zone, where the ghost's stored position is not what we wrote.
  UniverseId (*GetPlayerZoneID)() = nullptr;
  UniverseId (*GetContextByClass)(UniverseId componentid, const char* classname, bool includeself) = nullptr;
  bool (*IsValidComponent)(UniverseId componentid) = nullptr;
  // On-foot facing (M3): the direction the player looks along, which is the body heading of the avatar other players see.
  RotationPod (*GetCameraRotation)() = nullptr;
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
  // M3b (on-foot): a position relative to a container (a station interior, a ship). Same units as GetObjectPositionInSector.
  PosRotPod (*GetPositionalOffset)(UniverseId positionalid, UniverseId spaceid) = nullptr;
  // M3b (on-foot): write that offset back. Unlike the M3 setters this one takes RADIANS for yaw/pitch/roll (S10.4/S10.5:
  // the spike wrote GetPositionalOffset's own radians and the avatar faced the right way), so do NOT pass it through
  // pos_rot_to_game_write.
  void (*SetPositionalOffset)(UniverseId positionalid, PosRotPod offset) = nullptr;
  // M5 (trade): read a container's cargo. `tags` must be "" (never nullptr). GetNumCargo answers 0 both for an empty
  // container and for a failed read, which is exactly why M5-13 accepts only non-empty reads.
  std::uint32_t (*GetNumCargo)(UniverseId containerid, const char* tags) = nullptr;
  std::uint32_t (*GetCargo)(WareInfoPod* result, std::uint32_t resultlen, UniverseId containerid, const char* tags) = nullptr;
  // M5 (builds): spawn a STATION with a construction plan id. Unlike SpawnObjectAtPos2 this one takes the plan, which is what
  // makes the station come up complete (M5-19). An empty plan id ("") is the macro-plus-modules path the plan settled on (M5 Q7).
  UniverseId (*SpawnStationAtPos)(const char* macroname, UniverseId sectorid, PosRotPod offset, const char* constructionplanid,
                                  const char* ownerid) = nullptr;
  // M5-20: the owner faction of a component as a string ("player", "xenon", ...). The typed MD hook EntityChangedOwner
  // gives a faction id the mod cannot compare against anything, so the capture detector asks the game for the name.
  OwnerFactionPod (*GetOwnerDetails)(UniverseId componentid) = nullptr;
  // M5-23: the write half of the wallet. The table has no getter at all (the balance arrives from Lua, M5-22), but it does have
  // this one, and it is the only way to make the local money equal what the ledger says: AddPlayerMoney(delta) with the delta
  // design 12.2 computes (serverBalance + pending - local).
  void (*AddPlayerMoney)(std::int64_t money) = nullptr;
  // M12 (paint): read the paint mod installed on a ship (`objectid`) and install a paint (ware id) on one. GetInstalledPaintMod answers
  // false when the object has no paint; InstallPaintMod's useinventory is false so a host-side apply never consumes inventory.
  bool (*GetInstalledPaintMod)(UniverseId objectid, PaintModPod* paintmod) = nullptr;
  bool (*InstallPaintMod)(UniverseId objectid, const char* wareid, bool useinventory) = nullptr;
  // M16 (logo): the fleet logo of a ship and its setter. GetCurrentFleetLogo returns the caller's logo for an object the player
  // controls (an empty `file` means none); SetFleetLogo is a void write, so a wrong spec can only be seen by reading it back.
  LogoPod (*GetCurrentFleetLogo)(UniverseId controllableid) = nullptr;
  void (*SetFleetLogo)(UniverseId controllableid, LogoPod logo) = nullptr;
  // M18 (equipment mods): the ship mod and the engine mod are addressed by the ship id; shield mods by
  // (defensibleid, contextid, group), where the pair comes from GetShieldGroups. The readers fill a whole pod.
  bool (*GetInstalledShipMod2)(UniverseId shipid, ShipModPod* shipmod) = nullptr;
  bool (*InstallShipMod)(UniverseId shipid, const char* wareid) = nullptr;
  void (*DismantleShipMod)(UniverseId shipid) = nullptr;
  bool (*GetInstalledEngineMod2)(UniverseId objectid, EngineModPod* enginemod) = nullptr;
  bool (*InstallEngineMod)(UniverseId objectid, const char* wareid) = nullptr;
  void (*DismantleEngineMod)(UniverseId objectid) = nullptr;
  std::uint32_t (*GetNumShieldGroups)(UniverseId defensibleid) = nullptr;
  std::uint32_t (*GetShieldGroups)(ShieldGroupPod* result, std::uint32_t resultlen, UniverseId defensibleid) = nullptr;
  bool (*GetInstalledShieldMod)(UniverseId defensibleid, UniverseId contextid, const char* group, ShieldModPod* shieldmod) = nullptr;
  bool (*InstallShieldMod)(UniverseId defensibleid, UniverseId contextid, const char* group, const char* wareid) = nullptr;
  void (*DismantleShieldMod)(UniverseId defensibleid, UniverseId contextid, const char* group) = nullptr;
};

// Number of names missing_exports() checks, i.e. the exports GameFns can bind (a test asserts it): the self-test reports resolved/total
// from it, so it has to be the real count or the report lies by the difference.
inline constexpr std::size_t kGameFnCount = 52;

// Upper bound on the cargo lines one read copies (far above any real hold; keeps a bad export from allocating forever).
inline constexpr std::uint32_t kMaxCargoLines = 4096;

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
  // M4 guard: the zone the player is in (class 108 in the player chain); 0 when the export is missing or the read fails. The zone is
  // what SetObjectSectorPos re-parents an object to when its position crosses a zone border, so it names the frame the read-back is in.
  [[nodiscard]] UniverseId player_zone() const noexcept;
  [[nodiscard]] UniverseId context_by_class(UniverseId id, const char* classname, bool include_self) const noexcept;
  // Radians (the same convention as PosRotPod). nullopt when the export is missing; used for the on-foot body heading.
  [[nodiscard]] std::optional<RotationPod> camera_rotation() const noexcept;
  // true when the export is missing (cannot filter): callers treat "unknown" as valid.
  [[nodiscard]] bool is_valid_component(UniverseId id) const noexcept;

  // ---- M3 exports (M3-04). All null-safe: a missing export or a failed main-thread check gives the stated default. ----
  // 0 = missing / refused / the game returned 0. The owner is a faction id ("x4mp_team_1", "player").
  [[nodiscard]] UniverseId spawn_object(const char* macro, UniverseId sector, const PosRotPod& pos, const char* owner) const noexcept;
  // M5-19: spawn a station at a sector-local pose. 0 = missing / refused / the game returned 0. `plan` "" = macro plus modules.
  [[nodiscard]] UniverseId spawn_station(const char* macro, UniverseId sector, const PosRotPod& pos, const char* plan,
                                         const char* owner) const noexcept;
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
  // M3b: `id`'s position inside container `space` (station interior, ship). nullopt: export missing / id 0 / main thread.
  [[nodiscard]] std::optional<PosRotPod> positional_offset(UniverseId id, UniverseId space) const noexcept;
  // M3b: write `id`'s offset inside `space`. `pos` is in RADIANS (see GameFns::SetPositionalOffset). True when the call was made.
  bool set_positional_offset(UniverseId id, const PosRotPod& pos) const noexcept;

  // ---- M5 (trade) ----
  // The container's cargo, copied out of the game. Empty when the export is missing, the id is 0, the main-thread check
  // fails, or the game reports no lines at all (an empty read is not evidence of an empty hold: M5-13).
  [[nodiscard]] std::vector<CargoPod> cargo(UniverseId container) const;

  // ---- M5 (capture) ----
  // The owner faction of a component as the game names it ("player", "xenon", ...); empty when the export is missing, the
  // id is 0, the main-thread check fails or the game returns nothing. M5-20 compares this with "player".
  [[nodiscard]] std::string owner_faction(UniverseId component) const noexcept;

  // ---- M5 (wallet) ----
  // Add `delta` CREDITS to the local player (negative spends). True when the call was made; false when the export is missing,
  // `delta` is 0 or the main-thread check fails. The change comes back as an ordinary reading on the next GetPlayerMoney poll,
  // which is what note_apply()/observe() recognise as this node's own echo (M5-23).
  //
  // The export itself takes CENTS while GetPlayerMoney returns credits (decisions.md:519, the money-units decision): the
  // conversion happens here and nowhere else. Session 8 passed credits straight through, so a +100000 credit write moved the
  // player by 1000 and the ledger could never converge on the game.
  bool add_player_money(std::int64_t delta) const noexcept;

  // ---- M12 (paint) ----
  // The ware id of the paint mod installed on `id` (UIPaintMod.Ware). Empty when the export is missing, the id is 0, the
  // main-thread check fails, or the game reports no paint. Copied at once: the game owns the buffer.
  [[nodiscard]] std::string installed_paint(UniverseId id) const;
  // Install the paint `ware` on `id` (useinventory=false). false when the export is missing, the id or ware is empty, or the
  // main-thread check fails. An empty ware is "no paint" and is never sent to the game.
  bool install_paint(UniverseId id, std::string_view ware) const noexcept;

  // ---- M16 (logo) ----
  // The fleet logo of `id` as "<file>|<icon>" (the wire form: one opaque string, like paint's ware id). Empty when the export is
  // missing, the id is 0, the main-thread check fails, or the game reports no logo. A logo marked personal is a LOCAL file, so
  // the string may name a file that does not exist on another PC; the receiving node logs the failed apply, never crashes.
  [[nodiscard]] std::string fleet_logo(UniverseId id) const;
  // Set the fleet logo of `id` from that same "<file>|<icon>" form (a spec with no '|' names a file with no icon). false when the
  // export is missing, the id or file is empty, or the main-thread check fails. The caller treats false as "this PC cannot show it"
  // and keeps going.
  bool set_fleet_logo(UniverseId id, std::string_view spec) const noexcept;

  // ---- M18 (equipment mods) ----
  // The installed equipment mods of `ship` in the one wire form
  //   "s:<ship mod ware>;e:<engine mod ware>;g:<group>=<ware>,<group>=<ware>"
  // (empty parts omitted, "" = no mods at all). Only *mods* live here: real weapons/shields/engines come from the spawn
  // macro/loadout and are therefore already identical on both nodes, and no export can change them (see M18 notes).
  [[nodiscard]] std::string equipment_mods(UniverseId ship) const;
  // Apply such a spec to `ship`, changing only what differs from what is installed now: a mod is installed with
  // Install*Mod and removed with Dismantle*Mod, and every failure is logged and skipped rather than fatal (`refused`
  // comes back non-zero when at least one call failed, so the caller can keep polling instead of trusting the write).
  // Weapon and grouped-weapon mods have no addressable slot in the SDK, so a `w:` part is not produced and not applied.
  bool set_equipment_mods(UniverseId ship, std::string_view spec, int* refused = nullptr) const noexcept;

  // The wire and the ledger count credits, the game's write export counts cents.
  static constexpr std::int64_t kCentsPerCredit = 100;

 private:
  GameFns fns_{};
  GameInfo info_{};
};

}  // namespace x4mp::game
