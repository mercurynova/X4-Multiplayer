# X4MP Roadmap

Status: v1.0, 2026-10-01. Follows [`architecture.md`](architecture.md) and
[`decisions.md`](decisions.md). Sizes: **S** ≤ 1 day, **M** 2–3 days, **L** 4–6 days (one
developer). "Deps" lists task ids that must be done first. Tracks: **P** protocol,
**S** server, **W** web GUI, **N** native mod, **F** FakeNode/test tooling, **C** CI.

---

## 1. Milestones

| # | Milestone | Needs X4? | Exit criteria |
|---|---|---|---|
| **M0** | Repo skeleton, build, CI, protocol schema v0.1 compiled with codegen in C# and C++ | no | Fresh clone builds and tests on Windows + Linux (server) and Windows (mod core). `flatc` compiles every `.fbs` with the ADR-036 deltas applied. Golden vectors encoded by C# decode in C++ with identical field values; frame/datagram headers and Replication entries are byte-identical (FlatBuffers tables may differ in vtable layout, ADR-041). Published `x4mp-server.exe` serves the empty GUI shell and `/healthz` on 47790. CI green, Dependabot + CodeQL on. |
| **M1** | Server: handshake, sessions, relay, interest, replication, saves, teams, economy, admin API + GUI, all driven by FakeNode; mod net core headless | no | SRV §8 M1 exit criteria 1–6 (30-min `swarm --clients 8 --verify` with zero errors; GUI kick/ban/mute/broadcast/settings/save upload/session start-stop all audited; slow-reader + fuzz harmless; CI green on both OSes; teams join modes/presets/relations ≤ 1 s/foreign commands always rejected; 30-min economy swarm with clean auditor). **Plus:** C++ headless client (mod `core/`) completes handshake, heartbeat, resume and an in-band save download against the real server in Windows CI. **Plus:** mod policy (M1-X1..X5): a FakeNode with a mod mismatch is refused with the exact install/enable/disable/update lists and links, visible on the GUI Mods page. |
| **M2-spike** | In-game experiments S1–S8 (parallel with M1) | yes | Each spike has a recorded verdict (pass / fallback chosen) in `decisions.md` Part 3, and any ADR changed by a failed spike is updated before M3 starts. |
| **M2** | Mod loads via X4Native, connects, join dialog, heartbeat, save sync + load, resume across extension reload, self-test; minimal real authority (admin-uploaded save, checkpoints). **Plan: [m2-plan.md](m2-plan.md)** (18 exit criteria) | yes (1 PC) | From the start menu, a player joins using only the UI: handshake, in-band save download, `LoadGame`, paused at universe ready, `NodeReady`; the server shows **no** leave/join across the extension reload. Build mismatch is refused with a clear message. 30-min connection with < 0.2 ms/frame main-thread net cost. Self-test PASS table logged and visible in the GUI. Password never persisted. |
| **M3** | Teams + avatars; player positions and player ghosts both ways; chat; save hygiene v1 | yes (2 PCs) | MOD §9 M3 acceptance: two players fly together 30 min over ≥ 5 sectors incl. a highway, each in their own avatar, seeing the other with correct model, team colour and name; ghost error < 50 m below 500 m/s; no Game Over; < 10 log lines/s; < 1 FPS mod cost. Authority checkpoint contains zero `[MP] ` objects (`tools/savescan`) and avatars persist under team factions after reload. Relations from the GUI matrix visible in game (targeting colour). |
| **M3b** | On-foot presence v1 (ADR-046): Tier 0 HUD presence list ("Alice is on this station, in the bar") + Tier 1 MP lounge (fixed-layout room built from vanilla room macros, remote players as temporary actors with model/name/team, vanilla conversation Talk menu: message, wave). Includes chosen avatar appearance (ADR-051: race + variants, default from team origin). | yes (2 PCs) | Two players in the lounge see each other at the correct position (error < 0.5 m) and heading, with walk/idle animation; Talk menu works both ways; HUD list correct for any shared container; zero MP actors in any save (`tools/savescan`); a save from a lounge session loads without the mod. Gated on spikes S10.1–S10.5, S10.7, S10.8, S10.11–S10.15. |
| **M3c** | On-foot presence v2: Tier 2 in any shared room/ship, nearest-seat/HUD fallback when rooms don't match | yes (2 PCs) | Gated on S10.2 room matching. Players visible in matched rooms (bar, office, dock, bridge); unmatched rooms fall back without errors. |
| **M4** | Authority world streaming, manifest matching, server interest/prefetch, ghost-only clients, record/replay | yes (2 PCs) | MOD §9 M4 acceptance: ≥ 99% station matches; same NPC ships on client and authority in current and adjacent sectors; p95 error < 25 m within 5 km; busy sector (≥ 200 ships) ≥ 45 FPS client, < 3 ms/frame authority mod cost; < 200 kB/s per client; zero `spawn-near-player` pops on a highway transit; no despawn/respawn of the same net_id within 10 s while in interest. UDP realtime lane active in the mod. |
| **M5** | Events + in-game economy: kills, death/respawn, trade/cargo, station builds, capture, live relations, wallet reconciliation, transfers/pool, (loans/trades stretch), damage relay (stretch) | yes (2 PCs) | MOD §9 M5 acceptance items 2–12; every event in REQ §8.3 checklist executed once with log evidence; ledger auditor clean after a 60-min session with purchases on all nodes. |
| **M5b** | Talk-menu actions on remote players: send credits, invite to team, trade (uses M5 economy + teams) | yes (2 PCs) | Each action executes through the server economy/team APIs with an audit trail; works from the lounge and Tier 2 rooms. |
| **M6** | Packaging and hardening: installer, Check Install, Collect Logs, launcher (`launch.json`), HTTP save fallback in mod, release pipeline, user docs; loans/trades in game if not in M5; team asset commands from our UI | partly | One-zip install on a clean Windows PC passes the health check (Protected UI, VC++ redist, MotW, `version_db`, extensions enabled); release tag produces signed-checksum artifacts for server and mod; 4-hour soak with 3 clients and reconnects without leaks. |

---

## 2. M0 tasks (ordered)

**Status (2026-10-01): all M0 tasks merged to main.** Pending: the first GitHub Actions run, which proves the Linux legs, vcpkg caching and the e2e smoke.

