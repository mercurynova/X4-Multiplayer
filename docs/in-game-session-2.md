# In-game test session 2 (native probe, saves, menus, on-foot, diplomacy, HQ)

> **Status (2026-10-02):** the kit for sitting 1 (Parts A–D) is built and was dry-run **without X4** (probe DLL in a fake
> host against the real server and a FakeNode authority, all kit scripts, 65 s; see
> [../mod/tests/hostsim/probe_session2_dry_run.ps1](../mod/tests/hostsim/probe_session2_dry_run.ps1)). Parts E and F
> (sittings 2 and 3) wait for their spike blocks (M2-003, M2-004). Claude tells you when everything is merged.

Claude can't play X4, so you run these steps and send back zips of logs plus your notes. Everything runs on
**one PC** with **one copy** of X4. The X4MP server and a fake "authority" (FakeNode) run on the same PC.

## What this session answers

| Part | Sitting | Time | Questions (ids from [decisions.md](decisions.md) Part 3 and [roadmap.md](roadmap.md) §4) | Blocks |
|---|---|---|---|---|
| A | 1 | 15 min | Setup | — |
| B | 1 | 40 min | Native DLL in the start menu; download + load of a session save by name; pause at "universe ready"; what a save load and `/reloadui` do to our DLL (V05, S5); which thread runs each callback (V07) | **M2** |
| C | 1 | 40 min | Save control (V06, S6); custom save names; game clock for `EntitySpawn.game_time`; money units on the native path (V04 follow-up); MD object variables (V12 retest); opening a locked gate (S9) | **M2** |
| D | 1 | 25 min | Menu entry (V20/R1), standalone menu over the start menu, X4Native settings toggle, password box (V22), HUD (V23), extension list (R7/V28), web links (R8/V29); optional D8 = R3–R6 with SirNukes / UIX enabled | **M2** |
| E | 2 | 60–75 min | Team diplomacy S11 (ADR-047) and per-team HQ/research S12 (ADR-048) | M5 and later |
| F | 3 | 60 min | On-foot presence S10 (ADR-046) | M3b/M3c |

**Sitting 1 (parts A–D, about 2.5 hours) is the one M2 waits for.** Sittings 2 and 3 can happen any day after.
Each sitting starts with Part A steps A6–A9 (start the server and X4) and ends with "Sending results".

### Sitting 1 at a glance: X4 is started three times

| X4 start | Probe setting | What you do | Then |
|---|---|---|---|
| **Run 1** (B-run) | `-NoHooks` (hooks off) | A5–A9, then B1–B6. Hooks off is what makes B4 ("was our DLL unloaded on a save load?") readable | quit X4, `collect-logs.ps1 -Label b` |
| **Run 2** (main run) | normal (hooks on) | B1–B3 again (quick, nothing new to note), then Part C, then Part D (D1–D7) | quit X4, `collect-logs.ps1 -Label main` |
| **Run 3** (optional, D8) | normal, `-AutoLoad $false` | SirNukes / UIX enabled, one X4 start per combination | quit X4, `collect-logs.ps1 -Label d8-<combination>`, **disable them again** |

Third-party mods: **SirNukes Mod Support APIs and kuertee UI Extensions are installed but disabled on your PC. Runs 1 and 2
(and sittings 2 and 3) need them disabled** so the baseline is vanilla. Only D8 (R3, R4, R5, R6) needs them enabled; D8 comes
last, so it cannot disturb the baseline. How to switch them is in D8.

X4 may overwrite its log file each time it starts, so **run `collect-logs.ps1` every time you quit X4, before starting it
again** (see "Sending results").

---

## Part A. Before you start

### One-time preparation (about 10 minutes)

