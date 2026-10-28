# X4MP backlog (the single index of open work)

Started 2026-10-20 (user decision). **One line per item**; details live in the linked results / plan docs. The lead updates this
file at the end of every major step (docs/execution-plan.md §3.2) and walks it for every milestone exit report.

- **Target:** `M3 exit` (must be settled before M3 closes), `M4`, `M5`, `M6`, `later`, `wontfix`.
- **Status:** `open`, `fix in, in-game check pending`, `decision needed`, `deferred`, `done <date>` (done items move to the log at the bottom).
- Evidence docs: S1 = [sitting 1](spikes/session-4-sitting-1-results.md), S2 = [sitting 2](spikes/session-4-sitting-2-results.md),
  S3 = [sitting 3](spikes/session-4-sitting-3-results.md), R28 = [M3-28 + two-PC runs](spikes/session-4-m3-28-run-results.md),
  RI = [internet run](spikes/session-4-internet-run-results.md), R1028 = [2026-10-28 M3-34 + F1 run](spikes/session-4-2026-10-28-results.md),
  plan = [m3-plan.md §8](m3-plan.md).

## Reminders for the user (the lead brings these up)

| When | Item |
|---|---|
| Every major step / milestone planning | The **deferred majors** below (D1, D2): say whether their trigger has come |
| Before the next two-PC session | Sync both PC clocks (Windows time sync or `w32tm /resync`, elevated); check OBS writes a file on both PCs |
| Any other PC that builds the server | Needs .NET SDK **10.0.402** (`winget upgrade Microsoft.DotNet.SDK.10`): the SDK move is on main since 2026-10-20 (PR #17) |

## Deferred majors (dependency upgrades we chose to wait on)

Each one has an `ignore` entry for its major version in `.github/dependabot.yml`, so Dependabot does not reopen it; the entry
is removed when the work starts. **Trigger** = when we pick it up; **ready-to-go** = what the Sonnet brief will say.

| # | Upgrade | Was | Why deferred | Trigger | Ready-to-go notes |
|---|---|---|---|---|---|
| D1 | **TypeScript 6 -> 7** (`server/web`) | PR #6, CI red | Compiler major; a planned task, not a bump | Next web-GUI work (the player portal ADR-050 or M6 packaging), or when TS 6 stops getting fixes / a dependency needs 7 | Read the TS 7 release notes (breaking changes), run `npm run build` + Playwright e2e, fix type errors; one Sonnet task in `server/web` |
| D2 | **xunit.runner.visualstudio 3 -> 4** (test projects) | fixed (PR pending): 4.0.1; PR #13 was red only from NU1903 (SignalR.Common 10.0.12 advisory) | Test-runner major; may need test-project changes | Next .NET tooling pass (with the SDK move follow-ups), or when 3.x stops working with a new SDK / VS | Check the v4 migration notes (xunit v3 runner requirements), update `Directory.Packages.props`, regenerate lock files with a plain restore, full `dotnet test`; one Sonnet task |

## Open items

### Session 4 / M3 (in-game)

| ID | Item | Evidence | Target | Status |
|---|---|---|---|---|
| F21 | Markers flash around a superhighway transit | R28, plan M3-32/33 | M3 exit | fix in (M3-33), in-game check pending |
| F21b | Local-highway ride: the avatar's map icon freezes at the entry (hidden + hold at 1 Hz) and jumps at the exit; fix: in-sector highway keeps the true pose, hide/hold for superhighways only | R1028 | M3 exit | fix in (tracker: `Hidden` only for docked and the superhighway signal, a local highway keeps the true pose at the moving rate with `InHighway`), in-game check pending ("M3-34 check" step 4b in in-game-session-4.md) |
| F23 | Map icon bounces along the travel line in fast in-sector flight; engine moves our inert objects (286 km) | RI, R1028 | M3 exit | fast flight smooth in game 2026-10-28 (M3-34 hint cap + guard work); the remaining case is F23c |
| F23b | Target info shows 0 m/s for a remote ship above 1000 m/s (M3-34 velocity-hint cap) | plan M3-34, R1028 | M3 exit | decision needed: `diag.hints_off` removed the brake jumps (F23c); proposal is hints off as the default and accepting 0 m/s |
| F23c | **Map icon jumps while the remote ship brakes hard from travel speed (3D ghost steady).** Engine guard: 206 hits on Bob's ghost of Alice in one braking window (90 km displacements), 347 on Alice's avatar of Bob (66-177 km); `hints_off`: 0 hits, smooth. Mechanism of the km-scale displacement unknown; fix: hints off by default or no hint while the speed changes fast, extend the guard line (sector position / edge distance) | R1028 | M3 exit | **fix in (PR #38), in-game check pending** (no hint while decelerating > 30 m/s^2 or right after an engine-guard hit; in-game-session-4.md M3-34 step 2b) |
| X1 | 30-min flight over >= 5 sectors incl. a highway (M3 criterion; sittings reached ~25 min) | S3 | M3 exit | open |
| X2 | M3 exit report | roadmap | M3 exit | open (after the checks above) |
| F1 | Exit to the main menu keeps the session "in game" | S1, R1028 | M3 exit | decided (ADR-052), fix in (PR #24). **Client side PASS in game 2026-10-28** (30.0 s, parked avatar, normal rejoin, fast return / `/reloadui` / in-session Load never leave); the **authority path is untested** (blocked by F1b/F1c) |
| F1b | The menu-exit watch survives a manual Disconnect and cancels the next Join click (`JoinFeature::stop_session` does not disarm `menu_watch_` / clear `undecided_`) | R1028 | M3 exit | **fix in (PR #38), in-game check pending** (in-game-session-4.md F1 step 1b) |
| F1c | **An authority that rejoins from the main menu is never sent the start save and sticks at "checking the session save"**: the stash still claims the old save as loaded (`stored_loaded_sha`), the server trusts it (`SendStartSaveInfo`), the mod's own fallback needs a universe. Needed an X4 restart | R1028 | M3 exit | **fix in (PR #38), in-game check pending** (mod: the start menu voids the stashed loaded-save claim; in-game-session-4.md F1 step 1c) |
| F1d | The authority at the main menu starts the world-clock keepalive with `game_time 2.136`; Bob's ghost stamps were ~50 s off for 40 s | R1028 | M3 exit | **fix in, in-game check pending** (mod: the keepalive needs `universe_ready` and no start menu; server: a backward game-time jump over 5 s with a continuing tick is counted and logged but still applied, a legitimate in-place authority load looks the same; in-game-session-4.md F1 step 1d) |
| F9 | Ghost display delay sits at the 250 ms cap (latency p95 > 200 ms target) | S1, S3, R1028 | M4 | **measured 2026-10-28: still at the cap** (moving-ghost lat p95 median 250 ms, delay at 250 in 38 of 84 windows, rtt 13 ms) -> accepted for M3 (ADR-052); < 200 ms is an M4 item, expected to follow F9b |
| F9b | **Player-ship samples get the wrong time stamp:** `WorldMirror.ApplyPlayerState` stamps a player sample with the latest WorldUpdate's game time, not the sample's own time (20 Hz samples come out 4-400 ms apart, ~1.4% duplicates). The ghost gets the right speed on a jagged time axis: ~5-10 m path error, and the delay controller reads the noise as jitter (likely the cause of F9). Fix: map the sender's `SampleTimeUs` (server-clock estimate) into game time; FakeNode bots must send a server-clock estimate too; regression test for even, monotonic stamps. Experiment (arrival-time stamps) halved the error | HostSimM3 A/B 2026-10-21 (14 runs, not an M3-35/36 regression) | M3 exit | **fix in, in-game check pending** (branch `fix/f9b-sample-timestamps`, 2026-10-28; design in m3-plan.md perf-numbers paragraph; check: display latency p95 below the 250 ms cap) |
| F6 | Mod main-thread p95 0.27 ms with 8 ghosts (target < 0.2 ms) | S1 | M4 | open (perf pass) |
| F8 | FPS 100 -> 70 with 8 ghosts nearby | S1 | M4 | open |
| F13 | `AuthorityGraceSeconds` default 120 s ends a session before a real X4 restart is back; an Ended session cannot be resumed | S2 | M3 exit | decided (ADR-052): default ~600 s (GUI setting) + admin resume of an Ended session from its last checkpoint; fix in (PR #25), in-game check pending (in-game-session-4.md "F13 authority grace + resume") |
| F24 | **No checkpoint for 14 min around the re-host**: autosave is 15 min after the last checkpoint (`AutosaveMinutes`), nothing checkpoints when the authority leaves; Alice lost 8.5 min of play. Options: lower the default (5 min?), GUI "last checkpoint N min ago", "Request save now" before a planned re-host | R1028 | M3 exit | decided (user 2026-10-28, ADR-054): checkpoint before a planned authority leave (Disconnect, Esc > Exit / Quit); **fix in, in-game check pending** ("F24 save on planned leave" in in-game-session-4.md). Lowering the 15 min default and a GUI "last checkpoint N min ago" stay open ideas |
| F25 | `ManifestReport` sent in a denied phase after a save-load resume (server WRN `PhaseDenied`, `JoinFeature::finish_ready` waits only while `my_phase_ < 5`) | R1028 | M4 | **fix in, in-game check pending** (cause: the server never moves a node back from InGame by LoadStatus, so the report can never be legal there; the mod now skips the count-only report when the roster shows it InGame; in-game-session-4.md F1 step 1e) |
| F26 | Review fixes 2026-10-28 (lead review of PRs 37-41): (1,2) a player's sample clock froze after the authority's game time went back and gave equal stamps at the ceiling during a game-time stall: stamps are now strictly increasing and at most game time + 0.6 s (`PlayerSampleClock.TryStamp`); while game time is frozen the excess samples are dropped, counted in `WorldMirror.DroppedPlayerSamples`; (6) the mirror now treats a big backward jump / a reattached authority as an epoch change (player sample clocks restart, entity sample times are pulled back to the new game time); (3) a held Esc > Exit / Quit leave is released (x4mp.leave_go) when the session ends by another path; (4) a planned leave is answered only by its own checkpoint request; (5) hostsim coverage of the F1d clock gate and the F25 skipped ManifestReport | review | M3 exit | **fix in, in-game check pending** (in-game-session-4.md F13 step 3b and F24 step 8) |
| F13b | A rollback to a checkpoint (F13 resume, M3-21 authority reload) did not roll the server-owned ledger back: spend after the checkpoint lost ships and credits, income survived | user 2026-10-21 | M3 exit | decided (ADR-053), fix in (PR #28): ledger sequence mark per checkpoint, compensating entries, loans/trades/layout restored, admin actions kept, notice to players; in-game check pending (in-game-session-4.md "F13 authority grace + resume" step 9) |
| F14 | A GUI setting change did not survive a server restart (not reproduced in S3) | S2, S3 | later | open, watch |
| F19 | Vanilla `md.PlayerReputation` errors / "Prized Investor" notification on team factions | S3 | M5 | open, minor |
| A1 | Avatars report `active=1` although the mod writes `ActivateObject(false)` | R28 | M4 | open |
| A2 | M3-30 created ship has the starter loadout, not the returning player's upgrades | plan M3-30 | M5 | open |
| A3 | Wingman bots copy the leader's heading instead of facing their direction of travel | S2 | later | open (FakeWingman) |
| A4 | `[MP]` avatars stay in an exported checkpoint with owner "???" when loaded without the mod (V21b) | S2 | M6 | decided (ADR-052): strip `[MP]` objects on single-player export |
| L1 | `GetObjectPositionInSector(): Failed to retrieve sector` log noise during gate / highway transits | S3 | M4 | open, minor |
| N1 | Starter macro `ship_arg_s_fighter_01_a_macro` shows as Nova Vanguard; docs call it the Argon Elite | S2 | later | open (doc check) |

### Kit, tools, GUI

| ID | Item | Evidence | Target | Status |
|---|---|---|---|---|
| K1 | `sync-report.ps1` judges the whole mod log, should judge one run (2026-10-28: the "worst" 37081 ms / 868 m / 1221 m came from 2026-10-07 lines still in the log; the one "pop" was a respawn after an in-session Load) | S2, R1028 | M4 | open |
| K2 | Kit / publish should rebuild (or check) the web GUI build | S1 | M4 | open |
| K3 | GUI "Total playtime 0s" | S3, RI | later | open |
| K5 | GUI shows the player offline for ~1 s on every reload / save load (`ClientReload` detach + resume ~0.3 s) | R1028 | later | open, cosmetic |
| K6 | Script text: M3-34 step 2 expects `newest_age_ms` < 1 s, but a standing ship is replicated about once per 5 s (age 2-4 s, `lat_p95` 4.4 s, not a fault); the report counts a ghost respawn after a save load as a "pop" | R1028 | M3 exit | open (script text) |
| K4 | Firewall disable test: disabling the allow rule does not cut a live UDP flow; script 3.5 should use a block rule | RI | M3 exit | open (script text) |

### Engineering

| ID | Item | Evidence | Target | Status |
|---|---|---|---|---|
| E6 | **Node 20 deprecation** warnings in CI: `actions/cache@v4`, `actions/upload-artifact@v4`, `actions/download-artifact@v4`, `github/codeql-action@v3` target Node 20 (our `setup-node` is already 24). One Sonnet PR bumps all four majors together (upload + download artifact must match), release notes checked, full CI incl. e2e-headless; supersedes #8, #10, #16 | CI annotations (2026-10-20) | after E3 | fixed (PR #40, 2026-10-28; #8, #10, #16 closed), supersedes #8, #10, #16. Bumped to cache@v6, upload-artifact@v7, download-artifact@v8, codeql-action@v4; CI is the test |
| E11 | e2e-headless follow-up to E7: each of the 4 shards builds the mod cold (~7 min of its ~14-19); build it once in a job of its own and hand the output to the shards as an artifact | PR #26 (E7) | later | open |
| E8 | Keep the HostSimM3 sync-report numbers (path error, latency) from PASSING runs too (job summary or artifact), so a threshold miss can be told from noise | 2026-10-21 | M4 | open |
| E10 | **A durable SQLite commit on the session actor can stall heartbeats** on a slow disk: the economy/ledger stores commit with `synchronous=FULL` from the actor thread; under disk contention on the windows runner the live tests saw 11 s rtt and ping timeouts (CI fixed with `dotnet test -m:1`, PR #30). On a host with an HDD / busy NAS / cheap VPS this could drop players. Move the commit off the actor (or batch it) without losing durability | PR #30 analysis | M4 | open |
| E9 | `RelayModule.Chat` (line ~201) sends to nodes gated only on `IsAttached`, not `Announced`: a chat frame could reach a node before its Welcome | PR #21 review | M4 | open |
| E5 | Leftovers: worktrees `gracious-dubinsky…`, `charming-hodgkin…` (side sessions, need the user's OK); local branch `fix/net-disconnect-flake` (patch is in main via PR #30, needs `-D`); the merged remote branches on GitHub (deleting them needs the user's OK). All agent worktrees and merged local branches were removed 2026-10-22 | this file | now | open |
| E12 | Pair runners copy the mod log twice on failure, and the sync-report copy line has no test (code review 2026-10-21, findings 6/7) | code review | later | open |
| E13 | Ledger mark sequence was the implicit rowid of `ledger_tx` (PR #28 review) | PR #28 code review | - | fix in (migration 0010: explicit `seq` column, backfilled from rowid so stored marks stay valid, unique, frozen by the append-only trigger, assigned MAX+1 at commit; VACUUM + migration tests) |
| E14 | **e2e-headless shards 19-21 min wall** though their steps take 3-6 min: `tools/e2e.ps1` `Ensure-ModBuild` ran `mod/build.ps1 -NoTest`, which still compiles all ~540 Catch2 unit tests no shard runs (-NoTest only skips ctest); ~13 min of silent build on the 4-core runner, in every shard, plus the `mod` job = 5 full builds per run | lead, PR #41 CI run (2026-10-28) | - | fix in (PR #42). CI result: shards 19-21 min -> 13.5-16 min (cold sccache, run 40736569768) -> 12-14 min (warm, run 40739302151; misc shard mod build 205 s, 84/84 cache hits, was ~13 min); `mod` job ~8.5 min cold -> 6.3 min warm. (1) `mod/build.ps1 -Targets a,b` builds only those targets (same configure + warnings-as-errors); e2e builds `x4mp, x4mp-hostsim, x4mp-headless, x4mp_authority_live` (what the steps and `mod/tests/hostsim/*_run.ps1` look up; found by grepping every exe/dll name in e2e.ps1, the *_run.ps1 scripts and tools/session4). The `mod` job still builds everything and runs all ctest. (2) sccache (`mozilla-actions/sccache-action`, GitHub cache backend) in the `mod` job and the shards via `X4MP_COMPILER_LAUNCHER`, which also switches CI to `/Z7` (CMP0141). Local 20-core: full cold 48 s vs targeted cold 30 s. Follow-up idea: build once and hand the 4 binaries to the shards as an artifact |

## Flaky tests

Rule (execution-plan §3.4): a fail-then-pass is logged here with its run id; a **second** occurrence means investigating, not retrying.

| Test | Seen | Status |
|---|---|---|
| HostSimM3 turn-in-place yaw / HostSimPair online + removed count | M3-34 agent run (local), 2x on `chore/dotnet-10.0.13` (local), CI runs 39348939896, 39462803847 | **not flakes**, fixed in PR #17: ghost heading overshoot (M3-35) and a cross-process race in the pair test's world removal count (`removed_player`) |
| HostSimM3 sync-report `path error p95, steady flight < 300 m/s` 11.94 m (limit 10, Bot04; others 6.6-8.5) on a busy runner (latency worst 2.6 s) | PR #21 CI run 39603793368 (2026-10-21) | investigated: not a regression, the ~10 m comes from the player-sample time stamps (F9b) |
| `EconomyBehaviourLiveTests.AHeavyEconomyUnderAttackLeavesCleanBooksAndMatchingModels` (dotnet windows): runner stall, pings up to 11 s, 3 bots dropped | PR #19 CI run 39528117963 (2026-10-21) | fixed in PR #30 (disk contention from parallel test projects: `dotnet test -m:1`; the product side is E10) |
| Catch2 `core.net: a server Disconnect is delivered, and no-retry codes halt reconnecting` (`test_net_socket.cpp` 580-584, 5 s wait) on the windows `mod` job | PR #24 CI (2026-10-21) | **not a flake**, fixed in PR #30: a POLLHUP next to POLLWRNORM was read as a failed connect, so a Disconnect sent right after accept was lost |
| HostSimM3 `object count: got 4.0, want 7`: 2-3 bots welcomed but never download the save | PR #21 CI, PR #28 CI run 39666365507 (2026-10-22) | **not a flake**, fixed in PR #32: a FakeNode bot whose `SessionState` came > 2 s after its Welcome (busy actor, E10) stayed in bare-gateway mode and ignored the save offer |
| `EconomyHubTests.EconomyEventPushesAreRateLimited` | PR #26 CI (2026-10-22) | seen 1x, re-run passed |
| `EconomyAdminApiTests.ANegotiatingTradeCanBeCancelledAndTradeErrorsAreProblems` (fixture join refused: `HandshakeRejected RateLimited`, the global 20/s handshake bucket) | PR #39 CI (2026-10-28, ubuntu) | seen 1x, re-run passed; if it recurs, the admin test fixture should pace or batch its joins (test setup only, no limit change) |
| `e2e (playwright)`: the Playwright runner died with `EPIPE` (write to a closed socket in the reporter) and the job was cancelled after 15 min | PR #32 CI run 39843874603 (2026-10-22) | seen 1x, re-run passed |
| ReloadSurvival 401 when run right after HostSimM3 in one e2e call | 2026-10-04, 2026-10-06 (passes alone) | open, seen 2x: investigate when E1 is done |

## Done (log)

| Date | Item |
|---|---|
| 2026-10-28 | R1028 (two PCs): **F22 data path PASS** (no flash, `snaps=0`, no sawtooth), **F20b PASS in game** (re-provision after a rollback, Bob kept his ship), M3-34 step 4 PASS (no 286 km repair on a local-highway entry), F1 client side PASS. New: F23c, F21b, F1b, F1c, F1d, F24, F25, K5, K6; F9 measured |
| 2026-10-22 | E7 (PR #26): e2e-headless as 4 parallel shards, ~20 min wall instead of ~35. F13b (PR #28) merged. HostSimM3 stuck-bot join fixed in FakeNode (PR #32) |
| 2026-10-21 | E3: CI is the merge gate (PR #19, `ci-ok`), branch protection on main (required `ci-ok`, no force push / deletion, user OK). E4: Dependabot #12, #15, #18 closed, replaced by PR #22 / #23. M3-36 (PR #20), mod-report order (PR #21), F1 (PR #24), F13 (PR #25), red main fixed (PR #30) |
| 2026-10-20 | E1 + E2 (PR #17, `1e11a85`): .NET 10.0.13 / SDK 10.0.402 (CVE-2026-70159), M3-35 ghost heading fix, HostSimPair race fix, pair mod logs kept in the CI artifact; PR #14 closed as superseded |
| 2026-10-19 | Internet two-PC run: superhighway signal, re-host rebind, UDP fallback pass (RI) |
| 2026-10-07 | F4 (client map records explored space, M3-30 + hotfix), F18 (authority map flips, M3-31) confirmed in game (R28) |
| 2026-10-06 | F15, F16, F17, F20 fixed (M3-24/25/26/29); F16 confirmed in game 2026-10-19 |
