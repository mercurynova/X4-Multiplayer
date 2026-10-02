# Developer machine setup

This is the checklist for **any computer** you work on X4MP from. Run
`tools/check-env.ps1` after setup, and again whenever something seems off. It checks
everything below and tells you what's missing.

Last reviewed: 2026-10-01.

---

## 1. What each kind of machine needs

Not every machine needs everything. Pick the roles the machine will play.

| Role | What you do there | Needs sections |
|---|---|---|
| **Design / docs** | Planning with Claude, editing docs | §2 Core |
| **Server + GUI dev** | C# server, FakeNode, web dashboard | §2 Core + §3 Server/web |
| **Mod dev** | C++ DLL, Lua, MD, protocol C++ tests | §2 Core + §4 Native |
| **In-game tester** | Running X4 with the mod: spikes, milestone tests | §5 Game (+ §2 git to pull builds) |
| **Session host** | Running `x4mp-server.exe` for a play session | Only the published server exe + §7 network. No SDKs needed. |

A full workstation is all of the above.

---

## 2. Core (every dev machine)

| Tool | Version | Why | Install (admin terminal) |
|---|---|---|---|
| Git | any recent | source control | `winget install Git.Git` |
| GitHub CLI | any recent | PRs, CI status | `winget install GitHub.cli`, then `gh auth login` |
| Python | 3.12+ | `tools/x4cat_extract.py`, fake clients, scripts | `winget install Python.Python.3.12` |
| Claude Code | latest | Planning (Opus) + implementation agents (Sonnet) | desktop app or CLI |

## 3. Server and web GUI

| Tool | Version | Why | Install |
|---|---|---|---|
| .NET SDK | **10.0.4xx** (pinned by `global.json` to 10.0.400 + `latestFeature`; dev machine has 10.0.401) | server, FakeNode, tests | `winget install Microsoft.DotNet.SDK.10` |
| Node.js | 24.x LTS (22.x also fine) | web GUI build (Vite/React) | `winget install OpenJS.NodeJS.LTS` |
| npm | comes with Node | | |

The exact SDK version is pinned in `global.json` once M0-01 lands. The SDK must satisfy it
(`rollForward: latestFeature`).

## 4. Native mod (C++)

| Tool | Version | Why | Install |
|---|---|---|---|
| Visual Studio 2022 Build Tools | 17.6+ with **MSVC v143** and the "Desktop development with C++" workload | compiles `x4mp.dll` (x64) | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| CMake | ≥ 3.20 | build system | included in the VS C++ workload (or `winget install Kitware.CMake`) |
| Ninja | any | fast builds via CMake presets | included in the VS C++ workload |
| vcpkg | 2024+ | Catch2, nlohmann-json | included with VS 17.6+ (`VCPKG_ROOT`), or `git clone https://github.com/microsoft/vcpkg` + `bootstrap-vcpkg.bat` |
| flatc | pinned in `tools/flatc/` | FlatBuffers codegen | **don't install manually.** The M0-02 fetch script downloads the pinned version and checks its SHA-256 |

Build from a **"Developer PowerShell for VS 2022"** (or run `vcvars64.bat` first) so `cl`
and the Windows SDK are on PATH.

**Long paths (do this once per machine, admin PowerShell).** vcpkg build trees, especially
inside agent worktrees under `.claude/worktrees/`, create paths longer than Windows' 260-character
limit. Without these settings, git can't delete or check out those folders ("Filename too
long"), and some tools fail to build:
```
git config --system core.longpaths true
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem" -Name LongPathsEnabled -Value 1 -PropertyType DWORD -Force
```
The registry change takes effect for newly started programs (sign out/in to be safe). If a
folder still can't be deleted, use the long-path form:
`Remove-Item -LiteralPath "\\?\C:\full\path" -Recurse -Force`.

**X4Native** (eg3r, MIT) is **vendored in the repo** at the pinned release
`v9.0.0-611726` (see `mod-design.md`). Don't install it separately. Its `version_db/`
folder must ship with the mod, or the MD hooks silently do nothing.

## 5. Game machine (anyone running X4 with the mod)