**A1. Back up your saves folder.** It is `Documents\Egosoft\X4\<your numeric id>\save\` (on this PC it is under
OneDrive). Copy the whole folder somewhere safe. Pause OneDrive sync for the duration of each sitting.

**A2. Prepare the test saves (before A5, so they are made without the test extensions).**
X4 has **10 numbered save slots**; slot N is the file `save_00N.xml.gz` (slot 1 = `save_001.xml.gz`, slot 10 =
`save_010.xml.gz`) in the save folder. Other files: `quicksave.xml.gz`, `autosave_01.xml.gz` to `autosave_03.xml.gz`.
`powershell -ExecutionPolicy Bypass -File tools\session2\start-server.ps1 -List` prints every save file with its
size and date, in the form the scripts want (name without `.xml.gz`).

| Save | Needed for | What it should be |
|---|---|---|
| **T** | Parts B, C, D | A normal mid-game save. You are **in space, in your own ship**, not docked, somewhere quiet |
| **A** | Part E (S12) | A save **with a Player HQ and research unlocked**, ideally one research running. If it also has an embassy and an agent, S11.4 can run |
| **B** | Part E (S12) | An **early** save without a Player HQ |
| **F** | Part F (S10) | **Docked** at a large NPC station that has a trader corner, ideally a bar; you own at least one S or M ship (an L/XL ship is a bonus) |

**Status (2026-10-02):** you prepared saves T and A as **one save in slot 1** (mid-game, with a Player HQ; it serves both
Parts B–D and Part E) and save B in **slot 2**. These two are the masters: **never test on slots 1 and 2.** Copy them first:
1. In X4 (extensions not installed yet) load slot 1 and save it into a **spare slot**. Call its number `X`: file `save_00X`.
   That is the **working copy of T/A**; its name is what you give `-SaveName` in sittings 1 and 2.
2. Load slot 2 and save it into another spare slot `Y`: file `save_00Y` = the working copy of **B**.
3. Choose a third spare slot `Z` as the **scratch slot**: every "save to a new slot" in Part C means "save to slot `Z`,
   overwriting it each time".
Spare slots are rows that say "Empty Slot", or old saves you do not need (your A1 backup is the safety net). Write `X`, `Y`,
`Z` down. If a name is unclear, run `start-server.ps1 -List`. Save F is still open: no vanilla station with a bar has turned up
yet. Part F (S10) is sitting 3 and does not block M2. Prefer a vanilla bar; a room-generating mod is acceptable as a last
resort, but then S10 results must be marked "modded rooms" because the mod may change the room layout we are probing.

Diplomacy and HQ blocks (Part E) change the test save; most revert themselves, but `diplo1`, `diplo4`, `hq3d` and `hq6` leave permanent changes. Only ever run them on a copy. Do not pause the game during `diplo3`/`diplo5` (they wait 70 s of real time for a vanilla check that pausing stops).

Throw all the saves away after the session (they will contain test objects).

**A3. Steam launch options** (Library → X4 → Properties → Launch options):
`-debug all -logfile x4mp_s2.log`
The game log is then written to `Documents\Egosoft\X4\<your numeric id>\x4mp_s2.log`.

**A4. In X4** (start the game once if needed): Settings → **Extensions** → **Protected UI Mode: Off** (the button on that
page; restart X4 if it asks). Make sure every third-party mod is **disabled** (that includes SirNukes Mod Support APIs and
kuertee UI Extensions, which stay off until D8). When you later load a save, X4 may show a warning that third-party
extensions mark the game as modified: confirm it. Steam: make sure **automatic updates for X4 are off** (the mod only
accepts build 611726). On the start menu, under the title, X4 shows the **Version** and a build line: **write them down**
(the probe also logs them: see "Sending results").

**A5. Install the test extensions.** This needs the built probe DLL. Claude builds it on the main PC; on another PC run
`powershell -ExecutionPolicy Bypass -File mod\build.ps1 -Spikes` first (the install script says "x4mp_probe.dll not
found" otherwise). Then, in the repo folder, in PowerShell:
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
powershell -ExecutionPolicy Bypass -File tools\session2\start-server.ps1 -SaveName <working copy of T, e.g. save_005>
```
Leave the window open (the server and FakeNode stop when you close it or press Ctrl+C). It prints:
- the **admin GUI address**: `http://127.0.0.1:47790`;
- where the **admin password** is (`out\session2\data\initial-admin-password.txt`; log in as `admin`). If the GUI asks you to
  change it on first login, pick a new one and write it down;
- `FakeNode authority: checkpoint stored (sha …)`. This means the server now offers a copy of that save as the session
  save. (A first run publishes the server first: about 30 s.)

