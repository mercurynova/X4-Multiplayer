# In-game session 2: results

Script: [../in-game-session-2.md](../in-game-session-2.md). Build: X4 9.00 build 611726, X4Native v9.0.0-611726. One PC,
local server + FakeNode authority (`start-server.ps1`), real X4 as client with the `x4mp_probe` DLL.

## Sitting 1, Run 1 (hooks off), 2026-10-02

| Item | Result | Evidence (probe log `x4native\x4mp_probe\x4mp_probe.log`) |
|---|---|---|
| B1 DLL in the start menu | **PASS**: `on_frame_update` ticks in the start menu 1.6 s after init; joins the server from the start menu once the config exists | `frame first on_frame_update ms_since_init=1580` |
| B2 download + load by name | **PASS**: probe downloaded the session save and loaded it itself (`load_mode=event`, vanilla `loadSave`); `on_game_loaded` ~16 s and `on_universe_ready` ~33 s after the DLL re-init | `life event=on_game_loaded`, `on_universe_ready` |
| B3 pause at universe ready | **PASS**: `Pause()` via Lua holds for the full 20 s, also while the map is opened and closed; `Unpause()` works | `pause poll ... IsGamePaused=true` x19, `pause after Unpause() IsGamePaused=false` |
| B4 save load vs our DLL | **The DLL image is unloaded and reloaded on a save load** (`dll_image_inits=1` after the load, new module base). The X4Native **stash survives** (`survived_previous_shutdown=true`, re-init 49–51 ms after shutdown, `session_intent_present=true`) | `init ... dll_image_inits=1`, `stash ...` |
| B5 invisible to the server | **PASS**: server log shows `detached: "ClientReload"` then `resumed (baseline epoch E)`; probe: `Welcome ... resumed=true same_player_id=true` | server Logs page, probe `conn Welcome` |
| B6 `/reloadui` | **PASS**: player stays connected. `/reloadui` **also** unloads and re-inits the DLL (`dll_image_inits=1`, `init_count` +1), stash survives | `init ...`, `stash ... init_count=3` |
| B7 threads | `x4native_init`, `on_frame_update`, `on_ui_reload` run on the **UI thread**; `on_native_frame_update` runs on a **different thread**. **`on_game_loaded` was seen on the native-frame thread** after a save load (not the UI thread), but on the UI thread after `/reloadui` | `cb name=... tid=`, `threads ...` |
| Build check | `GetBuildVersionSuffix` returns an **empty string**; `GetGameVersion` = 9.0; X4Native reports "9.00 (build 900)" | `build where=init ... GetBuildVersionSuffix='' suffix_len=0` |

### Consequences for M2 (lead notes)

- **M2-07 reload survival**: the DLL is always unloaded on save load and on `/reloadui`, so the design is "stash +
  resume token", not "keep a live host" (pinning only if B6b is ever needed). The stash path works, and the server
  resume already makes the reload invisible. Fallback (c) `resume.json` is not needed.
- **M2-04 host**: `on_game_loaded` can arrive on the native-frame thread. The host must not touch game APIs or
  non-thread-safe state there: copy a flag and handle it on the next `on_frame_update`. Main thread = UI-frame thread
  (the M2-04 default) is confirmed.
- **Build check**: the suffix route gives nothing; use X4Native's detected build / `get_game_version()` or the
  pinned X4Native version instead, and keep "unknown build" as a warning. Decide in M2-06.
- **Probe log is truncated on every DLL init** (X4Native reopens the per-extension log), so a save load or
  `/reloadui` loses the earlier lines. The lead snapshotted the log during Run 2. The product logger (`core/log`) already
  writes its own file in append mode with a banner per init, so this only affects the probe.
- Usability: the probe waits silently when `x4mp_probe.json` is missing; the session kit should check the config
  exists before X4 starts.
