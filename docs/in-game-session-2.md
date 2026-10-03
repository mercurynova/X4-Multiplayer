# In-game test session 2 (native probe, saves, menus, on-foot, diplomacy, HQ)

> **Status (2026-10-02): the builds this script needs do not exist yet.** They are the pre-tasks
> M2-001 to M2-006 in [m2-plan.md](m2-plan.md) §5.1. Claude tells you when the kit is built and merged. Until then
> this is the plan for the session, so you can prepare the saves.

Claude can't play X4, so you run these steps and send back one zip of logs plus your notes. Everything runs on
**one PC** with **one copy** of X4. The X4MP server and a fake "authority" (FakeNode) run on the same PC.

## What this session answers

| Part | Sitting | Time | Questions (ids from [decisions.md](decisions.md) Part 3 and [roadmap.md](roadmap.md) §4) | Blocks |
|---|---|---|---|---|
| A | 1 | 15 min | Setup | — |
| B | 1 | 40 min | Native DLL in the start menu; download + load of a session save by name; pause at "universe ready"; what a save load and `/reloadui` do to our DLL (V05, S5); which thread runs each callback (V07) | **M2** |
| C | 1 | 40 min | Save control (V06, S6); custom save names; game clock for `EntitySpawn.game_time`; money units on the native path (V04 follow-up); MD object variables (V12 retest); opening a locked gate (S9) | **M2** |
| D | 1 | 25 min | Menu entry (V20/R1), standalone menu over the start menu, X4Native settings button, password box (V22), HUD (V23), extension list (R7/V28), web links (R8/V29); optional R3–R6 with SirNukes/UIX | **M2** |
| E | 2 | 60–75 min | Team diplomacy S11 (ADR-047) and per-team HQ/research S12 (ADR-048) | M5 and later |
| F | 3 | 60 min | On-foot presence S10 (ADR-046) | M3b/M3c |

**Sitting 1 (parts A–D, about 2 hours) is the one M2 waits for.** Sittings 2 and 3 can happen any day after.
Each sitting starts with Part A steps A6–A9 (start the server and X4) and ends with "Sending results".

---

## Part A. Before you start

### One-time preparation (about 10 minutes)