The save is **copied** to `out\session2\authority-save\`; the original is never touched. For sitting 2 use the working copy
of A (the same save), for sitting 3 save F. If you see an error ("Save not found" lists the newest saves), stop and send
`out\session2\` (Claude will look). Ports: TCP 47780 and UDP 47781 (this PC only), HTTP 47790.

**A7. Write the probe's config.** In a second PowerShell window. For **Run 1** (hooks off, needed for B4):
```
powershell -ExecutionPolicy Bypass -File tools\session2\write-probe-config.ps1 -NoHooks
```
Answer: server `127.0.0.1:47780`, name `Tester`. (For Run 2, 3 and the other sittings you run it again, see below.) It
writes `Documents\Egosoft\X4\x4mp\x4mp_probe.json` (no password; the mod reads its settings from this file, never from
environment variables) and prints every key. **`-NoHooks` is read only when X4 starts**: if X4 is already running, quit it
and start it again.
- `write-probe-config.ps1` with **no switch** = hooks **on** (normal). Run it at the start of Run 2.
- `-Pin` (only B6b) sets `pin_module`; `-AutoLoad $false` (only D8) stops the probe from loading the downloaded save itself.

**A8. Open the admin GUI** in a browser at `http://127.0.0.1:47790` and log in. Keep the **Players** page and the **Logs**
page at hand (Logs shows the server log live; type `Tester` into its search box to filter).

**A9. Note the time** (clock time) when you start X4. Write times next to anything odd you see; it makes the logs
much easier to read.

### Block commands (used from Part C on)

Every test is a *block*. Start one in either way; both do the same:
- **in game, chat:** `/x4mpspike <block>` (the chat window only exists in game, not in the start menu). **Controls:**
  Settings → Controls → the action **Toggle Chat Window**. The game data gives it **no default key**: if it is unbound, bind
  it to a free key (not F8 or F10–F12, which are debug keys). Open it, type the command, press Enter.
- **PowerShell (works everywhere, also in the start menu):** `powershell -ExecutionPolicy Bypass -File
  tools\session2\run-block.ps1 <block>`. The probe notices the change in 1–2 s.

Progress shows as on-screen notifications of the form **`X4MP spike: <block>: ...`**. The text in quotes below is what
follows the `X4MP spike: ` prefix. `/x4mpspike list` shows all block names. Exception: **`money` must be started
with `run-block.ps1`** (only the probe does the native part).

---

## Part B. Native probe: join, load, reload (Run 1, hooks off)

The probe connects to the server by itself, downloads the session save and loads it. You mostly watch. Run 1 uses the
`-NoHooks` config from A7.

**B1. DLL in the start menu.**
1. Start X4. Stay in the **start menu** (do not load anything).
2. Wait 30 seconds.
3. Look at the admin GUI → Players: a player **`Tester`** should appear (connected, waiting for the save or
   downloading). The Logs page shows `player N (Tester) joined as "Client"`.

Look for: `Tester` appears within 30 s. Write down if it does not.

**B2. Download and load by name.**
1. Still in the start menu, wait. The probe downloads the save (a few seconds for a normal save) and then **loads
   it by itself**. You see the normal loading screen.
2. If nothing has happened after 2 minutes: open **Load Game**. The downloaded copy is a file named `x4mp_<12 hex digits>`.
   **X4 only lists such file names when the list is sorted by "Name" or "Date"**: click the **Date** column header at the
   top of the list (the default slot view hides it). Look for an entry that looks like save T (the same sector/description
   as your working copy, so it appears twice). Note whether such an entry exists at all, then load it yourself.

Look for: the game loads save T's content. Note how long the loading screen took.

**B3. Paused at "universe ready".**
1. When loading finishes, the game should be **paused** for about 20 seconds, then unpause by itself.
2. During the pause: open the **map** (default key `M`), wait 3 s, close it. Note whether the game unpaused when the map
   closed.

Look for: paused right after loading (yes/no); unpaused by closing the map (yes/no); unpaused after ~20 s (yes/no).

