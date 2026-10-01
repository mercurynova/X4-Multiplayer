# X4 Platform & API Notes (for X4MP)

> See docs/architecture.md — it is authoritative where this doc differs.

Status: research notes, 2026-10-01. Target: X4: Foundations **9.00** (Windows x64, Steam).
Anything not confirmed from a primary source is marked **[UNVERIFIED]**.

Primary sources used (in order of trust):

| Tag | Source |
|---|---|
| `[exe]` | Export table of the installed `X4.exe` (`C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations\X4.exe`), parsed with a small Python PE reader: **2378 named exports**. |
| `[cdef]` | `ffi.cdef` blocks in vanilla UI Lua, unpacked to `C:\Personal\X4 Mult\x4-unpacked\ui\` (**2066** distinct C function signatures; all but one, `GetSlotComponent`, are in the export table). |
| `[xsd]` | `x4-unpacked\libraries\common.xsd`, `md.xsd`, `scriptproperties.xml` |
| `[md]` | `x4-unpacked\md\*.xml` (223 vanilla MD scripts) |
| `[x4n]` | eg3r/X4Native upstream: https://github.com/eg3r/X4Native |
| `[ref]` | The reference repo (`reference/`), its docs and shipped `version_db`. Lessons only; no code copied. |

---

## 1. X4Native (the native loader we build on)

### 1.1 Basics
- Repo: https://github.com/eg3r/X4Native. **License: MIT** (OK to depend on and redistribute
  with attribution). The reference's bundled copy says `author="eg3r"`, `version="900"`.
- Supports "X4: Foundations (v9.x, Windows x64)". No Linux support upstream (the
  reference's Linux build was a private fork). `[x4n]`
- Releases are tagged by game build: `v9.0.0-607242`, `v9.0.0-611726` (latest seen,
  released 2026-06-10). https://github.com/eg3r/X4Native/releases
- Docs: `docs/EXTENSION_GUIDE.md`, `CONTRIBUTING.md`.
  https://github.com/eg3r/X4Native/blob/main/docs/EXTENSION_GUIDE.md
- Requires **Protected UI Mode OFF** (Settings > Extensions). The Lua bootstrap checks
  `GetUISafeModeOption() ~= false` and that `package.config` uses `\` as the separator
  (Windows), then calls `package.loadlib(".\\extensions\\x4native\\native\\x4native_64.dll",
  "luaopen_x4native")`. `[ref: extensions/x4native/ui/x4native.lua]`

### 1.2 Architecture (two DLLs plus a Lua and MD shim)
- `x4native_64.dll` is a **thin proxy**, loaded by LuaJIT via `package.loadlib`. It stays
  loaded, so its file is locked.
- `x4native_core.dll` is the **hot-reloadable core** (logger, events, hooks, extension
  manager). The proxy copies it to `x4native_core_live.dll` and loads the copy, so the
  original file is never locked. `[x4n CONTRIBUTING.md]`
- Flow: "MD XML cues → Lua events → proxy → core → extensions". `[x4n]`
- Dependencies: nlohmann/json 3.11.3, **MinHook 1.3.3** (hooking), LuaJIT 2.1.0-beta3
  (`lua51_64.dll`, shipped with the game). `[x4n CONTRIBUTING.md]`
- Runtime needs the **VC++ 2015-2022 x64 redistributable** (MSVCP140/VCRUNTIME140).
  `[ref STATE.md]`
- Logs: `%USERPROFILE%\Documents\Egosoft\X4\<id>\x4native\x4native.log` plus a per-extension
  `<ext>\<ext>.log`. `[ref STATE.md]`

**Reference pitfall (Linux fork only):** several proxy instances copying to the same
`*_live` file raced and crashed the game when `NewMultiplayerGame` or a resolution change
spawned a worker that re-entered `load_core()`. We should test hot reload under load, and
must not call `NewMultiplayerGame` anyway (see section 5). `[ref STATE.md §2]`

### 1.3 Lifecycle events (built-in)
They are driven by `extension/md/x4native_main.xml` (MD → `raise_lua_event`) and
`ui/x4native.lua` (`RegisterEvent` → `api.raise_event`):

| C++ event | Source | Meaning |
|---|---|---|
| `on_game_loaded` | MD `<event_game_loaded/>` → `x4native.game_loaded` (also polled through `C.IsPlayerValid()` after `/reloadui`) | Save loaded, IDs valid, safe to call game functions; gamestart cues **not** yet run |
| `on_game_started` | MD `<event_game_started/>` | **New game only**, not save loads |
| `on_universe_ready` | MD `<event_universe_generated/>` | Fires for new game **and** save load once all stations are built. **Use this as the "world ready" gate.** |
| `on_game_save` | MD `<event_game_saved/>` | After a save |
| `on_ui_reload` | Lua file re-executes after `/reloadui` | DLL survives; new `lua_State*` |
| `on_frame_update` | Lua `SetScript("onUpdate", …)` | Every UI frame (UI thread) |
| `on_native_frame_update` | Native hook on internal `X4_FrameTick` | Typed `X4NativeFrameUpdate*`; needs the RVA in `version_db` |

Sources: `[ref x4native_main.xml, x4native.lua]`, `[x4n EXTENSION_GUIDE]`, `[x4n sdk/x4n_events.h]`.

Reference lesson: spawning or moving objects right after `on_game_loaded` raced the game's
"Movement worker" and crashed. **Wait for `on_universe_ready`** (with a timeout fallback)
before spawning anything. `[ref STATE.md ~L350-400]`

### 1.4 Extension layout
```
extensions/x4mp/
  content.xml        <dependency id="x4native" optional="false"/>  (id = routing key for logs, stash, events)
  x4native.json      { "library": "native/x4mp.dll", "priority": 50, "min_api_version": 1,
                       "autoreload": false, "logfile": "...", "settings": [...] }
  native/x4mp.dll
  ui.xml + ui/*.lua  (optional; our Lua UI)
  md/*.xml           (optional; our MD cues)
```
`x4native.json` fields: `library` (required), `priority` (lower loads earlier, default 0),
`min_api_version`, `autoreload` (dev only; polls the DLL mtime about every 2 s),
`logfile`, `settings` (shown under Settings → Extensions). `[x4n EXTENSION_GUIDE]`

### 1.5 C ABI (`sdk/x4native_extension.h`)
`#define X4NATIVE_API_VERSION 1`. Required DLL exports: `x4native_api_version()`,
`x4native_init(X4NativeAPI* api)`, `x4native_shutdown()`. The C++ wrapper macros are
`X4N_EXTENSION { ... }` and `X4N_SHUTDOWN { ... }`.

`X4NativeAPI` members (function pointers):
```
subscribe(event_name, X4NativeEventCallback, userdata, _api_ptr) -> int
unsubscribe(id)                    raise_event(name, void* data)
raise_lua_event(name, const char* param) -> int      register_lua_bridge(lua_event, cpp_event)
log(int level, const char* msg)    // NOT variadic: format the string first
get_game_version()  get_x4native_version()
get_game_function(const char* name) -> void*         // any X4.exe export by name
hook_before / hook_after(function_name, X4HookCallback, userdata, _api_ptr)   unhook(id)
resolve_internal(const char* name) -> void*          // non-exported funcs via version_db RVAs
md_subscribe_before / md_subscribe_after(uint32_t type_id, cb, userdata, _api_ptr)
stash_set/get/remove/clear(ns, key, ...)             // survives /reloadui + hot reload, not game exit
get/set_setting_bool|number|string(...)
get_lua_property(getter_fn, X4nLuaKey, field, X4nLuaValueType, out)   // "UI thread only"
get_lua_property_str(...)
```
Source: https://raw.githubusercontent.com/eg3r/X4Native/main/sdk/x4native_extension.h

### 1.6 C++ SDK conveniences (`sdk/`)
- `x4native.h` (umbrella), `x4n_core.h` (`x4n::game()` returns `X4GameFunctions*`, a typed
  table of all exports), `x4n_events.h`, `x4n_log.h`, `x4n_hooks.h`, `x4n_stash.h`,
  `x4n_settings.h`, `x4_md_events.h`, plus `x4n_ship.h`, `x4n_sector.h`, `x4n_faction.h`,
  `x4n_ware.h`, `x4n_entity.h`, `x4n_galaxy.h`, `x4n_visibility.h`, `x4n_memory.h`,
  `x4n_math.h`. Generated tables: `x4_game_func_list.inc` (X-macro
  `X4_FUNC(ret, name, (params))`), `x4_game_func_table.h`, `x4_game_types.h`,
  `x4_internal_func_list.inc`, `x4_game_offsets.h`, `x4_game_class_ids.inc`.
  https://github.com/eg3r/X4Native/tree/main/sdk
- Events: `x4n::on(name, cb)` (overloads for `void()`, `void(void*)`, `void(const char*)`,
  `void(const X4NativeFrameUpdate*)`), `x4n::off(id)`, `x4n::raise(name, data)`,
  `x4n::raise_lua(name, param)` ("Must be called from UI thread"),
  `x4n::bridge_lua_event(lua_event, cpp_event)`.
- MD: `x4n::md::on_<event>_before/after([](const x4n::md::<Event>Data& e){...})`, e.g.
  `on_killed_after`. The SDK advertises typed hooks for "all 551 MD event types".
- Hooks: `x4n::hook::before<&X4GameFunctions::Fn>(cb)` (a `HookControl&` can set
  `skip_original`), `after`, `remove`.
- Logging: `x4n::log::info/warn/error("fmt {}", v)`, `x4n::log::to_file("x.log")`.

### 1.7 Threading model
- Upstream guide: "All code runs on the UI thread". Extensions are auto-cleaned on unload
  (hooks and subscriptions removed). `[x4n EXTENSION_GUIDE]`
- **Conflict:** the reference observed MD event callbacks firing **on game worker threads**,
  and only did pure memory reads there. `[ref from_LLM_to_LLM.md]` The upstream header does
  not say which thread runs MD callbacks. **Design rule:** treat MD callbacks and hooks as
  possibly off-thread. Copy POD data into a lock-free queue and run every game API or Lua
  call from `on_frame_update` / `on_native_frame_update`.
- Our network I/O must run on our own thread, with non-blocking sends. A blocking send
  dropped the reference to 5 FPS. eg3r's own (unreleased?) X4Online uses the same pattern:
  a background network thread, game calls on the UI thread, lock-free queues.

### 1.8 version_db and internal functions
- `native/version_db/` holds three files: `func_history.json` (export add/remove history,
  2068 entries, builds `900` … `900-611726`), `type_changes.json` (283 struct fingerprints),
  and `internal_functions.json` (per-build **RVAs** plus `_find_hints`: unique strings, byte
  signatures, structural checks). The core loads it from
  `<x4native>/native/version_db/internal_functions.json` at startup.
  `[ref x4mp_windows/.../version_db]`
- Internal functions listed (22): `X4_FrameTick`, `EventQueue_InsertOrDispatch`
  (**required for every MD event hook**), `RadarVisibilityChanged_BuildEvent`,
  `IsRadarVisible_ReadByte`, `SetObjectRadarVisible_Action`, `CreateOrderInternal`,
  `SetOrderParamInternal`, `SetCommander`, `SetAssignment`, `GetFactionBuildMethod`,
  `FactionRelation_LookupReasonID`, `FactionRelation_GetFloat`, `ComponentRegistry_Find`,
  `MacroRegistry_Lookup`, `Component_GetCombinedSeed`, `CreateDynamicInterior`,
  `MD_CreateDynamicInterior_Handler`, `ConstructionDB_CreatePlanDirect`,
  `ConstructionDB_AddPlan`, `PlanEntry_Construct`, `GameAlloc`, `Screenshot_ArmSingleShot`.
- **Packaging trap:** if `version_db/` is not shipped, the log shows
  `MD event hook: EventQueue_InsertOrDispatch not resolved` and **all MD hooks, the native
  frame tick and radar hooks are silently disabled**. With it: "Resolved 2065/2065 game
  functions … Resolved 22 internal function(s) … MD event hook installed (600 type slots)".
  `[ref STATE.md 2026-09-18]`
- **Fragility:** RVAs are per game build. Every X4 patch can break hooks until upstream
  publishes new RVAs. Mitigations: pin the supported build, check `get_game_version()` at
  init, degrade gracefully (fall back to vanilla MD cues + `raise_lua_event`), and show an
  error in the join dialog.

### 1.9 Build requirements (Windows)
- Visual Studio 2022 Build Tools (MSVC), **C++23**, CMake ≥ 3.20, x64.
- X4Native itself: `cmake --preset default`, `cmake --build build --config Debug`,
  `cmake --build build --target deploy` (the game dir is found through the Steam registry,
  or set `-DX4_GAME_DIR=`).
- Minimal extension CMake: `add_library(x SHARED x.cpp)`, include `path/to/sdk`,
  `PREFIX ""`. Style rules: no exceptions across the DLL boundary (return codes + SEH), C
  ABI at boundaries, no STL in exported signatures. `[x4n CONTRIBUTING.md]`
- Implication for us: vendor the X4Native **SDK headers** (MIT) at a pinned commit as a git
  submodule. Do not build X4Native ourselves; ship the official release, or document it as
  a prerequisite.

### 1.10 Hot reload and UI reload
- `"autoreload": true` reloads only our DLL when its mtime changes (dev only). Statics are
  lost, so use `stash` for anything that must survive. `[x4n]`
- `/reloadui` (also triggered by some menu transitions) **shuts down and re-inits all
  extensions** ("Re-discovery: shutting down N existing extension(s)"). The reference's
  menu-initiated host lost its listener this way. **Network session state must live in
  `stash` (or the DLL must survive teardown)**, and the connection must be re-established
  or kept on re-init. Note that `"autoreload": false` does NOT prevent this.
  `[ref STATE.md Defect 2]`

---

## 2. Exported game functions relevant to multiplayer

All signatures below are verbatim from `[cdef]` and present in `[exe]`, unless noted.
`UniverseID` = `uint64_t`. Key structs:
`UIPosRot {float x,y,z,yaw,pitch,roll;}` (sector-local metres and angles),
`UIWareInfo {const char* ware; const char* macro; int amount;}`,
`FactionDetails {const char* factionID; factionName; factionIcon;}`,
`SpeedInfo {float speed, boostspeed, travelspeed;}` (these are capabilities, not current
speed). Paths are relative to `x4-unpacked\ui\addons\`.

### 2.1 Identity / player (read-only)
| Function | Notes / file |
|---|---|
| `UniverseID GetPlayerID(void)` | player entity (ego_detailmonitor/menu_docked.lua) |
| `UniverseID GetPlayerObjectID(void)` | the object the player is in |
| `UniverseID GetPlayerOccupiedShipID(void)` / `GetPlayerControlledShipID(void)` | ship the player sits in / pilots (menu_map.lua) |
| `UniverseID GetPlayerZoneID(void)`, `GetPlayerContainerID`, `GetPlayerGalaxyID` | context |
| `const char* GetPlayerName(void)`, `GetPlayerFactionName(bool userawname)` | (menu_playerinfo.lua) |
| `bool IsPlayerValid(void)` | true once a save is loaded (used by the x4native bootstrap) |
| `const char* GetObjectIDCode(UniverseID)` | "ABC-123" code, human-friendly but not unique |
| `uint64_t ConvertStringTo64Bit(const char*)` | Lua component string → `UniverseID` (ego_chatwindow) |

**IDs are per-process.** A `UniverseID` is not stable across machines, even with the same
save (spawned objects get fresh IDs). The protocol must use **authority-assigned network
IDs** and keep a per-node map `netId ↔ localUniverseID`.

### 2.2 Spatial: position / rotation / velocity
| Function | R/W | Notes |
|---|---|---|
| `UIPosRot GetObjectPositionInSector(UniverseID)` | R | helper.lua. Logs "Failed to retrieve sector" for docked or dying objects, so check the sector first `[ref]` |
| `void SetObjectSectorPos(UniverseID obj, UniverseID sector, UIPosRot)` | W | map editor (menu_mapeditor.lua). **Teleport**: no velocity, no interpolation |
| `UIPosRot GetPositionalOffset(UniverseID, UniverseID spaceid)` / `void SetPositionalOffset(UniverseID, UIPosRot)` | R/W | relative to parent space |
| `PosRot GetComponentOffset(UniverseID)` | R | targetsystem.lua |
| `UniverseID GetContextByClass(UniverseID, const char* classname, bool includeself)` | R | `"sector"`, `"cluster"`, `"zone"`, `"container"` |
| `UniverseID GetParentComponent(UniverseID)` | R | |
| `void MovePlayerToSectorPos(UniverseID sector, UIPosRot)` | W | moves the **player** |
| `bool TeleportPlayerTo(UniverseID controllable, bool allowcontrolling, bool instant, bool force)` | W | `CanTeleportPlayerTo` checks first |
| `float GetObjectBrakeDistance(UniverseID, float)`, `SpeedInfo GetDefensibleSpeeds(UniverseID)` | R | capabilities only |

**Gap: no exported velocity getter or setter.** MD/Lua properties exist:
`object.speed` ("Speed relative to the cluster (in m/s)") and `object.velocity` ("Linear
velocity relative to the cluster, but relative to the component's rotation")
`[xsd scriptproperties.xml]`. Lua `GetComponentData(id, "...")` may expose some of these
**[UNVERIFIED which keys]**. Practical plan: **derive velocity on the authority from
position deltas between ticks**, send pos+rot+vel, and extrapolate and interpolate on
clients with `SetObjectSectorPos` each frame. Remote ships cannot be given physical
momentum; they are kinematic puppets.

### 2.3 Universe enumeration (read-only)
| Function | Notes |
|---|---|
| `uint32_t GetNumSectors(UniverseID clusterid)` | per cluster (map editor). **No exported `GetClusters` / `GetSectors` list.** |
| `uint32_t GetNumSectorsByOwner(const char* factionid)` / `GetSectorsByOwner(UniverseID*, uint32_t, const char*)` | iterate all factions to cover owned sectors; unowned sectors need another route |
| `uint32_t GetNumAllFactions(bool includehidden)` / `GetAllFactions(const char**, uint32_t, bool)` | |
| `uint32_t GetNumAllFactionShips(const char*)` / `GetAllFactionShips(UniverseID*, uint32_t, const char*)` | **size buffers from GetNum**. The reference first capped at 2048 and missed ships. About 84k ships in a mature save |
| `GetNumAllFactionStations` / `GetAllFactionStations` | same pattern |
| `GetNumFixedStations(spaceid)` / `GetFixedStations(...)`, `GetNumDockedShips` / `GetDockedShips(bay_or_container, factionid)` | spatial queries |
| `uint64_t GetSectorPopulation(UniverseID)`, `bool IsContestedSector(UniverseID)`, `UniverseID GetSectorControlStation(UniverseID)` | |
| `const char* GetComponentClass(UniverseID)`, `bool IsComponentClass(UniverseID, const char*)`, `bool IsValidComponent(UniverseID)`, `bool IsComponentWrecked(UniverseID)`, `bool IsComponentOperational(UniverseID)` | validity checks; always check before writes |
| Lua `GetComponentData(id, "macro", "owner", "sector", "hullpercent", "idcode", ...)` | Lua global (not FFI), used about 888 times in vanilla UI. Many properties per call |

Pitfall: `GetAllFactionShips` with hidden factions returns the **same ship under several
factions**, so dedupe by ID. `[ref]` Full-galaxy enumeration (~84k ships) is too slow for
every frame. Spread it over frames and filter by sector of interest.

Complete sector and cluster enumeration: **[UNVERIFIED]** whether any export lists all
clusters. Options: MD (`find_sector`/`find_cluster` with `multiple="true"`, sent to Lua
through `raise_lua_event`), the sector macros from `index/macros.xml`, or the internal
`ComponentRegistry_Find`.

### 2.4 Spawning / removing (write)
| Function | Notes |
|---|---|
| `UniverseID SpawnObjectAtPos2(const char* macro, UniverseID sector, UIPosRot, const char* ownerid)` | **Main ghost-spawn primitive.** Returns 0 on failure. `ownerid` must be an **existing faction id** (`nullptr` or invented factions fail silently) `[ref]`. Spawns with the default loadout and AI **[UNVERIFIED: crew/pilot state]** |
| `void SpawnObjectAtPos(macro, sector, UIPosRot)` | no return value; avoid |
| `UniverseID SpawnStationAtPos(macro, sector, UIPosRot, const char* constructionplanid, const char* ownerid)` | stations |
| `void RemoveComponent(UniverseID)` | instant removal, no explosion. **Never on the player's own ship** (Game Over, `killmethod=removed`) `[ref]` |
| `void SelfDestructComponent(UniverseID)` | destruction with effects (good for replaying kills) |
| `void SetObjectForcedRadarVisible(UniverseID, bool)` | keep ghosts visible on radar |
| `void SetObjectCoverFaction(UniverseID, const char*)` | |

MD alternatives with more control: `<create_ship>` (dock, state, commandeerable,
`missioncue`…), `<create_station>`, `<destroy_object>`, `<warp object= sector=/zone=>`,
`<set_object_active>`, `<set_object_hull>`, `<set_object_shield>` `[xsd common.xsd]`.

**Ghost hygiene:** ghosts are real game objects. They show up in AI targeting, trade and
saves. The reference saw autosaves bake ghosts into the save. Needs: a dedicated ghost
faction strategy (see 2.5), disable the AI (empty orders, `set_object_active`?
**[UNVERIFIED]** whether physics or AI can be frozen), and remove all ghosts in an
`on_game_save` pre-hook. `event_game_saved` fires **after** saving, so hook
`SaveGame`/autosave or call `TriggerAutosave` ourselves **[UNVERIFIED best hook point]**.

### 2.5 Ownership / factions
| Function | R/W |
|---|---|
| `FactionDetails GetOwnerDetails(UniverseID)` / `FactionDetails2 GetOwnerDetails2(UniverseID)` | R |
| `void SetComponentOwner(UniverseID, const char* factionid)` | W, works on any component (map editor) |
| `void MakePlayerOwnerOf(UniverseID)` | W (interact menu "claim") |
| `bool CanClaimOwnership(UniverseID)` | R |
| `IsFactionEnemyToFaction`, `IsFactionHostileToFaction`, `IsFactionAllyToFaction`, `GetFactionRelationStatus2`, `GetUIRelationName` | R |
| `void SetFactionRelationToPlayerFaction(const char* factionid, const char* reasonid, float boostvalue)`, `SetRelationBoostToFaction(...)` | W |

**Design issue: there is only one "player" faction per game.** Remote players' ships must
be represented under some other real faction. The reference borrowed real NPC factions
(e.g. `antigone`), which looked like hostile or neutral NPCs. Better: ship **custom
factions in our extension's `libraries/factions.xml` diff** (the reference shipped a
`libraries/factions.xml`; contents not inspected). Decided: `x4mp_team_1..8`, one per team,
symmetric on every node, relations from the session matrix (ADR-014/016). Faction defs must exist when the save loads; adding factions to an existing
save is **[UNVERIFIED]**.

### 2.6 Cargo / trade
| Function | R/W | Notes |
|---|---|---|
| `uint32_t GetNumCargo(UniverseID container, const char* tags)` / `GetCargo(UIWareInfo*, uint32_t, UniverseID, const char* tags)` | R | `tags=""` means all wares; **not NULL** `[ref]` |
| `void AddTradeWare(UniverseID, const char* wareid)` | W | **Not a cargo function.** Vanilla uses it to add a ware to a station's *traded-wares list* (menu_station_overview.lua:2407, under `-- add` for `menu.selectedWares`). The reference used it as "+1 cargo unit", which is likely a misuse |
| `void RemoveTradeWare(UniverseID, const char*)` | W | trade-list counterpart |
| `bool DropCargo(UniverseID, const char* ware, uint32_t amount)` | W | ejects cargo into space (creates drops). Use it to remove cargo, but it spawns crates |
| `uint32_t GetPurchasableCargo(...)`, `GetCargoStatistics(...)` | R | |

**Better cargo write path: MD.** `<add_cargo object= ware= exact=N result=>` ("Add cargo to
an object", supports bulk amounts via the `random` attribute group: `exact`/`min`/`max`)
and `<remove_cargo>`, plus `<add_wares>`/`<remove_wares>` `[xsd common.xsd]`. This removes
the reference's "single unit per call" limitation. Bridge: C++ → `raise_lua` → Lua
`AddUITriggeredEvent(...)` → MD `<event_ui_triggered>` → action (one-frame latency; see §3.4).

### 2.7 Money / credits
The reference claimed "no credit read/write API". That is **wrong**. It is only missing from the
*C export* table:
- Lua globals (not FFI, not in `[exe]`): `GetPlayerMoney()` (used in menu_map.lua,
  helper.lua…), `TransferPlayerMoneyTo(amount, component)` (menu_map.lua:3498, 4151).
- C export: `void AddPlayerMoney(int64_t money)` (negative values presumably allowed
  **[UNVERIFIED]**); `GetCreditsDueFromPlayerTrades`, `GetMoneyLog(...)`.
- MD: `player.money`, `faction.money`, `container.money` properties; `<transfer_money from=
  to= amount= result=>` between factions or objects; `<reward_player money=>`;
  `event_player_money_updated`, `event_object_money_updated`,
  `event_player_owned_money_updated` `[xsd]`.
- C++ access to Lua globals: `api->get_lua_property(...)` (UI thread), or our own Lua shim
  that reads `GetPlayerMoney()` and sends it back through `raise_event` / a bridged event.

### 2.8 Orders / AI
| Function | Notes |
|---|---|
| `uint32_t CreateOrder(UniverseID, const char* orderid, bool default)`, `CreateOrder3(..., bool isoverride, bool istemp)`, `CreateOrder2` (exported, no cdef) | returns the order index; **no target parameter** |
| `bool EnableOrder(UniverseID, size_t idx)`, `RemoveOrder(…)`, `RemoveOrder2(…)`, `RemoveAllOrders2(UniverseID, bool onlytrade, bool onlypriority)`, `AdjustOrder(...)` | |
| `GetNumOrders` / `GetOrders(Order*, …)` / `GetOrders3(Order3*, …)`, `GetOrderDefinition` | read the queue |
| `CreateDeployToStationOrder(UniverseID)` | |
| **Lua global** `SetOrderParam(controllable, orderidx, paramidx, listidx, value)` | menu_map.lua:2975/3054 (about 91 uses). **This sets targets** (e.g. an Attack target via `ConvertStringToLuaID(tostring(target))`) |
| Lua `GetOrderParams(controllable, idx)` | read |
| MD `<create_order>`, `<edit_order_param order= param= value=>` | MD route |

So targeted orders do **not** need `SetOrderParamInternal`. The sequence is
`C.CreateOrder(ship, "Attack", false)` → Lua `SetOrderParam(...)` → `C.EnableOrder(ship, idx)`,
all run in Lua on the UI thread (the vanilla map menu works this way). The internal versions
in version_db (`CreateOrderInternal`, `SetOrderParamInternal`) are a fallback only.
**[UNVERIFIED: exact param index layout per order; read it from `aiscripts/order.*.xml`
`<param>` lists.]**

### 2.9 Saves / session
| Function | Notes |
|---|---|
| Lua global `LoadGame(filename)` | gameoptions.lua:2963, scheduled through `Helper.addDelayedOneTimeCallbackOnUpdate`. Not a C export |
| Lua global `SaveGame(filename, name)` | gameoptions.lua:9296 |
| `bool IsSaveValid(const char* filename)` | name **without** `.xml.gz` (e.g. `save_010`) `[ref]` |
| `bool IsSaveListLoadingComplete(void)`, `void ReloadSaveList(void)` | wait for this before `LoadGame` |
| `const char* GetSaveFolderPath(void)` | **use this** instead of guessing `Documents\Egosoft\X4\<id>\save` |
| `UISaveInfo GetLastSaveInfo(void)`, `bool HasSavegame(void)`, `bool DeleteSavegame(const char*)` | |
| `void TriggerAutosave(bool checkenabled)`, `GetAutosaveIntervalOption` / `SetAutosaveIntervalOption` | the authority controls save timing; disable autosave on clients |
| `GetSavesCompressedOption` / `SetSavesCompressedOption` (exported) | |

Load from C++: wait for `IsSaveListLoadingComplete()`, then `raise_lua("loadSave"?)`. The
reference raised a vanilla Lua event named `loadSave` that calls `LoadGame` **[UNVERIFIED
in vanilla Lua; ours should register its own Lua handler that calls `LoadGame(name)`]**.
Save-file integrity: saves are `.xml.gz`. Hash the file for the server checksum.
**[UNVERIFIED]** whether a save copied from another machine loads with the
"modified/unsigned" warnings. The reference did this successfully over scp.

### 2.10 Boarding
`CreateBoardingOperation(target, boarderfactionid, approachthreshold, insertionthreshold)`,
`StartBoardingOperation(target, faction)`, `AbortBoardingOperation`, `AddAttackerToBoardingOperation(...)`,
`UpdateBoardingOperation`, `GetCurrentBoardingPhase(target, faction)` are all exported (menu_map.lua).
They could replay a boarding on the authority. The reference only replicated the resulting
ownership change.

### 2.11 Built-in multiplayer exports: do NOT use
`NewMultiplayerGame(const char* modulename, const char* difficulty)`,
`ConnectToMultiplayerGame(const char* serverip)`, `IsNetworkEngineEnabled`, `IsOnlineEnabled`,
`WasSessionOnline`. These are Egosoft's RakNet/SLNet "ventures" stack and need Steam/EgoNet.
They spawned the worker thread that exposed the reference's proxy race. **Our design does
not use them.**

### 2.12 Known gaps (summary)
| Need | Status |
|---|---|
| Velocity read | no export. Derive from deltas; or MD `object.velocity` |
| Velocity / physics write | none. Kinematic `SetObjectSectorPos` only |
| Hull / shield write | MD `<set_object_hull>` / `<set_object_shield>` only. Read via Lua `GetComponentData(id,"hullpercent")` |
| List all sectors / clusters | no direct export (see 2.3) |
| Stable cross-machine IDs | none. Use network IDs |
| Weapon fire / projectiles | no API. Replicate outcomes (kills, damage%) not shots |
| Freeze AI / physics on ghosts | **[UNVERIFIED]** (`set_object_active`, empty order queue, `SetObjectSectorPos` every frame) |
| Credits | available via Lua and MD (2.7) |
| Bulk cargo | available via MD `add_cargo exact=` (2.6) |
| Targeted orders | available via Lua `SetOrderParam` (2.8) |

---

## 3. X4 Lua UI facts

### 3.1 Environment
- LuaJIT 2.1 (`lua51_64.dll`) with `ffi` available: `local ffi = require("ffi"); local C = ffi.C;
  ffi.cdef[[ ... ]]`. Redeclaring a cdef that another addon already declared raises an
  error, so wrap it in `pcall(ffi.cdef, ...)` (the x4native bootstrap does this).
- **Protected UI Mode** (`GetUISafeModeOption()` / `SetUISafeModeOption(bool)`,
  gameoptions.lua:444, 3380) blocks `package.loadlib`/`require` of native code and also
  restricts modified base-game UI files (`C.GetModifiedBasegameUIFilesExtensions()`). Users
  must turn it off. Our join flow should detect this and show a clear message.
- **No `os.getenv`** in the game's Lua (`os.getenv == nil`) `[ref STATE.md]`. No sockets
  and no file I/O beyond what's exposed. Steam relaunching X4 also drops env vars. So all
  config comes from a **file read by the C++ DLL** or from the in-game dialog.
- UI addons are registered with `ui.xml`:
  `<addon name="x4mp"><environment type="menus"><file name="ui/x4mp_menu.lua"/></environment></addon>`.
- Lua ↔ engine event APIs: `RegisterEvent(name, fn)` (receives MD `raise_lua_event`),
  `AddUITriggeredEvent(screen, control, value)` (to MD `event_ui_triggered`),
  `SetScript("onUpdate", fn)` (per frame),
  `Helper.addDelayedOneTimeCallbackOnUpdate(fn, bool, time)`.

### 3.2 Menu injection (main menu entries)
- Vanilla menus append themselves to the global `Menus` table and call
  `Helper.registerMenu(menu)` (e.g. ego_chatwindow/chatwindow.lua:44-46).
- The main/options menu is gameoptions.lua `OptionsMenu`. Its entries live in
  `config.optionDefinitions["main"]` (gameoptions.lua:1246), where `config` is **file-local**.
  The reference reached it by finding the menu in `Menus`, pulling `config` out through
  `debug.getupvalue` on a menu function, adding entries, and wrapping
  `menu.submenuHandler(optionParameter)`. That works but depends on internal layout, so
  re-verify it after each game patch.
- Lower-risk alternative: kuertee's **UI Extensions** mod adds callback hooks to vanilla
  menus (https://github.com/kuertee/x4-mod-ui-extensions). It would be a hard dependency.
  **[UNVERIFIED]** whether it covers the main menu.
- Re-install the injection on `RegisterEvent("gfx_ok")`/`"show"` since menus are rebuilt.

### 3.3 Text-entry dialog (Join: address / name / password)
- Widget API: tables of rows and cells, `cell:createEditBox({ height=…, description=…,
  maxChars=255, selectTextOnActivation=false }):setText(text, {...})`, with handlers
  `row[1].handlers.onEditBoxActivated`, `onEditBoxDeactivated(_, text, textchanged)`,
  `onTextChanged`. See ego_chatwindow/chatwindow.lua:655-659 for a complete working edit
  box, and ego_detailmonitorhelper/helper.lua:2195 (`Helper.createEditBox`),
  :2658 (`updateEditBoxText`), :2781 (`setEditBoxScript`), :2820/2825
  (`confirmEditBoxInput`/`cancelEditBoxInput`), :2830 (`Helper.activateEditBox`).
  `ActivateEditBox(id)` is a global (menu_map.lua:26994).
- Recommended build: our own menu (`table.insert(Menus, menu)`, `Helper.registerMenu`),
  opened from the injected main-menu button. It holds a frame with a 2-column table:
  Address, Port, Name, Password (no "masked text" option seen for passwords,
  **[UNVERIFIED]**), plus Connect/Cancel buttons. On Connect, Lua calls the DLL
  (`__X4NATIVE_API.raise_event("x4mp.join", json)` or a `register_lua_bridge`'d Lua event)
  and the DLL persists the values to a config file.
- The in-game chat window (ego_chatwindow) is a good pattern for an MP chat/HUD (edit box
  plus scrolling table).

### 3.4 How Lua talks to native code (and MD)
| Direction | Mechanism |
|---|---|
| Lua → C++ | `__X4NATIVE_API.raise_event(name, str)` (global exposed by the x4native bootstrap), or an `api->register_lua_bridge(lua_event, cpp_event)` so `RegisterEvent` callbacks forward automatically |
| C++ → Lua | `api->raise_lua_event(name, param)` (UI thread only) → Lua `RegisterEvent(name, fn(_, param))` |
| C++ → game | direct calls through `get_game_function` / `x4n::game()->Fn(...)` (UI thread) |
| C++ → Lua property | `api->get_lua_property(getter_fn, key, field, type, out)` |
| MD → Lua | `<raise_lua_event name="'x'" param="$obj"/>` (param: string, number or component) |
| Lua → MD | `AddUITriggeredEvent(screen, control, value)` → `<event_ui_triggered screen="'..'" control="'..'"/>`, value in `event.param3` |
| MD → C++ | `raise_lua_event` → bridged Lua event, or (faster) `md_subscribe_*` typed hooks |

Latency: Lua → MD is at least **one frame** (bvbohnen's Named Pipes docs note "1 frame
lua->md delay"). Batch many operations into one event payload.

---

## 4. Mission Director events useful for sync

Names from `x4-unpacked\libraries\common.xsd` / `md.xsd`. Numeric type IDs for
`md_subscribe_*` come from X4Native `sdk/x4_md_events.h` (those confirmed are given; the rest
can be looked up there).

| Purpose | MD event (XML) | X4Native type ID / data |
|---|---|---|
| Kills | `event_object_destroyed`, `event_object_killed_object`, `event_player_killed_object`, `event_player_owned_destroyed`, `event_unit_destroyed` | `Killed` = **237** (`KilledData`: source = killed, killer at raw+0x18) `[ref FOLLOWUP]` |
| Combat start | `event_object_attacked`, `event_object_attacked_object`, `event_player_attacked_object`, `event_player_ship_hit` | `AttackStarted` 32 (`attacker, target`), `AttackStopped` 33 |
| Ownership | `event_object_changed_owner`, `event_object_changed_true_owner`, `event_contained_object_changed_owner`, `event_contained_sector_changed_owner` | `EntityChangedOwner` **175**; `ObjectChangedOwner`/sector variant 78 |
| Boarding | `event_boarding_operation_created/started/removed`, `event_boarding_phase_changed`, `event_boarding_triggered` | 39 / 41 / 40 |
| Build | `event_build_started/finished/cancelled/added`, `event_build_finished_components`, `event_player_build_*`, `event_object_built_station`, `event_god_created_station`, `event_player_built_ship`, `event_player_built_station` | `BuildStarted` 61, `BuildFinished` 56 |
| Trade | `event_trade_started/completed/cancelled`, `event_player_trade_started/completed/cancelled`, `event_object_collected_ware`, `event_player_collected_ware` | look up |
| Movement / topology | `event_object_changed_sector`, `event_object_entered_gate`, `event_player_entered_gate`, `event_object_changed_zone` | `ObjectChangedSector` 83, `ObjectChangedZone` 87 |
| Docking | `event_object_docked`, `event_object_docked_at`, `event_object_undocked`, `event_object_undocking_started` | `ObjectDocked` 156 |
| Money | `event_player_money_updated`, `event_object_money_updated`, `event_player_owned_money_updated` | look up |
| Player control | `event_player_started_control`, `event_player_stopped_control`, `event_player_ejected`, `event_player_teleport_successful`, `event_player_changed_activity` | look up |
| Lifecycle | `event_game_loaded`, `event_game_started`, `event_universe_generated`, `event_game_saved` | used by x4native itself |
| UI bridge | `event_ui_triggered` (`param`=screen, `param2`=control, `param3`=value), `event_cue_signalled` | |

Approach: use typed `md_subscribe_after` hooks (no MD XML needed, zero-latency capture)
**when version_db resolves `EventQueue_InsertOrDispatch`**, and keep a pure-MD fallback
(`<cue instantiate="true"><conditions><event_object_destroyed .../></conditions>
<actions><raise_lua_event .../></actions></cue>`) for builds without RVAs. Remember the
off-thread caveat (§1.7).

Note: MD `<event_*>` conditions at universe scope (e.g. any ship destroyed anywhere) are
expensive or impossible in pure MD (most events need an `object=`/`space=` filter); the
native hook sees all events globally. **[UNVERIFIED]** MD support for global listeners per
event type.

---

## 5. Prior art and alternatives

### 5.1 mercurynova/X4Multiplayer_Mod (our `reference/`)
https://github.com/mercurynova/X4Multiplayer_Mod. Binary-only, no license, so don't copy.
What it teaches is already in PLAN.md. Extra API-level findings from this research:
- Their "no credits", "1 unit AddTradeWare" and "targeted orders need internals" blockers come
  from limiting themselves to C exports. Lua globals (`GetPlayerMoney`, `SetOrderParam`) and MD
  actions (`add_cargo exact=`, `transfer_money`, `edit_order_param`, `set_object_hull`) cover
  them.
- Their Windows MD hooks were dead until `version_db/` was shipped, so packaging must be
  verified by a startup self-test that logs resolved counts to the server.

### 5.2 eg3r X4Online (unreleased?)
Search snippets describe "X4Online", a native C++ multiplayer extension by the X4Native author:
the host's game is the server, **FlatBuffers** binary protocol, network I/O on a background
thread with **lock-free queues** to the UI thread, about **20 Hz** state broadcast, client-side
interpolation, "<1 ms per frame overhead". **[UNVERIFIED]**: `github.com/eg3r/X4Online`
returns 404 and it is not in eg3r's public repo list. It may become public later and is worth
monitoring as a competitor or collaborator. Its numbers are a reasonable baseline for our
budget (20 Hz snapshots, interpolation).

### 5.3 eg3r/X4Mcp (MIT)
https://github.com/eg3r/X4Mcp. An MCP/HTTP server running **inside** X4 as an X4Native
extension. It shows that hosting a socket server in-process on X4Native works, and it is an
MIT-licensed example of networking, threading and game-call marshalling we can study (and
reuse with attribution).

### 5.4 bvbohnen / SirNukes Mod Support APIs (MIT)
https://github.com/bvbohnen/x4-projects/tree/master/extensions/sn_mod_support_apis
- Named Pipes API: a Lua plugin plus `winpipe` DLL (loaded through `package.loadlib`, so it
  also needs Protected UI off), an MD wrapper, and an optional Python
  `X4_Python_Pipe_Server` (pywin32). X4 is the pipe *client*. Docs:
  `documentation/Named_Pipes_API.md`.
- Lessons: Lua ↔ MD has ≥1-frame latency, so batch messages. The Lua→MD bridge pattern
  (`AddUITriggeredEvent` / `raise_lua_event`) is proven. Their Simple Menu / Hotkey / Lua
  Loader APIs show stable ways to add menus without editing vanilla files **[UNVERIFIED
  details]**.
- For us: X4Native replaces the pipe DLL. A named pipe could still serve a *local* IPC
  fallback (e.g. a desktop launcher talking to the running game) but isn't needed.

### 5.5 Others
- Yxel69/x4p2pmultiplayer (MIT, 2 commits, P2P, minimal content):
  https://github.com/Yxel69/x4p2pmultiplayer. Nothing usable.
- Egosoft's own online features (Ventures, timelines) are asynchronous and Steam/EgoNet-bound,
  not real-time MP.
  https://www.pcgamer.com/x4-foundations-goes-online-but-dont-expect-multiplayer/

---

## 6. Recommendations for mod design (derived)
1. Gate all world interaction on `on_universe_ready`. Run every game call on the UI thread
   from a job queue drained in `on_frame_update`, with a per-frame time budget.
2. Ship `version_db/` with X4Native. At init, log the resolved/expected counts and report the
   capability flags (`md_hooks`, `native_tick`, `game_build`) to the server in the handshake.
3. Implement a small **Lua "capability shim"** in our UI addon for things that exist only as
   Lua globals: `GetPlayerMoney`, `TransferPlayerMoneyTo`, `SetOrderParam`, `GetOrderParams`,
   `GetComponentData`, `LoadGame`, `SaveGame`. The C++ side calls it via `raise_lua_event` with
   batched JSON payloads.
4. Implement an **MD "actions shim"** (`md/x4mp_actions.xml`) driven by `event_ui_triggered`
   for `add_cargo`/`remove_cargo`, `transfer_money`, `set_object_hull`/`shield`,
   `create_ship`, `destroy_object`, `edit_order_param`, `warp`.
5. Use network IDs, never `UniverseID`, on the wire. Use macro names + faction ids as spawn
   descriptors.
6. Use custom player factions via a `libraries/factions.xml` diff.
7. Save hygiene: remove ghosts before any save and disable client autosave. Only the
   authority's saves are canonical.
8. Pin the supported X4 build plus the X4Native release. Refuse to join on a mismatch (the
   server enforces it via handshake fields).

## 7. Open questions / to verify in-game (M2-M3)

> Merged into `docs/decisions.md` Part 3 (V01–V26) and the spike plan in `docs/roadmap.md`.
- [ ] Which thread MD typed callbacks actually run on, on 9.00 Windows.
- [ ] Ghost freezing: can a spawned ship's AI/physics be disabled so `SetObjectSectorPos`
      each frame doesn't fight the engine? Does it collide?
- [ ] Cost of `SetObjectSectorPos` for 200+ objects per frame; cost of `GetAllFactionShips`.
- [ ] Velocity read via `GetComponentData(id, "speed"/"velocity")` from Lua.
- [ ] Hook point to strip ghosts *before* saves (autosave + manual + quicksave).
- [ ] Custom factions loading into an existing save.
- [ ] Main-menu injection on 9.00 without `debug.getupvalue` (or confirm `debug` stays available).
- [ ] Lua `SetOrderParam` arg layout for `Attack`/`MoveTo`/`DockAt` orders (from `aiscripts/order.*.xml`).
