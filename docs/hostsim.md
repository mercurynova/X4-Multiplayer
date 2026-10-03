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
(from `game_version`, default 9.00), `GetBuildVersionSuffix` (default `611726`), `GetPlayerID`, `AddPlayerMoney`. Every
other game function is NULL. Game functions called off the script thread, or a subscriber that throws, fail the run.
"Main thread" = the thread that runs the script.

## Script language (one command per line; `#` comments; `${NAME}` from `--var`, plus `${work}` and `${save_dir}`)
If no `init` line exists the DLL is initialised before the first command. `set` lines that must precede init need an explicit `init`.

| Command | Meaning |
|---|---|
| `init` / `shutdown` | LoadLibrary + `x4native_init` (must return 0) / `x4native_shutdown` + FreeLibrary |
| `reload` | shutdown + FreeLibrary + LoadLibrary + init, **stash kept** (subscriptions dropped like X4Native does) |
| `restart` | same but the stash is cleared (game restart) |
| `frame <N>Hz <count>` / `frame fast <count>` | fire `on_native_frame_update` (X4NativeFrameUpdate) + `on_frame_update` per frame, real-time paced / unpaced; game time advances unless paused; prints callback p50/p95/max |
| `lua <event> <payload...>` | Lua->native event: raised under the cpp name the mod registered with `register_lua_bridge`; fails if the mod never bridged it. `lua-raw` skips that check. Payload text is passed as the `const char*` data |
| `load_save [name]` | `on_game_loaded` then `on_universe_ready` |
| `save` / `ui_reload` / `fire <event> [text]` | `on_game_save` / `on_ui_reload` / any event |
| `set <key> <value>` | `game_version`, `build_suffix`, `game_build`, `save_dir`, `paused`, `save_list_complete`, `save_valid`, `game_time`, `speed`, `ext_id` (before init), `setting.<key>` |
| `expect-lua <topic> [timeout_ms] [contains <text> \| json <path> [op value]]` | waits for a captured `raise_lua_event`, consumes it (default 10 s) |
| `expect-no-lua <topic> [window_ms]` | no such event within the window |
| `drop-lua [topic]` | discard captured events |
| `expect-log <text> [timeout=<ms>]` / `expect-no-log <text>` | text in the host-side log (what the mod passed to `api.log`) |
| `expect-file <path> <text>` | text in a file (relative to the work dir), 3 s retry |
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

## M2-X3 mod refusal scenario (not in CI)
`mod/tests/hostsim/mod_refusal_run.ps1` starts the published server, a FakeNode authority with `mod_refusal_authority.json` (two DLC and
four mods), gives one mod a Nexus URL through `PUT /api/v1/mods/entries/{id}`, and runs `mod_refusal.hostsim`: the DLL reports a mod list
that differs in all four ways (install 2, enable 1, disable 1, update 1) and the script checks `x4mp.mod_refusal` and `reject == mod`.
Ports 47968-47970, about 10 s after the publish. It blocks on child output (`Get-Content -Wait` in a job), no sleeping.
