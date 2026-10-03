# In-game test session 3 (the real X4MP mod: join, reload, reconnect, authority, saves)

> **Status (2026-10-03):** kit checked against the final merged code and dry-run **without X4**: `-WhatIf` of every script, the build, the
> publish, and the whole kit against a temp Documents folder with the real `x4mp.dll` in hostsim
> (`mod/tests/hostsim/session3_dry_run.ps1`, see section "Dry run" at the end).

Claude can't play X4, so you run these steps and send back the log zips plus your notes. Everything runs on **one PC** with **one copy** of X4;
the X4MP server and FakeNode (a fake player program) run on the same PC. This session uses the **real product mod `x4mp`** (not the session-2
probe). It checks the M2 exit criteria 1-5, 7-9 and 11-16 from [m2-plan.md](m2-plan.md) section 3, plus **V21** (an authority checkpoint loads in X4
**without** the mod). Criterion 6 (build mismatch) is checked in CI; 10, 17, 18 are CI / lead items.

## START HERE (10 lines)

1. Back up `Documents\Egosoft\X4\<id>\save\` (OneDrive: pause sync while testing). Never use slots 1 and 2; your working copy is slot 4, scratch slot 7. **Delete the session-2 leftovers `x4mp_*.xml.gz`** there (the join test needs the download to be missing).
2. Steam > X4 > Properties > Launch options: `-debug all -logfile x4mp_s3.log` (replaces the session-2 `x4mp_s2.log`). Turn Steam auto-update for X4 off.
3. Open two PowerShell windows in the repo folder (window 1 = server, window 2 = helpers). Always `powershell -ExecutionPolicy Bypass -File <script>`.
4. Window 2: `mod\build.ps1`, then `tools\session3\install.ps1 -RemoveTestExtensions` (answer `yes` twice: it removes the session-2 probe and spike).
5. X4 > Settings > Extensions: **Protected UI Mode: Off**; **x4native** and **X4 Multiplayer** on; the two test extensions gone. Quit X4.
6. Window 1: `tools\session3\start-fake-authority.ps1 -SaveName save_004 -JoinPassword Testpw-314159`; wait for `FakeNode authority: checkpoint stored`.
7. **Run 1** (Part B): start X4, **Multiplayer > Join a server**, steps B1-B6, B9; the 30-minute flight is B5. Quit X4 and run `collect-logs.ps1 -Label run1`.
8. **Run 1b** (B7, refusal), **Run 2** (B8, B11), **Run 3** (Part C, X4 is the authority), **Run 4** (Part D, no mod): same pattern, `collect-logs.ps1` after **every** X4 quit.
9. B10 (`find-password.ps1`) after a normal run, with X4 closed.
10. Send Claude: every `out\session3\logs-*.zip` file name, your notes with clock times, screenshots of the refusal screen (B7). At the end `uninstall.ps1`.

## What the session answers

| Run | Topology | Time | Steps / criteria |
|---|---|---|---|
| Run 1 | 1: real X4 = client, FakeNode = authority serving your save | 70 min (30 min connected) | B1 join from the start menu (1), no leave/join (2), B2 `/reloadui` (3), B3 HUD/menus, B4 reconnect (4), B5 30 min (5), B6 save control client half (12), B9 self-test (13) |
| Run 1b | 1, strict mods | 10 min | B7 mod refusal (7) |
| Run 2 | 1, normal | 25 min | B8 extension list (8), B10 password search (14), B11 remembered fields (15) and `launch.json` (16) |
| Run 3 | 2: real X4 = authority, FakeNode = clients | 40 min (20-min autosave watch overlaps) | C1-C6 (9, 11, 12 authority half), HUD / "Request save now" |
| Run 4 | no X4MP | 10 min | Part D (V21) |

**After every X4 quit run `tools\session3\collect-logs.ps1 -Label <word>` before starting X4 again.** X4 and X4Native overwrite their own logs on every
start. (The X4MP mod log is different: it is **appended** to, with a banner line `x4mp ... hello` per start; the zip always holds all of it.)

## Lessons from session 2 (read once)

- **The Load Game list hides `x4mp_*` file names** (the downloaded session save `x4mp_<12 hex>.xml.gz`, checkpoints `x4mp_ckpt_<16 hex>.xml.gz`). The mod loads
  them by name; you never look for them in the list. For Part D you copy a checkpoint file into a normal slot with Explorer instead.
- The old probe paused the game at "universe ready" and needed **Esc twice**. The real mod does **not** pause. If the game still looks frozen after a load,
  press Esc once or twice and **write it down** (time and what you saw).
- Session 2: closing the standalone window on the start menu left a blank screen (Alt+F4 was the only way out). The real mod now reopens the start menu:
  **a check** (B1, B7, B11). If you get a blank screen, Alt+F4 and tell Claude.
- A save load and `/reloadui` both unload and restart our DLL; the connection survives (criteria 2 and 3).
- Only Steam Workshop URLs open from inside X4; Nexus addresses are shown as text you copy (B7).
- Quicksave bypasses the Save-game block (session 2); the mod now **detects** it and reports it (B6).
- Chat: bind **Toggle Chat Window** (Settings > Controls) to a free key if it has none (not F8, F10-F12). `/x4mp` in the chat opens the Multiplayer window.
- The server runs in **Warn** mode for mods (your own non-DLC mods do not block you); only B7 uses `-Strict`. SirNukes and UIX can stay disabled; nothing here needs them.
- If slot 4 was saved while the session-2 probe was active, X4 may show a "missing extension" warning when the session save loads: click through and write it down.

---

## Part A. Setup

**A1. Back up** `Documents\Egosoft\X4\<id>\save\` (OneDrive on this PC). Pause OneDrive sync while testing.
Then delete every `x4mp_*.xml.gz` in that folder (session-2 leftovers such as `x4mp_<12 hex>.xml.gz` and `x4mp_s2test_1.xml.gz`; `uninstall.ps1 -WhatIf` lists them). The
session-2 download is a copy of the save the server will offer again: if it is still there, B1 does not prove the download (criterion 1). Slots 1 to 7 are never touched.

**A2. Steam launch options** (Library > X4 > Properties): `-debug all -logfile x4mp_s3.log`. The game log is then `Documents\Egosoft\X4\<id>\x4mp_s3.log`;
our Lua lines in it start with `[X4MP]`. Also: Steam automatic updates for X4 **off** (the mod only accepts build 611726). Write down the version line on the start menu.

**A3. Build and install the real mod** (window 2, repo folder):

```
powershell -ExecutionPolicy Bypass -File mod\build.ps1
powershell -ExecutionPolicy Bypass -File tools\session3\install.ps1 -RemoveTestExtensions
```

`install.ps1` checks that `x4mp_probe` and `x4mp_spike` are neither installed nor enabled, offers to remove them (it asks `yes/no` for each), then runs
`mod\tools\deploy.ps1`: it **replaces** `x4native` with the version in this repo (v9.0.0-611726) and installs `x4mp` into `<X4 install>\extensions\`; nothing
else is touched. Try `-WhatIf` first. Then in X4 > Settings > Extensions: **Protected UI Mode: Off**, **x4native** and **X4 Multiplayer** enabled, the two test
extensions gone. (Your saves keep working; settings files of the probe in `Documents\Egosoft\X4\x4mp\` are ignored.)

**A4. Optional settings file.** `powershell -ExecutionPolicy Bypass -File tools\session3\write-config.ps1 -SelfTest` writes
`Documents\Egosoft\X4\x4mp\x4mp.json` with `selftest: true` (the self-test then runs on every universe ready; B9). It never writes a password. `-Show` prints the file.

**A5. Test password**: use a throwaway like `Testpw-314159` (the commands below use it) in the Join dialog (B10 searches for it). Never a real password.

---

## Part B. Topology 1: real X4 = client, FakeNode = authority

**B0. Start the server and the fake authority** (window 1):

```
powershell -ExecutionPolicy Bypass -File tools\session3\start-fake-authority.ps1 -SaveName save_004 -JoinPassword Testpw-314159
```

(`-List` shows your saves; `-WhatIf` prints the plan.) First run: it builds the web GUI and publishes the server (a few minutes, once). It copies slot 4 to
`out\session3\authority-save\`, starts the server, signs in as admin for you and starts the fake authority. It prints the GUI address `http://127.0.0.1:47790` and
**where** the admin password is (`out\session3\admin-password.txt`; the password is never printed). Wait for
`FakeNode authority: checkpoint stored (sha ...)`. Leave the window open. Open the GUI, log in as `admin`; keep **Players** and **Logs** at hand. If the script warns
"GUI not built", Ctrl+C and run it again with `-Rebuild`. Note the clock time you start X4. Ctrl+C in window 1 stops server and fake authority.

