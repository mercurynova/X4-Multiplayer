# X4MP backlog (the single index of open work)

Started 2026-10-20 (user decision). **One line per item**; details live in the linked results / plan docs. The lead updates this
file at the end of every major step (docs/execution-plan.md §3.2) and walks it for every milestone exit report.

- **Target:** `M3 exit` (must be settled before M3 closes), `M4`, `M5`, `M6`, `later`, `wontfix`.
- **Status:** `open`, `fix in, in-game check pending`, `decision needed`, `deferred`, `done <date>` (done items move to the log at the bottom).
- Evidence docs: S1 = [sitting 1](spikes/session-4-sitting-1-results.md), S2 = [sitting 2](spikes/session-4-sitting-2-results.md),
  S3 = [sitting 3](spikes/session-4-sitting-3-results.md), R28 = [M3-28 + two-PC runs](spikes/session-4-m3-28-run-results.md),
  RI = [internet run](spikes/session-4-internet-run-results.md), plan = [m3-plan.md §8](m3-plan.md).

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
| D2 | **xunit.runner.visualstudio 3 -> 4** (test projects) | PR #13, CI red | Test-runner major; may need test-project changes | Next .NET tooling pass (with the SDK move follow-ups), or when 3.x stops working with a new SDK / VS | Check the v4 migration notes (xunit v3 runner requirements), update `Directory.Packages.props`, regenerate lock files with a plain restore, full `dotnet test`; one Sonnet task |

## Open items

### Session 4 / M3 (in-game)

| ID | Item | Evidence | Target | Status |
|---|---|---|---|---|
| F21 | Markers flash around a superhighway transit | R28, plan M3-32/33 | M3 exit | fix in (M3-33), in-game check pending |
| F22 | Ghost alternates between two spots after an authority re-host | RI, plan M3-34 | M3 exit | fix in (M3-34), in-game check pending |
| F23 | Map icon bounces along the travel line in fast in-sector flight; engine moves our inert objects (286 km) | RI, plan M3-34 | M3 exit | mitigated (M3-34: hint cap, engine guard, diag switches), in-game check pending |
| F23b | Target info shows 0 m/s for a remote ship above 1000 m/s (M3-34 velocity-hint cap) | plan M3-34 | M3 exit | decision needed after the M3-34 check (`diag.hints_off` result) |
| F20b | Re-provision path (checkpoint predates the online player) never exercised in game | RI | M3 exit | open (hostsim covers it) |
| X1 | 30-min flight over >= 5 sectors incl. a highway (M3 criterion; sittings reached ~25 min) | S3 | M3 exit | open |
| X2 | M3 exit report | roadmap | M3 exit | open (after the checks above) |
| F1 | Exit to the main menu keeps the session "in game" | S1 | M3 exit | decided (ADR-052): counts as leaving; **implemented (mod `MenuExitWatch`, 30 s confirm, server unchanged); in-game check "F1 main-menu leave" in in-game-session-4.md** |
| F9 | Ghost display delay sits at the 250 ms cap (latency p95 > 200 ms target) | S1, S3 | M3 exit | decided (ADR-052): measure in the next session; at the cap -> accept for M3, < 200 ms becomes M4 |
| F6 | Mod main-thread p95 0.27 ms with 8 ghosts (target < 0.2 ms) | S1 | M4 | open (perf pass) |
| F8 | FPS 100 -> 70 with 8 ghosts nearby | S1 | M4 | open |
| F13 | `AuthorityGraceSeconds` default 120 s ends a session before a real X4 restart is back; an Ended session cannot be resumed | S2 | M3 exit | decided (ADR-052): default ~600 s (GUI setting) + admin resume of an Ended session from its last checkpoint; implemented on feat/f13-authority-grace-resume, in-game check open (in-game-session-4.md "F13 authority grace + resume") |
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
| K1 | `sync-report.ps1` judges the whole mod log, should judge one run | S2 | M4 | open |
| K2 | Kit / publish should rebuild (or check) the web GUI build | S1 | M4 | open |
| K3 | GUI "Total playtime 0s" | S3, RI | later | open |
| K4 | Firewall disable test: disabling the allow rule does not cut a live UDP flow; script 3.5 should use a block rule | RI | M3 exit | open (script text) |

### Engineering

