# M4 exit report

Date: 2026-10-27. Criteria: [roadmap.md](roadmap.md) M4 row and [m4-plan.md](m4-plan.md) section 3. Plan of record:
[m4-plan.md](m4-plan.md) (eight waves, M4-01..M4-31). In-game evidence: [in-game-session-7.md](in-game-session-7.md).

**Read this first.** M4 is code-complete and every CI gate is green, but the milestone's acceptance numbers are
**in-game** numbers and session 7 has not been run yet. This report therefore has two kinds of rows: **PASS (CI)** where
the claim is a test result quoted from a command, and **PENDING (session 7)** where the claim is a number only a running
game can produce. The wave acceptance clause "every roadmap M4 acceptance number is quoted from a log, not asserted" is
**not met yet** - each pending row names the exact log line the number will come from, so the run fills the table instead
of rediscovering it.

---

## 1. Gates

| Gate | Result |
|---|---|
| Mod build, `relwithdebinfo` | exit 0, no compiler errors |
| `ctest` (full, from `mod/build/msvc-x64-relwithdebinfo`) | **635 tests: 631 passed, 0 failed, 4 skipped** (`smoke.netprobe_real_server`, `smoke.headless_real_server`, `live.authority_upload_kill`, `smoke.headless_udp_real_server` - all four need a live server or a game) |
| `x4mp_host_tests.exe` (whole binary) | 321 test cases, 13 405 assertions |
| Lua (`python mod/tests/lua/run_lupa.py`) | 252 passed, 0 failed |
| .NET `X4MP.Core.Tests` | 1378 / 1378 |
| .NET `X4MP.Server.Tests` | 597 passed, 1 skipped. One timing test is a known flake and passes alone: `Saves.SaveLatencyTests.ATransferRaisesAnInGameNodesP99LatencyByNoMoreThan10Ms` (p99 rise 19.7 ms against a 10 ms allowance) |
| `X4MP.Server.Tests` recording suite (M4-26..M4-28) | 13 / 13, including a ten-minute synthetic session that records and reads back with no decode error |

---

## 2. Acceptance criteria

| # | Criterion (roadmap M4) | Verdict | Evidence |
|---|---|---|---|
| 1 | ≥ 99% station matches | **PENDING (session 7)** | Implementation: `8dd3bf4`. The number is the first field pair of `[statics] manifest report <ckpt>: M/N stations matched over S sector(s) (U unmatched, A ambiguous, I by id code, E same UniverseID, R manifest rows, P pruned, T ms)`, also forwarded to the server log by `diag_hub().forward_log`. Per-sector detail follows as `[statics] sector <macro>: N stations, M matched, X missed, A ambiguous` |
| 2 | Same NPC ships on client and authority in current and adjacent sectors | **PENDING (session 7)** | Client side: `[sync] ghosts tracked=T visible=V spawned=.. respawned=.. hidden=.. removed=.. adopted=..` every 5 s. Authority side: `capture:` counters and the `EntitySpawn`/`WorldUpdate` counts in the recording (`x4mp-server record info`) |
| 3 | p95 error < 25 m within 5 km | **PENDING (session 7)** | `[sync] net=<id> frames=N err_p50/p95/max=.. m steady_p95=.. fast_p95=.. lat_p95=.. ms speed_max=.. snaps=.. extrap=.. held=..` - the path error is rendered position vs the sender's own samples at the same server time (M3 Q3 measurement, unchanged in M4) |
| 4 | Busy sector (≥ 200 ships): ≥ 45 FPS client, < 3 ms/frame authority mod cost | **PENDING (session 7)** | `budget: fps=.. frame_ms=P50/P95 mod_ms=P50/P95/MAX authority_ms=P50/P95/MAX clients=.. ghosts=.. suppressed=.. rx=..B/s tx=..B/s per_client=..B/s udp=..` every 30 s. `authority_ms` is `AuthorityFlow::step` timed through `stats::timing_hub()`, which is the authority-only cost the whole-mod `mod_ms` cannot isolate (M4-30) |
| 5 | < 200 kB/s per client | **PENDING (session 7)** | Same `budget:` line. `per_client` is `tx / clients` and is an **upper bound**: the server fans out by interest, so a real client pulls this or less. A tighter number comes from the recording (`x4mp-server record info` totals over the session span) |
| 6 | Zero `spawn-near-player` pops on a highway transit | **PENDING (session 7)** | `[sync] ghosts ... spawn_near=N` in the 5 s line; `N` must stay 0 across the transit. The counter is `M4-23` (`f8be507`) |
| 7 | No despawn/respawn of the same `net_id` within 10 s while in interest | **PENDING (session 7)** | `[sync] ghosts ... respawned=N` plus the per-net `[sync] net=.. snaps=..` lines. The near-tier dead-band (`58d5c71`) and the 60 s evict grace are the mechanism |
| 8 | UDP realtime lane active in the mod | **PASS (CI), activation PENDING (session 7)** | The lane is implemented and unit-tested end to end (`UdpLane`, `Off -> Binding -> Active/Fallback`, 3 s handshake, automatic TCP fallback), and M4-25 puts every lane counter on the session line: `stats: ... udp=active udp_frames=OUT/IN udp_dgram=OUT/IN udp_loss=..% udp_binds=.. udp_fb=.. udp_reprobe=.. udp_bad=.. udp_dup=..`. `test_udp_lane.cpp` `[m425]` proves the line reports `udp=active` after a bind and `udp=fallback` after a 3.5 s blocked handshake. What is **not** proven: that the lane comes up on the real server from the real game - session 7 |

