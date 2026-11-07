# Session 4, two-PC run, verification of the 2026-10-28 fixes (2026-11-07, 12:17-14:02 local)

Script: [in-game-session-4.md](../in-game-session-4.md) (F1, F13, F21b, F23c, F24, F25 checks and X1). Main `d1cc303` after a republish. PC A = server + X4 authority **Alice**, PC B = X4 client **Bob** over
Tailscale (ping 13 ms, UDP, 0 % loss). Logs (local, git-ignored): `out/session4/logs-s6b..s6g-20261107-*.zip` (Alice), Bob's extracted logs and `sync-report.txt`, server log `server-20261107.log`.
Mod log times are UTC, local = UTC-5; the server log is local. The lead's notes are the main source for what was seen on screen; the numbers below were spot-checked against the logs.

## Setup problems (all tooling, see F30)

| Problem | Cause | Fix used |
|---|---|---|
| `start-server-lan.ps1` ran a stale server exe from 2026-10-20 (schema 8) | `Ensure-Published` publishes only when the exe is missing | `-Rebuild` |
| The new session had no save: Alice sat at "checking the session save" | no save uploaded for the new session (F30b) | `upload-save.ps1` |
| Bob's name was refused | stale name binding row (his `player.key` from 10-19, identity unchanged) | released in the GUI |
| Bob's X4 ran at ~12 fps when not focused | remote-desktop session (Parsec) on PC B | focus the window; not the mod |
| The GUI had no "Resume session" button | the GUI build was from October (F30) | rebuilt later; K8/K9 below may be artefacts of it |

## Results

| Check | Verdict | Evidence (local time) |
|---|---|---|
| Block A warm-up (Alice autopilot) | **PASS**, smooth 4/5 | small flickers on the inner highway and at a gate; smooth on the highway |
| Map flicker crossing Nopileos' Fortune VI -> II | **new finding: F32** | 12:32, Alice's icon on Bob's map in 3 places for ~3 s |
| F23c hard brake, map icon | **PASS** | 12:35:40 and 12:37:22 (Alice), 12:39:08 (Bob, watched by Alice): smooth on the map |
| F21b local highway, map icon | **PASS** | Bob 12:41:44-12:42:25: the icon followed; small flashes only |
| X1 flight over several sectors incl. a highway | **PASS by user decision** | from 12:52:30, superhighway ring from Pious Mists II, 25 sector changes in 8 min 21 s; session continuous 12:17-13:01 without a disconnect. The user accepted this instead of the full 30 min |
| F1b Disconnect then Join | **PASS** | Bob Disconnect 13:09:24, first Join 13:09:41 worked; rejoin load ~34 s (normal) |
| F25 `ManifestReport` after a save load | **PASS** | fast-return load 13:11:56, in-session load 13:13:17, no `ManifestReport` violation (a 0.2 s SocketLost / resume at 13:12:00 during the load) |
| F27 (new): `PlayerShip` PhaseDenied on a rejoin | new, low | server WRN 13:10:24 |
| F24 step 4, Esc > Exit as authority | **PASS** | 13:14:35 checkpoint, `saved=true` 13:14:44 (~9 s) |
| F1 authority leave from the start menu | **PASS** | 13:15:06.9 -> 13:15:36.9, 30.0 s |
| F1d no world clock from the menu | **PASS** | no keepalive from the menu |
| F1c fresh authority join from the menu | **PASS** | 13:16:50 the authority was sent the save |
| F28 (new): Bob invisible on Alice's map after rejoin / re-host | **new, high** | from 13:10, see below |
| F24 step 1, Disconnect | **PASS** | 13:21:17, `saved=true` 13:21:23 (~6 s) **with the Multiplayer screen open** (answers open question 7a) |
| F24 step 2, come back where she left | **PASS** | |
| F24 step 3, X4 frozen during the save | **not testable by hand** | X4 freezes during the save; K7: the Disconnect button shifts when the notice appears |
| F24 step 5, Esc > Quit | **PASS** | 13:32:34 -> saved 13:32:40, `ClientQuit` |
| F24 step 6b Alt+F4 / 6c client Disconnect | **PASS** | Alt+F4 = `SocketLost`, no planned leave; client Disconnect leaves at once |
| F24 step 8 | skipped | covered by unit tests |
| F13 step 3, Alt+F4 and rejoin within the grace | **PASS** | admin checkpoint 13:36:27 at Nopileos' Fortune II / 8,000,000 Cr; Alice moved to Pious Mists II, spent to 162,728 Cr; Alt+F4 13:39:18; rejoined 13:42:02 (grace 900 s); position and 8,000,000 Cr back via the save, ledger "0 undone" |
| F13 step 3b, Bob on Alice's map after the rejoin | **PASS** | Bob moved smoothly right after |
| F13 resume of an Ended session + F13b admin-kept path | **PASS** | session 5 created from the checkpoint; admin +1,000,000 to Bob (100,000 -> 1,100,000); Alt+F4; grace 30 s -> Ended 13:59:15; admin resume 14:00:01; Alice reloaded; ledger "0 transactions undone, 1 kept"; Bob stayed 1,100,000 (correct per ADR-053) |
| F13b player-to-player reversal | **not testable in game** | no in-game transfers before M5b |
| F26 | reachable paths behaved | held leave (13:14:35, 13:32:34), leave answer, sample clock across the re-hosts: no regression seen |
| F9 | not re-measured | |

