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

## Sitting 1, Run 2 (first attempt, hooks on), 2026-10-02

- **Pinning breaks re-init.** With `hooks:true` the probe pins its DLL at shutdown. On the save load X4Native logged
  "UI reloaded — DLL re-initialized", but the probe never logged a new `x4native_init` and never reconnected: the server
  showed `detached: "ClientReload"`, then `left: ResumeGraceExpired` 60 s later. A pinned module is **not** re-initialised
  by X4Native, so hooks that force a pin are unusable across save loads. Consequence: product code must **not pin**,
  and any hook must be removable on shutdown (or not used). B6b (pin) is answered by this: do not pin.
- Run 2 was restarted with `hooks:false`; C2 used `saves2;native=0` (MD blocker only), C5 without the native
  `GetCurrentGameTime` hook.

## Sitting 1, Run 2 (hooks off), Part C, 2026-10-02

| Item | Result | Evidence (game log `x4mp_s2.log`, probe log) |
|---|---|---|
| C1 save wrapper + menu greying | **PASS**: the `ui` block captured the options config (`source=debug:displayOptions`); a menu save to a slot went through our `SaveGame` wrapper; with blocking on, `IsSavingPossible` returns false and the Esc-menu **Save Game** row is greyed | `SAVE INFO what=menu_hook tooltip=hooked save_row=hooked`, `SaveGame_called ... blocked=false`, `IsSavingPossible_called ... returned=false` |
| C2 autosave blocker (MD diff) | **PASS for the request path**: the vanilla `AutoSave_Request` was suppressed by our `md/notifications.xml` diff. No autosave appeared during the following flight and wait, but the log shows no natural autosave *attempt* in that window, so the natural sector-change path is unproven. Run with `native=0` (no `TriggerAutosave` hook, hooks off) | `autosave_suppressed_by_md_diff`, no new `md_event_game_saved` |
| C3 quicksave | **Bypasses the Lua wrapper** (as feared): a new Quicksave was written while blocking was on. The MD `game_saved` event still fires, so a client quicksave is **detectable** (agreed M2 behaviour: detect + report; hard block in M4) | `game_saved_event ... plausibly_via_wrapper=false` |
| C4 custom save name | **Saving by a custom name works** (`SaveGame("x4mp_s2test_1", ...)` wrote `x4mp_s2test_1.xml.gz`), but the file is **not shown in X4's Load Game list**, even sorted by Date. Loading by name works (B2), so the mod never relies on the list. The MD `game_saved` event fires ~30 ms after the call, **before the file is complete** (~33 MB written later): the upload must wait for the file to be complete (size stable / file closed), not for the event | `SAVE MEASURE what=saves4_timing to_game_saved_event_ms=29.9`, file on disk |
| C5 game clock | **PASS**: Lua `GetCurrentGameTime()` and MD `player.age` agree within 0.1 s over 229 samples; both stop while paused (Esc menu, save menu); after save+load the clock continues from the saved value. No SETA on the ship (not tested). `EntitySpawn.game_time` can come from either | `CLOCK MEASURE side=lua/md` |
| C6 money units | Native `AddPlayerMoney(100)` changes the balance by **1 credit** (native = **cents**); Lua `GetPlayerMoney()` returns **credits**; MD `player.money` is **cents** (800000000 = 8,000,000 Cr). Balance restored exactly | probe `money result ... delta_plus=1 delta_minus=-1 restored=true`; `MONEY INFO what=md_player_money player_money=800000000 credits=8000000` |
| C7 V12 object variables (retest) | **FAIL, confirmed**: `set_value name="$Ship.$x4mp_netid"` errors with "Failed to set component.{…}.$x4mp_netid" on a player ship and on a station (int, largeint and string alike); nothing is stored or survives a save. Vanilla only uses `$X.$var` on cues/tables, never on components. **Decision**: net ids live in an MD-side table keyed by component (e.g. `$X4MP.$NetIds.{$component}`), plus the native map; confirm that the table survives save/load in session 3 | `Error in MD cue ... Failed to set component`, `V12 FAIL what=verify_*` |
| C8 S9 gate activation (retest) | **FAIL / inconclusive**: the block picked an inactive gate with **no destination** (Turquoise Sea X, unknown sector); `set_object_active` left `isactive=0` both immediately and after save+load. A gate with a real destination was not tested. S9 is not an M2 item: rerun with a connected, locked gate later (the block should prefer gates whose `destination` exists) | `S9 INFO what=gate_before ... dest_sector=null`, `S9 FAIL what=gate_active_survived_reload isactive=0` |

## Sitting 1, Run 2, Part D (menus and UI), 2026-10-02

