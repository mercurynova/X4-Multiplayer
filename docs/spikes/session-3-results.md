# In-game session 3: results (2026-10-03)

Script: [in-game-session-3.md](../in-game-session-3.md). Real `x4mp` mod, X4 9.00 build 611726, one PC. Enabled extensions: all 7 DLCs,
x4native, x4mp, kuertee UIX (`ws_3477279743`, "UI Extensions and HUD") and SirNukes Mod Support APIs (`ws_2042901274`), both left on from session-2 D8
(so every result below is **with UIX and SirNukes enabled**). Log zips (local, git-ignored): `out/session3/logs-run1-attempt1-*`, `logs-run1-part1-*`,
`logs-run1-*`, `logs-run1b-*`, `logs-run2-*`, `logs-run2b-*`, `logs-run3-*`, `logs-run4-*`.

Verdict: **all game criteria pass** after the fixes below; see [m2-exit-report.md](../m2-exit-report.md). Open items at the end.

## Fixes made during the session (all merged on main)

| # | Found | Cause | Fix (commit) | Live retest |
|---|---|---|---|---|
| 1 | Closing the Multiplayer window on the start menu left a blank screen | vanilla `Helper.registerMenu` ignores `OpenMenu` while the menu's `shown` flag is still set; we opened our window without closing OptionsMenu through Helper | leave OptionsMenu with `Helper.closeMenuAndOpenNewMenu`, reopen on close (38f3f4b) | PASS (X, Esc, Back) |
| 2 | Every join refused: "disable: Kingdom End, ... Timelines" | the fake authority reported no DLCs; a DLC difference always refuses | `start-fake-authority.ps1` reads the enabled `ego_dlc_*` from the install (38f3f4b) | PASS |
| 3 | Resume after the save load refused: "update: <all DLCs>" | resume ClientHello is sent before Lua re-reports the extension list; the native scan said `900`, Lua `9.00` | version normalisation (mod + server), Lua list kept in the stash (38f3f4b) | PASS |
| 4 | Vanilla cockpit HUD gone in flight, only our line visible | our HUD frame lacked `keepHUDVisible`/`keepCrosshairVisible` | set both (38f3f4b) | PASS |
| 5 | Chat window closed itself after ~1.5 s; next key press said "Chat window disabled" | our HUD frame and the chat window both on layer 3 (`"Helper3"` registration displaced) | HUD on layer 6 (ee0be44) | PASS |
| 6 | After a server restart the in-game fresh rejoin never sent NodeReady (HUD said Connected falsely) | `SaveReady` dropped while stage was InGame | `Stage::Rejoining`: same save -> NodeReady without a load; changed save -> player chooses (27c188d) | PASS (HUD stayed, Phase InGame) |
| 7 | Our HUD line stayed gone after chat/map closed | stale `Helper6` entry in `View.menus` made `H.present()` true | release the entry on close, redraw after menu changes, 5 s refresh, no chat yield (27c188d) | not confirmed (see open item 1) |
| 8 | Bots refused in Run 3 (ExtensionsMismatch, no DLCs) | `start-fake-clients.ps1` had the same gap as #2 | bots report the local DLCs (307a22f) | worked around live with `--extensions` |
| 9 | Our HUD line gone for the rest of Run 3 (authority) after the checkpoint save screen; one `hud:` log line all run | tick loop stopped: Helper's one-time-callback batch runs without pcall, a raising callback drops ours and `timerPending` stayed true; or a stale blocker | watchdog + restart on every status/show/gfx_ok, draw past a stale blocker after 10 s, log the cause (44fe099) | not yet |

## Results