## Findings

| # | Sev. | Finding |
|---|---|---|
| F28 | **high** | **Alice's avatar driver stops placing Bob after his fresh rejoin; after Alice's re-hosts the avatar is gone.** A Bob Disconnect + Join fixed it each time. |
| F32 | medium | **Map icon flicker for seconds after a sector change at superhighway speed** (both 12:32 and 12:58 events). |
| F27 | low | `PhaseDenied` for `PlayerShip` / `PlayerState` at `universe ready` on a rejoin or in-session load. |
| F29 | low | A lowered `AuthorityGraceSeconds` does not shorten the running countdown. |
| F30, F30b | medium | Stale server exe and stale GUI from the helper script; no message for an authority joining a session without a save. |
| F31 | note | Resume after a server restart is not supported; the GUI gives no hint. |
| K7, K8, K9 | low | Disconnect button shifts; no grace countdown; stale session history and a stale low-FPS warning (K8/K9: re-check with the current GUI). |
| E15 | low | Each test project overwrites CI's single `results.trx`. |

### F28, Bob invisible on Alice's map (new, high)

**Seen.** After Bob's fresh rejoin (first Join 13:09:41) Bob's icon was not placed on Alice's map any more. After later re-hosts of Alice the janitor removed the leftover `[MP]` object and Alice had no avatar at all.
A Bob Disconnect + Join restored it, and was needed again after each later re-host.