**A1. Back up your saves folder.** It is `Documents\Egosoft\X4\<your numeric id>\save\` (on this PC it is under
OneDrive). Copy the whole folder somewhere safe. Pause OneDrive sync for the duration of each sitting.

**A2. Prepare the test saves.** Load a save you are happy to experiment with and save it to a **new slot**; quit.
Write down each file name (for example `save_012`).

| Save | Needed for | What it should be |
|---|---|---|
| **T** | Parts B, C, D | A normal mid-game save. You are **in space, in your own ship**, not docked, somewhere quiet |
| **A** | Part E (S12) | A save **with a Player HQ and research unlocked**, ideally one research running. If it also has an embassy and an agent, S11.4 can run |
| **B** | Part E (S12) | An **early** save without a Player HQ |
| **F** | Part F (S10) | **Docked** at a large NPC station that has a trader corner, ideally a bar; you own at least one S or M ship (an L/XL ship is a bonus) |

**Status (2026-10-02):** the user has prepared saves T and A as **one save in slot 1** (mid-game, with a Player HQ;
it serves both Parts B–D and Part E) and save B in **slot 2**. Copy each into a new slot before testing, as A2 says, so
the originals stay untouched. Save F is still open: no vanilla station with a bar has turned up yet. Part F (S10) is
sitting 3 and does not block M2. Prefer a vanilla bar; a room-generating mod is acceptable as a last resort, but then
S10 results must be marked "modded rooms" because the mod may change the room layout we are probing.

Diplomacy and HQ blocks (Part E) change the test save; most revert themselves, but `diplo1`, `diplo4`, `hq3d` and `hq6` leave permanent changes. Only ever run them on a copy. Do not pause the game during `diplo3`/`diplo5` (they wait 70 s of real time for a vanilla check that pausing stops).

All of these saves must have been made **without** the test extensions installed. Throw them away after the
session (they will contain test objects).

**A3. Steam launch options** (Library → X4 → Properties → Launch options):
`-debug all -logfile x4mp_s2.log`
The game log is then written to `Documents\Egosoft\X4\<your numeric id>\x4mp_s2.log`.

**A4. In X4** (start the game once if needed): Settings → Extensions: **Protected UI mode OFF** (restart X4 if it
asks). Disable every third-party mod for sittings 1–3, unless you want to run the optional step D8 (then keep
SirNukes Mod Support APIs and/or kuertee UI Extensions enabled for Part D only). Steam: make sure **automatic
updates for X4 are off** (the mod only accepts build 611726).

**A5. Install the test extensions.** In the repo folder, in PowerShell:
```
powershell -ExecutionPolicy Bypass -File tools\session2\install.ps1
```
It copies three folders into `<X4 install>\extensions\` and nothing else: `x4native` (the X4Native framework),
`x4mp_probe` (our native test DLL) and `x4mp_spike` (our Lua/MD test extension). It refuses to run if the real
`x4mp` extension is installed (they must not run together). In X4 → Settings → Extensions, all three must show as
enabled (X4 may ask to restart).

### At the start of every sitting

**A6. Start the local server and the fake authority.** In PowerShell, in the repo folder:
```
powershell -ExecutionPolicy Bypass -File tools\session2\start-server.ps1 -SaveName <save T file name, without .xml.gz>
```
Leave the window open. It prints:
- the **admin GUI address**: `http://127.0.0.1:47790`;
- where the **admin password** is (`out\session2\data\initial-admin-password.txt`). If the GUI asks you to change it
  on first login, pick a new one and write it down;
- `FakeNode authority: checkpoint stored (sha …)`. This means the server now offers a copy of save T as the session
  save.

For sitting 2 use save A, for sitting 3 save F (`-SaveName`). If you see an error instead, stop and send
`out\session2\` (Claude will look).

**A7. Write the probe's config.** In a second PowerShell window:
```
powershell -ExecutionPolicy Bypass -File tools\session2\write-probe-config.ps1
```
Answer: server `127.0.0.1:47780`, name `Tester`, no password. It writes
`Documents\Egosoft\X4\x4mp\x4mp_probe.json`. (The mod reads its settings from this file, never from environment
variables.)

**A8. Open the admin GUI** in a browser at `http://127.0.0.1:47790`, log in, and keep the **Players** and
**Events** pages at hand.

**A9. Note the time** (clock time) when you start X4. Write times next to anything odd you see; it makes the logs
much easier to read.

---

## Part B. Native probe: join, load, reload (sitting 1)

The probe connects to the server by itself, downloads the session save and loads it. You mostly watch.

**B1. DLL in the start menu.**
1. Start X4. Stay in the **start menu** (do not load anything).
2. Wait 30 seconds.
3. Look at the admin GUI → Players: a player **`Tester`** should appear (connected, waiting for the save or
   downloading).

Look for: `Tester` appears within 30 s. Write down if it does not.

**B2. Download and load by name.**
1. Still in the start menu, wait. The probe downloads the save (a few seconds for a normal save) and then **loads
   it by itself**. You see the normal loading screen.
