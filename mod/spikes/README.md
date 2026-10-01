# X4MP spike extension `x4mp_spike` (session 1)

Throwaway in-game test extension. XML + Lua only, no native DLL. It runs the eight steps of
[docs/spikes/session-1.md](../../docs/spikes/session-1.md) by itself after a save loads, writes
machine-readable lines to the X4 debug log and shows on-screen notifications. **Not shipped.** Remove it
after the session (`uninstall-spike.ps1`) and throw the two test saves away.

## Use

1. `mod\spikes\install-spike.ps1` (optional `-X4Dir "<folder with X4.exe>"`). Installs to
   `<X4>\extensions\x4mp_spike\` and unblocks the files.
2. Steam launch options: `-debug all -logfile x4mp_spike.log`. Settings > Extensions: Protected UI mode OFF.
3. Load the test save (made before installing). Wait. Total run time is about 8 minutes (15 s start delay,
   then the steps at the times in the config block). Be in space (not docked), in a ship.
4. When "X4MP spike complete" appears: save to a new slot, quit to the main menu, load that save. On that
   load the extension only runs the **Verify** checks (the `md.$X4MP_Done` flag is in the save).
5. Send back the log (or all lines containing `[X4MP-SPIKE]`) plus the notes the notifications ask for.
6. `uninstall-spike.ps1`.

Config (step on/off, ghost counts `100,250,500`, ghosts per frame, seconds per tier, spawn distances, step
durations) is the **CONFIG BLOCK** at the top of the `Boot` cue in `md/x4mp_spike.xml`. Set `$ForceFullRun`
to `true` to run all steps again on a save that already completed a run.

## Log format

```
[X4MP-SPIKE] <step> <PASS|FAIL|INFO|MEASURE> key=value key=value ...
```

`step` is `S1`..`S9`, `MISC`, `LUA`, `BOOT`, `VERIFY`, `DONE`. MD lines come from `debug_text filter="general"`,
Lua lines from `DebugError`. Spaces inside values are replaced by `_` on the Lua side. Useful greps:

| grep | meaning |
|---|---|
| `[X4MP-SPIKE]` | everything |
| `[X4MP-SPIKE] .* FAIL` | every failure |
| `[X4MP-SPIKE] S3 MEASURE` | ghost cost per tier (the headline numbers) |
| `[X4MP-SPIKE] S8 MEASURE` | Lua to MD to Lua round trip |
| `[X4MP-SPIKE] S7 MEASURE` | sector count, ship enumeration |
| `[X4MP-SPIKE] VERIFY` | persistence checks after the reload |

Anything that went wrong inside the engine (unknown attribute, bad expression) is logged by X4 itself near the
`[X4MP-SPIKE]` lines; include them when you send the log.

## Steps, API used, log keys, V-items answered

Every step is its own cue (MD) or coroutine (Lua), so one failing step does not stop the others. Times below
are defaults from the config block (step N starts at 15 s + sum of the earlier step durations and 10 s gaps).

### Step 1, S7: sector list and ship enumeration (V09, V10)
- MD `find_sector multiple="true"` (default scope = galaxy, normal clusters), `find_gate`,
  `find_highway_entry_gate superhighwaygate="true"`, `find_ship space="player.galaxy" multiple="true"`.
  Lua `GetNumAllFactions/GetAllFactions` + `GetNumAllFactionShips/GetAllFactionShips` over all factions
  (twice: hidden factions excluded and included), timed with `QueryPerformanceCounter` (fallback `os.clock`,
  `GetCurRealTime`); `GetComponentData(sector, "macro"...)`.
- Keys: `S7 MEASURE sectors_found=`, one `S7 INFO sector=<macro> owner= known= gates=<macro,macro> gates_active= highways=`
  line per sector, `S7 MEASURE md_find_ship_all ships= systime_before= systime_after=` (whole seconds only),
  `S7 MEASURE enum=GetAllFactionShips include_hidden= factions= ships_total= ships_unique= api_ms= dedupe_ms= top5=`,
  `S7 INFO lua_probe_sector= md_macro= macro=`.

### Step 2, S4: money (V04)
- Lua `GetPlayerMoney()`, `C.AddPlayerMoney(int64)`; MD `player.money`, `transfer_money` (player to team 1 and back,
  player to Argon and back, overdraw), `event_player_money_updated` listener. The unit of `AddPlayerMoney` is
  probed with +100 units (vanilla passes price*100, so cents are expected; MD `money` type is cents).
- Sequence: probe unit, +1000 cr, -1000 cr, overdraw to -5000 cr, restore. The original balance is restored at the end.
- Keys: `S4 INFO lua_money_start= md_money_raw=`, `probe_add_units= delta_lua= unit=`, `S4 PASS|INFO what=add_plus_1000cr`,
  `what=overdraw balance_after= went_negative= clamped_at_zero=`, `S4 INFO md_player_money_while_overdrawn=`,
  `S4 INFO transfer_money <from>_to_<to> requested= result=`, `S4 INFO event_player_money_updated n= old= new=`,
  `S4 PASS|FAIL what=restore_original`.

### Step 3, S1: team factions (V01, V08, V25 rename)
- `libraries/factions.xml` diff adds `x4mp_team_1..8` (`active="0"`, own `<color r g b>`, `<account>`), `libraries/colors.xml`
  diff maps `faction_x4mp_team_k`, `t/0001-l044.xml` page 90444 holds the names ("MP Team k").
- MD: lookup of `faction.x4mp_team_k`, `set_faction_active`, `set_faction_relation` (team 1 = 0.8 ally, team 2 = -0.5
  hostile), `set_faction_relation_locked`, an attempt to change a locked relation, `create_ship` for teams 1 and 2
  (Argon courier, no pilot, 5 km away, named "MP Team k", made known), `set_faction_identity` rename of team 3.
  Lua: `GetAllFactions(true)` listing and `IsFactionAlly/Enemy/HostileToFaction` against `player`.
- Keys: `S1 PASS what=faction_resolves id= name= isactive_before=`, `what=set_faction_active isactive_after=`,
  `S1 INFO relations_set team1_to_player= ...`, `relation_locked`, `set_relation_while_locked`,
  `S1 PASS what=create_ship_for_team team= ship= owner= dist_m=`, `S1 INFO ship_state team= owner= relation_to_player=`,
  `S1 PASS|FAIL lua_faction= listed_in_GetAllFactions= ally_to_player=`, `S1 INFO set_faction_identity team3 before= after=`.
- **You:** target "MP Team 1" and "MP Team 2" and note colour and friendly/enemy status.

### Step 4, S8: cargo, ownership, latency (V13, V14)
- MD `add_cargo`/`remove_cargo exact= result=` with `ware.energycells` on the team 1 ship (add 37, remove 12, remove
  1000 more than present, add 100000 over capacity, then empty it) and +1/-1 on a station in the sector;
  `set_owner` to team 3; Lua `SetComponentOwner` back to team 1 (via `C.GetOwnerDetails` before/after);
  Lua `GetNumCargo/GetCargo` cross-check; 10 pings Lua `AddUITriggeredEvent("X4MP_Spike","ping",n)` -> MD
  `event_ui_triggered` -> `raise_lua_event` -> Lua `RegisterEvent`.
- Keys: `S8 PASS|INFO what=add_cargo exact=37 result=`, `what=remove_cargo ...`, `S8 INFO lua_cargo_of= list=`,
  `what=station_cargo`, `S8 PASS|FAIL what=set_owner_md`, `what=SetComponentOwner_lua`, `what=owner_after_lua_SetComponentOwner`,
  `S8 MEASURE lua_md_lua_roundtrip= pings= rt_ms_avg= rt_ms_min= rt_ms_max= frames_avg=`.

### Step 5, S2: avatar takeover (V02)
- MD `create_ship` owned by `faction.player` (Argon fighter, 1.5 km away), `player.canteleportto.{ship}`; Lua
  `CanTeleportPlayerTo(ship, false|true, false|true)`, `TeleportPlayerTo(ship, true, true, true)`, then reads
  `GetPlayerOccupiedShipID/GetPlayerControlledShipID`. The old ship is **never removed**; the log says whether removing it
  would be possible (not occupied, not controlled, still valid). After `$S2ReturnAfter` seconds (15) the player is moved
  back to the old ship (set 0 to stay in the new one).
- Keys: `S2 PASS what=create_player_owned_ship md_canteleportto=`, `S2 INFO can_teleport_no_control= can_teleport_control=
  can_teleport_control_force=`, `S2 PASS|FAIL what=teleport_result occupied_now= is_new_ship= controlling_new=`,
  `S2 INFO what=old_ship_removable_check removal_would_be_possible= note=not_removed_by_spike`,
  `S2 PASS|INFO what=teleport_back_to_old_ship`.
- **You:** control, HUD, camera, anything odd (black screen, game over).

### Step 6, S3: ghost cost (V03, V16, V17)
- Lua only for the load: `SpawnObjectAtPos2("ship_arg_s_scout_01_a_macro", sector, pos, "x4mp_team_2")` 40 per frame,
  3 to 8 km from the player (falls back to `ownerless` if team 2 fails), `ActivateObject(id, false)`, per-frame
  `SetObjectSectorPos` on circular paths for 20 s per tier, tiers 100, 250, 500. Frame time comes from the interval between
  `onUpdate` calls, move cost from timing the move loop. `GetComponentData(id, "velocity"|"speed")` is probed on 3
  ghosts mid-run. Destroy: half via MD `destroy_object explosion="true"` (list sent by `AddUITriggeredEvent`), half via
  `SelfDestructComponent`; survivors and everything else are removed with `RemoveComponent` (never the player's ship).
- Keys: `S3 INFO tier= what=spawned ok= failed= spawn_ms_per_frame_avg=`, `what=ActivateObject_false ok= failed=`,
  `what=velocity_probe velocity= speed= expected_speed_ms=`, **`S3 MEASURE ghosts=N move_ms_avg= move_ms_p95= frame_ms_avg=
  frame_ms_p95= idle_frame_ms_avg= move_cost_per_ghost_us=`**, `what=drift_check_vs_commanded_m avg_drift_m=` (large drift
  means the engine fights the teleports), `what=destroy_issued`, `what=destroy_result_after_6s md_half_still_valid=
  lua_half_still_valid= *_wrecked=`, `what=cleanup_RemoveComponent_survivors`, `S3 INFO md_sees_ghosts_in_sector=`,
  `S3 PASS|INFO what=md_leftover_check`.
- **You:** FPS at 100, 250, 500; stutter, engine trails, collisions, anything strange.

### Step 7, S9: plot and unlock state (ADR-037)
- Read-only: `md.$` story flags (`BoronQueendomReturned`, `BoronGate1_Open`, `ArgonAllianceFailed`, `TerranGateDND`,
  `BoronGateDND`, `StoryMentors`), count of known/unknown sectors, inactive gates (first 25 listed), each Boron DLC sector
  with owner/known/gate active state. Then **one unlock**: `set_known known="true"` on the first unknown sector, log
  before/after. It is left applied so that Verify can check it survived the reload; revert is `set_known known="false"`.
- Keys: `S9 INFO md_global ...`, `S9 MEASURE sectors_known= sectors_unknown=`, `inactive_gates=`, `S9 INFO inactive_gate in_sector=
  dest_sector=`, `S9 INFO boron_sector= owner= known= gates=<macro:active|inactive>`, `S9 PASS|FAIL what=set_known_sector`.
- How vanilla stores plot progress (all under `x4-unpacked\`):
  - Global MD flags in the `md.` namespace, saved with the game: `md.$BoronQueendomReturned` (set in
    `extensions/ego_dlc_boron/md/setup_dlc_boron.xml:533`), `md.$BoronGate1_Open` (`extensions/ego_dlc_boron/md/story_boron_prelude.xml:580`),
    `md.$ArgonAllianceFailed` (`extensions/ego_dlc_terran/md/story_covert_operations.xml:8502`), do-not-disturb lists
    `md.$BoronGateDND`, `md.$TerranGateDND`, `md.$StoryMentors` group (`md/setup.xml:107`). Flags are set without a value;
    readers test existence with `md.$Flag?`.
  - Gate and highway unlocks are `set_object_active` on the gate object, done by cues such as `Boron_Gates_Open_HereticsEnd`,
    `Boron_Gates_Open_KingdomEnd`, `Boron_Gates_Open_RemainingSystems` (setup_dlc_boron.xml:417-640); the Boron highways
    are switched off at universe creation (`setup_dlc_boron.xml:257-260`).
  - Sector discovery is the `known` state (`set_known`, `find_sector known=`); story cues also use `cuestate.complete`
    of named cues as progress markers.
  - `gamestart.storystate.*` (e.g. `story_queendom_returned`) is only readable during script initialisation of a new
    game, not after loading a save (scriptproperties.xml:2681).

### Step 8, misc (V12, V20)
- MD sets `$x4mp_netid = 4242` on the team 1 ship and `7` on a station and keeps references in `md.$X4MP_PersistShip` /
  `md.$X4MP_PersistStation`. On the next load (**Verify** mode) it checks the variable through the stored reference and by
  searching for the ship (`find_ship owner=faction.x4mp_team_1`). Lua probes `type(debug)`, `debug.getupvalue`, finds the
  `OptionsMenu` entry in the global `Menus` table and looks for the file-local `config` upvalue (the main-menu injection
  method from x4-api-notes 3.2). The Lua `hello` command logs which globals exist, protected UI mode and the timer used.
- Keys: `MISC INFO what=set_netid_on_ship netid_readback=`, `MISC PASS|FAIL what=debug.getupvalue_available`,
  `MISC PASS|INFO what=OptionsMenu_config_upvalue`, `LUA INFO what=globals ...`, `LUA INFO timer= debug_getupvalue=`,
  `LUA INFO protected_ui_mode=`, and after the reload `VERIFY PASS|FAIL what=V12_ship_object_variable_via_md_global_ref`,
  `what=V12_netid_found_by_find_ship_search`, `what=V12_station_object_variable`, `VERIFY INFO what=V01_faction_after_reload id=
  isactive= relation_to_player= locked=`, `VERIFY PASS|FAIL what=S9_set_known_survived`, `what=pollution_check team2_ships_in_galaxy=`
  (expected 1, the S1 ship).

| V item | Step / log |
|---|---|
| V01 | S1 `faction_resolves`, `set_faction_active`, `create_ship_for_team`; VERIFY `V01_faction_after_reload` |
| V02 | S2 |
| V03 | S3 MEASURE, `drift_check`, `ActivateObject_false` |
| V04 | S4 |
| V08 | S1 relations, lock test |
| V09, V10 | S7 |
| V12 | MISC + VERIFY |
| V13 | S8 cargo, owner |
| V14 | S8 MEASURE |
| V16 | S3 `destroy_*` |
| V17 | S3 `velocity_probe` |
| V20 | MISC `debug.getupvalue_available`, `OptionsMenu_config_upvalue` |
| V25 (part) | S1 `set_faction_identity` |
| V21 | not covered here (needs a load without the extension, do it by hand after uninstalling) |

## Where each API comes from (all under `x4-unpacked\`)

Cited in comments in the source files. Summary: Lua FFI signatures are copied verbatim from vanilla `ffi.cdef` blocks
(`ui/addons/ego_detailmonitor/menu_map.lua:503`, `menu_mapeditor.lua:60-121`, `menu_platformundock.lua`, `menu_interactmenu.lua`,
`menu_playerinfo.lua:185-205`, `ego_targetmonitor/targetmonitor.lua:82`); Lua globals (`GetPlayerMoney`, `GetComponentData`,
`ConvertStringTo64Bit`, `ConvertStringToLuaID`, `IsValidComponent`, `RegisterEvent`, `SetScript`, `AddUITriggeredEvent`) are used
by vanilla Lua; MD actions and conditions are checked against `libraries/md.xsd`/`common.xsd` (the file validates against
`md.xsd`) and mirror vanilla usage in `md/*.xml`.

## Known limits

- MD expressions are only checked for syntax by the schema. Property paths (for example
  `$Ship.cargo.{$Ware}.count`, `player.canteleportto.{$ship}`) follow `libraries/scriptproperties.xml` but were not run in the game.
- The player must be in a ship in space when S1, S2, S3, S8 run (they spawn relative to `player.ship`).
- Lua is the only place that can be timed in milliseconds. MD times are game seconds or whole wall-clock seconds.
- The colour of a team is set in `factions.xml` (`<color r g b>`, allowed by `factions.xsd`) and mapped in `colors.xml`.
  Delete `libraries/colors.xml` from the extension if it causes any warning, the test still works.
