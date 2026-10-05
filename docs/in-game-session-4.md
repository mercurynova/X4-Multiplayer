# In-game test session 4 (M3: two players see each other)

> **Status (2026-10-04): FINALISED.** Sitting 0 ran on 2026-10-03 (results: [spikes/session-4-sitting-0-results.md](spikes/session-4-sitting-0-results.md)). Sittings 1-3 were finalised by
> the kit-check task M3-14 against the merged code: every script, parameter and log line named below exists in the code, and the kit was dry-run without X4
> (`mod/tests/hostsim/session4_dry_run.ps1`: `-Part kit` = CI step `Session4Kit`, `-Part topology` = the three topologies end to end with the real `x4mp.dll` in hostsim).
> Not verified by anything but your eyes: how it LOOKS (ghost model, colours, orientation, smoothness), the chat window, real highways and docks, real frame cost, two real PCs.

Claude can't play X4, so you run these steps and send back the log zips plus your notes. Plan: [m3-plan.md](m3-plan.md) (exit criteria in section 3, spikes in section 5).

| Sitting | When | PCs / X4 copies | Time | What it answers |
|---|---|---|---|---|
| **0** | done | 1 / 1, throwaway kit | ~60 min | Spikes S13.1-S13.12 |
| **1** | next | 1 / 1: real X4 = **client**, FakeNode authority serving your save + 2 FakeNode **wingman** bots | ~70 min | Criteria 2, 3, 5-7, 9 (one-PC part), 10, 11 (client half), 12, 14 |
| **2** | after 1 | 1 / 1: real X4 = **authority**, FakeNode wingmen (3, then 7) | ~60 min | Criteria 4, 5, 8, 11 (authority half), V21b |
| **3** | after 1-2 pass | **2 / 2** (Q1): PC A = server + authority, PC B = client | ~100 min | Criteria 1, 2, 3, 6, 9, 10, 13 over a real LAN |

## START HERE (sittings 1-3, 11 lines)

