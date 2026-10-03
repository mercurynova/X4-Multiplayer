# M2 plan: the mod works inside real X4

Status: plan v1, 2026-10-02 (after M1 complete). Owner: lead (Opus). Implementers: Sonnet agents, one task each,
in a worktree ([execution-plan.md](execution-plan.md) §5, §6.1). In-game tester: the user.

Read with: [architecture.md](architecture.md) §4.3–4.5, §5.5, §7.2 (authoritative), [mod-design.md](mod-design.md)
§1, §2, §6, §7, §8.4–8.5, §9 "M2", [protocol.md](protocol.md) §4, §6.3–6.5, [roadmap.md](roadmap.md) §1 (M2 row),
§4 and §6.1, [decisions.md](decisions.md) Part 3, and the user test script
[in-game-session-2.md](in-game-session-2.md).

---

## 1. Goal, scope and non-goals

**Goal.** From the X4 start menu a player joins an X4MP server using only the in-game UI: connect, handshake,
download the session save in-band, load it, stay connected (heartbeat, reconnect, resume) and survive the X4Native
extension reload that a save load and `/reloadui` cause. A real X4 can also act as the **authority**: it loads the
session save (including one an admin uploaded in the GUI), makes and uploads checkpoints, and the session runs.

**In scope**

| Area | What M2 delivers |
|---|---|
| Mod host | `main.cpp` becomes a real host: lifecycle gates (`on_game_loaded` / `on_universe_ready`), frame loop with a measured budget, config from `x4mp.json` (never env), file log, `try/catch` around every callback, `PlayerGuard` + `SafeRemove` wired, main-thread assertion |
| Lua UI | `x4mp_bridge.lua` (JSON codec, verbs and topics), Multiplayer / Join / Status screens on the rendering interface, standalone menu first, embedded OptionsMenu entry after the session-2 verdict, HUD status widget or the notification fallback, `__X4MP_USER` (last address and name, never the password) |
| Client join | `core/session` on the frame loop; `SessionSaveInfo` → in-band download into `GetSaveFolderPath()` → `LoadGame` → paused at universe ready → `LoadStatus` / `ManifestReport` (count-only stub, real matching is M4) → `NodeReady` → unpause |
| Reload survival | Stash intent + universe epoch, `Disconnect{ClientReload}`, resume after re-init; whichever fallback session 2 selects (§4) |
| Authority (minimal) | Host role from the Join dialog (admin password), `GalaxyMetadata` from MD `find_sector` (V09), string table, `RequestSave` → `SaveJob` → upload of save + manifest **bound to its connection** (carry-over), `EntitySpawn` for the authority's own ship with **`game_time` always filled** (carry-over), loading an **admin-uploaded save** (carry-over), vanilla autosave blocked |
| Save control | Client saving blocked while connected (menu save, quicksave, autosave) per the S6 verdict; authority saves only via `SaveJob` |
| Mods phase 1, mod side | M2-X1 (Lua extension list), M2-X2 (`core/mods`, real `ClientHello.extension_list` + `extensions_hash`, carry-over), M2-X3 (grouped refusal with links) |
| Server | Authority download/load of a stored session save; read-through save catalog (the unreproduced 404 after upload); `<patches>` reader behind `/mods/save-requirements` (was 501); `LogForward` handling + self-test table on the Players page; live push of unknown-key mod refusals on the hub |
| Diagnostics | `/x4mp_selftest` PASS table (log + GUI), `NodeStats` every 2 s (FPS, frame ms, main-thread net ms, RTT, game time) |
| Launch | `launch.json` one-shot auto-connect (consumed and deleted) |
| Harness | `x4mp-hostsim` (fake X4Native host that loads the real `x4mp.dll` in CI), FakeNode `--save-file` (a real save as the authority checkpoint), Lua unit runner, session-2 and session-3 kits |

**Non-goals (explicit)**

- No world replication in game: no ghosts, no `WorldUpdate` streaming from the real authority beyond the one
  self-spawn smoke entity, no interest handling on the client (M3/M4).
- No manifest matching (M2 sends a count-only `ManifestReport`; M4 matches). No avatars, no `TeleportPlayerTo` (M3).
- No team faction activation in game, no relations applied in game (M3). Teams are still assigned by the server.
- No in-game economy (M5), no chat UI (M3; the chat wrapper code stays out of M2 except what the spike needs).
- No UDP realtime lane in the mod (M4). No HTTP save fallback in the mod (M6).
- No installer, launcher or Check Install (M6). Install stays the dev deploy script (`mod/tools/deploy.ps1`).
- On-foot (S10), diplomacy (S11) and HQ/research (S12) spikes run in session 2 but **their features** are M3b/M3c,
  M5/M6 and post-M5; M2 only records their verdicts.
- Only **one** X4 instance is needed (§6.4). The two-instance tests start at M3.

---

## 2. Carry-overs from M1 (roadmap "Wave K / M1 COMPLETE")

