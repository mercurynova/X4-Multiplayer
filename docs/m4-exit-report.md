# M4 exit report

Date: 2026-10-27; two-instance stand evidence added 2026-11-08 (section 2a). Criteria: [roadmap.md](roadmap.md) M4 row and [m4-plan.md](m4-plan.md) section 3. Plan of record:
[m4-plan.md](m4-plan.md) (eight waves, M4-01..M4-31). In-game evidence: [in-game-session-7.md](in-game-session-7.md).

**Read this first.** M4 is code-complete and every CI gate is green. On 2026-11-08 the milestone's in-game numbers were
measured for the first time, but not in the shape the wave asked for: the run was the **two-instance stand** - two X4
installs on one PC, one relay, one authority, one guest - not a session with a 200-ship sector, a highway transit and a
scripted attention A/B. This report therefore has four kinds of rows: **PASS (CI)** where the claim is a test result,
**PASS (stand)** where a log line from the stand proves it, **PARTIAL (stand)** where the stand measured only part of the
claim, and **PENDING (session 7)** where the number still needs the real run. The wave acceptance clause "every roadmap
M4 acceptance number is quoted from a log, not asserted" is **met for rows 2, 5, 7 and 8** and **not met** for the rest;
each pending row still names the exact log line its number will come from.

The stand run is described in section 2a: what it was, what it produced, and the three things it deliberately could not
produce (a station manifest report, a 200-ship sector, a highway transit).

---

## 1. Gates

| Gate | Result |
|---|---|
| Mod build, `relwithdebinfo` | exit 0, no compiler errors |
| `ctest` (full, from `mod/build/msvc-x64-relwithdebinfo`) | **731 tests: 727 passed, 0 failed, 4 skipped**, 199.26 s (2026-11-08, with both stand clients running). The four that did not run are `710 smoke.netprobe_real_server`, `711 smoke.headless_real_server`, `722 live.authority_upload_kill`, `731 smoke.headless_udp_real_server` - all need a live server or a game. `714 guard.installer` passed in this run (it fails when an X4 client holds the loader DLL open; it is the one test that cannot pass mid-session) |
| `x4mp_host_tests.exe` (whole binary) | 321 test cases, 13 405 assertions |
| Lua (`python mod/tests/lua/run_lupa.py`) | 252 passed, 0 failed |
| .NET `X4MP.Core.Tests` | 1378 / 1378 |
| .NET `X4MP.Server.Tests` | 597 passed, 1 skipped. One timing test is a known flake and passes alone: `Saves.SaveLatencyTests.ATransferRaisesAnInGameNodesP99LatencyByNoMoreThan10Ms` (p99 rise 19.7 ms against a 10 ms allowance) |
| `X4MP.Server.Tests` recording suite (M4-26..M4-28) | 14 / 14, including a ten-minute synthetic session that records and reads back with no decode error, and the `record mirror` CLI arm |

---

## 2. Acceptance criteria

