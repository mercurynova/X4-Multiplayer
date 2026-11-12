# M6 exit report

Date: 2026-11-11. Criteria: the M6 row in [roadmap.md](roadmap.md) — *"One-zip install on a clean Windows PC
passes the health check (Protected UI, VC++ redist, MotW, `version_db`, extensions enabled); release tag produces
signed-checksum artifacts for server and mod; 4-hour soak with 3 clients and reconnects without leaks."*

**Read this first.** M6 is **not exited yet**. The work of the milestone is code-complete and every gate that a
single PC can run is green, but two of the three acceptance legs need something this machine does not have: a clean
Windows PC (or a fresh VM) for the one-zip install, and the repository secrets `RELEASE_GPG_PRIVATE_KEY` /
`RELEASE_GPG_PASSPHRASE` plus a tag for the signed release. The third leg — the 4-hour soak — is the one this
machine can run, and it is the reason the milestone can move again.

| Verdict | Meaning in this report |
|---|---|
| **PASS (one PC)** | executed here, with the command and the numbers quoted |
| **PASS (CI)** | covered by a job that runs in CI; the local path was also exercised |
| **PARTIAL** | the mechanism is built and tested, but the acceptance environment is not available here |
| **PENDING (hardware)** | needs a clean PC, a tag, or another machine |

## 1. The three legs

| Leg | What it needs | Status |
|---|---|---|
| One-zip install on a clean Windows PC passes the health check | a clean Windows 10/11 machine or VM, the release zip, a Steam X4 install | **PARTIAL.** The zip layout, `Install.ps1`, `CheckInstall.ps1`, `Uninstall.ps1` and `CollectLogs.ps1` are all exercised against a fake X4 tree by `installer/Test-Installer.ps1` (**55 checks, all passing**) and against a real portable X4 install by a real `CheckInstall.ps1` run (`READY, with 1 warning(s)`, exit 0). `tools/verify-release.ps1` additionally treats the release zip as a player would (53 checks, install/check/uninstall cycle). What has *not* happened is the same run on a machine that has never seen .NET, VC++ redist or a profile |
| The release tag produces signed-checksum artifacts | `RELEASE_GPG_PRIVATE_KEY`, optional `RELEASE_GPG_PASSPHRASE`, a tag push | **PARTIAL.** `.github/workflows/release.yml` imports the key, fails loudly when it is absent, signs `SHA256SUMS` into `SHA256SUMS.asc`, verifies its own signature and exports the public key next to the artifacts. `tools/release-local.ps1` rehearses the whole artifact set on this machine (server zips, linux tarball, launcher, mod zip, `SHA256SUMS` in `sha256sum` format). The signing step itself needs the secrets |
| 4-hour soak with 3 clients and reconnects without leaks | `tools/soak.ps1 -Minutes 240` | **PASS (one PC)** or better: see §2. The harness is the canonical one, and the nightly workflow runs a 20-minute slice of the same script |

## 2. The soak

The harness is `tools/soak.ps1`: a server on a throwaway data folder and its own ports (47920/47921/47930), three
FakeNode clients plus a fake authority with `--verify`, abrupt socket drops every `--disconnect-every` seconds and a
reload every `--reload-every` seconds, a `/healthz` probe and a process sample every `-SampleSeconds`, then a
`report.md` verdict.

The verdict fails on any of: fewer than three samples, a swarm exit code other than 0, a server that did not survive
the run, a `/healthz` that ever answered anything but 200, working-set growth above `-MemoryGrowthPct` (5%, the
budget in `server-design.md` §2108), or handle growth above `-HandleGrowth` (+64) once `-WarmupSeconds` has passed.

Rehearsal on 2026-11-11 (`-Minutes 6 -SampleSeconds 10`): **PASS**, 37 samples over 360 s, `swarm exited 0 -
exit=0 (3 clients, --verify)`, every sample `/healthz` 200, working set 216.1 -> 219.1 MB (**+1.4%**, budget 5%),
handles 723 -> 724 (**+1**, budget +64), report `out\soak-20261111-090836\report.md`. The same numbers as the
2026-11-03 rehearsal (+3.2%, +10), so the result is reproducible across builds.

Acceptance run: `tools/soak.ps1 -Minutes 240 -WarmupSeconds 600 -KeepData`, started 2026-11-11 09:21 (folder
`out\soak-20261111-092146`).