| Carry-over | Where it goes |
|---|---|
| Mod authority fills `EntitySpawn.game_time` | **M2-08** (encoder that cannot emit 0) + **M2-09** (in-game self-spawn); the session-2 time probe confirms the clock source |
| Mod authority binds upload jobs to their connection (mod-design §6.2) | **M2-08** (`core/authority/upload_job`, CI test against the real server) |
| The mod's real `extension_list` | **M2-X1** + **M2-X2** (= M2-03) |
| Admin-uploaded saves are not loaded by the authority | **M2-02** (server + FakeNode) + **M2-09** (mod) |
| `/mods/save-requirements` 501 (no `<patches>` reader) | **M2-02** |
| One unreproduced 404 creating a session right after a save upload (write-behind catalog) | **M2-02** (read-through) |
| Unknown-key mod refusals not pushed on the hub | **M2-13** |
| HTTPS / CSP headers (SRV §4.3) | deferred → **M6** (hardening; LAN/VPN only until then, Q14) |
| No GUI for API tokens / audit / user management | deferred → **M6** |
| Economy 30-min run should use settings that complete trades | deferred → **M5** (when the economy goes in game; fix FakeNode defaults/docs then) |
| `WorldUpdate` capture above 20 Hz gives verify errors | deferred → **M4** (authority streaming) |
| Sector frames lack owner/faction, `SectorDto` lacks extents | deferred → **M4** (the real galaxy feed lands with streaming; M2's `GalaxyMetadata` sends what the schema already has) |

---

## 3. Exit criteria

Each criterion names how it is verified: **CI** (GitHub Actions, no X4), **local** (lead runs a command, no X4),
**game** (the user, by script in session 3, `docs/in-game-session-3.md`, written by M2-14). "hostsim" is the fake
X4Native host from M2-01 that loads the real `x4mp.dll`.

| # | Criterion | Verified by |
|---|---|---|
| 1 | **Start-menu join, UI only.** Real X4 as client, FakeNode authority serving a real save (`--save-file`), save **not** present locally: Join dialog → connect → `Welcome` → in-band download with SHA-256 verify → `LoadGame` → paused at universe ready → `NodeReady` → unpaused. Server shows the node `InGame`; mod log has the phase sequence. | game; the same flow minus the real load in CI (hostsim e2e step) |
| 2 | **No leave/join across the save-load reload.** Between Join and `InGame`, the server log/events show `resumed=true` with the same `player_id` and **zero** `PlayerLeft`/`PlayerJoined` for that node. | game; CI (hostsim simulates shutdown + re-init with a shared stash) |
| 3 | **`/reloadui` while in game**: resumed within 2 s, UI entry points present again, no leave/join. | game; CI (hostsim) |
| 4 | **Reconnect.** Server killed and restarted while X4 is in game: HUD/status goes Reconnecting → Connected within 5 s after the server listens again; X4 keeps running. | game; CI (hostsim backoff test) |
| 5 | **30-minute connection.** No disconnects; mod perf log and `NodeStats` show main-thread net cost **p95 < 0.2 ms/frame**; RTT and FPS visible in the GUI. | game |
| 6 | **Build mismatch refused** with a clear message in the Join dialog (`GameVersionMismatch`, text names the supported build). | CI (hostsim with a forged build string); game (dev config override, debug build only) |
| 7 | **Mod mismatch refused** with the grouped install / enable / disable / update message and working links (or URL text, per R8). | game (blocked dummy extension); CI (hostsim with an extension fixture) |
| 8 | **Real extension list.** GUI Players → Mods shows the user's real extensions (ids, versions, enabled) and a stable hash; it matches X4 Settings → Extensions. | game; CI (Catch2 fixtures, M2-03) |
| 9 | **Authority from an admin-uploaded save.** Admin uploads a save and starts a session; real X4 joins as authority from the start menu, downloads, loads, answers `RequestSave{SessionStart}`; the `x4mp_<session>_<n>` checkpoint (save + manifest) is stored with `ghosts_cleaned=true`; session `Running`; `fakenode swarm --clients 3` joins and verifies that checkpoint's SHA-256. "Save now" in the GUI produces a second checkpoint. | game + FakeNode; CI covers the server side with a FakeNode authority (M2-02) |
| 10 | **Upload job bound to its connection.** A connection killed mid-upload resumes on the new connection; the old job never reads the new inbox or writes the new socket. | CI (M2-08 test against the real server, 50 iterations, 0 failures) |
| 11 | **`EntitySpawn.game_time` filled.** The encoder never emits 0 (unit test); in game, the server log shows the authority self-spawn with `game_time` within 1 s of `SessionState.game_time`. | CI + game |
| 12 | **Save control.** Client connected: Save menu entries disabled with our tooltip, quicksave makes no file, no autosave in 20 min. Authority: no vanilla autosave in 20 min; only `SaveJob` saves. (If session 2 shows quicksave cannot be blocked, this criterion becomes "detected and reported in the GUI", and the hard block moves to M4 with ADR.) | game |
| 13 | **Self-test** PASS table in `x4mp.log` and on the GUI Players page for that node. | game; CI (hostsim) |
| 14 | **Password never persisted.** A PowerShell search of `uidata.xml`, `x4mp.json`, every X4MP/X4Native log and the hostsim stash dump finds the test password **0** times. | game (script); CI (hostsim) |
| 15 | **Remembered fields.** Last address and name are pre-filled after an X4 restart. | game |
| 16 | **`launch.json`** auto-connects without UI and is deleted after use; an expired file is ignored and deleted. | game; CI (hostsim) |
| 17 | **No regressions.** CI green on both OSes: all M1 suites, `mod` job (incl. new Catch2 suites), Lua unit tests, the hostsim e2e step; `tools/e2e.ps1` full run locally. | CI + local |
| 18 | **Session-2 verdicts recorded.** Every item of [in-game-session-2.md](in-game-session-2.md) has a verdict in `decisions.md` Part 3 (or a session-2 results file), and any ADR a verdict changes is updated before wave 2 briefs are written. | lead |

Evidence goes into `docs/m2-exit-report.md` in the M1 style (command, key numbers, verdict, date).

---

## 4. Dependencies on in-game session 2

Session 2 ([in-game-session-2.md](in-game-session-2.md)) runs on builds from wave 0 (§5.1). Sitting 1 (parts A–D,
about 2 hours) is what M2 waits for; sittings 2 and 3 (diplomacy/HQ and on-foot) feed later milestones and do not
block M2.

### 4.1 Which task waits for which finding

| Finding (session-2 test id) | Blocks | Fallback design if the answer is bad |
|---|---|---|
| **B1** DLL loads in the start menu, `on_frame_update` ticks there | M2-05/06 start-menu join | Join only from the in-game pause menu: the player loads any save, joins, and the mod then loads the session save |
| **B4/B5** What the save-load reload does (V05, S5): order of `X4N_SHUTDOWN` / re-init, DLL unloaded or not, stash kept, net thread join time | **M2-07** design | (a) *Stash survives, join ≤ 200 ms:* the current design. (b) *Join slower or the DLL unloads with the thread still in `WSAPoll`:* `closesocket` first to wake the poll, then join; if still slow, **pin the module** (`GetModuleHandleEx(PIN)`) and keep one process-wide session host whose pointer lives in the stash, so the socket survives re-init and no reconnect happens. (c) *Stash wiped too:* write only `{host, port, session_id, resume_token, expires}` to `Documents\Egosoft\X4\x4mp\resume.json` (deleted on read, 120 s expiry, no password-derived data) and let the server accept a resume with a valid token + `player_key` without a fresh auth proof (small server change, protocol.md §4.3 note) |
| **B6** `/reloadui` path (same questions) | M2-07 | Same as above |
| **B3** Pause held at universe ready; does opening/closing a menu unpause? | M2-06 | Re-assert `Pause()` each frame while in Loading/Matching/CatchingUp, capped at 10 s; if pausing is impossible, run unpaused (harmless in M2: no world streaming) and record it as an M4 risk |
| **B2** `LoadGame` / `IsSaveValid` on a non-standard file name (`x4mp_<sha12>`), save list pickup after `ReloadSaveList` | M2-06 | Map the contract name (`local_file_name`) to a reserved local slot we create ourselves, recorded in `x4mp.json` with its SHA-256; never overwrite a file we did not create (hash check). protocol.md §6.4 then says the local name is advisory |
| **B7** Thread of each callback kind (V07) | M2-04 (`assert_main_thread` definition), later M4/M5 MD capture | Design already treats MD callbacks as off-thread (`md_ring`). If even Lua-bridged events or `X4N_EXTENSION` run on another thread than `on_frame_update`, "main thread" = the `on_frame_update` thread captured on the first frame, and all game calls move into the frame handler |
| **C1–C3** Save control (V06): are `SaveGame` / `IsSavingPossible` wrappable, does quicksave go through them, does the MD autosave diff work, does vanilla autosave call the `TriggerAutosave` export | **M2-10** (clients), **M2-09** (authority) | Note: `SaveGame` and `LoadGame` are **Lua globals, not X4.exe exports** (absent from the SDK's `x4_game_func_list.inc`), so the old "X4Native `hook_before` on the save export" fallback does not exist. Autosave: MD diff flag, else `hook_before<TriggerAutosave>` with `skip_original`. Menu save: Lua wrap. Quicksave: if it bypasses Lua, M2 detects it (`on_game_save` while a client) and reports it in the GUI; the hard block becomes an M4 requirement (ghosts only exist from M4) |
| **C4** `SaveGame("x4mp_…")` accepted; sync or async; time to `on_game_save`; file complete at that moment | M2-09 (`SaveJob`) | Wait for `on_game_save`; if it does not fire for Lua-initiated saves, poll the file (size and mtime stable for 1 s, gzip trailer readable, 60 s timeout). If custom names are refused, use the reserved-slot mapping from B2 |
| **C5** `GetCurrentGameTime()` behaviour: monotonic, paused with the game, continues across save/load, equal to MD `player.age` | M2-08/09 (`game_time` source for `EntitySpawn`, `SaveStarted`, `NodeStats`) | Use MD `player.age` sampled through the bridge once per frame (the wire field is documented as authority game seconds; only consistency with `WorldUpdate` matters) |
| **C6** Native money path units (`AddPlayerMoney` from C++, `TransferPlayerMoneyTo`, the typed `event_player_money_updated` value) | nothing in M2 (M5) | Convert at the edge as ADR-042 says; record the native units next to the V04 row |
| **D1–D3** Menu injection (V20/R1, R3–R6), standalone menu over the start menu, X4Native settings `button` | **M2-11** (embedded entry), M2-05 (standalone menu) | Probe chain UIX → `require("debug")` → row append → standalone. Note: the X4Native 9.0.0 API hands extensions **no `lua_State*`** (`x4native_extension.h`), so the "native `lua_getupvalue`" source of mod-design §7.2 is not available unless the probe finds another way; plan without it. If the standalone menu cannot open over the start menu: X4Native settings button, then `launch.json`/`x4mp.json` auto-connect, then the in-game route (B1 fallback) |
| **D4** Password edit box `textHidden` (V22) | M2-05 | Plain edit box plus a warning line; the value still lives only in a local until sent |
| **D5** HUD frame on layer 3 (V23) | M2-11 | Notifications only (`show_notification` / `show_help`) |
| **D6/D7** `GetExtensionList()` fields (R7, V28), `OpenWebBrowser` (R8, V29) | M2-03, M2-X3 | Native folder scan decides `source`; show URL text only |
| V12, S9, S10, S11, S12 | nothing in M2 | Feed ADR-010 / ADR-037 / ADR-046 / ADR-047 / ADR-048 per their research docs |

### 4.2 Ordering consequence

Wave 1 (§5.2) does not wait for session 2. Wave 2 briefs are written **after** sitting 1's verdicts are recorded,
so each brief names the chosen path (design or fallback) instead of asking the implementer to guess.

---

## 5. Waves and tasks

Conventions: ids `M2-001..` are pre-tasks that build the session-2 kit (throwaway or test-only), `M2-01..` are M2
product tasks, `M2-X1..X3` keep their roadmap §6.1 ids. Sizes as roadmap (S ≤ 1 day, M 2–3, L 4–6 developer days);
**time-box** is the agent wall-clock budget, after which the agent commits what exists and reports (execution-plan
§6.1). At most **4 agents at a time**. Every brief carries the §6.1 rules: no sleep-polling, run the suite in the
foreground with a long timeout or as a background run, state the cost of any retry loop.

**Shared-file rules for all of M2**

| Shared file / resource | Rule |
|---|---|
| `mod/CMakeLists.txt` | Append-only, one clearly delimited block per task; the lead resolves trivial merge conflicts |
| `mod/extension/x4mp/ui.xml`, `content.xml` | Created by M2-05 with **every** planned Lua file listed (stubs); later tasks fill their own file only |
| `mod/native/main.cpp` | Created as a thin host by M2-04 with a feature registry; later tasks add a feature file and one registry line |
| `mod/extension/x4mp/ui/x4mp_bridge.lua` | M2-05 creates; in wave 2 only M2-06 edits it (others add topics through their own file) |
| `tools/e2e.ps1`, `.github/workflows/ci.yml` | Only M2-01 in wave 1, only M2-14 later |
| SQLite migrations | Only M2-02 may add one (`0009_*`); M2-13 keeps its data in memory |
| `server/src/X4MP.Core/Session/MessagePolicy.cs`, `SaveService*` | M2-02 in wave 1, M2-13 in wave 2 (different waves) |
| Ports | C# tests use the shared `TestPorts` helper. C++/hostsim tests take ports from `-PortBase`; the e2e `HostSim` step uses **47950–47954** (the existing steps keep 47960–47972; load tests 47980/47981/47990). The user's sessions use the defaults 47780/47781/47790 |
| X4 install | Only `mod/tools/deploy.ps1` and the session kit's install script write into `<X4 install>\extensions\` (x4native, x4mp / x4mp_probe / x4mp_spike folders only). Nothing else in the X4 folder is touched; no agent runs them, the user does |

### 5.1 Wave 0: session-2 kit (before the user's session 2)

M2-001 goes first (it creates the spike v2 framework); M2-002/003/004 then run in parallel on their own files;
M2-005 and M2-006 run in parallel with them.

| ID | Title | Goal / content | Files | Acceptance | Size / time-box | Deps |
|---|---|---|---|---|---|---|
| **M2-001** | Spike v2 framework + UI block | Turn `mod/spikes/x4mp_spike` into v2: a **block registry** (blocks run on demand, not automatically), launcher via chat `/x4mpspike <block>` **and** via the probe's config watch (`x4mp_spike.run` Lua event from M2-005), session-1 log format. Block `ui`: V20/R1 `config` capture (UIX accessor, `require("debug")` over `displayOptions`/`createOptionsFrame`/`displayOption`, validation), row append, standalone menu opened over the start menu and in game, X4Native settings `button` row, V22 `textHidden` box (logs length only, never the text), V23 layer-3 frame, R7 `GetExtensionList()` + `GetModifiedBasegameUIFilesExtensions()` dump, R8 buttons for a Nexus https, a Workshop https and a `steam://` URL (`CanOpenWebBrowser` first). `ui.xml` lists the files of M2-002..004 as stubs | `mod/spikes/x4mp_spike/**`, `mod/spikes/README.md` | Installs with `install-spike.ps1`; luacheck clean; XML well-formed; README documents every log key; block runs twice without duplicate rows (idempotent on `/reloadui`) | M / 90 min | — |
| **M2-002** | Spike blocks `saves*`, `clock`, `money`, `v12`, `s9gate` | C1 wrap `SaveGame`/`IsSavingPossible` (log every call and caller), C2 MD diff on `md/notifications.xml` autosave branch with `global.$x4mp_noSave` toggled from the block, C3 log whether quicksave passed through the wrapper, C4 `SaveGame("x4mp_s2test_1", ...)` + `on_game_save` timing (Lua `GetCurRealTime`; native QPC comes from M2-005), C5 `GetCurrentGameTime()` vs MD `player.age` at 1 Hz through pause, SETA and save/reload, V12 object variable with the correct syntax (`set_value name="$ship.$x4mp_netid"`), S9 gate activation through MD on an inactive gate + save/reload persistence | `mod/spikes/x4mp_spike/md/x4mp_spike_saves.xml`, `ui/x4mp_spike_saves.lua`, `md/notifications.xml` (diff) | Same as M2-001; the diff validated against `x4-unpacked/libraries/md.xsd` + `diff.xsd` locally when `x4-unpacked/` exists | M / 75 min | M2-001 |
| **M2-003** | Spike block `onfoot` (S10) | S10.1–S10.15 exactly as [research/on-foot-presence.md](research/on-foot-presence.md) §6, mirror actor, recommended run order as sub-blocks (`onfoot1` = S10.1/3/4/11/12/14/7/8, `onfoot2` = the rest) | `md/x4mp_spike_onfoot.xml`, `ui/x4mp_spike_onfoot.lua` | Same as M2-001; actor janitor proven by a log line on load | L / 150 min | M2-001 |
| **M2-004** | Spike blocks `diplo` (S11) and `hq` (S12) | S11.1–S11.7 per [research/diplomacy.md](research/diplomacy.md) §7 (incl. `libraries/diplomacy.xml` diff with `x4mp_test_treaty`/`x4mp_test_action`), S12.1–S12.8 per [research/team-hq-research.md](research/team-hq-research.md) §6; each step its own sub-block so the user can run them one by one | `libraries/diplomacy.xml`, `md/x4mp_spike_diplo.xml`, `md/x4mp_spike_hq.xml`, `ui/x4mp_spike_diplo.lua`, `ui/x4mp_spike_hq.lua` | Same as M2-001 | L / 150 min | M2-001 |
| **M2-005** | Native probe extension `x4mp_probe` | Throwaway DLL (CMake option `X4MP_SPIKES`, default OFF, so CI is unchanged) linking `x4mp_core`, plus a tiny Lua/MD shim. Config only from `Documents\Egosoft\X4\x4mp\x4mp_probe.json` (resolved with `SHGetKnownFolderPath`), re-read when its mtime changes (worker thread). Logs: thread id of `X4N_EXTENSION`, `X4N_SHUTDOWN`, `on_frame_update`, `on_native_frame_update`, `on_game_loaded`, `on_universe_ready`, `on_game_save`, `on_ui_reload`, two frequent typed MD events, a `hook_after` on `GetCurrentGameTime` (first 5 calls each + counts). Lifecycle timestamps (QPC) and stash round-trip. Connects with `core/session` from the start menu (name, server, optional password from the file), auto-downloads the session save into `GetSaveFolderPath()`, then (if `auto_load`, once per X4 process, remembered in the stash) asks Lua to `ReloadSaveList`, waits for `IsSaveListLoadingComplete`, checks `IsSaveValid` and calls `LoadGame` (B2); `unload_for_reload()` on shutdown and resume on init; `pin_module` toggle (B4); `Pause()` at universe ready, `IsGamePaused` at 1 Hz for 20 s, then `Unpause()` (B3); QPC around a Lua-initiated `SaveGame` and `on_game_save` (C4); `hook_before<TriggerAutosave>` logging calls, optional `skip` (C2 fallback); native `AddPlayerMoney(+100)`/`(-100)` with Lua `GetPlayerMoney` read-back (C6); per-frame main-thread cost of `Session::poll` (p50/p95 every 5 s); optional `reloadui_after_s` using `ExecuteDebugCommand("reloadui")` if `/reloadui` cannot be typed; watch of `spike_block` → raises `x4mp_spike.run` | `mod/spikes/x4mp_probe/**`, one `X4MP_SPIKES` block in `mod/CMakeLists.txt`, `mod/build.ps1 -Spikes` switch | Builds with `-Spikes`; normal `build.ps1` and CI unchanged; a hostsim-free smoke: the DLL loads in a 20-line test exe with a stub `X4NativeAPI` and logs its init line; never logs the password (test) | M / 120 min | — |
| **M2-006** | Session-2 kit + FakeNode `--save-file` | FakeNode authority option `--save-file <path.xml.gz>`: uploads that file (verbatim, real SHA-256) as its checkpoint with an empty-station manifest; `inspect`/swarm clients accept it. Scripts in `tools/session2/`: `install.ps1` / `uninstall.ps1` (deploy x4native + `x4mp_probe` + `x4mp_spike` into `<X4 install>\extensions\`, unblock, never touch anything else, refuse if `x4mp\` is present), `start-server.ps1 -SaveName <name>` (published server with `--data-dir out\session2\data`, FakeNode authority with the chosen save copied from the save folder resolved via `[Environment]::GetFolderPath('MyDocuments')`, prints the GUI URL and where the admin password file is), `write-probe-config.ps1` (asks for name/server, writes `x4mp_probe.json` without a password unless asked), `run-block.ps1 <block>`, `collect-logs.ps1` (zips the X4 `-logfile`, the `x4native\` log folder, server and FakeNode logs into `out\session2\logs-<timestamp>.zip`; uploads nothing) | `tools/X4MP.FakeNode*/**`, `tools/session2/**`, `docs/fakenode.md` (option row) | FakeNode test: a 6 MB gzip fixture generated at test time round-trips with the same SHA-256; `--save-file` on a missing file exits with a clear error; scripts pass `-WhatIf`; no hard-coded user paths | M / 90 min | — |

**Contract with the script.** The block names, notification texts, window/button labels and script names used in
[in-game-session-2.md](in-game-session-2.md) are the contract for M2-001..006; a brief may rename one only if it
updates the script in the same commit. Block → owner:

| Blocks / commands | Owner |
|---|---|
| `ui` (start-menu row "Multiplayer (X4MP test)"), `ui_standalone` (test window with "Test password" field and "Check" button), `hud`, `extensions`, `links` | M2-001 |
| `saves1`, `saves1_block`, `saves2` (also calls `C.TriggerAutosave(true)` so the probe's hook can log it), `saves4`, `clock`, `v12`, `s9gate`, and the Lua side of `money` | M2-002 |
| `onfoot1`, `onfoot2` | M2-003 |
| `diplo1`..`diplo7`, `hq1`, `hq2`, `hq3`, `hq3d`, `hq4`..`hq8` | M2-004 |
| Native side of `money`; `run-block.ps1` commands `reloadui` and `pin_on`; the X4Native settings row "X4MP probe: test button" (notification "probe button clicked") | M2-005 |
| `install.ps1`, `uninstall.ps1` (also **lists**, never deletes, `x4mp_*.xml.gz` and test saves), `start-server.ps1` (tees FakeNode output to `out\session2\fakenode.log`), `write-probe-config.ps1`, `run-block.ps1`, `collect-logs.ps1` | M2-006 |

**Session 2 (user).** After wave 0 is merged, the lead builds (`mod/build.ps1 -Spikes`, `tools/e2e.ps1 -Steps Publish`)
and tells the user the kit is ready. The user runs sitting 1 (parts A–D) and sends the zip; the lead records verdicts
(criterion 18) and updates ADRs. Sittings 2 and 3 can follow at any time.

### 5.2 Wave 1: foundations (does not wait for session 2)

| ID | Title | Goal | Files / areas | Acceptance tests | Size / time-box | Parallel-safety | Deps |
|---|---|---|---|---|---|---|---|
| **M2-01** | `x4mp-hostsim`: fake X4Native host | An exe that `LoadLibrary`s the real `x4mp.dll`, hands it a fake `X4NativeAPI` (subscribe/raise, `raise_lua_event` capture, stash kept in host memory across shutdown/re-init, settings, log, `get_game_function` / a zeroed `X4GameFunctions` table with fakes for `GetCurrentGameTime`, `GetSaveFolderPath`, `IsSaveListLoadingComplete`, `IsSaveValid`, `IsGamePaused`, version), drives lifecycle events from a script (`frame 60Hz`, `lua x4mp.join {...}`, `load_save` → `on_game_loaded` + `on_universe_ready`, `reload` = shutdown + re-init, `kill-server`), and asserts on Lua topics and server state through the admin API. New `HostSim` step in `tools/e2e.ps1` and the `e2e-headless` CI job | `mod/tools/hostsim/**`, `mod/tests/hostsim/**` (scripts), `tools/e2e.ps1`, `.github/workflows/ci.yml` | Against today's M2-04 skeleton (or a stub DLL until it lands): loads, inits, ticks 600 frames, simulates a reload with stash kept, exits 0; the step runs in CI < 2 min | M / 120 min | Owns e2e.ps1 and ci.yml in wave 1; ports 47950–47954 | M0-08 |
| **M2-02** | Server: authority loads a stored session save | When a session is created from a stored save (admin upload or older checkpoint) and the authority's `ClientHello.loaded_save_sha256` differs, send it `SessionSaveInfo` and walk it through SyncingSave → Loading like a client; after its `NodeReady`, issue `RequestSave{SessionStart}` and accept that checkpoint as the first authority-made one (an uploaded save has no manifest). Save catalog read-through so `POST /sessions {saveId}` right after `complete` never 404s. `SavePatchesReader` behind `/mods/save-requirements` (first 64 KB, synthetic fixture). FakeNode authority honours the new path (`--expect-session-save`) | `server/src/X4MP.Core/Saves/**`, `Session/**` (authority join path), `X4MP.Server/Saves`, `X4MP.Persistence` (catalog read-through; migration `0009` only if unavoidable), `tools/X4MP.FakeNode*` | Live test: upload via REST → create + start session → FakeNode authority downloads, verifies, "loads", uploads SessionStart checkpoint → `Running` → 3 swarm clients verify; 200 × create-right-after-upload never 404s; `<patches>` fixture yields the expected requirements; existing M1 suites green | M / 120 min | Owns `MessagePolicy.cs`, `SaveService*`, migrations in wave 1 | — |
| **M2-03** (= M2-X2) | `core/mods`: real extension list | As roadmap §6.1 M2-X2: id → folder over install / user / Workshop roots, `content.xml` parse, DLL and `subst_*.cat` detection, class hint, content hash with cache, on a worker thread; implements `IExtensionProvider` for `core/session` | `mod/native/core/mods/**`, `mod/tests/test_mods*.cpp`, fixtures under `mod/tests/fixtures/mods/` (synthetic, no game files) | As roadmap: Catch2 over fixture folders; no frame-thread file I/O (asserted); hash stable; `ClientHello` waits ≤ 2 s then sends without hashes; allowlist excluded from the hash using the generated constants | M / 120 min | Own folder; CMake block | M1-X1 |
| **M2-04** | Mod host skeleton | `main.cpp` → `ModHost`: config (`core/config`, `x4mp.json` at init and `on_ui_reload`), file log with categories and secret redaction, lifecycle gates, frame loop with QPC budget + metrics, feature registry (`IFeature{on_init,on_frame,on_universe_ready,on_shutdown}`), `try/catch` + disable after 3 throws, `game/` adapter for the M2 exports (`GetCurrentGameTime`, `GetSaveFolderPath`, `IsSaveListLoadingComplete`, `IsSaveValid`, `ReloadSaveList`, `IsGamePaused`, version/build), `assert_main_thread` (definition per B7, defaults to the `on_frame_update` thread), `PlayerGuard` refresh + `SafeRemove` wired, supported-build check (refuse + reason) | `mod/native/main.cpp`, `mod/native/host/**`, `mod/native/game/**`, tests | Catch2 for budget, registry, exception containment and build check; hostsim smoke (once M2-01 lands); raw-remove guard still passes | M / 120 min | Owns `main.cpp`; CMake block | M1-N1..N3 |
| **M2-05** (+ M2-X1) | Lua bridge + standalone screens | `x4mp_bridge.lua` (≈150-line JSON codec, `_G.__X4NATIVE_API` retry on `gfx_ok`/`show`, verbs `join`, `disconnect`, `ui_ready`, `request_status`, `extensions`; topics `status`, `notify`, `error`, `load_save`), the rendering interface + **standalone** `X4MPMenu` implementation, Multiplayer / Join / Status screens (address, name, password `textHidden` per D4, team placeholder), `__X4MP_USER` (`lastAddress`, `lastName`, never the password), text page 92000; M2-X1 extension list gathered in the start menu and on `/reloadui`. A Lua unit runner (`mod/tests/lua/run.ps1` + `run.sh`, LuaJIT or Lua 5.1 from the CI image) with stubs for `Helper`/`C`/`RegisterEvent` | `mod/extension/x4mp/ui.xml`, `ui/**`, `t/0001-l044.xml`, `mod/tests/lua/**`, CI `mod-lint` job (runner) | Lua tests: codec round-trip incl. unicode and escapes, join-state validation, password cleared after send, no `__X4MP_USER` field holds it; luacheck clean; hostsim sees `x4mp.join` with the right JSON | M / 120 min | Owns all `mod/extension/x4mp/ui/*` in wave 1 | — |
| **M2-08** | `core/authority`: upload job + spawn encoder | `UploadJob` owned by one connection generation: on reconnect/resume the old job is cancelled and joined before the new one starts, and it can never read the new inbox or write the new socket (mod-design §6.2 lesson); resume offsets from `SaveUploadAccept`; 8-chunk window. `EntitySpawnBuilder` that requires `game_time` (constructor argument; asserts/refuses 0). A headless `--authority` mode in `x4mp-headless` that answers `RequestSave` with a file from disk | `mod/native/core/authority/**`, `mod/tools/headless/**`, tests | Catch2 unit tests; live test against the real server: 50 runs that kill the connection at random points of the upload, 0 failures, 0 cross-connection reads; encoder test that no path emits 0 | M / 120 min | Own folder; headless shared with nobody in wave 1 | M1-N3 |

Run order inside the 4-agent cap: M2-01, M2-02, M2-04, M2-05 first; M2-03 and M2-08 take the next free slots.

### 5.3 Wave 2: the mod in game (briefs written after session-2 sitting 1)

| ID | Title | Goal | Files / areas | Acceptance tests | Size / time-box | Parallel-safety | Deps |
|---|---|---|---|---|---|---|---|
| **M2-06** | Client join flow | `Session` driven from the frame loop; `x4mp.join` → `start()`; `x4mp.status` at ≤ 2 Hz; rejection codes → localized text (build, mod, auth, full, banned, name); `SessionSaveInfo` → download into `GetSaveFolderPath()` (B2 path or reserved-slot fallback) → `x4mp.load_save` → `LoadGame`; `LoadStatus` phases; pause at universe ready (B3 path); count-only `ManifestReport`; `NodeReady` with universe epoch; unpause | `mod/native/features/join/**`, `x4mp_bridge.lua`, `x4mp_menu.lua` (status rows), `md/x4mp_main.xml` | hostsim e2e: join → download → simulated load → `NodeReady` → server `InGame`; rejection texts for 4 codes; build mismatch (criterion 6) | L / 150 min | Owns `x4mp_bridge.lua`, `x4mp_menu.lua` in wave 2 | M2-01, M2-04, M2-05; B1–B3 |
| **M2-07** | Reload survival | Intent + epoch in the stash, `unload_for_reload()` in `X4N_SHUTDOWN` with the measured join budget, resume on init, epoch decides "new universe vs `/reloadui`", the fallback chosen from B4/B5/B6 (module pin + live host, or `resume.json` + server token resume) | `mod/native/features/resume/**`, `core/session` (small), server only if fallback (c) | hostsim: 20 reloads at random phases (start menu, mid-download, Loading, InGame) → always `resumed=true`, same `player_id`, no `PlayerLeft`; net-thread join time logged | M / 120 min | Touches `core/session` (no one else in wave 2) | M2-04, M2-06; B4–B6 |
| **M2-09** | Authority in game | Join dialog "Host this session (authority)" + admin password → `requested_roles = Authority\|Client`; on the authority: `GalaxyMetadata` from MD `find_sector` (`md/x4mp_galaxy.xml`, V09 recipe) + `StringTableAdd`; `RequestSave` → `SaveJob` (block vanilla autosave, `SaveGame` via Lua, `SaveStarted` with `game_time` per C5, wait per C4, empty manifest, `UploadJob` from M2-08); self-spawn `EntitySpawn` for the authority's own ship; the M2-02 "load the stored session save" path on the authority | `mod/native/features/authority/**`, `md/x4mp_galaxy.xml`, `ui/x4mp_menu.lua` (host option; after M2-06 merged) | hostsim + FakeNode: session from an uploaded save reaches `Running`, swarm of 3 clients verifies the checkpoint; criterion 11 game_time check on the server log | L / 150 min | Starts after M2-06 merges (menu file) | M2-02, M2-06, M2-08; C1–C5 |
| **M2-10** | Save control + self-test | Client: wrap `IsSavingPossible`/`SaveGame` (tooltip "Saving is disabled while connected as a client"), MD autosave diff (`md/notifications.xml`), `TriggerAutosave` hook if C2 says so, `on_game_save` while client → `LogForward` warning. Janitor skeleton at `on_universe_ready` (`[MP] ` prefix count, nothing to remove yet). `/x4mp_selftest` + `selftest=true`: X4Native resolve counts and hooks, supported build, adapter probe result, save-wrap installed, `PlayerGuard` contents, game-time sanity → PASS table in log and `LogForward` | `ui/x4mp_saves.lua`, `md/notifications.xml`, `mod/native/features/saves/**`, `features/selftest/**` | Lua tests for the wrappers (chaining, unwrap only if ours); hostsim: self-test table arrives at the server | M / 120 min | Own files | M2-04, M2-05; C1–C3 |
| **M2-11** | Embedded entry + HUD | `x4mp_optionsmenu_adapter.lua`: probe chain per D1–D3 (UIX → `require("debug")` → append → standalone), single log line `X4MP ui: optionsmenu adapter OK(source=…)\|DEGRADED(…)`, wrapper chaining and idempotence; `x4mp_hud.lua` per D5 or notification fallback | `ui/x4mp_optionsmenu_adapter.lua`, `ui/x4mp_hud.lua` | Lua tests with a vanilla-shaped and a SirNukes-shaped stub; idempotent on repeated load | M / 90 min | Own files | M2-05; D1–D5 |
| **M2-13** | Server: diagnostics in the GUI | Handle `LogForward` (rate-limited, into the server log with the node prefix); last self-test table per node in memory, shown on the Players page detail; push unknown-key mod refusals on the hub (carry-over) | `server/src/X4MP.Core/Session/**`, `X4MP.Server` hub/REST, `server/web/src/pages/players/**` | Unit + Playwright: a FakeNode `--selftest` sends a table that appears on the Players page within 2 s; an unknown-key refusal appears live | M / 90 min | Owns `MessagePolicy.cs` in wave 2 | M2-02 merged |

### 5.4 Wave 3: finishing and acceptance

| ID | Title | Goal | Files / areas | Acceptance tests | Size / time-box | Deps |
|---|---|---|---|---|---|---|
| **M2-X3** | Grouped mod refusal with links | As roadmap §6.1; links via `C.OpenWebBrowser` when `C.CanOpenWebBrowser()` and R8 allows, else URL text; Multiplayer screen lists the session's mod set when connected | `ui/x4mp_join_mods.lua`, `x4mp_menu.lua` | Lua tests for grouping; hostsim with a mismatch fixture shows four groups | M / 90 min | M2-06, M2-03; D7 |
| **M2-12** | `launch.json`, `NodeStats`, net cost | One-shot `launch.json` (consume, delete, expiry); `NodeStats` every 2 s (fps, frame_ms_p95, game_time, rtt, tx/rx, main-thread net ms p95); perf log line every 5 s | `mod/native/features/launch/**`, `features/stats/**` | hostsim: launch file connects without UI and is deleted; expired file ignored + deleted; `NodeStats` visible via the admin API | S / 60 min | M2-06 |
| **M2-14** | Session-3 kit + acceptance script + exit tooling | `docs/in-game-session-3.md` (criteria 1–9, 11–16 step by step, plus V21: load an authority checkpoint **without** the mod), `tools/session3/*.ps1` (start server + FakeNode authority with `--save-file`, upload a save through the REST API for criterion 9, `find-password.ps1` for criterion 14, `collect-logs.ps1`), e2e/ci updates for the final hostsim scenarios | `docs/in-game-session-3.md`, `tools/session3/**`, `tools/e2e.ps1`, `ci.yml` | Lead dry-runs every script without X4 (`-WhatIf` / against hostsim) | M / 90 min | M2-06..M2-13 |

Then: the user runs session 3; the lead writes `docs/m2-exit-report.md` and marks M2 complete in roadmap.md.

**Critical path:** M2-005/006 → session 2 sitting 1 → M2-06 → M2-07 → M2-09 → M2-14 → session 3. Wave 1 runs
during the wait for session 2.

---

## 6. Testing strategy

### 6.1 Stays in CI (no X4)

- All M1 suites (Protocol, Core, FakeNode, Persistence, Server, web, Playwright, swarm, headless C++ client).
- Catch2 for every new `core/` module (mods, authority) and the host pieces that do not need the game.
- `x4mp-hostsim` e2e step (Windows, `e2e-headless` job): the **real DLL** against the **real server** and a FakeNode
  authority: join, download, simulated load, reload with stash kept, `/reloadui`, server kill/restart, build and mod
  mismatch, `launch.json`, self-test delivery, password search over everything it wrote.
- Lua unit runner on the `mod-lint` job: codec, join-state logic, save wrappers, adapter probe logic against stubs.
- XML well-formedness (existing). XSD validation of MD/diff files runs **locally only** (the XSDs are Egosoft data and
  are not committed).

### 6.2 New harness pieces

| Piece | Task | Why |
|---|---|---|
| `x4mp-hostsim` | M2-01 | The only way to run `x4mp.dll` end to end without X4; turns most game-only checks into CI checks |
| FakeNode `--save-file` | M2-006 | A real X4 client needs a **real** save from the authority; with one X4 copy the authority must be FakeNode |
| FakeNode `--expect-session-save`, `--selftest` | M2-02, M2-13 | Server paths for the authority loading a stored save, and the GUI self-test view |
| `x4mp-headless --authority` | M2-08 | Upload-job reconnect tests against the real server from C++ |
| Lua unit runner | M2-05 | Lua is most of the UI; luacheck alone does not test behaviour |
| `tools/session2/`, `tools/session3/` | M2-006, M2-14 | The user starts everything with one command and sends back one zip |
| `x4mp_probe` + spike v2 | M2-001..005 | Answers the session-2 questions before product code depends on them |

### 6.3 What only the user can check (in game)

Real `LoadGame`, the reload order, pause behaviour, menus over the start menu, save blocking, the HUD, true frame
cost, the real extension list, and anything visual. Every such item has a numbered script step and a log line to
look for; the user sends `collect-logs` zips, never edits logs by hand. Results that land in the repo are scrubbed of
Steam ids and user paths (as session 1's log was).

### 6.4 One X4 instance is enough for M2

Two topologies, one X4 each, both on one PC with the server on loopback:

1. **Real X4 = client**, FakeNode = authority serving the user's own save (`--save-file`): criteria 1–8, 12–16.
2. **Real X4 = authority**, FakeNode swarm = clients: criteria 9 and 11.

M2 has no world replication, so nothing needs a second real game. A second copy starts to matter at M3 (two players
seeing each other's avatars), as dev-setup.md §5.x already decided. The only M2 item that a second PC would add is
V26 (a save copied from another machine); a download of the user's own save is close enough for M2.

---

## 7. Risks and open questions

### 7.1 Biggest risks

| Risk | Impact | Mitigation |
|---|---|---|
| The save-load reload tears down the DLL or the stash (B4/B5) | Every join shows leave/join; worst case the socket dies on every load | Three fallbacks ready (§4.1 B4/B5); M2-07 is written after the evidence |
| Saving cannot be fully blocked on clients (quicksave bypasses Lua, no save export to hook) | Polluted client saves later (M4 ghosts) | M2 detects and reports; hard block becomes an M4 entry criterion with an ADR |
| Non-standard save names refused by `SaveGame`/`LoadGame` | Download/load path and authority slots | Reserved-slot mapping (B2/C4) with hash checks, never overwriting user files |
| Menus: no standalone menu over the start menu and no injection | No UI-only join from the start menu | X4Native settings button, `launch.json`/config auto-connect, or join from the pause menu |
| Pause cannot be held at universe ready | Brief unpaused window | Harmless in M2; re-evaluate in M4 |
| Session 2 is long (sitting 1 ≈ 2 h, all three ≈ 4.5 h) | Delays wave 2 | Only sitting 1 blocks M2; wave 1 runs meanwhile |

### 7.2 Open questions for the user

1. Is it fine to split session 2 into three sittings and treat only sitting 1 (about 2 hours) as blocking for M2?
2. For the session-2 and session-3 tests the local server will hold copies of your test save (on this PC only, under
   `out\`, git-ignored). OK?
3. Hosting as authority needs the **admin password** typed into the in-game Join dialog (sent only as an HMAC
   proof, never stored). Acceptable, or do you prefer a separate "host key" the GUI generates?
4. If quicksave cannot be blocked on clients, is "detected and shown in the GUI" acceptable for M2, with the hard
   block deferred to M4?
5. Do you have SirNukes Mod Support APIs and/or kuertee UIX installed now? If not, R3–R6 wait for the M6
   compatibility pass.
6. Do you have the session-2 saves: a normal mid-game save, one **with a PHQ and research unlocked**, and an
   **early save without a PHQ**? (S12 needs the last two; S10 prefers a save docked at a big station with a bar.)

**User answers (2026-10-02):**
1. Yes: three sittings; only sitting 1 blocks M2.
2. Yes: the local server may hold copies of the test saves (this PC only, under git-ignored `out\`).
3. Admin password in the Join dialog (HMAC proof only) is fine for now; a GUI "host key" can come later.
4. Yes: "detected and shown in the GUI" is enough for M2; hard quicksave block in M4.
5. Update 2026-10-02: SirNukes Mod Support APIs and kuertee UIX are now **installed but disabled**. Sitting 1 runs
   with both disabled (vanilla baseline); the script tells the user when to enable them for R3–R6 and to disable them
   again afterwards. (Original answer: not installed yet; the user can install SirNukes and/or kuertee UIX before the session.) The script must say
   which tests need them, and run without them otherwise (SirNukes stays reference-only per ADR-043).
6. Not yet; the user can make the PHQ/research, early no-PHQ and docked-at-bar saves, and may find a mid-game save
   online. The session-2 kit must tolerate a third-party save: list its extensions/DLC on load and flag unknown
   mods instead of failing, and the user only installs mods they choose themselves.

## 8. Handoff notes from merged tasks

**M2-02 → M2-09 (in-game authority on a stored save):**
- `SessionSaveInfo` for a start save has an empty `manifest_sha256`, size 0 and `checkpoint_id` 0: skip the manifest
  download and `ManifestReport`.
- Send `SaveReady{sha256}` (empty manifest sha), report Loading then Matching, then `NodeReady` with
  `loaded_save_sha256`.
- If the authority already runs the stored save, put its sha (32 bytes) in `ClientHello.loaded_save_sha256`; the
  server then sends no info. No automated test covers this branch yet (FakeNode always sends empty): M2-09 adds one.
- After `NodeReady` expect `RequestSave{SessionStart}` and upload the checkpoint (save + manifest,
  `ghosts_cleaned=true`).
- Not yet tested: a SessionStart checkpoint made by an authority that resumed mid-flow. M2-09 adds a hostsim case.
- Known gap: `POST /sessions {saveId}` with a catalog row whose file is missing logs a warning and continues as if
  no save was chosen (kept for an existing test).

**Kit integration (2026-10-02, done):** the script now has Run 1 (`write-probe-config.ps1 -NoHooks`, B4/B5/B6) and Run 2
(hooks on, Parts C/D); C2 uses `run-block.ps1 skip_autosave_on/off`; `ui` runs before C1; the probe logs the raw
`GetGameVersion`/`GetBuildVersionSuffix` (`build` lines); D8 is the only place SirNukes/UIX are enabled; save files are
`save_00N.xml.gz`; X4 lists non-`save_NNN` file names only when the Load Game list is sorted by Name/Date (so the downloaded
`x4mp_*` copy and `x4mp_s2test_1` are found there); the server's "Events" page does not exist (it is **Logs**: `detached:
"ClientReload"` + `resumed` is the good outcome). Gaps found by the dry run: the probe's mod version must equal FakeNode's
(the server compares it with the authority), and `start-server.ps1` sets `Net.ModBuildStrict=false`. Dry run:
`mod/tests/hostsim/probe_session2_dry_run.ps1` (docs/hostsim.md).

**M2-005 probe → session-2 script:** with `hooks:true` (default) the probe pins its DLL at shutdown, so B4's
"was the DLL unloaded?" needs a separate run with `hooks:false` (done, see above).

**M2-002 → session-2 script:** run the `ui` block before C1 step 4 so the Save row can be greyed; `saves1_block` must
be re-run after every save load (the Lua flag resets, the MD flag `global.$x4mp_noSave` is saved in the game);
`saves2` calls `C.TriggerAutosave(true)`, so the probe should have `skip_autosave:true` for C2 or an autosave lands.

**M2-04 → session 2 / M2-06:** the supported-build check reads `GetBuildVersionSuffix` and takes the longest run of
5+ digits as the build number; that string's real format is unverified. If the version matches but no build number
is found, the host starts with a WARN (`Unverified`) rather than refusing. Session 2 must log the raw
`GetGameVersion`/`GetBuildVersionSuffix` values (kit integration: probe logs them at init); then decide whether
"unknown build" should refuse. New `x4mp.json` keys: `frame_budget_us` (100..50000, default 1500) and
`log_categories`. Features register in `mod/native/host/feature_list.cpp` (one include + one `registry.add` line).

**M2-08 → M2-09 (in-game authority upload):** use `CheckpointUploader` (`mod/native/core/authority/upload_job.h`);
`mod/tools/headless/authority_driver.cpp` is the complete reference flow. On `Welcome` → `new_connection(resumed)`, on
disconnect → `connection_lost()`, route `SaveUploadAccept`/`SaveChunkAck`/`SaveStored` to `on_frame`, `pump()` every
frame. Two real protocol findings, keep them: (1) send `SaveUploadEnd` only after the server acks the final offset
(Bulk is sent only when Control is idle, so End could overtake the last chunks); (2) ignore `SaveStored` whose
`upload_id` is neither 0 nor the current upload's (a stale Aborted from the dropped connection). `SaveStarted` is
sent once, never repeated on resume. Self-spawn uses `EntitySpawnBuilder(game_time)` with the same `player.age` as
`SaveStarted`, after both files are stored. Live kill test: `x4mp_authority_live` (needs
`X4MP_LIVE_SERVER_EXE`, ports 47980–47983, ~100 s); M2-14 adds it to the `e2e-headless` job.

**M2-05 → M2-06/M2-11:** bridge contract in `docs/mod-design.md` §7 "M2 bridge contract" and the
`x4mp_bridge.lua` header. Lua cannot raise vanilla `loadSave`, so `x4mp.load_save` calls `LoadGame(name)` after
0.1 s; M2-06 may replace that handler after session 2 (B2). Renderers register with `X4MPScreens.setRenderer`; the
embedded options-menu entry (M2-11) is a second renderer.

**CI flake fixes (2026-10-02):** two test races fixed (C++ retention snapshot, hub-stall topic count). One CI run saw
`host stop took 23.7 s` on Windows that could not be reproduced (locally always ~2.0 s = the designed drain). The test
now asserts the shutdown goodbye instead of a wall-clock bound, so a slow stop is no longer caught. Follow-up (M2-13 or
M6): log per-hosted-service stop durations at shutdown and bound `PersistenceWriter.DisposeAsync`.

**M2-11 → M2-14 / session 3:** HUD `notify` mode raises `AddUITriggeredEvent("X4MP","notify",text)` but no product MD
cue listens to it yet: add a tiny MD cue (`show_notification`) in wave 3, or route notify through the bridge. Session 3
must check: HUD disappears on map/Esc and comes back by itself; row also in the in-game Esc menu; the `append`
fallback source has never run against real gameoptions.lua.

**M2-10 -> M2-06 / M2-09 / M2-11 / M2-13 (save control + self-test, merged by the lead):**
- Session code (M2-06) must call `features::diag_hub().set_connection(NodeRole::Client|Authority, connected)` on every state change (a pure client blocks saves; the saves feature pushes `x4mp.saves` on change) and `diag_hub().set_log_sender(fn)` with a function that queues a `LogForward` line (returns false when not allowed / rate limited); without them the self-test and the quicksave warning are only logged locally. `IPlatform` gained `subscribe_event` / `raise_lua` (default no-ops; `X4Platform` implements them, `FakePlatform` records them): use them for any new bridge verb instead of touching main.cpp.
- M2-09: wrap the authority's own `SaveGame` in `X4MPSaves.allowSaves(fn)` if it ever runs while the block is on (authority nodes are not blocked by native today). M2-11: hand the captured options-menu config to `X4MPSaves.setMenuConfig(cfg)` so the Save row can be greyed without UIX. M2-13: `DiagHub::last_selftest()` holds the last table; the `SELFTEST ...` lines already arrive as `LogForward` once the sender exists.
- Verified locally: md diff + `md/x4mp_saves.xml` against the unpacked `md.xsd` / `diff.xsd` (the diff applied to vanilla `notifications.xml` still validates). Lua tests run with LuaJIT through `lupa` when no interpreter is on PATH.

**M2-06 → M2-07 / M2-09 / session 3:**
- M2-07: a minimal resume exists (`join.state` stash key + Session intent saved in `on_shutdown`, `unload_for_reload()`;
  `on_init` resumes when the stage was loading/ingame). Still needed: the epoch rule (new universe vs `/reloadui`), the
  shutdown time budget, and the 20-reload hostsim test.
- M2-09: `JoinRequest` already parses `role:"authority"` + `admin_password` (sent as `requested_roles=3`); hook an
  AuthorityDriver into `JoinFeature::handle_session_event` and step it in `pump_session`.
- Build check now: suffix → X4Native game version → X4Native release version (`900-611726`); a correct install is
  Supported. Caveat: if the game updates but X4Native does not, only X4Native's own game-version detection catches it.
- `join_flow_run.ps1` (hostsim + server + FakeNode, ports 47953–47955, ~30 s) is not in CI yet: M2-14 adds it.
- No pause at universe ready (session-2 double-Esc finding).
- Session 3 must confirm: start-menu restore on window close (`OpenMenu("OptionsMenu", nil, nil, true)`), `x4mp.*`
  verbs via `raise_event`, LogForward reaching the server from a real client, loadSave from native.

**M2-09 -> M2-10 / M2-14 / session 3 (authority in game, branch `worktree-agent-afd02183eaed5dc0e`):**
- Code: `mod/native/features/authority/` (`AuthorityFlow` owned by `JoinFeature`, pure helpers in `authority_data.*`), `md/x4mp_galaxy.xml`,
  `ui/x4mp_authority.lua` (one added line in `ui.xml`), host option + admin password in `x4mp_menu.lua`, texts 16-19, 25, 26. Bridge verbs and topics:
  `docs/mod-design.md` section 7 table. `JoinFeature` hook points are marked `M2-09` (`sync_authority`, `step_authority_ready`, frame routing at the top
  of `handle_session_event`, the Welcome case, `pump_session`).
- Flow per `RequestSave`: MD galaxy collect, `StringTableAdd` (once per session), Lua `SaveGame(x4mp_ckpt_<16 hex>)`, `SaveStarted(game_time)`,
  wait until the `.xml.gz` is stable (1.5 s) and openable, hash on a worker + empty-station manifest, `GalaxyMetadata`, `CheckpointUploader`,
  one self-spawn (`EntitySpawnBuilder(game_time)`, same time as `SaveStarted`), `record_and_trim` (keeps the last 2 of the saves listed in
  `<config dir>uthority-saves.json`; never touches other saves). Not covered: a DLL reload in the middle of a checkpoint (the server re-requests).
- Real finding fixed in `JoinFeature` (M2-06 code): **NodeReady is phase-gated server-side** (`MessagePolicy`: Matching/CatchingUp/InGame) and the node's
  phase is published asynchronously, so NodeReady sent in the same frame as the Matching report got `PhaseDenied`. It now waits (max 3 s) until the
  RosterUpdate shows our node in Matching (`finish_ready`). M2-07 touches the same area: keep that wait.
- An authority whose universe is ready and that receives no `SessionSaveInfo` within 2.5 s of the Welcome reports Loading, Matching, NodeReady itself
  (`authority: no session save to load`). "Already runs the start save": ClientHello carries the sha from the stash key `join.authority`
  (set after a loaded session save and after each stored checkpoint, cleared by a game load we did not cause); test seam `loaded_save_sha256` in the join payload.
- Server: `WorldMirror.ApplySpawn` logs spawns of 1-4 entities with their `game_time` (criterion 11 evidence in the server log).
- Tests: Catch2 `tests/test_authority_flow.cpp`, Lua `tests/lua/test_authority.lua`, ctest `hostsim.authority_role`, e2e (not CI, ports 47956-47958, ~80 s)
  `mod/tests/hostsim/authority_flow_run.ps1`. Only the game can confirm: MD syntax/properties of `x4mp_galaxy.xml` (schema-valid, never run), that
  `SaveGame` from the Lua event writes `x4mp_ckpt_*.xml.gz` into the save folder, `player.occupiedship` data, whether X4 keeps the file locked until done.