| # | Criterion (roadmap M4) | Verdict | Evidence |
|---|---|---|---|
| 1 | ≥ 99% station matches | **PASS (stand, 2026-11-09)** | Implementation: `8dd3bf4`. The number is the first field pair of `[statics] manifest report <ckpt>: M/N stations matched over S sector(s) (U unmatched, A ambiguous, I by id code, E same UniverseID, R manifest rows, P pruned, T ms)`, also forwarded to the server log by `diag_hub().forward_log`. Per-sector detail follows as `[statics] sector <macro>: N stations, M matched, X missed, A ambiguous`. **On 2026-11-09 the two-client stand produced the number.** Authority checkpoint 2: `2026-11-09 06:20:10.822Z authority: manifest lists 1316 station(s) over 152 sector(s)`, census `06:20:14.595Z statics: station census closed: 1537 station row(s) over 152 sector(s) (0 sector(s) unanswered)`. The guest downloaded `x4mp_995ce7b24df9.x4mf` (`statics: manifest 400aac0e596bca1eaf287d2efe2076ca loaded ...: 1317 rows indexed of 1317 entries (0 without a macro, 0 without a sector, 0 collisions)`) and reported `2026-11-09 06:22:53.197Z [statics] manifest report 400aac0e596bca1eaf287d2efe2076ca: 40/40 stations matched over 3 sector(s) (0 unmatched, 0 ambiguous, 0 by id code, 0 same UniverseID, 1317 manifest rows, 0 pruned, 105 ms)` - 100%, also forwarded to the server log. Per-sector: sector 9 12/12, sector 36 22/22, sector 116 6/6. The earlier 06:14 report on checkpoint 1 read 34/40 only because that manifest was encoded with 86 of 152 sectors committed (the six stations of sector 116 were outside it). Two defects had to be fixed first: the Lua bridge verb allowlist lacked `statics_scan` (`mod/extension/x4mp/ui/x4mp_bridge.lua:78`), so `B.send` dropped every MD reply as `unknown_verb`; and `StaticsFeature::pump` fed the bridge JSON envelope to the bare-batch parser (`mod/native/features/statics/statics_feature.cpp`), so no scan was ever parsed (the same bug the capture scan fixed in `ghost_feature.cpp:240-246`; fixed here with `auth::scan_payload`). The 2026-11-08 session's `manifest report skipped` was these two defects, not a matching failure |
| 2 | Same NPC ships on client and authority in current and adjacent sectors | **PASS (stand)** | Client side, guest 5 s line 2026-11-08 06:49:07.393Z: `[sync] ghosts tracked=119 visible=119 spawned=119 respawned=0 hidden=0 removed=0 adopted=0 hints=263248 rep_msgs=19276 rep_entries=126700 frame_p95_us=3686 frame_max_us=5747 rep_bad=0 spawn_failed=0` - `tracked == visible == spawned` for every line of the run. Authority side, 06:37:35.692Z `[save] [sync] stream: spawns=387 updates=147455 polls=532247 fast_polls=51968 skipped_no_macro=0 skipped_no_pose=98051 skipped_far=0 encode_failed=0 velocity_clamped=183 send_failed=0 sectors_complete=3`. The stand's CaptureSet was `3 sectors [9@5Hz 36@1Hz 116@1Hz], 1 focus [9 r=18750m@20Hz]`, i.e. one current sector and two adjacent ones exactly as the criterion asks; 387 spawns and 147 455 updates reached the guest with no bad frame (`rep_bad=0`) |
| 3 | p95 error < 25 m within 5 km | **PARTIAL (stand)** | `[sync] net=<id> frames=N err_p50/p95/max=.. m steady_p95=.. fast_p95=.. ex_p95=.. ex_n=.. lat_p95=.. ms speed_max=.. snaps=.. extrap=.. held=..` - the path error is rendered position vs the sender's own samples at the same server time (M3 Q3 measurement, unchanged in M4). Stand, 2026-11-08 06:37:39.891Z: `net=250/251` (parked) `err 0.00/0.00/0.00 m`; `net=276 frames=104 err_p50/p95/max=92.43/194.10/278.26 m fast_p95=194.10 ex_p95=246 ex_n=25 lat_p95=789 ms speed_max=857`; `net=294 err_p50/p95/max=880.48/2075.93/2580.06 m fast_p95=2075.93 ex_p95=2078 ex_n=79 lat_p95=250 ms speed_max=4567 held=0`. So the error is **zero when the hull is not moving and equals about 0.4 s of travel when it is** (`err / speed_max` = 0.23/0.42/0.45 s for 857/3497/4567 m/s). The criterion's "within 5 km" band is not isolated by this line - it is per ghost, not per distance bucket - so the sub-25 m claim is **unproven** for hulls in motion; what the stand proves is that the guard, the freeze and the encoder no longer contribute to it (section 2a). **2026-11-11 distance-bucketed update:** the new `[sync] net=<id> near n=.. p50/p95/max=.. m` line scores only fresh poses within 5 km and logs held/long-extrapolation poses on a separate `stale` line. One-PC stand native arm: 1680 windows over 20 nets, **91.7% of 5 s windows < 25 m**, aggregate p95 **30.84 m**, every net's median window < 25 m, max 90.40 m; the residual >25 m windows are extrapolation of steady hulls in the adjacent 1 Hz sectors, not interpolation error. The attention `activate` arm was worse: 84.9% < 25 m, p95 86.65 m, max 126.33 m, `mod_p95` 12.98 ms, `spawn_failed=209` (native `mod_p95` ~0.7 ms, `spawn_failed=0`). The strict target is therefore not met for all hulls on this stand; it is met for every net's median and 91.7% of windows, with the remainder the 1 Hz adjacent-sector extrapolation. A two-machine session 7 is still needed for rows 4 and 6. |
| 4 | Busy sector (≥ 200 ships): ≥ 45 FPS client, < 3 ms/frame authority mod cost | **PARTIAL (stand)** | `budget: fps=.. frame_ms=P50/P95 mod_ms=P50/P95/MAX authority_ms=P50/P95/MAX clients=.. ghosts=.. suppressed=.. rx=..B/s tx=..B/s per_client=..B/s udp=..` every 30 s. `authority_ms` is `AuthorityFlow::step` timed through `stats::timing_hub()`, which is the authority-only cost the whole-mod `mod_ms` cannot isolate (M4-30). **The cost half passes:** authority 2026-11-08 06:48:49.887Z `mod_ms=0.583/1.151/18.526 authority_ms=0.408/0.821/18.394 clients=1 ghosts=0`, and the guest of the same window `mod_ms=2.478/6.233/10.768` - p95 0.821 ms of authority time against a 3 ms allowance at 119-132 streamed hulls. **The FPS half cannot be judged here:** the two clients share one GPU, so the guest reports `fps=7.6 frame_ms=134.68/158.04` and the authority `fps=25.5 frame_ms=39.29/46.94`; and the busiest sector held 119-132 tracked hulls, not 200. This row needs the real session 7 on two machines |
| 5 | < 200 kB/s per client | **PASS (stand)** | Same `budget:` line. `per_client` is `tx / clients` and is an **upper bound**: the server fans out by interest, so a real client pulls this or less. Stand, authority 2026-11-08 06:48:49.887Z: `tx=10993B/s per_client=10993B/s` (11 kB/s against the 200 kB/s allowance; 06:47:49.812Z `12278B/s`, 06:48:19.853Z `11688B/s`). The guest's own line reads `rx=3770B/s tx=743B/s per_client=743B/s`, i.e. the sender side of the same session. A tighter number still comes from the recording (`x4mp-server record info` totals over the session span) |
| 6 | Zero `spawn-near-player` pops on a highway transit | **PENDING (session 7, transit only)** | The counter is `M4-23` (`f8be507`), unit-tested at `mod/tests/ghosts/test_driver.cpp:880-889` (`CHECK(r.driver->counters().spawn_near == 1)`). **It is readable in a live log now**: the `[sync] ghosts` line was split into `[sync] ghosts tracked=.. visible=.. spawned=.. respawned=.. hidden=.. removed=.. adopted=.. hints=.. rep_msgs=.. rep_entries=.. frame_p95_us=.. frame_max_us=.. clk_ms=.. bias_ms=..` plus `[sync] ghosts health: rep_bad=.. spawn_failed=.. unmapped=.. unresolved=.. spawn_near=..`, because the single line was longer than `log::kMaxLineChars = 240` (`core/log/log.h:50`) and was cut mid-word after `spawn_failed=0` (measured 2026-11-08: 272 characters total, 232 of message). `tools\session4\sync-report.ps1` reads the health line and prints a `spawn-near-player pops, mod lifetime total` row with limit `= 0 in transit`: `spawn_near=0` gives PASS, any non-zero value gives WARN with the distance band (a synthetic log with `spawn_near=3` prints `3 (detail lines: 3, 623-4081 m)`). **The counter did move on the stand, at join**: 92 `spawn-near-player:` lines in two stream-start batches (`06:00:52` 52 lines, `06:31:36` 40 lines), distances **623-4081 m** (`net=238 sector=9 distance=1102 m`, `net=174 sector=9 distance=4081 m`). A join spawns the sector at once and the spawner takes the nearest first (`max_spawns_per_frame = 40`, `ghost_driver.h:138`), so the first frame creates the 40 closest hulls - a real pop on arrival, outside the criterion's transit scope. What is still owed is `N` across a highway transit itself |
| 7 | No despawn/respawn of the same `net_id` within 10 s while in interest | **PASS (stand)** | `[sync] ghosts ... respawned=N` plus the per-net `[sync] net=.. snaps=..` lines. The near-tier dead-band (`58d5c71`) and the 60 s evict grace are the mechanism. Stand, every 5 s line of rounds 4-6: `respawned=0 hidden=0 removed=0 adopted=0` with `tracked == visible == spawned` (e.g. 06:49:07.393Z `tracked=119 visible=119 spawned=119 respawned=0 hidden=0 removed=0 adopted=0`). The only correction the ghosts needed was the interpolator's own blend, visible as `snaps=4` on one 4567 m/s hull in `net=294 ... snaps=4`, which is not a respawn |
| 8 | UDP realtime lane active in the mod | **PASS (CI and live stand)** | The lane is implemented and unit-tested end to end (`UdpLane`, `Off -> Binding -> Active/Fallback`, 3 s handshake, automatic TCP fallback), and M4-25 puts every lane counter on the session line: `stats: ... udp=active udp_frames=OUT/IN udp_dgram=OUT/IN udp_loss=..% udp_binds=.. udp_fb=.. udp_reprobe=.. udp_bad=.. udp_dup=..`. `test_udp_lane.cpp` `[m425]` proves the line reports `udp=active` after a bind and `udp=fallback` after a 3.5 s blocked handshake. **And it comes up on the real server from the real game:** guest 2026-11-08 06:48:52.230Z `[perf] stats: fps=7.7 frame_p95=143.07ms mod_p95=4.397ms mod_max=5.171ms connected=1 rtt=0.6ms rx=6629B/s tx=696B/s node_stats_sent=594 udp=active udp_frames=1984/20002 udp_dgram=19245/20660 udp_loss=0.0% udp_binds=1 udp_fb=0 udp_reprobe=0` - one bind, no fallback, no reprobe, and the authority's `budget:` line of the same window also reads `udp=active` |

