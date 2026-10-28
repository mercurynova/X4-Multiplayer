# Session 4, two-PC run, M3-34 and F1 checks (2026-10-28, 08:47-09:49 local)

Script: [in-game-session-4.md](../in-game-session-4.md) "M3-34 check" and "F1 main-menu leave check". Main `9165e0e` (M3-34, F1, F13, F13b built in). PC A = server + X4 authority
**Alice**, PC B = X4 client **Bob** over Tailscale (11-19 ms rtt, median 12.8 ms, p95 23 ms). Logs (local, git-ignored): `out/session4/logs-s4e-*` (Alice, PC A), Bob's four zips
`logs-s4b/c/d/e-*` (s4b up to 09:11, s4c = `diag.hints_off` run, s4d = highway run, s4e = to the end), server log `server-20261028.log`, the lead's notes of Alice's overwritten log.
Mod log times are UTC, local = UTC-4; the server log is local. Both PCs' clocks agreed to well under a second this time (the F9b stamp is still on a different clock, see F9).

## Results

| Check | Verdict | Evidence |
|---|---|---|
| M3-34 step 1, fast in-sector flight, both directions | **PASS, with one new finding (F23c)** | smooth at speed on the watcher's map, both directions; the map icon jumped only while the flyer braked hard (Backspace) from travel speed |
| M3-34 step 1 diag: `hints_off` on Bob (Alice flew from 09:18:30) | **cause confirmed: the velocity hint** | hard brake fully smooth, zero engine-guard hits; one small flicker at 09:19:42 in a gentle slow-down (see F23c) |
| M3-34 step 2, re-host with Bob online (F22) | **PASS (data path)** | Bob saw Alice once (net 18), spawned at her real spot, `snaps=0` in every window, no `pos=(0,0,0)`, no 34-47 s sawtooth. The re-host UX itself failed twice (F1b, F1c) |
| M3-29 re-provision path (F20b), by accident | **PASS in game** | the 08:53 checkpoint predates Bob; after the re-host the server asked Alice to provision Bob again, Bob kept his ship (details below) |
| M3-34 step 4, local highway | **PASS for "no 286 km repair", new finding F21b** | no `repaired the parked avatar` line, no engine-guard hit with `highway context YES` (`engine_highway=0` all day); but the avatar icon stays frozen at the entry for the whole ride |
| F1 client: exit to main menu | **PASS** | armed 13:34:37.088Z, fired 13:35:07.092Z (30.0 s), server `left: ClientQuit` 09:35:07.031 (30.5 s after Bob's exit: detach `ClientReload` 09:34:36.576). Players list went offline, `avatar net_id=17 stays parked at its pose` |
| F1 rejoin is normal | **PASS** | first click worked (13:36:02.5Z), `Welcome ... resumed=false`, same avatar reused (`avatar net_id=17 exists (live), answering with the same ship`), one `[MP] Bob` |
| F1 fast return (exit + Load of a quicksave of this session within 10 s) | **PASS** | exit 13:38:26.99Z (`reload #4`), Load 13:38:34.0Z (`reload #5`, 7 s later), no `leaving the session (F1)`; `reload resume: new universe (save load; the player id changed)`. Janitor swept 3 leftover `[MP]` objects at 13:40:00Z (the quicksave contained them: expected) |
| F1 `/reloadui` x2 | **PASS** | 13:42:05.8Z and 13:42:13.5Z: `reload resume: same universe`, no `main menu:` line |
| F1 in-session Load (Esc > Load Game) | **PASS, one cosmetic** | 13:42:40.5Z; `reload: game loaded after 1319 frame(s), a load in progress`, no leave. The GUI showed Bob offline for ~1 s at the load start (K5) |
| F1 authority (Alice): menu exit, 30 s timer, grace | **not done** | Alice pressed Disconnect by hand 5.8 s after the arm, so the 30 s timer never ran for the authority; blocked by F1b/F1c anyway |
| F13 authority grace + resume | **not run** | session paused |
| F9 display delay | **at the 250 ms cap -> accept for M3 (ADR-052)** | numbers below |

## Findings

| # | Sev. | Finding |
|---|---|---|
| F23c | medium | **Map icon of the remote ship jumps randomly while it brakes hard from travel speed; the 3D ghost stays steady.** Caused by the velocity hint (A/B below). |
| F21b | low-medium | **Local highway ride: the avatar's map icon freezes at the highway entry until the exit**, then jumps to the real position within a second. By design (hidden + hold), but visible. |
| F1b | medium | **The menu-exit watch survives a manual Disconnect and cancels the next Join click** (`stop_session` does not disarm it nor clear `undecided_`). |
| F1c | **high** | **An authority that rejoins from the main menu is never sent the start save and sticks at "checking the session save"**: its stash still claims the old save as loaded, and the server trusts it. Only an X4 restart helped. |
| F24 | medium (decision) | **No checkpoint for 14 min around the re-host; everything Alice did from 08:53 to 09:02 was lost.** Autosave is every 15 min after the last checkpoint and nothing checkpoints when the authority leaves. |
| F1d | low | **The authority at the main menu keeps broadcasting the world clock with `game_time 2.136`.** Bob's ghost stamps went off by tens of seconds for 40 s. |
| F25 | low | **`ManifestReport` is sent in a phase the server denies after a save-load resume** (WRN `policy violation "ManifestReport" ("PhaseDenied")` at both in-session Loads). Harmless today. |
| K5, K6 | low | GUI shows the player offline for ~1 s on every reload; test script and tool text (resting ghost `newest_age`, K1 confirmed). |
| (info) | - | Bob's X4 fell to 2-5 fps for ~55 s at 09:45 (mod p95 12.48 ms, the report's "worst"); not the mod, cause unknown. |

