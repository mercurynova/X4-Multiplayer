# X4MP Execution Plan: how the build is delegated

Status: draft, 2026-10-01. Companion to [`roadmap.md`](roadmap.md) (what to build) and
[`architecture.md`](architecture.md) (how it fits together). This doc covers **who builds
what, in what order, and how work is checked**.

## 1. Working model

| Role | Model | Responsibilities |
|---|---|---|
| Lead / architect | **Opus** (main session) | Planning, task briefs, decomposition, resolving design questions, reviewing every delivered task against its acceptance criteria, merging, updating docs/ADRs. Writes no large code. |
| Implementers | **Sonnet** subagents (`model: "sonnet"`) | Implement exactly one brief (one task or one tight sequential chain), with tests, on an isolated git worktree branch. Report back in the fixed format (§5). |
| Reviewer pass | Opus (or a Sonnet `/code-review` run that Opus then triages) | Build + test locally, check each acceptance criterion, review for scope creep and contract drift, request fixes via SendMessage to the same implementer. |
| In-game tester | **User** | Runs M2-spike experiments and later milestone acceptance in X4; Claude can't play the game. |

Rules for implementers (put in every brief):
1. Stay inside the brief's file scope. Do not edit `docs/` except where the brief says to
   (e.g. a protocol catalog table); report design problems instead of redesigning.
2. Never copy code from `reference/` (unlicensed). Never modify the X4 install or `x4-unpacked/`.
3. Every acceptance criterion needs a test or a reproducible command. "Done" means
   `dotnet build`/`dotnet test`/`npm test`/`ctest` pass with zero warnings.
4. Don't add dependencies that aren't in `Directory.Packages.props` / vcpkg manifest without
   saying so in the report.
5. Commit on your worktree branch with a message like `M0-04: FrameCodec + registry`, plus
   the attribution trailer. Do not push.

## 2. Prerequisites (user action, before any coding)

Checked on this PC on 2026-10-01:

| Tool | Needed for | Status |
|---|---|---|
| .NET **SDK** | server, FakeNode, tests | **Missing**: only runtimes 8.0.31 and 10.0.12 are installed |
| Node.js + npm | web GUI | OK (v24.16.0) |
| Visual Studio 2022 Build Tools (MSVC v143, "Desktop development with C++") | mod DLL, C++ protocol tests | **Missing** |
| CMake ≥ 3.20 (+ Ninja) | mod build | **Missing** (comes with the VS C++ workload) |
| vcpkg | Catch2, nlohmann-json | **Missing** (comes with VS 2022 17.6+, or clone it) |
| flatc | schema codegen | Missing; **fetched automatically** by task M0-02's pinned script |
| git, gh, Python 3.12 | repo, CI, tooling | OK |

**Decision needed: .NET 8 vs .NET 10.** ADR/PLAN lock .NET 8, but .NET 8 LTS support
ends **10 Nov 2026**, about 6 weeks from now. .NET 10 is the current LTS (supported to
Nov 2028), and its runtime is already installed here. Recommendation: **switch to .NET 10**
(`net10.0`, SDK 10.0.1xx). The designs use nothing .NET 8-specific, so the change only
touches `global.json`/`Directory.Build.props` and the ADR text.