**B4. What the save load did to our DLL (automatic, only readable in this run).** Nothing to do, but this run must have
hooks off: the probe log must show `cfg loaded ... hooks=0` and `init hooks disabled by config`. (With hooks on, the probe
pins its DLL in memory, so "was it unloaded?" cannot be answered; that is why Run 2 is separate.) The probe logs
the shutdown/re-init order, whether its memory ("stash") survived (`stash ... survived_previous_shutdown=`), how long its
network thread took to stop (`shutdown unload_for_reload done ... join_ms=`) and whether the DLL image was unloaded
(`init ... dll_image_inits=1` again after the load = unloaded; `=2` = stayed loaded).

**B5. Invisible to the server.**
1. In the admin GUI → **Logs**, filter on `Tester` and look at the lines since you started X4.

Look for: the good outcome is `player N (Tester) detached: "ClientReload"` followed by `player N (Tester) resumed (baseline
epoch E)`. The bad outcome is `player N (Tester) left: ...` followed by `... joined as ...`. Write down what you see.

**B6. `/reloadui` while in game.**
1. Open the chat window (see "Block commands": **Toggle Chat Window**), type `/reloadui`, press Enter.
2. If the chat window does not open, tell PowerShell instead:
   `powershell -ExecutionPolicy Bypass -File tools\session2\run-block.ps1 reloadui`
   (the probe then triggers the UI reload itself within ~5 s).
3. Check GUI → Logs again.

Look for: the UI flickers/reloads; `Tester` stays connected; no `left`/`joined` pair.

**B6b (only if Claude asks after reading the logs).** Repeat B1–B6 with the module-pin option on:
`write-probe-config.ps1 -NoHooks -Pin` (or `run-block.ps1 pin_on` while X4 runs), then quit X4 to the desktop and start
again from B1. **Switch back afterwards:** run `write-probe-config.ps1` (no switches) before Run 2; a pinned DLL cannot
be unpinned in a running X4.

**B7. Threads (automatic).** Logged throughout (`cb` and `threads` lines). Nothing to do.

**End of Run 1:** quit X4 to the desktop, then
`powershell -ExecutionPolicy Bypass -File tools\session2\collect-logs.ps1 -Label b`.
Leave the server window running.

---

## Run 2 (main run): hooks on. Parts C and D

Back in PowerShell: `powershell -ExecutionPolicy Bypass -File tools\session2\write-probe-config.ps1 -Server 127.0.0.1:47780 -Name Tester`
(no `-NoHooks`: hooks on, needed for C2 and C5). Start X4 (note the time), stay in the start menu, and let it
connect, download and load again (B1, B2, B3: nothing new to report unless it behaves differently from Run 1). When the
save is loaded you are in space in save T: Part C.

---

## Part C. Saves, clock, money, retests (Run 2, in game with save T loaded)

Run each block with the chat command or `run-block.ps1` (see "Block commands").
**After every save load the Lua blocking flag resets**: run `saves1_block` again before any step that says "blocking ON".

**C1. Menu save goes through our wrapper.** Blocks `ui`, `saves1`, `saves1_block`.
1. Run `ui`. Wait for "ui: done (source …)". (It captures the options-menu configuration that the Save-row greying needs;
   it also adds a harmless test row to the main menu.)
2. Run `saves1`. Wait for "saves1: wrapper installed (logging only)".
3. Open the menu (Esc) → **Save Game** → save into the **scratch slot Z**. Note the slot.
4. Run `saves1_block`. Wait for "saves1: blocking ON".
5. Open the menu (Esc) again.

Look for: in step 5 the **Save Game** row is **greyed out** and its tooltip says "Saving is disabled while connected as a
client (X4MP test)". Note the exact tooltip text if it differs. Close the menu. (If the row is not greyed, the log line
`SAVE INFO what=menu_hook ... save_row=` says why: `hooked` = the row was reachable, `no_config` = it was not.)

**C2. Autosave.** Block `saves2` (also tells the probe to skip the native autosave call for a moment).
1. Settings → **Game Settings**: **Autosave** must be **On**. Note the **Autosave Interval** dropdown value, then set it to
   the **shortest** one (the first entry; each entry shows a range in minutes, for example "N - M min"). Close Settings.
