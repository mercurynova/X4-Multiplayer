# X4MP spike extension `x4mp_spike` (session 1 + v2 framework for session 2)

Throwaway in-game test extension. XML + Lua only, no native DLL. **Not shipped.** Remove it after the session
(`uninstall-spike.ps1`) and throw the test saves away.

* **v2 (session 2, [docs/in-game-session-2.md](../../docs/in-game-session-2.md))**: a block registry. Nothing runs by
  itself; every test is a *block* the user starts on demand. See "v2: blocks" below.
* **Session 1** (the eight steps of [docs/spikes/session-1.md](../../docs/spikes/session-1.md)) is kept, but its MD
  auto-run on game load is switched off (`md.$X4MP_AutoRun` must be set to re-enable it). Its sections further down
  stay valid as the reference for those log keys.

## Use (v2)

1. `mod\spikes\install-spike.ps1` (optional `-X4Dir "<folder with X4.exe>"`). Installs to
   `<X4>\extensions\x4mp_spike\` and unblocks the files. (Session 2 normally uses `tools\session2\install.ps1`.)
2. Steam launch options: `-debug all -logfile x4mp_spike.log`. Settings > Extensions: Protected UI mode OFF.
3. Start a block (see below), read the notification, send the log (every line with `[X4MP-SPIKE]`).

## v2: blocks

A block is a Lua function registered under a name. Blocks run **only** when launched; running a block twice is safe
(idempotent: no duplicate menu rows, wrappers or windows).

| Launcher | How |
|---|---|
| Chat | open the chat window, type `/x4mpspike <block> [k=v ...]` (the vanilla chat window passes `/cmd args` to `ExecuteDebugCommand`; `x4mp_spike_core.lua` wraps that global and delegates every other command). `/x4mpspike list` shows the registered blocks. Works in game, not in the start menu (no chat there) |
| Lua event | `x4mp_spike.run`, param `<block>;k=v;k=v`. Raised by the M2-005 probe (its `spike_block` config watch, which `tools/session2/run-block.ps1` writes) and usable from any MD cue (below). Works in the start menu too |

### Blocks owned by the framework (task M2-001)

| Block | What it does (session-2 part) |
|---|---|
| `list`, `ping` | registered block list; framework smoke test (Lua log, MD round trip, notification) |
| `ui` | D1: capture OptionsMenu `config` (UIX accessor, then `require("debug")` upvalue scan of `displayOptions`, `createOptionsFrame`, `displayOption`, validated), insert the row **"Multiplayer (X4MP test)"** after `timelines` (if no `config`: wrap `displayOptions` and draw the row into the frame, appended at the end), redraw the main menu if showing. A click on the row opens the standalone window |
| `ui_standalone` | D2/D4: standalone menu `X4MPSpikeMenu` over the start menu or in game: title, **"Test password"** edit box (`textHidden = true`), **"Check"** button (logs only the length), "Close". The frame also has the standard close button |
| `hud` | D5/V23: passive frame on **layer 3** (same layer as the chat window), top right, "X4MP test HUD". `hud off` removes it. Logs whether the frame is still present after other menus (1 Hz, on change only) |
| `extensions` | D6/R7: dumps `GetExtensionList()` (field names with types, then one line per extension with every scalar field) and `GetModifiedBasegameUIFilesExtensions()` |
| `links` | D7/R8: window with **"Nexus page"**, **"Workshop page"**, **"Steam link"** (https, https, `steam://`); each asks `CanOpenWebBrowser()` first, then `OpenWebBrowser(url)` |

Blocks of the other tasks (`saves*`, `clock`, `money`, `v12`, `s9gate`, `onfoot1/2`, `diplo1..7`, `hq*`) are
registered from their own files; until those tasks land the files are inert stubs that only log `LUA INFO what=stub_loaded`.

### Writing a block (M2-002..004)

Only edit your own files: `ui/x4mp_spike_saves.lua` (M2-002), `ui/x4mp_spike_onfoot.lua` (M2-003),
`ui/x4mp_spike_diplo.lua` and `ui/x4mp_spike_hq.lua` (M2-004), plus your own `md/x4mp_spike_<name>.xml` and library
diffs. `ui.xml` already lists them; `x4mp_spike_core.lua` loads first, so the global `X4MPSpike` exists in your file.

