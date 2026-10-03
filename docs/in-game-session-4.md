# In-game test session 4 (M3: two players see each other) - OUTLINE

> **Status (2026-10-03): Sitting 0 is FINALISED (runnable); sittings 1-3 are an OUTLINE.** For sittings 1-3 script names, labels, log lines and timings below are the plan.
> Sitting 0 is finalised by the wave-0 kit tasks (M3-001/002) before it runs; sittings 1-3 are finalised by the
> **kit-check task M3-14** against the merged code (every script exists, every log line is real, dry run in hostsim),
> as M2-14 did for session 3. Do not start sittings 1-3 until their sections say "finalised".

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
5. Sitting 0: follow its own **0.A START HERE** below (`install-spike.ps1`, `write-probe-config.ps1 -ScratchSlot`, `run-block.ps1`, `collect-logs.ps1 -Label s0`, then `install-spike.ps1 -Restore`).
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

## Sitting 0: spikes S13 (before coding, one PC, ~60-75 min) - FINALISED (M3-002)

One PC, no server, no FakeNode. The throwaway test kit (`x4native` + `x4mp_probe` native DLL + `x4mp_spike` Lua/MD) replaces the product mod for the
sitting. Everything that spawns, moves you or removes a ship runs on a **scratch save slot** you can lose.

### 0.A START HERE (sitting 0 only)

