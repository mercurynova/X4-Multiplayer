# X4MP Roadmap

Status: v1.0, 2026-10-01. Follows [`architecture.md`](architecture.md) and
[`decisions.md`](decisions.md). Sizes: **S** ≤ 1 day, **M** 2–3 days, **L** 4–6 days (one
developer). "Deps" lists task ids that must be done first. Tracks: **P** protocol,
**S** server, **W** web GUI, **N** native mod, **F** FakeNode/test tooling, **C** CI.

---

## 1. Milestones

| # | Milestone | Needs X4? | Exit criteria |
|---|---|---|---|
| **M0** | Repo skeleton, build, CI, protocol schema v0.1 compiled with codegen in C# and C++ | no | Fresh clone builds and tests on Windows + Linux (server) and Windows (mod core). `flatc` compiles every `.fbs` with the ADR-036 deltas applied. Golden vectors encoded by C# decode and re-encode byte-identically in C++. Published `x4mp-server.exe` serves the empty GUI shell and `/healthz` on 47790. CI green, Dependabot + CodeQL on. |
| **M1** | Server: handshake, sessions, relay, interest, replication, saves, teams, economy, admin API + GUI, all driven by FakeNode; mod net core headless | no | SRV §8 M1 exit criteria 1–6 (30-min `swarm --clients 8 --verify` with zero errors; GUI kick/ban/mute/broadcast/settings/save upload/session start-stop all audited; slow-reader + fuzz harmless; CI green on both OSes; teams join modes/presets/relations ≤ 1 s/foreign commands always rejected; 30-min economy swarm with clean auditor). **Plus:** C++ headless client (mod `core/`) completes handshake, heartbeat, resume and an in-band save download against the real server in Windows CI. |
| **M2-spike** | In-game experiments S1–S8 (parallel with M1) | yes | Each spike has a recorded verdict (pass / fallback chosen) in `decisions.md` Part 3, and any ADR changed by a failed spike is updated before M3 starts. |
| **M2** | Mod loads via X4Native, connects, join dialog, heartbeat, save sync + load, resume across extension reload, self-test | yes | From the start menu, a player joins using only the UI: handshake, in-band save download, `LoadGame`, paused at universe ready, `NodeReady`; the server shows **no** leave/join across the extension reload. Build mismatch is refused with a clear message. 30-min connection with < 0.2 ms/frame main-thread net cost. Self-test PASS table logged and visible in the GUI. Password never persisted. |
| **M3** | Teams + avatars; player positions and player ghosts both ways; chat; save hygiene v1 | yes (2 PCs) | MOD §9 M3 acceptance: two players fly together 30 min over ≥ 5 sectors incl. a highway, each in their own avatar, seeing the other with correct model, team colour and name; ghost error < 50 m below 500 m/s; no Game Over; < 10 log lines/s; < 1 FPS mod cost. Authority checkpoint contains zero `[MP] ` objects (`tools/savescan`) and avatars persist under team factions after reload. Relations from the GUI matrix visible in game (targeting colour). |
| **M4** | Authority world streaming, manifest matching, server interest/prefetch, ghost-only clients, record/replay | yes (2 PCs) | MOD §9 M4 acceptance: ≥ 99% station matches; same NPC ships on client and authority in current and adjacent sectors; p95 error < 25 m within 5 km; busy sector (≥ 200 ships) ≥ 45 FPS client, < 3 ms/frame authority mod cost; < 200 kB/s per client; zero `spawn-near-player` pops on a highway transit; no despawn/respawn of the same net_id within 10 s while in interest. UDP realtime lane active in the mod. |
| **M5** | Events + in-game economy: kills, death/respawn, trade/cargo, station builds, capture, live relations, wallet reconciliation, transfers/pool, (loans/trades stretch), damage relay (stretch) | yes (2 PCs) | MOD §9 M5 acceptance items 2–12; every event in REQ §8.3 checklist executed once with log evidence; ledger auditor clean after a 60-min session with purchases on all nodes. |
| **M6** | Packaging and hardening: installer, Check Install, Collect Logs, launcher (`launch.json`), HTTP save fallback in mod, release pipeline, user docs; loans/trades in game if not in M5; team asset commands from our UI | partly | One-zip install on a clean Windows PC passes the health check (Protected UI, VC++ redist, MotW, `version_db`, extensions enabled); release tag produces signed-checksum artifacts for server and mod; 4-hour soak with 3 clients and reconnects without leaks. |