2. If nothing has happened after 2 minutes: open **Load Game**. Look for the newest entry (it has the same in-game
   name as save T, with today's time). Note whether such an entry exists at all, then load it yourself.

Look for: the game loads save T's content. Note how long the loading screen took.

**B3. Paused at "universe ready".**
1. When loading finishes, the game should be **paused** for about 20 seconds, then unpause by itself.
2. During the pause: open the **map** (default `M`), wait 3 s, close it. Note whether the game unpaused when the map
   closed.

Look for: paused right after loading (yes/no); unpaused by closing the map (yes/no); unpaused after ~20 s (yes/no).

**B4. What the save load did to our DLL (automatic).** Nothing to do. The probe logged the shutdown/re-init order,
whether its memory ("stash") survived, and how long its network thread took to stop.

**B5. Invisible to the server.**
1. In the admin GUI → **Events**, look at the entries for `Tester` since you started X4.

Look for: **no** "left" followed by "joined" for `Tester` while the save loaded. A "resumed" entry is the good
outcome. Write down what you see.

**B6. `/reloadui` while in game.**
1. Open the chat window (find its key under Settings → Controls, search for "chat"), type `/reloadui`, press Enter.
2. If the chat window does not open, tell PowerShell instead:
   `powershell -ExecutionPolicy Bypass -File tools\session2\run-block.ps1 reloadui`
   (the probe then triggers the UI reload itself within ~5 s).
3. Check GUI → Events again.

Look for: the UI flickers/reloads; `Tester` stays connected; no left/joined pair.

**B6b (only if Claude asks after reading the logs).** Repeat B1–B6 with the module-pin option on:
`run-block.ps1 pin_on`, then quit X4 to the desktop and start again from B1.

**B7. Threads (automatic).** Logged throughout. Nothing to do.

---

## Part C. Saves, clock, money, retests (sitting 1, in game with save T loaded)

Run each block with the chat command shown, or, if chat does not work, with
`powershell -ExecutionPolicy Bypass -File tools\session2\run-block.ps1 <block>`. On-screen notifications say
`X4MP spike: <block> …` and tell you when it wants you to do something.

**C1. Menu save goes through our wrapper.** Block `saves1`.
1. Run `/x4mpspike saves1`. Wait for "saves1: wrapper installed (logging only)".
2. Open the menu → **Save game** → save to a **new slot**. Note the slot.
3. Run `/x4mpspike saves1_block`. Wait for "saves1: blocking ON".
4. Open the menu → **Save game** again.

Look for: in step 4 the save entries are **greyed out** and a tooltip says saving is disabled. Note the exact text
if any. Close the menu.

**C2. Autosave.** Block `saves2`.
1. Run `/x4mpspike saves2`. The block turns on our autosave blocker and asks the game for an autosave in two ways
   (through the game's own autosave trigger and through our probe). Wait for "saves2: done".
2. Settings → Game: note your **autosave interval** setting, then set it to the **shortest** value.
3. Fly through a **gate or highway** into another sector (vanilla autosaves on sector change once the interval has
   passed). Wait 2 minutes, change sector once more.
4. Open **Load Game** and look at the autosave entries.
5. Put the autosave interval back to what it was.

Look for: **no new autosave** since you started the block. Note the newest autosave time you see.

**C3. Quicksave.**
1. Blocking is still ON from C1. Press the **Quicksave** key (Settings → Controls, search for "quicksave").
2. Open **Load Game**.

Look for: a new quicksave appeared (yes/no). Either answer is useful.

**C4. Custom save name and timing.** Block `saves4`.
1. Run `/x4mpspike saves4`. The block turns blocking off, saves as `x4mp_s2test_1` and measures how long it takes.
2. Wait for "saves4: done".
3. Open **Load Game**: is there an entry from just now? Note how it is named in the list.

**C5. Game clock.** Block `clock` (runs 4 minutes, logs once a second).
1. Run `/x4mpspike clock`.
2. For 30 s fly normally.
3. Press `Esc` (pause menu) and stay there 20 s, then close it.
4. Turn on **SETA** for 20 s (Settings → Controls, search for "SETA"), then off.
5. When "clock: save now" appears: save to a new slot, load that save, and wait until "clock: done".

**C6. Money on the native path.** Block `money`.
1. Note your credits (top of the screen or the empire menu).
2. Run `/x4mpspike money`. It adds 1 credit, removes it again and logs the units it sees.
3. Check that your credits are back to exactly the noted value.

**C7. Retest V12, object variables.** Block `v12`.
1. Run `/x4mpspike v12`. Wait for "v12: set".
2. Save to a new slot, load that save, wait 20 s.

Look for: "v12: verify" lines appear after the load (the result is in the log).

**C8. Retest S9, opening a locked gate.** Block `s9gate`. **Only in the throw-away save T.**
1. Run `/x4mpspike s9gate`. The block activates one gate that is still closed in your save and tells you which
   sector it is in.
2. Open the map and look at that gate. Note whether it shows as active.
3. Save to a new slot, load it, look at the gate again.

---

## Part D. Menus and UI (sitting 1)

**D1–D3. Menu entry and standalone menu.**
1. Quit to the **start menu** (keep X4 running).
2. In PowerShell: `run-block.ps1 ui`. Wait 10 s.
3. Look at the start menu list. Is there a row **"Multiplayer (X4MP test)"**? Click it if present and note what
   opens. Go back.
4. `run-block.ps1 ui_standalone`. A small test window should open **over** the start menu. Note whether it shows,
   whether you can click its button, and whether it closes with its close button.
5. Settings → Extensions → **X4Native** settings page → look for an **"X4MP probe: test button"** row. Click it.
   A notification "probe button clicked" should appear.

**D4. Password box.**
1. In the standalone test window (step 4), there is a field "Test password". Type `Banana-Test-42`.
2. Note whether the text shows as dots/stars or plain text. Click "Check". The log records only the **length**.

**D5. HUD.**
1. Load save T again. When in space: `/x4mpspike hud`. A small box "X4MP test HUD" should appear at the top right.
2. Open and close the map, the pause menu and a station menu (if near one).

Look for: the box stays visible in the cockpit, does not steal the mouse, and comes back after each menu.

**D6. Extension list.**
1. `/x4mpspike extensions`. Nothing to see; it logs the extension list.
2. Open Settings → Extensions and take one **screenshot** of the list.

**D7. Web links.**
1. `/x4mpspike links`. A test window with three buttons opens: "Nexus page", "Workshop page", "Steam link".
2. Click each one. Note for each: did a browser or the Steam overlay open, and which page?

**D8 (optional, only with SirNukes and/or UIX enabled).** Repeat D1–D3 and B6 once with each library combination
you have (none, SirNukes only, UIX only, both). Also in game: type a SirNukes `/` command you know in chat and note
whether it still works. X4 needs a restart for each combination.

---

## Part E. Team diplomacy (S11) and per-team HQ (S12) (sitting 2)

Start the server with `-SaveName <save A>` (step A6), then start X4 and wait in the start menu: the probe downloads
and loads save A's copy by itself, as in B2. If nothing happens within 2 minutes, load save A yourself. Run the sub-blocks one at a time and wait for
"… done" before the next. Full procedures for Claude's reference: [research/diplomacy.md](research/diplomacy.md) §7
and [research/team-hq-research.md](research/team-hq-research.md) §6.

| Step | Run | What you do | What to note |
|---|---|---|---|
| E1 (S11.1) | `/x4mpspike diplo1` | Open Diplomacy → Factions and Relations. **Screenshot.** Wait for "diplo1: part 2", look again, screenshot | Are "X4MP Team 1/2" listed? Which section? Colours? Lock icon and its text? Is Team 3 hidden? |
| E2 (S11.2) | `diplo2` | Nothing | (all in the log) |
| E3 (S11.3) | `diplo3` | Wait 70 s. Open Diplomacy: is Team 2 still "Unreceptive"? Is it in the faction-pair dropdown of interference actions? Wait for "diplo3: part 2" and look again | Screenshots of both looks |
| E4 (S11.4) | `diplo4` | If you have agents: open Agent Actions, look for "X4MP test action", start it. Open Diplomatic Events: is "X4MP test treaty" listed? Try to pick an option | Could you pick an option without an agent? Screenshot |
| E5 (S11.5) | `diplo5` | Wait 70 s | (log) |
| E6 (S11.6) | `diplo6` | Open the Diplomacy menu. Is there a new tab "Teams (test)"? Click it, then click back to a vanilla tab | Did the tab draw? Did switching back work? Screenshot |
| E7 (S11.7) | `diplo7` | Nothing | (log) |
| E8 (S12.1) | `hq1` | Open the research menu at your HQ | Does our log list match what the menu shows as completed? Screenshot of the menu |
| E9 (S12.2) | `hq2` | When asked: go to a **workbench** and check whether a weapon mod MK1 is craftable; note whether data-leak scans drop blueprints if you can test it quickly; start a research and cancel it, note whether resources come back. When asked: save, reload | Your notes for each prompt |
| E10 (S12.3) | `hq3` | When asked: open the research menu, then the map on your HQ (owner shown?) | Any errors or odd plot messages? |
| E11 (S12.6) | `hq6` | Nothing | (log) |
| E12 (S12.7) | `hq7` | Nothing | (log) |

Then load **save B** yourself from Load Game (the probe stays connected; it only auto-loads once per X4 start) and
run:

| Step | Run | What you do | What to note |
|---|---|---|---|
| E13 (S12.3d) | `hq3d` | Use **save B** (no Player HQ). This **spawns two HQs**: one for team 2 and one owned by you (`hq3d noplayer=1` skips yours). `hq4`, `hq5` and `hq8` need them, so run E13 before those. Wait for "hq3d: done" | (log) |
| E14 (S12.4) | `hq4` | Open the research menu when asked. **Do not press Start.** Screenshot. Close it when asked | Did the menu open with a research module? Any errors? |
| E15 (S12.5) | `hq5` | Wait 60 s | (log) |
| E16 (S12.8) | `hq8` | Open the research menu / encyclopedia when asked. Screenshot | Do the two test entries appear, with the right "done" state? |

If you are short on time, do E8, E9, E10, E11 (= S12.1, .2, .3, .6) and E1, E2, E6 first.

---

## Part F. On-foot presence (S10) (sitting 3)

Start the server with `-SaveName <save F>`, start X4 and let the probe load save F's copy (docked at the big
station), or load save F yourself after 2 minutes. Run `onfoot1` first; it
covers the most important steps (S10.1, .3, .4, .11, .12, .14, .7, .8) in that order. Each step announces itself
and says what to do. Full procedures for Claude's reference:
[research/on-foot-presence.md](research/on-foot-presence.md) §6.

| Step | What the notification asks of you | What to note |
|---|---|---|
| F1 (S10.1) | Get up, walk pad → elevator → corridor → trader corner → (bar) → transporter, then sit back in the pilot seat | Clock time you entered each place |
| F2 (S10.3) | Look at the test character "Spike Alice" in front of you; after 5 min walk into another room and come back | Appearance, name/title shown, still there? |
| F3 (S10.4) | Walk an S-curve, then run, then stand and turn. Three modes, 1 min each | Rate each mode 1–5 (smoothness, walk animation, sliding feet) |
| F4 (S10.11) | Stay docked | — |
| F5 (S10.12) | Use the test menu "Go to lounge", then "Leave". Open the station's transporter: is "Multiplayer Lounge" listed? Walk out of the lounge door | Where does the door lead? Any black screen or getting stuck? |
| F6 (S10.14) | When asked: save (lounge present, you outside), save again (you inside). **Later**, after the session: disable `x4mp_spike` in Settings → Extensions, load both saves, then re-enable and load one again | Do both saves load without the extension? Are you stuck anywhere? |
| F7 (S10.7) | Walk up to the test character and talk to it. Try the three choices | Did "Message", "Wave", "Open MP menu" appear and work? Any normal NPC chatter instead? |
| F8 (S10.8) | Stand in the bar/trader corner while 8 test characters walk around for 2 min. When asked: save and reload (twice) | FPS before/during; any test characters left after loading? |

Then, if time remains, `onfoot2` (S10.2, .5, .6, .9, .13, .15): visit a second station, fly > 50 km away and back,
and reload the save when asked; rate the lounge with characters (S10.15) 1–5.

---

## Sending results

1. Quit X4 to the desktop (this flushes all logs).
2. Stop the server: press `Ctrl+C` in the server window.
3. In PowerShell: `powershell -ExecutionPolicy Bypass -File tools\session2\collect-logs.ps1`
   It creates `out\session2\logs-<date-time>.zip` containing:
   - the game log `Documents\Egosoft\X4\<your numeric id>\x4mp_s2.log`;
   - the X4Native log folder `Documents\Egosoft\X4\<your numeric id>\x4native\` (`x4native.log` and the probe's log);
   - the server log `out\session2\data\logs\server-<date>.log` and the FakeNode output `out\session2\fakenode.log`.
   It uploads nothing. Claude reads it locally and removes your Steam id and user paths before anything goes into
   the repository.
4. Tell Claude the zip's name, and paste your notes in this shape (copy, fill in):
   ```
   Sitting: 1 / 2 / 3     Date:          X4 started at (clock):
   B1 Tester appeared in start menu: yes/no (after ~__ s)
   B2 auto-load happened: yes/no; manual entry in Load Game: yes/no; loading took ~__ s
   B3 paused after load: yes/no; map close unpaused: yes/no; unpaused after ~20 s: yes/no
   B5 Events for Tester during load: ...
   B6 reloadui via: chat / run-block; Events: ...
   C1 save entries greyed with tooltip: yes/no; tooltip text: ...
   C2 newest autosave time: ...  new autosave during the test: yes/no
   C3 quicksave created a save: yes/no
   C4 entry for x4mp_s2test_1 visible: yes/no; name shown: ...
   C6 credits restored exactly: yes/no
   C8 gate sector: ...; active on map: yes/no; after reload: yes/no
   D1 start-menu row present: yes/no; what opened: ...
   D2 standalone window over start menu: shows/clickable/closes: ...
   D3 X4Native settings button: present/clicked/notification: ...
   D4 password shown as: dots / plain text
   D5 HUD visible / survives map / pause menu / station menu: ...
   D7 Nexus / Workshop / Steam link: what opened for each
   Anything strange (with clock time): ...
   ```
   Add screenshots for D6 and the Part E steps that ask for them.

## Cleaning up (after the last sitting)

1. `powershell -ExecutionPolicy Bypass -File tools\session2\uninstall.ps1` removes the three folders it installed
   from `<X4 install>\extensions\` (nothing else).
2. The script then **lists** the test and downloaded saves it can find in your save folder (`x4mp_*.xml.gz` and the
   slots you noted). Delete those yourself in Explorer, along with the test saves T, A, B and F. Your backup from A1
   is your safety net.
3. Steam launch options: remove `-logfile x4mp_s2.log` (keep `-debug all` only if you want it).
4. Settings → Game: autosave interval is back to your value (C2 step 5).
5. `out\session2\` can stay (git-ignored) or be deleted.

## If something goes wrong

| Symptom | What to do |
|---|---|
| X4 crashes | Note the clock time, restart X4, continue with the next step. Include the crash time in your notes; the logs are flushed every second |
| `Tester` never appears (B1) | Check that the server window still runs and that Settings → Extensions shows `x4native` and `x4mp_probe` enabled. Continue with Part C anyway (load save T yourself) |
| A block never says "done" | Wait 2 more minutes, then go on with the next step and note it |
| Game stuck paused after B3 | Press `Esc` twice. Note it (that is a result too) |
| You end up stuck somewhere on foot (Part F) | Load the last save. Never use the console to remove ships; our code never removes your ship, and nothing in this session asks you to |
| "Failed to verify the file signature" lines in the log | Normal for unsigned mods; ignore |
