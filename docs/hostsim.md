# x4mp-hostsim: the fake X4Native host (M2-01)

`mod/tools/hostsim/**` builds `x4mp-hostsim.exe` (CMake block `X4MP_HOSTSIM` in `mod/CMakeLists.txt`, built with the
normal mod build). It `LoadLibrary`s a real X4Native extension DLL (`x4mp.dll`), hands it a fake `X4NativeAPI` and
drives lifecycle events from a script. Scenarios live in `mod/tests/hostsim/*.hostsim`.

```
x4mp-hostsim --dll <x4mp.dll> --script <file.hostsim> [--var K=V]... [--work-dir D] [--ext-id x4mp]
  [--admin-url http://127.0.0.1:47952 --admin-user admin --admin-password P]
  [--server-pid N] [--server-exe EXE --server-arg A... --server-log F --server-env K=V...]
  [--stash-dump F] [--timeout-scale F] [--max-seconds N]
```
Exit 0 = pass, 1 = `HOSTSIM FAIL line N: <cmd>: <reason>`, 2 = bad args/script. Build: `mod/build.ps1`; run the CI step:
`tools/e2e.ps1 -Steps HostSim` (ports 47950-47952).

## What the fake host provides
subscribe/unsubscribe/raise_event, `register_lua_bridge`, `raise_lua_event` (captured; off-main-thread call = violation),
stash in host memory (survives shutdown, FreeLibrary and reload; `restart` clears it), settings, log (`api.log` and
`_ext_log_fn`; the mod's own file log lands in `<work>/extension/logs/x4mp.log`, an empty `x4mp.portable` is written
there), `get_game_function` + a zeroed `X4GameFunctions` with fakes for `GetCurrentGameTime`, `GetSaveFolderPath`
(`<work>/saves/`), `IsSaveListLoadingComplete`, `IsSaveValid`, `IsGamePaused`, `ReloadSaveList`, `GetGameVersion`
(from `game_version`, default 9.00), `GetBuildVersionSuffix` (default `611726`), `GetPlayerID`, `AddPlayerMoney` and, since M3-04, the fake-universe exports (below). Every
other game function is NULL. Game functions called off the script thread, or a subscriber that throws, fail the run.
"Main thread" = the thread that runs the script.

## Script language (one command per line; `#` comments; `${NAME}` from `--var`, plus `${work}` and `${save_dir}`)
If no `init` line exists the DLL is initialised before the first command. `set` lines that must precede init need an explicit `init`.