1. **Back up** `Documents\Egosoft\X4\<id>\save\` (copy the folder somewhere else). OneDrive paused. Steam Cloud syncs this folder and restores deleted files (session 3 A1); that is why the scratch slot is a *numbered slot you overwrite*, never a file you delete.
2. Steam launch options: `-debug all -logfile x4mp_s4.log`. Steam auto-update for X4 **off**. Build 611726.
3. In X4 Settings > Extensions: **Protected UI Mode OFF** (the spike's Lua is not loaded otherwise). Bind **Toggle Chat Window** (Settings > Controls) for step 0.13.
4. PowerShell window in the repo folder. Always `powershell -ExecutionPolicy Bypass -File <script>` (switches only, no `-X $false`).
5. Build the probe once: `powershell -ExecutionPolicy Bypass -File mod\build.ps1 -Spikes` (needs the build tools, see `docs\dev-setup.md`).
6. Install the kit (X4 must be closed): `powershell -ExecutionPolicy Bypass -File tools\session4\install-spike.ps1`. It **parks the product `x4mp` outside the X4 install** (`out\session4\parked\`), switches it off in your `content.xml` (backup `content.xml.x4mp-bak`), installs the kit and unblocks the files. Never touches saves. (`-WhatIf` shows what it would do.) The product and the kit must never be enabled together.
7. Write the probe settings: `powershell -ExecutionPolicy Bypass -File tools\session4\write-probe-config.ps1 -ScratchSlot save_007` (`7` also works). It writes `hooks=false` (with hooks the probe pins its DLL: session-2 lesson), no server, no password, and the scratch slot name; it tells you whether `save_007.xml.gz` exists.
8. **After every X4 quit run `tools\session4\collect-logs.ps1 -Label s0`** (before the next X4 start: X4 overwrites its log). Send the zip file name plus your notes.
9. At the very end: `install-spike.ps1 -Restore` (kit out, product back and enabled), remove the launch option.

### 0.B Scratch slot setup (once, about 3 minutes)

The scratch slot is overwritten by the sitting (the persistence step saves into it). Pick a game you can afford to lose:

1. Start X4 (kit installed), **load** the save you want to test with (a normal, single-player game; a quiet Argon/Teladi sector is best, no wars nearby).
2. Fly to **open space** in a friendly, quiet sector (not near a gate, not in a mining field) and stop.
3. Esc menu > **Save Game** > choose **slot 7** (`save_007`). This is the scratch copy; the original slots stay untouched. If the slot already holds something, it is overwritten: check the name first.
4. Keep playing in that same session. Whenever a step says "load the scratch save", load **slot 7**.
5. Steam Cloud note: Steam may restore an older `save_007` at the next X4 start. If a step behaves like an old situation, load slot 7 and save again over it before continuing.
6. The probe checks the *loaded* save's name against the scratch slot. A line `REFUSED ... scratch` in the log means you are on another save: load slot 7 and run the block again (the log prints the raw value it saw).

### 0.C How a block is started and where to look

`powershell -ExecutionPolicy Bypass -File tools\session4\run-block.ps1 <block> [args]` (second window or the Terminal). The probe re-reads its settings file every few seconds; X4 shows a short on-screen message (`X4MP spike: ...`) when a block starts. Do not use Alt-Tab-heavy workflows: leave X4 in **windowed or borderless** mode so you can run the command. Other usable launcher: the in-game chat window (`/x4mpspike <block> args`) for the Lua blocks.
Logs (collect-logs picks them all up): the game log `x4mp_s4.log` (`[X4MP-SPIKE] S13.n ...`) and the probe log `x4native\x4mp_probe\x4mp_probe.log` (`[X4MP-PROBE] ... s13 <S13.n> block=<name> PASS|FAIL|INFO|WARN|REFUSED`). You never need to read them: you send them. Ratings are 1-5 (1 = bad, 5 = perfect) with a short sentence.

### 0.D The steps (in this order; the order matters)

Each step: **Do** = what you run/do; **Look** = what you watch; **Rate/Note** = what you write down (always add the clock time).

| Step | Spike | Do | Look | Rate / note |
|---|---|---|---|---|
| 0.1 | smoke | `run-block.ps1 list`, then `run-block.ps1 s13_check`, `run-block.ps1 ping` | Messages "X4MP spike: blocks: ..." and "ping ok". If none appears: see "If something does not work" | Did both messages appear? |
| 0.2 | S13.5 seat | **Load the scratch save (slot 7)**. Right after the load, while still **standing in the cockpit**: `run-block.ps1 seat` (60 s). Stand 15 s, sit down in the pilot seat, 15 s, stand up again, 15 s, walk out of the cockpit if you can | nothing special; just do it slowly | Clock times of: loaded, "I stand", "I sit down", "I stand up", "I walked out" |
| 0.3 | S13.6 takeover | **Load slot 7 again**. Right after the load (still standing): `run-block.ps1 takeover` | A fighter appears about 300 m away; you are moved into it; the old ship stays behind and disappears a few seconds later. **Never** more than one block at a time | Did it work (yes/no)? Anything odd (black screen, you are on foot, game over, two ships)? Time until you could fly the new ship |
| 0.4 | S13.6 docked | Dock **inside** a station (hangar/dock, not a pad), then Esc > Save Game > **slot 7**, then **load slot 7** (you wake up docked). `run-block.ps1 takeover_docked` | As 0.3 but from a docked state (it refuses by itself when you are not docked) | Same as 0.3 |
| 0.5 | S13.7 teams | **Load slot 7 again** and fly to open space in the quiet sector. **First** `run-block.ps1 teams_product` (this activates the team factions; run it BEFORE `ghost_spawn`, otherwise the ghosts fall back to "ownerless"). Two Argon fighters (MP Team 1 / MP Team 2) appear about 1.5 km ahead. Target both (look around with the target key; open the Map). Then shoot **MP Team 2** (the hostile one) for about 5 s with a weak weapon. Then `run-block.ps1 teams_report`, then `run-block.ps1 teams_end` | Targeting colour and name of each (team 1 should read as friendly/allied, team 2 as hostile, each in its own team colour on the map?). Is the hostile one shown red? Does its hull stop at 50 % (the block sets a minimum)? | Colours you see (words), hull % of team 2 after shooting, anything attacking you |
| 0.6 | S13.1 | `run-block.ps1 ghost_spawn` (about 70 s). Two ships appear 1 km ahead (small one left, bigger one right). Target each, open the Map, wait the full 60 s | Name `[MP] ...` shown? colour (team 2)? visible on radar/map? do they move by themselves (they must not)? | yes/no for name, colour, radar, moves; the colour in words |
| 0.7 | S13.2 motion | `run-block.ps1 ghost_motion a`, wait until the on-screen message says it is done (about 100 s), then `ghost_motion b`, then `ghost_motion c`. Each runs the ghost on a circle at 100 / 300 / 600 m/s and on a fast line | Smoothness while it circles near you. (c adds an engine-velocity helper at 5 Hz.) Trails / engine glow | **Rate smoothness 1-5 for a, b and c** separately (also say if fast speeds look different). Pass target: a or c rated 4 or more |
| 0.8 | S13.2 collision | After 0.7 the ghost is parked. Fly **slowly** (under 30 m/s) into it | What happens: bounce? ghost pushed away? your hull damaged? nothing? | One sentence + hull % before/after |
| 0.9 | S13.3 | `run-block.ps1 ghost_xsector`. The S ghost moves into another sector for 20 s, then back. Open the **Map** and look for it in the other sector during that time | Is it visible there on the map (as a ghost/ship icon)? | yes/no, which sector |
| 0.10 | S13.10 pause | `run-block.ps1 pause_move`, then within 60 s open the **Esc menu** and keep it open for 20 s, look behind the menu, then **close it with Esc** (never click Save/Load while it runs) | Does the ghost keep moving behind the menu? | yes/no; any freeze or error after closing |
| 0.11 | S13.4 sample | `run-block.ps1 sample` (120 s), one manoeuvre group per run, run it **three times**: (a) cruise, boost, travel drive, turns; (b) a local highway (a ring-shaped speed highway inside a sector) and a **superhighway** (between sectors, as far as available) and a gate jump; (c) dock on a **pad**, undock, dock **inside**, undock, **stand up** and walk, sit down, turn SETA on and off | Nothing visible; the log is matched to your times | Clock time of every manoeuvre (to the second where you can) |
| 0.12 | S13.9 SETA | `run-block.ps1 seta` (120 s): turn SETA **on** (you need the SETA item; if you have none, say so). Wait 5 s. Then turn it **on** again after it was switched off. Also try the Lua path once: with SETA on, `run-block.ps1 seta_off` | Was SETA switched off by itself within about 1 s? A notification? | yes/no, time it took, did SETA come back on by itself? |
| 0.13 | S13.8 persistence | `run-block.ps1 persist_spawn` (a team ship appears next to you; note its name). Esc > Save Game > **slot 7**. Then **load slot 7**. Then `run-block.ps1 persist_check` and `run-block.ps1 md_table` | The ship is where you left it? still named? does not move on its own? | yes/no for each |
| 0.14 | S13.11 | `run-block.ps1 galaxy_dump` (a few seconds) | On-screen message "galaxy_dump: N sectors" | N; "file written" or "logged" (both are fine) |
| 0.15 | S13.12 chat | Make sure no Ventures account is needed. `run-block.ps1 chat`, then **open the chat window** with your Toggle Chat Window key: lines from **Alice** (orange) and **Bob** (default colour) should be there. Type `hello` and Enter. Type `/x4mpspike list` too. Later `run-block.ps1 chat off` | Shown at all? names in colour (Alice orange, Bob another colour)? your own `hello` appears in the window? | yes/no for each, the colours in words |
| 0.16 | cleanup | `run-block.ps1 cleanup` (removes the probe's spawned ships; the ship you sit in is refused by design) | the ghosts disappear | Anything left behind? |
| 0.17 (optional, 10 min) | S11 | session-2 `diplo1`, `diplo2` as in `docs\in-game-session-2.md` part E | as there | as there |

Between steps you may run `run-block.ps1 s13_status` (what is active) or `run-block.ps1 s13_stop` (abort the active native block).

### 0.E After the last step

1. Quit X4 normally. Run `powershell -ExecutionPolicy Bypass -File tools\session4\collect-logs.ps1 -Label s0`. It zips the game log, the probe logs, your probe settings (no password), and `galaxy-dump.json` (read from `Documents\Egosoft\X4\x4mp\galaxy-dump.json`, or rebuilt from the game log; a copy is kept in `out\session4\galaxy-dump.json` for the FakeNode in sittings 1-2). It prints the sector count: **it must say 140 or more**.
2. `powershell -ExecutionPolicy Bypass -File tools\session4\install-spike.ps1 -Restore` (kit out, product back and enabled).
3. Send Claude: the zip file name, your notes with clock times, your ratings, and screenshots of the colours (0.5, 0.6, 0.15) if easy.

### 0.F If something does not work

| Symptom | What to do |
|---|---|
| No on-screen message after `run-block.ps1` | Wait 10 s (the probe re-reads the file every few seconds). Check the X4 Extensions list: `x4native`, `x4mp_probe`, `x4mp_spike` enabled, Protected UI Mode OFF, and **`x4mp` (the product) not enabled**. Check `run-block.ps1` printed `spike_block = ...`. If still nothing, collect the logs and send them |
| "X4MP spike: unknown block" | Typo in the name; `run-block.ps1 list` shows the Lua blocks, the native ones are in 0.D |
| `REFUSED` in the probe log | Not on the scratch save (or not docked for `takeover_docked`). Load slot 7 (or dock first), then run the block again |
| `run-block.ps1` says it needs a scratch slot | `write-probe-config.ps1 -ScratchSlot save_007` first |
| Ghosts show the wrong colour or "ownerless" | You ran `ghost_spawn` before `teams_product`. Run `teams_product`, then `cleanup`, then `ghost_spawn` again |
| The ghost appears above/below instead of ahead | Tell Claude; the probe has a `pitch_sign` setting for it |
| A block seems to hang | `run-block.ps1 s13_stop`; only one native block runs at a time (starting another aborts the first) |
| X4 crashes or you get Game Over | Do not repeat it: quit, **collect the logs**, load slot 7 (never your real saves), write down the last step and the clock time |
| `collect-logs.ps1` says no `[X4MP-...]` lines | The launch option `-debug all -logfile x4mp_s4.log` is missing, or X4 was started with another log name (`-GameLogName <name>`) |
| Sector count below 140 | Send the log anyway; say which DLCs are enabled |
| `install-spike.ps1` says X4.exe not found / probe dll missing | Pass `-X4Dir "<folder with X4.exe>"`; run `mod\build.ps1 -Spikes` first |
| You want to start over | `run-block.ps1 cleanup`, load slot 7 (Steam Cloud may have restored an older one; save over it again) |

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