Suggested install commands (run by the user, admin rights needed):
```
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

## 3. Repository and branching

- M0-01 runs `git init` in `C:\Personal\X4 Mult` with `.gitignore` excluding `reference/`,
  `x4-unpacked/`, build outputs and `data/`. `docs/`, `protocol/`, `tools/x4cat_extract.py`
  and `PLAN.md` become the first commit.
- Every Sonnet task runs with `isolation: "worktree"` on branch `task/<ID>-<slug>`.
  Opus reviews it, then merges to `main` (fast-forward or squash). Parallel agents never
  share a working tree.
- GitHub remote (needed for CI from M0-16): **user decision**: create a private repo
  (e.g. `x4mp`) and push, or keep it local until M1. CI tasks can be written before a
  remote exists; they just can't run.

## 4. Delegation waves

Each box is one Sonnet agent. Agents in the same wave run in parallel. A wave starts when
its dependencies have been reviewed and merged.

### M0

| Wave | Agent | Tasks (sequential inside the agent) | Needs |
|---|---|---|---|
| 0 | — | User installs prerequisites; .NET version decided | — |
| 1 | `scaffold` | M0-01 (repo, sln, all empty projects, props, gitignore, first commit) | .NET SDK |
| 2 | `protocol-cs` | M0-02 → M0-03 → M0-04 → M0-05 → M0-06 | wave 1 |
| 2 | `web-shell` | M0-09 | wave 1 |
| 2 | `server-infra` | M0-13 (Serilog) → M0-14 (persistence) | wave 1 |
| 3 | `protocol-cpp` | M0-07 | `protocol-cs`, MSVC |
| 3 | `server-host` | M0-10 → M0-11 → M0-12 → M0-15 | `web-shell`, `server-infra` |
| 4 | `mod-skeleton` | M0-08 | `protocol-cpp` |
| 5 | `ci` | M0-16 | all above |

Five reviews gate M0. The protocol chain is the critical path. If MSVC isn't installed
yet, waves 2–3 (C# + web) still proceed and the C++ tasks wait.

### M1 (outline; briefs written when M0 lands)

| Wave | Parallel agents |
|---|---|
| A | `net-core` (M1-01→02→03→04), `fakenode-core` starts with stubs (M1-F1 part 1), `admin-auth` (M1-S1), `mod-net-1` (M1-N1) |
| B | `session` (M1-05), `config-metrics` (M1-13, M1-14), `events` (M1-11), `mod-net-2` (M1-N2) |
| C | `world-interest` (M1-06→07), `teams-domain` (M1-T1→T2), `ledger` (M1-E1→E2) |
| D | `replication` (M1-08, later M1-09), `relay` (M1-10), `saves` (M1-12), `fakenode-core` part 2 (M1-F1 verify, M1-F2) |
| E | `teams-fanout` (M1-T3→T4), `economy-actions` (M1-E3→E4→E5), `admin-api` (M1-S2→S3), `mod-session` (M1-N3) |
| F | `gui-shell` (M1-W1→W2→W3), `gui-map` (M1-W4), `gui-sessions` (M1-W5→W6), `fakenode-teams-econ` (M1-F3→F4) |
| G | `teams-ui` (M1-T5), `economy-ui` (M1-E6→E7), `load` (M1-C2) |
| H | `e2e` (M1-C1), `docs` (M1-C3) |

Concurrency cap: **≤ 4 Sonnet agents at a time**. That's enough parallelism without
merge conflicts piling up in shared files (`Directory.Packages.props`, `MsgType` registry,
`Program.cs`). Tasks that touch the same shared file go in different waves, or their brief
names one owner of the file.

### M2-spike (user + Opus, parallel with M1)
S1, S2, S4, S5 first (they gate team/avatar/economy scope), then S3, S6, S9, S7, S8.
For each spike Opus writes a step-by-step test script and a small throwaway extension
under `mod/spikes/` (built by a Sonnet agent). The user runs it in X4 and pastes back the
logs, and Opus records the verdict in `decisions.md` Part 3.

## 5. Brief template (Opus → Sonnet)

```
TASK <ID(s)>: <title>
Branch: task/<id>-<slug> (worktree)
Context to read first: docs/architecture.md §<n>; docs/<detail>.md §<n>; ADR-<n>
Goal: <2-3 sentences>
In scope (files/dirs): <list>
Out of scope: <list, incl. "do not edit docs except X">
Contracts to honour: <type names, message ids, ports, settings keys>
Acceptance criteria (each needs a test or command):
  1. ...
Commands that must pass: <dotnet build -warnaserror; dotnet test --filter ...; npm test>
Report back (≤ 250 words): what was built, files touched, test results (counts),
  deviations from the brief and why, open issues, follow-ups.
```

## 6. Review checklist (Opus, per task)

1. Branch builds clean and all tests pass locally (rerun, don't trust the report).
2. Each acceptance criterion maps to a passing test or verified command.
3. No edits outside the brief's scope, no new dependencies left unreported, no
   `reference/` code.
4. Contracts match `architecture.md` / `protocol.md` (names, ids, ports, lanes).
5. Run the `code-review` skill on the diff for correctness. Send fixes back to the same
   implementer (SendMessage) rather than fixing in the Opus session.
6. Merge, update `roadmap.md` task status, and record any design change as an ADR.

## 7. Open items for the user

1. Install prerequisites (§2).
2. .NET 8 → .NET 10? (recommended yes)
3. Create a private GitHub repo now, or stay local until M1?
4. When to schedule the first in-game spike session (S1/S2/S4/S5, ~1–2 hours of play-testing).