**Wave AC.** "A recorded session replays byte-identically" - **PASS (CI and live stand)**: `RecordingTests` re-encodes
every recorded message through `FrameCodec` and compares, and `x4mp-server record verify <dir>` does the same from the
CLI. The mirror run compares an authority recording against a replay recording with `RecordingDivergence.Compare` and
reports `divergence=0`. **On the stand (2026-11-09) a live session was recorded and read back:** 15 845 messages over
1 177.3 s, `record verify` -> `ok ... reencoded=15845 mode=as-fast-as-possible speed=1 elapsed_ms=47`,
`record mirror` -> `ok mirrored=15845 reencoded=15845`, `record compare` -> `divergence=0 authority=15845
mirror=15845` (one half-written record at the tail was trimmed before reading because the relay was force-stopped; see
[in-game-session-7.md](in-game-session-7.md) 7.9).

---

## 2a. The 2026-11-08 two-instance stand run

The numbers marked `(stand)` above come from one PC running two full X4 installs against one relay, not from session 7.
What was running: `D:\Steam\steamapps\common\X4 Foundations` as the authority (`X4.exe`) and `D:\X4MP-stand\x4b` as the
guest (`X4b.exe`, the patched stand binary - the stock one dies on the single-instance mutex), one `x4mp-server.exe` on
port 47790 with nodes on 47780, both installs on mod DLL `530E978EC7F589B3`, session save `x4mp_ckpt_dc84b1b0a2e4d3e5`,
`tracked=visible=spawned=119` ghosts and a `[perf]` line every 30 s on each side.

