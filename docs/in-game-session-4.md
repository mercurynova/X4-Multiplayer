# In-game test session 4 (M3: two players see each other) - OUTLINE

> **Status (2026-10-03): OUTLINE, not runnable yet.** Script names, labels, log lines and timings below are the plan.
> Sitting 0 is finalised by the wave-0 kit tasks (M3-001/002) before it runs; sittings 1-3 are finalised by the
> **kit-check task M3-14** against the merged code (every script exists, every log line is real, dry run in hostsim),
> as M2-14 did for session 3. Do not start a sitting until its section says "finalised".

Claude can't play X4, so you run these steps and send back the log zips plus your notes. Plan: [m3-plan.md](m3-plan.md)
(exit criteria in section 3, spikes in section 5).

| Sitting | When | PCs / X4 copies | Time | What it answers |
|---|---|---|---|---|
| **0** | **Before coding** (after wave 0) | 1 / 1, throwaway `x4mp_probe` + `x4mp_spike`, scratch save slot | ~60 min | Spikes S13.1-S13.12 (how ghosts can be made, moved and named; takeover from the post-load state; highways; seat; SETA; pause; galaxy dump; chat window) |
| **1** | After wave 3 | 1 / 1: real X4 = **client**, FakeNode authority serving your save + 2 FakeNode **wingman** bots | ~70 min | Criteria 2, 3, 5-7, 9 (bot on team 2), 10, 11, 12, 14 |
| **2** | After wave 3 | 1 / 1: real X4 = **authority**, FakeNode clients (3 wingmen, then 7 for the perf check) | ~60 min | Criteria 4, 5, 8, 9, 11, V21b |
| **3** | After sittings 1-2 pass | **2 / 2** (Q1): PC A = server + authority, PC B = client | ~100 min | Criteria 1, 2, 3, 6, 9, 10, 13 over a real LAN |

## START HERE (outline)