The fake authority reports **your DLCs**: the script reads every enabled `ego_dlc_*` extension of your X4 install (and your Documents `content.xml` enabled flags), writes them to
`out\session3\authority-extensions.json` (versions as the game shows them, e.g. `9.00`) and prints `Authority extensions: your DLCs: ...`. Without that the server refuses a
player who owns DLCs (a DLC difference refuses even in Warn mode). The server compares DLC versions normalised (`900` equals `9.00`). If the line says `none` or the script
warns that no DLC was found, pass `-X4Dir "<folder with X4.exe>"`. `-AuthorityExtensions vanilla|modded|FILE` replaces the list (B7 uses `modded`).

**B1. Join from the start menu (criteria 1, 2).** The point: the mod downloads the save itself (slot 4 is only the source of the copy the fake authority serves).

1. Start X4 and stay on the **start menu**. The main list has a row **Multiplayer** (hover: "Join or manage an X4MP multiplayer session"). If it is missing:
   note it, see "If something does not work".
2. Click **Multiplayer**: a window titled **Multiplayer** opens (it shows "Last server: ..." after the first try). Click **Join a server**. Fill in **Server address**
   `127.0.0.1:47780`, **Player name** `Tester`, **Password** your test password (dots). Leave **Host this session as the authority: No**. Click **Connect**.