Timeline of one cycle (three measured rounds agree): relay and both clients up -> the mod ports 7779 (authority MCP) and
7780 (guest MCP) answer after 2-3 min -> `load_game latest` -> `universe ready` 3 min later -> join -> 3-7.5 min until the
first `[sync] ghosts` line, because the join itself reloads the UI once (documented in
`mod/docs/two-instances-one-pc.md` section 11a).

What the stand produced, per row of section 2: rows 2, 5, 7 and 8 have a number; rows 3 and 4 are partial (a per-ghost
error line with no distance buckets, and no second machine for an honest FPS figure); rows 1 and 6 have none.

What it cannot produce, by construction:

- **A station manifest report.** The session is loaded from a save on both sides, and that ready path takes the
  "manifest report skipped" branch, so row 1 has no `[statics]` line to quote.
- **A 200-ship sector.** The busiest captured sector held 119-132 tracked hulls; `polls=532247` over the run proves the
  sampling, not the load.
- **A highway transit.** No client flew a superhighway, so the zero-pops criterion itself (`N` across the transit) has no value,
  and the attention A/B of M4-29 was not repeated on the stand. The counter itself did move: 92 pops at 623-4081 m on the two
  joins the log covers (see row 6).
- **A trustworthy FPS.** Two X4 processes share one GPU: the guest reported `fps=7.6 frame_ms=134.68/158.04` while the
  authority reported `fps=25.5`. Only `mod_ms` / `authority_ms` / `per_client` mean anything there.