1. Back up `Documents\Egosoft\X4\<id>\save\` on **every** PC (OneDrive paused; Steam Cloud syncs it and restores deleted files, see session 3 A1). Slots 1-7 are never touched; sitting 0 uses a scratch slot you name (default slot 7).
2. Steam launch options on every PC: `-debug all -logfile x4mp_s4.log`; Steam auto-update for X4 **off**; both PCs on build 611726 with the **same DLCs**.
3. Two PowerShell windows in the repo folder (window 1 = server, window 2 = helpers). Always `powershell -ExecutionPolicy Bypass -File <script>`.
4. Bind **Toggle Chat Window** (Settings > Controls) on every PC.
5. Sitting 0: `tools\session4\install-spike.ps1` (swaps the product mod out, the probe and spike in); afterwards `install-spike.ps1 -Restore`.
6. Sittings 1-3: `mod\build.ps1`, then `tools\session4\install.ps1` (product mod). PC B gets the zip from `tools\session4\make-client-kit.ps1` (no build tools needed there).
7. **After every X4 quit run `tools\session4\collect-logs.ps1 -Label <word>`** (on the PC where X4 ran) before starting X4 again.
8. After sittings 1-3 also run `tools\session4\sync-report.ps1` (summary of the `[sync]` and `[perf]` lines) and paste its output into your notes.
9. Send Claude: the zip file names, your notes with clock times, ratings (sitting 0), screenshots where asked.
10. At the end: `uninstall.ps1` on every PC; remove the launch option.

## Lessons carried from sessions 2 and 3 (read once)

- The Load Game list hides `x4mp_*` saves; the mod loads them by name. Copy a checkpoint into a normal slot with Explorer when a step asks for it.
- A save load and `/reloadui` both restart our DLL; the connection survives. Write down anything that looks frozen after a load.
- Use `-FreshDownload` on the fake-authority script when you want a real download (Steam Cloud keeps old `x4mp_*` files).
- Quicksave still writes a file on a client (hard block is M4); from M3 the janitor removes `[MP] ` ships from it when it is loaded with the mod.
- UIX and SirNukes may stay enabled (session 3 ran with both).
- **New in M3:** your client's NPC world is **not** the host's (NPC sync is M4). Different traffic around you is expected. Local NPCs can attack you (Q12): fly in quiet, friendly sectors.

---

## Sitting 0: spikes S13 (before coding, one PC, ~60 min)

Setup: `install-spike.ps1`; `write-probe-config.ps1 -ScratchSlot save_007` (the takeover and persistence blocks refuse to run on any other save); load the scratch slot; fly to open space in a quiet sector. Each block is started with `tools\session4\run-block.ps1 <block>` (window 2) and logs to the probe log and `x4mp_s4.log` (`[X4MP-SPIKE] S13...`).

| Step | Block | What you do | What to note |
|---|---|---|---|
| 0.1 | `ghost_spawn` | Look at the two new ships 1 km ahead, target each, open the map; wait 60 s | Name shown? colour? on radar? do they move by themselves? |
| 0.2 | `ghost_motion a`, `b`, `c` | Watch each 20 s mode (circle 100/300/600 m/s, line 3 km/s); rate smoothness 1-5 each; then fly **slowly** into the parked ghost | Ratings; trails/engine glow; what the collision did (bounce, damage, ghost pushed) |
| 0.3 | `ghost_xsector` | Open the map, find the ghost in the next sector | Seen there? |
| 0.4 | `sample` (120 s, run it several times) | Cruise, boost, travel drive, a local highway, a superhighway, a gate, an accelerator if near, dock on a pad, dock inside, stand up and walk, turn SETA on and off | Clock time of each manoeuvre (the log is matched to it) |
| 0.5 | `seat` | Load the scratch save, stand in the cockpit for 30 s, sit down, stand up, walk out | Clock times |
| 0.6 | `takeover` | Right after loading (still standing): a fighter appears 300 m away and you are moved into it; the old ship disappears | Did it work? anything odd? |
| 0.7 | `takeover_docked` | Load a scratch save that is docked **inside** a station; same as 0.6 | Same |
| 0.8 | `teams_product` + `dress` | Two team ships appear (team 1 allied, team 2 hostile); target both; shoot the hostile one for 5 s | Colours; hull never drops below the minimum |
| 0.9 | `persist_spawn`, save to the scratch slot, reload, `persist_check` | Only the save/reload | — |
| 0.10 | `seta` | Turn SETA on | Was it switched off? notification? |
| 0.11 | `pause_move` | Open the Esc menu for 20 s | Did the test ghost keep moving behind the menu? |
| 0.12 | `galaxy_dump` | Nothing | `Documents\Egosoft\X4\x4mp\galaxy-dump.json` exists |
| 0.13 | `chat` | Open the chat window: a line from "Alice" (coloured) appears; type `hello` | Shown? colour? your line logged by the spike? |
| 0.14 (optional) | session-2 `diplo1`, `diplo2` | As in session 2, part E | — |

Quit X4, `collect-logs.ps1 -Label s0`, `install-spike.ps1 -Restore`.

---

## Sitting 1: real X4 = client, FakeNode authority + wingmen (one PC)

**1.0 Start** (window 1): `tools\session4\start-fake-authority.ps1 -SaveName save_004 -JoinPassword Testpw-314159 -FreshDownload -Wingmen 2 -GalaxyFile <dump from 0.12>`. Wait for `checkpoint stored`; the two bots `Wing1`, `Wing2` wait until a player named `Tester` is in game, then fly in formation 400-800 m around you. `Wing2` is on team 2 (allied at first).

**1.1 Join and takeover (criteria 3, 14).** Start X4: the Multiplayer window shows the remembered address and name (from `x4mp.json`). Join as `Tester`. After loading you stand in the host's ship; within a few seconds you are moved into **your own fighter** next to it. *Look for:* the host ship is gone from beside you, then reappears as `[MP] Host` (the fake authority's ship). `x4mp.log`: `takeover: ...` lines in order, ending `takeover: done`; `remove_blocked_by_guard` lines are fine, a Game Over is not.

**1.2 See the wingmen (criteria 2, 6).** Fly straight, turn, boost, then use travel drive. *Look for:* `[MP] Wing1` / `[MP] Wing2` follow smoothly (rate 1-5), names and colours. Then jump through a gate: the bots follow (they appear in the new sector, no streak). Take a local highway and, if near, a superhighway. Dock on a pad, dock inside, undock.

**1.3 Reloads (criterion 7).** `/reloadui` twice; then quit to the menu and join again (rejoin). *Look for:* no duplicate wingmen, no leftover ships where the old ones were; `janitor: removed N` and `ghosts: adopted N` lines.

**1.4 Chat (criterion 10).** Toggle Chat Window: `hi all`, then `/t team only`, then `/w Wing1 psst` (the bots echo). With SirNukes enabled, one of its `/` commands still works.

**1.5 Relations (criterion 9).** GUI **Teams & Factions**: set team 1 <-> team 2 to **Hostile**, then **Allied**. *Look for:* `[MP] Wing2`'s targeting colour changes within 2 s each time.

**1.6 SETA (criterion 12).** Turn SETA on: off again within 1 s, notification.

**1.7 Quicksave (criterion 8, client half).** Quicksave (known: a file is written). Later load that file with the mod in single player: `janitor: removed N [MP] objects`.

**1.8 Performance (criterion 11).** Window 1: restart with `-Wingmen 7`. Fly 10 minutes among them. *Look for:* GUI Players > Tester: mod main-thread p95 < 0.2 ms; FPS compared with 1 minute disconnected at the same spot (write both down).

Quit X4, `collect-logs.ps1 -Label s1`, `sync-report.ps1`.

---

## Sitting 2: real X4 = authority, FakeNode clients (one PC)

**2.0 Start** (window 1): `tools\session4\start-fake-clients.ps1 -SaveName save_004 -Wingmen 3` (uploads the save, creates the session, prints the in-game admin password).

**2.1 Host and self-spawn (criteria 4, 5).** Host from the start menu (as session 3 C2). After the load **stand** for 30 s, then sit down. *Look for:* window 1 / GUI Logs: your ship's `EntitySpawn` arrives within 1 s of sitting down (not before). Three bot avatars `[...]` appear next to you as team ships and fly around you; they take no damage if shot.

**2.2 Checkpoint with avatars (criteria 4, 8).** GUI **Request save now**. Then `tools\session4\savescan.ps1 -Latest`: `[MP] objects: 0`, `team ships: 3` (the avatars). Stop one bot (window 1 prints how): its avatar stays parked.

**2.3 Authority reload (criterion 4).** Quit X4 and host again (it loads the newest checkpoint). *Look for:* the three avatars are where they were saved and move again when the bots resume; `avatars: bound 3, spawned 0`.

**2.4 Perf (criterion 11).** Restart window 1 with `-Wingmen 7`; 10 minutes; mod p95 on the host.

**2.5 V21b (criterion 8 / Q9).** Copy the newest `x4mp_ckpt_*.xml.gz` to the scratch slot, `uninstall.ps1`, load it without the mod. Note exactly what X4 says (refuses? loads with warnings? the avatar ships?). Reinstall afterwards.

Quit X4, `collect-logs.ps1 -Label s2`, `sync-report.ps1`.

---

## Sitting 3: two PCs (needs Q1)

**3.0 Network.** PC A: `tools\session4\start-server-lan.ps1` (prints PC A's LAN address and checks the firewall rules for TCP 47780, UDP 47781, HTTP 47790; it tells you the admin command to add them if missing). PC B: install the client kit. Both PCs: same DLCs, build 611726.

**3.1 Join over the LAN.** PC A hosts (authority). PC B joins `<PC A address>:47780`. *Look for:* PC B shows the **download progress** (session-3 open item 10); takeover on PC B; each sees the other as `[MP] <name>`; GUI shows **UDP** active for both.

**3.2 Fly together 30 minutes (criteria 1, 2).** PC A: give your ship an autopilot trip through >= 5 sectors including a gate and a highway (Q2); PC B: fly alongside, then swap roles halfway. *Look for:* smoothness (1-5) on both screens; ghost never far off where the other really is; no Game Over; `[sync]` numbers from `sync-report.ps1` on both PCs.

**3.3 Chat and relations (criteria 9, 10).** Chat both ways (All, Team). Two-team check (Q10): GUI moves PC B's player to team 2, Hostile then Allied: colours on both PCs.

**3.4 Leave / rejoin, reload, checkpoint.** PC B: `/reloadui`; quit to menu and rejoin (back in its avatar where it parked); PC A: **Request save now** during flight (PC B keeps flying; note the host's ~5 s save screen); PC B leaves: its ship stays parked as `[MP] <name> (offline)` on PC A.

**3.5 UDP fallback (criterion 13).** PC A: `start-server-lan.ps1 -BlockUdp` toggles the UDP rule off for 1 minute: GUI shows TCP for PC B within 3 s, no disconnect; then back on.

Quit X4 on both, `collect-logs.ps1 -Label s3a` (PC A) / `-Label s3b` (PC B; the zip is copied to PC A or sent), `sync-report.ps1` on both.

---

## Sending results

Zips `out\session4\logs-<label>-<time>.zip` (game log `x4mp_s4.log`, `x4native\`, `Documents\Egosoft\X4\x4mp\` without `launch.json`, server and FakeNode logs; never saves, passwords or the database); notes with clock times; ratings; `sync-report.ps1` output; screenshots of anything odd.

## Quick reference (outline)

| What | Where |
|---|---|
| Admin GUI | `http://<server PC>:47790` (password file `out\session4\admin-password.txt`) |
| Mod log | `Documents\Egosoft\X4\x4mp\logs\x4mp.log` (`[sync]`, `[perf]`, `takeover:`, `ghosts:`, `avatars:`, `janitor:`) |
| Game log | `Documents\Egosoft\X4\<id>\x4mp_s4.log` |
| Galaxy dump (sitting 0) | `Documents\Egosoft\X4\x4mp\galaxy-dump.json` |
| Ports | TCP 47780, UDP 47781, HTTP 47790 |
