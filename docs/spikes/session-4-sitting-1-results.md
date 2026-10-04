# In-game session 4, sitting 1: real X4 = client, FakeNode authority + wingmen (2026-10-04)

Script: [in-game-session-4.md](../in-game-session-4.md) Sitting 1. One PC, X4 9.00 build 611726, all DLCs, UIX + SirNukes enabled, session save
`save_004` (served with `-FreshDownload`), start sector **Grand Exchange I** (galaxy-dump index 1). Logs (local, git-ignored): `out/session4/logs-s1a-*`,
`logs-s1b-*`, `logs-s1c-*`. Recordings (local, git-ignored): `Video Recordings/Local Machine/2026-10-04 02-05-47.mp4` (02:05-02:48 local) and later files.
Times below are UTC (local = UTC-4).

## Results

| Step | Verdict | Evidence |
|---|---|---|
| 1.1 join + takeover (criteria 3, 14) | PASS | download, load, takeover **done in 0.38 s** (0 refusals), save ship JCG-144 removed after the guard, avatar AVA-004 (Argon Elite), `janitor: swept: 0`; rejoin: same avatar net id, takeover 0.38 s; **Last server** line + pre-filled address/name present |
| 1.1 loadout | known gap | client's local avatar copy has Mk2 weapons/shield, Mk3 engine, Mk2 thrusters (no early-game loadout on the client copy) |
| 1.2 wingmen (criteria 2, 6) | PASS after fix | smoothness **4 straight, 4.5 turns/boost**; path error p95 2-8 m steady; gate jump: one snap per ghost, ghosts visible ~3 s after the jump; superhighway exit: one snap; 0 pops. **Facing was wrong until M3-15** (Finding 2) |
| 1.3 reloads (criterion 7) | PASS | `/reloadui` x2: `ghost adoption: records=3 adopted_by_idcode=3 lost=0`, then `spawned=0 adopted=3`; no duplicates; exit to menu + rejoin: fighter again, no self-ghost |
| 1.4 chat (criterion 10) | PASS (toast untested) | `hi all`, `/t`, `/w Wing01` all work, whisper answered by Wing01 only, names in team colour (own line too); SirNukes `/` command still works; toast not seen because no line arrived while the window was closed (retest in sitting 3) |
| 1.5 relations (criterion 9) | **PASS on retest** (after M3-18) | first run: relations applied but the moved player's ghost kept team 1 (Finding 5). Retest 18:37 UTC: Wing02 team 2 cyan -> moved to team 1 orange -> back cyan; Hostile -> red (radar + name), Allied -> back; applies acked < 0.1 s. Server DB keeps teams across restarts (team 2 already existed) |
| 1.6 SETA (criterion 12) | not tested | the user has no SETA item |
| 1.7 quicksave (criterion 8, client half) | PASS | quicksave: 3 `[MP] ` leftovers (expected on a client); loaded single player: `janitor: swept: 3 leftover ... 0 refused` after the 15 s standalone grace; re-saved `save_008`: `0 '[MP] ' named`, result OK |
| 1.8 performance (criterion 11) | partial | 8 ghosts: mod main-thread p95 median **0.27 ms** (target < 0.2, Finding 6); rx median 14 kB/s, tx 2.4 kB/s; **FPS 70 with 8 ghosts vs 100 after Disconnect** (Finding 8); UDP state not visible in the GUI (Finding 7) |

`sync-report.ps1` (whole log, incl. older runs): path error p95 PASS (1.7 m median, worst 50 m in catch-ups), steady WARN (1.9 m, worst 21 m), **display latency FAIL 218 ms (target <= 200)** (Finding 9), pops PASS (0), log rate PASS (3.4 lines/s).

## Findings

| # | Finding | Status |
|---|---|---|
| 1 | Exit to the main menu keeps the session "in game" (the DLL re-inits and resumes stage `ingame`; the start-menu Multiplayer screen shows Connected/Disconnect). The server still thinks the player flies. | open: going to the menu should leave the session or offer Rejoin |
| 2 | Ghosts never turned (always +Z): X4's `SetObjectSectorPos`/`SpawnObjectAtPos2` take **degrees**, `GetObjectPositionInSector` returns radians (x4n_math.h:20); the mod wrote radians | **fixed M3-15**, confirmed in game (wingmen turn with the player); orientation signs confirmed correct |
| 3 | The open map went blurry 11.6 s after opening: the HUD's stale-entry heal (`blockedMax` 10 s) force-drew over the live MapMenu | **fixed M3-16**, confirmed in game (map sharp > 30 s) |
| 4 | Map fog of war: while connected only the current radar bubble is shown, every explored area is unexplored. Same save in single player is correct (decompressed session save is identical). Not the HUD (persists after M3-16). | open: diagnosis points at the client takeover (TeleportPlayerTo / SafeRemove of the save's player ship). Experiments: map before vs after the takeover; a build that skips removing the original ship. Sitting 2 (authority, no takeover) is a free comparison. **Video clue (02:38:47 local):** the HUD says "To: **Unknown Sector**" and stations carry **"?"** markers, so the client lost the player's *knowledge* of sectors/stations (`knownto=player`), not only the map fog |
| 5 | A player moved to another team keeps their avatar in the old team faction (nothing re-owned avatars, real or fake authority) | **fixed M3-18**, confirmed in game (retest 1.5) |
| 6 | Mod main-thread p95 0.27 ms with 8 ghosts (target < 0.2 ms) | open, perf pass |
| 7 | Kit: the sitting server's GUI was an old web build (no "Realtime lane"/"Ghosts shown"): the publish reused a stale `server/web/dist` | web rebuilt before the 1.5 retest; open: make the kit/publish rebuild or check the web build |
| 8 | FPS 100 -> 70 with 8 ghosts nearby (~4.3 ms/frame, the mod itself ~0.3 ms) | open: split rendering vs per-frame `SetObjectSectorPos` (parked vs driven ghosts; compare with 8 NPC ships) |
| - | The 230 m path-error spike at 06:26:22 UTC: the video shows the map open and blurred at that moment (Finding 3), not a ghost problem | explained |
| 9 | Ghost display delay adapts up to the 250 ms cap (latency p95 218 ms, target <= 200 ms) on one PC: jitter is probably X4 frame hitches (frame p95 30-100 ms) + bot timing | open, look at the jitter estimator |

Bot-only behaviour (not mod issues): wingmen cap at 350 m/s (fall behind on boost/travel drive, never use highways, fly straight to the player), fixed 400 m orbit clips through stations (no reaction from the station). Doc fixes from this sitting: sector index 1 is Grand Exchange I; no `-OwnAvatar` on a client savescan.
