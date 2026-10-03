# In-game test session 3 (the real X4MP mod: join, reload, reconnect, authority, saves)

> **Status (2026-10-03):** kit written by task M2-14 and dry-run **without X4** (`-WhatIf` on every script, and the hostsim
> end-to-end scenarios the same flows come from: `tools/e2e.ps1`). The launch / stats parts (B5, B11) match the as-built M2-12 features.

Claude can't play X4, so you run these steps and send back one zip of logs plus your notes. Everything runs on **one PC** with
**one copy** of X4; the X4MP server and FakeNode (a fake player program) run on the same PC. This session uses the **real product mod
`x4mp`** (not the session-2 probe). It checks the M2 exit criteria 1-9 and 11-16 from [m2-plan.md](m2-plan.md) section 3, plus
one extra check, **V21** (an authority checkpoint loads in X4 **without** the mod).

## What this session answers

| Part | Topology | Time | Criteria |
|---|---|---|---|
| A | setup | 15 min | install the real mod, check nothing else interferes |
| B | **1: real X4 = client**, FakeNode = authority serving your save | 60-70 min (the 30-minute connection runs inside it) | 1 join from the start menu, 2 no leave/join across the load, 3 `/reloadui`, 4 reconnect, 5 30-minute connection, 7 mod refusal, 8 real extension list, 12 (client half) save control, 13 self-test, 14 password never on disk, 15 remembered fields, 16 `launch.json` |
| C | **2: real X4 = authority**, FakeNode clients | 40 min (the 20-minute autosave watch can overlap) | 9 authority from an uploaded save, 11 `game_time`, 12 (authority half), plus HUD / "Save now" |
| D | V21 | 10 min | a checkpoint loads without the mod |

Criterion 6 (build mismatch) is checked in CI with a forged build string; the in-game override exists only in a debug build, so it is **not** part
of this session. Criteria 10, 17 and 18 are CI / lead items.

### X4 is started several times

| X4 start | Server side | You do |
|---|---|---|
| **Run 1** | topology 1, normal | B1-B6, B8-B11 (join, reloadui, 30 minutes, self-test, password, save control) |
| **Run 1b** | topology 1, `-Strict -AuthorityExtensions modded` | B7 (mod refusal) |
| **Run 2** | topology 1, normal again | B12 (remembered fields after an X4 restart), B13 (`launch.json`) |
| **Run 3** | topology 2 | Part C |
| **Run 4** | no X4MP | Part D (V21) |

**Run `tools\session3\collect-logs.ps1 -Label <word>` every time you quit X4, before starting it again** (X4 and X4Native overwrite
their logs on every start).

## Lessons from session 2 (read once)

- **The Load Game list does not show `x4mp_*` file names** (the downloaded session save, authority checkpoints). The mod loads them by
  name; you never need to find them in the list. For Part D you copy a checkpoint file into a normal slot instead.