1. Back up `Documents\Egosoft\X4\<id>\save\` on **every** PC (OneDrive paused; Steam Cloud syncs it). Slots 1-7 are never touched; your working copy is `save_004` (any name works: `-List` shows them). Same build 611726 and **same DLCs** on both PCs.
2. Steam launch options on every PC: `-debug all -logfile x4mp_s4.log`; Steam auto-update for X4 off. In X4 bind **Toggle Chat Window** (Settings > Controls).
3. Two PowerShell windows in the repo folder: window 1 = server / fake nodes (stays open), window 2 = helpers. Always `powershell -ExecutionPolicy Bypass -File <script>`; switches only (`-FreshDownload`, never `-X $false`).
4. Window 2: `mod\build.ps1`, then `tools\session4\install.ps1` (if the sitting-0 kit is still installed: `install-spike.ps1 -Restore` first, or add `-RemoveTestExtensions`). X4 > Settings > Extensions: **Protected UI Mode OFF**, **x4native** and **X4 Multiplayer** on.
5. `out\session4\galaxy-dump.json` (written by `collect-logs.ps1 -Label s0`, 140+ sectors) must exist: the fake nodes use its real sector names. The scripts warn when it is missing.
6. **Sitting 1**: window 1 `tools\session4\start-fake-authority.ps1 -SaveName save_004 -FreshDownload -Wingmen 2`; wait for `checkpoint stored`; start X4; Multiplayer > Join a server: `127.0.0.1:47780`, name **`Tester`**.
7. **Sitting 2**: window 1 `tools\session4\start-fake-clients.ps1 -SaveName save_004 -Wingmen 3`; start X4; Join dialog: name `Tester`, **Host this session as the authority = Yes**, the admin password the script printed; after the load stand 30 s, then sit down: the wingmen start by themselves.
8. **Sitting 3**: PC A `tools\session4\make-client-kit.ps1` and copy the zip to PC B; PC A window 1 `tools\session4\start-server-lan.ps1 -SaveName save_004` (read its firewall output); PC A hosts, PC B joins `<PC A address>:47780`.
9. **After every X4 quit**, on the PC where X4 ran: `tools\session4\collect-logs.ps1 -Label s1` (s2, s3a, s3b), then `tools\session4\sync-report.ps1`. X4 overwrites its log at the next start.
10. **Record the screen** of every PC running X4: OBS (Windows Game Bar `Win+Alt+R` as fallback) into the `Video Recordings` folder in the repo root (git-ignored, never committed), with a clock showing **seconds** in view (Windows taskbar clock with seconds, or an OBS Text source with a clock). Start it before X4 and write down the start time; the clock lets Claude match the footage to log timestamps (ffmpeg pulls single frames).
11. Send Claude: the zip names, the recording file names, your notes with clock times, ratings (1-5), the `sync-report.ps1` output, screenshots where asked. At the end `uninstall.ps1` on every PC and remove the launch option.

## Lessons carried from sessions 2 and 3 (read once)

- The Load Game list hides `x4mp_*` saves; the mod loads them by name. Copy a checkpoint into a normal slot with Explorer when a step asks for it.
- A save load and `/reloadui` both restart our DLL; the connection survives. Write down anything that looks frozen after a load.
- Steam Cloud restores deleted `x4mp_*` files: use `-FreshDownload` on `start-fake-authority.ps1` when you want a real download.
- Quicksave still writes a file on a client (hard block is M4); the janitor removes `[MP] ` ships from it when it is loaded with the mod.
- UIX and SirNukes may stay enabled (session 3 ran with both).
- **New in M3:** your client's NPC world is **not** the host's (NPC sync is M4). Different traffic around you is expected, and local NPCs can attack you (Q12): stay in quiet, friendly sectors. The ships you see of the other players are ghosts named `[MP] <name>`.
- Where you appear (sittings 1 and 2): sitting 1 with wingmen puts the fake host ship and your avatar in the **first sector of the galaxy dump with a gate** (index 1; **Grand Exchange I** in this galaxy dump, seen in sitting 1), at `-HostStand` (default 25 km off-centre, empty space); without wingmen you appear next to where your save's ship really is. If you land in something, change `-HostStand x,y,z` and restart.

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

**1.0 Start** (window 1): `powershell -ExecutionPolicy Bypass -File tools\session4\start-fake-authority.ps1 -SaveName save_004 -FreshDownload -Wingmen 2`
(`-WhatIf` shows the plan; `-HostStand x,y,z` moves where you appear). It prints the plan (galaxy file, `Wingmen   : 2 bots (Wing01..) ... around the player 'Tester'`, your DLCs), starts the server and
`fakenode swarm --with-authority ...`. Wait for `checkpoint stored` and two `[Wing0N] avatar net_id=` lines. The bots wait until a player named **`Tester`** is in game and then orbit it at 400 m.
Admin GUI: `http://127.0.0.1:47790` (password file `out\session4\admin-password.txt`, never printed).

**1.1 Join and takeover (criteria 3, 14).** Start X4 (start menu): **Multiplayer > Join a server**, address `127.0.0.1:47780`, name `Tester`, Connect. Status: download (a real one, no `CACHE HIT` in window 1), load, matching, Connected. After the load you stand in the cockpit of your save's ship; within seconds you are moved into **your own fighter** (an Argon Elite) in empty space; the host ship copy is gone and reappears as the ghost `[MP] Host`.
*Mod log* (`Documents\Egosoft\X4\x4mp\logs\x4mp.log`), in this order: `takeover: standing in ship ... requesting the avatar`, `takeover: avatar granted: net_id=`, `takeover: the avatar is not in the loaded save: spawned a local copy`, `takeover: teleported the player into ship`, `takeover: the guard confirmed (10 frames, ...)`, `takeover: removed the local copy`, `takeover: done in`, then `janitor: swept: 0 leftover`. Lines `remove_blocked_by_guard` are fine, a Game Over is not.
*Write down:* seconds from the end of the load to being in the fighter; whether you saw the HUD hint "Sit in the pilot seat to take over your ship" (then sit down and note it: it means the teleport was refused while standing); **what equipment the fighter has** (Info > ship: early-game parts or Mk2/Mk3? the client's local copy gets no loadout yet, so expect high-end parts; the authority's avatar gets the early-game loadout); quit and join again (**rejoin**) and check the **Last server** line and the pre-filled address and name (criterion 14).

