# Session 4, M3-28 map fog experiment, runs 0 and 0b (2026-10-06)

Script: [in-game-session-4.md](../in-game-session-4.md) "M3-28 map fog experiment". One PC, main `2633ad4` (M3-24..28), real X4 authority `Tester`,
2 FakeNode wingmen (`start-fake-clients.ps1 -SaveName save_004 -Wingmen 2`). Logs (local, git-ignored): `out/session4/logs-f18-0-*`, `logs-f18-0b-*`.

## Results

| Run | Setup | Map flips (Finding 18) |
|---|---|---|
| 0 | fresh session; flew to Pious Mists II (`cluster_22_sector001`, the sitting-3 glitch sector); map + `/x4mp knowledge watch` 90 s; Wing01 -> team 2, 60 s; back to team 1, 60 s | **none** |
| 0b | quit + re-host (loads the session-start checkpoint), bots restarted (they join AFTER the re-host, the sitting-3 order), same B-E | **none** |

So bots alone do not reproduce Finding 18; switch runs 1-5 were not done. What a real second player does that the bots never do (next candidates):
highway travel (the avatar is held at the highway entry while its player is Hidden), docking, a player that leaves (parked avatar with
controller 0), a real client's PlayerState flags; in sitting 3 the avatar also spawned ~6.6 km above the sector plane.

Also confirmed:
- **M3-24:** this PC got a new machine-tagged identity (player 11); the name `Tester` was accepted.
- **M3-28 HUD guard:** 17 `hud: blocked by MapMenu` events, **0** `helper.xpl ... invalid frame ID` errors (sitting 3: 14).
- **Watch probe** works: header every 2 s, per-ship lines on change. Both avatars report `radar=1 live=1 gravidar=0 active=1 known=1`
  (`active=1` although the mod writes `ActivateObject(false)`; to check against the design, H3 in the analysis).
- **M3-25 rollback** works: `world rolled back to net ids below 1 (3 entities removed)` on the re-host.

## Finding 20 (new)

**Online players lose their avatar after an authority reload from a checkpoint that predates them.** The only checkpoint was the
session-start one (stored 3 s before the bots joined). On the re-host the server rolled the world back (M3-25, correct: the avatars were
not in that save) and the authority's director kept 0 of 2 records (M3-22, correct), but the still-connected bots never got a new
avatar: nobody re-sent their `PlayerShip` / asked the authority to provision them. Restarting the bot process (they rejoined) fixed it:
`PlayerShip from player N: provisioning a new avatar`. A real client would be stranded the same way. Fix: after a rollback (or when an
authority finishes loading), the server must make the authority provision every online non-authority player whose ship entity is gone
(re-send their last PlayerShip, or have the client re-announce).