- **After every save load you may need to press Esc twice** to get the game running (menu pause and the mod's pause interfere). The
  real mod does not pause at universe ready; if the game still looks frozen, press Esc once or twice and write it down.
- **The probe and spike extensions must be disabled or uninstalled while the real `x4mp` runs.** `install.ps1` refuses otherwise and
  offers to remove them.
- Your saves: the **working copy** is slot 4 (`save_004`), the **scratch** slot is 7 (`save_007`). Never test with slots 1 and 2 (masters).
  The kit only ever **reads** your saves; it copies, never overwrites, except for the scratch copy you make yourself in Part D.
- The chat window only exists in game. Bind **Toggle Chat Window** (Settings > Controls) to a free key if it has none (not F8, F10-F12).
- A save load and `/reloadui` both restart our DLL (the connection survives, that is what criteria 2 and 3 are about).
- Only Steam Workshop URLs open from inside X4; Nexus links are shown as text (criterion 7).
- Third-party mods: by default the kit runs the server in **Warn** mode, so your own enabled mods do not block you. Nothing in this session
  needs SirNukes or UIX; both can stay as they are, but if the join is refused for a reason you do not understand, disable all
  non-DLC mods and try again, and tell Claude.

---

## Part A. Before you start

**A1. Back up your saves folder** (`Documents\Egosoft\X4\<id>\save\`, under OneDrive on this PC). Pause OneDrive sync while testing.

**A2. Steam launch options** (Library > X4 > Properties): `-debug all -logfile x4mp_s3.log`. The game log is then
`Documents\Egosoft\X4\<id>\x4mp_s3.log`; our Lua lines in it start with `[X4MP]`.

**A3. In X4:** Settings > Extensions > **Protected UI Mode: Off**. Turn Steam automatic updates for X4 **off** (the mod only accepts build
611726). Write down the Version / build line on the start menu.

**A4. Build and install the real mod.** In the repo folder, in PowerShell:

```
powershell -ExecutionPolicy Bypass -File mod\build.ps1
powershell -ExecutionPolicy Bypass -File tools\session3\install.ps1 -RemoveTestExtensions
```

`install.ps1` checks that `x4mp_probe` and `x4mp_spike` are neither installed nor enabled, offers to remove them (it asks first), then runs the
normal `mod\tools\deploy.ps1`: it copies `x4mp` and `x4native` into `<X4 install>\extensions\` and nothing else. Try `-WhatIf` first to
see the plan. Then in X4 > Settings > Extensions make sure **x4native** and **X4 Multiplayer** are enabled and the two test extensions are gone.

**A5. Optional settings file.** `powershell -ExecutionPolicy Bypass -File tools\session3\write-config.ps1 -SelfTest` writes
`Documents\Egosoft\X4\x4mp\x4mp.json` with `selftest: true` (the self-test then runs on every universe ready; criterion 13). It never writes a
password. `-Show` prints the file.

**A6. Pick the test password** you will type in the Join dialog, a throwaway such as `Testpw-314159` (criterion 14). Never use a real password.

**A7. Open two PowerShell windows** in the repo folder. Window 1 runs the server; window 2 runs the helper scripts.

---

## Part B. Topology 1: real X4 = client, FakeNode = authority

**B0. Start the server and the fake authority** (window 1):

```
powershell -ExecutionPolicy Bypass -File tools\session3\start-fake-authority.ps1 -SaveName save_004 -JoinPassword Testpw-314159
```

(`-List` shows your saves; `-WhatIf` prints the plan.) It copies slot 4 to `out\session3\authority-save\`, publishes the server on the first
run (about 30 s), starts it, signs in as admin for you and starts the fake authority. It prints the GUI address `http://127.0.0.1:47790`
and **where** the admin password is (`out\session3\admin-password.txt`; the password itself is never printed). Wait for
`FakeNode authority: checkpoint stored (sha ...)`. Leave the window open. Open the GUI, log in as `admin`, keep the **Players** and **Logs** pages
at hand. Note the clock time you start X4.

**B1. Join from the start menu (criteria 1 and 2).** Slot 4's contents must **not** be what you rely on: the point is that the mod downloads the
save by itself.

1. Start X4 and stay on the **start menu**. The entry **Multiplayer** is in the main list (hover: "Join or manage an X4MP multiplayer session").
   If it is missing, type nothing yet; look at the end of Part B for the fallback (`/x4mp` only works in game).
2. Click **Multiplayer**. A window opens ("Join a server"). Fill in **Server address** `127.0.0.1:47780`, **Player name** `Tester`, **Password**
   your test password (it shows as dots). Leave **Host this session as the authority** on **No**. Click **Connect**.
3. The Connection status line walks through: Connecting, Signing in, Checking the session save, Downloading the session save: ...,
   Loading the session save, Matching the universe, **Connected**. Then the normal X4 loading screen shows and the game loads.
   (If you press Esc twice at the end and the game was not frozen, still write it down.)

Look for: all of the above in order, no error line; **Players** page: `Tester` connected, phase `InGame`. In **Logs** (type `Tester` in the search
box) the sequence `joined as`, then `detached: "ClientReload"` and `resumed (baseline epoch ...)`, and **no** `left` and no second `joined` for
`Tester` (criterion 2). In `Documents\Egosoft\X4\x4mp\logs\x4mp.log` (the mod log):
`ServerHello`, `Welcome: ... resumed=false`, `SessionSaveInfo`, `session save complete and verified as ...`, `raised the Lua event loadSave`,
`extension shutdown: session unloaded for a resume ... join_ms=` (a number, under 200), `Welcome: ... resumed=true`,
`reload resume: new universe (save load; ...)`, `universe ready: NodeReady sent`. Write down how long the download and the loading screen took.

**B2. `/reloadui` (criterion 3).** In game open the chat (your Toggle Chat Window key), type `/reloadui`, Enter.
Look for: the HUD line (top right, "X4MP: Connected, ...") and the Multiplayer entry are back within about 2 seconds; the Players page still shows
`Tester` connected; Logs show another `detached: "ClientReload"` + `resumed`, no `left`. In `x4mp.log`: `reload resume: same universe (ui reload; ...)`
(if instead it says `new universe`, tell Claude; that is the fingerprint rule being wrong).

**B3. HUD and menus (M2-11 checks).** Open the map, close it, press Esc, close it. The HUD line may disappear while a menu is open: it must
**come back by itself** within about a second after the menu closes. Open the Esc menu: there should be a **Multiplayer** row there too.
Say which of these did not happen. In `x4mp_s3.log` look for the line `[X4MP] ui: optionsmenu adapter OK(source=...)` or `DEGRADED(...)` and copy it
into your notes (the `append` source has never run against the real game).
If the HUD never shows, switch to notifications: tell Claude (`__X4MP_USER.hudMode = "notify"` shows "X4MP: <status>" as a normal notification).

**B4. Reconnect (criterion 4).** While in game: in window 1 press Ctrl+C (stops the server **and** the fake authority). The HUD (and the Connection status line) should go to
**Connecting** (the mod calls the reconnect phase "Connecting"). X4 must keep running. Wait about 20 seconds, then start window 1 again with the **same command** as in B0. Look for: the HUD shows
Connected again within about 5 seconds **after the fake authority has reported "checkpoint stored"**; X4 never froze. Note what the status line says
meanwhile (it may show the save download again after a restart: that is fine, write it down).

**B5. 30 minutes connected (criterion 5).** Fly around normally for 30 minutes (a quiet sector; you can do the save-control checks below meanwhile).
Look for: no disconnect (Logs: no `detached` for `Tester` after B4); Players page shows ping and FPS for `Tester`, and its detail page the frame time, mod main-thread cost, traffic in/out and game time (updated every 2 s);
`x4mp.log` has a `[perf] stats: fps=... frame_p95=...ms mod_p95=...ms ...` line every 5 seconds; `mod_p95` (the mod's own main-thread cost per frame, 95th percentile) should be below **0.2 ms**.

**B6. Save control, client half (criterion 12).** While connected:
1. Press Esc: the **Save Game** row is greyed; hover it: "Saving is disabled while connected as a client".
2. Press your quicksave key. Known from session 2: a quicksave **still writes a file**. The expected new behaviour is that the mod **detects** it:
   `x4mp.log` shows `a game save was written while connected as a client ...` and the server Logs page shows a line starting `[saves]`.
3. Wait 20 minutes (the B5 flight): **no** `autosave_*.xml.gz` file appears in the save folder and no autosave notification shows.
Write down the file names and times you see.

**B7. Mod refusal (criterion 7).** Quit X4 (run `collect-logs.ps1 -Label run1`). In window 1 press Ctrl+C and start it again as:

```
powershell -ExecutionPolicy Bypass -File tools\session3\start-fake-authority.ps1 -SaveName save_004 -Strict -AuthorityExtensions modded
```

(The fake authority now claims it runs UIX, SirNukes Mod Support APIs, a Workshop mod and a Nexus-only mod; you have none of them.) Start X4,
Multiplayer > Join, fill in address and name, **Connect**. Look for: the **Status** screen shows the headline **"Your mods do not match this session."**
with groups **Install:**, **Enable:**, **Disable:**, **Update:** (whichever apply to you), each entry with its name and version, and:
- a **Workshop** entry has an **Open Workshop page** button: click it, your browser should open a steamcommunity.com page;
- a **Nexus** entry shows a **read-only text row with the address**: click it, press Ctrl+C, paste into a text box or the browser address bar:
  does the copy work? (Session 2: Nexus links do not open from X4, so copying is the only way; say whether clicking selects the text.)
- the hint lines: "Install these first, then restart the game." and "Changes need a game restart. Enable and disable mods in Settings, Extensions."
Then the Multiplayer screen while **connected** (Run 2 / any normal run) lists "Mods of this session". The server **Mods** page > player reports shows the
refusal with the same lists. Quit X4 (`collect-logs.ps1 -Label run1b`).

**B8. Real extension list (criterion 8).** In a normal run (Ctrl+C window 1, restart it with the B0 command): when `Tester` is connected, open the GUI
**Mods** page (or the Players page > `Tester` > Mods) and compare the extension list with X4 > Settings > Extensions: same ids, versions and
enabled flags (DLCs `ego_dlc_*`, Workshop `ws_<id>`). Note the **hash** shown, quit X4, start it, join again: the hash should be the same.

**B9. Self-test (criterion 13).** With `selftest: true` (A5) the table appears on universe ready; otherwise type `/x4mp_selftest` in the chat.
Look for in `x4mp.log`: `SELFTEST begin`, one line per check (`x4native.api`, `game.adapter`, `x4native.hooks`, `build.supported`, `saves.wrappers`, `saves.block`,
`player.guard`, `game.time`, `main_thread`) each `PASS` (a `WARN` for `build.supported` is expected if the build string cannot be read), then
`SELFTEST summary: N PASS, 0 FAIL` and `SELFTEST forwarded N lines to the server`; a notification "X4MP self-test: N passed, 0 failed ...". The GUI
**Players** page > `Tester` shows the same table (the last self-test, within about 2 seconds).

**B10. Password never persisted (criterion 14).** Quit X4 completely first. Then run (window 2; it asks for the password hidden, or pass `-Password`):

```
powershell -ExecutionPolicy Bypass -File tools\session3\find-password.ps1
```

It searches `x4mp.json`, `launch.json`, `x4mp.log`, `uidata.xml`, `config.xml`, every X4 log in the user folder (including `x4mp_s3.log`), the whole `x4native\`
folder, the installed extension folders and `out\session3\` (server logs and database) for your test password (UTF-8, UTF-16, URL-quoted) and prints
**HIT / NO HIT per file, never the password**. Expected: `RESULT: no hit`. Anything else: send the output.

**B11. Remembered fields (criterion 15) and `launch.json` (criterion 16).** Start X4 again (server up, B0 command): open Multiplayer > Join: **Server address** and
**Player name** are pre-filled with the last values; the **Password** is empty. For `launch.json`: create `Documents\Egosoft\X4\x4mp\launch.json` (next to `x4mp.json`) containing
`{"server":"<host>:47780","name":"Tester","password":"<session password, optional>","expires_utc":"<now + 10 min, UTC, like 2026-10-03T12:00:00Z>"}`
(optional: `"role":"authority"` with `"admin_password"`). With the
server running, start X4: it connects
**without any UI**, and the file is deleted immediately. Then a file whose `expires_utc` is in the past is ignored and also deleted (log line `launch.json expired`).

### If something does not work in Part B
- No **Multiplayer** row on the start menu: note it; in game `/x4mp` opens the window. Look for the `[X4MP] ui: optionsmenu adapter ...` line.
- Closing the Multiplayer window on the start menu must bring the start menu back (session 2: it did not for the probe). If it does not, write it down;
  Alt+F4 is the escape.
- Refused for "build": the status says which build the mod needs; send the line `build` from `x4mp.log`.
- Refused for mods in a **normal** run: disable non-DLC mods, or tell Claude (the default run uses Warn mode; a DLC difference always refuses).

---

## Part C. Topology 2: real X4 = authority, FakeNode = clients

Quit X4 from Part B (`collect-logs.ps1 -Label topo1`), and Ctrl+C window 1.

**C1. Start the server without a fake authority** (window 1):

```
powershell -ExecutionPolicy Bypass -File tools\session3\start-fake-clients.ps1 -SaveName save_004
```

It starts the server, uploads slot 4 through the REST API and creates and starts a session from it (the same calls as the GUI **Sessions** page; use
`upload-save.ps1` on its own to do only that), prints the **in-game admin password** (a fixed throwaway test value, `x4mp-host-test`, loopback only)
and waits for the session to become Running. (If you prefer the GUI: Sessions > Saves library > Upload, then create a session from it.)
Check in the GUI Sessions page: the save is listed, the session waits for an authority.

**C2. Host from the start menu (criterion 9).** Start X4 (start menu). Multiplayer > Join: address `127.0.0.1:47780`, name `Host`, click the button **Host this session as the authority: No** until it reads **Yes**, enter the **Admin password** shown by the script, **Connect**. Look for: the status goes through Downloading the session save,
Loading the session save, Matching, **Connected** with Role: authority. Window 1 prints `session state: ...` ending in `Running`; then it starts 3 FakeNode clients
that download and verify the checkpoint. In the GUI **Sessions** page: a checkpoint `x4mp_<session>_<n>` (save + manifest) with `ghosts_cleaned`, session **Running**;
Players page: `Host` plus three `Bot..` players, all InGame. In `x4mp.log`: `Welcome: ... roles=...`, `authority:` lines, `universe ready: NodeReady sent`.
Window 1 shows, for each of the 3 clients, "joined with the save after ..." (it downloaded and SHA-256-verified the checkpoint) and then a summary with `errors=0`.

**C3. "Save now" and checkpoint files.** In the GUI Sessions page press **Save now**: a **second** checkpoint appears (about a minute for a big save).
In the X4 save folder there are now `x4mp_ckpt_<16 hex>.xml.gz` files (two at most; older ones the mod made are removed, your own saves never).

**C4. `game_time` (criterion 11).** GUI **Logs**, search `EntitySpawn`: a line `world: EntitySpawn of 1 entities, first net_id N, game_time T`. T should match the in-game
time at the moment of the session start (within about a second of the `SessionState` time; note T and the in-game clock when the line appeared).

**C5. Authority save control (criterion 12).** Authority: no vanilla autosave in 20 minutes (no `autosave_*.xml.gz` change in the save folder), only checkpoint
saves (`x4mp_ckpt_...`) appear, and only when the server asks (start and "Save now").

**C6. Authority HUD / self-test.** The HUD line shows "X4MP: Connected, ... players". `/x4mp_selftest` works here too (`saves.block` expects **no** block for an authority).

Quit X4 (`collect-logs.ps1 -Label topo2`) and Ctrl+C window 1.

---

## Part D. V21: an authority checkpoint loads **without** the mod

1. In the save folder find the newest `x4mp_ckpt_<16 hex>.xml.gz` (Explorer, sort by date). **Copy** it (do not move) to `save_007.xml.gz`, replacing the
   scratch slot's file (it is your scratch slot).
2. Remove the mod: `powershell -ExecutionPolicy Bypass -File tools\session3\uninstall.ps1` (removes `x4mp` and `x4native` from the install, lists saves,
   deletes none). Check in X4 > Settings > Extensions that neither shows up.
3. Start X4, **Load Game**, slot 7. Expected: it loads like a normal save (X4 may warn that extensions the save used are missing: a note, not an error),
   you are in your ship, nothing about X4MP anywhere, no error messages. Note anything odd (missing objects, errors in `x4mp_s3.log`).
4. Optional: reinstall with `install.ps1` for further work.

---

## Sending results

1. After each X4 quit: `powershell -ExecutionPolicy Bypass -File tools\session3\collect-logs.ps1 -Label <run1|run1b|topo1|topo2|...>`. The zip
   `out\session3\logs-<label>-<time>.zip` holds the X4 log, `x4native\`, `Documents\Egosoft\X4\x4mp\` (settings, `logs\x4mp.log`), the server logs
   and the FakeNode output. It never includes admin passwords, the database or saves, and uploads nothing: tell Claude the file names.
2. Your notes: the clock times, which step failed or surprised you, screenshots of the refusal screen (B7) and the Players page.
3. Afterwards `uninstall.ps1` (removes the mod), remove `-logfile x4mp_s3.log` from the Steam launch options, delete `out\session3\`
   and the `x4mp_*.xml.gz` test saves yourself.

## Quick reference: where things are

| What | Where |
|---|---|
| Admin GUI | `http://127.0.0.1:47790` (user `admin`; password file `out\session3\admin-password.txt`) |
| Mod log | `Documents\Egosoft\X4\x4mp\logs\x4mp.log` (appended; a banner per DLL start) |
| Mod settings | `Documents\Egosoft\X4\x4mp\x4mp.json` (`write-config.ps1`) |
| Game log with our Lua lines `[X4MP] ...` | `Documents\Egosoft\X4\<id>\x4mp_s3.log` |
| X4Native logs | `Documents\Egosoft\X4\<id>\x4native\` |
| Server / FakeNode output | `out\session3\` (`server.*.log`, `fakenode.log`, `fakenode-clients.log`, `data\logs\`) |
| Ports | TCP 47780, UDP 47781 (this PC only), HTTP 47790 |