**Wave AC.** "A recorded session replays byte-identically" - **PASS (CI)**: `RecordingTests` re-encodes every recorded
message through `FrameCodec` and compares, and `x4mp-server record verify <dir>` does the same from the CLI. The mirror
run compares an authority recording against a replay recording with `RecordingDivergence.Compare` and reports
`divergence=0`.

---

## 3. What each wave built

| Wave | Tasks | Landed in |
|---|---|---|
| Plan | Eight waves, the eight open questions Q1-Q8 answered | `a88a330` |
| W1 | Capture set, incremental universe index, MD sector scan, dedupe test | `8a0a526`, `78da437`, `b24d894`, `d340347` |
| W2 | The authority streams the captured world: rotating slice, dead reckoning, keepalive, one `WorldUpdate` per 20 Hz tick | `4f45167`, `af7efe7` |
| W3 | The client half: budgeted ghost spawns, local NPC suppression, disconnect freeze | `6bda085`, `837f88d` |
| W4 | Manifest reader, station matching, container-hash replacement, manifest pruning, real manifest reporting | `8dd3bf4`, `ae28bd3` |
| W5 | Gate hint before arrival, spawn-near counter, near-tier hysteresis | `148318f`, `f8be507`, `58d5c71`, `4f99c5e` |
| W6 | UDP lane counters on the session line; `.x4rec` record, replay and mirror compare with the `record` CLI | `03170e6`, `7571a1f` |
| W7 | The 30 s budget line (M4-30); the attention-policy switch (M4-29) | `f92581c`, `a9080f6` |

---

## 4. Deliberate non-goals carried out of M4

These are recorded so that session 7 does not test for them:

- **Streamed ships are visual and positional only.** No NPC AI, combat, orders or economy replication; local brains stay
  suppressed; damage and kills are M5.
- **`activate` is not the default.** The attention A/B (`diag.ghost_attention_activate`) exists to be measured, and Q7 of
  the plan says the default stays `native` unless the measurement shows a client FPS cost that outweighs the behavioural
  risk. Until session 7 measures both arms, `native` ships.
- **The mirror is a library and a CLI, not a second server that joins as a node.** `record compare` compares two
  recordings; a second server consuming the authority's live stream is not built.
- **The recorder writes what `TrySend` accepted**, including frames later dropped by a lane watermark, so a recording is
  "what the server tried to send", not "what the client received".
- **No cross-sector pathfinding or traffic simulation on the client** (Q2: interest-only scanning, no full-galaxy sweep).
- **No save-format change**: the manifest stays a sidecar written at checkpoint time.

---

## 5. What is left before M4 can be called closed

1. Run [in-game-session-7.md](in-game-session-7.md): two clients, one authority, a busy sector of ≥ 200 ships, a highway
   transit, and both attention arms.
2. Fill rows 1-8 of section 2 with the quoted numbers.
3. Then, and only then, change the M4 verdict in [roadmap.md](roadmap.md) from "code complete, numbers pending" to closed.