| Command | Meaning |
|---|---|
| `init` / `shutdown` | LoadLibrary + `x4native_init` (must return 0) / `x4native_shutdown` + FreeLibrary |
| `reload` | shutdown + FreeLibrary + LoadLibrary + init, **stash kept** (subscriptions dropped like X4Native does) |
| `restart` | same but the stash is cleared (game restart) |
| `reloadui` | `reload` + `on_game_loaded` right after init: what /reloadui really does (X4Native replays it; the mod then treats the universe as ready again and, on a client, adopts its ghosts from the stash). Plain `reload` keeps the old no-replay behaviour (the join flows) |
| `frame <N>Hz <count>` / `frame fast <count>` | fire `on_native_frame_update` (X4NativeFrameUpdate) + `on_frame_update` per frame, real-time paced / unpaced; game time advances unless paused; prints callback p50/p95/max |
| `lua <event> <payload...>` | Lua->native event: raised under the cpp name the mod registered with `register_lua_bridge`; fails if the mod never bridged it. `lua-raw` skips that check. Payload text is passed as the `const char*` data |
| `load_save [name]` | `on_game_loaded` then `on_universe_ready` |
| `save` / `ui_reload` / `fire <event> [text]` | `on_game_save` / `on_ui_reload` / any event |
| `set <key> <value>` | `game_version`, `build_suffix`, `game_build`, `save_dir`, `paused`, `save_list_complete`, `save_valid`, `game_time`, `speed`, `ext_id` (before init), `setting.<key>` |
| `expect-lua <topic> [timeout_ms] [contains <text> \| json <path> [op value]]` | waits for a captured `raise_lua_event`, consumes it (default 10 s) |
| `expect-no-lua <topic> [window_ms]` | no such event within the window |
| `drop-lua [topic]` | discard captured events |
| `expect-log <text> [timeout=<ms>]` / `expect-no-log <text>` | text in the host-side log (what the mod passed to `api.log`) |
| `expect-file <path> <text> [timeout=<ms>]` | text in a file (relative to the work dir, or absolute), 3 s retry (scaled); `timeout=0` = one look (use it inside `until`, see "Two-DLL pair run") |
| `expect-file-order <path> <a> >> <b> >> <c> [timeout=<ms>]` | the file holds the texts in this order (each after the end of the previous one), 3 s retry (M3-23) |
| `write-file <path> <text>` | creates/overwrites a file (relative to the work dir; parent folders are created), e.g. `extension/launch.json` before `init` (M2-12) |
| `expect-no-file <path>` | the file must not exist (3 s retry), e.g. a consumed `launch.json` |
| `expect-state <name> <op> <value>` | `paused game_time reload_save_list_calls money_delta reloads frames stash_count hooks lua_pending last_shutdown_ms subs.<event>`; ops `== != < <= > >=` |
| `expect-sub <event> [min]`, `expect-bridge <lua_event>` | the mod subscribed / bridged |
| `expect-stash <key> present\|absent\|== <text>` | key is `<ns>/<key>` or just `<key>` (default namespace = ext id) |
| `expect-no-secret <text> [dir...]` | the text appears nowhere in log, Lua events, stash, or any file under the work dir (and extra dirs) |
| `expect-admin <path> [<jsonpath> [op value]] [timeout=<ms>]` | GET against the admin REST API (logs in, retries until the timeout). ops: `exists absent == != < <= > >= contains` |
| `kill-server` / `start-server` | terminate `--server-pid` / the started one; relaunch from `--server-exe/--server-arg/--server-env`, waits for `/healthz` |
| `stash-set <key> <text>` / `stash-remove <key>` | write / drop one stash key (default namespace = ext id); for corrupt-stash scenarios (M2-07) |
| `print <text>`, `settle <ms>` | note / fixed pause (avoid; use expects) |
| `exec <command line>` | runs the command through the shell and waits; a non-zero exit fails the script. For kit scripts that edit a config file while the DLL runs (the session-2 dry run). `${NAME}` is substituted like everywhere else |

### Fake universe commands (M3-04; `mod/tools/hostsim/world.*`)
Behind the M3 game exports hostsim keeps a tiny universe: objects (macro, owner, sector, pose, active, forced radar, wrecked,
name, id code), sectors (`100001`, `100002` exist), the dock station (300001), the player ship (200001) with seat / docked / SETA
state, and the counters `expect-state` reads. The mod's calls (`SpawnObjectAtPos2`, `SetObjectSectorPos`,
`GetObjectPositionInSector`, `ActivateObject`, `SetComponentOwner`, `SetObjectForcedRadarVisible`, `IsComponentWrecked`,
`GetObjectIDCode`, `GetComponentName`, `TeleportPlayerTo`, `CanTeleportPlayerTo`, `IsSetaActive`,
`IsPlayerOccupiedShipDocked`, `GetPlayerOccupiedShipID`, `GetPlayerControlledShipID`, `GetPlayerObjectID`,
`GetPlayerContainerID`, `GetContextByClass`, `IsValidComponent`) change or read it. Nothing moves by itself except the scripted
player-ship path during `frame` (game time, paused when `set paused 1`). Objects the mod spawns get ids from 400001.
Selectors: `<id>`, `last` (newest object, spawned by the mod or by `world object add`), `player` (the player ship), `station`,
`macro=<m>`, `owner=<o>`, `name=<n>` (newest match; a value with spaces needs the whole token quoted: `"name=[MP] Pia"`).

