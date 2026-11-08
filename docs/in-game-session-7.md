# In-game test session 7 (M4 acceptance: authority world streaming, manifest matching, record/replay)

> **Status (2026-10-27): written from the code as committed at `a9080f6`, not yet run.** Everything named here exists in
> that build. M4 is code-complete with every CI gate green ([m4-exit-report.md](m4-exit-report.md) section 1); what is
> left is exactly what a machine cannot produce: the numbers. This document is the run sheet, and the exit report's
> section 2 is the table the numbers go into.
>
> **Status (2026-11-08): a two-instance stand was run instead, and it answers four of the ten criteria.** Six restart
> cycles of the stand (one PC, two X4 installs, relay on 47790, DLL `530E978EC7F589B3`, save
> `x4mp_ckpt_dc84b1b0a2e4d3e5`) produced `tracked=visible=spawned=119`, `authority_ms` p95 0.821 ms and
> `per_client=10993B/s` - see [m4-exit-report.md](m4-exit-report.md) section 2a for the log lines. Every `Record:` line
> below now carries a `Stand 2026-11-08:` note holding what that stand measured, or why it could not. Still owed to a
> real two-machine session: the station manifest report (7.2), a 200-ship sector (7.5), a highway transit (7.6), the
> attention A/B (7.8) and the server recording (7.9).

M4 is "authority world streaming, manifest matching, server interest/prefetch, ghost-only clients, record/replay"
([roadmap.md](roadmap.md) row M4, [m4-plan.md](m4-plan.md)). Eight waves landed; the plan's Q1-Q8 decided the shape.

| # | Acceptance criterion | Step |
|---|---|---|
| 1 | ≥ 99% station matches | 7.2 |
| 2 | Same NPC ships on client and authority, current and adjacent sectors | 7.3 |
| 3 | p95 error < 25 m within 5 km | 7.4 |
| 4 | Busy sector (≥ 200 ships): ≥ 45 FPS client, < 3 ms/frame authority mod cost | 7.5 |
| 5 | < 200 kB/s per client | 7.5 |
| 6 | Zero `spawn-near-player` pops on a highway transit | 7.6 |
| 7 | No despawn/respawn of the same `net_id` within 10 s while in interest | 7.7 |
| 8 | UDP realtime lane active in the mod | 7.1 |
| 9 | Attention policy A/B, both numbers recorded (M4-29) | 7.8 |
| 10 | Record, replay and mirror compare on a real session (M4-26..M4-28) | 7.9 |

## 7.0 Before you start (~20 min, both PCs)