| Requirement | Value | Notes |
|---|---|---|
| X4: Foundations | **9.00, build 611726** exactly | The mod refuses to connect on any other build (ADR pinned build). **Turn off Steam auto-update** for X4, or a patch will break the mod until we re-pin. |
| DLCs | The same set on every machine in a session | Dev machine has: Split Vendetta (`ego_dlc_split`), Cradle of Humanity (`ego_dlc_terran`), Tides of Avarice (`ego_dlc_pirate`), Kingdom End (`ego_dlc_boron`), Timelines (`ego_dlc_timelines`), Hyperion Pack (`ego_dlc_mini_01`), Envoy Pack (`ego_dlc_mini_02`). The handshake compares the extension list. |
| Protected UI mode | **OFF** | Settings → Extensions. Without this, X4Native's Lua can't load the DLL. |
| VC++ 2015–2022 x64 redistributable | installed | `winget install Microsoft.VCRedist.2015+.x64` (the mod DLL may link statically; X4Native needs it) |
| Mark-of-the-Web | stripped from downloaded mod files | `Get-ChildItem -Recurse <mod dir> \| Unblock-File`. The installer/Check Install will do this (M6). |
| Third-party mods | **disabled** during dev/testing, **except** the compatibility pass (below) | Mods come from three places: `<X4 install>\extensions\`, `Documents\Egosoft\X4\extensions\` and the Steam Workshop folder (`<steam library>\steamapps\workshop\content\392160\<id>\`, id `ws_<id>`). Enable state is per profile in `Documents\Egosoft\X4\<steamid>\content.xml`. Disable them in-game (Settings → Extensions) or unsubscribe. `tools/check-env.ps1` lists all three and flags stale `content.xml` entries. |
| Mod install location | `<X4 install>\extensions\x4mp\` (and vendored `x4native\`) | until the M6 installer exists, the dev build script copies them there |

### Paths (dev machine; yours may differ)

| What | Path |
|---|---|
| X4 install | `C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations` |
| User data (saves, config, logs) | `%USERPROFILE%\Documents\Egosoft\X4\<steam-user-id>\` (e.g. `...\X4\<steam-user-id>\`) |
| Saves | `...\<steam-user-id>\save\*.xml.gz` (load by name **without** `.xml.gz`) |
| User extensions | `%USERPROFILE%\Documents\Egosoft\X4\extensions\` |
| Profile extension enable state | `...\<steam-user-id>\content.xml` (`<extension id="..." enabled="true\|false">`) |
| Steam Workshop mods | `<steam library>\steamapps\workshop\content\392160\<workshop-id>\` (library folders from `steamapps\libraryfolders.vdf`) |
| Game log (debug) | launch X4 with `-debug all -logfile debuglog.txt` → written to the user data folder |

**OneDrive note:** if Documents is redirected to OneDrive (as on the dev machine:
`...\OneDrive - <org>\Documents\Egosoft\X4\...`), saves and the mod config sync through
OneDrive. Large saves mid-sync can be locked or half-written. Always resolve the path with
`[Environment]::GetFolderPath('MyDocuments')`, never hard-code `C:\Users\<you>\Documents`.
Consider pausing OneDrive during sessions.

The mod reads its config from a **file**, not environment variables, because Steam
relaunches X4 and drops the environment (see `requirements.md` PIT list).

### Compatibility pass (before each mod release)

X4MP depends on no library mod (ADR-043), but many players run them, so each release is
checked against the two common ones:

- **SirNukes Mod Support APIs**: Steam Workshop 2042901274 or Nexus 503.
- **kuertee UI Extensions and HUD**: Nexus 552 or Steam Workshop 3477279743, version 9.0.0.x.

Install them into `<X4 install>\extensions\` (or subscribe). Run the self-test and the
Join/chat test script in four configurations: none, SirNukes only, UIX only, both. For each
configuration, record the adapter's `source=` value from the
`X4MP ui: optionsmenu adapter` log line, whether X4Native's Settings → Extensions →
X4 Multiplayer page still appears, and whether chat round-trips (including a SirNukes
`/command` when it is installed). Disable both again afterwards.

There is no player-facing install step. The player README says: "Works with or without
kuertee UI Extensions and SirNukes Mod Support APIs."

### 5.x Testing with two real X4 instances (M2/M3+)

Researched 2026-10-01 at the user's request. **Decision: the user will buy a second copy** for the
two-instance checks (needed from the M3 exit; not before M2 ends).
- **One Steam copy on two PCs at once (one PC in Offline Mode) is not an option we use.**
  It may technically launch, and our server would not notice (identity is the per-install
  `player_key`, not the Steam id), but running one license on two machines at the same time
  is against Steam's terms. Steam Families does not help either: each game can only be
  played by one family member at a time unless the family owns more copies.
- **Most testing needs only one real X4.** The design lets FakeNode fill the other role:
  the real X4 as the authority with FakeNode clients, or the real X4 as a client with a
  FakeNode authority (`swarm --with-authority`). Use this for nearly all M2/M3 work.
- **For the real two-instance checks** (the M3 exit and later), use a second license:
  a teammate/friend with their own copy (also gives a realistic internet test), or a second
  copy on sale. The handshake hash covers **enabled** DLCs only, so a base-game-only second
  copy can join a session if the other player disables their DLCs in Extensions for that
  session (verify in a spike). A GOG copy is a separate build: check that X4Native and our
  pinned build (9.00 / 611726) support it before buying.

## 6. Local-only folders (not in git): regenerate on each machine

These are ignored by `.gitignore` and must be rebuilt after a fresh clone.

| Folder | What | How to recreate |
|---|---|---|
| `reference/` | read-only clone of the old mod (unlicensed: **never copy code**) | `git clone --depth 1 <previous-multiplayer-mod-repo> reference` |
| `reference-analyzer/` | the user's own save-analysis tool (Python, **GPL-3.0**, private): reference only, never copy code or its extracted game-data CSVs; notes in `docs/research/save-analyzer-notes.md` | `gh repo clone <owner>/<save-analyzer> reference-analyzer` (plain `git clone` fails: the repo-local gh credential helper doesn't apply before the clone exists) |
| `reference-tatertrader/` | TaterTrader auto-trade mod (public, **GPL-3.0**): reference for the ADR-050 P3 trade-route finder only, never copy code; notes in `docs/research/tatertrader-notes.md` | `git clone --depth 1 https://github.com/DeadAirRT/TaterTrader reference-tatertrader` |
| `x4-unpacked/` | game UI Lua (ffi signatures), MD scripts, libraries, used as the API reference | `python tools/x4cat_extract.py "<X4 install>" x4-unpacked "^(extensions/[^/]+/)?(ui/\|md/\|libraries/\|aiscripts/\|index/\|t/0001-l044)"` (~110 MB, ~1,600 files) |
| `data/` | server runtime data (SQLite, saves, logs) | created by the server on first run |
| build outputs | `bin/`, `obj/`, `build/`, `web/dist/`, `node_modules/` | normal builds |