3. The status screen ("Connection status") walks through: Connecting, Signing in, Checking the session save, Downloading the session save: NN%, Loading the session
   save, Matching the universe, **Connected**. Then the normal X4 loading screen shows and the game loads. Write down how long the download and the loading screen took.

Look for: all of the above in order, no red error line. Test the window itself once: click **Back**, then close the window with its X (or Esc): **the start menu must come back**.
- GUI **Players** page: row `Tester`, Status "Online NN ms", Role (client); click it: **Connection** panel, **Phase** `InGame`.
- GUI **Logs** (type `Tester` in **Search**): `player N (Tester) joined as ...`, then `detached: "ClientReload"` and `resumed (baseline epoch ...)`, and **no** `left:` line and no second
  `joined as` for Tester (criterion 2).
- Mod log `Documents\Egosoft\X4\x4mp\logs\x4mp.log`: at the top of each start a banner (`x4mp ... hello`, `game_version=... build_suffix=...`, `build check: Supported`; copy the
  `build check` line into your notes). Then, in this order: `ServerHello:`, `Welcome: player_id=... resumed=false`, `SessionSaveInfo:`,
  `session save complete and verified as x4mp_...`, `raised the Lua event loadSave`, `extension shutdown: session unloaded for a resume ... join_ms=` (a number, under 200),
  `resuming the session after an extension reload (stage loading, ...)`, `Welcome: ... resumed=true`, `universe ready: NodeReady sent`.
- The downloaded file is `x4mp_<12 hex>.xml.gz` in the save folder (not in the Load list).

**B2. `/reloadui` (criterion 3).** In game open the chat (your Toggle Chat Window key), type `/reloadui`, Enter. Look for: the HUD line (top right, "X4MP: Connected, ...") and the
Multiplayer entry are back within about 2 seconds; **Players** still shows Tester online; **Logs** show another `detached: "ClientReload"` + `resumed`, no `left:`. Mod log:
`reload: the universe was ready before the reload ...` (the host opens the universe-ready gate itself, X4Native never repeats `on_universe_ready` after
`/reloadui`), then `universe ready (epoch 1, ...)` and `reload resume: same universe (ui reload; ...)`. If it says `new universe` instead, tell Claude (the fingerprint rule is then
wrong). The self-test (`/x4mp_selftest`) must show `player.guard` as PASS or WARN, no longer SKIP.