**1.2 See the wingmen (criteria 2, 6).** Fly straight, turn, boost, travel drive. *Look for:* `[MP] Wing01`, `[MP] Wing02` (they orbit you) and `[MP] Host` (parked 300-600 m away), names and colours (team 1), on the map and radar; rate **smoothness 1-5**; do the ghosts **face their direction of travel** (nose forward; sideways, upside down or nose-down means the orientation signs in `GhostsApi::to_pos_rot` must flip: say which); the ship model; no ghost pops (a ghost vanishing and reappearing). Then jump a gate (the wingmen follow and appear in the new sector, no streak), take a local highway and, if near, a superhighway, dock on a pad, dock inside, undock, walk out of the cockpit if you can.
*Mod log:* `ghost spawned: net=... label='[MP] Wing01'` once per ghost (3 in total), every 5 s `[sync] player=Wing01 net=.. frames=.. err_p50/p95/max=.. m steady_p95=.. lat_p95=.. ms` and `[sync] ghosts tracked=3 visible=3 spawned=3 respawned=0 ...`.

**1.3 Reloads (criterion 7).** `/reloadui` twice. *Look for:* no duplicate wingmen, no leftover ships where the old ones were. *Mod log:* `ghosts: init (restored 3 ghost records and N strings from the stash ...)`, then `ghost adoption: records=3 adopted_by_id=.. adopted_by_idcode=.. lost=0`, and in the next `[sync] ghosts` line `spawned=0 ... adopted=3`. Then quit to the menu and join again: you are in a fighter again where you left it (no ghost of yourself anywhere); `janitor: swept: N` with N the number of old copies.

**1.4 Chat (criterion 10).** Toggle Chat Window: `hi all`, then `/t team only`, then `/w Wing01 psst` (the bots answer `echo: ...`; the whisper answer is for you only). *Look for:* do our lines show in the vanilla window outside Ventures, is the author name coloured (team colour), does your own line appear, does a toast show when the window is closed. With SirNukes enabled one of its `/` commands still works. *Mod log:* `chat: sent channel=all bytes=N` (no text).

**1.5 Relations (criterion 9, bot on team 2).** GUI **Teams & Factions**: **+ New team** (team 2), move `Wing02` to it with the member's **Move to...** menu; then click the cell team 1 / team 2 of the relation grid until it says **Hostile**, **Apply relations (1)**, then **Allied**, apply. *Look for:* `[MP] Wing02`'s targeting colour and its name colour change within 2 s each time (target it, look at the map). *Mod log:* `teams: apply seq=N reason=...`, `teams: MD report ... -> ok`. **M3-18:** right after the move `[MP] Wing02` itself (not only the relations) must change to the team-2 colour/faction within 2 s, without flicker or a second ship; FakeNode log: an `EntityChange` for its net id; client log: `ghost` re-own.

**1.6 SETA (criterion 12).** Turn SETA on if you have the item (if you have none, say so). It must go off within 1 s with the notification "SETA is disabled in multiplayer". *Mod log:* `selfship: SETA is active while connected: switch-off requested` then `selfship: SETA is off again after N ms`, or `selfship: SETA was stopped at the source (MD)`.

**1.7 Quicksave (criterion 8, client half).** Quicksave (a file is written). Quit, start X4 **single player**, load that quicksave (the mod loaded): `janitor: swept: N leftover '[MP] ' object(s) removed` with N = ghosts + old copies. Window 2: `tools\session4\savescan.ps1 -Save <the quicksave name> -Role Client` before the load lists them (exit 1); load it single player, save to a **new slot**, scan that: the list is empty (exit 0). Do not pass `-OwnAvatar` on a client: the takeover line's idcode is the *authority's* copy, the client's local copy gets its own idcode (sitting 1).

