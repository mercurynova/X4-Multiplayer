# In-game session 4, sitting 3: two PCs, real X4 authority + real X4 client (2026-10-06)

Script: [in-game-session-4.md](../in-game-session-4.md) Sitting 3. PC A = server + X4 authority **Alice**; PC B = X4 client **Bob**, joined over the
**Tailscale address** (`tailscale ping`: direct via LAN, 19 ms; GUI ping 6 ms). One operator (Parsec from PC A into PC B). Session save `save_004`,
kit from main `c836053` (M3-22 + M3-23 diagnostics). Logs (local, git-ignored): `out/session4/logs-s3a-*` (PC A); PC B: `logs-s3b-*` plus Bob's own
mod log (portable mode, see Finding 15). Recordings on both PCs. Times are local (UTC-4) unless marked Z.

## Results

| Step | Verdict | Evidence |
|---|---|---|
| 3.0 network (criterion 13 part) | PASS | Tailscale direct over LAN; `start-server-lan.ps1 -Check` said "ok" for all ports but **no X4MP rules existed** (it counts any allow rule, e.g. a program rule on all ports): the three named rules were created by hand. 30 MB save download over Tailscale in ~2 s |
| 3.1 join (criteria 3, 14) | PASS after workaround | first join: Bob became **player 4 = Alice** (Finding 15), the authority was dropped (`Alice left: Rejoined`), session AuthorityLost. Alice re-hosted inside the 900 s grace (the setting survived the server restart: Finding 14 not reproduced). With `x4mp.portable` on PC B: Bob = player 10, avatar granted after 10.1 s (the authority had to spawn it), takeover done 10.4 s, rejoin later 0.71 s |
| 3.2 fly together (criteria 1, 2, 6) | PASS with findings | ghost of Alice on PC B: flight smooth, **turning in place lags seconds then snaps** (Finding 17); Bob's avatar on PC A: 4/5, turns quickly; gate: Bob shown ~3 s after the jump; highway: the avatar waits at the entry and jumps at the exit (design), Alice's ghost hidden + shown again per highway (no pop counted). ~25 min of flight, not the full 30 |
| 3.2 map (Finding 4) | **not reproduced** | Bob's map showed Alice's discoveries; knowledge probe constant through every takeover stage (`sectors_known=6/152 ... gates_known=3/323` at universe ready, avatar spawned, teleported, guard, original removed, first ghost, janitor). Sitting 1 had a **FakeNode** authority serving the raw save; here a real authority's checkpoint |
| 3.3 chat (criterion 10) | PASS | `hi from Bob` arrived on PC A with a **toast** (first real toast test), `/t` from Alice arrived on PC B |
| 3.3 relations (criterion 9) | PASS | Bob -> team 2: avatar re-owned at once; Hostile red on both PCs (radar + names), Allied blue on both, back to team 1. Side effects in the authority's X4 log: vanilla `md.PlayerReputation` error on `faction.x4mp_team_2` and a "Prized Investor" notification naming X4MP Team 2 (Finding 19) |
| 3.4 reload, checkpoint, leave, rejoin (criteria 3, 7) | PASS | `/reloadui` on PC B: Alice's ghost adopted, no new duplicate; Request save now: ~5 s save screen on PC A, Bob kept moving; Bob disconnects: avatar parked and still (M3-20 holds); rejoin: back in the parked fighter, no second Bob |
| 3.5 UDP fallback (criterion 13) | not done | session paused; firewall rules now exist under the expected names |
| perf | note | PC A at **10 FPS whenever its X4 window is unfocused** (Parsec in front), 85-120 focused: unfocused FPS numbers are void. Mod p95: authority 0.13 ms, client 0.20 ms |

`sync-report.ps1` on Bob's log: pops 0 (PASS), display latency p95 **250 ms = the cap** on a 6 ms link (FAIL, Finding 9 again), path-error medians
mixed with the stale parked ghost of Finding 16 (not judged). Note: PC B's `collect-logs.ps1` picked up PC A's mod log through OneDrive.

## Findings (numbering continues sitting 2)

| # | Finding | Status |
|---|---|---|
| 15 | **Player identity is shared through OneDrive.** `player.key` (and `x4mp.json`, the logs, the X4 debug log name) live in `Documents\Egosoft\X4\x4mp\`; with Documents in OneDrive, every PC of the same user has the same key, so a second PC joins as the same player and replaces the first (the authority was dropped) | open: keep the key (and logs) per machine, e.g. `%LOCALAPPDATA%`; workaround: `x4mp.portable` in the extension folder |
| 16 | **Stale ghost after an authority re-host.** Alice's ship was net_id 10 (first run); after the quit + re-host the authority reloaded the checkpoint (saved before net 10 existed) and self-spawned as net_id 1; the server's world mirror kept net 10, so clients show two `[MP] Alice` (one parked where Alice was at the quit) | open: on an authority reload the server must drop entities newer than the checkpoint (or the authority's `next_net_id` must continue the server's) |
| 17 | **Client ghost turns late when the remote ship turns in place**: seconds of lag, then a quick snap; flight and turns while moving look right; the authority's avatar of Bob turns quickly | **fixed (M3-26), in-game check pending**: the client decoder (`core/ghost/replication.cpp`) treated a Replication entry without POS as a state-only update, but the server sends a rotation-only entry (ROT + TIME) for a ship turning in place, so the heading only changed at the next keyframe (5 s, the one entry with POS). Now ROT / VEL entries are pose samples too. Check: Alice turns in place on PC A, Bob's ghost of her follows within about a second |
| 18 | **Authority map glitches (zoom in/out) in the sectors where a remote player's avatar has been**: Pious Mists II (avatar spawn + park), the highway in Nopileos' Fortune VI (avatar waited at the highway entry). Persists with the avatar parked, with X4 focused, after `/reloadui`; the client's map is fine. X4 log: `helper.xpl(2539) GetChildren(): invalid frame ID ... destroyed already` (14x) | open. **Video (PC A, 12:06:01-12:06:30 local, 2 s samples):** not a zoom: the sector's map **flips between two states**, (a) normal explored area and (b) the whole sector black except one small radar bubble around a ship. Same picture as **Finding 4** (sitting 1, client, "fog resets while the map is open"): 4 and 18 are probably one bug that sitting 1 saw on the client and sitting 3 on the authority. Suspects: what the map draws as the player's explored/radar area when team-faction ships (relation +1.0 to the player, `set_faction_known`) are near, their pose writes, the team apply (first noticed right after the team move) |
| 19 | Team factions trip vanilla scripts: `md.PlayerReputation.PromotionInstant` lookup on `faction.x4mp_team_2` fails; a reputation notification names "X4MP Team 2" | open, minor |
| - | Kit: `topology.ps1 -Check` reports ports as open when only an unrelated allow rule (program rule, any port) covers them | open: check the named X4MP rules |
| - | Kit: `install.ps1` fails with "The cloud file provider is not running" when Documents is in OneDrive and OneDrive is not running | open: clear message ("start OneDrive") |
| - | `GetObjectPositionInSector(): Failed to retrieve sector` for the own ship, ~100/s in bursts during gate / highway transits (X4 log noise) | open, minor: skip the read while the sector is unknown |
| - | Design: an avatar of a player on a highway sits at the highway entry in the authority universe (visible to everyone there) | open: decide (hide it, or move it along the highway) |