**B3. HUD and menus.** Open the map, close it, press Esc, close it. The HUD line may vanish while a menu is open: it must **come back by itself** within about a second after
the menu closes. The Esc menu should have a **Multiplayer** row too. Say which of these did not happen. In `x4mp_s3.log` find `[X4MP] ui: optionsmenu adapter OK(source=...)` or
`DEGRADED(...)` and copy it into your notes (the `append` source has never run against the real game). If the HUD never shows, tell Claude (a fallback shows the status as a normal notification).

**B4. Reconnect (criterion 4).** In game: window 1 Ctrl+C (stops server **and** fake authority). The HUD (and the **Connection status** line) goes to **Connecting** (the mod uses that
word for reconnecting); X4 must keep running. After about 20 seconds start window 1 again with the **same command** as B0. Look for: the HUD shows **Connected** again within about
5 seconds **after** window 1 prints `checkpoint stored`; X4 never froze. Write down what the status line says meanwhile (it may show the save download again: fine).
Expected after the server comes back (the resume token is gone, so the mod joins fresh): `x4mp.log` shows `rejoin: fresh Welcome while the universe is running`, then
`rejoin: the session save is the running universe` and `universe ready: NodeReady sent`; **no** `raised the Lua event loadSave` and no loading screen; the GUI **Players** page shows Tester
**InGame** again. The vanilla cockpit HUD (radar, steering overlay) stays the whole time. If the session save is a different one, the HUD says **The session save changed: ...** and nothing loads
until you open `/x4mp` and press **Load the new session save**. `x4mp_s3.log` has `[X4MP] hud: view Helper6=X4MPHud[hud=1,pc=1] frames=1` lines: if the cockpit HUD disappears anyway,
send that log (each line lists everything X4 holds in its view stack at that moment).

**B5. 30 minutes connected (criterion 5).** Fly around a quiet sector for 30 minutes (do B6 meanwhile). Look for: no disconnect (**Logs**: no new `detached` for Tester after B4);
**Players** shows Status "Online NN ms" and FPS; the Tester detail page **Connection** panel shows Ping, FPS, **Frame time (p95)**, **Mod main-thread cost (p95)**, **Traffic in / out**,
**Game time** (updates every 2 s). `x4mp.log` has `[perf] stats: fps=... frame_p95=...ms mod_p95=...ms ...` every 5 seconds; `mod_p95` should stay below **0.2 ms**.

**B6. Save control, client half (criterion 12).** While connected:
1. Esc menu: the **Save Game** row is greyed; hover: "Saving is disabled while connected as a client".
2. Press quicksave. Known: it **still writes a file**. Expected now: `x4mp.log` shows `a game save was written while connected as a client ...`; GUI **Logs** shows
   `node:Tester [saves] a game save was written ...`; the text "Saving is disabled while connected as a client. This save is a local copy only." appears as a note in the
   X4MP window if it is open (`/x4mp`), **not** as an on-screen popup.
3. After 20 minutes (the B5 flight): no `autosave_*.xml.gz` in the save folder. Write down file names and times.

**B7. Mod refusal (criterion 7).** Quit X4 (`collect-logs.ps1 -Label run1`). Window 1: Ctrl+C, then:

```
powershell -ExecutionPolicy Bypass -File tools\session3\start-fake-authority.ps1 -SaveName save_004 -Strict -AuthorityExtensions modded
```