**1.8 Performance (criterion 11).** Window 1: Ctrl+C, then `start-fake-authority.ps1 -SaveName save_004 -FreshDownload -Wingmen 7`; join again, fly 10 minutes among them. *Look for:* GUI **Players > Tester**: "Mod main-thread cost (p95)" below 0.2 ms, "Realtime lane: UDP", "Ghosts shown 8"; write down your FPS with the 7 wingmen and 1 minute later after `Disconnect` at the same spot.

Quit X4, `collect-logs.ps1 -Label s1`, `sync-report.ps1`.

---

## Sitting 2: real X4 = authority, FakeNode wingmen (one PC)

**2.0 Start** (window 1): `powershell -ExecutionPolicy Bypass -File tools\session4\start-fake-clients.ps1 -SaveName save_004 -Wingmen 3` (`-Target` = the name you host with, default `Tester`). It starts the server, uploads the save, creates and starts the session, prints the in-game admin password (`x4mp-host-test`), then **waits until the session runs and your ship exists** before it starts the wingmen (the real authority can only place their avatars next to your ship).

**2.1 Host and self-spawn (criteria 4, 5).** Start X4: **Multiplayer > Join a server**: `127.0.0.1:47780`, name `Tester`, click **Host this session as the authority** until it says **Yes**, the admin password. After the load **stand in the cockpit for 30 s**, then sit down (sitting 2 run 1: X4 seated the player itself during the first checkpoint save right after the load, so this may not be possible). *Mod log:* `authority: no player ship was reported by MD with the checkpoint; the self-spawn waits for the pilot seat (the player stands)`, nothing more while you stand, then `selfship: the player sat down`, `authority: self-spawn sent net_id=1 ...` within 1 s, `authority: world clock: keepalive WorldUpdate at 20 Hz started`. Window 1 then prints `Your ship exists for the session. Starting 3 wingman bot(s)`. The three bot avatars appear next to you as team ships (names `[MP] Wing01`..), orbit you, and take no damage if shot. *Mod log:* `avatars: PlayerShip from player N: provisioning a new avatar`, `avatars: spawned the avatar of player N: id .. macro ship_arg_s_fighter_01_a_macro owner x4mp_team_1 name '[MP] Wing01' ...`. *Write down:* does the avatar appear clear of any station (get_safe_pos), what equipment it has (**early-game**, not Mk2/Mk3: open its info), does it keep that after a checkpoint and a load.

**2.2 Checkpoint with avatars (criteria 4, 8).** GUI **Sessions & Saves > Request save now** (the host sees X4's save screen for about 5 s: note it). *Mod log:* `janitor: checkpoint check: 0 stale '[MP] ' object(s) ... ghosts_cleaned=true`, `authority: manifest lists 3 avatar(s)`, `stored (save`. Then window 2: `tools\session4\savescan.ps1 -Save <x4mp_ckpt_... name from the log> -Role Checkpoint -Manifest out\session4\data\saves\<sha256>.x4mf` (the manifest is in the server's save store, not next to the save: the newest `.x4mf` there) must end `[MP] objects: 0` and exit 0, with 3 team ships (the avatars). **Check the first real save against the scanner** (`SaveScan` assumes the XML shape `<component class= macro= code= owner= name=>`; if it finds nothing or errors, send the zip). Stop one wingman (Ctrl+C in window 1 stops all; restart with `-Wingmen 2`): the avatar of the one that left stays parked: `avatars: player N left: avatar net_id=.. stays parked at its pose`.

**2.3 Authority reload (criterion 4).** Quit X4 and host again with the same name (it loads the newest checkpoint). *Look for:* the avatars are where they were saved. *Mod log:* `avatars: N avatar record(s) restored`, `avatars: rebound player N net_id=.. to the ship in the loaded universe`, **no** second `avatars: spawned the avatar` line. The ship ids changed by the load; the idcode and name decide.