| Step | Criterion | Result | Evidence |
|---|---|---|---|
| B1 join from the start menu | 1, 2 | PASS | Run 2 retest with slot 7 (file never on disk): 34.8 MB downloaded and SHA-verified in 0.18 s on loopback (no visible progress), loadSave, resume `Welcome resumed=true`, `NodeReady sent` |
| B1 start menu returns on close | - | PASS after fix 1 | `standalone: closed, reopening start menu` |
| B2 `/reloadui` | 3 | PASS | detached ClientReload + resumed, no `left:`; HUD line back. Open item 2 |
| B3 HUD and menus | - | PASS | `optionsmenu adapter OK(source=uix)`; Esc menu Multiplayer row |
| B4 reconnect after server restart | 4 | PASS after fix 6 | token refused (code 19) -> fresh Welcome -> `rejoin: the session save is the running universe ... without a load` -> NodeReady; vanilla HUD stayed; GUI Phase InGame |
| B5 30 min connected | 5 | PASS | no detach 13:40-14:10; mod_p95 max 0.089 ms; fps 70-100 in focus (~10 when X4 is in the background) |
| B6 save control, client | 12 | PASS | Save row greyed with our tooltip; quicksave 13:40:45 detected (mod log + GUI Logs `node:Tester [saves]`); no autosave in 30 min |
| B7 mod refusal | 7 | PASS | grouped Install (Nexus address box selects on click, copy works; Workshop button opens steamcommunity.com) / Disable; GUI Recent rejections matches; start menu returns. Open item 4 |
| B8 real extension list | 8 | PASS | GUI Players > Tester > Mods matches Settings > Extensions |
| B9 self-test, client | 13 | PASS | 7 PASS, 0 FAIL, 1 WARN (build number unreadable, known), 1 SKIP (open item 2); 10 lines forwarded |
| B10 password search | 14 | PASS | `RESULT: no hit in 83 file(s)` |
| B11 remembered fields | 15 | PASS | address and name pre-filled, password empty. Open item 5 |
| B11 launch.json | 16 | PASS | `launch.json consumed` (secrets redacted), auto-joined, file deleted; expired file `ignored and deleted`, no connection |
| C2 host from the start menu | 9 | PASS | roles Authority+Client+Admin; checkpoint `x4mp_ckpt_377c...` written in 4.5 s, uploaded, session Running; 3 bots joined after downloading it (with the fix-8 workaround) |
| C3 Request save now | 9 | PASS | second checkpoint `x4mp_ckpt_895c...` (5.1 s), at most two kept (`removed 1 old checkpoint save(s)`) |
| C4 `game_time` | 11 | PASS | server `EntitySpawn ... game_time 383.332` = mod `self-spawn sent net_id=1 game_time=383.332`. Open item 3 |
| C5 authority save control | 12 | PASS | 20 min: no vanilla autosave; only `x4mp_ckpt_*`, incl. the server's scheduled checkpoint at 15:24 ("save request 3 (Autosave)") |
| C6 HUD / self-test, authority | 13 | PASS (self-test) | 8 PASS, 0 FAIL, 1 WARN; `saves.block expected false`; HUD line: fix 9 |
| D V21 checkpoint without the mod | V21 | PASS | slot 7 = newest checkpoint, mod uninstalled: loads with no warning, no X4MP trace in the game log |

## Open items (to fix before M2 is closed, or carried into M3 where marked)

1. **Our HUD line** (fixes 7 and 9 are not yet confirmed live), and **chat + Esc hides the vanilla HUD with the mod**. Plain X4 (Run 4): Esc keeps the HUD and the
   chat fades after ~10 s. With the mod (build ee0be44): HUD hidden, chat stays. Retest on main first; the game log now names the cause (`hud: loop restarted`,
   `hud: blocked for`, `hud: redraw failed`).
2. **After `/reloadui` the mod does not know the universe is ready** (self-test SKIP `player.guard` "universe not ready", no `reload resume: same universe` line).
3. **The session-start checkpoint has no self-spawn**: MD reported no player ship (`ship (none)`) ~2 s after universe ready although the player was flying;
   later checkpoints report it. Send the self-spawn once the ship is known (or retry MD after a short delay).
4. **(fixed in close-out B, awaiting an in-game look)** **Refusal screen shows the raw server diff** in red above the groups: it is not version-normalised (`ego_dlc_split@900` "missing" and `@9.00` "extra"), lists
   optional library mods as missing, and repeats the headline. Players should only see the groups.
5. **"Last server: ..."** line missing in the Multiplayer window (the fields are remembered).
6. Game log noise: `GetNumAllFactionShips(): Failed to retrieve faction with ID 'x4mp_team_N'` for N = 1..8 at every load (our Lua asks for factions that do not exist yet).
7. `x4mp_*.x4mf` manifests pile up in the save folder (one per join).
8. **(fixed in close-out B)** Test kit: **Steam Cloud restores deleted save files** at X4 start (`steam_autocloud.vdf`), so deleting `x4mp_*` leftovers does not stick; the download test needs a save the cloud never saw (slot 7 worked).
9. (M3+) Every authority checkpoint shows X4's normal saving screen for ~5 s on the host; with real players that interrupts the host each time. Design question for later milestones.
10. (M3) Download progress is invisible on loopback (0.18 s for 35 MB); check it on two PCs.