| ID | Track | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|---|
| M0-01 | C | Repo scaffolding: `README.md`, `.gitignore` (ignores `reference/`, `x4-unpacked/`, build dirs), `.editorconfig`, `global.json` (SDK 10.0.400, rollForward latestFeature), `Directory.Build.props` (net10.0, nullable, warnings as errors, deterministic), `Directory.Packages.props`, `X4MP.sln` with all empty projects from architecture §12, lock files | — | `dotnet build` and `dotnet test` pass on Windows and Linux with zero warnings; project-reference direction test (Protocol ← Core ← Transport/Persistence ← Server) passes | M |
| M0-02 | P | Pin `flatc` in `tools/flatc/` (version + SHA-256, fetch script for Windows/Linux); compile all `protocol/schema/*.fbs` | M0-01 | `flatc --cpp --csharp` succeeds for every schema file; script fails on checksum mismatch | S |
| M0-03 | P | Apply schema delta list ADR-036 (D1–D11) and fix any compile issues found in M0-02; set `ProtocolMajor=0`, `ProtocolMinor=1`, port constants 47780/47781/47790 | M0-02 | Schema compiles; `MsgType` ids ascending and unique; protocol.md §20 catalog updated for new ids (0x0115, 0x0815, 0x0403 reserved) | M |
| M0-04 | P | `X4MP.Protocol` (C#): MSBuild target running `flatc --csharp` into `obj/`; `FrameCodec` (8-byte header, `MaxFrameBytes` check before allocation, lane byte), `MsgType`→decoder registry, guarded decode mapping failures to `ProtocolViolation` | M0-03 | Round-trip test for every message type; truncated/oversized/zero-length frames raise `ProtocolViolation`, never `IndexOutOfRange`; split-across-reads framing test | M |
| M0-05 | P | `ReplicationCodec` + `Quantize` (C#) per PROTO §10.2/§11; UDP datagram header codec (24 B) | M0-04 | Byte-exact vectors for each mask combination, angle wrap, coarse velocity, EXT skip; NaN/∞ rejected | M |
| M0-06 | P | Golden vector generator (C# console in tests) → `protocol/testdata/*.bin` + index JSON; CI regeneration check | M0-04, M0-05 | Vectors for every `MsgType` + codec cases are committed; regeneration produces no diff | S |
| M0-07 | P/N | C++ protocol: CMake custom command `flatc --cpp --scoped-enums`; `protocol/cpp/x4mp_wire.h` (frame/datagram headers, ReplicationCodec, quantisation, HMAC interface); Catch2 test verifies (`Verifier`) and decodes every golden vector, compares decoded fields with `index.json`, and re-encodes frame/datagram headers and Replication entries byte-identically | M0-06 | Test passes on `windows-latest` MSVC; any field mismatch or header/Replication byte difference fails CI | M |
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
| M1-F4 | Economy behaviours: `--economy idle|casual|heavy`, `--dupe-attack`, `--loan-default`, authority `--income-rate`, `--trade-fail`, `--trade-timeout` (withholds confirms, answers `TradeQuery`), per-node `CreditDelta{seq}` reconciliation model | M1-F1, M1-E5 | 10-min `swarm --teams 3 --relations ffa --economy heavy --dupe-attack --trade-fail 10% --trade-timeout 5% --verify`: 0 invariant violations, 0 duplicate effects, every InDoubt trade resolved or listed | M. **Done (task/m1-f4-economy-behaviours):** `--economy`, `--dupe-attack`, `--loan-default`, `--income-rate`, `--admin-url` (see tools/X4MP.FakeNode/README.md); `EconomyReconciler` per node; Economy page e2e (frozen wallet request fails, overdue badge) |

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

**Follow-ups from M1-S1 review (2026-10-01):** (1) ~~move auth routes from `/api/auth/*` to `/api/v1/auth/*`~~ done in M1-S2; (2) ~~a password change must invalidate the user's other sessions/cookies~~ done in M1-S2 (cookies by `pw_version`, API tokens the user minted are revoked); (3) persist lockout/throttle state only if restarts become an attack vector (currently in-memory, acceptable).

**Follow-ups from M1-05 / M1-N2 review (2026-10-01):**
1. The SessionActor mailbox is unbounded. Before M1-10 relays high-rate traffic, coalesce `PlayerState` per node (latest-wins) and bound per-node inbound queues, so a flooding client can't grow server memory.
2. Settings push has no wire message yet: the protocol `SessionSettings` table is team/economy only. Extend it (or add `ServerSettingsUpdate`) for general live settings such as `ModListVisibility` (schema change → golden vectors).
3. `StopCompleted` has no producer; Stopping→Ended relies on the 30 s timeout. Wire it in M1-12 (save on stop).
4. Authority admission has no team gate yet (M1-T2).
5. Mod (M1-N3): Control frames queued while briefly disconnected must be kept and replayed after resume (the M1-N2 outbox currently discards them).
6. Accepted deviation: the authority's resume window equals the 120 s grace (clients keep 60 s).

**Follow-ups from M1-06/07 review (2026-10-01):** (1) `SqliteWorldStore` finds its session through a GUID in `sessions.settings_json`; switch to `ISessionModule.OnSessionBegun(sessionId)` (added by M1-E2 in parallel). (2) `Welcome.max_ghosts` isn't yet set from `InterestOptions.MaxGhosts`; set it in the gateway/actor Welcome path. (3) The module order (Teams first) is pinned by `TeamsHostingTests`; keep it when adding modules.

**Schema change batch (collect, apply once with golden-vector regen), from reviews 2026-10-01:** (a) `PlayerShip` lacks `player_id` (M1-10 currently stamps it into `request_key.Hi`, a hack to remove); (b) general live-settings push message (M1-05 follow-up 2); (c) `OnFootState` (ADR-046, M3b); (d) extension list in `ClientHello` (ADR-044 phase 1, if not already present). One task updates the `.fbs` files, `protocol.md` §20 and golden vectors, then fixes the C#/C++ users.

**Done (task/m1-schema-batch):** (a) `PlayerShip.player_id` (server-stamped, hack removed); (b) `ServerSettingsUpdate` 0x0116 (full `PushToNodes` set, sent after Welcome and on change; `Interest.MaxGhosts` now `PushToNodes`); (c) `OnFootState` 0x0302 + `RoomKey`/`OnFootMode`/`OnFootAnim`/`RoomKind` (schema, policy, vector only; server accepts and drops; the `OnFootPresence` capability bit is still to add in M3b); (d) `ClientHello.extensions` already existed.
**M1-09 notes (UDP lane built):** server `UdpRealtimeServer` (Transport) + `NetOptions.UdpPort` (0 = off), client `UdpRealtimeClient` (Protocol), FakeNode `--udp --loss PCT`. UDP clients keep many frames in flight with ack-driven per-field baselines (the one-frame cap is gone for them); **TCP-only clients keep the one-frame-in-flight cap** (unchanged, ~10 Hz at 100 ms RTT). Follow-ups: C++ mod UDP side (UdpHello bind, seq/ack, hold unknown-id entries up to 2 s, ignore entries of older frames per ghost); FakeNode `--latency`; NodeStats udp loss for client fakes; TCP N-frames-in-flight.

**Follow-up from M1-08 review (2026-10-01):** replication keeps only one tick's frames in flight per client and skips ticks until the previous one is flushed. On TCP over a high-latency link (VPN/internet, ~100 ms RTT) that caps updates at ~1/RTT (≈10 Hz), below the Near 20 Hz target. M1-09 (UDP, ack-driven) must replace this; if UDP slips, allow N frames in flight on TCP with per-frame baselines.

**Follow-ups from M1-12 review (2026-10-01):** (1) the Stopping timeout stays 30 s; a large late-game final save may exceed it, so make it a setting and/or extend it while a save upload is progressing. (2) ~~The save's `money` is read as cents ÷ 100; verify against a real save.~~ **Resolved 2026-10-02:** the save header money is whole credits (user confirmed 5,447,419 ↔ ≈ 5.4 M Cr in game; see research/save-analyzer-notes.md); the ÷ 100 was a bug, fixed in task/fix-save-money-units. (3) Team HQ seeding (ADR-049) goes through `ISaveSeedHook.OnInitialSaveStored`.

**Wave D status (2026-10-01): M1-08, M1-10, M1-12 and M1-N3 are merged.** E2E check by the lead: real server + FakeNode authority + 4 clients, the full join flow (save upload → download → verify → InGame) with `--verify`: 143,283 entries checked, 0 position errors, 28/28 checksums.

**Wave E status (2026-10-01): M1-09 (UDP), M1-T3, M1-T4, M1-E3 and the schema change batch (a)–(d) are merged.** E2E check by the lead: real server + FakeNode authority + 4 clients, `swarm --udp --loss 5 --verify --commander foreign --team-assets --duration 40`: 131,478 entries checked, 0 position errors, 28/28 checksums, 5/5 UDP bound (0 fallback, rx loss ≤ 4.7%), foreign orders 308 sent / 0 forwarded. Tests after fixes: Protocol 526, FakeNode 80, Core 1,021, Persistence 51, Server 322 (+1 skipped). Fixed after review (task/m1-t3-fixes, merged): economy half of `SessionSettings` now populated and pushed; team store loads after migrations (it previously failed on a fresh data dir and never reloaded stored teams on restart); `ApplyPresetAsync` authority guard. Open: mod-side UDP (M2); TCP-only clients still capped at one frame in flight (~10 Hz at 100 ms RTT); two save tests (`AuthorityKilledAtHalfway…`, `CatchUpReplays…`) flake under heavy machine load; add `SameWallet`/`RequestIdReuse` to `EconomyReject` in the next schema batch.

**Wave F status (2026-10-01): M1-E4 (loans), M1-E5 (trades + schema: `SameWallet`, `RequestIdReuse`, `OutOfRange`, `LoanState.Withdrawn`), M1-S2 (admin REST under /api/v1, ApiProblem errors, auth route move, session invalidation on password change) and M1-F3 (FakeNode team behaviours) are merged, plus bug fixes found in review:** (1) authority takeover after a loading authority left (three Saves causes: no checkpoint request when an old one existed, joiners given the soon-replaced checkpoint, SaveReady picking the oldest same-hash checkpoint); (2) graceful TCP close so a kicked client receives `Disconnect{reason}` instead of a reset (drain input ≤ 2 s); (3) **save upload race**: a dropped authority socket cleared the outstanding `RequestSave`, so the resumed authority was asked again (before its Welcome, or racing its interrupted-upload resume → 60 s timeout). Now the request survives a detach and is only sent to an Announced node. Lead verified 25 consecutive passes of the save test classes. Migrations: 0005_loans, 0006_admin_security, 0007_trades (runner version = file count, keep contiguous). Tests: Protocol 526, FakeNode 116, Core 1,152, Persistence 59, Server 409 (+1 skipped). Open: combined `max_open_offers_per_player` (loans + trades) not enforced; fake clients don't trade wares; trade/loan REST + GUI are M1-E6/E7; admin cancel of an Active loan doesn't claw back the disbursement (by design for now).

**Long-run note (wave F):** the M1-F3 agent ran ~1 h 50 min in total over three sequential tasks: F3 itself (~26 min), the two session bug fixes (~16 min) and the save flake hunt (~70 min). The hunt was slow because each failing run of the save test waits 60–90 s for its timeout, and the agent first tried broader changes (admission, test client, transport) before narrowing to the real SaveService race. The time was partly needed (a real production race, proven with 60 consecutive passes), partly avoidable (no time box in the brief). See execution-plan §6.1.

**Wave G status (2026-10-02): M1-S3 (AdminHub `/hubs/admin` + AdminBroadcaster, per-connection bounded pumps instead of SignalR groups) and M1-W1 (GUI shell + hub wiring to the generated contract) are merged.** W1 acceptance verified live by the agent: hub connects with the cookie, header follows `SessionChanged`, auto-reconnect + re-subscribe after a server restart. A regression seen after the S3 merge (`AuthorityTakeoverLiveTests` ~6/15 alone) was a **FakeNode harness race**: `NodeLink` dropped frames that arrived before its handler was set (e.g. `SessionSaveInfo` 9 ms after Welcome); the hub only shifted timing. Fixed by buffering; lead verified 20/20. Tests: Protocol 526, FakeNode 116, Core 1,152, Persistence 59, Server 426 (+1 skipped); web 34. Open: Teams/Economy hub topics (M1-T5/E6); `TickP99Ms` always 0 (no actor tick timer yet); data dir is set by `--data-dir` / `X4MP__DataDir` / `X4MP_DATA_DIR` (default: next to the exe) — document in M1-C3. **Test stability backlog** (pass alone, fail occasionally when all test projects run in parallel): `TransportTests.ACloseWhileThePeerStillSends…` (~1/10, also alone; suspected RST discarding the unread Disconnect), `SaveTransferTests.ClientsThatJoinBeforeTheFirstCheckpoint…`, `SqliteWorldStoreTests.JournalRowsRoundTripInOrderAndTruncate`. Do a stability pass before M1-C1 (E2E CI must be reliably green). GUI nit from the lead's live demo: while the hub is reconnecting, the header keeps showing the last session state (e.g. RUNNING); it should show it as stale/unknown until reconnected (fix with M1-W2). Durations (harness): S3 26 min + fix 69 min (over its 45 min box: rare flake, 1 in 12–30, and one loop ran on a stale binary); W1 9 min + wiring 4 min.

**Wave H status (2026-10-02): M1-W2 (Dashboard), M1-W3 (Players + detail, **Playwright E2E** under `server/web/e2e`, `npm run e2e`), M1-W5 (Sessions & Saves, resumable upload), M1-W6 (Chat, Logs, Settings, Diagnostics) and M1-E6 (economy admin REST + economy hub topic) are merged, plus fixes:** loan/trade-linked transactions are not reversible (409 `NotReversible`, use loan/trade admin actions); **save header money is whole credits** (the seeder divided by 100; user-confirmed 5,447,419 ↔ ≈ 5.4 M Cr). Lead demoed all pages live in the browser with a FakeNode swarm. Tests: Protocol 526, FakeNode 116, Core 1,176, Persistence 64, Server 476 (+1 skipped); web 94 (+1 live skipped); e2e 5. **Gaps found:** M1-F2 (FakeNode failure injection: `--slow-reader`, `--disconnect-every`, `--reload-every`, `fuzz`, `inspect`) was never scheduled — needed for the M1 exit (slow-reader/fuzz harmless) and the Diagnostics slow-reader check; admin-uploaded saves are stored but the authority doesn't load them yet (needs the mod: M2); `Net.MaxPlayers` is Boot-only (restart); no team column in player DTOs (add with M1-T5); FakeNode has no chat handling; rejected player economy requests are not evented. **Stability backlog** now also: `HubStallTests.AFrozenBrowser…`, `SqliteSaveCatalogTests.AnEndedSessionNoLongerProtectsItsSaves` (both pass alone 6/6 and 10/10; fail only under the full parallel run). Agents polled with `sleep 298` loops — banned in briefs from wave I (execution-plan §6.1). Durations (harness): W2 6 min, W3 10, W5 13, W6 13, E6 30 + fix 9, money fix 11.

**Wave I status (2026-10-02): M1-F2 (FakeNode failure injection: `--slow-reader`, `--latency/--jitter`, `--disconnect-every`, `--reload-every`, `fuzz`, `inspect`), M1-T5 (Teams REST `/api/v1/teams` + hub teams topic + Teams & Factions page; team column on Players), M1-E7 (Economy page), M1-W4 (Map: galaxy + sector canvases, follow through jumps; 3,000 entities draw in 0.32 ms, 10,000 in 0.82 ms) and a test-stability pass are merged. All GUI pages are now real.** Product bugs found and fixed: graceful close set `_graceful` after completing the queue (RST wiped the kick reason); on resume `CaptureSet` could precede `Welcome` (InterestTransport now requires `Announced`); a SlowConsumer close left the socket open forever (now aborted); replication stamped entries with game time 0 before the first WorldUpdate (now held). Test-only: every test called the process-global `SqliteConnection.ClearAllPools()` (now per data dir). Lead verified: full suite Protocol 526, FakeNode 140, Core 1,176, Persistence 64, Server 512 (+1 skipped); web 161 (+1); **e2e 16/16**. F2 acceptance (agent): fuzz 60 s → 404 violating frames, 6 temp bans, server alive, neighbour swarm 0 errors; slow reader closed as SlowConsumer, others 0 errors (needs dense settings `--sectors 6 --ships 6000`); 20 resumes + 12 reloads with 0 verify errors; `--latency 100` and `--udp --jitter 20` 0 errors. **Open:** `SaveLatencyTests.ATransferRaises…P99…` is wall-clock and load-sensitive → move to the nightly load job (M1-C2); `SaveTransferTests…BeforeTheFirstCheckpoint` and `HubStallTests` didn't reproduce (HubStall has wall-clock bounds — suspect); Server tests take ~7 min mostly from fixed ~14 s live-test waits → switch to condition waits; capture rates above the authority's 20 Hz tick give verify errors without injection (investigate); no test for the SlowConsumer socket abort; team presets fail with `NoFactionSlot` once > 8 players are known (exclude detached players); sector frames lack owner/faction, `SectorDto` lacks extents, galaxy-frame interest lacks admin views; the map jump e2e depends on FakeNode seed 42 (`Jump02`); FakeNode prints nothing for `TeamRelations`; economy overdue badge + frozen-wallet request rejection need M1-F4. Durations (harness): F2 72 min, T5 25, E7 12, W4 38, stability 47.

**Wave J status (2026-10-02): M1-F4 (FakeNode economy behaviours: `--economy`, `--dupe-attack`, `--loan-default`, `--income-rate`, admin audit), M1-X1+X2 (mod-policy schema delta, `ModPolicyChanged`=0x0117, constants generated from `protocol/constants/mod_policy.json` for C#/C++, pure `ModPolicyEvaluator` + gateway integration, FakeNode `--extensions`), M1-C2 (load harness `X4MP.LoadTests` + `budgets.json` + `.github/workflows/nightly.yml`; real tick-duration metric; `Category=Perf` tests excluded from CI), M1-C3 (`docs/server-admin.md` rewrite, new `docs/fakenode.md`) and a test speed pass (live tests stop on their condition; Server tests ~545 s → ~260 s) are merged.** F4 10-min acceptance: 0 duplicate effects, reconciliation drift 0, auditor clean, verify 0 errors. C2 local 3-min run: 1+16 clients, 20.6k entities, tick p99 3.7 ms, CPU p95 66%, 173 MB, 0 drops. Lead verified: full suite Protocol 530, FakeNode 193, Core 1,276, Persistence 64, Server 533 (+1 skipped); e2e 18/18; latency test 8/8 alone. **Design issue found (affects the real mod authority, M2/M4):** `EntitySpawn` states carry no game time, so the server stamps them with the latest `WorldUpdate`'s time; when that clock update goes over UDP and the spawns over TCP, latency/jitter reorders them and spawns get stale stamps. FakeNode now sends spawn ticks entirely on the ordered TCP lane (`FakeAuthority.NeedsOrderedLane`); the mod authority must do the same — or add `game_time` to `EntitySpawn` (schema, next batch; preferred). Also: `WorldMirror.IngestWorldUpdate` has no stale-tick guard (a reordered datagram can roll game time back) — add one. **Open from reviews:** HTTPS/self-signed + CSP headers from SRV §4.3 not implemented; startup banner lists the node TCP port as a GUI URL; `/api/v1/server` `protocolRange` 0..0; no GUI for API tokens/audit, no user management; nightly.yml has never run on GitHub (check the first run); X3/X4/X5 next; the mod's real `extension_list` is M2; a live test spends ~12 s in Kestrel host shutdown (fold into wave K); no InDoubt trade arose in the F4 run at 5% timeouts. Durations (harness): F4 63 min, X1+X2 33, C2 39, C3 4, speed 56, latency fix 17.

**Wave K status / M1 COMPLETE (2026-10-02): M1-X3+X4 (mod persistence, `/api/v1/mods` REST, `ModEditor` role, hub mods topic, live `ModPolicyChanged`, pre-authority recheck, rejected joins don't claim names), M1-X5 (FakeNode extension presets), M1-X4 GUI (Mods page), M1-C1 (E2E CI: swarm + Playwright on ubuntu, headless C++ client on windows; `tools/e2e.ps1`), server fixes (`EntitySpawn.game_time`, stale-tick guard, presets skip detached players, banner, protocolRange, prompt node-connection shutdown) and the M1 exit runs are merged.** Exit report: [m1-exit-report.md](m1-exit-report.md) — all 9 criteria PASS; first fully green GitHub run 37077752855. Repo is **public** (GPL-3.0, history scrubbed 2026-10-02). CI-only fixes on the way: timing tests Perf-tagged/condition waits, FakeNode authority upload job bound to its connection (old job stole the resumed job's `SaveUploadAccept`), shared `TestPorts` helper (port TOCTOU). **Carry-over to M2+:** the mod authority must fill `EntitySpawn.game_time` and bind upload jobs to their connection (mod-design §6.2); the mod's real `extension_list` (M2-X2); admin-uploaded saves aren't loaded by the authority yet; `/mods/save-requirements` 501 (no `<patches>` reader); unknown-key mod refusals not pushed on the hub; HTTPS/CSP (SRV §4.3) not implemented; no GUI for API tokens/audit or user management; economy 30-min run should use settings that complete trades; one unreproduced 404 creating a session right after a save upload (write-behind save catalog); `WorldUpdate` capture rates above 20 Hz give verify errors without injection (investigate); sector frames lack owner/faction, `SectorDto` lacks extents.

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

### 3.8 Mod management, phase 1 (ADR-044, [mod-management.md](mod-management.md) §3)

M1-03's "extensions hash" check becomes the fast path of M1-X2. M1-X1..X5 are not on the
M1 critical path but are part of M1 exit (handshake rejections audited and visible in the GUI).

| ID | Track | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|---|
| M1-X1 | P | Schema delta: `ExtensionInfo` (+ enums), `ClientHello.extension_list` (deprecate `extensions:[string]`), `ModPolicy`/`ModPolicyEntry`/`ModRef`/`ModPolicyViolation`, `Welcome/SessionSettings.mod_policy`, `ServerHello.mod_policy_version`, `ModPolicyChanged` id, `Disconnect.mod_violation`; shared constants (client-only allowlist, Nexus URL regex, hash line format) generated for C#/C++ | M0-03 | `flatc` compiles; golden vectors added for the new tables; constants identical in C# and C++ (test) | S |
| M1-X2 | S | `ModPolicyEvaluator` (pure) + gateway integration: fast path on `extensions_hash`; classification (allowlist > admin class > hint; Unknown = Sim); Required/Allowed/Blocked × enabled × version rule × hash; `unknown_default`; Strict/Warn; DLC always strict; authority checked in `AdminList` mode | M1-03, M1-X1 | Table-driven test over every rule × player state; DLC mismatch rejects under Warn; allowlisted library differences never reject; FakeNode client with a missing Required mod gets `ExtensionsMismatch` with the exact `install` list incl. links | M |
| M1-X3 | S | Persistence + domain: migration with `player_extension_reports`, `session_mod_policy`, `session_mod_entries`, `mod_catalog`; report stored on every `ClientHello` (admitted/warned/rejected); janitor keeps 20 per player; `NexusUrl` validation; Workshop link derivation; `SavePatchesReader` for `<patches>` | M0-14, M1-X2, M1-12 | Reports survive restart; bad Nexus URL ⇒ 400 with field error; `ws_<n>` yields both Workshop URLs; `<patches>` read from a real 9.00 save fixture (first 64 KB only) | M |
| M1-X4 | S/W | Admin REST + hub (`/sessions/{sid}/mods…`, import-from-authority, save-requirements, `/players/{id}/extensions`, mod catalog), audited, `ModPolicyChanged` push to nodes (no kick) + `PlayerModsReported`; **Sessions → Mods page** and **Players → Mods tab** (mod-management §4.3, phase-1 parts) | M1-X3, M1-S2, M1-W1 | Playwright: import from a FakeNode authority fills the list with Workshop links; toggling a mod off makes the next joining bot fail with `disable`; paste of a non-x4foundations Nexus URL shows an inline error; per-player status updates ≤ 1 s | L |
| M1-X5 | F | FakeNode `--extensions <json>` / `--extensions-preset vanilla\|modded\|mismatch`; swarm verifies rejection details | M1-F1, M1-X2 | `swarm --extensions-preset mismatch` produces one rejection per bot with the expected lists and no other errors | S |

**M1 critical path:** M1-01 → M1-02 → M1-03 → M1-05 → M1-06 → M1-07 → M1-08 (with M1-F1
growing alongside) → M1-T1/M1-E1 → M1-T4/M1-E5 → M1-S2/S3 → M1-W1 → pages → M1-C1.

---

## 3b. M2 tasks (plan: [m2-plan.md](m2-plan.md), 2026-10-02)

M2 makes the mod work inside real X4 with **one** X4 copy (real X4 as client + FakeNode authority serving a real save,
or real X4 as authority + FakeNode clients). The full plan (scope, non-goals, 18 numbered exit criteria, fallbacks per
session-2 finding, briefs-level task table, testing strategy, risks and open questions) is in
[m2-plan.md](m2-plan.md); the user's test script for session 2 is [in-game-session-2.md](in-game-session-2.md).

| Wave | Tasks | Waits for |
|---|---|---|
| 0 (session-2 kit) | M2-001 spike v2 framework + UI block, M2-002 saves/clock/money/V12/S9 blocks, M2-003 on-foot S10, M2-004 diplomacy S11 + HQ S12, M2-005 native probe `x4mp_probe`, M2-006 FakeNode `--save-file` + `tools/session2/` scripts | — |
| Session 2 | User, sitting 1 (parts A–D, ~2 h) blocks M2; sittings 2–3 (S11/S12, S10) feed later milestones | wave 0 |
| 1 | M2-01 `x4mp-hostsim` (fake X4Native host, CI), M2-02 server: authority loads a stored/uploaded save + catalog read-through + `<patches>` reader, M2-03 = M2-X2 `core/mods`, M2-04 mod host skeleton, M2-05 Lua bridge + standalone screens (+ M2-X1), M2-08 `core/authority` upload job bound to its connection + `EntitySpawn` encoder with `game_time` | — (runs during session 2) |
| 2 | M2-06 client join flow, M2-07 reload survival, M2-09 authority in game, M2-10 save control + self-test, M2-11 embedded menu entry + HUD, M2-13 server diagnostics (LogForward, self-test view, unknown-key refusals on the hub) | session-2 sitting 1 verdicts |
| 3 | M2-X3 grouped mod refusal, M2-12 `launch.json` + `NodeStats`, M2-14 session-3 kit + acceptance script | wave 2 |

**Status 2026-10-03:** waves 0, 1 and 2 are **done and merged**; session 2 sitting 1 + D8 done
([spikes/session-2-results.md](spikes/session-2-results.md)). Wave 2 delivered the client join flow (M2-06), reload
survival with an epoch rule (M2-07), the in-game authority (M2-09), save control + self-test (M2-10), the options-menu
adapter + self-reshowing HUD (M2-11) and node diagnostics in the GUI (M2-13). Verified only through hostsim e2e scripts
(`mod/tests/hostsim/{join_flow,reload_survival,authority_flow}_run.ps1`, not yet in CI: M2-14) and unit tests; the real
game confirms in session 3. Fixed along the way: FakeNode ghost-enumeration race (Playwright economy flake), two
`core.retention` test races, a publish-modified `packages.lock.json` that slipped into merges twice (follow-up task
offered to stop publish from dirtying it). Known intermittent: `core.latest-wins` (seen once). Wave 3 next.

**Status 2026-10-03 (later):** wave 3 **done and merged** (M2-X3 grouped mod refusal, M2-12 `launch.json` + NodeStats,
M2-14 CI e2e steps + lock-file root fix + HUD notify cue + session-3 kit). CI runs the hostsim e2e scenarios (join,
20 reloads, authority, mod refusal, launch/stats) and the 50-run upload-kill test on every push. The session-3 kit was
checked against the final code (found and fixed: the mod never asked for the LogForward capability, so self-test and
quicksave warnings never reached the server) and dry-run end to end with the real `x4mp.dll` in hostsim
(`mod/tests/hostsim/session3_dry_run.ps1`, 161 s).

**Status 2026-10-03 (evening):** in-game session 3 done: **all 18 exit criteria and V21 pass**
([m2-exit-report.md](m2-exit-report.md), [spikes/session-3-results.md](spikes/session-3-results.md)); nine defects were found and
fixed live. **Next:** an M2 close-out task for the session-3 open items 1-7 (HUD line + chat/Esc, universe-ready after
`/reloadui`, first-checkpoint self-spawn, refusal screen text, "Last server" line, team-faction log noise, manifest cleanup),
a 15-minute retest, then M3 planning.

**Status 2026-10-03 (night): M2 complete.** Close-out A/B merged, retest in game passed (session-3 results, "Retest").
Carried into M3: self-spawn when the player sits down; Join fields in `x4mp.json`. **Next: M3 planning.**

M1 carry-overs placed in M2: `EntitySpawn.game_time` and connection-bound upload jobs (M2-08/09), real `extension_list`
(M2-X1/X2), authority loading admin-uploaded saves and the post-upload 404 (M2-02/09), `/mods/save-requirements`
(M2-02), unknown-key refusals on the hub (M2-13). Deferred: HTTPS/CSP and token/audit/user GUIs → M6; economy swarm
trade settings → M5; `WorldUpdate` > 20 Hz verify errors and sector owner/extents → M4.

## 3c. M3 tasks (plan: [m3-plan.md](m3-plan.md), 2026-10-03)

**Status 2026-10-04 (evening): session 4 sittings 1 and 2 done** ([sitting 1](spikes/session-4-sitting-1-results.md), [sitting 2](spikes/session-4-sitting-2-results.md)); live fixes merged and confirmed in game: M3-15 ghost rotation units, M3-16 HUD over map, M3-18 avatar re-own on team move, M3-20 parked avatar velocity, M3-21 authority fresh rejoin; plus M3-17/M3-19 CI flakes and kit fixes. M3-22 avatar records decided by the loaded save (checkpoint ledger, survives server restarts) is merged, in-game check pending. M3-23 Finding 4 diagnostic build (knowledge probe + `diag` switches) is merged. **Next:** Finding 4 experiment runs (one PC, script in in-game-session-4.md), then sitting 3 (two PCs over Tailscale, one operator via Parsec, script rewritten). Open: findings 1, 4, 6, 8, 9, 13, 14 and the small items in the sitting-2 results; test flakes: HostSimM3 ghost yaw check (m3_client.hostsim:78-79, seen twice: got -2.40 and 1.83; passes on rerun, the authority's line-71 timeout is its knock-on), ReloadSurvival 401 when run right after HostSimM3 in one e2e call (passes alone).

**Status 2026-10-04: M3 build phase done.** Waves 0-3 merged (M3-001/002, M3-01..14); sitting 0 ran 2026-10-03; main verified after M3-14 (mod 445 ctest + 230 Lua, all .NET suites, 16 local e2e steps incl. the new HostSimM3 / UdpLane / Session4Kit). M3-14 took ~3 h: its two-DLL pair run found and fixed three integration bugs (no authority `WorldUpdate`, host ship not a player ship, NodeStats UDP/ghost fields empty). **Next: session 4 sittings 1-3**, then the M3 exit report.

**Status 2026-10-03: M3 planned, awaiting the user's answers** to the 14 open questions at the top of
[m3-plan.md](m3-plan.md) (second X4 copy, ghost-error metric, UDP in M3, avatar spawn, offline avatars, ...). Scope: teams in
game, own-ship capture, avatars + client takeover, player ghosts, UDP lane, chat + roster, save hygiene v1, the two M2
carry-overs; NPC streaming stays M4, on-foot presence M3b/M3c. Waves: 0 = spike kit for session-4 sitting 0 (S13, before
coding), 1 = foundations (M3-01..07), 2 = in game (M3-08..12, after the S13 verdicts), 3 = hygiene + integration + kit
(M3-13/14). User script outline: [in-game-session-4.md](in-game-session-4.md) (sitting 0 spikes; sittings 1-2 one PC with
FakeNode wingmen; sitting 3 two PCs).

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

### 4.1 Session 2 retest list

User script: [in-game-session-2.md](in-game-session-2.md) (kit = M2 wave 0, [m2-plan.md](m2-plan.md) §5.1). Session 2 needs the native DLL from M2 work (S5, S6, V07; see
`spikes/session-1-results.md`). It also retests the following. R3–R6 come from
`research/library-mods.md` (ADR-043); R7–R8 from `mod-management.md` (ADR-044).

| ID | Retest | Pass criterion | If it fails |
|---|---|---|---|
| R1 | **V20** `require("debug").getupvalue` with Protected UI Mode off, at file load and on the `gfx_ok` retry | Returns `config` from `menu.displayOptions` (or `createOptionsFrame` / `displayOption`) and passes validation | Native `lua_getupvalue`, then row append, then standalone (MOD §7.2) |
| R2 | **V12** MD object variables with the correct syntax | `$x4mp_netid` set and survives save/reload | Manifest matching only (ADR-010) |
| R3 | **SirNukes installed:** `config` capture at file load still wins; adapter logs `source=debug`; retry path after SirNukes' on-load init finds `config` via `createOptionsFrame`/`displayOption` or uses `append` | Embedded "Multiplayer" row appears with SirNukes' "Extension Options" row | Row append (source 4) or standalone menu |
| R4 | **SirNukes installed:** X4Native's Settings → Extensions → X4 Multiplayer page still appears | Page visible | Report to X4Native; our entry points don't depend on it |
| R5 | **SirNukes installed:** chat wrappers stack on its `Online*` globals; our session chat round-trips; a SirNukes `/command` still works; disconnect leaves SirNukes' globals in place | All three | Own chat menu (MOD §7.6 fallback) |
| R6 | **kuertee UIX installed:** `OptionsMenu.uix_getConfig()` returns the vanilla-shaped `config`; adapter logs `source=uix`; UIX's re-sorted Extensions page coexists with X4Native's injector | Both | Fall through to `require("debug")` |
| R7 | `GetExtensionList()` in the start menu: `personal`/`isworkshop` values for user-folder and Workshop mods; `GetModifiedBasegameUIFilesExtensions()` names vs ids (V28) | Fields documented in mod-management §1.3 | Native folder scan decides `source` |
| R8 | `C.OpenWebBrowser` with Nexus https, Workshop https and `steam://url/CommunityFilePage/<id>` (V29) | Each opens something usable | Show URL text only |

### 4.2 Session 2: on-foot presence block S10 (ADR-046)

The full procedures are in [research/on-foot-presence.md](research/on-foot-presence.md) §6. Most
of S10 needs no native DLL and one PC. A "mirror actor" replays your own walk 3 s later to stand
in for a remote player. If time is short, run S10.1, S10.3, S10.4, S10.11, S10.12, S10.14, S10.7
and S10.8 first.

| ID | Experiment | Pass criterion | If it fails |
|---|---|---|---|
| S10.1 | Read on-foot state: container, room, room-local position and heading, room/transport events | Container and room resolve everywhere. Position matches MD within 1 cm. `changed_room` fires within 1 frame | MD `player.room`/`relativeposition` via the shim at 10 Hz |
| S10.2 | Room-key determinism across revisit, reload and (optionally) a second PC | Identical room keys per station | Key on roomtype+macro; accept F2 approximation |
| S10.3 | Spawn an MP character (`create_cue_actor macro=player.entity.macro`, name, team owner) | Visible, right look, name and title shown, survives 5 min | `<select race tags>` fallback look |
| S10.4 | Movement modes: teleport 5 Hz / `start_actor_walk` 2–4 Hz / `SetPositionalOffset` per frame | One mode with walk animation, rated ≥ 4/5, p95 error < 1 m | Best available mode; F2 slot mode |
| S10.5 | Facing and emotes | Yaw ±15°. ≥ 2 body gestures and ≥ 1 face emote | Wave as notification only |
| S10.6 | Room transitions, transporter, interior teardown, capital-ship bridge | Clean re-placement; 0 leftover actors after `interiors_despawning` | Despawn on unknown rooms; ships out of scope |
| S10.7 | Talk → custom conversation choices + `open_conversation_menu` | Choices render and dispatch; vanilla comm suppressed | Own menu on a key |
| S10.8 | Cost of 8 walking actors; save strip, janitor, `temporary` trait | < 0.5 ms/frame; 0 actors after reload | Snap mode; janitor only |
| S10.9 | Catalogue progress-gated interiors on 5 stations | Info | — |
| S10.10 | (optional, 2 PCs, M3 build) two-player walk | Right room, < 1 m error, no leftovers | Fallbacks |
| S10.11 | Create the MP lounge (vanilla corridor + room macros, fixed door and seed, private) | Created on 2 stations; no errors; vanilla interiors unaffected | Other macro/module; reuse a vanilla room |
| S10.12 | Teleport into and out of the lounge; transporter listing; door | In/out works; listing and door reported | MD teleport only |
| S10.13 | Lounge slot positions identical across reloads | Identical room-local offsets | Slot snapping |
| S10.14 | Lounge save safety, incl. loading without the mod | Saves load without the mod; janitor removes the orphan lounge | Always remove lounges before saves |
| S10.15 | Actors + Talk inside the lounge | As S10.4/S10.7; seats work | Snap mode |

### 4.3 Session 2: team diplomacy block S11 (ADR-047)

The full procedures are in [research/diplomacy.md](research/diplomacy.md) §7. The block is
MD + Lua only, so no native DLL is needed, on one PC, in about 20–30 minutes. The spike
extension adds a `libraries/diplomacy.xml` diff with one test action and one test event. A
save with a PHQ embassy and at least one agent is best; without one, S11.4(a) is skipped.

| ID | Experiment | Pass criterion | If it fails |
|---|---|---|---|
| S11.1 | Activated `x4mp_team_1..3` in Diplomacy → Factions and Relations: section, colours, lock icon + our `reason` text; `set_faction_known` on/off; `hidden` tag on team 3 | Teams 1–2 listed with correct relation and lock reason; team 3 hidden | Hide teams (`hidden`); relations only in our own screen |
| S11.2 | Lock semantics: with team 1 locked, try player→team1, team1→argon, `add_faction_relation`; unlock+set+relock in one block; `event_faction_relation_changed` params; effect of locking `player` (documented, never used) | Locked pairs blocked from both sides; unlock-set-relock applies the same frame; event fires with readable params | Unlocked team factions + authority watchdog re-applying the matrix |
| S11.3 | `set_faction_diplomacy_active` / `…_events_allowed` on a team faction, survives two vanilla 30 s checks both ways; UI section and interference dropdown | Flags stick; UI follows | Accept teams always "unreceptive" |
| S11.4 | Custom diplomacy content: (a) test action round trip via `event_diplomacy_action_operation_started` → `complete_diplomacy_action_operation`; (b) `create_diplomacy_event_operation` between two teams with `agent=null`, shown in Diplomatic Events | (a) clean round trip; (b) visible; note whether an option is selectable without an agent | Custom action/event UX stays rejected (expected) |
| S11.5 | Client suppression of NPC diplomacy events (`EventCapable=false` via vanilla library, then `events_allowed false`; wait 70 s) | Flags stay false for vanilla factions | Client-only diff guarding Protocol Null event generation |
| S11.6 | Inject a "Teams" tab into `DiplomacyMenu` (wrap `menu.createLeftBar`/`createInfoFrame` via `Menus`; also `require("debug")` for `config.leftBar`) | Tab renders; switching tabs works; no Lua errors | Standalone Team Diplomacy screen only |
| S11.7 | NPC↔NPC relation change detection (`argon↔teladi` set + restore; `event_faction_relation_changed`, `event_player_relation_changed`) | Events fire with faction ids and value | Authority polls relations every 10 s (`x4n::faction::get_relation`) |

### 4.4 Session 2: per-team HQ and research block S12 (ADR-048)

The full procedures are in [research/team-hq-research.md](research/team-hq-research.md) §6. The
block is MD + Lua only, so no native DLL is needed, on one PC, in about 30–45 minutes. It needs
save A (PHQ with research unlocked, ideally one research in progress) and save B (early, no PHQ).
If time is short, run S12.1, S12.2, S12.3 and S12.6 first.

| ID | Experiment | Pass criterion | If it fails |
|---|---|---|---|
| S12.1 | Read research state: `HasResearched` and `IsKnownItem("researchables")` per research ware, `CanResearch()`, `GetHQs("player")`, `player.headquarters`, `$x4ep1_hq_research_unlocked`, `UnlockResearch.state`, active research progress | All readable; completed list matches the vanilla research menu | Read via MD `ware.research.unlocked` only |
| S12.2 | Grant and revoke via MD (`add_research`/`remove_research`) for teleportation, `research_mod_weapon_mk1`, `research_module_dock`; teleport reason, crafting, scan drops; event; save/reload; cancel refund | Both directions take effect immediately and persist; event and cancel behaviour documented | Grant-only; reconcile via fresh checkpoint on team change |
| S12.3 | HQ ownership: re-own the PHQ to `x4mp_team_1` and back; `set_faction_headquarters` on a team; spawn HQ-macro stations for `x4mp_team_2` and a second `player` one; `GetHQs` counts | No crash; `player.headquarters`/`GetHQs`/research state documented; team-owned spawn works | Inherited PHQ stays `player`-owned on the authority (ADR-033 exception) |
| S12.4 | Team HQ as the local player's HQ (player-view): locally re-own a team HQ to `player`, open the vanilla research menu, re-own back | Menu opens with the module, no errors or stray MD | Own Team Research panel only (v1 plan) |
| S12.5 | `StartResearch` on a team-owned (non-player) research module | Documented (expected: nothing runs) | Confirms server-run timer |
| S12.6 | Blueprints: read `player.blueprints`, `add_blueprints`, confirm no remove; build a module on a team-faction station without the `player` blueprint | Team construction doesn't need `player` blueprints (or the dependency is documented) | Temporary `player` grant on the authority during team builds |
| S12.7 | Licences on team factions: `add_licence`/`remove_licence faction=x4mp_team_1`, `haslicence`, `heldlicences` | Both work | Licences mirrored only on local `player` |
| S12.8 | Encyclopedia reveal: `add_research` + `add_encyclopedia_entry researchables` for a root and a successor | Entries appear with the right state | Own panel shows the full catalogue |

## 5. Post-v1 backlog (from user decisions 2026-10-01)

- **Shared story/unlocks (ADR-037):** `StoryState`/`UnlockEvent` protocol messages, authority-side unlock capture, client-side apply, join-time snapshot, per-team mission progress. Target M5–M6, depending on S9.
- **Fog of war (ADR-038):** `FogOfWar` session option; per-team visibility filter in the server interest manager (keep the hook from M1 on).
- **Starting credits GUI (ADR-039):** presets + custom in Sessions → Settings (fold into M1 economy tasks).
- **Loan enforcement (ADR-040):** `LoanEnforcement` policy, with AutoCollect / Penalty / Diplomacy (reputation loss, relation drift) / admin seizure. The M1 ledger must already record due, overdue and default events.
- **Player portal (ADR-050, [research/player-portal.md](research/player-portal.md)):** post-M5. P1 player sign-in (one-time in-game link), static knowledge + team blueprints/research, my assets with fleets/groups, empire notes and naming conventions; P2 market snapshots with per-team visibility (Strict default, admin setting); P3 planners (production chain, trade routes).
- **Modded-game support, mod management phase 3 (ADR-044, [mod-management.md](mod-management.md) §5):** compatibility classes (`Verified / ClientOnly / AuthorityOnlyMD / Incompatible / Untested`) as a shipped, server-overridable list; client-side suppression of state-changing MD for `AuthorityOnlyMD` mods; extension-settings `sync` alignment with the session save; save-required (`<patches>`) mods enforced as hard Required with a refusal to drop them from a campaign; per-mod verification test plan (two-node 30-min M4 run, savescan, third-node join). Not before M4 acceptance.

## 6. Mod management tasks after M1 (ADR-044)

Phase 1 server work is in §3.8. User decision: X4MP never hosts, downloads or installs
mods; it only shows and opens Nexus / Steam Workshop links and toggles enable state of mods
the player already has.

### 6.1 M2: mod side of phase 1

| ID | Track | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|---|
| M2-X1 | N | Lua: `GetExtensionList()` + `GetModifiedBasegameUIFilesExtensions()` gathered in the start menu and on `/reloadui`, sent as `x4mp.extensions` to native | M2 Lua bridge | Log line lists every extension with id/version/enabled; works before any save is loaded | S |
| M2-X2 | N | `core/mods/`: id → folder mapping over install / user / Workshop roots, `content.xml` parse (`save`, dependencies), DLL and `subst_*.cat` detection, class hint, content hash (`.cat` index or capped file hash) with cache, on a worker thread; fills `ClientHello.extension_list` + `extensions_hash` (allowlist excluded) | M1-X1, M1-N3, M2-X1 | Catch2 tests over fixture folders (cat mod, loose mod, Workshop id, DLL mod, missing folder); no frame-thread file I/O (asserted); hash stable across runs; Connect waits ≤ 2 s for enrichment then sends without hashes | M |
| M2-X3 | N | Join dialog renders `ModPolicyViolation` grouped (install / enable / disable / update) with link buttons (`C.OpenWebBrowser` when available, else URL text); Multiplayer screen lists the session's mod set when connected | M2-X2, M2 Join dialog | In game: a client missing a Required mod sees the grouped message and the Nexus/Workshop button opens the page (or shows the URL, per R8); with matching mods the join is unchanged | M |

M2 exit adds: "A client with a mod mismatch is refused with the grouped install / enable /
disable / update message and working links."

### 6.2 M6: phase 2 (launcher sync)

| ID | Track | Title | Deps | Acceptance criteria | Size |
|---|---|---|---|---|---|
| M6-X1 | S | `GET /api/v1/join/mod-manifest` + `GET /join/nonce` (HMAC-gated when the session has a password), links re-validated | M1-X4 | Manifest matches the GUI list; wrong proof ⇒ 401; no secrets in the payload | S |
| M6-X2 | N/F | Launcher scan + plan: three sources, newest profile `content.xml` (ask if several), shared classification rules and allowlist; plan of toggles (installed mods only), missing (links), version mismatches | M6-X1, launcher shell | Fixture profiles produce the expected plan; a missing mod never produces a toggle; stale `content.xml` entries are reported, not touched | M |
| M6-X3 | N/F | Launcher apply + restore: refuse while `X4.exe` runs; backup + `pending-restore.json` journal; atomic rewrite of only the affected `<extension>` entries; OneDrive retry; restore on X4 exit or next launcher start (only entries still holding the launcher's value); "keep this mod set"; "Restore my original mods"; last 10 backups | M6-X2 | Kill the launcher mid-session ⇒ next start restores; a player change made in game is not overwritten; byte-identical file after apply + restore when nothing else changed | M |
| M6-X4 | N/F | Trust and links: per-server first-contact prompt, extra confirmation before enabling DLL / base-game-replacing mods, Workshop `steam://url/CommunityFilePage/<n>` (https fallback) and Nexus buttons, "Check again" poll of the Workshop folder | M6-X2 | Only nexusmods.com/x4foundations and Steam Workshop links are opened; DLL mod needs a second click; a newly subscribed Workshop item is picked up without restarting the launcher | S |
| M6-X5 | W | GUI Mods page phase-2 bits: "launcher sync" status per player (policy version the node started with), copyable manifest URL | M6-X1 | Player who started via the launcher shows the current policy version | S |

M6 exit adds: "Starting a session through the launcher with one mod to enable and one to
disable needs no manual Settings → Extensions change, and the player's original mod set is
back after X4 exits."

**M1-T3 notes (team fan-out):** deltas go to InGame and AwaitingTeam nodes; a node that is still loading gets a full
TeamTable/TeamRelations/SessionSettings copy when it reaches InGame. Refusing the authority's own move while Running uses
`TeamRejectReason.SessionRunningRestricted` (REST maps it to 409 in M1-T5). `TeamModule.EconomySettings` + `PushSettings()` let the
economy supply its half of `SessionSettings`; `TeamModule.ResyncPlayer` is wired to `ReplicationModule.Resync` by `AddTeams`.
Open: `ApplyPresetAsync` does not yet guard the authority's membership while Running; the fake authority records
`ReassignPlayerAssets` but does not emit `EntityChange` (M1-F3).
### Implementation notes: M1-E3 (donate, transfer, pool actions)

- `EconomyService.Transfer/Donate` (new `TxKind.Transfer`, `LedgerReason.Transfer/Donation`); the pool actions share the
  same replay-first path. `EconomyService.CheckRate` (5 per 10 s sliding window, `EconomyRateLimiter`) is called by
  `EconomyModule` once per incoming request. New settings: `DonateScope` (default Teammates), `SharedWalletSpend`
  (AnyMember), `AllowAlliedTransfers` (false). `EconomyActionCompleted` is published on the bus per booked action.
- The wire has no `SameWallet`, `SelfTarget` or `RequestIdReuse` reasons (schema owned elsewhere): same-wallet (Shared
  mode, same team) maps to `NotApplicableInSharedMode`, self-dealing and non-leader `SharedWalletSpend=LeaderOnly` to
  `NotParty`, a reused key with another payload to `AmountInvalid` with detail "request id reused with a different payload".
  Suggest adding `SameWallet`/`RequestIdReuse` to `EconomyReject` in the next schema wave.
- Rejected requests are not stored (only committed ones are), so a replay of a rejection is re-evaluated.
- `EconomyModule` requires session phase Running (else `SessionNotRunning`); the wire `EconomyResult.ref_id` is the request key.
### Implementation notes: M1-E4 (loans)

- Code: `EconomyService.Loans.cs` (offer, respond, repay, forgive, withdraw, admin forgive/cancel, `ProcessLoanTimers`, `AuditLoans`),
  `EconomyModule.Loans.cs` (wire requests, `LoanStatus`, `ServerNotice`, 1 s timer throttle), `LoanModels.cs` (`LoanRecord`, `LoanState`,
  `LoanResult`, `LoanStateChanged` event, `LoanIncomeSplitter`). Migration `0005_loans.sql`.
- A loan row is written in the same SQLite transaction as the ledger posting that changes it (`PostRequest.Loans` -> `EconomyCommit.Loans`);
  a posting may carry only loans (forgive, overdue). The ledger caches loans and raises `LoanCommitted`; the service turns that into the
  `LoanStateChanged` event, `LoanChanged` callback (status frames, notices) for every path, including auto-repay.
- Escrow wallet owner id is `2^40 + loanId` (`LoanWallets.Escrow`): **M1-E5 must keep trade escrows out of that range** (e.g. plain trade ids).
- Wire mapping: loan id on the wire is `Id128{lo = id, hi = 0}`; `EconomyResult.ref_id` of loan requests is that id (not the request key).
  Lender withdraw (`Withdrawn`) shows as `Cancelled` (wire `LoanState` has no Withdrawn). Same-wallet (Shared mode, one team) maps to
  `NotApplicableInSharedMode`, a non-lender/borrower to `NotParty`, a reused key to `AmountInvalid` (as in M1-E3). No `GameTime` due in v1.
- Auto-repay rides in the `GameIncome` transaction (extra entries to the lender wallets, merged per wallet, oldest loan first), so there is
  no separate `LoanAutoRepay` transaction. Refunds (decline, withdraw, expiry, cancel) ignore wallet freezes; a frozen economy postpones the timers.
- New settings: `LoanScope` (Teammates), `MaxOpenLoansPerPlayer` (5), `MaxLoanPrincipal`, `MaxLoanInterestBp` (5000), `OfferDefaultTtlMinutes` (30);
  `LoanScope` and `MaxOpenLoansPerPlayer` are pushed in `EconomySettings`.
- Admin cancel of an Active loan closes it without reversing the disbursement (`reverseDisbursement` waits for the M1-E6 reversal action).
- Overdue only flags (ADR-040): no penalty, repayment still works; the module runs the timers at most once a second.

### Implementation notes: M1-E5 (escrowed trades)

- Code: `EconomyService.Trades.cs` (engine), `EconomyModule.Trades.cs` (wire), `TradeModels.cs` (`TradeRecord`, `ITradeStore`, `ITradeWorld`, in-memory store), `MirrorTradeWorld.cs` (mirror + asset policy), `SqliteTradeStore` + migration `0007_trades.sql` (`trades` as a JSON document, `trade_locks` with primary key (session, entity) = the unique lock index; renumbered after M1-E4 took 0005). Shared files only got hook calls (`BeginTrades`, `TradesNodeAttached`, `TradesNodeLeft`, `TradesTick`, `TradeOnMessage`) and the trade options.
- Flow: propose (validate scope, shape, ownership, locks, proximity, open limit; locks the assets; proposer accepts its own version) / counter (bumps the version, resets acceptances, moves the locks) / accept (stale = `StaleVersion`; the accept that completes the pair re-validates, escrows the one credits item, saves, sends one `AssetTransferOrder`). Credits are escrowed on accept, not on propose. Trade escrow wallet = `Escrow(2^41 + tradeId)` (`TradeRecord.EscrowOf`), clear of the loan escrows (`2^40 + loanId`).
- Terminal states: `Completed` (settle: escrow to the payee, mirror owner via `EntityChange`-style `SetOwner`), `RolledBack` (refund, unlock), `Cancelled` (initiator or admin), `Rejected` (counterparty decline, or an accept-time validation failure that kills the trade: scope, ownership, asset gone), `Expired`. `OutOfRange`, `InsufficientFunds`, `AuthorityUnavailable` only refuse that accept; the trade stays open.
- Settle once: only a Transferring or InDoubt trade reacts to `AssetTransferConfirm`; ledger ids `trade-settle:<id>` / `trade-refund:<id>` make a repeat after a crash a replay. Timeline on the actor tick: 30 s (`TradeExecuteTimeoutSeconds`) then `TradeQuery` x3, `TradeQueryIntervalSeconds` (10) apart, then `InDoubt` (alert `economy_trade_in_doubt_<id>`). Authority gone for good: Transferring becomes InDoubt; authority back: every Transferring/InDoubt trade is queried; a late `ok` settles it. `ok=false, compensated=false, failed_line>0` (partly applied) goes InDoubt, "Unknown" answers roll back.
- Admin (service level; REST is M1-E6): `AdminResolveTrade(id, complete, actor, reason)` for InDoubt, `AdminCancelTrade` for negotiating trades; both publish `AdminActionTaken`. State changes publish `TradeStateChanged`.
- Auditor check (`AuditTrades`): final or negotiating trades hold no escrow, Transferring/InDoubt exactly their credits, every lock belongs to an open trade. Startup recovery refunds an escrow that was posted for a trade whose save never happened.
- Permissions: `AssetPermissionGate` takes `isLocked`; orders, renames, gifts and `TradeReport` on an entity in an open trade get `IntentResult{Rejected, Conflict}` (`PermissionDenied` event). Proximity (`TradeRequiresProximity`, default on): the offered ship / ware container must be in the same sector as the receiver's ship (or chosen `receive_into_asset`) per the mirror, else `OutOfRange` (new wire reason). The roadmap's `team_assets` table does not exist: team ownership is the mirror's `owner_team`/`owner_player` (journaled), updated on settle.
- Schema (append-only): `EconomyReject` + `SameWallet`, `RequestIdReuse`, `OutOfRange`; `LoanState` + `Withdrawn`. The M1-E3 transfer/donate code and the M1-E4 loan code now answer `SameWallet`/`RequestIdReuse`; the loan module sends `Withdrawn`.
- New settings: `TradeScope` (Allied), `TradeShipsEnabled`, `MaxOpenTradesPerPlayer` (5), `TradeRequiresProximity` (true), `TradeExecuteTimeoutSeconds` (30), `TradeQueryIntervalSeconds` (10); the first three are pushed in `EconomySettings`.
- FakeNode: `--trade` (clients sell team ships to peers every 5 s, accept incoming offers, retry a dropped accept), authority `--trade-fail PCT` (order fails, compensated) and `--trade-timeout PCT` (confirm withheld; 80% of those were applied; a third never answer a query so they end InDoubt), summary lines `trade:` and `trade-authority:`; clients keep ghost owners in step with `EntityChange`. Swarm run: PerPlayer credit mode (one team resolves Auto to Shared, which is `SameWallet`), `TradeRequiresProximity=false`, 2 s / 1 s trade timeline; see `TradeLiveTests`.
- Open: wares have no live in-game path yet (the fake only trades ships); stations rejected; REST/GUI (M1-E6/E7); `max_open_offers_per_player` (loans + trades together) is not enforced.

### Implementation notes: M1-E6 (economy admin REST + hub)

- Code: `EconomyService.Admin.cs` (adjust, wallet freeze, reverse, loan cancel with disbursement reversal, `Totals`, `PreviewMigration(CreditMode)`), `EconomyEndpoints.cs` (REST), `EconomyViews.cs` (domain to DTO, wallet names), `EconomyDtos.cs`, `AdminBroadcaster.Economy.cs` (hub topic). Store additions: `IEconomyStore.GetTransaction/QueryTransactions`, the reversal link written in the same transaction as the reversal (`UPDATE ledger_tx SET reversed_by_tx`, the one change the append-only trigger allows), `SqliteAdminQueries.EconomyEvents`. No migration (still 0007).
- Paths are `/api/v1/economy/...` (the live session; the economy exists once the session row does, otherwise 409 `NoSession`; summary and policy answer without a session). Wallet route: `/wallets/{kind}/{ownerId}` with kind `Player|TeamShared|TeamPool|Escrow|World` (case, `_` and `-` ignored). Transactions page by id cursor (`before`, newest first); `ledger.csv` is one row per entry, streamed in chunks of 500, text fields starting with `= + - @` get a leading apostrophe; exporting is audited.
- Errors (all `ApiProblem`): 400 `ValidationFailed` per key, 404 `NotFound`, 409 `NoSession`, `WouldOverdraw`, `AlreadyReversed`, `NotReversible`, `NotAdjustable`, `WrongState`, `EconomyFrozen`, `RequestIdReuse`, `ConfirmationRequired` (body = problem + `migrationPreview`). `Idempotency-Key` is honoured by adjust and reverse (stored like player request ids under `admin:<key>`; a replay returns the first transaction); the other mutations are state transitions and answer `WrongState` when repeated.
- Rules: admin postings ignore wallet freezes; only debits are limited (crediting an overdrawn wallet is always allowed); `force` lets a player or shared wallet go overdrawn, a pool or escrow never; reversing a plain transaction negates its entries. Anything tied to a loan or trade (`LoanEscrow/Disburse/Repay/AutoRepay/Refund`, a `GameIncome` split by auto-repay, `TradeEscrow/Settle/Refund`) gives 409 `NotReversible` even with `force`, pointing to the loan/trade admin actions, because a reversal would leave `Outstanding`, state and escrow out of step with the ledger (the auditor loan/trade checks would fire). Only loan cancel with `reverseDisbursement` takes a disbursement back, together with the state change. `returnAsset` is refused with 400 until `AssetReturn` exists. `Reversal`, `ModeMigration`, `TeamMove` also give `NotReversible`.
- Audit: the economy service publishes `AdminActionTaken` (`economy.adjust`, `economy.wallet.freeze|unfreeze`, `economy.reverse`, `economy.loan.forgive|cancel`, `economy.trade.cancel|resolve_complete|resolve_refund`, `economy.unfreeze`, `economy.migrate`) with the actor `admin:<user>` and the reason; the REST layer writes `economy.policy` and `economy.ledger.export` itself. Rows arrive through the event bus (asynchronously, a few ms).
- Policy: `PATCH /economy/policy` maps its fields to the `Economy.*` settings (per-key errors like `PATCH /settings`); a credit-mode change on a Running or Paused session answers 409 with the migration preview unless `confirm: true`, then applies the setting and reconciles the balances at once. Design deviation: `EconomyPolicyDto`/`WalletDto` follow the real options and wallet state (a wallet has `version`, no `updatedAt`).
- Hub: `SubscribeEconomy` (returns `EconomySummaryDto`) / `UnsubscribeEconomy`; pushes `WalletChanged` (coalesced per wallet, `EconomyWalletIntervalMs` 250), `LedgerPosted` (every transaction), `LoanChanged` / `TradeChanged` (keyed per id, latest wins), `EconomyEvent` (`EconomyEventsPerSecond` 10, local negative ids), `EconomySummary` (1 Hz), `EconomyAlert` (alerts whose code starts with `economy`). Handlers run on the actor thread (`EconomyLedger.Committed`, `LoanCommitted`, `EconomyService.TradeObserved`) and return before building anything while the topic is empty (`PayloadsByKind`: `wallet`, `ledger`, `loan`, `trade`, `economy-event`, `economy-alert`, `economy-summary`). The module raises `ServiceStarted` so the broadcaster can attach when the session row appears.
- Open: rejected player requests are not events yet (the event log holds state changes and completed actions); trade `returnAsset`; `economy_events` table was not created (events come from `session_events`, which is write-behind).

## M1-T4 implementation notes (AssetPermissionPolicy)

- `X4MP.Core/Permissions`: `AssetPermissionPolicy` (pure, table-tested) and `AssetPermissionGate` (looks up sender team/leader, mirror owners, positions, relations). `RelayModule.AssetPermissions` runs it in `OnIntent` after the interest check and before custom validators; `AddRelay()` wires it when a Teams module is registered (no teams = no enforcement).
- Rejections answer `IntentResult{Rejected}` (NotYourAsset, PolicyDenied, HostileRequired, FriendlyFireDisabled, NotAllied, UnknownEntity, InvalidParameters, NotPermitted for out of range), are never forwarded and publish a `PermissionDenied` domain event limited to `Relay.PermissionDeniedEventsPerSecond` (5) per player. Not counted as protocol violations.
- New settings: `Relay.ClaimRangeMetres` (30000), `Relay.PermissionDeniedEventsPerSecond` (5). The mirror already mirrored `owner_team`/`owner_player` (M1-06); now covered by gate tests.
- Out of range also covers "target in another sector" and "sender has no ship position yet". Gifts always need `AllowAssetTransfer`, even inside one team. A neutral target gives `HostileRequired`, an allied or teammate target `FriendlyFireDisabled`.
- FakeNode: `--commander shared|own|foreign` (clients send 2 AssetOrders/s) and `--team-assets` (authority tags ships: id%4 = 1 team 1 common, 2 team 1 teammate-owned, 3 team 2). Summary line `commander(...)`: orders-sent, accepted, rejected, forwarded-to-authority.
- Follow-up: the `TradeReport` station check ignores trade-asset items (M1-E5); the leader for `OwnerAndLeader` comes from `TeamModule.LeaderOf` (not on `ITeamDirectory`).

## M1-T5 implementation notes (Teams REST, hub, page)

- REST under `/api/v1/teams` (flat, current session; server-design 4.4 has `/sessions/{sid}/...`): `GET ""` (whole `TeamsStateDto`), `POST ""`, `GET|PATCH|DELETE /{id}` (`?moveMembersTo=`), `GET /unassigned`, `PUT /members/{playerId}` (`teamId` null = Unassigned), `PUT /members` (bulk), `GET|PUT /relations`, `PUT /relations/{a}/{b}`, `GET /preset/{preset}/preview`, `POST /preset` (`confirm`), `GET|PATCH /policy`. All audited as `teams.*` (a lobby password is never logged: only `password: set`). Authority move while Running (assign, unassign, delete, bulk, preset that would move it) answers 409 `SessionRunningRestricted`; a preset that changes a Running session answers 409 `ConfirmationRequired` with the preview until `confirm: true`.
- Hub topic `Teams` (`SubscribeTeams` returns `TeamsStateDto`, `UnsubscribeTeams`). The module's `Changed` event only marks the topic dirty; 40 ms later the broadcaster builds the state once on the actor, diffs it with the last one sent (`TeamsDiff`) and pushes `TeamUpserted`, `TeamDeleted`, `TeamMemberChanged`, `PlayerAwaitingTeam`, `TeamRelationsChanged`, `TeamPolicyChanged`, or one `TeamsReset` when a player vanished. Roster events (join, leave, detach) and `Teams.*` setting changes also mark it dirty. Nothing is built without subscribers (`PayloadsByKind["teams"]`).
- `PlayerDto`/`PlayerLiveDto` carry `teamId`/`teamName`; the Players page has a Team column.
- Deviations: no per-request `moveAssets` (the `MoveAssetsWithPlayer` setting decides); no `AssetCount`/wallets in `TeamsStateDto` (the Economy page owns pools); `DELETE` does not require `moveMembersTo` (members become unassigned).
- The preset player set is every member plus every node in the session (detached ones too), so a long-running session full of old bots can exceed 8 players (`NoFactionSlot`); the Playwright spec kicks and unassigns stale bots first.

## M1-F3 implementation notes (FakeNode teams)

- CLI: `--team <id|name>`, `--team-pick lobby-random`, `--teams N`, `--relations coop|allied|ffa|twoteams` (swarm; implies `--teams`, prints the server `Teams.*` settings it needs). Details in `tools/X4MP.FakeNode/README.md` ("Teams").
- No REST for teams yet (M1-T5), so a layout is reached through server settings (`JoinMode=Lobby`, `AllowCreateInLobby`, `AutoAssign=Balance`, `DefaultRelation`) and the clients place themselves through the lobby; tests use `TeamModule` in-process.
- Closed the M1-T3 open item: the fake authority applies `ReassignPlayerAssets` (owner_team of the player's ships) and sends one `EntityChange` per ship; the server mirror and the clients' ghosts follow. Verified live: after an admin move the mirror shows the new `owner_team`, `--commander own` orders pass for the new team and are `NotYourAsset` for the old one, `--verify` stays at 0 errors.
- Fake NPC hostility: `FakeAuthority.Hostility()` (team pairs at war, ship pairs sharing a sector) follows a relation change within one poll (0-20 ms in the live test).
- Behaviour change: team-tagged ships are now spread over all team ids (`k = (id/8) mod n`) and `--commander own` includes ships the client owns itself.
- Server observation (not investigated, outside M1-F3): a second FakeNode run against a persistent server whose authority left while loading leaves the session in `WaitingForAuthority`/`AuthorityLoading` and the join never starts; restart the server between runs.

**Wave K / M1-FIXK (2026-10-02):** `EntitySpawn.game_time` (schema append; server uses it, 0 falls back to the latest WorldUpdate; FakeNode fills it, `NeedsOrderedLane` and the clock-update trick removed); `WorldMirror.IngestWorldUpdate` stale guard (older in tick AND game time is dropped; reset when an authority attaches); team presets place only attached nodes (detached/left players are left unassigned and rejoin through the normal join path); startup banner and `/api/v1/server` `adminUrls` no longer list the node TCP listener (endpoints logged separately); `protocolRange` is `major*1000+minor` (0..1 today); node connections close on Kestrel graceful shutdown (host stop ~12 s -> ~2 s, the remaining 2 s is the drain of a peer that does not close).