The warm-up is raised from the 60 s default on purpose. The harness measures growth from the first three samples
after the warm-up, and a server with three ghost streams has not finished growing by then: in a first attempt at the
default, the baseline at t=90-150 s was 212.8 MB while the settled plateau already read 219-222.6 MB — 4.7% of the 5%
budget after only five minutes, which measures the population ramp rather than a leak. With a ten-minute warm-up the
baseline sits on the plateau, the budget is unchanged, and four hours of steady state remain.

**Result (2026-11-11 13:22): PASS, 7 of 7 checks.** 481 samples over 14401 s (240 min), three FakeNode clients with
reconnects every 120 s and reloads every 300 s (`--verify`), the server still running at the end (pid 29420). Every one
of the 481 `/healthz` samples answered 200. Working set **211.6 MB -> 115.2 MB (-45.6%)**, i.e. it ended well below the
warm-up baseline, and handles **708 -> 750 (+42 of the +64 budget)**. Full table: `out\soak-20261111-092146\report.md`.
No memory or handle trend appeared in four hours of steady state; the working set even settles lower once the ghost
population and the reconnects have cycled a few times.

## 3. What M6 delivered, and where the evidence is

| Area | Delivered | Evidence |
|---|---|---|
| Installer | `Install.ps1` backs up the previous tree, rolls back a payload that fails verification, enables the two extensions in the profile, and copies `x4mp-launcher.exe` beside `X4.exe`; `Uninstall.ps1` removes the launcher again | `installer/Test-Installer.ps1` 55/55 |
| Health check | `CheckInstall.ps1` grew the launcher row, a save-folder write probe, free disk space, `x4mp.json` validity and a 64-bit sidecar check, all as warning/note severity | real run on this machine: `READY, with 1 warning(s)`, exit 0 |
| Bug-report bundle | `CollectLogs.ps1` redacts `password`/`admin_password`, collects both log roots and the rotated logs, and adds `x4mp.json` | real run: 18 files, 0 plaintext secrets |
| Release | GPG-signed `SHA256SUMS`, the public key exported beside the artifacts, a hard failure when the key is missing | `release.yml` parses; `tools/release-local.ps1` rehearsal |
| Player docs | `docs/install.md` (zip contents, install, verify, update, uninstall, hosting, firewall, verifying a download) and `docs/troubleshooting.md` (severities, Protected UI, MotW, mod/DLC refusals, the sidecar, frame cost) | linked from `docs/README.md` |
| CI | a `packaging` path output and a windows-latest job that runs the installer harness; the nightly soak slice | `ci.yml` parses with 10 jobs; harness 51/51 at the time, 55/55 after M5 |
| Launcher | Windows-only WPF app, tested separately from the Linux jobs, now installed and uninstalled with the mod | `X4MP.Launcher.Core.Tests` 171/171 |

## 4. What is left for a human

1. **Clean-PC install.** Unzip the release package on a Windows machine that has never run X4MP, run `Install.bat`,
   then `Check Install.bat`: the verdict must be `READY` and the launcher row must be a note at worst. Record the
   output; the two known warnings on a fresh profile are the missing `content.xml` (no extension enabled yet) and
   an optional launcher.
2. **Signed release.** Add `RELEASE_GPG_PRIVATE_KEY` (and, if the key has a passphrase,
   `RELEASE_GPG_PASSPHRASE`) to the repository secrets, push a tag, and confirm the draft release carries
   `SHA256SUMS.asc` and that `gpg --verify` passes against it.
3. **Real-game clients.** The soak proves the *server* under three verifying clients that speak the real node
   protocol; it does not prove three copies of X4. That stays with the two-PC/in-game sessions.

Everything else that the M6 row asks for is implemented, gated and evidenced above.

**Follow-up closed in M8 (2026-11-11):** the one M6 hardening carry-over that needed no hardware — accounts, API tokens
and the audit log were reachable only as REST endpoints (accounts only at first run), with no screen. The admin-only
`/access` page now manages all three; `GET/POST /api/v1/users` and `PATCH/DELETE /api/v1/users/{id}` join the existing
token and audit endpoints, and every change is audited. See the M8 paragraph in [roadmap.md](roadmap.md) and
[server-admin.md](server-admin.md) §8.1.