| Item | Result | Evidence / notes |
|---|---|---|
| D1 start-menu row (V20/R1) | **PASS**: the `ui` block captured the options-menu config via `require("debug")` on `displayOptions` and inserted "Multiplayer (X4MP test)" after "Play Timelines" (vanilla, no SirNukes/UIX); clicking it opened our window | `UI PASS what=row_state method=config_insert`, `ui_block_done source=debug:displayOptions` |
| D2 standalone menu over the start menu | **PASS with a bug to design around**: `OpenMenu` shows our window over the start menu (`is_startmenu=true`). **Closing it does not bring the start menu back** (Close button and Esc alike; the user had to Alt+F4). The real Join screen must reopen the options/start menu on close (or open as a child of it) | `standalone_displayed`, `standalone_close due_to=close/back` |
| D3 X4Native settings entry | **PASS (click arrives)**: the probe's setting is a toggle behind the **"…" button next to the extension** on Settings → Extensions; flipping it reaches the DLL (`on_setting_changed key=test_button`). The probe's MD `show_notification` did **not** appear (at least not in the menu): don't rely on it for feedback | probe `button setting changed key=test_button` |
| D4 password box (V22) | **PASS**: `textHidden` shows dots; only the length is logged (14), the text never appears in the log | `UI INFO what=password_check length=14` |
| D5 HUD (V23) | **Partial**: a layer-3 frame shows top-right, but opening any other menu (map, Esc) **closes it** and it does not come back by itself. Product HUD must re-show itself after menus close (or fall back to notifications) | `HUD INFO what=hud_closed` after each menu |
| D6 extension list (R7) | Logged; screenshot taken (Settings → Extensions shows id, version, date; DLCs `ego_dlc_*` 9.00, Workshop ids `ws_<id>`) | `extensions` block output |
| D7 web links (R8) | **Only Steam-hosted URLs open**: `OpenWebBrowser("https://steamcommunity.com/...workshop")` opened the default browser; Nexus `https://www.nexusmods.com/...` and `steam://store/...` did **nothing**, although `CanOpenWebBrowser()` was true and the call returned no error. X4 evidently allow-lists steamcommunity.com. Product: Workshop links can open; Nexus links must be shown as copyable URL text | `LINKS PASS what=OpenWebBrowser_called` for all three; user saw only the Workshop page |
| Pause interplay | After every load the user needed **Esc twice** to unpause. The probe's `Pause()` at universe ready and the user's Esc/menu pause interfere; at hold-over the game was already unpaused and the probe still called `Unpause()`. Product: pause only if not already paused, undo only our own pause, or don't pause at all | probe `pause hold over: Unpause() IsGamePaused_before=false` |
| Threads (B7 follow-up) | In Run 2 `on_frame_update` was seen on **two thread ids** (`tids=53904,57400`). Re-check before relying on "UI frame thread = one fixed thread"; the host's `assert_main_thread` should compare against the thread of the current `on_frame_update`, not one captured at init | probe `threads ... on_frame_update{... tids=53904,57400,}` |

## Sitting 1 verdict (2026-10-02): M2 wave 2 is unblocked

Decisions for the wave-2 briefs (m2-plan §4.1 B/C/D):
- **B (join/load/reload):** DLL is unloaded on every save load and `/reloadui`; stash survives; server resume makes
  it invisible. M2-07 = stash + resume token, no pinning, no `resume.json`. Never pin the DLL (a pinned DLL is not
  re-initialised). Hooks must be removable at shutdown or avoided.
- **B2:** load by name via vanilla `loadSave` works; downloaded `x4mp_*` names are not shown in the Load list (fine).
- **B3:** `Pause()`/`Unpause()` via Lua work, but interfere with the player's own Esc pause: pause only if not
  already paused and undo only our own pause (or skip pausing; it is harmless in M2).
- **B7:** game calls only from `on_frame_update`; `on_game_loaded` may arrive on the native thread (copy a flag only);
  `on_frame_update` was seen on two thread ids in one run, so the main-thread check compares with the current frame's
  thread.
- **Build check:** `GetBuildVersionSuffix` is empty; use X4Native's detected build / version string.
- **C (saves):** Lua wrapper + greyed Save row work; the MD autosave diff suppresses the autosave request; quicksave
  bypasses Lua but raises `game_saved` (detect + report, hard block M4). Custom save names save and load by name.
  `game_saved` fires before the file is complete: upload only once the file is complete.
- **Clock/money:** `GetCurrentGameTime()` == MD `player.age` (pauses stop both); native money = cents, Lua = credits,
  MD `player.money` = cents.
- **Object variables on components do not exist**: net ids in an MD table keyed by component (confirm persistence in
  session 3).
- **D (UI):** start-menu row insertion via `require("debug")` works without UIX/SirNukes; the standalone window opens
  over the start menu but must restore it on close; `textHidden` works; X4Native per-extension settings live behind
  the "…" button; HUD frames are closed by other menus (re-show after menus close); only Steam-hosted URLs open in the
  browser.
- **Not done in sitting 1:** D8 (SirNukes/UIX compatibility, optional), S9 with a connected gate. Sittings 2 (Part E)
  and 3 (Part F) feed later milestones.