2. In PowerShell: `run-block.ps1 skip_autosave_on`. (Without it, step 3 would make a real autosave.)
3. Run `saves2`. It turns on our autosave blocker and asks the game for an autosave in two ways (through the game's own
   autosave trigger and through the MD request). Wait for "saves2: done".
4. In PowerShell: `run-block.ps1 skip_autosave_off`. (From here on the probe no longer swallows autosaves, so the test
   below tests only our blocker.)
5. Fly through a **gate, highway or anomaly** into another sector (vanilla autosaves on a sector change once the minimum
   time of the interval has passed). Wait for the interval's **maximum** minutes (this also covers the timer-based
   autosave), changing sector once more halfway.
6. Open **Load Game**: the autosave entries (labelled "Autosave") are listed at the top, above the slots.
7. Put the autosave interval back to what it was (step 1).

Look for: **no new autosave** since you started the block. Note the newest autosave time you see. (The log line
`SAVE INFO what=autosave_suppressed_by_md_diff` is the blocker working.)

**C3. Quicksave.**
1. Blocking must still be ON (C2 turned it on). Press the **Quicksave** key (**F5** by default; Settings → Controls →
   "Quicksave").
2. Open **Load Game**.

Look for: a new entry labelled "Quicksave" at the top (yes/no). Either answer is useful.

**C4. Custom save name and timing.** Block `saves4`.
1. Run `saves4`. The block turns blocking off, saves as `x4mp_s2test_1` and measures how long it takes.
2. Wait for "saves4: done".
3. Open **Load Game** and click the **Date** column header (a file name that is not `save_NNN` is hidden in the default
   slot view). Is there an entry from just now? Note how it is named in the list (we expect "X4MP S2 test 1").

**C5. Game clock.** Block `clock` (runs 4 minutes, logs once a second).
1. Run `clock`. Notification: "clock: started, runs 240 s. Fly 30 s, pause 20 s, SETA 20 s".
2. For 30 s fly normally.
3. Press `Esc` (pause menu) and stay there 20 s, then close it.
4. Turn on **SETA** for 20 s (**Toggle SETA Mode**, default **Shift+4**; check Settings → Controls), then off. This needs
   a SETA installed on your ship; if you have none, skip this step and write "no SETA".
5. When "clock: save now (new slot), then load it and wait until 'clock: done'" appears: save to the **scratch slot Z**,
   load that save, and wait until "clock: done". (After the load you see "clock: resumed after load at N s".)

**C6. Money on the native path.** Block `money`, **started with PowerShell** (the probe does the native part).
1. Note your credits (top of the screen or the empire menu).
2. In PowerShell: `run-block.ps1 money`. The probe adds 100 native units, then removes them, and the Lua side logs
   what it sees (whether 100 native units are 1 credit or 100 credits is what we learn). Notification at the end:
   "money: logged before and after (the probe does the +-100)".
3. Check that your credits are back to exactly the noted value.

**C7. Retest V12, object variables.** Block `v12`.
1. Run `v12`. Wait for "v12: set".
2. Save to the **scratch slot Z**, load that save, wait 20 s ("v12: verify done, see the log.").

Look for: `V12 PASS/FAIL what=verify_...` lines appear in the log after the load (the result is in the log).

**C8. Retest S9, opening a locked gate.** Block `s9gate`. **Only in the throw-away save T.**
1. Run `s9gate`. The block activates one gate that is still closed in your save and tells you which sector it is in.
2. Open the map and look at that gate. Note whether it shows as active.
3. Save to the **scratch slot Z**, load it, wait 20 s, look at the gate again.

---

## Part D. Menus and UI (Run 2, sitting 1)

**D1–D3. Menu entry and standalone menu.**
1. Quit to the **start menu** (keep X4 running).
2. In PowerShell: `run-block.ps1 ui`. Wait 10 s.
3. Look at the start menu list. Is there a row **"Multiplayer (X4MP test)"** (it is placed right after "Play Timelines")?
   Click it if present and note what opens. Go back.
4. `run-block.ps1 ui_standalone`. A small test window should open **over** the start menu. Note whether it shows,
   whether you can click its button, and whether it closes with its close button.