```lua
-- luacheck: globals X4MPSpike
local S = X4MPSpike
local K, log = S.K, S.log

S.register("saves1", function(args)          -- args.raw, args.<k>, args._[1..] (positional)
    log("C1", "INFO", K("what", "wrapper_installed"))
    S.notify("saves1: wrapper installed (logging only)")   -- on-screen text "X4MP spike: ..."
end, "C1 wrap SaveGame")

S.registerMD("diplo1", "S11.1 (implemented in md/x4mp_spike_diplo.xml)")   -- Lua only forwards to MD
```

| API | Meaning |
|---|---|
| `X4MPSpike.register(name, fn, desc)` | register a block; `fn(args)` runs inside `pcall` (an error logs `RUN FAIL what=block_error`); the same name again replaces it |
| `X4MPSpike.registerMD(name, desc)` | block implemented in MD: running it calls `toMD(name, args.raw)` |
| `X4MPSpike.run(name, rawArgs)` | run a block now |
| `X4MPSpike.log(step, level, kv)`, `X4MPSpike.K(name, value, ...)` | session-1 log line `[X4MP-SPIKE] <step> <PASS/FAIL/INFO/MEASURE> k=v ...`; `K` builds the `k=v` pairs (spaces in values become `_`) |
| `X4MPSpike.notify(text)` | on-screen notification (through MD); text is prefixed `X4MP spike: ` and logged as `NOTIFY INFO text=` |
| `X4MPSpike.toMD(control, value)` | Lua to MD |
| `X4MPSpike.now()` | seconds from `QueryPerformanceCounter` when available (else `GetCurRealTime`) |
| `X4MPSpike.addUpdate(fn)` | per-frame hook. **Never call `SetScript("onUpdate", ...)` yourself**: it keeps one handler and would replace the framework's |
| `X4MPSpike.startRoutine(name, fn)`, `waitSeconds(s)`, `waitFrames(n)` | coroutine scheduler on the frame loop |
| `X4MPSpike.parseArgs(text)` | `"k=v k=v pos"` or `"k=v;k=v"` to `{ k = v, _ = {pos}, raw = text }` |
| `X4MPSpike.blockNames()` | sorted list of registered blocks |

**MD trigger convention** (for blocks implemented in MD, and for MD cues that must start a Lua block):