**Evidence (Alice's mod log).** Before the rejoin the avatar stats line is healthy: `avatars=1 live=1 ... unknown_net_id=0 set_pose=68583` (13:08, local). From the 13:11 window every Bob state is counted as unknown: `unknown_net_id=335` and growing,
`set_pose` stops (68775); after a re-host the line reads `avatars=0 live=0 states_in=75 unknown_net_id=75 set_pose=0`, and later `live=1 ... unknown_net_id=1235 set_pose=0` (every state unknown, nothing placed).
It recovered only after a Bob Disconnect + Join (`unknown_net_id=0` again from ~13:43). Server side the takeover itself completed each time (`takeover: avatar spawned`, `takeover: teleported` for Bob).

**Reading.** The avatar driver keeps a net id to avatar mapping; a fresh rejoin or a re-host gives Bob a new net id (or removes the object), and the driver is not told, so it files his states as unknown. The F20b re-provision
path (PASS on 2026-10-28) covers only a world rollback. The exact trigger (the fresh rejoin, the re-host, or the janitor sweep) is not pinned: the fix agent should reproduce it in the HostSim pair test (client fresh rejoin, authority re-host, client states keep flowing).
**Status:** open, being fixed in parallel. Block M3 exit on it.

### F32, flicker after a sector change at superhighway speed (new, medium)

**Seen.** 12:32, Alice crossing Nopileos' Fortune VI -> II: her icon appeared in three places on Bob's map for ~3 s. 12:58:41-12:59:57 (laps 19-21 of the superhighway ring): Bob's icon flickered on Alice's map right after Bob's sector changes.

**Evidence.**
- Bob's log, 12:32: Alice's ghost hidden 17:31:56Z, shown 17:31:59Z, then `ghost engine guard`: the game moved the ghost 64-204 km, speeds 1.5-5.5 km/s, `highway context no`; the guard counter went 754 -> 1360 in ~25 s.
- Alice's log, 12:58: `avatars: engine guard: avatar of player 13 ...: the game moved it: 76785.6 m ... (speed 11349 m/s), highway context no; placed it back, velocity zeroed` at 17:58:06Z, 149 km at 17:58:15Z, 237 km (3313 m/s) at 17:58:41Z; `vel_hints` and `engine_moved` rose by 23 together per event.
- Lap 14 (12:57:06-12:57:24): Alice's autosave; her ghost fell behind briefly (coincides with her autosave; cause not checked).

**Reading.** Same mechanism as F23c (an inert object flown away by the engine, then dragged back by the guard, the map drawing the displaced frames) but triggered by a sector change at superhighway speed instead of a hard brake: a velocity hint is sent for the first samples in the new sector and
the engine integrates it against the sector wrap. The F23c fix (no hint while decelerating or right after a guard hit) does not cover the sector change. **Fix direction:** no hint for ~2 s after a sector change on either side (ghost and avatar), and zero the object's velocity on the sector switch. In-game check: the superhighway ring again, expect no guard burst.

### F27, PhaseDenied on a rejoin (new, low)

Server WRN `13:10:24.648 conn 10: policy violation "PlayerShip" ("PhaseDenied"); 1 in the last minute`, 2 ms after Bob's `universe ready` knowledge line (his fresh rejoin); the same for `"PlayerState"` on Alice's in-session load (13:31:01) and `"PlayerShip"` again on Bob's rejoin
(13:57:46). The takeover completed each time. Same family as F25 (a message sent in the moment of `universe ready`, before the server has moved the node to a phase that allows it). Fix: send after the phase is confirmed, or allow the message in the readying phase.

### F13 resume and F13b (what the game could and could not show)

The Alt+F4 rejoin within the grace restored position and 8,000,000 Cr from the checkpoint (the money comes back through the save, not through the ledger; "0 undone"). The resume of an Ended session (session 5, created from the checkpoint) loaded the checkpoint and ledger
reported `0 transactions undone, 1 kept`: Bob's +1,000,000 was an admin adjustment, which ADR-053 keeps. The player-to-player reversal path needs in-game transfers (M5b). In-game money does not follow the server wallets yet (M5): the F13b result is the server side only.

### F29, F31 and the grace

Lowering `Session.AuthorityGraceSeconds` after the authority was lost did not shorten the running countdown: the deadline is set at the loss (`SessionActor.Phases.cs` ~93). Workaround used: Stop session (13:50:26 `Stopping -> Ended`). Fix: recompute the deadline from the loss time when the setting changes.
F31: a session cannot be resumed after a server restart (ADR-052/053 notes: create a new session from the checkpoint). Not a bug, but the GUI gives no hint; add one.

### F30, the helper script publishes stale builds (tooling)

`Ensure-Published` (`tools/session3/common.ps1`) publishes the server only when the exe is missing (it first ran the 2026-10-20 exe) and builds the web GUI only when `server/web/dist` is missing, publishing with `SkipWebBuild=true`: the GUI was the October build (no Resume button).
Fix: `start-server-lan` republishes when the exe or the web build is older than the source. F30b: an authority joining a session that has no save sits at "checking the session save" with no message; the mod and the GUI should say "no save for this session, upload one".

## What only the game showed

- The sector-change flicker (F32) and Bob's invisibility after a rejoin (F28): neither shows in unit or HostSim tests, which do not run the real engine or a real client rejoin against a re-hosting authority.
- F24 works with the Multiplayer screen open (the save needs 6-9 s; X4 itself freezes during it).
- Real X4 restarts and Alt+F4 are `SocketLost`, not planned leaves, as designed.
- The F23c brake fix and the F21b local-highway fix hold in real flight.

## Open questions

- F28: which of the three triggers (fresh rejoin, re-host, janitor sweep) starts it; first reproduce in the pair test.
- K8/K9: are they artefacts of the stale GUI? Re-check with the current GUI.
- Lap 14: did the autosave stall Alice's ghost sender? Coincides, not checked; low priority.
- X1: the user accepted 8 min 21 s / 25 sector changes. Record this in the M3 exit report as a decision, not as a 30 min run.

## Next steps

1. Fix F28 (high) and F32 in parallel; then re-run the Bob rejoin + re-host sequence and the superhighway ring (a short session; same two PCs).
2. Tooling: F30/F30b (republish when stale, a no-save message), E15 (one `.trx` per test project), K7.
3. F27, F29, F31 and the GUI re-check (K8, K9) with the current GUI.
4. Then the M3 exit report (X2): X1 as the user's decision, the verified rows above, F9 accepted for M3, M4 items from the backlog.
5. After this major step: remind the user to run a code review and start a fresh session (docs are updated).
