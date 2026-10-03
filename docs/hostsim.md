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
| `expect-state <name> <op> <value>` | `paused game_time reload_save_list_calls money_delta reloads frames stash_count hooks lua_pending last_shutdown_ms subs.<event>`; ops `== != < <= > >=` |
| `expect-sub <event> [min]`, `expect-bridge <lua_event>` | the mod subscribed / bridged |
| `expect-stash <key> present\|absent\|== <text>` | key is `<ns>/<key>` or just `<key>` (default namespace = ext id) |
| `expect-no-secret <text> [dir...]` | the text appears nowhere in log, Lua events, stash, or any file under the work dir (and extra dirs) |
| `expect-admin <path> [<jsonpath> [op value]] [timeout=<ms>]` | GET against the admin REST API (logs in, retries until the timeout). ops: `exists absent == != < <= > >= contains` |
| `kill-server` / `start-server` | terminate `--server-pid` / the started one; relaunch from `--server-exe/--server-arg/--server-env`, waits for `/healthz` |
| `print <text>`, `settle <ms>` | note / fixed pause (avoid; use expects) |

JSON path: `$.a.b[0].c`, `list[name==Bob].state`, `.length` on arrays/objects/strings. Payloads of `lua` are not echoed
(they may hold a password).

## Writing a scenario for a new feature
1. Put `<name>.hostsim` in `mod/tests/hostsim/`; register an `add_test(NAME hostsim.<name> ...)` in your own block of
   `mod/CMakeLists.txt` for scenarios that need no server (copy `hostsim.real_dll`), `PASS_REGULAR_EXPRESSION "HOSTSIM OK"`.
2. Scenarios that need the server run in the e2e `HostSim` step: pass them with `-HostSimScript` or (M2-14 owns
   `tools/e2e.ps1`) ask for a step; the server, admin password, FakeNode authority and `--var` values are provided there.
3. Assert through `expect-lua` (what the mod tells Lua), `expect-state`, `expect-admin` (what the server saw) and
   `expect-file` on `extension/logs/x4mp.log`. Keep each scenario under ~30 s of wall time.