* **Lua to MD**: `X4MPSpike.toMD(control, value)` is `AddUITriggeredEvent("X4MP_Spike2", control, value)`. In MD:
  `<event_ui_triggered screen="'X4MP_Spike2'" control="'saves1'"/>`; the value is `event.param3`, the control `event.param2`.
  Use the **block name as the control** so `registerMD(name)` works; use other control names for replies.
  (The screen id without the 2, `X4MP_Spike`, is session 1's channel and stays as it was.)
* **MD to Lua**: `<raise_lua_event name="'x4mp_spike.run'" param="'<block>;k=v;k=v'"/>` starts a Lua block, same as typing
  `/x4mpspike <block> k=v`. Other MD to Lua messages can use your own event name with `RegisterEvent`.
* **MD log lines** use the same format with `debug_text ... filter="general" context="false"`:
  `'[X4MP-SPIKE] <step> <level> k=v ...'`. MD can show a notification itself with `show_notification`.
  `md/x4mp_spike_core.xml` logs every Spike2 event as `MD INFO what=spike2_event control=`, so a cue you forgot to
  write shows up in the log.

## v2 log keys

`step` values added in v2: `RUN`, `NOTIFY`, `UI`, `HUD`, `EXT`, `LINKS`, `MD`; `LUA` as before.

| Line | Meaning |
|---|---|
| `LUA INFO what=x4mp_spike_core_loaded version=2 timer= run_event=x4mp_spike.run` | framework loaded (also after `/reloadui`) |
| `LUA INFO what=chat_command_wrapper command=x4mpspike result=wrapped` (or `installed_no_previous`, `already_installed`) | `ExecuteDebugCommand` wrapper installed |
| `LUA INFO what=stub_loaded file=` | a task stub file loaded (nothing registered) |
| `RUN INFO block= state=start run=N args=` and `state=returned` | a block started / returned; `run=N` counts runs in this Lua state (a second run shows `run=2`) |
| `RUN INFO what=blocks known=a,b,c` | answer to `list` |
| `RUN FAIL what=unknown_block block= known=` | no such block |
| `RUN FAIL block= what=block_error err=` | the block raised a Lua error |
| `RUN INFO what=ping src= frame= timer=` and `MD INFO what=lua_to_md_ping frame= age=` | `ping` block, Lua side and MD side |
| `RUN INFO block= what=forwarded_to_md` | an MD block was started |
| `NOTIFY INFO text=` | notification requested |
| `MD INFO what=spike2_event control=` | MD saw a Lua to MD event |
| `UI INFO what=optionsmenu_found found= menus=` | `OptionsMenu` present in `Menus` |
| `UI INFO what=optionsmenu_functions displayOptions= createOptionsFrame= displayOption= submenuHandler= currentOption= isStartmenu=` | types of the functions the adapter needs |
| `UI INFO what=protected_ui_mode value=` | `GetUISafeModeOption()` |
| `UI INFO what=uix_accessor present=` and `UI PASS/INFO what=uix_getConfig ok= valid=` | UIX source (R1 source 1) |
| `UI PASS/INFO what=require_debug require_ok= type= getupvalue= global_debug=` | V20: does `require("debug")` work, is `getupvalue` there |
| `UI INFO what=upvalue_named_config function= index= valid=` and `UI INFO what=upvalue_scan function= is_function= upvalues= found=` | scan of each vanilla function |
| `UI PASS what=config_capture source=uix` or `source=debug:<function>` (`main_rows= optionsLayer=`) | V20 answer: which source captured a valid `config`. `source=none` plus `note=falling_back_to_displayOptions_wrapper` otherwise; `reused=true` on a second run |
| `UI PASS/FAIL what=row_state method=config_insert` or `wrap_displayOptions`, `action=inserted/already_present/appended_at_end/wrapper_installed/wrapper_already_installed rows_with_id=` | the row append. `rows_with_id` must be 1, also after a second run (**idempotency check**) |
| `UI INFO what=main_menu_redraw ok= reason=` | the main menu was redrawn so the row shows |
| `UI INFO what=ui_block_done source=` | `ui` finished |
| `UI INFO what=row_clicked row=` | the user clicked the row |
| `UI INFO what=standalone_menu_registered name=X4MPSpikeMenu`, `UI INFO what=standalone_OpenMenu_called mode= err=` | menu registered; `OpenMenu` called (the result of the call, not of the display) |
| `UI INFO what=standalone_onShowMenu mode= is_startmenu=` and `UI PASS/FAIL what=standalone_displayed mode= err=` | the engine called our menu / the frame was built and displayed (D2 answer: these appear when it opens over the start menu) |
| `UI INFO what=password_check length= text_hidden_requested=true` | **Check** clicked; only the length (-1 = nothing typed). The text is dropped afterwards and never logged |
| `UI INFO what=standalone_close mode= due_to=` | window closed |
| `UI FAIL what=helper_missing` | global `Helper` not available |
| `HUD INFO what=hud_menu_registered`, `HUD PASS/FAIL what=hud_displayed layer=3`, `HUD INFO what=hud_closed` | `hud` block |
| `HUD INFO what=frame_present_change present=` | our layer-3 frame appeared or disappeared (1 Hz poll); compare with what you saw after opening the map or pause menu |
| `EXT INFO what=GetExtensionList count=` and `EXT INFO what=field_names fields=name:string,...` | R7 shape of the list |
| `EXT INFO extension index= <field>=<value> ...` | one line per extension, every scalar field (strings cut at 80 chars, at most 200 extensions) |
| `EXT INFO what=GetModifiedBasegameUIFilesExtensions value=` | extensions that modify base-game UI files (`(empty)` if none) |
| `LINKS INFO what=click which=nexus` (or `workshop`, `steam`) `url= can_open_web_browser=` | a link button was clicked |
| `LINKS PASS/FAIL what=OpenWebBrowser_called which=` and `LINKS INFO what=OpenWebBrowser_skipped reason=CanOpenWebBrowser_false` | the call ran (what opened is the user's observation) / was skipped |

Greps: `[X4MP-SPIKE] UI`, `[X4MP-SPIKE] RUN`, `[X4MP-SPIKE] .* FAIL`.

---

# Session 1 (kept for reference)

Session 1 ran the eight steps of [docs/spikes/session-1.md](../../docs/spikes/session-1.md) by itself after a save
loaded. In v2 this is off unless `md.$X4MP_AutoRun` is set. Total run time was about 8 minutes. Be in space (not
docked), in a ship, when running them.

Config (step on/off, ghost counts `100,250,500`, ghosts per frame, seconds per tier, spawn distances, step
durations) is the **CONFIG BLOCK** at the top of the `Boot` cue in `md/x4mp_spike.xml`. Set `$ForceFullRun`
to `true` to run all steps again on a save that already completed a run.

## Log format

```
[X4MP-SPIKE] <step> <PASS|FAIL|INFO|MEASURE> key=value key=value ...
```

`step` is `S1`..`S9`, `MISC`, `LUA`, `BOOT`, `VERIFY`, `DONE` (session 1) and `RUN`, `NOTIFY`, `UI`, `HUD`, `EXT`, `LINKS`, `MD` (v2, table above). MD lines come from `debug_text filter="general"`,
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

---

# Blocks `saves1`, `saves1_block`, `saves2`, `saves4`, `clock`, `money`, `v12`, `s9gate` (task M2-002)

Files: `ui/x4mp_spike_saves.lua`, `md/x4mp_spike_saves.xml`, `md/notifications.xml` (a `<diff>` that adds one sibling branch in
front of the vanilla `player.autosave.available` branch of cue `AutoSave_Attempt`: with `global.$x4mp_noSave` true the
autosave request is dropped and logged; otherwise inert). Blocking has two flags: Lua `S.saves.blocking` (resets on a
Lua reload) and MD `global.$x4mp_noSave` (saved in the game; logged at every load as `SAVE INFO what=md_loaded`).

| Block | Does |
|---|---|
| `saves1` | wraps `SaveGame` and `IsSavingPossible` (chain safe, idempotent), logs every call and its caller (`debug.traceback` if a debug library is reachable); hooks the options menu Save row and tooltip if the menu internals can be reached. Notification "saves1: wrapper installed (logging only)" |
| `saves1_block [off=1]` | blocking ON (OFF with `off=1`): `SaveGame` swallowed, `IsSavingPossible` returns false, MD flag set. "saves1: blocking ON" |
| `saves2 [native=0]` | blocking ON, `C.TriggerAutosave(true)` (so the probe's hook logs it), then MD signals the vanilla autosave request cue. "saves2: done" |
| `saves4` | blocking OFF, calls `X4MP_Probe.markSaveBegin(name)` if present, `SaveGame("x4mp_s2test_1", ...)`, waits for the MD save event (60 s). "saves4: done" |
| `clock` | 240 s, one sample per second on both sides, "clock: save now" at 120 s, resumes after a load through MD (`clock;resume=1;n=;dur=`), "clock: done" |
| `money` | logs `GetPlayerMoney` before and 3 s later plus MD `player.money`; never changes money (the probe does the native +-100) |
| `v12` | MD: sets `$x4mp_netid` (4242), `$x4mp_big` (4000000000), `$x4mp_str` on the player ship and `$x4mp_netid` (7) on a station; references stored in `md.$X4MP_S2_V12*`; 15 s after every load the verify cue logs PASS/FAIL |
| `s9gate` | MD: `find_gate active=false`, prefers a gate in a known sector, `set_object_active`, ref stored in `md.$X4MP_S2_Gate`; verify 15 s after every load |

Log keys (step in front, `k=v` pairs):

| Line | Meaning |
|---|---|
| `SAVE INFO what=wrappers SaveGame= IsSavingPossible= debug_traceback=` | wrapper install result (`wrapped`, `already_installed`, `missing_global`) |
| `SAVE INFO what=SaveGame_called n= filename= name= blocked= t= caller=` / `SaveGame_swallowed` | every `SaveGame` Lua call, caller as a `\|`-joined traceback |
| `SAVE INFO what=IsSavingPossible_called total= arg1= real= returned= calls_since_last_line= caller=` | rate limited: new key or every 2 s |
| `SAVE INFO what=menu_hook tooltip= save_row= config_source=` | whether the Save row and tooltip were hooked (`hooked`, `already`, `no_config`...) |
| `SAVE INFO what=blocking value=` / `md_noSave_flag value=` | flag changes (Lua / MD) |
| `SAVE INFO what=md_event_game_saved success= age= noSave_flag=` and Lua `game_saved_event param= seconds_since_last_wrapper_call= plausibly_via_wrapper=` | every game save of any origin; C3: a quicksave with `plausibly_via_wrapper=false` bypassed Lua |
| `SAVE INFO what=autosave_suppressed_by_md_diff age=` | the diff dropped a vanilla autosave (C2 PASS) |
| `SAVE INFO what=md_signal_vanilla_AutoSave_Request ...` | saves2 poked the vanilla request cue |
| `SAVE INFO/FAIL what=TriggerAutosave_called ok= err=` | native call result |
| `SAVE INFO what=saves4_before`, `SaveGame_returned call_ms=`, `SAVE MEASURE what=saves4_timing to_game_saved_event_ms= returned_before_event=`, `saves4_file ...` | C4 |
| `CLOCK MEASURE side=lua n= game_time= real= frame=` and `CLOCK MEASURE side=md n= age=` | C5, join on `n` |
| `CLOCK INFO what=clock_start / md_resume_clock / clock_done` | lifecycle |
| `MONEY INFO what=lua_GetPlayerMoney stage= value= delta=` and `what=md_player_money stage= player_money= credits=` | C6 Lua and MD views |
| `V12 INFO what=set_on_ship / set_on_station ... *_readback=`; `V12 PASS/FAIL what=verify_ship_via_md_global_ref / verify_ship_found_by_search / verify_station` | V12 |
| `S9 MEASURE what=gate_retest inactive_gates=`; `S9 INFO what=gate_before ...`; `S9 PASS/INFO what=gate_after_set_object_active`; `S9 PASS/FAIL what=gate_active_survived_reload` | S9 gate |

---

# Blocks `onfoot1`, `onfoot2` (S10, task M2-003)

Files: `ui/x4mp_spike_onfoot.lua` (orchestration, 2 Hz state sampler, FFI position reads, mirror controller, test menu) and
`md/x4mp_spike_onfoot.xml` (actor and interior services, listeners, janitor). Procedures: [docs/research/on-foot-presence.md](../../docs/research/on-foot-presence.md) section 6.
Logging only. Save F should be a **vanilla** station with a bar; if a room-generating mod ("More Ship Rooms" style) is installed, label every S10
result "modded rooms" and note which room types the game generated in `dynamicroom` slots (S10.9 and S10.2 log `dynamic_types=` / `roomtype=` for exactly that).

| Block | Steps (in this order) |
|---|---|
| `onfoot1` | S10.1 state sampler, S10.3 character spawn, S10.4 movement modes, S10.11 lounge creation, S10.12 enter/leave, S10.14 lounge save safety, S10.7 conversation, S10.8 cost and save hygiene |
| `onfoot2` | S10.2 room keys, S10.5 facing and emotes, S10.6 transitions and teardown, S10.9 interior catalogue, S10.13 lounge slots, S10.15 lounge with actors |

Arguments (chat `/x4mpspike onfoot1 k=v`, or `;`-separated in a run event): `step=N[,M]` run only those steps (either block accepts any step: 1-9, 11-15);
`next=1` end the current wait early; `stop=1` abort the run; `cleanup=1` remove every test actor and lounge and switch listeners off;
`dur=<s>` step duration; `mode=A|B|C` (S10.4 and S10.15 mirror mode); `phase=1|2` (S10.8); `go=1` / `leave=1` / `x= y= z=` (S10.12); `variant=bar|office|venturer`;
`label=` (S10.2); `count=` (S10.9); `body=object` (use `GetPlayerObjectID` as the body transform); `keep=1` (S10.3: keep the actors); `force=1`.
A second launch while a run is active is refused (logged `S10.CTL FAIL what=already_running`).

**What changes game state** (nothing else is touched; no ship, no station, no vanilla interior or NPC is ever removed):

* test actors: cue actors of `md.X4MP_SpikeOnFoot.Actors`, names `Spike Alice`, `Spike Bob`, `Spike Mirror`, `Spike Face`, `Spike Follower`, `Spike Carol`, `Spike NPC 1..8`,
  `Spike Seat 1/2`, `Spike Walker`; owner `x4mp_team_1`; title "X4MP spike"; at most 8 at a time; each step removes its own at its end;
* up to one private dynamic interior "Multiplayer Lounge" (plus the three short-lived ones of S10.11) on the station you are docked at, `persistent=false`;
* the player's own entity is moved into the lounge and back (S10.12, S10.14, S10.15) by `add_actor_to_room`; the return room and position are remembered before the move;
* S10.14 and S10.8 ask you to make saves (they contain the markers on purpose, see the janitor).

**Markers and janitor.** Every actor has: the name prefix `Spike `, the title override, the entity variables `$x4mp_spike` and `$x4mp_tag`, an entry in the saved list
`md.$X4MP_OF_Actors` and the table `md.$X4MP_OF_ByTag`. Every lounge has an entry `[station, interior, room]` in `md.$X4MP_OF_Lounges`. The janitor
(cues `OF_Janitor`, 5 s after every game load, and `OF_JanitorLounge`, 6 s after) works **only from these two lists**, logs what it found (proof that it ran) and removes it.
A lounge the player is standing in is not removed: it is deferred (`lounge_player_inside_deferred`) and removed on the player's next room change after leaving.
Entities that lost their variables are still found through the list; an actor that did not survive the save shows up as `stale` (that is the answer for `set_entity_traits temporary`).
A load with the extension disabled leaves the actors and the lounge in the save (S10.14 run 3); the next load with the extension removes them.

Log keys (step `S10.n`; `S10.JAN` = janitor, `S10.CTL` = run control, `S10.MD` = MD to Lua bookkeeping):

| Line | Meaning |
|---|---|
| `S10.JAN INFO what=lua_loaded file= markers= janitor=` | file loaded (also after `/reloadui`) |
| `S10.JAN INFO what=root_cue_started actors_listed= lounges_listed=` | MD root cue (owner of the actors) started; on a fresh save both are 0 |
| `S10.JAN INFO what=load_sweep_found_actor tag= name= marker_var= temporary_flag= room=` and `S10.JAN PASS/INFO what=load_sweep_done listed_before= removed= stale= listed_now=` | **janitor proof**, 5 s after every load. `PASS` = nothing was left over. `marker_var=nil` means entity variables do not survive a save (V12 retest, on actors) |
| `S10.JAN PASS/INFO what=lounge_sweep reason=load listed= removed= deferred_player_inside= stale=` and `lounge_player_inside_deferred` | lounge janitor |
| `S10.CTL INFO what=run_start block= steps=`, `run_done`, `next_requested`; `S10.CTL FAIL what=already_running` / `unknown_step` | run control |
| `S10.n INFO what=step_begin block= title=` and `what=step_end_marker` | step boundaries; `S10.n FAIL what=step_error err=` = Lua error in the step |
| `S10.MD INFO what=actor_id_received tag= raw= id= position_readable=` / `FAIL what=actor_id_conversion_failed` | the actor id arrived in Lua (needed for S10.4 mode C and S10.5 read-backs) |
| `S10.1 MEASURE n= side=lua t= frame= container= env_object= occupied_ship= lua_room= player_x/y/z/yaw= object_x/y/z/yaw= camera_yaw= speed_player_mps=` | 2 Hz Lua sample (`GetPlayerContainerID`, `GetEnvironmentObject`, `GetPlayerRoom`, `GetPositionalOffset` of `GetPlayerID` and `GetPlayerObjectID`, camera) |
| `S10.1 MEASURE side=md n= room_type= room_macro= room_dynamic= walkablemodule= platform= entity_x/y/z= entity_yaw_deg=` | MD sample with the same `n` |
| `S10.1 MEASURE what=pos_compare n= still= err_player_cm= err_object_cm= yawdiff_*=` | native vs MD room-local position; `still=true` samples decide the 1 cm criterion |
| `S10.1 INFO what=lua_room_change_detected frame=` and `what=md_event_changed_room` / `md_event_received_in_lua kind= frames_since_lua_room_change=` | change detection per frame vs the MD `changed_room` event (criterion: within 1 frame); also `transport_finished`, `started_control`, `stopped_control` |
| `S10.1 PASS/FAIL what=summary ... best_body_source= best_still_err_max_cm= max_walk_speed_mps=` | verdict; `best_body_source` says whether `GetPlayerID` or `GetPlayerObjectID` is the body (a later S10.4 in the same Lua session uses it) |
| `S10.2 INFO what=room_key label= container= container_seed= owner= room_macro= roomtype= kind= dynamic_name= walkablemodule= anchor_x/y/z= chain=` | one line per room (whole station at the start, and each room you enter); compare the sets across revisit / reload |
| `S10.2 INFO what=keyset_begin / keyset_end label= rooms=` | bracket one catalogue |
| `S10.3 PASS/FAIL what=actor_spawned tag= requested= used= name= macro= placed= room_type= same_room_as_player= marker_var_readback=` | spawn result (`requested=player used=crew` = the player's macro did not work as an NPC) |
| `S10.3 MEASURE what=actor_status tag= exists= room_type= x/y/z= moved_from_spawn_m= iswalking= dist_to_player_m=` | survival and drift while you stay in the room (also used by S10.4/.5/.6) |
| `S10.4 INFO/PASS what=mirror_phase mode= error_samples= err_p50_m= err_p95_m= err_max_m= frame_ms_avg= commands_sent=` | one line per mode (A teleport 5 Hz, B walk 2 Hz and 4 Hz, C `SetPositionalOffset`); error is the actor's real position (FFI) vs the commanded track point 3 s late |
| `S10.4 FAIL what=SetPositionalOffset_error / mode_C_no_actor_id` | mode C could not run |
| `S10.5 PASS/INFO what=yaw_after_placement requested_deg= read_back_yaw= error_deg=`, `what=yaw_after_walk_end_with_rotation_90`, `INFO what=sequence_issued / emotion_issued / lookat_issued` | facing (criterion +-15 deg); gestures and emotes are tester observations |
| `S10.6 PASS/FAIL what=follower_replaced_in_new_room`, `PASS/FAIL what=interiors_despawning_cleanup actors_before= leftover=`, `INFO what=step_end interiors_despawning_seen=` | transitions and teardown |
| `S10.7 INFO what=conversation_started actor= conversation=`, `PASS what=next_section section= choiceparam=`, `INFO what=open_conversation_menu`, `what=menu_displayed mode=conv`, `what=conversation_finished outcome=`, `PASS/INFO/FAIL what=step_end conversations_started= message_chosen= wave_chosen= menu_chosen=` | S10.7. No `conversation_started` line = the custom handler did not receive the talk |
| `S10.8 MEASURE what=frame_time phase=baseline_0_actors ...` and `PASS/FAIL what=frame_time_delta baseline_ms_avg= with_8_actors_ms_avg= delta_ms=` | cost (criterion < 0.5 ms); frame time = interval between `onUpdate` calls |
| `S10.8 INFO what=actors_removed which=all removed=` / `run2_instruction_given` | run 1 strip / run 2 hand-over to the janitor |
| `S10.9 INFO what=station index= name= owner= canhavedynamicinterior= shadyguy= shady_tradesvisible= shady_room_type= rooms= dynamic_rooms= dynamic_types=` | interior catalogue (`dynamic_types` = which room types the game generated, "modded rooms" evidence) |
| `S10.11 INFO what=lounge_create_begin variant= corridor_macro= doors= first_door= rooms_before=`, `PASS what=lounge_created variant= interior_name= room_macro= room_type= room_x/y/z_in_station= rooms_after=`, `FAIL what=lounge_create_failed` (look for an engine error just before it), `PASS/FAIL what=summary variants_created=` | S10.11 |
| `S10.12 PASS/FAIL what=lounge_go moved= player_in_lounge= x/y/z=`, `what=lounge_leave back_in_return_room=`, `INFO what=menu_go_to_lounge_clicked / menu_leave_clicked`, `what=OpenMenu_called` | S10.12 / S10.14 / S10.15 teleports (transporter listing and door destination are tester notes) |
| `S10.13 INFO what=lounge_slot index= x= y= z= ischair=` and `MEASURE what=lounge_slots_summary slots= checksum=` | slot offsets; equal `slots` and `checksum` across runs = deterministic |
| `S10.14 INFO what=step_end saves_to_make=lounge-outside,lounge-inside` | S10.14 (the disable-extension load is done by hand afterwards) |
| `S10.15 MEASURE what=lounge_summary mirror_mode= mirror_err_p95_m= frame_ms_avg= conversations_started=` and `PASS/FAIL what=actor_in_slot tag= slot_index=` | S10.15 |

Not implemented: S10.10 (two-player smoke test; needs the M3 build and two PCs). The L/XL bridge repeat of S10.3 is done by hand: stand on the bridge and run `/x4mpspike onfoot1 step=3`.
Deviation from the research table: S10.11 gets the corridor door from `get_room_definition ... doors=` for the entertainment corridor group (seeded; contains `room_arg_corridor_04_macro`) instead of naming a fixed corridor macro, because a door name is only available through that action.
