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

**Status (M3-29): fixed, in-game check pending.** The server re-sends the stranded player's last PlayerShip (pose refreshed from their newest PlayerState) to the next authority once it is in game; the client follows a replaced avatar net id without a new takeover. See docs/m3-plan.md section 8, M3-29.

## Two-PC run (same day, 16:15-16:47 local; Alice = authority PC A, Bob = client PC B over Tailscale)

Logs: `out/session4/logs-f4-a-*` (PC A), `logs-f4-b-*` (PC B). Both PCs had new machine-tagged identities (M3-24); the old names had to be
released in the GUI first (expected; the refusal text was clear).

**Finding 4 (client) found: the client's own avatar ship does not record explored space.** Bob's map stayed fogged with only his radar
bubble; flying 20-30 s into fog left **no revealed trail** on Bob's map, while Alice (flying her save ship) left one. With
`diag.takeover_off` (Bob stays in the save's original player ship, no takeover) Bob **did** leave a trail. So the ship the takeover gives the
client (spawned for the team faction, then `SetComponentOwner(player)`; or bound from the save as a team-owned avatar copy) is not treated
as the player's exploring ship by the engine. (Earlier sightings: sitting 1 "only the radar bubble", sitting 3 Bob showed only what the
downloaded save had already discovered.)

**Finding 18 (authority) reproduced: superhighway exit.** Bob's map-flip candidates one at a time: near Alice 90 s, a local highway: no
flips. Bob through the **superhighway** that also triggered it in sitting 3: Alice's map "freaked out" right after; Bob's position flashed
on ~4 spots, and it calmed down once Bob's avatar caught up. Alice's watch: Bob's avatar `TLH-305` **bounced between two sectors**
(`cluster_04_sector002` <-> `cluster_04_sector001`) at 20:45:03-20:45:07 UTC, and at 20:45:18 it was briefly `live=1` (in the authority
player's live view; normally `live=0`). Bob's client sent no state from 20:44:40 to 20:45:09 (`selfship: no state is sent: the ship's
sector is not in the sector map`, inside the superhighway) and then `teleport state, sector 6`. Watch otherwise constant for the avatar:
`own=t1 rel=1 radar=1 gravidar=0 active=1 known=1`. One avatar for Bob (no duplicate after the takeover_off run).

**Fix directions:** (4) give the client a ship the engine treats as the player's own explorer (create it player-owned, or keep the save's
player ship and make it the avatar copy); (18) the authority must move a player's avatar exactly once at a superhighway exit (no sector
ping-pong between the last known state and extrapolation / velocity hints), plus consider the +0.99 own-team relation so a team ship is
never the engine's `self` range.

**Finding 18 status (M3-31, 2026-10-06): fix in, in-game check pending.** Authority avatar: no stale velocity hint during a silent gap, one move to the exit sector (no jump back to the old pose / sector), the client sends one Hidden state when its sector leaves the sector map; player <-> own team relation is now +0.99. Script: `docs/in-game-session-4.md` "M3-31 check"; summary: `docs/m3-plan.md` "M3-31".
