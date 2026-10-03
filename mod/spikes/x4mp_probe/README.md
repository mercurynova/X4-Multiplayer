# x4mp_probe (M2-005): native probe for in-game session 2

Throwaway X4Native extension (DLL + tiny Lua/MD shim). Not shipped. Build: `mod\build.ps1 -Spikes` (CMake option
`X4MP_SPIKES`, default OFF; a plain build never sees it). The DLL ends up at
`mod\build\msvc-x64-<config>\x4mp_probe.dll`; this folder (content.xml, ui.xml, x4native.json, ui/, md/) is the extension, the DLL
goes to `native\x4mp_probe.dll` inside the installed copy. Tests: `build.ps1 -Spikes -Filter probe` (config, password-never-logged,
stub-host smoke that loads the real DLL).

Log: `<profile>\x4native\x4mp_probe.log` (X4Native per-extension log). Every line is
`[X4MP-PROBE] t=<ms since first init of this X4 process, QPC> tid=<thread id> <tag> key=value ...`.
Lua shim lines go to the X4 debug log as `[X4MP-PROBE-LUA] ...`.

## Config: `Documents\Egosoft\X4\x4mp\x4mp_probe.json`

Resolved with `SHGetKnownFolderPath`, never env vars. Read at init, re-read by a worker thread (1 s poll) when mtime or size
changes. All keys optional; wrong types keep the default (log names the key only, never a value).

