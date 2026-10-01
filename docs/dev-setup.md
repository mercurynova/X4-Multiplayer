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
| .NET SDK | **10.0.1xx** (pending decision; see `execution-plan.md` §2. If we stay on .NET 8, use SDK 8.0.4xx) | server, FakeNode, tests | `winget install Microsoft.DotNet.SDK.10` |
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
| Third-party mods | **disabled** during dev/testing | Keep `Documents\Egosoft\X4\<steamid>\extensions` empty, or disable those extensions in-game. |
| Mod install location | `<X4 install>\extensions\x4mp\` (and vendored `x4native\`) | until the M6 installer exists, the dev build script copies them there |

### Paths (dev machine; yours may differ)

| What | Path |
|---|---|
| X4 install | `C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations` |
| User data (saves, config, logs) | `%USERPROFILE%\Documents\Egosoft\X4\<steam-user-id>\` (e.g. `...\X4\<steam-user-id>\`) |
| Saves | `...\<steam-user-id>\save\*.xml.gz` (load by name **without** `.xml.gz`) |
| User extensions | `%USERPROFILE%\Documents\Egosoft\X4\extensions\` |
| Game log (debug) | launch X4 with `-debug all -logfile debuglog.txt` → written to the user data folder |

**OneDrive note:** if Documents is redirected to OneDrive (as on the dev machine:
`...\OneDrive - <org>\Documents\Egosoft\X4\...`), saves and the mod config sync through
OneDrive. Large saves mid-sync can be locked or half-written. Always resolve the path with
`[Environment]::GetFolderPath('MyDocuments')`, never hard-code `C:\Users\<you>\Documents`.
Consider pausing OneDrive during sessions.

The mod reads its config from a **file**, not environment variables, because Steam
relaunches X4 and drops the environment (see `requirements.md` PIT list).

## 6. Local-only folders (not in git): regenerate on each machine

These are ignored by `.gitignore` and must be rebuilt after a fresh clone.

| Folder | What | How to recreate |
|---|---|---|
| `reference/` | read-only clone of the old mod (unlicensed: **never copy code**) | `git clone --depth 1 <previous-multiplayer-mod-repo> reference` |
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
