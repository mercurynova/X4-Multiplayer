# In-game session 3: results (2026-10-03)

Script: [in-game-session-3.md](../in-game-session-3.md). Real `x4mp` mod, X4 9.00 build 611726, one PC. Enabled extensions: all 7 DLCs,
x4native, x4mp, kuertee UIX (`ws_3477279743`, "UI Extensions and HUD") and SirNukes Mod Support APIs (`ws_2042901274`), both left on from session-2 D8.
Log zips (local, git-ignored): `out/session3/logs-run1-attempt1-*`, `logs-run1-part1-*`, `logs-run1-*`.

## Fixes made during the session (all merged on main)

| # | Found | Cause | Fix (commit) |
|---|---|---|---|
| 1 | Closing the Multiplayer window on the start menu left a blank screen | vanilla `Helper.registerMenu` ignores `OpenMenu` while the menu's `shown` flag is still set; we opened our window without closing OptionsMenu through Helper | leave OptionsMenu with `Helper.closeMenuAndOpenNewMenu`, reopen on close (38f3f4b) |
| 2 | Every join refused: "disable: Kingdom End, ... Timelines" | the fake authority reported no DLCs; a DLC difference always refuses | `start-fake-authority.ps1` reads the enabled `ego_dlc_*` from the install (38f3f4b) |
| 3 | Resume after the save load refused: "update: <all DLCs>" | resume ClientHello is sent before Lua re-reports the extension list; the native scan said `900`, Lua `9.00` | version normalisation (mod + server), Lua list kept in the stash (38f3f4b) |
| 4 | Vanilla cockpit HUD gone in flight, only our line visible | our HUD frame lacked `keepHUDVisible`/`keepCrosshairVisible` | set both (38f3f4b) |
| 5 | Chat window closed itself after ~1.5 s; next key press said "Chat window disabled" | our HUD frame and the chat window both on layer 3 (`"Helper3"` registration displaced) | HUD on layer 6 (ee0be44) |
| 6 | After a server restart the in-game fresh rejoin never sent NodeReady (HUD said Connected falsely) | `SaveReady` dropped while stage was InGame | `Stage::Rejoining`: same save -> NodeReady without a load; changed save -> player chooses (27c188d) |
| 7 | Our HUD line stayed gone after chat/map closed | stale `Helper6` entry in `View.menus` made `H.present()` true | release the entry on close, redraw after menu changes, 5 s refresh, no chat yield (27c188d) |

Not yet verified in game: 6 and 7 (installed for Run 1b onwards).

## Results so far

| Step | Criterion | Result | Evidence |
|---|---|---|---|
| B1 join from the start menu | 1, 2 | PASS (join, load, resume); **download not exercised** | the leftover `x4mp_f789ed3bd8a1.xml.gz` was restored by OneDrive after deletion, so the mod verified the existing file; redo with OneDrive paused |
| B1 start menu returns on close | - | PASS after fix 1 | `standalone: closed, reopening start menu` |
| B2 `/reloadui` | 3 | PASS (detached ClientReload + resumed, no `left:`) | missing log line `reload resume: same universe`; see open item 2 |
| B3 HUD and menus | - | PASS | `optionsmenu adapter OK(source=uix)` |
| B4 reconnect after server restart | 4 | PASS for the connection (token refused -> fresh join); FAIL: vanilla HUD lost, no NodeReady | fixes 6/7; retest |
| B5 30 min connected | 5 | PASS | no detach 13:40-14:10; mod_p95 max 0.089 ms; fps 70-100 in focus |
| B6 save control, client | 12 | PASS | Save row greyed; quicksave 13:40:45 reported in mod log and GUI Logs; no autosave written |
| B9 self-test | 13 | PASS (7 PASS, 1 WARN build number, 1 SKIP) | `player.guard` SKIP "universe not ready" after `/reloadui`: open item 2 |

## Open items

1. **Chat + Esc hides the vanilla HUD.** Vanilla `chatwindow.lua` (layer 3, `viewHelperType` "Chat") sets no `keepHUDVisible`, so while it is up the cockpit HUD is hidden;
   Esc leaves it up, X closes it, Enter keeps HUD and chat. Check in Run 4 (no X4MP) whether plain X4 does the same.
2. **After `/reloadui` the mod does not know the universe is ready** (self-test SKIP `player.guard`, no `reload resume: same universe` line). Fix: carry the
   universe-ready state across the UI reload (stash) or re-detect it.
3. `x4mp_*.x4mf` manifests pile up in the save folder (one per join); clean up old ones.
4. The test kit should warn when OneDrive will restore deleted `x4mp_*` files (or the script deletes them itself with a check).
5. FPS reads ~10 while X4 is in the background (alt-tab); not a bug, note for the GUI FPS column.