5. Settings → **Extensions** → find the **X4Native** page/entry → look for an **"X4MP probe: test button"** switch
   (a toggle). Flip it. A notification "probe button clicked" should appear. Note where you found it (or that you could
   not).

**D4. Password box.**
1. In the standalone test window (step 4), there is a field "Test password". Type `Banana-Test-42`.
2. Note whether the text shows as dots/stars or plain text. Click "Check". The log records only the **length**.

**D5. HUD.**
1. Load the working copy of save T again. When in space: `/x4mpspike hud` (or `run-block.ps1 hud`). A small box
   "X4MP test HUD" should appear at the top right.
2. Open and close the map, the pause menu and a station menu (if near one).

Look for: the box stays visible in the cockpit, does not steal the mouse, and comes back after each menu.

**D6. Extension list.**
1. `/x4mpspike extensions` (or `run-block.ps1 extensions`). Nothing to see; it logs the extension list.
2. Open Settings → Extensions and take one **screenshot** of the list.

**D7. Web links.**
1. `/x4mpspike links` (or `run-block.ps1 links`). A test window with three buttons opens: "Nexus page", "Workshop page",
   "Steam link".
2. Click each one. Note for each: did a browser or the Steam overlay open, and which page?

**End of Run 2:** quit X4, then `powershell -ExecutionPolicy Bypass -File tools\session2\collect-logs.ps1 -Label main`.

**D8 (optional, Run 3: only these steps need SirNukes Mod Support APIs and/or kuertee UI Extensions).**
Do this **last**, after the Run 2 zip exists. What each library tests:

| Step | Needs enabled | What to note |
|---|---|---|
| R3 | SirNukes only | after `ui`: is the row "Multiplayer (X4MP test)" still there once? (log `UI PASS what=config_capture source=...`, `row_state ... rows_with_id=1`) |
| R4 | SirNukes only | Settings → Extensions: the X4Native page and the "X4MP probe: test button" are still there |
| R5 | SirNukes only | in game: `/x4mpspike ping` still works in chat ("X4MP spike: ping ok") **and** a SirNukes `/` command you know still works |
| R6 | UIX only | after `ui`: log `UI ... what=uix_getConfig ok=true` and `config_capture source=uix` |

**Enable / disable (each change needs an X4 restart):** quit X4 to the desktop, start it, Settings → **Extensions**, switch the
extension (**SirNukes Mod Support APIs**, **UI Extensions and HUD**) to enabled (X4 asks to restart; confirm), quit and start
X4 again. For each combination you want (SirNukes only, UIX only, both; each combination is one X4 start):
1. `write-probe-config.ps1 -AutoLoad $false -Server 127.0.0.1:47780 -Name Tester` (the probe still connects and downloads
   but does not load by itself), start X4, stay in the start menu.
2. D1–D3 (`run-block.ps1 ui`, look at the row, `ui_standalone`, X4Native toggle).
3. Load the working copy of T yourself, then R5 (chat `/x4mpspike ping`, a SirNukes command), then B6 (`/reloadui`; after
   it `run-block.ps1 ui` again and check the row is still there exactly once).
4. Quit X4, `collect-logs.ps1 -Label d8-<combination>` (for example `d8-sirnukes`).
When finished, **disable both again** (Settings → Extensions, restart X4) and run `write-probe-config.ps1` once more
(back to the normal settings). Record which combination each result belongs to.

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

Every time you quit X4 (end of Run 1, Run 2, each D8 start, and each later sitting):