1. **Back up** `Documents\Egosoft\X4\<id>\save\` on both PCs (pause OneDrive; Steam Cloud syncs it). Same X4 build and
   the same DLCs on both machines.
2. Steam launch options on both: `-debug all -logfile x4mp_s7.log`. Steam auto-update for X4 off.
3. Build and install on PC A: `powershell -ExecutionPolicy Bypass -File mod\build.ps1`, then
   `powershell -ExecutionPolicy Bypass -File tools\session4\install.ps1`. On PC B either the same two commands from a
   copy of the repo or the kit from `tools\session4\make-client-kit.ps1`.
4. X4 > Settings > Extensions on both: **Protected UI Mode OFF**, **x4native** and **X4 Multiplayer** on.
5. Start the server on PC A with recording on (this is what 7.9 reads):
   `x4mp-server.exe --data <dir>` with `X4MP__Net__RecordDirectory` set to a folder, or `Net:RecordDirectory` in the
   server config. The default part size is 64 MiB and parts are named `x4mp-rec-00001.x4rec`.
6. Host on PC A with your usual name, join on PC B. Both must be **Connected**.
7. Open the mod log on both PCs (it is also the file you send back at the end).

**Where the numbers are.** Every number in this document comes from one of five log lines. Nothing else needs to be
measured by hand:

| Line | Where | Carries |
|---|---|---|
| `budget: fps=.. frame_ms=P50/P95 mod_ms=P50/P95/MAX authority_ms=P50/P95/MAX clients=.. ghosts=.. suppressed=.. rx=..B/s tx=..B/s per_client=..B/s udp=..` | every 30 s | criteria 4, 5, 8, 9 |
| `[statics] manifest report <ckpt>: M/N stations matched ...` | once per manifest, per client | criterion 1 |
| `[sync] ghosts tracked=.. visible=.. spawned=.. respawned=.. hidden=.. removed=.. adopted=.. spawn_near=..` | every 5 s, client | criteria 2, 6, 7 |
| `[sync] net=<id> frames=.. err_p50/p95/max=.. m steady_p95=.. fast_p95=.. lat_p95=.. ms ..` | every 5 s, per ghost | criterion 3 |
| `stats: ... udp=active udp_frames=../.. udp_dgram=../.. udp_loss=..% udp_binds=.. udp_fb=..` | every 5 s | criterion 8 detail |

## 7.1 Baseline: the session line says what it is doing (~5 min)

Both nodes connected, both in the same sector, no other traffic.

- Wait 30 s. On **both** logs there must be a `budget:` line. On a client `authority_ms=0.000/0.000/0.000`; on the
  authority it must be non-zero once a sector is being streamed.
- Check `udp=` in that line and in the 5 s `stats:` line. Expect `udp=active` on both. If it says `udp=fallback`, the
  UDP lane did not come up: write down the address pair (NAT, VPN, Wi-Fi) - the lane is supposed to fall back silently,
  and criterion 8 then reads `PASS (fallback)` only with a note, not `active`.
- Write down `clients=` (should be 1 with two players) and `ghosts=` on the client.

Stand 2026-11-08: `udp=active` on both sides - guest `[perf] stats: ... udp=active udp_frames=1984/20002
udp_dgram=19245/20660 udp_loss=0.0% udp_binds=1 udp_fb=0 udp_reprobe=0` (06:48:52.230Z), and the authority's `budget:`
line of the same window also reads `udp=active`. `clients=1`, `ghosts=119`, `suppressed=1625`. The authority's cost
field is non-zero (`authority_ms=0.408/0.821/18.394`), the guest's is zero (`authority_ms=0.000/0.000/0.000`).

## 7.2 Station matches (criterion 1)

The client reads the checkpoint manifest at universe ready and matches its own stations to it.

1. Authority stores a checkpoint (the normal flow; the manifest is written next to it).
2. On the **client** log, find `[statics] manifest report`.
3. The number is `M/N`: `matched / stations`. Criterion 1 is `M/N >= 0.99`.
4. If it is below, send the per-sector lines that follow (`[statics] sector <macro>: ...`) and the
   `[statics] unmatched key '...'` lines - they name the key, which is (sector macro, object macro, 50 m bucket).

Record: `M/N = ______`, sectors `______`, unmatched `______`.

Stand 2026-11-08: **not produced.** The checkpoint flow skipped the manifest report on every cycle:
`[sess] universe ready: NodeReady sent (epoch c832087431fac984, manifest report skipped)` (also `dc7bc99fec93b571`,
`3540732393af0601`), and neither log holds a `[statics] manifest report` or `[statics] sector` line. The row needs the
real session.

## 7.3 The same NPC ships on both sides (criterion 2)

1. Authority sits in a sector with plenty of traffic. Client jumps into the **same** sector.
2. Let it settle 30 s. The client's `[sync] ghosts tracked=T visible=V` counts what the authority streamed.
3. Then the client jumps **one sector away** (an adjacent sector in the same cluster) and back. During the visit the
   tracked count must follow the authority's capture (Sector + Adjacent + Linger), not fall to zero.
4. Cross-check on the authority side: `x4mp-server record info <dir>` afterwards lists `EntitySpawn` / `WorldUpdate`
   counts, and the session's `capture:` counters show the sectors it was asked for.

Record: client `tracked=____ visible=____` in the shared sector; adjacent sector `tracked=____`; authority
`EntitySpawn=____`.

Stand 2026-11-08: client `tracked=119 visible=119 spawned=119` (`[sync] ghosts ... tracked=119 visible=119
spawned=119 respawned=0 hidden=0 removed=0 adopted=0`, 06:49:07.393Z); authority `spawns=387 updates=147455
sectors_complete=3` over the run, with the capture set reading `3 sectors [9@5Hz 36@1Hz 116@1Hz]`. No adjacent-sector
jump was made - both clients stayed put - so the "tracked follows the capture, does not fall to zero" half is **owed**.

## 7.4 Path error (criterion 3)

1. Fly with the other player's ghost in view for at least 2 minutes, including a stretch under 300 m/s and one above
   1000 m/s.
2. Read the per-ghost `[sync] net=<id> ... err_p95=.. m` lines. Criterion 3 is `err_p95 < 25 m` while the ghost is
   within 5 km.
3. Also read `steady_p95` (the M3 number, still expected < 10 m below 300 m/s) and `lat_p95` (expected <= 200 ms on a LAN).

Record: `err_p95 = ____ m`, `steady_p95 = ____ m`, `lat_p95 = ____ ms`, distance `____ m`.

Stand 2026-11-08: parked hulls `net=250/251` read `err 0.00/0.00/0.00 m`; `net=276 speed_max=857` reads
`err_p50/p95/max=92.43/194.10/278.26 m fast_p95=194.10 ex_p95=246 ex_n=25 lat_p95=789 ms held=0`; `net=294
speed_max=4567` reads `880.48/2075.93/2580.06 m fast_p95=2075.93 ex_p95=2078 ex_n=79 lat_p95=250 ms snaps=4 held=0`.
The error is 0.23-0.45 s of travel, i.e. a latency, and it is **not** zero in motion: the `< 25 m` half of the
criterion is unproven, and this line has no distance bucket, so `within 5 km` cannot be isolated from it. What the stand
does prove is that neither the engine guard, nor the extrapolation freeze, nor the encoder contributes any more
(section 2a of the exit report).

## 7.5 Busy sector: FPS, authority cost, bandwidth (criteria 4 and 5)

Pick a sector whose map shows **≥ 200 ships** (a shipyard or a core sector under a war). Verify the count from the
authority's own capture: the scan answer for that sector names the number of ships it pushed.

1. Both nodes in that sector, 5 minutes of flying (no menus, no SETA).
2. On the **client**: `budget: fps=..` must be `>= 45` and `frame_ms` should stay near the display's frame time.
3. On the **authority**: `budget: authority_ms=P50/P95/MAX` must have `P95 < 3.000`.
4. On the **client**: `per_client=..B/s` must be `< 200000`. Remember it is `tx / clients`, an upper bound: the server
   fans out by interest, so the real per-client figure is this or less. For a tighter number use 7.9's recording: total
   bytes over the session span divided by the span and the client count.

Record: `fps = ____`, `authority_ms P50/P95/MAX = ____/____/____`, `per_client = ____ B/s`, `tx = ____ B/s`,
sector ship count `____`.

Stand 2026-11-08: **the cost and bandwidth halves pass, the FPS half cannot be judged.** Authority
`mod_ms=0.583/1.151/18.526 authority_ms=0.408/0.821/18.394` against a 3 ms p95 allowance, at 119-132 tracked hulls
(06:48:49.887Z); the guest of the same window `mod_ms=2.478/6.233/10.768`. `tx=10993B/s per_client=10993B/s` (also
12 278 and 11 688 B/s), the guest `rx=3770B/s`. FPS is meaningless on this stand - two X4 processes share one GPU, so
the guest reports `fps=7.6 frame_ms=134.68/158.04` and the authority `fps=25.5 frame_ms=39.29/46.94` - and the busiest
sector held 119-132 hulls, not 200. Owed: a two-machine session in a shipyard or war-sector.

## 7.6 Highway transit: no pops (criterion 6)

1. Take a highway across a busy sector with ghosts in view (the client should be *moving*; a ghost that appears while
   the camera is still does not count as a pop, see m4-plan Q8).
2. After the transit read `[sync] ghosts ... spawn_near=N` on the client. Criterion 6 is `N == 0` for the whole transit.
3. If `N > 0`, the same 5 s lines around it plus the sector name are the bug report.

Record: `spawn_near = ____` before / during / after.

Stand 2026-11-08: **not produced, and not yet readable.** No highway transit was flown. Worse, the counter is the last
field of the `[sync] ghosts` line, which is cut at `log::kMaxLineChars = 240` (`core/log/log.h:50`): measured 272
characters total, 232 of message, ending mid-word after `spawn_failed=0`. The counter itself is unit-tested
(`mod/tests/ghosts/test_driver.cpp:880-889`). Splitting that line needs a matching change in
`tools\session4\sync-report.ps1`, which parses the field order - do that before the real session.

## 7.7 No churn while in interest (criterion 7)

1. Stay in one sector with the other player for 10 minutes; let one of you leave and come back once.
2. `[sync] ghosts ... respawned=N` must not increase for a `net_id` that stayed in interest the whole time. A
   `respawned` bump for the player who actually left and returned is expected.
3. The per-net `[sync] net=<id> snaps=.. extrap=.. held=..` lines show whether an individual ghost was snapped.

Record: `respawned = ____` over 10 minutes; any net_id that churned: `____`.

Stand 2026-11-08: `respawned=0 hidden=0 removed=0 adopted=0` in every 5 s line of a 20-minute session, with
`tracked == visible == spawned` (e.g. 06:49:07.393Z `tracked=119 visible=119 spawned=119 respawned=0`). The only
correction the ghosts needed was the interpolator's own blend, `snaps=4` on the 4567 m/s hull - not a respawn,
and not a new `net_id`.

## 7.8 Attention policy A/B (criterion 9, M4-29)

The switch is `diag.ghost_attention_activate` in `x4mp.json` (flat `"diag.ghost_attention_activate": true` also works).
The default (`false`) is the **`native`** arm: a streamed ship stays inert. `true` is the **`activate`** arm:
`ActivateObject(true)` right after `make_inert`, at the spawn and at every 5 s re-assert.

1. In the busy sector of 7.5, with the same save and the same spot, run 5 minutes with the switch **off**. Note the
   `budget: fps=` and `frame_ms` numbers and the `[sync] ghosts tracked=` count.
2. Restart X4, set the switch **on**, run the same 5 minutes. The log must contain the warning line
   `ghosts: diag.ghost_attention_activate is set: the attention policy is 'activate' ...` - if it is missing, the switch
   did not reach the driver and the run is invalid.
3. Both numbers go into the exit report. The default stays `native` unless the measurement shows a client FPS cost that
   outweighs the behavioural risk (m4-plan Q7).

Record: `native fps = ____` (`frame_ms p95 = ____`, `tracked = ____`) / `activate fps = ____` (`frame_ms p95 = ____`,
`tracked = ____`).

Stand 2026-11-08: **not run.** The A/B needs two comparable 5-minute runs in one busy sector with a restart between
them, and on this stand the FPS reading is dominated by the second client on the same GPU (`fps=7.6` for the guest,
`fps=25.5` for the authority), so the two arms would not be distinguishable. The switch and its warning line are
implemented and unit-tested; the default stays `native` until a two-machine measurement says otherwise.

## 7.9 Record, replay, mirror (criterion 10)

With `Net:RecordDirectory` set, the server recorded the whole session.

1. `x4mp-server.exe record info <dir>` - prints `parts=N bytes=.. protocol=0x.. session=0x..` and the message counts.
2. `x4mp-server.exe record verify <dir>` - re-encodes every message and replays the file; it must print
   `ok messages=.. ... reencoded=..`. A decode error fails here.
3. For the mirror arm: replay the authority's recording into a second recording and compare:
   `x4mp-server.exe record compare <authority dir> <mirror dir> --streams`. Expect `divergence=0`. Without `--streams`
   the stream id is not compared (the replay driver sends through one node connection, so the ids differ by design).

Record: `parts = ____`, `messages = ____`, `verify = ____`, `divergence = ____`.

Stand 2026-11-08: **not produced.** The relay ran without `Net:RecordDirectory` (no recording exists for the run), so
`record info` / `record verify` / `record compare` had nothing to read. The wave's own criterion is already covered by
CI - `RecordingTests` re-encodes every recorded message and compares, and the ten-minute synthetic session plays back
with no decode error - but criterion 10 stays open until a live session is recorded.

## 7.10 What to send back

1. Both `x4mp_s7.log` files (client and authority).
2. The server log.
3. The recording directory (or at least the `record info` / `record verify` output).
4. Your notes for 7.2-7.9, even where a number is missing - a missing line is itself a finding.

The exit report's section 2 is filled from exactly these, and every row quotes the line it came from.