| ID | Item | Evidence | Target | Status |
|---|---|---|---|---|
| E3 | CI becomes the merge gate (PR flow, `ci-ok`, branch protection, full CI on PRs) | execution-plan §3.4 | now | ci.yml done (PR #19, 2026-10-21); branch protection waits for the user's OK |
| E4 | Dependabot: #12, #15, #18 closed 2026-10-21 (superseded; replaced by PR #22 / #23, merged); the Actions majors #8, #10, #16 go to E6 | GitHub | done | done 2026-10-21 |
| E6 | **Node 20 deprecation** warnings in CI: `actions/cache@v4`, `actions/upload-artifact@v4`, `actions/download-artifact@v4`, `github/codeql-action@v3` target Node 20 (our `setup-node` is already 24). One Sonnet PR bumps all four majors together (upload + download artifact must match), release notes checked, full CI incl. e2e-headless; supersedes #8, #10, #16 | CI annotations (2026-10-20) | after E3 | open |
| E7 | **Faster e2e-headless** (~34 min serial on one runner): split into 3-4 parallel Windows jobs balanced by step time (Headless 7.8 min, Avatars 5.8, AuthorityFlow 2.7, the rest 0.2-2), each builds the mod itself; then look at why Headless / Avatars wait so long (test-only server timeouts, never looser checks). Target ~13-15 min wall. `ci-ok` keeps needing every job. Considered and not taken: e2e only in a merge queue (personal-account repos may not have merge queues, and PR e2e caught the 2026-10-21 bot-join regression before main), larger paid runners (the steps wait, they are not CPU-bound), path filters (just removed) | user 2026-10-21 | after E3 (#19) | open |
| E8 | Keep the HostSimM3 sync-report numbers (path error, latency) from PASSING runs too (job summary or artifact), so a threshold miss can be told from noise | 2026-10-21 | M4 | open |
| E9 | `RelayModule.Chat` (line ~201) sends to nodes gated only on `IsAttached`, not `Announced`: a chat frame could reach a node before its Welcome | PR #21 review | M4 | open |
| E5 | Leftovers: worktrees `gracious-dubinsky…`, `charming-hodgkin…` + the PR #17 agent worktrees; branches `deps/signalr-client-advisory`, `chore/sdk-10.0.402`, `chore/dotnet-10.0.13`, `chore/pair-logs-in-ci`, `fix/pair-removed-race` | this file | now | open |

## Flaky tests

Rule (execution-plan §3.4): a fail-then-pass is logged here with its run id; a **second** occurrence means investigating, not retrying.

| Test | Seen | Status |
|---|---|---|
| HostSimM3 turn-in-place yaw / HostSimPair online + removed count | M3-34 agent run (local), 2x on `chore/dotnet-10.0.13` (local), CI runs 39348939896, 39462803847 | **not flakes**, fixed in PR #17: ghost heading overshoot (M3-35) and a cross-process race in the pair test's world removal count (`removed_player`) |
| HostSimM3 sync-report `path error p95, steady flight < 300 m/s` 11.94 m (limit 10, Bot04; others 6.6-8.5) on a busy runner (latency worst 2.6 s) | PR #21 CI run 39603793368 (2026-10-21) | seen 1x, re-run |
| `EconomyBehaviourLiveTests.AHeavyEconomyUnderAttackLeavesCleanBooksAndMatchingModels` (dotnet windows): runner stall, pings up to 11 s, 3 bots dropped | PR #19 CI run 39528117963 (2026-10-21) | seen 1x |
| Catch2 `core.net: a server Disconnect is delivered, and no-retry codes halt reconnecting` (`test_net_socket.cpp` 580-584, 5 s wait) on the windows `mod` job | PR #24 CI (2026-10-21) | seen 1x |
| ReloadSurvival 401 when run right after HostSimM3 in one e2e call | 2026-10-04, 2026-10-06 (passes alone) | open, seen 2x: investigate when E1 is done |

## Done (log)

| Date | Item |
|---|---|
| 2026-10-20 | E1 + E2 (PR #17, `1e11a85`): .NET 10.0.13 / SDK 10.0.402 (CVE-2026-70159), M3-35 ghost heading fix, HostSimPair race fix, pair mod logs kept in the CI artifact; PR #14 closed as superseded |
| 2026-10-19 | Internet two-PC run: superhighway signal, re-host rebind, UDP fallback pass (RI) |
| 2026-10-07 | F4 (client map records explored space, M3-30 + hotfix), F18 (authority map flips, M3-31) confirmed in game (R28) |
| 2026-10-06 | F15, F16, F17, F20 fixed (M3-24/25/26/29); F16 confirmed in game 2026-10-19 |
