# Session 4, two-PC run over the internet (2026-10-19, 22:30-23:09 UTC)

Script: [in-game-session-4.md](../in-game-session-4.md) M3-33 check, M3-29 re-host, 3.5. Main `618a453` (M3-24..33). PC A = server + X4 authority **Alice**;
PC B = X4 client **Bob** on a **different network** (both wired), joined over Tailscale (`tailscale ping`: direct, not relayed, 11 ms; GUI ping 12-15 ms).
PC B is a different machine than in earlier runs: new identity (player 13). Logs (local, git-ignored): `out/session4/logs-s5a-20261019-190839.zip` (PC A),
`logs-s5b-20261019-190914.zip` (PC B). Recordings: `Video Recordings/Local Machine/2026-10-19 18-29-55.mp4` (PC A, local time = UTC-4),
`Video Recordings/Second Machine/2026-10-19 18-30-35.mp4` (PC B). **PC B's clock is ~24 s ahead of PC A's** (Alice's superhighway entry 22:53:42 on PC A
= Bob hides her ghost 22:54:06 on PC B).

## Results

| Check | Verdict | Evidence |
|---|---|---|
| Internet link (criterion 13 part) | PASS | Realtime lane UDP, loss 0.0 %, ping 12-15 ms |
| Join + takeover on a new PC (M3-30) | PASS | avatar provisioned at once, takeover ~1.5 s, one `[MP] Bob` |
| M3-33 superhighway signal | PASS | both rides: `superhighway entry signalled by sector` + `also signalled by zone (+0 ms)`, `exit signalled by exit` 3.6 s later; **the `gate` event never fired** (no earlier signal exists). Bob hid Alice's ghost at her entry and showed it once in the exit sector; Alice's ride looked fine on Bob |
| M3-29 / Finding 20 re-host (Bob stays online) | PASS (rebind path) | Alice quit + re-hosted: the server rolled back (`net ids below 15, 1 entities removed`, old Alice net 13), Alice self-spawned as net 15, Bob's avatar `SAD-023` was in the checkpoint and was rebound (no re-provision needed). Bob kept one ship. Alice came back at her **checkpoint** pose (22:49, before the superhighway): expected, the universe rewinds to the newest checkpoint. The "checkpoint predates the player" case (re-provision) was not exercised |
| 3.5 UDP fallback (criterion 13) | PASS | Disabling the allow rule did nothing (stateful firewall / existing flow); an explicit **block** rule on UDP 47781 switched Bob to TCP within seconds, Bob stayed connected, Alice kept moving; rule removed + allow rule enabled: UDP back after ~5 s |
| Finding 16 (stale ghost after re-host) | PASS | Bob's log tracks only net 15 after the re-host, no leftover net 13 |

## Findings (numbering continues)

| # | Finding | Status |
|---|---|---|
| 22 | **After an authority re-host the remote player's ghost alternates between two spots** (one icon, both in Nopileos' Fortune II, Alice standing still). Bob's `[sync]` for Alice net 15: `newest_age_ms` climbs 34 -> 39 -> 44 -> 47 s and drops back to 34 every ~15 s, `held` = all frames, `delay_ms=100`: Bob keeps receiving a ~34 s old sample (likely the self-spawn pose from the checkpoint, `self-spawn sent net_id=15 game_time=1081.344`) alternating with Alice's real pose. Seen 23:01-23:02 UTC (PC A clock), user timestamp 23:01:04 | open |
| 23 | **Map icon bounces back and forth along the travel line during fast in-sector flight**, both directions (Alice's ghost on Bob before she reached the highway; Bob's avatar on Alice when Bob travelled). One icon, "jumping rapidly almost back and forth instantly", along a diagonal line, far from the true spot; PC focused. Bob's `[sync]` for Alice at the time is accurate (err p95 23-45 m, max 151 m, no snaps), so the 3D ship stays on its path: the map icon is drawn somewhere else for single frames. Suspect: the 5 Hz velocity hint (`set_object_velocity`) / engine flight model vs our per-frame pose writes. Related: at 22:49:22 the authority repaired Bob's **parked** avatar `it was 286216.7 m off its pose` (around Bob's local-highway ride): the engine moves avatars by itself | open |
| - | Replication rate of Alice's ship on Bob was ~10 Hz (`rep_msgs` +51 per 5 s), not the 20 Hz M3-33 intends | open: check (authority FPS ~11 while unfocused may cap it) |
| - | FPS ~11-12 on whichever PC is unfocused (one operator): FPS numbers from an unfocused PC are void (as in sitting 3) | note |