**2.4 Perf (criterion 11).** Window 1: Ctrl+C, restart with `-Wingmen 7`; 10 minutes; GUI Players > Tester (mod p95), your FPS.

**2.5 V21b (criterion 8 / Q9).** Copy the newest `x4mp_ckpt_*.xml.gz` into a normal slot with Explorer, `uninstall.ps1`, load it **without the mod**. Write down exactly what X4 says (refuses? loads with warnings? the avatar ships?). Reinstall afterwards (`install.ps1`).

Quit X4, `collect-logs.ps1 -Label s2`, `sync-report.ps1`.

---

## Sitting 3: two PCs (needs Q1)

**3.0 Network.** PC A: `powershell -ExecutionPolicy Bypass -File tools\session4\make-client-kit.ps1` (zip in `out\session4\`; it contains the built mod, X4Native and the install / uninstall / log scripts, no secrets) and copy it to PC B (USB stick or share); on PC B unzip anywhere and follow `START-HERE-PC-B.txt` (`tools\session4\install.ps1`; no build tools needed). PC A: `tools\session4\start-server-lan.ps1 -Check` first: it prints PC A's LAN address(es) (**PC B joins `<address>:47780`**) and reads (never changes) the firewall for TCP 47780, UDP 47781, TCP 47790; for a missing one it prints the exact `New-NetFirewallRule ...` command: run it yourself in an **elevated** PowerShell, then `-Check` again. Both PCs: same DLCs, build 611726.

**3.1 Join over the LAN.** PC A window 1: `tools\session4\start-server-lan.ps1 -SaveName save_004` (add `-JoinPassword <throwaway>` if the LAN is not yours). PC A starts X4 and hosts as in 2.1 (`127.0.0.1:47780`, name `Alice`, authority Yes, the printed admin password), stands, sits. PC B starts X4: Join `<PC A address>:47780`, name `Bob`. *Look for:* PC B shows the **download progress** (session-3 open item 10: invisible on loopback), takeover on PC B (as 1.1), each sees the other as `[MP] <name>`; GUI Players > Bob: **Realtime lane: UDP**, Players > Alice likewise.

**3.2 Fly together 30 minutes (criteria 1, 2).** PC A: give your ship an autopilot trip through >= 5 sectors including a gate and a highway (Q2); PC B flies alongside, then swap halfway (the autopilot ship is on PC A's side only while PC A flies: the host's autopilot works as vanilla). *Look for:* smoothness (1-5) on both screens; the ghost never far off where the other really is; no Game Over; `[sync]` numbers.

**3.3 Chat and relations (criteria 9, 10).** Chat both ways (All and `/t`). Two-team check (Q10, 5 minutes): GUI Teams & Factions: move Bob to a new team 2, relation **Hostile**, then **Allied**: the colours of the other player's ghost on both screens within 2 s. No PvP damage yet.

**3.4 Leave / rejoin, reload, checkpoint.** PC B: `/reloadui`; quit to the menu and rejoin (back in its avatar where it parked, no duplicate); PC A: **Request save now** during flight (PC B keeps flying; note the host's ~5 s save screen); PC B leaves: its ship stays parked on PC A as `[MP] Bob (offline)`. A newer checkpoint, then PC B rejoins: it takes over the avatar again (`takeover: the avatar is in the loaded save: local ship ...` is expected only if PC B loaded a save that contains it).

**3.5 UDP fallback (criterion 13).** In an elevated PowerShell on PC A run the line `start-server-lan.ps1 -Check` printed for blocking UDP (`Disable-NetFirewallRule -DisplayName 'X4MP game UDP'`): within 3 s the GUI shows **TCP** for Bob (the mod logs only `net: udp: binding to ...` at the start; the state change is visible in the GUI), no disconnect, the flight continues; after a minute run the enable line (`Enable-NetFirewallRule -DisplayName 'X4MP game UDP'`): UDP returns within about 30 s (re-probe). This script never changes the firewall; only you do.

Quit X4 on both, `collect-logs.ps1 -Label s3a` (PC A) / `-Label s3b` (PC B; send the zip), `sync-report.ps1` on both.

---

## Exit criteria and what each sitting answers (m3-plan section 3)

| # | Criterion | In game | Without X4 (CI, `HostSimM3` unless said) |
|---|---|---|---|
| 1 | Fly together 30 min | 3.2 | pending the two-PC sitting |
| 2 | Ghost quality (path error, latency, no pops) | 1.2, 3.2, `sync-report.ps1` | host ghost path error < 10 m p95 from the mod's `[sync]` lines; 0 respawns |
| 3 | Avatar takeover, rejoin | 1.1, 1.3, 3.1, 3.4 | takeover, guard, host copy removed, adoption after `/reloadui` |
| 4 | Avatars on the authority | 2.1-2.3 | 7 avatars driven, parked, in the manifest, rebound after a renumbering load (`HostSimAvatars` too) |
| 5 | Pilot-seat self-spawn | 2.1 | `authority_flow.hostsim`, `session4_authority.hostsim` |
| 6 | Ghost lifecycle (gate, highway, dock, leave, kick) | 1.2, 3.2, 3.4 | gate jump; Hidden / highway flags in Catch2 and `pair_scenario.hostsim` |
| 7 | Reloads | 1.3, 3.4 | 3 `/reloadui` with adoption (`reloadui` command), `HostSimGhosts` 20 reloads |
| 8 | Save hygiene v1 | 1.7, 2.2, 2.5 | pre-save check clean, `savescan` on fixtures |
| 9 | Teams in game | 1.5, 3.3 | hub tests |
| 10 | Chat | 1.4, 3.3 | both ways + bot echo |
| 11 | Performance | 1.8, 2.4, `sync-report.ps1` | mod p95 about 0.10 ms client / 0.12 ms authority with 7 remote players, about 1-2 kB/s per client |
| 12 | SETA off in 1 s | 1.6 | pair scenario (hostsim) |
| 13 | UDP lane + fallback | 3.1, 3.5 | `UdpLane` step; GUI/API `udpActive` asserted |
| 14 | Remembered fields | 1.1 | hostsim |
| 15 | No regressions | - | CI + `./tools/e2e.ps1` |
| 16 | S13 verdicts | - | recorded by the lead |

## In-game checks the code could not settle (collected from the M3 handoff notes)

Look at these during the sittings; each is "does it behave like the code assumes?":

- **Map with the HUD on (M3-16)**: open the map and leave it open > 30 s; it must stay intact (no blur, no empty 3D view), also over Esc menu / ship config / trade. Log shows "hud: blocked by MapMenu (live), not forcing"; the line returns after the menu closes. (M3-16)
- **Loadout**: the authority's avatar has early-game equipment (`apply_loadout` of the basic Elite loadout replaces the spawn default; are the `../con_*` paths right); the client's local copy of its own ship currently has the **default** equipment (known gap, report what it is). (2.1, 1.1)
- **Safe position**: MD `get_safe_pos` result and its number format; no avatar inside a station. (2.1)
- **Ghost orientation**: yaw / pitch / roll signs (`kYawSign` ...), model, name colour, radar. (1.2)
- **Velocity hints**: MD `set_object_velocity` accepted at 5 Hz together with the per-frame set; the batch list shape. (1.2)
- **Gate jump**: `SetObjectSectorPos` with sector-local metres across a gate, `ConvertStringTo64Bit` for sector components. (1.2, 3.2)
- **Takeover**: works standing without the hint; time to the guard; idcode and name survive a checkpoint (`takeover: the avatar is in the loaded save: local ship ...` for a returning player); `GetAllFactionShips("x4mp_team_k")` lists the avatars after a load; is the own ship's name `[MP] <name>` a problem. (1.1, 3.4)
- **Idcode rebind** after a load (avatars on the authority). (2.3)
- **Teams**: the Lua array reaches MD as a list; the effect of the `<relations>` block (Xenon / Khaa'k hostile to team ships); re-sending relations while locked; a checkpoint with active team factions loads **without** the mod (Q9, 2.5). (1.5, 2.5)
- **Chat**: the vanilla window outside Ventures, the coloured author, `menu.shown` while faded, clicking a name. (1.4)
- **SETA**: whether `stopactivity` ends a started SETA (only if you have the item). (1.6)
- **SaveScan** against the first real checkpoint and quicksave (XML shape; `RemoveComponent` and `GetAllFactionShips`; `GetComponentName` of stations). (1.7, 2.2)
- **Janitor sweep lines**: `janitor: waiting for the takeover to finish before sweeping`, `swept: ...`, `checkpoint check: ...`. (1.1, 1.7, 2.2)
- **Remembered fields** survive a run without the mod. (1.1)

## If something does not work

| Symptom | What to do |
|---|---|
| A script says `Galaxy dump ... : N sectors (FEWER than 140)` or `No galaxy dump` | Sitting 0 first (`collect-logs.ps1 -Label s0`), or pass `-GalaxyFile`. Without it the bots use sectors your X4 does not have |
| The wingmen never appear (sitting 1) | The Join name must be exactly `Tester` (or pass `-Target`). Window 1 shows `[Wing01] avatar net_id=` when they have their avatars |
| The wingmen never start (sitting 2) | The script waits for your ship: sit down in the pilot seat; it prints `session Running; waiting for your ship` meanwhile. A bot that does not get its avatar within 90 s stops with `no avatar within 90 s` |
| No `[MP]` ghosts at all on the client | `rep_msgs=0` in the `[sync] ghosts` line = no `Replication`: is the host ship in the GUI Players list with a ship (authority), is the session Running |
| Stuck in the host's ship after the load | Look for `takeover:` lines; sit in the pilot seat (hint); send the log |
| `start-server-lan.ps1` says `UNKNOWN (could not read the rules)` | Normal without admin rights for some profiles; use the printed commands in an elevated PowerShell, test the join from PC B |
| PC B cannot connect | `Test-NetConnection <PC A address> -Port 47780` on PC B; the three firewall rules on PC A; same network (not "guest") |
| X4 crashes or Game Over | Quit, **collect the logs**, note the last step and the clock time |
| `collect-logs.ps1` says no lines | The launch option `-debug all -logfile x4mp_s4.log` is missing |
| `install.ps1` refuses | The sitting-0 kit is still installed: `install-spike.ps1 -Restore` or `-RemoveTestExtensions` |

## Sending results

Zips `out\session4\logs-<label>-<time>.zip` (game log `x4mp_s4.log`, `x4native\`, `Documents\Egosoft\X4\x4mp\` incl. `x4mp-lines.txt` and `sync-report.txt`, server and FakeNode logs; never saves, passwords, `launch.json` or the database); your notes with clock times; ratings; the `sync-report.ps1` output; screenshots of anything odd.

## Quick reference

| What | Where |
|---|---|
| Scripts | `tools\session4\`: `install.ps1`, `uninstall.ps1`, `start-fake-authority.ps1 [-Wingmen N] [-HostStand x,y,z]`, `start-fake-clients.ps1 [-Wingmen N] [-Target name]`, `start-server-lan.ps1 [-Check]`, `upload-save.ps1`, `make-client-kit.ps1`, `collect-logs.ps1 -Label`, `sync-report.ps1 [-Log \| -Zip]`, `savescan.ps1` |
| Admin GUI | `http://<server PC>:47790` (password file `out\session4\admin-password.txt`) |
| Mod log | `Documents\Egosoft\X4\x4mp\logs\x4mp.log` (`[sync]`, `[perf]`, `takeover:`, `ghost`, `avatars:`, `janitor:`, `selfship:`, `authority:`) |
| Game log | `Documents\Egosoft\X4\<id>\x4mp_s4.log` |
| Ports | TCP 47780, UDP 47781, HTTP 47790 |
| Targets (`sync-report.ps1`) | path error p95 < 50 m (< 10 m steady below 300 m/s), display latency <= 200 ms, mod main-thread p95 < 0.2 ms, < 20 kB/s per client, < 10 log lines/s, 0 ghost respawns |
