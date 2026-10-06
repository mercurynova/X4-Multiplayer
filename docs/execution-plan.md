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
5. Commit on your worktree branch with a message like `M0-04: FrameCodec + registry`, with **no
   attribution lines** (no `Co-Authored-By:`, no "Generated with"). Do not push.

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

**Decided 2026-10-01: .NET 10** (`net10.0`, SDK 10.0.400 pinned, `latestFeature`), because
.NET 8 support ends 10 Nov 2026. All prerequisites were installed on the dev machine on
2026-10-01 (SDK 10.0.401, VS 2022 Build Tools + C++ workload with CMake and vcpkg).

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
- GitHub remote: **https://github.com/mercurynova/X4-Multiplayer** (private, `origin`). Implementers do not push; Opus merges and pushes after review.
  (e.g. `x4mp`) and push, or keep it local until M1. CI tasks can be written before a
  remote exists; they just can't run.

### 3.1 Worktree cleanup (user request, 2026-10-04)

Every agent worktree is a full repo copy plus its own build output (~1.5-3 GB each; 26 of them reached 43 GB). The lead
cleans them up on a fixed rhythm instead of letting them pile up.

**A worktree is removed when all of these hold:**
1. Its branch is merged into `main`, and `main` is pushed.
2. The lead verified `main` after that merge (build + tests / e2e as for the task) and CI on the push is green.
3. No follow-up to that agent is expected: no open question, no "send it back for a fix", no rebase pending. In
   practice: the **wave it belongs to is closed** (all of the wave's tasks merged), or the task is two waves old.
4. `git -C <worktree> status --porcelain` is empty (no uncommitted work). A dirty worktree is never deleted: the lead
   looks at what is in it first and asks the user if it is not obviously disposable.

**Never removed automatically:** a branch that is not merged (abandoned or failed tasks: ask the user), a worktree of an
agent that is still running or might be resumed, and anything outside `.claude/worktrees/`.

**When:** at the end of every wave (after the wave's last merge is verified), at the end of a milestone, and before a
history rewrite. A quick look at `.claude/worktrees` size is part of the wave-end checklist.

**How:** `git worktree remove <path>` then `git branch -d <branch>` (the safe `-d`, which refuses unmerged branches; never
`-D` without the user's OK), then `git worktree prune`. The Claude desktop app's worktree clean-up tool may be used for the
same set. Report the freed space in one line.

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

### 6.1 Long-running agents (user request, 2026-10-01)

Sonnet tasks usually finish in 10–50 minutes. When one runs much longer, the lead checks
what it is doing (worktree `git status`/`git diff`, running processes) and tells the user
**why** it is slow. A slow agent is fine if the time is really needed (e.g. a large task, or a
verification loop the brief asked for); it is not fine if the agent is going in circles.

- Flake hunts and other open-ended investigations get a time box in the brief (about 45 min):
  after it, commit what exists and report the failure, message and best hypothesis.
- Loops of the form "run N times until it passes" must state their cost up front
  (a failing run of a test with a 60 s timeout costs 60–90 s per iteration).
- Agents' self-reported durations are unreliable (M1-S3 said ~2 h, the harness measured
  26 min; M1-W1 said ~50 min, measured 9 min). Use the harness `duration_ms`.
- **No sleep-polling.** Run tests in the foreground and wait (raise the command timeout; the full
  suite takes ~7 min), or start them as a background run that notifies on completion. Don't loop
  `sleep N` + check: each poll leaves a shell and a sleep process behind and can idle the agent for
  minutes after the run finished (seen in wave H, 2026-10-02). Put this line in every brief.
  Also no background `sleep N` used as a timer or "check back later" (seen again in M2 wave 2, 2026-10-03:
  seven idle `sleep 240..1500` processes plus `sleep 5` loops). Briefs must say this explicitly.
  The words "no sleep-polling loops" were read as allowing single long waits (M3 wave 4, 2026-10-06: nine
  `sleep 270..840` processes). Put this exact line in briefs: **"Never run `sleep` or `Start-Sleep` at all;
  run long commands with run_in_background and wait for the completion notification."**
- **Never kill processes by name** (`Stop-Process -Name`, `taskkill /IM`): other agents and the lead run tests on the same machine
  (M3-31, 2026-10-06, killed the lead's running e2e). Stop only the process ids you started. Put this line in every brief.
- Record the reason for any run over ~1 hour in the wave status note in roadmap.md
  (what took the time, whether it was needed), so we can tune briefs.

## 7. Open items for the user

1. ~~Install prerequisites~~ Done on the dev machine (repeat per machine via dev-setup.md).
2. ~~.NET version~~ Done: .NET 10.
3. ~~GitHub repo~~ Done: private https://github.com/mercurynova/X4-Multiplayer (2026-10-01).
4. When to schedule the first in-game spike session (S1/S2/S4/S5, ~1–2 hours of play-testing).