(No join password this time, so leave the password field empty. The fake authority now claims: Split Vendetta and Cradle of Humanity, UIX, SirNukes Mod Support APIs, the Workshop mod "Warehouse Fleets"
and the Nexus-installed mod "Better Traders" (the script gives it a Nexus address). Your own set differs, so you are refused.) Start X4, **Multiplayer > Join a server**, address
and name, **Connect**. Look for the **Connection status** screen with the headline **"Your mods do not match this session."** and groups headed **Install: N**, **Enable: N**, **Disable: N**,
**Update: N** (whichever apply; a DLC you own that the authority lacks is listed under Disable), each entry with name and version, and:
- **Warehouse Fleets** (Workshop) has an **Open Workshop page** button: click it; your browser should open a steamcommunity.com page (the item is fake, an empty page is fine);
- **Better Traders** (Nexus) shows the line "Cannot be opened from the game. Copy this address into a web browser:" and a read-only **Address** box with
  `https://www.nexusmods.com/x4foundations/mods/1234`: click it, Ctrl+C, paste into the browser address bar: does copying work? (say whether clicking selects the text);
- hint lines: "Install these first, then restart the game." and "Changes need a game restart. Enable and disable mods in Settings, Extensions."
- GUI **Mods** page: **Recent rejections** and the **Players** panel (button **Reports** for Tester) show the same lists.
Then close the window (the start menu must come back). Quit X4 (`collect-logs.ps1 -Label run1b`).

**B8. Real extension list (criterion 8).** Window 1: Ctrl+C, start it again with the B0 command (normal run). Start X4, join as in B1. In the GUI open **Players > Tester**, section **Mods** (or the
**Mods** page, Players panel > **Reports**): "All reported mods (N)" lists id, version, source and enabled; compare with X4 > Settings > Extensions: same ids, versions and enabled flags
(DLCs `ego_dlc_*`, Workshop `ws_<id>`). The header says "Latest report ... N extensions (M enabled)". Quit X4, start it, join again: the numbers are the same.

**B9. Self-test (criterion 13).** With `selftest: true` (A4) it runs on universe ready; otherwise type `/x4mp_selftest` in the chat. Look for in `x4mp.log`: `SELFTEST begin`, one line per check
(`x4native.api`, `game.adapter`, `x4native.hooks`, `build.supported`, `saves.wrappers`, `saves.block`, `player.guard`, `game.time`, `main_thread`) each `PASS` (`WARN` for `build.supported` is
expected if the build string cannot be read), then `SELFTEST summary: N PASS, 0 FAIL, W WARN, S SKIP` and `SELFTEST forwarded N lines to the server`. The result also appears as a note
("X4MP self-test: N passed, ...") in the X4MP window if open. GUI **Players > Tester > Diagnostics > Self-test** shows the same table (within about 2 seconds).

**B10. Password never persisted (criterion 14).** Quit X4 completely. Window 2 (asks for the password hidden, or pass `-Password`):

```
powershell -ExecutionPolicy Bypass -File tools\session3\find-password.ps1
```