What it did buy: three defects the CI suite could not see, each found from a counter and fixed with a test.
`velocity_clamped` (`09e2413`) - a finite-difference velocity beyond the wire's range used to drop the whole update; the
pose now goes out with zero velocity and the clamp is counted. `fast_polls` (`935c41e`) - a hull in a 1 Hz sector was read
about once a second, so a 4 km/s ship was a 4 km extrapolation; a track faster than 300 m/s is now polled every 0.15 s
whatever its tier says. The ghost extrapolation cap (`a7a5cc1`) - the streamer's own keepalive leaves a correct hull with
no updates for a second, which the 500 ms default cap turned into a frozen ghost; ghosts now dead-reckon for 6 s while
avatars keep 500 ms.

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
  risk. The one-PC stand measured both arms on 2026-11-11: native 91.7% of 5 s near windows < 25 m (aggregate p95 30.84 m, `mod_p95` ~0.7 ms, `spawn_failed=0`) vs activate 84.9% (p95 86.65 m, `mod_p95` 12.98 ms, `spawn_failed=209`). `native` wins on path error, frame cost and spawn success, so it remains the default.
- **The mirror is a library and a CLI, not a second server that joins as a node.** `record compare` compares two
  recordings; a second server consuming the authority's live stream is not built.
- **The recorder writes what `TrySend` accepted**, including frames later dropped by a lane watermark, so a recording is
  "what the server tried to send", not "what the client received".
- **No cross-sector pathfinding or traffic simulation on the client** (Q2: interest-only scanning, no full-galaxy sweep).
- **No save-format change**: the manifest stays a sidecar written at checkpoint time.

---

## 5. What is left before M4 can be called closed

1. ~~Split the `[sync] ghosts` line so `spawn_near` and the fields behind it survive `log::kMaxLineChars`~~ **Done**: the
   5 s report is two lines now, `[sync] ghosts tracked=.. visible=.. ... bias_ms=..` (~172 chars) and
   `[sync] ghosts health: rep_bad=.. spawn_failed=.. unmapped=.. unresolved=.. spawn_near=..` (~86 chars), and
   `tools\session4\sync-report.ps1` reads the health line and prints a `spawn-near-player pops` row with limit `= 0 in transit`
   plus the distance band of the detail lines. A synthetic log with `spawn_near=0` gives PASS, with `spawn_near=3` gives WARN
   (`3 (detail lines: 3, 623-4081 m)`): the criterion is scoped to the transit, and a join legitimately spawns the sector at once.
2. Run [in-game-session-7.md](in-game-session-7.md) on two machines: two clients, one authority, a busy sector of
   >= 200 ships, a highway transit, and both attention arms. That run fills row 1 (station manifest), row 3 (the sub-25 m
   claim inside 5 km), row 4 (client FPS on a second machine) and row 6 (`spawn_near` across a transit), and replaces the
   stand's `(stand)` verdicts with session numbers.
3. Then, and only then, change the M4 verdict in [roadmap.md](roadmap.md) from "code complete, numbers pending" to closed.

The 2026-11-08 stand (section 2a) closes neither of the two measurements left by itself, but it removed the obstacles that would
have made session 7 confusing: the encoder no longer reports a protocol range as a failure, a fast hull is no longer sampled
once a second, a ghost no longer freezes between keepalives, and the criterion-6 counter is readable in a live log. Two gaps
stay open by design and should be called out in session 7 instead of being rediscovered there: `skipped_far` has never been
non-zero (no save on the stand held a hull beyond the wire's 33 554 km), and the residual fast-hull error is a latency of
0.23-0.45 s of travel, not a stall.