| Key | Default | Meaning |
|---|---|---|
| `server` | `"127.0.0.1:47780"` | host or host:port; empty = no connection |
| `name` | `"Tester"` | player name |
| `password` | absent | session password; goes only to `core/session`; never logged, stashed or written |
| `connect` | `true` | false = do not start a session |
| `auto_load` | `true` | after the save download, ReloadSaveList / IsSaveListLoadingComplete / IsSaveValid / load, once per X4 process (stash `probe.autoload_done`) |
| `load_mode` | `"event"` | `event`: raise vanilla Lua event `loadSave` (falls back to the shim's `LoadGame` after 10 s); `lua`: shim calls `LoadGame` |
| `pin_module` | `false` | pin the DLL (`GetModuleHandleEx` PIN) at init and when it flips to true live |
| `hooks` | `true` | install `hook_after<GetCurrentGameTime>` and `hook_before<TriggerAutosave>` (read at init only) |
| `skip_autosave` | `false` | TriggerAutosave hook sets `skip_original` (live; `run-block.ps1 skip_autosave_on` / `skip_autosave_off`, used for C2) |
| `pause_on_ready` / `pause_seconds` | `true` / `20` | `Pause()` at `on_universe_ready`, poll `IsGamePaused` 1 Hz, `Unpause()` after N s |
| `allow_native_thread_calls` | `false` | allow game/Lua calls from `on_native_frame_update` when no `on_frame_update` ticks |
| `spike_block` + `spike_block_seq` | `""` / `0` | when `(seq, block)` differs from the last handled pair, run the block. `money` also runs the native money test; `reloadui` arms a 5 s UI reload; `pin_on` pins; any other name is raised as Lua event `x4mp_spike.run` with the block name as the string parameter. The pair present at startup counts as handled (stash) |
| `reloadui_after_s` | `0` | > 0 (set while running): `ExecuteDebugCommand("reloadui")` after N s, once; the probe then rewrites the key to 0. A value found at startup is ignored and reset |
| `save_test` | `""` | when the value changes to a non-empty name: `SaveGame(name)` through Lua with QPC before the call and at `on_game_save` |
| `money_test` | `false` | false -> true runs the native money test |

A key file `x4mp_probe_key.txt` (random player key, hex) is created next to the config so the player identity is stable.

## Log tags (what the session-2 analysis greps)

`init` (version, wall time, module base, `dll_image_inits`), `build` (raw `GetGameVersion` and `GetBuildVersionSuffix` values, logged at init and at the first UI frame: the build-check evidence), `stash` (round trip, init count, ms since previous shutdown, intent present),
`cfg`, `cb` (first 5 calls of every callback with tid, plus every new thread id seen; callbacks: x4native_init, x4native_shutdown,
on_frame_update, on_native_frame_update, on_game_loaded, on_game_started, on_universe_ready, on_game_save, on_ui_reload,
on_before_reload, md_changed_zone, md_changed_state, md_money_updated, hook_after_GetCurrentGameTime, hook_before_TriggerAutosave,
on_setting_changed, lua_reply), `threads` (every 10 s: counts and tid sets), `frame` (first UI / native frame), `life`
(lifecycle events with ms since init), `conn` (Session states, Welcome with `resumed` and `same_player_id`), `save` (SaveInfo,
download progress), `autoload`, `pause`, `savetest`, `autosave` (every TriggerAutosave call), `money`, `perf` (every 5 s:
`Session::poll` p50/p95/max in ms), `shutdown` (`unload_for_reload done ... join_ms=`), `pin`, `reloadui`, `spike`, `button`,
`lua`, `seh` (a guarded native call raised an exception), `mark`, `core` (the x4mp core logger).

## Lua shim and events

Native has no `lua_State*`, so: native -> Lua `x4mp_probe.cmd` (`verb;arg`: loadgame, pause, unpause, money_read, savegame,
reloadui) and `x4mp_probe.notify` (text -> MD notification); Lua -> native through `__X4NATIVE_API.raise_event`:
`x4mp_probe.reply`, `x4mp_probe.save_begin` (name; call `X4MP_Probe.markSaveBegin(name)` just before a `SaveGame` to get the
native QPC for C4) and `x4mp_probe.mark` (label). Native -> spike: `x4mp_spike.run` (param = block name).
X4Native settings row: toggle "X4MP probe: test button" (id `test_button`); a change shows the notification "probe button clicked".

## Safety notes

* Identity sent to the server: `mod_version` is the `ClientIdentity` default (equal to FakeNode's, which the server compares with the authority), `mod_build` is `x4mp_probe`; `tools/session2/start-server.ps1` sets `Net.ModBuildStrict=false` for that.
* Game calls (`GetSaveFolderPath`, `ReloadSaveList`, ...) run under an SEH guard: a crash is logged as `seh`, not fatal.
* With `hooks` on, the DLL pins itself at shutdown (a framework detour could otherwise point into freed code). That makes
  "was the DLL unloaded on reload?" unobservable; set `hooks:false` for a clean B4 reading.
* `ReloadSaveList`, `IsSaveListLoadingComplete`, `IsSaveValid`, `GetSaveFolderPath`, `IsGamePaused`, `AddPlayerMoney` are real X4.exe exports
  and are called natively. `Pause`, `Unpause`, `LoadGame`, `SaveGame`, `GetPlayerMoney`, `ExecuteDebugCommand` are Lua globals (shim).

## S13 blocks (M3-001, session-4 sitting 0)

Native half of spikes S13.1-S13.10 ([m3-plan.md](../../../docs/m3-plan.md) section 5). Sources: `src/s13.cpp`, `src/s13.h`; tests: `tests/s13_tests.cpp`
(`build.ps1 -Spikes -Filter probe`; a fake world behind the stub API loads every block). The Lua/MD half is `mod/spikes/x4mp_spike` (M3-002).

Start a block like any other: `spike_block` = the name (optionally with an argument after a space) and a bumped `spike_block_seq` in
`x4mp_probe.json`. `ghost_motion` takes its mode as `ghost_motion a` or `ghost_motion_a`. Names are case-insensitive. Only one block runs at
a time: starting another aborts the first (logged). Game calls run only from `on_frame_update`; nothing pauses or sleeps. Every removal goes
through a copy of the product guard (player ship, controlled ship, player, player object, container and the ship/station context of each are
never removed; the guard is re-collected right before every removal). Spawned objects are listed in
`Documents\Egosoft\X4\x4mp\x4mp_probe_s13_registry.json` (id + idcode; stale entries after a load are dropped, never removed by id alone).

| Block | Spike | What it does |
|---|---|---|
| `s13_check` | - | logs which native functions exist in the game table (also runs before the first block): `PASS native functions present=[..] MISSING=[none]` |
| `ghost_spawn` | S13.1 | removes previous ghosts (idempotent), spawns an S (`ghost_macro_s`) and an M (`ghost_macro_m`) ship with `SpawnObjectAtPos2` under `ghost_faction` (falls back to `ghost_fallback_faction` and logs it), `spawn_distance_m` ahead of the player's ship (S 100 m left, M 100 m right), `ActivateObject(false)`, logs id / idcode / name / class / owner / `GetNumOrders` / `pilot` (GetComponentData, `n/a` if unreadable), raises `x4mp.spike_dress`, then samples position drift for `drift_seconds` (60): `PASS drift ... max_drift_m` (< 1) |
| `ghost_motion a\|b\|c` | S13.2 | on the S ghost (spawned if missing): circle r=1000 m around the player's start position at 100 / 300 / 600 m/s and a 3 km/s line passing 1 km beside it, `motion_seconds` (20) each. a = per-frame `SetObjectSectorPos` interpolating 20 Hz keys one key behind; b = raw sets at 20 Hz; c = a + Lua event `x4mp.spike_velocity` at 5 Hz. Per segment: `PASS segment <name> ... SetObjectSectorPos cost n= p50= p95= max=` in us and frame dt in ms. The ghost is parked again at the end |
| `ghost_xsector` | S13.3 | moves the S ghost into another sector (`xsector_name` substring, else same cluster, else the first other sector from `GetSectorsByOwner`) at (2000,0,2000); logs sector/cluster/zone before, after 1/5/30 frames (`PASS context after`), holds `xsector_hold_seconds` (20) for the map check, moves back (`PASS return check`) |
| `sample` | S13.4 | `sample_hz` (20) for `sample_seconds` (120): one `sample k= t= sec= ref= pos= ang_raw=(yaw,pitch,roll) v= occ= ctl= cont= obj= dock= foot= hw= seta= pose_us=` line per sample, `sector change` lines, a summary with achieved Hz, call-cost p50/p95/max and an angle-unit guess. `ref` = which id gave the pose (occ, ctl, cont, obj); `foot` is a guess (no ship, player object set); `hw` is the id of the player's `highway` context |
| `seat` | S13.5 | `seat_seconds` (60): one `seat_edge` line whenever occupied / controlled / player object / container / player id changes |
| `takeover`, `takeover_docked` | S13.6 | **refuses** (`REFUSED`, nothing spawned) unless `GetLastSaveInfo` filename or name equals `scratch_slot`. Spawns a `player`-owned `takeover_macro` ship `takeover_distance_m` (300) from the player's reference ship, `CanTeleportPlayerTo` (raw text logged), `TeleportPlayerTo(ship, 1, 1, force=1)`, waits for 10 consecutive frames with occupied/controlled = the new ship, then guard-checked removal of the vacated original and a 30-frame settle check. No confirmation in 10 s = nothing removed. `takeover_docked` additionally needs the player docked |
| `persist_spawn` | S13.8 | refuses unless on the scratch slot; spawns a team-owned inert ship, writes `x4mp_probe_s13_persist.json` (idcode, pos, sector name, name, game time). Then save in game, reload |
| `persist_check` | S13.8 | after the reload: finds the ship by idcode (`GetAllFactionShips`), logs position delta (`PASS found by idcode ... delta_m` < 1), name, orders and, after 5 s, whether it moved (active) or not |
| `seta` | S13.9 | `seta_seconds` (120): logs `IsSetaActive` edges; on a rising edge raises `x4mp.spike_seta_off` and logs `PASS SETA went off N ms` or `FAIL ... still active 1 s` |
| `pause_move` | S13.10 | `pause_wait_seconds` (60): waits for game time to stop advancing (the Esc menu), then moves the S ghost +100 m/s each frame and reads it back: `pause detected`, `PASS pause ended ... ui_frames_during_pause= native_frames_during_pause=`, `PASS SetObjectSectorPos while paused ... err_m`. If UI frames do not tick while paused the first frame after logs a `WARN on_frame_update did not tick for N ms (native frames in that gap: M)` |
| `cleanup` | - | removes every object in the registry (guard-checked); the takeover ship the player now sits in is refused by the guard, by design |
| `s13_stop`, `s13_status` | - | abort the active block / show the active block and registry size |

Log lines: `[X4MP-PROBE] t=.. tid=.. s13 <S13.n> block=<name> <PASS|FAIL|INFO|WARN|REFUSED> key=value ...`. Missing native functions log
`FAIL MISSING native function=<name>` once and the block carries on or ends. All functions below exist in the vendored 9.00-611726 table.

Config keys (all optional, ints in metres/seconds): `scratch_slot` (""), `ghost_macro_s` (`ship_arg_s_fighter_01_a_macro`), `ghost_macro_m`
(`ship_arg_m_bomber_01_a_macro`), `ghost_faction` (`x4mp_team_2`), `ghost_fallback_faction` (`ownerless`), `takeover_macro`, `xsector_name`,
`spawn_distance_m` (1000), `takeover_distance_m` (300), `drift_seconds` (60), `motion_seconds` (20), `sample_seconds` (120), `sample_hz` (20),
`seat_seconds` (60), `seta_seconds` (120), `pause_wait_seconds` (60), `xsector_hold_seconds` (20), `pitch_sign` (1; flip to -1 if ghosts appear
above/below instead of ahead), `angles_in_radians` (true: `GetObjectPositionInSector` angles are radians, `SetObjectSectorPos` wants degrees, per x4n_math.h; the
`sample` summary guesses the unit from the raw ranges).

Native functions used: `SpawnObjectAtPos2`, `FindMacro`, `ActivateObject`, `SetObjectSectorPos`, `GetObjectPositionInSector`,
`GetPlayerOccupiedShipID`, `GetPlayerControlledShipID`, `GetPlayerObjectID`, `GetPlayerContainerID`, `GetPlayerID`, `GetContextByClass`,
`TeleportPlayerTo`, `CanTeleportPlayerTo`, `IsSetaActive`, `GetCurrentGameTime`, `IsGamePaused`, `IsPlayerOccupiedShipDocked`, `IsValidComponent`,
`RemoveComponent` (guard-checked only), `GetObjectIDCode`, `GetComponentName`, `GetComponentClass`, `GetOwnerDetails`, `GetNumOrders`,
`GetAllFactions`, `GetAllFactionShips`, `GetNumAllFactionShips`, `GetSectorsByOwner`, `GetLastSaveInfo`, plus `IsComponentOperational` and the framework's
`get_lua_property("GetComponentData", id, "pilot")`.

### Events for the spike MD/Lua (M3-002 implements the handlers; native -> Lua via `raise_lua_event`)

| Event | Payload | When |
|---|---|---|
| `x4mp.spike_dress` | `id\|name` (id = decimal UniverseID of the spawned ghost / persist ship) | right after each ghost / persist spawn: set name, minimum hull, radar |
| `x4mp.spike_velocity` | `id\|vx\|vy\|vz` (m/s, sector frame, 3 decimals) | `ghost_motion c`, every 200 ms |
| `x4mp.spike_seta_off` | `1` | `seta` block, on each rising edge of `IsSetaActive` |

Known limits: the probe still pins itself when `hooks` is on (session-2 behaviour; set `hooks:false` for sitting 0). `GetLastSaveInfo` is assumed
to describe the save that was last loaded or saved (the refusal logs the raw `filename|name`, so a wrong assumption refuses safely).
