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
| `skip_autosave` | `false` | TriggerAutosave hook sets `skip_original` (live) |
| `pause_on_ready` / `pause_seconds` | `true` / `20` | `Pause()` at `on_universe_ready`, poll `IsGamePaused` 1 Hz, `Unpause()` after N s |
| `allow_native_thread_calls` | `false` | allow game/Lua calls from `on_native_frame_update` when no `on_frame_update` ticks |
| `spike_block` + `spike_block_seq` | `""` / `0` | when `(seq, block)` differs from the last handled pair, run the block. `money` also runs the native money test; `reloadui` arms a 5 s UI reload; `pin_on` pins; any other name is raised as Lua event `x4mp_spike.run` with the block name as the string parameter. The pair present at startup counts as handled (stash) |
| `reloadui_after_s` | `0` | > 0 (set while running): `ExecuteDebugCommand("reloadui")` after N s, once; the probe then rewrites the key to 0. A value found at startup is ignored and reset |
| `save_test` | `""` | when the value changes to a non-empty name: `SaveGame(name)` through Lua with QPC before the call and at `on_game_save` |
| `money_test` | `false` | false -> true runs the native money test |

A key file `x4mp_probe_key.txt` (random player key, hex) is created next to the config so the player identity is stable.

## Log tags (what the session-2 analysis greps)

`init` (version, wall time, module base, `dll_image_inits`), `stash` (round trip, init count, ms since previous shutdown, intent present),
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

* Game calls (`GetSaveFolderPath`, `ReloadSaveList`, ...) run under an SEH guard: a crash is logged as `seh`, not fatal.
* With `hooks` on, the DLL pins itself at shutdown (a framework detour could otherwise point into freed code). That makes
  "was the DLL unloaded on reload?" unobservable; set `hooks:false` for a clean B4 reading.
* `ReloadSaveList`, `IsSaveListLoadingComplete`, `IsSaveValid`, `GetSaveFolderPath`, `IsGamePaused`, `AddPlayerMoney` are real X4.exe exports
  and are called natively. `Pause`, `Unpause`, `LoadGame`, `SaveGame`, `GetPlayerMoney`, `ExecuteDebugCommand` are Lua globals (shim).