## 7. Network (session host + players)

| Port | Proto | Purpose |
|---|---|---|
| 47780 | TCP | game nodes: control + bulk (save transfer) |
| 47781 | UDP | game nodes: realtime position lane |
| 47790 | TCP (HTTP) | admin web GUI + HTTP save fallback |

Open inbound on the **server** machine for the LAN/VPN only (LAN/VPN play only in v1:
Tailscale or ZeroTier for remote friends). Example:
```
New-NetFirewallRule -DisplayName "X4MP" -Direction Inbound -Protocol TCP -LocalPort 47780,47790 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "X4MP UDP" -Direction Inbound -Protocol UDP -LocalPort 47781 -Profile Private -Action Allow
```

## 8. Accounts and secrets

- GitHub: private repo **https://github.com/mercurynova/X4-Multiplayer**. `gh auth login` as `mercurynova` on each machine (or `gh auth switch -u mercurynova` if several accounts are logged in). Repo-local git identity: `mercurynova` / `59706122+mercurynova@users.noreply.github.com`.
- **Never commit** session passwords, admin passwords or `data/`. The server writes its
  initial admin password to a file under `data/` on first run.

## 9. New-machine quick start

```
gh auth login                      # or: gh auth switch -u mercurynova
gh repo clone mercurynova/X4-Multiplayer "C:\Personal\X4 Mult"
cd "C:\Personal\X4 Mult"
git config user.name "mercurynova"
git config user.email "59706122+mercurynova@users.noreply.github.com"
git config credential.https://github.com.helper ""
git config --add credential.https://github.com.helper "!gh auth git-credential"
powershell -ExecutionPolicy Bypass -File tools\check-env.ps1
# install whatever it reports missing, then regenerate local-only folders (§6)
```
Then open the folder in Claude Code. It reads `CLAUDE.md` for project context.
**Claude's memory is per-machine**, so anything important must live in the repo
(`CLAUDE.md`, `docs/`), not only in memory.

---

## 10. CI (GitHub Actions)

The repo is private with limited Actions minutes, so CI is path-gated and lean
(`.github/workflows/ci.yml`; Windows minutes bill 2x). A docs-only change runs only the
tiny `changes` job. Run the same commands locally before pushing.