---

## 2. M0 tasks (ordered)

| ID | Track | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|---|
| M0-01 | C | Repo scaffolding: `README.md`, `.gitignore` (ignores `reference/`, `x4-unpacked/`, build dirs), `.editorconfig`, `global.json` (SDK 10.0.400, rollForward latestFeature), `Directory.Build.props` (net10.0, nullable, warnings as errors, deterministic), `Directory.Packages.props`, `X4MP.sln` with all empty projects from architecture §12, lock files | — | `dotnet build` and `dotnet test` pass on Windows and Linux with zero warnings; project-reference direction test (Protocol ← Core ← Transport/Persistence ← Server) passes | M |
| M0-02 | P | Pin `flatc` in `tools/flatc/` (version + SHA-256, fetch script for Windows/Linux); compile all `protocol/schema/*.fbs` | M0-01 | `flatc --cpp --csharp` succeeds for every schema file; script fails on checksum mismatch | S |
| M0-03 | P | Apply schema delta list ADR-036 (D1–D11) and fix any compile issues found in M0-02; set `ProtocolMajor=0`, `ProtocolMinor=1`, port constants 47780/47781/47790 | M0-02 | Schema compiles; `MsgType` ids ascending and unique; protocol.md §20 catalog updated for new ids (0x0115, 0x0815, 0x0403 reserved) | M |
| M0-04 | P | `X4MP.Protocol` (C#): MSBuild target running `flatc --csharp` into `obj/`; `FrameCodec` (8-byte header, `MaxFrameBytes` check before allocation, lane byte), `MsgType`→decoder registry, guarded decode mapping failures to `ProtocolViolation` | M0-03 | Round-trip test for every message type; truncated/oversized/zero-length frames raise `ProtocolViolation`, never `IndexOutOfRange`; split-across-reads framing test | M |
| M0-05 | P | `ReplicationCodec` + `Quantize` (C#) per PROTO §10.2/§11; UDP datagram header codec (24 B) | M0-04 | Byte-exact vectors for each mask combination, angle wrap, coarse velocity, EXT skip; NaN/∞ rejected | M |
| M0-06 | P | Golden vector generator (C# console in tests) → `protocol/testdata/*.bin` + index JSON; CI regeneration check | M0-04, M0-05 | Vectors for every `MsgType` + codec cases are committed; regeneration produces no diff | S |
| M0-07 | P/N | C++ protocol: CMake custom command `flatc --cpp --scoped-enums`; `protocol/cpp/x4mp_wire.h` (frame/datagram headers, ReplicationCodec, quantisation, HMAC interface); Catch2 test decodes every golden vector with `Verifier` and re-encodes identically | M0-06 | Test passes on `windows-latest` MSVC; any C#/C++ byte difference fails CI | M |
| M0-08 | N | Mod skeleton: `mod/CMakeLists.txt` + presets (msvc-x64 debug/release/relwithdebinfo), vcpkg manifest (catch2, json), vendored X4Native `v9.0.0-611726` (SDK, runtime, `version_db`, LICENSE, VERSION with hashes), `native/core/` module stubs + tests, `x4mp.dll` stub with `X4N_EXTENSION` hello log, `extension/x4mp/` with `content.xml`/`x4native.json`, `RemoveComponent` grep guard | M0-01, M0-07 | `cmake --build --preset msvc-x64-relwithdebinfo` produces `x4mp.dll`; `core/` tests build with no X4 SDK on the include path; grep guard fails a planted violation; packaging check fails if `version_db/internal_functions.json` lacks `900-611726` | M |
| M0-09 | W | Web scaffold: Vite + React 18 + TS, router, login page + layout shell, ESLint, Vitest, Playwright config, dev proxy to 47790 | M0-01 | `npm run lint/typecheck/test/build` pass; `dist/` produced | M |
| M0-10 | S | Embed SPA into `X4MP.Server` (`BuildWeb` target, `ManifestEmbeddedFileProvider`, SPA fallback, cache headers), `/healthz`, HTTP bind 47790 | M0-01, M0-09 | `dotnet run` serves the SPA from embedded resources with `web/dist` deleted; deep link `/players` returns `index.html` | S |
| M0-11 | S | Single-file publish (win-x64, linux-x64) + CLI verbs `run`, `version`; data-dir resolution | M0-10 | One exe from an empty folder creates `./data` and serves the GUI; `version` prints server version, protocol range, build hash | S |
| M0-12 | S | Windows service: `AddWindowsService`, `service install/uninstall`, `NT SERVICE\X4MP` account, `%ProgramData%\X4MP` | M0-11 | On a Windows VM the service starts, serves the GUI, stops gracefully ("shutdown complete"), uninstalls | S |
| M0-13 | S | Serilog: console, rolling file, RingBufferSink, redaction policy | M0-01 | Logs in console and `data/logs`; tests for ring cap and `Authorization` redaction | S |
| M0-14 | S | Persistence foundation: connection factory (WAL pragmas), migration runner, `0001_init.sql` (SRV §2.8 schema + `journal`, `string_table`, `checkpoints` tables), `PersistenceWriter` batching | M0-01 | Empty dir creates DB at v1; restart idempotent; 1,000 batched inserts in one transaction | M |
| M0-15 | W/S | `TsContractGenerator` + `generated.ts` drift check | M0-01, M0-09 | Adding a DTO property without regenerating fails CI | S |
| M0-16 | C | CI: `ci.yml` jobs `web`, `dotnet` (windows + ubuntu), `protocol` (flatc + golden vectors C#/C++), `mod` (windows: CMake build, Catch2, grep guard, XML well-formedness, luacheck), `e2e` smoke (published exe → `/healthz`); `release.yml` skeleton; Dependabot; CodeQL (C#, JS, C++) | M0-04..M0-13 | PR shows green checks on both OSes; a formatting break, a failing test or a vector mismatch fails CI | M |

**M0 critical path:** M0-01 → M0-02 → M0-03 → M0-04 → M0-05 → M0-06 → M0-07 → M0-08 → M0-16.
Web/server shell (M0-09…M0-15) runs in parallel after M0-01.

---

## 3. M1 tasks (ordered within each track)

Naming follows the canonical catalog (PROTO §20, ADR-027). Server task numbers map to
SRV §8 where noted.

### 3.1 Networking and core (server)

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-01 | `INodeListener`/`INodeConnection`, `SendQueue` (3 lanes, coalescing, watermarks 64/256 KiB, Control caps 8/32 MiB + 15 s age, pull hint), writer loop (SRV M1-1) | M0-04 | Priority order, coalesce-replace, `DroppedLane`, `SlowConsumer` close tests; `TrySend` allocation-free < 200 ns; non-reading peer never blocks producers | M |
| M1-02 | TCP transport (Kestrel `ConnectionHandler` on 47780) + InProc transport (SRV M1-2) | M1-01 | Echo test over both; 1,000 connect/disconnect cycles leak no handles | M |
| M1-03 | `NodeGateway`: `ServerHello`/`ClientHello`/`Welcome`/`Disconnect`; HMAC auth (session/admin), `player_key` identity + name binding, ban (key hash, CIDR), per-IP limit, 10 s timeout, **pinned-build check** (`supported_game_builds`, equality with authority, mod build equality, extensions hash) (SRV M1-3) | M1-02, M0-14 | Integration: success, wrong password (1 s delay), protocol major mismatch, unsupported game build, mod build mismatch, extensions mismatch, name taken, banned key/CIDR, silent 11 s close, 5th connection per IP refused | M |
| M1-04 | `MessagePolicy` generated from the catalog (role × phase × lane) + violation counting + temp IP ban (SRV M1-4) | M1-03 | Every `MsgType` has an entry (reflection test); client `WorldUpdate` ⇒ violation ⇒ close after threshold | S |
| M1-05 | `SessionActor`: `SessionPhase`/`NodePhase` machines, Ping/Pong RTT + clock offset, `NodeStats` ingest, resume token (60 s), `ClientReload` handling, `AuthorityLost` grace 120 s, `TimeProvider` (SRV M1-5) | M1-03 | Table-driven transition tests on a fake clock; authority drop → `AuthorityLost` → `Running` on resume, `Stopping` after grace; client resume keeps player row and gets baselines reset; `ClientReload` produces no leave/join event | L |
| M1-06 | World mirror (`net_id` keyed, persistent + hot + player ships), `GalaxyMetadata` cache by save sha, `SectorGraph` k-hop cache, string table, **journal** with `SaveStarted` marker and compaction (SRV M1-6) | M1-05 | 20k entities at 20 Hz with zero steady-state allocations (benchmark); journal entries after marker replay in order; compaction drops entries before the previous checkpoint | L |
| M1-07 | Interest manager (Near grid, Sector, Adjacent prefetch, Linger), `InterestUpdate`, `CaptureSet` union (≤ 1/500 ms, admin views), `SectorComplete` gating/forwarding, `InterestHint` capability handling, ghost budget policy (SRV M1-7) | M1-06 | Gate crossing yields correct spawn/despawn sets with no despawn of the promoted sector; Linger holds 20 s; an uncaptured sector is forwarded only after the authority's `SectorComplete`; over-budget drops XS/S/M from Adjacent first | L |
| M1-08 | Replication: priority accumulator, byte budget, field-mask entries vs per-client baselines advanced on TCP flush, keyframes 5/15 s, tombstones, spawn-before-state hold, `InterestChecksum` + `ResyncRequest` (SRV M1-8) | M1-07, M1-01 | `--verify` clients: 0 position errors over 5 min; budget lowered to 32 KB/s keeps Near ≥ 10 Hz; dropped realtime frames cause no verify errors; checksum mismatch triggers resync | L |
| M1-09 | UDP realtime lane on 47781: `UdpHello` binding with token, NAT rebind, `seq/ack/ack_bits`, ack-driven baselines with the "unacked in-flight differs" rule, 3 s fallback to TCP | M1-08 | `--udp --loss 5%` verify run has 0 errors; rebind from a new source port works; without UDP the session falls back within 3 s. *May slip to M4 without blocking M1 exit.* | M |
| M1-10 | Relay: `PlayerState` → derived velocity (3-sample slope) → others + authority 20 Hz; `PlayerShip` → authority → `EntitySpawn{controller_player}` (avatar flow); `Intent` → authority with 5 s timeout and exactly one `IntentResult`; `GameEvent` fan-out by interest; chat channels incl. Team, mute; all persisted (SRV M1-9) | M1-05, M1-06 | Chat A reaches B, C but not after mute; `KillClaim` reaches only the authority; authority `GameEvent` reaches interested clients and `session_events`; silent authority ⇒ `Rejected{Timeout}` | M |
| M1-11 | Event bus + subscribers (persistence, audit, alert evaluator) (SRV M1-10) | M1-05, M0-14 | Publishing never blocks with a stalled subscriber; alert fires for authority FPS < 15 for 30 s (fake clock) | S |
| M1-12 | Save service: `RequestSave` cadence (15 min), `SaveStarted` marker, in-band upload of save + manifest, `ghosts_cleaned` gating, content-addressed store + sniff, in-band windowed download with resume, HTTP fallback `GET /files/saves/{sha}` with tokens + Range, `ManifestReport` policy (> 0.5% ⇒ `ManifestMismatch`), `WorldCatchUp`, `join_checkpoint_policy`, janitor (SRV M1-11) | M1-05, M1-06, M0-14 | 200 MB fake save authority → server → 3 clients verified; kill at 50% resumes (< 60% re-sent); hash mismatch rejected; non-gzip ⇒ 422; `ghosts_cleaned=false` never current; catch-up replays journal since checkpoint; other clients' realtime p99 +≤ 10 ms during transfer | L |
| M1-13 | Metrics: `Meter` instruments, `ConnectionStats`, `MetricsSampler` ring buffers (SRV M1-12) | M1-01 | `dotnet-counters` shows instruments; `/diagnostics/metrics` returns 600-sample series | S |
| M1-14 | Configuration: options with `[Setting]`, SQLite overrides provider + reload, schema endpoint, `SessionSettings` push (SRV M1-13) | M0-14 | `TickRateHz` PATCH effective < 1 s; invalid value ⇒ 400 per key; boot-only key ⇒ "restart required"; audited | M |

### 3.2 FakeNode

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-F1 | FakeNode core: deterministic galaxy + world (pure function of seed, tick), fake authority (honours `CaptureSet`, `SectorComplete` after "index pass", net_id allocation, checkpoints with fake save + manifest, `GalaxySummary`, `NodeStats`), fake clients (download/verify save, `PlayerState` 20 Hz, behaviours wander/patrol/explore), `--verify` (positions, interest, events, checksum) | M0-04, M1-03 | `swarm --clients 8 --verify` 10 min with zero violations against the server | L |
| M1-F2 | Failure injection: `--slow-reader`, `--latency/--loss`, `--disconnect-every` (resume), `--reload-every` (`ClientReload`), `fuzz`, `inspect` | M1-F1 | Fuzz 60 s leaves the server up with violations counted; slow reader affects only itself; reconnect resumes with keyframes | M |
| M1-F3 | Team behaviours: `--team`, `--team-pick lobby-random`, `--commander shared|own|foreign`, matrix-aware fake NPC hostility, `ReassignPlayerAssets` | M1-F1, M1-T3 | Foreign orders rejected 100% with nothing forwarded | M |
| M1-F4 | Economy behaviours: `--economy idle|casual|heavy`, `--dupe-attack`, `--loan-default`, authority `--income-rate`, `--trade-fail`, `--trade-timeout` (withholds confirms, answers `TradeQuery`), per-node `CreditDelta{seq}` reconciliation model | M1-F1, M1-E5 | 10-min `swarm --teams 3 --relations ffa --economy heavy --dupe-attack --trade-fail 10% --trade-timeout 5% --verify`: 0 invariant violations, 0 duplicate effects, every InDoubt trade resolved or listed | M |

### 3.3 Teams (server)

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-T1 | Team domain: `Team`, membership (sticky), `TeamRelationMatrix` (symmetric, versioned), presets (Co-op, AlliedSeparate, FreeForAll, TwoTeams), faction-slot allocator **1..8**, persistence, `TeamOptions` with ADR-017 defaults | M1-05, M0-14 | Presets produce documented tables; matrix symmetric + versioned; slot exhaustion ⇒ `NoFactionSlot`; memberships survive restart | M |
| M1-T2 | Join-time assignment: `AwaitingTeam`, Auto (SingleTeam/Balance/NewTeamPerPlayer), Lobby (`TeamChoice`, `TeamCreateRequest`, locked/full/HMAC password, 300 s fallback), AdminAssign, authority-must-have-team gate | M1-T1, M1-03 | Each mode places 6 FakeNode clients as expected; wrong team password/locked team rejected; AdminAssign client gets no save or replication until assigned; reconnect skips `AwaitingTeam` | M |
| M1-T3 | Team fan-out: `TeamTable`, `TeamRelations`, `SessionSettings{TeamPolicy}`, `TeamMemberChanged`, `RelationChangeRequest`/`RelationProposal` per policy, `ReassignPlayerAssets` + client resync on move | M1-T1, M1-10 | Relation change reaches every FakeNode ≤ 1 s and fake NPC hostility follows; moving the authority's player while Running ⇒ 409 | M |
| M1-T4 | `AssetPermissionPolicy`: mirror `owner_team`/`owner_player` from `EntitySpawn/Change`, gating of `AssetOrder`, `AssetRename`, `AssetGift`, `StationBuildRequest`, `TradeReport`, `KillClaim`/`HitReport` (range, hostility, friendly fire), `CaptureReport`; `IntentResult{Rejected}` + `PermissionDenied` | M1-T1, M1-06, M1-04 | Table-driven test covers every policy × relation × action cell; `--commander foreign` 100% rejected, 0 forwarded | M |
| M1-T5 | Teams REST + hub + **Teams & Factions page** (cards, drag-and-drop + keyboard, relation grid, presets with confirm, join mode, unassigned badge) | M1-T1..T4, M1-W1 | Playwright: Free-for-all with 4 bots ⇒ 4 hostile teams; drag Bot2 to Bot1's team ⇒ pushed ≤ 1 s; Allied cell applied ⇒ `TeamRelationsChanged`; AdminAssign bot appears under Unassigned and downloads the save after assignment | L |

### 3.4 Economy (server)

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-E1 | Ledger core: wallets (Player, TeamShared, TeamPool, Escrow, World), double-entry transactions, synchronous commit, append-only triggers, `economy_requests` idempotency + payload hash, `EconomyAuditor` + freeze | M0-14, M1-05 | 100k-op property test keeps Σ = 0; kill -9 after ack loses nothing; replay returns identical result; reuse with different payload rejected; injected corruption ⇒ freeze | L |
| M1-E2 | Credit modes: Auto/PerPlayer/Shared resolution, pool + withdraw policy/daily limit, migrations on mode switch/team move (confirm + preview), `CreditDelta` booking incl. `seq` dedupe and `acked_delta_seq` in `WalletUpdate`, team-asset income, overdraft flag, `StartingCredits`, `inherit_team` seeding | M1-E1, M1-T1 | Co-op preset in Auto ⇒ one shared wallet with exact sum; back to 2 teams splits with remainder to pool; Running switch without confirm ⇒ 409 + preview; overdrawn wallet blocks outgoing requests; duplicate `CreditDelta{seq}` booked once | M |
| M1-E3 | Donate, transfer and pool actions with scopes Off/Teammates/Allied/Anyone, rate limit, validation rules | M1-E2, M1-T3 | Scope × relation table test; Hostile donation with `Allied` scope ⇒ `ScopeDenied`, succeeds after relation becomes Allied; 6th request in 10 s ⇒ `RateLimited` | M |
| M1-E4 | Loans: offer with principal escrow, accept/decline/cancel/expiry, repay (partial), `repay_total` cap, optional auto-repay % from income, real-time due + overdue timer, admin forgive/cancel | M1-E1, M1-E3 | Every transition tested on a fake clock; decline/expiry refunds escrow exactly; 25% auto-repay of 1,000 income repays 250; overdue visible in hub ≤ 10 s | M |
| M1-E5 | Trades: proposal/counter/accept on the same version, credit escrow, asset locks (unique index), proximity, per-trade `AssetTransferOrder{lines[]}` to authority, `AssetTransferConfirm`, 30 s timeout → `TradeQuery` ×3 → `InDoubt`, admin resolve/cancel, rollback, settle-once, ownership update | M1-E1, M1-T4 | `--trade-fail 10%`: all failures refunded and unlocked; `--trade-timeout 5%`: trades go InDoubt and are resolvable; duplicate confirms settle once; second trade on a locked ship rejected; completed ship trade updates mirror and `team_assets` | L |
| M1-E6 | Economy admin REST + hub (wallets, ledger + CSV, adjust, freeze, reverse with `WouldOverdraw`/`AlreadyReversed`/`force`, loans, trades, summary, events, policy), all audited | M1-E1..E5, M1-S1 | Happy + error path per endpoint; reversal links both transactions, second ⇒ 409; frozen wallet rejects requests while `CreditDelta` still books; `audit_log` row with reason for every admin money action | M |
| M1-E7 | **Economy page** (overview, wallets, live transactions, loans, trades, policy with migration preview, drawers, badges, auditor banner) | M1-E6, M1-W1 | Playwright with `--economy casual`: live transactions; GUI reversal restores balances; freezing Bot3 makes its next donation fail; overdue badge; InDoubt "refund" restores payer | L |

### 3.5 Admin API and GUI

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-S1 | Admin auth: bootstrap admin (initial password file), login/logout/me/change-password, cookie, `X-X4MP` CSRF header, bearer tokens, roles, login rate limit, private-network allow-list, security headers (SRV M1-15) | M0-14 | 401/403/400/429 cases; forced first password change; public IP ⇒ 403 | M |
| M1-S2 | REST endpoints (SRV §4.4 excluding team/economy groups and 501 stubs) + ProblemDetails (SRV M1-16) | M1-05..M1-14, M1-S1 | Happy + error test per endpoint; kick/ban of an online bot disconnects ≤ 1 s; ban blocks reconnect | L |
| M1-S3 | `AdminHub` + `AdminBroadcaster` (dashboard, galaxy from `GalaxySummary`, sector 4 Hz with admin interest, logs, diagnostics, chat, transfers, alerts) (SRV M1-17) | M1-S2 | SignalR test receives each push type; empty groups cost nothing; sector subscription makes the fake authority capture that sector | M |
| M1-W1 | GUI shell: auth gate, `HubProvider`, layout, alerts, theme (SRV M1-18) | M0-09, M1-S3 | Login, forced change, logout; hub auto-reconnects after server restart | M |
| M1-W2 | Dashboard (SRV M1-19) | M1-W1 | Tiles/table live with a swarm; sparklines backfilled after reload | M |
| M1-W3 | Players + detail (kick, mute, ban, unban, notes, release name) (SRV M1-20) | M1-W1 | Playwright kick/ban/unban flow | M |
| M1-W4 | Map: galaxy canvas + sector canvas (SRV M1-21) | M1-W1 | Markers move; sector view shows entities; interest overlay follows jumps; ≥ 50 FPS at 3,000 entities (reported) | L |
| M1-W5 | Sessions & Saves page (SRV M1-22) | M1-W1, M1-12 | Resumable 50 MB upload survives reload; starting a session makes bots download that save | M |
| M1-W6 | Chat, Logs, Settings, Diagnostics pages (SRV M1-23..26) | M1-W1, M1-13, M1-14 | Per SRV acceptance (admin broadcast reaches bots; log source filter; MaxPlayers 2 ⇒ third bot `SessionFull`; slow-reader visible in diagnostics) | L |

### 3.6 Mod net core (headless, parallel track)

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-N1 | `core/log` (async, rate-limited per call site) + `core/config` (defaults → user file → one-shot `launch.json`; no env) + `core/queue` (SPSC inbox, reliable outbox, latest-wins state slots, MPSC `md_ring`) | M0-08 | Unit tests incl. queue contention; invalid config value falls back with an error line | M |
| M1-N2 | `core/net`: `net` thread, non-blocking Winsock + `WSAPoll`, connect handling `WSAEWOULDBLOCK` (PIT-022), framing + `Verifier`, Ping/Pong, clock sync (min-RTT of 16), reconnect backoff 1/2/4/8/10 s, 8 MB write-buffer cap | M1-N1, M0-07 | Windows CI connects to a real server socket; 30-min soak against the M1 server; server kill/restart ⇒ reconnect ≤ 5 s | M |
| M1-N3 | `core/session`: handshake (HMAC via BCrypt), `Welcome` handling, resume token, `ClientReload`, in-band save download to a temp dir with SHA-256 verify; headless test client `x4mp-headless` | M1-N2, M1-03, M1-12 | In CI: headless client joins a FakeNode-authority session, downloads the save, sends `SaveReady`/`NodeReady`, survives a simulated reload with no leave/join on the server | M |

### 3.7 CI, load and docs

| ID | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|
| M1-C1 | E2E CI job: published server + `fakenode swarm --verify --teams 3 --economy casual --dupe-attack` + Playwright suite + headless mod client (SRV M1-27) | M1-W2..W6, M1-T5, M1-E7, M1-N3 | Green < 10 min; artifacts on failure | M |
| M1-C2 | Load harness + nightly (1 authority, 16 clients, 20k entities): tick p99 < 15 ms, CPU, memory, bytes/client (SRV M1-28) | M1-08, M1-F1 | Nightly job reports numbers and fails above budget | M |
| M1-C3 | Docs: `docs/server-admin.md` (install, ports 47780/47781/47790, firewall, service, first login, LAN/VPN) and `docs/fakenode.md` (SRV M1-29) | M1-S2, M1-F2 | A teammate runs server + swarm from the docs alone | S |

**M1 critical path:** M1-01 → M1-02 → M1-03 → M1-05 → M1-06 → M1-07 → M1-08 (with M1-F1
growing alongside) → M1-T1/M1-E1 → M1-T4/M1-E5 → M1-S2/S3 → M1-W1 → pages → M1-C1.

---

## 4. M2-spike: in-game experiments (run in parallel with M1)

A throwaway extension (`mod/spikes/`, not shipped) on the pinned build, single PC unless
noted. Each spike records: procedure, measurements, verdict, fallback chosen, and updates
`decisions.md` Part 3. Ordered by risk.

| ID | Experiment (verifies) | Procedure | Pass criterion | If it fails |
|---|---|---|---|---|
| S1 | Team factions in an existing save (V01, V08) | Install a `factions.xml` diff with `x4mp_team_1..8` (`active="0"`) and `colors.xml` diff; load a campaign save made **before** install; MD `set_faction_active`; `SpawnObjectAtPos2(macro, sector, pos, "x4mp_team_2")`; set + lock relations to `player`; check targeting colour and police reaction | Faction exists, activates, spawn returns non-zero, colour/relations correct; survives save + reload | ADR-015 fallback (borrowed factions, mirror avatars, all allied); revise ADR-014 |
| S2 | Avatar takeover (V02) | Spawn a `player`-owned S fighter near the player; `CanTeleportPlayerTo` then `TeleportPlayerTo(ship, true, true, true)`; then suppress the original ship via the guarded remove path; also test while docked | Player controls the new ship, no Game Over, original ship removable once vacated | Mirror model; keep players in the save ship (single-ship identity) |
| S3 | Ghost cost (V03, V16, V17) | Spawn 100/250/500 ships in one sector at 40/frame; `ActivateObject(false)`; drive with `SetObjectSectorPos` each frame on scripted paths; measure ms/frame, stutter, engine trails, collisions; destroy via `SelfDestructComponent` vs MD `destroy_object`; read `GetComponentData(id,"velocity")` | ≤ 1.5 ms/frame at 250 ghosts; ghosts stay inert; smooth visuals | Lower `max_ghosts`; far ghosts at 1–5 Hz; MD `set_object_active` |
| S4 | Money API (V04) | Read `GetPlayerMoney()` vs HUD; `AddPlayerMoney(+1000)`, `(-1000)`, overdraw below 0; MD `transfer_money` player ↔ `x4mp_team_1` ↔ NPC; observe `event_player_money_updated` per purchase type | Units known; negative deltas work; behaviour at < 0 known; event fires for every change | Clamp local money at 0 + team debt; MD-only writes; polling only |
| S5 | Extension reload survival (V05, V07) | Connect a spike net thread to the M1 server (or a stub) from the start menu; load a save; log `X4N_SHUTDOWN`/re-init order, DLL unload, stash contents, thread join time; log thread id of MD typed callbacks | Stash survives; resume invisible to server; join ≤ 200 ms | Pin the module (`GetModuleHandleEx` pin) and keep the socket across re-init |
| S6 | Save control (V06) | Wrap Lua `SaveGame`/`IsSavingPossible`; try manual save, quicksave, autosave with the MD diff; time `SaveGame` (late-game save); spawn ghosts, strip in the same frame as `SaveGame`, restore after `on_game_save`; scan with `tools/savescan` | Client saving fully blockable; authority save contains zero `[MP] ` objects; hitch measured | X4Native `hook_before` on the save export |
| S7 | Sector list and graph (V09, V10) | MD `find_sector multiple="true"` → Lua → JSON; macro via `GetComponentData`; gate/highway links; time a full `GetAllFactionShips` pass | All sectors (≥ 140 with DLC) with macros and links; enumeration cost known | Static galaxy table from `maps/`; longer index period |
| S8 | Cargo + ownership shim (V13, V14) | `AddUITriggeredEvent` → MD `add_cargo/remove_cargo exact=` on a station and a ship with `result`; `SetComponentOwner` on a ship with crew/orders/subordinates; measure Lua→MD latency | Bulk cargo works with results; owner change clean or cleanup steps known | Restrict trades to ships without subordinates; batch shim calls |
| S9 | Story progression and unlocks (ADR-037) | Map how vanilla stores plot progress and universe unlocks: md/ story and plot scripts (e.g. the Boron storyline), gate/sector activation, `known` flags, faction/feature unlocks. Try applying an unlock on a "client" save through MD (open a locked gate/sector, set a plot flag), and check that it survives a save/reload. | Catalogue of unlock types with read+apply paths; at least the sector/gate unlock can be applied through MD | Authority-only story; clients get unlocks only through the next save checkpoint (rejoin) |

S1, S2, S4 and S5 gate M3/M5 scope and should report before M1's economy and team tasks
finish so that ADR-014/015/019 can be confirmed or revised.

## 5. Post-v1 backlog (from user decisions 2026-10-01)

- **Shared story/unlocks (ADR-037):** `StoryState`/`UnlockEvent` protocol messages, authority-side unlock capture, client-side apply, join-time snapshot, per-team mission progress. Target M5–M6, depending on S9.
- **Fog of war (ADR-038):** `FogOfWar` session option; per-team visibility filter in the server interest manager (keep the hook from M1 on).
- **Starting credits GUI (ADR-039):** presets + custom in Sessions → Settings (fold into M1 economy tasks).
- **Loan enforcement (ADR-040):** `LoanEnforcement` policy, with AutoCollect / Penalty / Diplomacy (reputation loss, relation drift) / admin seizure. The M1 ledger must already record due, overdue and default events.