1. Quit X4 to the desktop (this flushes all logs).
2. In PowerShell: `powershell -ExecutionPolicy Bypass -File tools\session2\collect-logs.ps1 -Label <word>` (`b`, `main`,
   `d8-sirnukes`, `s2`, `s3`...). The server may keep running meanwhile. It creates
   `out\session2\logs-<word>-<date-time>.zip` containing:
   - the game log `Documents\Egosoft\X4\<your numeric id>\x4mp_s2.log`;
   - the X4Native log folder `Documents\Egosoft\X4\<your numeric id>\x4native\` (`x4native.log` and the probe's log
     `x4mp_probe.log`);
   - the server log `out\session2\data\logs\server-<date>.log` and the FakeNode output `out\session2\fakenode.log`.
   It uploads nothing and never includes the admin password. Claude reads it locally and removes your Steam id and user
   paths before anything goes into the repository.
3. At the very end stop the server: press `Ctrl+C` in the server window.
4. Tell Claude the zip names, and paste your notes in this shape (copy, fill in):
   ```
   Sitting: 1 / 2 / 3     Date:          X4 started at (clock):   Run: 1 / 2 / D8 (which libraries)
   Start-menu version + build line (what X4 shows): ...
   Save files used (working copy of T/A = save_00X, B = save_00Y, scratch slot Z): ...
   B1 Tester appeared in start menu: yes/no (after ~__ s)
   B2 auto-load happened: yes/no; manual entry in Load Game (Date sort): yes/no; loading took ~__ s
   B3 paused after load: yes/no; map close unpaused: yes/no; unpaused after ~20 s: yes/no
   B5 Logs lines for Tester during load: ...
   B6 reloadui via: chat / run-block; Logs: ...
   C1 Save Game row greyed with tooltip: yes/no; tooltip text: ...
   C2 newest autosave time: ...  new autosave during the test: yes/no
   C3 quicksave created a save: yes/no
   C4 entry for x4mp_s2test_1 visible: yes/no; name shown: ...
   C5 SETA used: yes/no
   C6 credits restored exactly: yes/no
   C8 gate sector: ...; active on map: yes/no; after reload: yes/no
   D1 start-menu row present: yes/no; what opened: ...
   D2 standalone window over start menu: shows/clickable/closes: ...
   D3 X4Native settings toggle: found where / flipped / notification: ...
   D4 password shown as: dots / plain text
   D5 HUD visible / survives map / pause menu / station menu: ...
   D7 Nexus / Workshop / Steam link: what opened for each
   D8 combination: R3 / R4 / R5 / R6 results: ...
   Anything strange (with clock time): ...
   ```
   Add screenshots for D6 and the Part E steps that ask for them.

Where Claude finds the build question's answer: in `x4mp_probe.log`, the line
`[X4MP-PROBE] ... build where=init GetGameVersion=<major.minor> GetBuildVersionSuffix='<raw text>' suffix_len=<n>`
(logged at init and again at the first frame). Your note of what X4 itself shows on the start menu is the cross-check.

## Cleaning up (after the last sitting)

1. `powershell -ExecutionPolicy Bypass -File tools\session2\uninstall.ps1` removes the three folders it installed
   from `<X4 install>\extensions\` (nothing else).
2. The script then **lists** the test and downloaded saves it can find in your save folder (`x4mp_*.xml.gz` and the
   most recent others). Delete those yourself in Explorer, along with the working copies of T, A, B, F and the scratch
   slot (never the masters in slots 1 and 2 unless you want to). Your backup from A1 is your safety net.
3. Delete the probe's own folder `Documents\Egosoft\X4\x4mp\` (config and player key).
4. Steam launch options: remove `-logfile x4mp_s2.log` (keep `-debug all` only if you want it).
5. Settings → Game Settings: autosave interval is back to your value (C2 step 7). SirNukes / UIX are disabled again (D8).
6. `out\session2\` can stay (git-ignored) or be deleted.

## If something goes wrong

| Symptom | What to do |
|---|---|
| X4 crashes | Note the clock time, restart X4, continue with the next step. Include the crash time in your notes; the logs are flushed every second |
| `Tester` never appears (B1) | Check that the server window still runs and that Settings → Extensions shows `x4native` and `x4mp_probe` enabled. Continue with Part C anyway (load the working copy of T yourself) |
| A block never says "done" | Wait 2 more minutes, then go on with the next step and note it |
| Unknown block / nothing happens after `run-block.ps1` | Check the name against this script; `/x4mpspike list` shows the registered ones. Notification "X4MP spike: unknown block ..." means a typo |
| Game stuck paused after B3 | Press `Esc` twice. Note it (that is a result too) |
| You end up stuck somewhere on foot (Part F) | Load the last save. Never use the console to remove ships; our code never removes your ship, and nothing in this session asks you to |
| "Failed to verify the file signature" lines in the log | Normal for unsigned mods; ignore |