It searches `x4mp.json`, `launch.json`, `x4mp.log`, `uidata.xml`, `config.xml`, `content.xml`, every top-level `*.log` of the X4 user folder (including `x4mp_s3.log`), `x4native\`, the installed
`x4mp` and `x4native` folders and `out\session3\` (server logs and database) for your test password (UTF-8, UTF-16, URL-quoted) and prints **HIT / NO HIT per file, never the password**.
Expected: `RESULT: no hit`. Anything else: send the whole output.

**B11. Remembered fields (15) and `launch.json` (16).** Start X4 (server up with the B0 command): **Multiplayer** shows "Last server: ..." and **Join a server** has **Server address** and
**Player name** pre-filled; **Password** is empty. Quit X4 (`collect-logs.ps1 -Label run2`). Then, with the server still up (window 2):

```
powershell -ExecutionPolicy Bypass -File tools\session3\write-launch.ps1 -Name Tester -Password Testpw-314159
```

(`-Server host:port` if not local; it is valid for 10 minutes; `-Minutes N` changes that). Start X4: it connects **without any UI** and the file is deleted at once (check:
`Documents\Egosoft\X4\x4mp\launch.json` is gone; `x4mp.log`: `launch.json consumed: ...` and `joining 127.0.0.1:47780`; **Players** shows Tester). Quit X4, then
`write-launch.ps1 -Expired` and start X4 again: **no** connection, the file is deleted, log line `launch.json expired`. Quit X4 (`collect-logs.ps1 -Label run2b`). Run B10 again if you like.

### If something does not work in Part B
- No **Multiplayer** row on the start menu: in game `/x4mp` opens the window; look for the `[X4MP] ui: optionsmenu adapter ...` line in `x4mp_s3.log`.
- Window closed but a blank screen: Alt+F4, send the `x4mp_s3.log` line `[X4MP] standalone: closed, reopening start menu (...)` (or its absence) to Claude.
- No cockpit HUD in flight (only our X4MP line): send `x4mp_s3.log`; the HUD frame must keep the vanilla HUD (`keepHUDVisible`). `/x4mp` opens the Multiplayer window for details.
- Refused for "build": `x4mp.log` has `REFUSING TO START: ...`; send that line.
- Refused for mods in a **normal** run: disable non-DLC mods and retry, or tell Claude (a DLC difference always refuses).
- Nothing happens after Connect: look at the Connection status screen text and at `x4mp.log` (`joining ...`, `server Disconnect code=...`).

---

## Part C. Topology 2: real X4 = authority, FakeNode = clients (Run 3)

Quit X4 from Part B (`collect-logs.ps1 -Label run2b` if not done) and Ctrl+C window 1.

**C1. Start the server without a fake authority** (window 1):

```
powershell -ExecutionPolicy Bypass -File tools\session3\start-fake-clients.ps1 -SaveName save_004
```

It starts the server, uploads slot 4 through the REST API and creates and starts the session "Session 3" from it (the same calls as the GUI **Sessions & Saves** page; `upload-save.ps1` does only that),
prints the **in-game admin password** (the fixed throwaway value `x4mp-host-test`, local only) and waits (up to 30 min) for the session to become Running. GUI **Sessions & Saves**: the save is listed under **Saves**,
the current session waits for an authority.

**C2. Host from the start menu (criterion 9).** Start X4. **Multiplayer > Join a server**: address `127.0.0.1:47780`, name `Host`, click the button **Host this session as the authority: No** until it reads **Yes**,
enter **Admin password** `x4mp-host-test`, **Connect** (leave **Password** empty). Look for: Downloading the session save, Loading the session save, Matching the universe, **Connected**, and
**Role: authority** on the status screen. Window 1 prints `session state: ...` ending in `Running`, then starts 3 FakeNode clients (they stay 10 minutes) and prints `[Bot...] joined with the save after ...s` for
each (they downloaded and SHA-256-verified the checkpoint). Mod log: `Welcome: ... roles=...`, `authority: SaveGame requested as x4mp_ckpt_...`, `authority: checkpoint x4mp_ckpt_... stored`,
`authority: self-spawn sent net_id=... game_time=...`. GUI: **Sessions & Saves** shows the session **RUNNING** with Authority `Host`, and the **Saves** table a new checkpoint row (Name `x4mp_ckpt_<16 hex>`, Source `authority`) marked **in use**
(a badge "ghosts not cleaned" must **not** be there); your uploaded `save_004` row has Source `admin-upload`; **Players**: `Host` plus three `Bot...` players.

**C3. "Request save now" and checkpoint files.** GUI **Sessions & Saves** > **Request save now** > confirm **Request save**: a **second** checkpoint row appears (about a minute for a big save).
In the X4 save folder: `x4mp_ckpt_<16 hex>.xml.gz` files (two at most; the mod removes only its own older ones, never your saves). Note the file names.

**C4. `game_time` (criterion 11).** GUI **Logs**, Search `EntitySpawn`: `world: EntitySpawn of 1 entities, first net_id N, game_time T`. T must be non-zero and within 1 second of the
`game_time` in the mod log lines `authority: self-spawn sent ... game_time=T2` and `authority: SaveStarted sent (game_time T2, ...)`. Write down T and T2.

**C5. Authority save control (criterion 12).** 20 minutes: no `autosave_*.xml.gz` change in the save folder; only `x4mp_ckpt_...` saves appear, and only at session start and on "Request save now".

**C6. HUD / self-test.** The HUD line shows "X4MP: Connected, N players, NN ms". `/x4mp_selftest` works here too (`saves.block` expects **no** block for an authority).

Quit X4 (`collect-logs.ps1 -Label run3`) and Ctrl+C window 1.

---

## Part D. V21: an authority checkpoint loads **without** the mod (Run 4)

1. In the save folder find the newest `x4mp_ckpt_<16 hex>.xml.gz` (Explorer, sort by date). **Copy** it (do not move) to `save_007.xml.gz`, replacing the scratch slot's file.
2. Remove the mod: `powershell -ExecutionPolicy Bypass -File tools\session3\uninstall.ps1` (removes `x4mp` and `x4native` from the install, lists saves, deletes none). Check in X4 > Settings > Extensions
   that neither shows up.
3. Start X4, **Load Game**, slot 7. Expected: it loads like a normal save (X4 may warn that extensions the save used are missing: a note, not an error), you are in your ship, nothing about
   X4MP anywhere. Note anything odd (missing objects, errors in `x4mp_s3.log`). Quit X4 and `collect-logs.ps1 -Label run4` (`x4mp\` config files only; no mod is running).
4. Optional: reinstall with `install.ps1` for further work.

---

## Sending results

1. Zips: `out\session3\logs-<label>-<time>.zip` hold the X4 log (`x4mp_s3.log`), `x4native\`, `Documents\Egosoft\X4\x4mp\` (`x4mp.json`, `logs\x4mp.log`, `authority-saves.json`; never `launch.json`),
   the server logs and the FakeNode output. They never contain admin passwords, the database or saves, and upload nothing: tell Claude the file names.
2. Your notes: clock times, which step failed or surprised you, screenshots of the refusal screen (B7) and the Players page.
3. Afterwards: `uninstall.ps1`; remove `-logfile x4mp_s3.log` from the Steam launch options; delete `out\session3\`, `Documents\Egosoft\X4\x4mp\` and the `x4mp_*.xml.gz` test saves yourself.

## Quick reference: where things are

| What | Where |
|---|---|
| Admin GUI | `http://127.0.0.1:47790` (user `admin`; password file `out\session3\admin-password.txt`); pages: Dashboard, Players, Map, **Sessions & Saves**, **Mods**, Teams & Factions, Economy, Chat, **Logs**, Settings, Diagnostics |
| Mod log (appended) | `Documents\Egosoft\X4\x4mp\logs\x4mp.log` |
| Mod settings / one-shot launch file | `Documents\Egosoft\X4\x4mp\x4mp.json` (`write-config.ps1`) / `launch.json` (`write-launch.ps1`) |
| Game log with our Lua lines `[X4MP] ...` | `Documents\Egosoft\X4\<id>\x4mp_s3.log` |
| X4Native logs | `Documents\Egosoft\X4\<id>\x4native\` |
| Server / FakeNode output | `out\session3\` (`server.*.log`, `fakenode.log`, `fakenode-clients.log`, `data\logs\`) |
| Downloaded session save / checkpoints | save folder: `x4mp_<12 hex>.xml.gz` / `x4mp_ckpt_<16 hex>.xml.gz` |
| Ports | TCP 47780, UDP 47781 (this PC only), HTTP 47790 |

## Dry run (what was checked without X4)

`mod/build.ps1`, `tools/e2e.ps1 -Steps Publish`, `-WhatIf` of every script, and `powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\session3_dry_run.ps1` (temp Documents and fake X4 folder,
ports 47974-47976): install refuse/remove/deploy, `write-config`, `start-fake-authority` with a join password and the real DLL joining + reloading + self-test + a server restart with the same command,
`find-password` NO HIT / planted HIT, `write-launch` consumed / expired, `collect-logs` contents, `start-fake-clients` with the real DLL as authority (upload, 3 FakeNode clients, "Request save now"), `uninstall`.
**Only the game can tell:** the real UI labels and layout, `/reloadui` behaviour, the start-menu restore, Esc-menu row, the options-menu adapter source, whether a quicksave is detected, real save/load timing,
the HUD coming back after menus, whether X4 warns about missing extensions in slot 4, Nexus text selection, Workshop link opening.