| Command | Meaning |
|---|---|
| `seat on\|off` | the player sits in / leaves the pilot seat (`GetPlayerOccupiedShipID`/`ControlledShipID` are 0 when off) |
| `dock [on\|off]`, `undock` | `IsPlayerOccupiedShipDocked`; docking stops the path and moves the ship to the station's sector |
| `highway on\|off` | `GetContextByClass(ship, "highway")` answers a highway (M3-09) |
| `seta on\|off` | `IsSetaActive` (it does not change `speed`: combine with `set speed 5`) |
| `ship path circle radius=R speed=S [center=x,y,z] [sector=ID]` | the player ship flies a circle in the x-z plane (m, m/s); heading = tangent in RADIANS (0 = +z, pi/2 = +x; S13.4: the game gives radians) |
| `ship path line from=x,y,z to=x,y,z speed=S [sector=ID] [loop]` | straight line; stops at `to` (restarts with `loop`) |
| `ship path gate from=.. to=.. speed=S sector=A to_sector=B [exit=x,y,z]` | line, then the ship appears in sector B at `exit` (default `to`); counts `gate_jumps` |
| `ship path stop`, `ship place sector=ID pos=x,y,z [yaw=deg]` | stop the path / put the ship somewhere at once |
| `world sector add <id>` | a new sector id |
| `world object add macro=M sector=S [owner=O] [pos=x,y,z] [name=N] [yaw=deg] [class=station]` | scripted placement (not counted as a mod spawn; prints the id) |
| `world object wreck\|unwreck\|remove <sel>`, `world object owner <sel> <faction>`, `world object name <sel> <text>` | edit; the player ship and the station cannot be removed |
| `world renumber` | a save load: every spawned / scripted object (not the player ship, the station, the sectors) gets a new id; positions, names, id codes stay (M3-11: the avatar binder test) |
| `world object pos <sel> x,y,z` | something pushed the object (a parked avatar is snapped back) |
| `world object push <sel> dx,dy,dz` | the player bumps it (M3-10: a ghost must be put back) |
| `world md-emulate on\|off` | M3-10: hostsim plays the MD / Lua half: `x4mp.ghost_dress` sets name + min hull + radar, `x4mp.ghost_velocity` stores the hint (`expect-object ... speed/velocity_hints`, filters `moving`/`hinted`/`minhull`), `x4mp.sector_map_collect` is answered from `world md-sectors`, `x4mp.teams_apply` with a matching `x4mp.teams_md` report (the team setup is Ok). Answers arrive at the start of the next `frame` |
| `world md-sectors <macro>=<id>,...` | the sector list the emulated MD answers the selfship feature's `x4mp.sector_map_collect` with (the wire index is the rank of the sorted macro) |
| `world factions <a>,<b>,...` | the list `GetAllFactions` returns (default: player, argon, paranid, xenon, x4mp_team_1..8) |
| `until <timeout_ms> <expect-... command>` | M3-10: repeats the expectation until it holds, running 200 ms of frames between tries (the mod acts on frames); fails with the last failure at the timeout |
| `repeat <n>` ... `end-repeat` | repeats the lines in between n times (not nested; `${var}` allowed in n); errors keep the original line numbers |
| `world spawn-fail <n>` | the next n `SpawnObjectAtPos2` calls return 0 |
| `world teleport allow\|deny [reason]`, `world controlled-when-docked on\|off` | behaviour switches (assumptions: m3-plan section 8, M3-04) |
| `expect-object <sel> exists\|absent` | the object exists / does not |
| `expect-object <sel> <field> <op> <value>` | fields `id cls macro owner name idcode sector x y z yaw pitch roll active radar wrecked min_hull speed vx vy vz velocity_hints`; ops as `expect-admin` (`== != < <= > >= contains`); bools compare as `true` / `false` |
| `expect-object count <op> <n> [macro=M] [owner=O] [sector=S] [name=N] [moving=0\|1] [hinted=0\|1] [minhull=P] [active=0\|1] [radar=0\|1]` | number of objects (the player ship and station included) matching the filters |
| `expect-ghost <player> err_p50\|err_p95\|err_max\|samples <op> <value> [timeout=<ms>]` | M3-10, **real**: reads the `[sync] player=<name> ...` lines the ghost feature logs every 5 s (path error = rendered position vs the sender's own samples at the same server time). `err_*` = the worst of the LAST THREE full windows (>= 60 rendered frames), `samples` = rendered frames summed over all windows. Retries until the timeout (default 5 s, scaled) but runs NO frames while waiting: wrap it as `until <ms> expect-ghost ... timeout=0`. No `[sync]` line yet fails an `err_*` check (no more stub). `ghost-sample` values take precedence when present |
| `ghost-sample <player> <metres>` | add one ghost error sample (the ghost feature's test hook and the DLL-free smoke use it) |

`expect-state` also reads `dress_events velocity_events teams_applies objects spawns set_pos_calls teleports owner_calls activate_calls radar_calls removed seat docked seta
path_active gate_jumps`. Checks run on the script thread right where they stand, so `frame` first (the mod acts on frames).
The DLL-free smoke is ctest `hostsim.world_objects`, `hostsim.world_ship` (the stub extension drives the fake through the real
SDK function table) and `hostsim.world_failure_exit_code`.

The fake game also answers (resolved by name, not in the SDK table; M3-11) `GetNumAllFactions` / `GetAllFactions` (player, argon, paranid, `x4mp_team_1..8`) and
`GetNumAllFactionShips` / `GetAllFactionShips` (the ships of an owner), which the avatar binder and the janitor use, and (M3-12) `RemoveComponent` (drops the object; the player's current ship and the station are refused) so `game::safe_remove` works. `CanTeleportPlayerTo` answers `granted` when allowed, like the real game (S13.6).

### Two-DLL pair run (M3-14, CI step `HostSimM3`)
`mod/tests/hostsim/m3_pair_run.ps1` (ports 47930-47932, about 100 s; `./tools/e2e.ps1 -Steps Publish,HostSimM3`): **two real `x4mp.dll` instances in two hostsim processes**, the
authority `HostAlice` (`m3_authority.hostsim`, a session made from an uploaded save, the Lua/MD half emulated) and the client `Pia` (`m3_client.hostsim`), plus `-Bots` (default 6)
FakeNode bots (`--avatars --chat-echo --behavior wander`, joined while the client is still joining: the string-table regression test). The two scripts synchronise through marker
files in a shared folder (`write-file` / `until ... expect-file ... timeout=0`). It covers join + download + load + resume, the takeover (guard, host copy removed), 7 ghosts
(the host's ship and 6 bots: team faction, inert, radar, minimum hull), the avatars on the authority (7, driven from `PlayerState`), chat both ways with the bots' echo, a gate jump
(the authority's avatar of the client follows into sector 2), the GUI view (`stats.udpActive`, `stats.ghosts` on the dashboard API), the host ghost's path error from the mod's own
`[sync]` lines (< 10 m p95; the authority flies a circle at 250 m/s), a checkpoint whose manifest lists 7 avatars with a clean pre-save check, a "save load" (`world renumber` + reload:
7 avatar records restored and rebound, no second spawn, the client's ghosts stay), 3 client reloads (0 leaks, 0 duplicates), the client leaving (its avatar stays parked). Afterwards
`tools/session4/sync-report.ps1 -Strict` judges both mod logs (CI limits: mod frame p95 < 0.5 ms (product target 0.2), < 20 kB/s, < 12 log lines/s, path error and latency medians).
`-UseRunningServer -AdminPasswordFile <file> [-NodeAdminPasswordValue <pw>]` skips the server and session creation: the session-4 kit dry run (`session4_dry_run.ps1 -Part topology`)
uses it behind `start-server-lan.ps1`.
Hostsim additions of M3-14: `world md-emulate on` now also plays the authority's MD half (`x4mp.avatars_safepos` -> `P;seq;1;x;y;z`, `x4mp.avatars_dress` -> name / minimum hull /
radar and `D;seq;1;loadout:basic`); **`expect-file <path> <text> timeout=<ms>`** (default 3 s scaled): `until <ms> expect-file <path> <text> timeout=0` is a single look that
does not block the frame loop (a blocked authority stops sending its clock and `PlayerState`, and the other node sees seconds of lag: the first pair runs showed 5 s of ghost latency
until every wait inside `until` was frame-friendly: use `timeout=0` / `expect-lua ... 0` / `expect-admin ... timeout=0` inside `until`).

### Avatar run (M3-11, CI step `HostSimAvatars` since M3-14)
`mod/tests/hostsim/avatars_run.ps1` (ports 47944-47946, about 2 minutes; needs `mod/build.ps1` and `tools/e2e.ps1 -Steps Publish`): the real DLL is the authority of a
session from an uploaded save, FakeNode bots (`avatars_sim.ps1` starts them without waiting, so the authority keeps ticking) are the players. `avatars.hostsim` plays
the Lua/MD side and checks: avatar spawned under `x4mp_team_1`, inert, at the MD safe position (and at the wanted spot after the timeout when MD does not answer), early-game
dress request, driven from the bot's `PlayerState` with velocity hints, parked on leave, snapped back when pushed, the checkpoint manifest lists both avatars, and after
`world renumber` + reload both are bound again by idcode with no second spawn.
### Ghost runs (M3-10, CI step `HostSimGhosts`)
`mod/tests/hostsim/ghosts_run.ps1` (ports 47965-47967): the published server, a FakeNode authority on the synthetic galaxy file, two FakeNode bots
(`--avatars`, wandering; one stays, one leaves after 80 s) and ONE hostsim process with the real DLL as the client Pia (`ghosts_scenario.hostsim`).
It checks spawn + dress + inert + radar + min hull, the 5 Hz velocity hints, `expect-ghost` path error < 2 m from the mod's own `[sync]` lines,
a pushed ghost put back, the leaver relabelled `(offline)`, and N reloads (default 20) with 0 leaks / 0 duplicates. The server runs with
`Interest.NearRadiusM = 60000` so all bots stay in the 20 Hz Near tier. Why the bots are started first: the string table reaches a node only in
its join catch-up, and a bot that joins meanwhile is not in it (see m3-plan section 8, M3-10).

### Pair runs (M3-04, CI step `HostSimPair`)
`mod/tests/hostsim/pair_run.ps1` starts the published server, a FakeNode authority serving a dummy save and **two** hostsim
processes at once, each with its own work dir and its own `x4mp.dll` instance, running `pair_scenario.hostsim` (variables
`tcp`, `name`, `other`, `sync`; players Pia and Pax). Ports 47940-47942 (pair range 47940-47949), about 20 s after the publish:
```
./tools/e2e.ps1 -Steps Publish,HostSimPair      (PowerShell; or pair_run.ps1 -Scenario my_pair.hostsim directly)
```
The processes synchronise with `write-file ${sync}/<name>.done` + `expect-file ${sync}/<other>.done ok` and see each other
through `expect-admin /api/v1/players $[name==${other}].online == true`. They run with `--timeout-scale 5`.

Option `--pre-init-wexport NAME=VALUE` (repeatable): calls the DLL export `void NAME(const wchar_t*)` after every load,
before `x4native_init` (test-only seams of throwaway DLLs; the probe's `x4mp_probe_set_config_dir`).

## Session-2 dry run (not in CI)
`mod/tests/hostsim/probe_session2_dry_run.ps1` runs `x4mp_probe.dll` (built with `mod/build.ps1 -Spikes`) through
`mod/tests/hostsim/probe_session2.hostsim` against the published server and a FakeNode authority started by the real
`tools/session2/start-server.ps1`, on ports 47953-47955 and a temp tree (the kit scripts honour the test-only environment
variables `X4MP_S2_DOCS_ROOT` and `X4MP_S2_OUT_DIR`, so the real Documents folder is never touched). It also runs
`write-probe-config.ps1`, `run-block.ps1` and `collect-logs.ps1`. About 65 s; needs `tools/e2e.ps1 -Steps Publish` first:
```
powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\probe_session2_dry_run.ps1
```

JSON path: `$.a.b[0].c`, `list[name==Bob].state`, `.length` on arrays/objects/strings. Payloads of `lua` are not echoed
(they may hold a password).

## Writing a scenario for a new feature
1. Put `<name>.hostsim` in `mod/tests/hostsim/`; register an `add_test(NAME hostsim.<name> ...)` in your own block of
   `mod/CMakeLists.txt` for scenarios that need no server (copy `hostsim.real_dll`), `PASS_REGULAR_EXPRESSION "HOSTSIM OK"`.
2. Scenarios that need the server run in the e2e `HostSim` step: pass them with `-HostSimScript` or (M2-14 owns
   `tools/e2e.ps1`) ask for a step; the server, admin password, FakeNode authority and `--var` values are provided there.
3. Assert through `expect-lua` (what the mod tells Lua), `expect-state`, `expect-admin` (what the server saw) and
   `expect-file` on `extension/logs/x4mp.log`. Keep each scenario under ~30 s of wall time.

## M2-09 authority scenarios (not in CI)
`mod/tests/hostsim/authority_flow_run.ps1` (ports 47956-47958, ~80 s; needs `mod/build.ps1` and `tools/e2e.ps1 -Steps Publish`): the real DLL is the
authority of a session created from an uploaded save. `authority_flow.hostsim` plays the Lua/MD side (`lua-raw x4mp.auth_md ...`, `expect-lua x4mp.auth_save`,
`exec authority_sim.ps1 -Mode save` writes the checkpoint file the game would write, `-Mode post` presses "Save now"); `authority_running_save.hostsim` is the
"already runs the save" branch. `hostsim.authority_role` (ctest) checks the join role and password handling without a server.

## Session-3 kit dry run (not in CI)
`mod/tests/hostsim/session3_dry_run.ps1` (ports 47974-47976, about 3 minutes plus the one-time web build; needs `mod/build.ps1`; it publishes through the kit's own `Ensure-Published`) runs **every**
`tools/session3` script against a temp Documents folder (`X4MP_S2_DOCS_ROOT`), a temp `out\session3` (`X4MP_S3_OUT_DIR`) and a fake X4 install (`-X4Dir`), with the real `x4mp.dll` in hostsim:
`session3_client.hostsim` (join with a password against `start-fake-authority.ps1`, reload, self-test from the `write-config.ps1` file, server restart with the same command),
`session3_launch.hostsim` / `session3_launch_expired.hostsim` (files from `write-launch.ps1`), `session3_authority.hostsim` (real DLL as authority behind `start-fake-clients.ps1`, 3 FakeNode clients, "Request save now").
It also checks `find-password.ps1` (clean, planted UTF-16 and URL-quoted) and the contents of the `collect-logs.ps1` zip.

## Session-4 kit dry run (M3-14; `-Part kit` is CI step `Session4Kit`)
`mod/tests/hostsim/session4_dry_run.ps1 [-Part kit|topology|all]` (kit about 70 s; topology about 8 minutes; ports 47977-47979) runs every `tools/session4` script against a temp Documents
folder (`X4MP_S2_DOCS_ROOT`), a temp `out\session4` (`X4MP_S4_OUT_DIR`) and a fake X4 install (`-X4Dir`); the real Documents folder and X4 install are never touched.
- **kit** (no server): sitting 0 (install-spike / write-probe-config / run-block / collect-logs / extract-galaxy-dump / `-Restore`) and sittings 1-3: `-WhatIf` of every script and what it
  prints (wingman command lines, DLC list, join password "set", firewall commands), the DLC report file, `start-server-lan.ps1 -Check` (addresses, firewall status read-only,
  the printed commands, no rule created), `sync-report.ps1` PASS / FAIL on synthetic logs in the real formats (file and zip, `-Strict` exit codes), `collect-logs.ps1` in product mode
  (`x4mp-lines.txt`, `sync-report.txt`, the mod's own files, never `launch.json`), `savescan.ps1` on the synthetic fixtures, `install.ps1` / `uninstall.ps1` (refuses next to the sitting-0 kit,
  deploys, enables, removes, saves untouched), `make-client-kit.ps1` (contents, no secrets / pdb / saves / repo paths, and the **unzipped kit's own install.ps1 and collect-logs.ps1 run
  without the repo**).
- **topology** (needs `mod/build.ps1` and `tools/e2e.ps1 -Steps Publish`; the web GUI is built once through the kit's `Ensure-Published`): sitting 1 = `start-fake-authority.ps1 -Wingmen 2
  -FreshDownload` + the real DLL as `Tester` (`session4_client.hostsim`: takeover, `[MP] Host` + `Wing01` + `Wing02`, chat echo, reloads, adoption); sitting 2 = `start-fake-clients.ps1
  -Wingmen 2` + the real DLL as the authority `Tester` (`session4_authority.hostsim`: stand, sit, self-spawn, the script waits for the host ship before it starts the wingmen, 2 avatars,
  chat, checkpoint with 2 avatars, rebind after a save load); sitting 3 = `start-server-lan.ps1` (game port listens on all interfaces) + `m3_pair_run.ps1 -UseRunningServer` (two real DLLs + a bot).

## M2-X3 mod refusal scenario (not in CI)
`mod/tests/hostsim/mod_refusal_run.ps1` starts the published server, a FakeNode authority with `mod_refusal_authority.json` (two DLC and
four mods), gives one mod a Nexus URL through `PUT /api/v1/mods/entries/{id}`, and runs `mod_refusal.hostsim`: the DLL reports a mod list
that differs in all four ways (install 2, enable 1, disable 1, update 1) and the script checks `x4mp.mod_refusal` and `reject == mod`.
Ports 47968-47970, about 10 s after the publish. It blocks on child output (`Get-Content -Wait` in a job), no sleeping.