### F23c, map icon jumps while braking (new)

**Seen.** Fast in-sector flight with the travel drive was smooth on the watcher's map in both directions. When the flyer braked hard with Backspace (Alice ~09:00, Bob a few minutes
later) the **map** icon of the ship jumped randomly until it was fully stopped; the 3D ghost stayed steady. With `diag.hints_off` on Bob (Alice flew from 09:18:30) the same hard
brake was fully smooth, with one small flicker at 09:19:42 during a gentle slow-down.

**Evidence (viewer = Bob, Alice flies, hints on, s4b).** The engine guard fired only in the braking window and then stopped:

- `12:59:06.059Z ghost engine guard: net=15 label='[MP] Alice' the game moved our object: 90403.2 m from the placed pose (tolerance 1769 m, speed 5797 m/s), highway context no; placed it back, velocity zeroed`
- `12:59:08.952Z [sync] engine guard: ghosts moved by the game=120 into another sector=0 in a highway context=0 (reads 470)`, then `=206 (reads 648)` at 12:59:13.960Z, never again (3 guard lines in the whole log).
- Bob's windows for Alice's ghost: 12:58:33 (spd 1490 m/s) rising to 6054 m/s at 12:59:08 with no hit; the hits start as she brakes (spd 6054 -> 3137 -> 239 m/s in 10 s). In the hit windows `hints` jumps 200 -> 321 -> 419 (the guard queues a zero hint per hit; normal rate is ~5 per 5 s), frame p95 727 us.
- The same on Alice (viewer of Bob's avatar): `13:01:16.374Z avatars: engine guard: avatar of player 12 (net_id 16): the game moved it: 177086.4 m from where it was placed (tolerance 908 m, speed 2927 m/s), highway context no`; `engine_moved` 0 -> 125 -> 347 in two 5 s windows (13:01:13-13:01:23Z, 347 hits in about 7 s, i.e. nearly every guard-hot frame). Again at 13:08:40Z (66 km, 301 -> 342 hits, after the re-host) and one hit each at 13:17:59Z (236 km, speed 0) and 13:28:48Z (675 km, 6687 m/s, the local-highway exit).
- **Hints off (s4c, Bob, Alice braked from 5147 to 743 m/s around 13:19:07-13:19:12Z): no engine-guard line at all, `hints 0` in every window, err p95 6.8 m in the brake window**, steady flight 0.01 m.
- `engine_highway` stayed 0 on both PCs all day: this is not a highway capture.

**Reading.** The engine moves our inert object by tens to hundreds of km between our placements, but only with the velocity hint switched on and only during hard deceleration (not during the
acceleration through the same speeds, 12:58:33-12:59:03). The guard puts it back in the same frame, which is why the 3D view is steady, but the map reads the object's own state and draws the displaced
position for single frames: "random jumps". Hints off removes it, so the hint (or the state it leaves in the engine) is the carrier. What is NOT explained: the displacement is 66-675 km, far more than a
5 Hz hint (<= 1000 m/s x 0.2 s) can fly an object; and it recurs nearly every frame during the 2 s hot mode although each hit queues a zero velocity. So it is not plain integration of the last hint.
The residual flicker with hints off at 09:19:42 is not pinned: Bob's window around it (13:19:42Z, spd 2655 m/s, err p95 16.8 m, max 23.6 m) is clean; the largest path error of that run is 92 m at 13:19:52Z at the start of the slow-down.

**Code.** `GhostDriver::guard_check` (`mod/native/features/ghosts/ghost_driver.cpp` ~285) and the hint block in the per-ghost update (~470-492: `hint_max_mps = 1000`, `hint_interval_us = 200000`, velocity = single-frame finite difference
of the rendered pose, a zero hint only when speed > cap); avatar side `AvatarDirector` (`avatar_director.cpp` ~694-704 hold, ~894 hint cap, ~749 engine guard).

**Fix direction.** (1) Make `hints_off` the default (the ghost is placed every frame anyway) and accept F23b (target info shows 0 m/s): the A/B shows the hint buys nothing visible and costs this. Or keep a hint only
below ~100-150 m/s and only while the rendered speed is not changing quickly (no hint while decelerating/accelerating). (2) Extend the guard line with the sector-relative position and the distance to the sector edge (the
displacements look like sector-scale wraps; untested hypothesis) and the object's own reported velocity, to settle the mechanism if the hint stays. (3) Re-run with Alice's side `hints_off` too (this run only switched Bob), and check the
map during a hard brake after the change: expected 0 guard hits.

### F21b, avatar icon freezes during a local-highway ride (new)

**Seen (Bob rides a local highway, Alice watches her map).** Bob's icon froze at the highway entry; the 3D ghost on the highway looked fine; within a second after the exit the icon jumped to his real position.

**Evidence.**
- Bob s4d: `13:27:43.235Z selfship: highway context on (sector 6, superhighway signalled: false)`, `13:28:48.248Z selfship: highway context off (sector 6, ...)`: 65 s, the sector stayed in the sector map, no `not in the sector map` line.
- What Bob's mod sent: his tx dropped from about 2.1-2.4 kB/s to 250-295 B/s for the whole ride (`stats` lines 13:27:48 to 13:28:43Z), i.e. the 1 Hz hidden stream (`selfship: ready (20 Hz moving / 5 Hz idle / 1 Hz hidden ...)`) with `Hidden + InHighway` flags; back to ~2.4 kB/s after the exit.
- Alice (`avatars: [drive]`): `states_in` +5 per 5 s from 13:27:46Z (about 1/s, was ~20/s), **`set_pose` frozen at 19550 for 65 s** (13:27:46 to 13:28:46Z), `vel_hints` frozen at 1162, `repairs=0`, `engine_moved=343` unchanged; at 13:28:48.776Z one guard hit (675 km, speed 6687 m/s, highway context no), then states_in +76 and set_pose +103 in the next window.

**Cause.** By design (M3-31/33): `OwnShipTracker` sets `kHidden` for any `in_highway` (`mod/native/features/selfship/own_ship_tracker.cpp` ~180-182), the stream drops to the hidden rate, and `AvatarDirector` holds a hidden avatar at its last pose
(`holding`, ~696-704). That is right for a superhighway (the sector leaves the map) but a local highway keeps the ship in a mapped sector, so a true pose could be sent.
**Fix direction.** For an in-sector (local) highway: keep `InHighway`, drop `Hidden`, send at the normal moving rate, and let the avatar side place and interpolate it with a zero hint (the guard already covers the engine moving it). Keep hold/hide for superhighways only.
Cosmetic; do not block M3 exit on it unless the lead wants highway rides to look right on the map.

### F1b and F1c, the re-host from the main menu (new)

Timeline (Alice local, mod UTC in notes, server local):
- 09:02:14 exit to main menu (`extension shutdown: session unloaded for a resume (stage 'ingame' ...)`, server `detached: "ClientReload"`), 09:02:15 resumed; `main menu: the start menu is up ... leaving in 30000 ms unless a game loads` armed at 13:02:15.061Z (due 13:02:45).
- 09:02:20 Alice pressed Disconnect by hand: `disconnect requested by the UI`, server `left: ClientQuit`, `Running -> AuthorityLost`.
- **F1b, 09:03:28:** first Join click: `13:03:28.054Z joining ... (roles 3, fresh)` and **the same millisecond** `main menu: the player exited to the main menu; leaving the session (F1)` / `leaving the session (main menu)`. The click was cancelled by the stale watch.
- **F1c, 09:03:39:** second click: `Welcome: player_id=11 resumed=false`, `AuthorityAssign: net ids continue from 15`; server `AuthorityLost -> AuthorityLoading (authority rejoined without resuming)` and `the authority loads save 6e368b33b021: world rolled back to net ids below 15 (2 entities removed)` but **no** `the authority is sent the session's start save` line. The mod stayed at "checking the session save" with no further `[sess]`/`[save]` line until X4 was restarted (~09:05, server `detached: "SocketLost"` 09:05:59). After the restart: 09:07:01 `the authority is sent the session's start save ... (29671001 bytes)`, download 0.1 s, 09:07:34 first request, 09:07:41 `Running` (first checkpoint).

**F1b cause.** `JoinFeature::stop_session` (`mod/native/features/join/join_feature.cpp` ~499) tears the session down but does not call `menu_watch_.note_game_loaded()` and does not reset `undecided_`; only `leave_for_main_menu` (~324) does. The watch is evaluated as `menu_watch_.due(now) && session_ && undecided_` (line ~253): with `session_` null it waited, and the next Join created a `session_` while the armed watch (deadline long past) and `undecided_` were still there, so it fired on the first frame.
**Fix direction.** Disarm the watch and clear `undecided_` in `stop_session` (every manual disconnect, rejected join, failure). A regression test in `test_resume_state.cpp`/the hostsim join flow: arm, UI disconnect, join again, expect no leave.

**F1c cause.** `join_feature.cpp` ~474 sends `ClientHello.loaded_save_sha256 = AuthorityFlow::stored_loaded_sha(*stash_)` for a fresh authority join. That stash value is the sha of the save the game last ran (set in `finish_ready` ~985 and `AuthorityFlow::note_loaded_save`) and is only forgotten by `forget_loaded_sha` on a game load that the mod did not cause (~258). Exiting to the main menu unloads the game but leaves the stash value, and `session.h` documents the hello field as "empty = main menu". The server (`SaveService.SendStartSaveInfo`, `server/src/X4MP.Core/Saves/SaveService.StartSave.cs` ~100, `LoadedSaveOf`) sees `loaded == start.Sha`, treats the game as already running the save and sends no `SessionSaveInfo`. The mod's own fallback `JoinFeature::step_authority_ready` (~1020) requires `ctx.gates.universe_ready`, which is false at the main menu, so nothing ever happens.
**Fix direction.** (1) Clear the stash sha when the game is gone: in `leave_for_main_menu`/the start-menu detection and in any shutdown with the start menu up, and never send a non-empty `loaded_save_sha256` while `ui_ready.startmenu` is true or the universe is not ready. (2) Server-side belt and braces: if a hello claims a loaded save but the node is not reporting `Loading`/`Matching`/`NodeReady` within a few seconds, send the `SessionSaveInfo` anyway. (3) A mod-side timeout on "checking the session save" with a visible message. Hostsim case: authority fresh join with a stale sha and no universe.

### F24, no checkpoint for 14 minutes (decision)

Checkpoints (server): `08:53:42` first (SessionStart), `09:07:41` (request 2 "Migration", after the re-host), `09:22:47` (request 3 "Autosave"), `09:37:57` (request 4 "Autosave"), `09:44:20` (request 5 "Admin"). The re-host loaded the 08:53 checkpoint, so Alice's 08:53-09:02 flight and everything after the checkpoint was lost (as designed, the universe rewinds).
**Why:** `SaveOptions.AutosaveMinutes = 15` (`server/src/X4MP.Core/Saves/SaveOptions.cs` 27); `SetNextAutosave` (`SaveService.Checkpoints.cs` ~187) restarts the timer when a checkpoint becomes current, so the first autosave was due at 09:08:42, six minutes after Alice left at 09:02:14. Nothing requests a checkpoint when the authority leaves (it cannot: it is gone) and no warning shows the age of the last checkpoint. The later requests are exactly 15 min apart (09:07:34, 09:22:41, 09:37:47).
**Decision needed / fix direction.** Lower the default (5 min costs a 30 MB save every 5 min, about 2 s upload on a LAN; the saves are 29-34 MB), show "last checkpoint N min ago" in the GUI Sessions page, and offer "Request save now" before a planned re-host. Not a bug in the code, a product setting; the user decides.

### F1d, authority at the main menu broadcasts the world clock

The lead's note: `13:02:15.048Z authority: world clock: keepalive WorldUpdate at 20 Hz started (game_time 2.136)` while Alice was at the main menu (no universe). On Bob, Alice's ghost (held, standing) `newest_age_ms` went from 16352 (13:02:14) to **-29836 (13:02:19)** and climbed back to +906 only at 13:02:54, then grew normally once Alice was gone: the ghost's sample stamps (F9b: the latest WorldUpdate game time) were off by about 50 s for 40 s. Harmless here (standing ghost) but a moving ghost would have snapped. Fix: the authority must not start the keepalive before its universe is ready (`AuthorityFlow`, world-clock keepalive start in the resume path) and the server should ignore a `WorldUpdate` whose game time goes backwards by more than a few seconds.

### F25, ManifestReport denied after a save-load resume

Server WRN `09:39:58.694 conn 21: policy violation "ManifestReport" ("PhaseDenied"); 1 in the last minute` and `09:43:59.755 conn 24: ...` at the fast-return Load and the in-session Load (not at ordinary joins, not after `/reloadui`). `MessagePolicy` allows `ManifestReport` only in Matching/CatchingUp; `JoinFeature::finish_ready` (~974) waits for the roster to show Matching only while `my_phase_ < 5`, and after a resume the node's roster phase is the old InGame, so the report goes out 65 ms before `NodeReady` and before the server has processed the LoadStatus. Fix: wait for the phase change after the LoadStatus (or skip the count-only report when resuming in-game), and check that the "N in the last minute" counter cannot disconnect a node.

## Re-host details and F20b / F22

- 08:53:02 Alice joined, start save sent, 08:53:42 first checkpoint, 08:55:57 Bob joined (his 08:53:41 attempt was refused `NameTaken` "name is bound to another player", the M3-24 machine-tagged name binding; he joined after releasing it in the GUI, presumably), takeover ~08:57:50.
- Re-host 09:07:01 (after the F1c restart): `world rolled back to net ids below 15 (0 entities removed)`. Server `09:07:34.555 relay: player 12 lost its avatar to a world rollback: the authority is asked to provision a new one (sector 6, the player's newest position)`; Alice `PlayerShip from player 12: provisioning a new avatar`, `spawned the avatar of player 12 ... net_id 17 ... (safe position)`; Bob `13:07:34.707Z takeover: my avatar was replaced: net_id 16 -> 17 (the authority re-provisioned it after a world rollback); the own ship stays, no new takeover`. This is the F20b case ("checkpoint predates the online player") and it works: Bob kept his ship and flew on (Alice's avatar `set_pose` kept rising, 13:08:29-13:08:49Z).
- Alice's self-spawn came late (`self-spawn sent net_id=18 game_time=183.895 (late: the ship was not known at the checkpoint)`, 13:07:42.730Z; the pilot-seat wait warning at 13:07:41 is the existing one). Bob: `ghost removed by the server: net=15` at 13:03:39 (Alice's rollback), `ghost spawned: net=18 ... pos=(118267,794,197548)` at 13:07:42.887Z, first `[sync]` window `err p50/p95/max=0.75/2.93/3.19 m ... snaps=0 extrap=5 newest_age_ms=168`.
- **F22 verdict: PASS for the data path.** No `pos=(0,0,0)` in Bob's log, `snaps=0` in all 266 windows of the day, no 34-47 s sawtooth. The ghost shows `lat_p95` 4.3-4.5 s and `newest_age` 2.0-3.4 s once Alice stands still (13:08:45 onward) with `held` ~180 of 200 frames: a resting ship is replicated about once per 5 s (`rep_msgs` +1 per 5 s), so the sample age of a standing ship is seconds by design. The script's "`newest_age_ms` stays < 1 s" is too strict for a standing ship (K6).

## Numbers (Bob's log, 2026-10-28 lines only)

K1 again: the report scans the whole file and the log still holds 2026-10-06/07 lines. The headline "worst" values come from there: `lat_p95=37081 ms` is `2026-10-07 18:48:01Z`, `err p95/max 868.65/1221.73 m` is `2026-10-07 18:50:22Z` (the old superhighway transit, F21). The day's own numbers (266 `[sync]` windows, 84 with a moving ghost, max speed 6054 m/s):

| Metric | Value |
|---|---|
| Path error p95, ghost speed < 300 m/s (41 windows) | median 0.36 m, worst 5.64 m (13:07:55Z, right after the re-host spawn); limit 10 m: PASS |
| `steady_p95` over the day | worst 5.86 m (13:25:53Z); all under 10 m |
| Path error p95, ghost speed >= 300 m/s (43 windows, travel drive up to 6054 m/s) | median 13.3 m, worst 99.4 m (12:59:08Z, 6054 m/s); max single frame 346.6 m (13:19:02Z, 5173 m/s); this is the speed-proportional error of the 250 ms delay plus the brake window; no snaps |
| Display latency `lat_p95`, moving windows (84) | median **250 ms** (the cap), 9 windows < 200 ms, 22 at 200-249, 53 at >= 250; the stationary/hold windows that read 4.4 s are not judged by the report |
| `delay_ms` in moving windows | median 247, at the 250 ms cap in 38 of 84; falls to 137-200 ms only in the fast windows |
| Snaps / extrapolation | `snaps=0` in every window; `extrap` 0 except the brake/stop windows |
| Ghost respawns ("pops") | **1**, `13:44:01.398Z ghost respawned: net=18 label='[MP] Alice' ...` 1.4 s after the in-session Load (13:43:59.6Z `ghost adoption: records=1 adopted_by_id=0 ... lost=1`, janitor swept 3 `[MP]` leftovers at 13:44:01.0Z). The Load replaces the universe, the ghost is re-created: expected. The report's "pop" check should not count a respawn after a save load (K6) |
| Mod main thread | stats p95 median 0.17 ms; worst **12.48 ms at 13:45:17Z with `fps=2.3 frame_p95=939 ms`**: Bob's X4 ran at 1.8-5.6 fps from 13:45:01Z to 13:45:55Z (the 8.25, 6.71, 5.88 ms entries are the same minute) and then his socket died (`send failed (winsock error 10053)` 13:45:54Z, `HeartbeatTimeout` on the server 09:46:03). The mod cost is just scaled by the frozen game; the cause of the stall (PC B itself, not the mod) is unknown. Normal windows: 0.14-0.28 ms; `[perf] frames` p95 0.16-0.17 ms |
| Bandwidth (client, median 5 s window) | rx 0.28-0.44 kB/s, tx 0.68-0.89 kB/s; rtt median 12.8 ms |

### F9 verdict

At the cap: median moving-ghost `lat_p95` 250 ms, `delay_ms` at 250 in 45 % of moving windows, with an rtt of 13 ms. ADR-052 says "if still at the cap, accept 250 ms for M3 and move the < 200 ms target to an M4 perf item". **F9 is accepted for M3**; F9b (player-ship sample time stamps, queued after this session) is the likely reason the controller reads noise as jitter and stays high, so this run is the F9b baseline.

## Alice's engine guard totals (avatar of Bob, hints on)

`engine_moved`: 0 until 13:01:16Z, 347 by 13:01:23Z (66-177 km displacements, speeds 469-2927 m/s), 342 after the re-host (13:08:40-13:08:49Z, 66 km), 343-344 at the end (13:17:59Z 236 km, speed 0; 13:28:48Z 675 km). `engine_highway=0`, `repairs=0` for the whole day, so the M3-34 "286 km repair" of the internet run did not recur on a local-highway entry (M3-34 step 4 passes), and the engine moves are tied to Bob's braking, not to highways.

## Not determined

- Why the displacement per guard hit is 66-675 km and recurs every frame in hot mode (F23c).
- The 09:19:42 flicker with hints off.
- Why Bob's X4 dropped to 2-5 fps at 09:45 (also: the F1 timeline at the end, `HeartbeatTimeout` 09:46:03 and a refused resume `ResumeExpired` 09:47:28, is a consequence of that stall plus Alice having left at 09:44:31).
- `main menu:` line missing at the first half of the fast-return test (reload #4, 13:38:27Z): no `the start menu is up` line was logged before the Load 7 s later; the result (no leave) is right either way.
- Whether the F1 authority path (grace countdown instead of Stopping/Ended) works: not reached.