| Job | Runs on | When | What |
|---|---|---|---|
| `web` | ubuntu | `server/web/**` changed | `npm ci`, lint, typecheck, test, build |
| `dotnet` | ubuntu | `server/**`, `tools/X4MP.*/**`, `tools/flatc/**`, `protocol/**`, `Directory.*.props`, `global.json` | fetch flatc, `dotnet restore --locked-mode`, build `-warnaserror -p:SkipWebBuild=true`, test |
| `dotnet` (windows) | windows | same paths, **push to main only** | same as above |
| `protocol-cpp` | windows | `protocol/**`, `tools/flatc/**` | `protocol/cpp/build.ps1` (vcpkg, Catch2, golden vectors) |
| `mod` | windows | `mod/**` (not `mod/spikes/**`), `protocol/**`, `tools/flatc/**` | `mod/build.ps1` (build, core tests, raw-remove guard, packaging check) |
| `mod-lint` | ubuntu | same as `mod` | XML well-formedness of `mod/extension/**`; luacheck only if `.lua` files exist |
| `e2e` (swarm) | ubuntu | `server/**`, `protocol/**`, `tools/X4MP.*`, `tools/flatc/**`, `tools/e2e.ps1`; always on push to main | `tools/e2e.ps1 -Steps Publish,Swarm`: publish linux-x64, bootstrap password change, `fakenode swarm --with-authority --teams 3 --relations ffa --economy casual --dupe-attack --verify --admin-url` |
| `e2e` (playwright) | ubuntu | same | `tools/e2e.ps1 -Steps Publish,Playwright`: the Playwright suite (Chromium, browsers cached) against the published server exe |
| `e2e-headless` | windows | `mod/**` (not spikes), `server/src/**`, `protocol/**`, `tools/X4MP.*`, `tools/flatc/**`; always on push to main | `tools/e2e.ps1 -Steps Publish,Headless`: builds the mod (shared vcpkg cache), then `x4mp-headless` joins a FakeNode-authority session: handshake, heartbeat, resume, save download |

Everything runs when `ci.yml` itself changes or on manual dispatch. Newer pushes to the same
ref cancel older runs. Test results and logs upload only on failure. Caches: NuGet, npm,
`tools/flatc/bin` (keyed on `flatc.lock.json`) and the vcpkg binary cache.

**End-to-end run (M1-C1) and how to reproduce it locally.** `tools/e2e.ps1` is the one script CI and developers run
(PowerShell 5.1 or 7; `pwsh tools/e2e.ps1` on Linux). Steps `Publish` (single-file server for this OS, Release,
`-p:SkipWebBuild=true`, plus FakeNode into `out/fakenode`), `Swarm`, `Playwright` and, on Windows, `Headless` (builds the
mod with `mod/build.ps1 -NoTest` unless `-SkipModBuild`). Default: all that this OS can run. Every step runs even if an
earlier one failed; the exit code is non-zero if any failed, and a timing table is printed (and added to the GitHub job
summary). Useful switches: `-Steps Swarm -SkipPublish` (reuse `out/`), `-SwarmSeconds`, `-Clients`, `-PlaywrightArgs economy`
(one spec), `-PlaywrightRetries` (CI uses 1: the economy specs are timing sensitive), `-SkipNpmInstall`, `-PortBase`
(default 47960; servers use 47960-47965 / 47970-47972, Playwright's GUI 5274, so a normal dev server keeps running).
Logs, `summary.md` and the Playwright traces go to `out/e2e/` (CI uploads them as `e2e-<name>-logs` on failure; open a trace
with `npx playwright show-trace`). The headless step relaxes `Net.ModBuildStrict` on its throw-away server because the
headless client reports mod build `dev` and FakeNode's authority `fakenode`. Reference timing on a developer PC (warm
caches, mod already built): publish 26 s, swarm 48 s, Playwright 177 s, headless 7 s = about 4.5 min sequentially.

**Nightly load job (M1-C2).** `.github/workflows/nightly.yml` (cron 03:17 UTC and manual dispatch; ubuntu, plus
windows when the dispatch input `windows` is ticked) publishes the real server and FakeNode, then runs the
`X4MP.LoadTests` harness: 1 authority + 16 clients, ~20k entities in the mirror, 9 minutes (60 s warm-up). It
writes `report.json` + `report.md` (job summary and the `load-report-<os>` artifact) and **fails** when a budget in
`server/tests/X4MP.LoadTests/budgets.json` is exceeded (tick p99 < 15 ms, CPU p95 < 100% of a core, working set
< 500 MB, no dropped frames, no verify errors, ...). It then runs the wall-clock tests marked
`[Trait("Category", "Perf")]` (today `SaveLatencyTests`), which PR CI excludes with `--filter "Category!=Perf"`.
Locally: `dotnet test X4MP.sln --filter "Category!=Perf"` for the default run, `--filter "Category=Perf"` for the perf
tests. The harness by hand: build Release, then `X4MP.LoadTests run --server-exe <x4mp-server> --fakenode-exe <FakeNode>
--duration 180` (non-default ports 47980/47981/47990; `--budget tickP99MsMax=0.5` overrides a budget, `evaluate
--report report.json` re-checks a finished run).

Other workflows: `codeql.yml` (C#, JS/TS; weekly and on push to main only; needs GitHub
Advanced Security on a private repo; set the repo variable `CODEQL_DISABLED=true` to turn it
off), `release.yml` (tags `v*`: server exes, mod zip, `SHA256SUMS`, draft release) and
`dependabot.yml` (weekly, minor/patch grouped; `Google.FlatBuffers` is ignored because it must
move together with flatc, ADR-041).
