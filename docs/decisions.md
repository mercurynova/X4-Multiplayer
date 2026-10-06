# X4MP Decision Log

Status: consolidated 2026-10-01 (design phase close). Companion to `docs/architecture.md`,
which is authoritative. Each ADR records context, decision and consequences. "Locked" ADRs
are user decisions from `PLAN.md`; "Lead" ADRs were decided by the project lead during
consolidation; "Consolidation" ADRs resolve conflicts found between the five detailed docs.

Doc abbreviations: **REQ** = `requirements.md`, **API** = `x4-api-notes.md`, **PROTO** =
`protocol.md`, **SRV** = `server-design.md`, **MOD** = `mod-design.md`.

---

## Part 1. Architecture decision records

### ADR-001 Server stack (Locked)
- **Context:** need a standalone process with a LAN web GUI and persistence.
- **Decision:** C# / **.NET 10 (LTS, `net10.0`, SDK 10.0.4xx)**, ASP.NET Core (Kestrel raw TCP `ConnectionHandler` + HTTP),
  SignalR for live GUI push, SQLite (`Microsoft.Data.Sqlite` + Dapper), Serilog. GUI is
  React + Vite + TypeScript, built to static files and embedded in a single-file exe (SRV §1, §5.1).
- **Consequences:** one `x4mp-server.exe` (win-x64 primary, linux-x64 also published).
  No EF Core, no Blazor.
- **Amended 2026-10-01 (user):** originally .NET 8; switched to .NET 10 before any code was written
  because .NET 8 support ends 10 Nov 2026 (.NET 10 is supported to Nov 2028).

### ADR-002 Topology: server-centric relay, one authority (Locked)
- **Context:** X4 cannot run headless; the reference made one game the host.
- **Decision:** the server never runs X4. Every X4 instance (node) dials out to the server.
  Exactly one node holds the `Authority` role and simulates the universe; all others are
  `Client`s. The authority's human is also a client player. Only the server listens.
- **Consequences:** no inbound firewall rules on players' PCs (PIT-055). Authority handoff is
  possible later (ADR-009 makes it cheap). Authority FPS remains the scalability limit (PIT-038).

### ADR-003 Windows-first mod on X4Native (Locked)
- **Decision:** one native DLL `x4mp.dll` (C++23, MSVC, `/MT`) on X4Native, plus Lua UI and
  MD scripts. `mod/native/core/` is pure C++ with no X4 headers; Winsock is isolated in one
  file so a later POSIX port swaps one file (MOD §2.1, §8.2).
- **Consequences:** Protected UI Mode must be off (PIT-003). Linux is out of scope for v1.

### ADR-004 Pinned game build; refuse on mismatch (Lead)
- **Context:** X4Native RVAs and MD event ids are per build (PIT-045). PROTO §4.2 made a
  game-build mismatch a warning by default; MOD §1.5 allowed an `allow_unsupported_build` override.
- **Decision:** pinned to **X4 9.00 build 611726** and **X4Native `v9.0.0-611726`**. The mod
  refuses to connect on an unsupported build (no user override in release builds). The
  server rejects any node whose `game_build` is not in `SupportedGameBuilds` (default
  `["900-611726"]`) with `GameVersionMismatch`, and any node whose game build differs from
  the authority's. `mod_version` + `mod_build` must equal the authority's exactly
  (`ModVersionMismatch`). `extensions_hash` (enabled DLC + extensions) must match
  (`ExtensionsMismatch`; admin may downgrade to a warning). `RequireSameGameBuild` is
  removed as a setting; it is always on.
  **Amended 2026-10-01 (ADR-043, ADR-044):** the hash covers enabled DLCs and
  simulation-affecting extensions only; a client-only library allowlist is excluded, and a
  hash mismatch is resolved against the session mod policy instead of failing outright.
- **Consequences:** every X4 patch needs a new X4Native release and a new mod release. The
  supported-build list lives in one constant shared by mod and server (generated from
  `protocol/schema/common.fbs` constants or a shared JSON in `protocol/`).

### ADR-005 Encoding: FlatBuffers + one hand-packed codec (Lead)
- **Context:** SRV §1.3 preferred hand-written LE structs (no NuGet); PROTO §2 chose
  FlatBuffers. ~110 message types must exist in C++ and C#.
- **Decision:** **FlatBuffers for every control, session, world-event, team, economy, admin
  and telemetry message** (one root table per `MsgType`, no top-level union). The only
  hand-packed format is the field-mask entry list inside `Replication.entries:[ubyte]`
  (PROTO §10.2), with byte-exact golden vectors. One `flatc` version is pinned in
  `tools/flatc/`. Generated code is produced at build time and not committed. C++ runs the
  FlatBuffers `Verifier` on every inbound frame; C# decodes inside a guard and maps any
  failure to `Disconnect(MalformedMessage)` + a violation.
- **Consequences:** server adds the `Google.FlatBuffers` package. SRV §1.3 and task M0-8 are
  superseded. Python bindings are optional (only for tooling such as golden-vector checks).

### ADR-006 Transport, ports and lanes (Lead)
- **Decision:** defaults **TCP 47780** (Control + Bulk lanes, and Realtime when UDP is
  unavailable), **UDP 47781** (Realtime lane, capability `UdpRealtime`), **HTTP 47790**
  (admin GUI, REST, SignalR, save HTTP fallback). Each is configurable. 8-byte TCP frame
  header (`u32 len | u16 type | u8 flags | u8 lane`), 24-byte UDP datagram header with
  `seq/ack/ack_bits`, max datagram 1200 B, `MaxFrameBytes` 1 MiB (PROTO §3). Writers drain
  strictly **Control > Realtime > Bulk**. LZ4 (`Lz4Frames`) is defined but off in v1.
- **Consequences:** replaces SRV's 7778/7779/7790 and REQ-001's 7778. REQ-002 ("TCP only
  in v1") is relaxed: UDP is optional and capability-negotiated, never a user-chosen mode.
  The server implements UDP in M1 (can slip without blocking); the mod implements it in M4.

### ADR-007 Save transfer: in-band Bulk by default, HTTP fallback (Lead)
- **Decision:** saves and manifests move as windowed `SaveChunk`s (256 KiB, window 8, ack
  every 4) on the Bulk lane in both directions. HTTP `GET /files/saves/{sha256}` on 47790
  with a 1 h player-bound token is a fallback (capability `SaveHttp`). Saves are
  content-addressed on the server. The authority's own save slot is named
  `x4mp_<sessionId>_<n>`; downloaded copies are stored as `x4mp_<sha12>.xml.gz` in
  `GetSaveFolderPath()`.
- **Consequences:** the mod needs no HTTP stack in v1. MOD §7.4 ("in M6, download over HTTP")
  is superseded: in-band download ships in **M2** (joining needs it).

### ADR-008 Checkpoints: SaveStarted marker, journal, manifest (Lead)
- **Decision:** a checkpoint = authority save + `SaveStarted{checkpoint_id, game_time,
  next_net_id}` sent on Control **in the same main-thread frame** as the `SaveGame` call,
  after ghosts are stripped + a manifest file (`X4MF`, FlatBuffers) uploaded with the save.
  The server journals every persistent-entity mutation after the marker. Joiners load the
  checkpoint, match the manifest, then replay `WorldCatchUp` from the journal. A save
  uploaded with `ghosts_cleaned=false` is stored but never made current.
- **Consequences:** the manifest replaces MOD's sidecar `x4mp_<session>_<n>.x4mp.json` as
  the canonical record of net_ids, avatar registry and `inherit_team` status; the authority
  may also keep a local copy for crash recovery. Team table and wallets are server-owned and
  never in the sidecar. Cadence is **server-driven** (`RequestSave{Autosave}` every
  `AutosaveMinutes`, default 15); the mod has no autosave timer of its own.

### ADR-009 Static entity binding via the manifest, not ID hashing (Lead)
- **Context:** MOD §3.5 proposed deterministic station NetIds (hash of match key); PROTO
  §8.4 used manifest-assigned ids.
- **Decision:** stations, gates, accelerators, highway entries and save-time deployables get
  ordinary authority-allocated `net_id`s recorded in the checkpoint manifest. Each node
  binds them by match key **(sector macro, macro, position rounded to 50 m)**, tie-break by
  owner then `idcode`, then nearest exact position within 1 m, performed while paused right
  after load. `ManifestReport` returns match statistics; unmatched stations > 0.5% =>
  `Disconnect(ManifestMismatch)`.
- **Consequences:** no 31-bit hash collisions; ids survive authority migration. MOD §3.5,
  §4.2 and M4 task 2 are amended.

### ADR-010 Entity identity
- **Context:** SRV §2.6 used X4 `UniverseID` (u64) as entity id and assumed it is stable
  across machines; PROTO/MOD/API showed it is per-process.
- **Decision:** wire identity is **`net_id: u32`**, authority-assigned, monotonic, never
  reused within a session lineage (persisted via `next_net_id` in checkpoints and X4Native
  stash). `0` = none, `0xFFFFFFFF` reserved. `UniverseID` appears only as diagnostics.
  Sectors travel as a **`u16` sector index** (1-based, sorted by sector macro) defined by
  `GalaxyMetadata`; each node maps index → macro → local UniverseID using
  `GetComponentData(id,"macro")`. Macros, NPC factions and wares are interned in the
  session **string table**. Players and teams are server-assigned `u16` ids.
- **Consequences:** SRV `EntityState` sketch is replaced by PROTO's `EntityState` (32 B,
  Euler i16 rotation, i32 1/64 m position). MOD's macro-string `sectorKey` becomes the index
  on the wire (macros remain the node-local key).

### ADR-011 Interest management is server-owned (Lead + Consolidation)
- **Decision:** per client, tiers **Near** (15 km, 20 Hz), **Sector** (5 Hz), **Adjacent**
  (1 hop, 1 Hz prefetch), **Linger** (previous sector, 20 s, 1 Hz). Player ships are
  replicated galaxy-wide at ≥ 2 Hz. The only client input is `PlayerState` (position,
  sector); `InterestHint` exists behind capability `InterestHint` and may be ignored. The
  server sends the authority one `CaptureSet` (union of client tiers + admin map views,
  ≤ 1 per 500 ms). A sector's local NPCs are suppressed on a client only after that
  sector's **`SectorComplete`**, which the authority sends only after a full index pass.
- **Consequences:** MOD §3.3 tiers (T0–T3) are the authority's *internal* read schedule
  and must meet the CaptureSet rates (20 Hz focus spheres of `NearRadius`). MOD's
  client-sent `InterestUpdate`, `leave_grace` and lookahead "0 in M4" are dropped
  (`PrefetchDepth` = 1 from the start; Linger replaces `leave_grace`).

### ADR-012 Replication: server mirror + per-client acked baselines
- **Decision:** authority sends `WorldUpdate` (dead-reckoned, changed entities only) and
  reliable `EntitySpawn/Despawn/Change/Cargo`. Server keeps a mirror and per-client
  baselines; `Replication` entries carry **absolute** values for masked fields. Baselines
  advance on UDP ack or on TCP writer flush (never on enqueue). Full-mask keyframes every
  5 s (Near/Sector) / 15 s (Adjacent/Linger). `InterestChecksum` every 5 s +
  `ResyncRequest{sectors}`. Ghosts are despawned **only** on explicit `EntityDespawn`.
- **Consequences:** MOD's `SectorManifest` every 2 s and "missing from two manifests"
  pruning are removed. SRV §2.3's "advance baseline on Queued/Coalesced" is corrected to
  "advance on flush/ack" (PROTO §10.3).

### ADR-013 Client render model: ghosts for dynamics, matching for statics
- **Decision:** the only v1 client mode is ghost rendering (capability `GhostRender`):
  dynamic NPC objects in interest sectors are suppressed locally and replaced by inert
  kinematic ghosts keyed by `net_id`, moved with `SetObjectSectorPos` (Hermite + slerp,
  adaptive interp delay 100–400 ms, extrapolate ≤ 500 ms / 1 s Adjacent). Static objects
  are matched (ADR-009) and updated in place. Bind/pin is not built (`HybridBind` reserved).
  Ghost spawn ≤ 40/frame, materialised ghosts ≤ `max_ghosts` (4000).

### ADR-014 Team faction mapping: symmetric (Consolidation)
- **Context:** PROTO §14.2 and SRV §2.13 map "my own team" to the game's `player` faction
  per viewer, so team k's assets are `player` on its members' nodes and `x4mp_team_k`
  elsewhere. MOD §11.2 recommends **symmetric** mapping: team k is `x4mp_team_k` on every
  node, and `player` owns only the local human's own avatar.
- **Decision:** **symmetric mapping.** Team factions `x4mp_team_1` … `x4mp_team_8`
  (faction slot 1–8; slot 0 = none) ship inactive in `libraries/factions.xml` and are
  activated per session. On every node, team k's assets are owned by `x4mp_team_k`; the
  local human's own avatar is `player`. The authority converts nothing: the faction string
  on the authority maps 1:1 to `owner_team`.
- **Rationale (robustness):** (1) a viewer's team change or an authority migration never
  re-owns the world on any node; (2) the authority's save is identical regardless of which
  human hosts, which makes migration and re-hosting cheap; (3) team income lands in the
  team faction account, never mixed with a human's `player` money, which keeps the ledger
  reconciliation exact; (4) no per-node `player`-ownership side effects (notifications,
  salaries, "player owned destroyed" events) on ghosts.
- **Consequences:** own-team assets are commanded through `Intent{AssetOrder}` from our UI,
  not the vanilla property menu (vanilla "player-view mode" is an M6+ option).
  `native-host` mode (authority's team = `player` on the authority) is not built in v1.
  PROTO §14.2, SRV §2.13 and `teams.fbs` comments are amended; slot range changes from
  0..N-1 to 1..8.

### ADR-015 Client player ships are persistent avatars (Consolidation)
- **Context:** PROTO §13 models client ships as transient "player mirrors" on the
  authority (stripped before save). MOD §3.7/§11.4 makes them real, team-owned, persistent
  **avatars**.
- **Decision:** avatars. On first join the authority spawns the player's avatar
  (`StarterShip` setting, at the team spawn point) under `x4mp_team_k` and returns its
  `EntitySpawn{origin=PlayerShip, controller_player}`. The client spawns a local `player`
  copy and moves the human in with `TeleportPlayerTo`, then suppresses the save's original
  ship. On rejoin the existing avatar is taken over. While its player is online the avatar
  is inert and driven kinematically by `PlayerState`; offline it is a parked ship. The
  authority human keeps the save's player ship. `PlayerShip` (C→S→A) becomes "request my
  avatar" (macro fields used only for diagnostics/ship-swap).
- **Fallback (team factions or TeleportPlayerTo unavailable, spike S1/S2):** PROTO's mirror
  model — non-persistent mirror under a borrowed faction, stripped before each save, all
  teams allied.

### ADR-016 Relation values (Consolidation)
- **Decision:** Allied = **+0.75** (X4 ally band), Neutral = 0, Hostile = −1.0, applied
  via MD `set_faction_relation` and then `set_faction_relation_locked` for every team pair
  and for `player` ↔ each team on each node (own team: +1.0). Team factions get
  `set_faction_diplomacy_exclusion`. Team ↔ NPC relations are copied from the save's
  `player` at session creation and stay fixed until M6 reputation sync (MOD §11.6).
- **Consequences:** SRV's "+1.0 or ally level" is resolved to +0.75.
- **Correction 2026-10-01 (diplomacy research, ADR-047):** `set_faction_relation_locked` is
  **faction-wide** (`common.xsd:35358`), not per pair. Lock only the `x4mp_team_k` factions,
  **never `player`**, because locking `player` would freeze all of the local player's NPC
  relations. To change a team relation, a node unlocks the team faction, sets the relation,
  then relocks it. Re-verify in spike S11.2. MOD §11.6 follows this.
- **Correction 2026-10-06 (M3-31, Finding 18):** player <-> **own team** is **+0.99**, not +1.0. +1.0 is the engine's `self` relation range
  (`libraries/factions.xml` header comment): the map-flip evidence (a team ship briefly `live=1` for the authority's player) says the engine can
  treat a +1.0 team ship as the player's own. +0.99 is still inside the ally band (0.5..1.0), so colours, docking and "friendly" logic are the same.
  The MD apply and its read-back use the value of the plan. `x4mp.json` `diag.team_self_relation_100` writes the old +1.0 again (for comparison runs).
  Team <-> team values are unchanged (Allied +0.75, Neutral 0, Hostile -1.0).

### ADR-017 Team policy defaults (Consolidation)
- **Decision:** `JoinMode=Auto`, `AutoAssign=SingleTeam` (everyone co-op, so
  `CreditMode=Auto` resolves to Shared), `allow_self_team_change=false` (admin-only moves;
  MOD default `admin_only`), `MoveAssetsWithPlayer=ShipOnly` (= MOD `stay` for everything
  but the avatar), `AssetPolicy=SharedCommand`, `DefaultRelation=Neutral`,
  `MaxTeams=8` (= slots), `RelationChangePolicy=AdminOnly`, `LobbyTimeoutSeconds=300`.
  Moving the authority's player is allowed only when the session is not Running.

### ADR-018 Permission gate is the server (Consolidation)
- **Decision:** `AssetPermissionPolicy` in the `SessionActor` checks every client message
  that commands or modifies an asset (PROTO §16.2 table) before forwarding. The authority
  does not re-check. The mod greys out disallowed actions using `TeamTable`,
  `TeamRelations` and `SessionSettings`. Rejections are `IntentResult{Rejected, reason}` +
  a rate-limited `PermissionDenied` event, not protocol violations. Asset ownership in the
  mirror is `(owner_team, owner_player)`; `owner_player=0` = team-common.

### ADR-019 Credits: server ledger, whole credits, local reconciliation (Consolidation)
- **Context:** PROTO uses whole credits; MOD §12.1 proposed int64 cents on the wire; X4's
  money units in Lua/MD are unverified.
- **Decision:** server double-entry ledger is the single source of truth (SRV §2.14).
  **Wire amounts are int64 whole credits**; each node converts at the game boundary (if
  the game uses cents, the node keeps the sub-credit remainder locally). `CreditMode`
  Auto | PerPlayer | Shared; Auto = Shared iff exactly one team. Wallet kinds Player,
  TeamShared, TeamPool, Escrow (+ internal World). Local `player` money is kept equal to
  the human's spendable balance by MOD §12.2's loop: the node sends
  `CreditDelta{seq, amount, source}` for each local change it did not cause, and the server
  echoes `acked_delta_seq` in `WalletUpdate`; the node converges with `AddPlayerMoney`
  (fallback MD `transfer_money`). Game spend that overdraws a wallet is still booked, marks
  it `Overdrawn`, raises an alert, and blocks outgoing player actions until ≥ 0. Team-asset
  income on the authority (team faction account deltas) is reported as `CreditDelta{team_id}`
  and booked to the team pool/shared wallet.
- **Consequences:** schema delta D6. Spike S4 decides units and negative-money behaviour.

### ADR-020 Economy scopes and defaults (Consolidation)
- **Decision:** scope enum **Off | Teammates | Allied | Anyone** (`Allied` = same team or
  an Allied team; SRV's `AlliedTeams` renamed). Defaults: `donate_scope=Teammates`,
  `loan_scope=Teammates`, `trade_scope=Allied`, `team_pool_enabled=true`,
  `pool_withdraw_policy=AnyMember`. Scope is checked at request time and again at
  accept/execute; offers that no longer qualify are cancelled and refunded. Rate limit
  5 requests / 10 s / player. Every request carries a 128-bit `request_key`; a duplicate
  returns the stored result; reuse with a different payload is rejected and counted.

### ADR-021 Loans (Consolidation)
- **Context:** SRV escrows principal at offer and uses `interest_bp` + `auto_repay_pct`;
  PROTO moves nothing until accept and uses `repay_total`; MOD has `autoCollect` at due.
- **Decision:** **principal is escrowed at offer** (acceptance cannot fail for funds;
  decline/expiry/cancel refunds). The wire carries **`repay_total`** (flat, explicit, no
  rounding ambiguity); the server enforces `repay_total ≤ principal × (1 + MaxLoanInterestBp/10000)`.
  Optional `auto_repay_pct` (0–100) diverts that share of the borrower's positive game
  income to repayment (SRV). Due dates are real-time (`due_in_s`, 0 = none). Overdue only
  flags (no seizure) in v1. States: Offered, Active, Overdue, Repaid, Forgiven, Declined,
  Expired, Cancelled.

### ADR-022 Escrowed trades: whole-trade settlement on the authority (Consolidation)
- **Context:** PROTO sends one `AssetTransferOrder` per line, possibly to the piloting
  client, and reverts lines by reverse orders; SRV allows exactly one credits leg, sends
  `TradeExecute`, and has `InDoubt`; MOD settles all ops in one authority job with
  precheck-then-apply and compensation.
- **Decision:** keep PROTO's negotiation (item lists, `TradeCounter` with versions, both
  parties accept the same version). Execution: validate → escrow all credit items →
  one **per-trade `AssetTransferOrder{trade_id, lines[]}`** to the **authority only** →
  authority prechecks every line before mutating anything, journals, applies, and on any
  failure compensates in reverse order → one `AssetTransferConfirm{trade_id, ok,
  failed_line, compensated, error}`. Idempotent by `trade_id`. No reply in 30 s →
  `TradeQuery` (S→A) up to 3× → **`InDoubt`** → admin resolve (complete | refund).
  v1 limits: ware lines must use containers the authority simulates (stations, parked or
  NPC-run team ships); a client's currently piloted ship and any avatar cannot be traded
  or used as a ware container; stations reserved (`trade_stations_enabled=false`); one
  open trade per asset (lock). Optional proximity rule (`TradeRequiresProximity`, default
  true: same sector).
- **Consequences:** schema delta D1–D3. PROTO §15.6 steps 3–6 and SRV §2.14 trade state
  machine are amended. States: Proposed, Countered, Accepted, Escrowed, Transferring,
  InDoubt, Completed, RolledBack, Cancelled, Expired, Rejected.

### ADR-023 Save hygiene (Consolidation)
- **Decision:** every mod-spawned object is in the stash-backed `GhostRegistry` and named
  with the `[MP] ` prefix. Clients never save while connected (MD autosave diff + Lua
  `SaveGame`/`IsSavingPossible` wrap; hook fallback). The authority's vanilla autosave is
  blocked; saves happen only through `SaveJob` on `RequestSave`: freeze replication,
  strip ghosts, `SaveGame`, emit `SaveStarted`, restore ghosts after `on_game_save`. No
  helper satellites, ever. Janitor on load removes `[MP] ` objects and reference leftovers.
  Team-owned assets and avatars are real session data and are saved.
- **Consequences:** session saves depend on the mod (spike/verify V21).

### ADR-024 Threading models
- **Decision (server):** per connection one reader and one writer task; one single-writer
  `SessionActor` loop per session owns all mutable state (no locks); admin side reads
  immutable snapshots (~250 ms) or posts commands; economy mutations commit synchronously
  before ack. **(mod):** X4 UI/main thread does all game calls under a per-frame budget
  (2.0 ms authority / 1.5 ms client); one `net` thread owns sockets, framing, Verifier,
  heartbeats and reconnect; MD callbacks only push PODs into a lock-free `md_ring`.
- **Consequences:** see architecture §7.

### ADR-025 Lifecycle names and reload handling (Consolidation)
- **Decision:** canonical `SessionPhase` = Idle, WaitingForAuthority, AuthorityLoading,
  Running, Paused, AuthorityLost, Migrating, Stopping, Ended. Canonical `NodePhase` =
  Admitted, AwaitingTeam, SyncingSave, Verifying, Loading, Matching, CatchingUp, InGame
  (+ server-side Detached, Failed). On an X4Native extension reload the mod sends
  `Disconnect{code=ClientReload}` (new code 6), stores intent + resume token in the stash,
  and resumes; the server keeps the slot for `ResumeGraceSeconds` and emits no leave/join.
- **Consequences:** SRV's shorter node machine and `SessionPaused` message are replaced
  (`SessionState.paused`). MOD's `Bye{reason=reload}` → `Disconnect{ClientReload}`.

### ADR-026 Canonical timers and limits (Consolidation)
| Setting | Value | Note |
|---|---|---|
| Heartbeat interval / timeout | 1 s / 10 s | PROTO §7 (network thread answers) |
| Handshake timeout | 10 s | |
| `ResumeGraceSeconds` | 60 | client and authority socket loss |
| `AuthorityGraceSeconds` | 120 | `AuthorityLost` → `Stopping`/`Migrating` |
| Control lane soft / hard cap | 8 MiB / 32 MiB | PROTO (SRV's 2/8 MiB superseded) |
| `SlowConsumerTimeoutSeconds` | 15 | oldest Control item age |
| Realtime low / high watermark | 64 KiB / 256 KiB | |
| `MaxFrameBytes` | 1 MiB | |
| `TickRateHz` / `BandwidthBudgetKBps` | 20 / 256 | per client |
| `NearRadius` / `PrefetchDepth` / `LingerSeconds` / `CaptureEvictSeconds` | 15 km / 1 / 20 / 60 | |
| `max_ghosts` / ghost spawns per frame | 4000 / 40 | |
| `AutosaveMinutes` | 15 | server-driven `RequestSave` (SRV/MOD; PROTO's 20 superseded) |
| `MaxPlayers` / `MaxConnectionsPerIp` | 8 / 4 | |
| Intent timeout | 5 s | `Rejected{Timeout}` |
| Trade execute timeout | 30 s, then 3 × `TradeQuery` | then InDoubt |
| `NodeStats` period | 2 s | replaces SRV `NodeTelemetry` (1 Hz) and MOD `NodeStatus` (5 s) |
| Violations | > 20 / min ⇒ close + 5 min IP ban | |

### ADR-027 Canonical message names (Consolidation)
PROTO §20 is the canonical catalog. Old names map as follows:

| Old (doc) | Canonical |
|---|---|
| `Hello` (SRV, MOD) | `ClientHello` (+ `ServerHello` first) |
| `Reject`, `VersionMismatch` (SRV) | `Disconnect{code}` |
| `SessionInfo`, `SessionPaused` (SRV) | `SessionState` |
| `SaveRequired` (SRV), `saveRequired` (MOD) | `RequestSave{SessionStart}` / `SessionSaveInfo` |
| `AssumeAuthority` (SRV) | `AuthorityAssign` |
| `InterestSet` (MOD) | `CaptureSet` |
| `EntityBatch` (MOD) | `WorldUpdate` |
| `Despawn` (MOD/SRV) | `EntityDespawn` |
| `SectorManifest` (MOD) | removed (ADR-012) |
| `OwnerChanged` (MOD), `AssetOwnershipChanged` (SRV) | `EntityChange{Owner, OwnerTeam, OwnerPlayer}` |
| `StationStock`, `PlayerCargo` (MOD) | `EntityCargo` (absolute amounts) |
| `Event.Kill`, `Event.Capture`, `Event.Trade`, `Event.BuildRequest`, `Event.PlayerDied`, `Command` (MOD); `ActKill`, `ActCapture`, `ActTrade`, `ActBuild`, `ActOrder`, `ActRename`, `ActTransferAsset` (SRV) | `Intent{KillClaim, CaptureReport, TradeReport, StationBuildRequest, PlayerDeath, AssetOrder, AssetRename, AssetGift}` |
| `ActRejected` (SRV) | `IntentResult{Rejected}` |
| `Event.Damage` (MOD) | reserved `DamageReport` (0x04xx, M5) |
| `Chat` (MOD) | `ChatSend` / `ChatMessage` |
| `NodeTelemetry` (SRV), `NodeStatus` (MOD) | `NodeStats` |
| `LogLines` (SRV) | `LogForward` (Control lane, ≤ 50 lines/s) |
| `TeamList`, `TeamAssigned`, `TeamChoiceRejected` (SRV) | `TeamTable`, `TeamRequestResult` |
| `TeamChanged` (MOD) | `TeamMemberChanged` |
| `CreditResult` (SRV), `Credits.Result` (MOD) | `EconomyResult` |
| `Wallet.Balance` / `Wallet.Delta` (MOD) | `WalletUpdate` / `CreditDelta` |
| `Credits.Transfer` (MOD) | `CreditTransferRequest` / `DonateRequest` |
| `LoanOfferRequest`, `LoanRepayRequest`, `LoanWithdraw`, `LoanUpdate` (SRV); `Loan.*` (MOD) | `LoanOffer`, `LoanRepay`, `LoanCancel`, `LoanStatus` |
| `TradeProposeRequest`, `TradeRespond`, `TradeWithdraw`, `TradeUpdate` (SRV); `Offer.*` (MOD) | `TradeProposal`, `TradeAccept`/`TradeCounter`, `TradeCancel`, `TradeStatus` |
| `TradeExecute`/`TradeExecuted` (SRV), `Settle`/`SettleResult` (MOD) | `AssetTransferOrder` / `AssetTransferConfirm` (per trade) |
| `TradeStatusQuery` (SRV) | `TradeQuery` (new, D2) |
| `AssetLock` (MOD) | server-side lock only (not a message) |
| galaxy summary (SRV §2.5) | `GalaxySummary` (new, D4) |

### ADR-028 Security model
- **Decision:** threat model = LAN or VPN, hardened for an accidental port-forward. Session
  password, admin password and team password use HMAC-SHA256 challenge-response over the
  `ServerHello` nonce (password never on the wire). `player_key` = 32 random bytes stored
  in the mod config; server stores SHA-256. Resume token 128 bits. No TLS/DTLS in v0
  (reserved capability). Admin GUI: PBKDF2 (600k), strict cookie, `X-X4MP` CSRF header,
  private-network allow-list, CSP, audit log (SRV §4.2–4.3, §7.3). Identity always derives
  from the connection, never payload. Strict size/NaN validation.

### ADR-029 Repository layout and codegen
- **Decision:** top-level `server/`, `mod/`, `protocol/`, `tools/`, `docs/`, `.github/`
  (architecture §11). `protocol/schema/*.fbs` + `protocol/testdata/` (golden vectors; SRV's
  `protocol/testvectors` renamed) + `protocol/cpp/` (hand-written header-only wire helpers).
  The C# runtime (`FrameCodec`, `ReplicationCodec`, `Quantize`) lives in
  `server/src/X4MP.Protocol` and runs `flatc --csharp` as an MSBuild step; C++ runs
  `flatc --cpp` as a CMake custom command. Generated code is never committed.
  `reference/` and `x4-unpacked/` stay out of version control (or are git-ignored).

### ADR-030 Test tooling
- **Decision:** FakeNode is C# (`tools/X4MP.FakeNode`, SRV §6) and is the M1 workhorse
  (authority, clients, swarm, `--verify`, fuzz, teams, economy). The mod gets `FakeGame`
  (in-process `IGame`) for `roles/` tests, a headless C++ test client in M1 for the net
  core, and record/replay + mirror mode in M4 (MOD §8.4).

### ADR-031 Telemetry
- **Decision:** `NodeStats` every 2 s (adds capability/health report: `md_hook`
  installed|fallback, `native_tick`, `team_factions` ok|fallback, adapter state,
  `game_time`). `GalaxySummary` (A→S, 0.2 Hz) feeds the GUI galaxy map. `LogForward` is
  optional (capability). Server metrics via `System.Diagnostics.Metrics`.

### ADR-032 Authority attention policy
- **Decision:** default `attention=native` (no `ActivateObject` forcing, never satellites);
  `attention=activate` is an option measured in M4 (MOD §3.6, PIT-034/038).

### ADR-033 Pre-existing save assets and money (Consolidation)
- **Decision:** at session creation (once, recorded in the manifest), the save's
  `player`-owned assets are re-owned to `x4mp_team_<inherit_team>` (default: the
  authority's team) by a budgeted job; the save's player money seeds that team's wallet
  (Shared) or pool (PerPlayer). New players' wallets start at `StartingCredits`.

### ADR-034 Station economy divergence accepted in v1
- **Decision:** clients' matched stations keep running locally. Only player trade deltas
  replicate (`Intent{TradeReport}` → authority applies with MD `add_cargo/remove_cargo
  exact=` → `EntityCargo`), plus a full stock resync of the station the player is docked
  at every 10 s. Never use `AddTradeWare` or `DropCargo` for cargo.

### ADR-035 Milestone scope shifts (Consolidation)
- **Decision:** in-band save download and load moves into **M2** (joining requires it).
  Teams/avatars (spike-dependent) are in **M3**. Wallet reconciliation, transfers and pool
  are **M5**; loans and trades in game are M5 stretch / M6. The mod's net core is built and
  tested headless against the real server in **M1** (parallel track). M6 keeps installer,
  launcher, health check, HTTP save fallback in the mod and release packaging.

### ADR-036 Schema delta list (apply in M0 task M0-03)
The `.fbs` drafts were never compiled. The following changes bring them in line with this
log; protocol stays at 0.1 until M0 closes.

| # | Change |
|---|---|
| D1 | `AssetTransferOrder` → per trade: `trade_id, lines:[AssetTransferLine{kind, asset, to_team, to_player, ware_ref, amount, dest_asset}], deadline_ms`. `AssetTransferConfirm` → `trade_id, ok, failed_line, compensated, error`. Authority only. |
| D2 | Add `TradeQuery{trade_id}` S→A (0x0815); the authority answers with `AssetTransferConfirm` or `state=Unknown`. Add `TradeState.InDoubt`. |
| D3 | `LoanOffer`: add `auto_repay_pct:ubyte`; comment: principal escrowed at offer. |
| D4 | Add `GalaxySummary` A→S (0x0115): per-sector ship counts by class, station counts. |
| D5 | `faction_slot` range 1..8 (0 = none); comments in `teams.fbs` / `control.fbs` describe symmetric mapping (ADR-014). |
| D6 | `CreditDelta`: add `seq:ulong` (per node, monotonic). `WalletUpdate`: add `acked_delta_seq:ulong`. |
| D7 | `DisconnectCode`: add `ClientReload = 6`. Remove `RequireSameGameBuild` semantics; `ServerHello` carries `supported_game_builds:[string]`. |
| D8 | `Capability`: add `MdHooks`, `NativeFrameTick`, `TeamFactions` (feature report bits). `NodeStats`: add `md_hook_state`, `team_setup_state`. |
| D9 | `EconomyScope` value `Allied` documented as "same team or Allied team". Defaults per ADR-020; `TeamPolicy.allow_self_team_change` default false. |
| D10 | `PlayerShip` comment: request/take over own avatar (ADR-015). `EntityOrigin` keeps `PlayerShip`. |
| D11 | Reserve `DamageReport` id 0x0403. |

### ADR-037 Shared story progression and universe unlocks (user decision 2026-10-01)
**Context:** In vanilla X4, plot progress unlocks content: sectors (e.g. the Boron
storyline opens Boron space), gates, factions, blueprints and features. If each team
progressed independently, a team further along would get extra sectors, which is an
unfair advantage.
**Decision:** Universe-level unlocks are **session-global** and owned by the authority.
When any unlock happens (sector/gate opened, faction or feature unlocked, plot flag that
changes the shared universe), it applies to **every player regardless of team**. Story
missions are experienced **together per team**. In v1 we expect all players to be on one
team, so that means the whole session. Players on a team share plot state, and the
mission steps a team completes advance it for all of its members.
**Consequences:**
- The authority is the single source of plot/unlock state. Clients do not run story
  missions that change the universe on their own (this extends Q7).
- New protocol work: an `UnlockEvent` / `StoryState` snapshot covering sector/gate
  discovery, plot flags, faction/feature unlocks, and per-team mission progress. Joiners
  get it at join.
- A new M2 spike is needed to map how vanilla MD stores plot progress and sector/gate
  unlocks (md/ story_*.xml, gate activation, `known` flags) and whether we can apply them
  on clients through MD.
- Session setting `StoryUnlockScope = Global` (default, the only option in v1). A
  `PerTeam` variant is listed for later and must stay off whenever there is more than
  one team.

### ADR-038 Fog of war: off in v1, planned later (user decision 2026-10-01)
**Decision:** No fog in v1. The `FogOfWar` session option stays in the backlog as a
real requirement. When teams are at war, hostile-team ships and stations are visible
only within radar/known range, so warfare feels realistic. The server's interest
manager must keep a per-team visibility filter hook so it can be added without a
protocol redesign.

### ADR-039 Starting credits are a GUI setting (user decision 2026-10-01)
**Decision:** `StartingCredits` is an admin GUI session setting (Sessions → Settings).
It has presets (0, 100k default, 1M, 10M, custom), and a separate value for "save money
goes to the inheriting team" vs "split among players".

**Update 2026-10-01 (user):** in Shared credit mode the starting amount is added **per member** (a team of 3 at 100k starts with 300k), confirmed by the user. The amount itself stays an admin GUI setting so groups can pick low-funds starts (e.g. 10k) or rich starts. Implemented as the Live setting `Economy.StartingCredits` (M1-E2). The GUI control lands with the Sessions/Settings pages (M1-W5/W6).

### ADR-040 Loan enforcement: flag-only in v1, enforcement options later (user decision 2026-10-01)
**Decision:** v1 overdue loans are only flagged. Planned later as a per-session
`LoanEnforcement` policy:
- `AutoCollect`: take a share of the borrower's income or balance until repaid.
- `Penalty`: late fee or interest bump.
- `Diplomacy`: the borrower's team loses reputation with the lender's team and/or NPC
  factions; repeated defaults can shift the team relation toward Neutral/Hostile.
- Admin-only seizure of pledged assets.
The ledger and Loan model must record due/overdue events and default history now so
these can be added without migrating data.

### ADR-041 FlatBuffers 25.2.10 pin and C# verifier workaround (M0-04, 2026-10-01)
**Context:** flatc 25.12.19 generates C# that needs a newer `Google.FlatBuffers` runtime than
NuGet has, so flatc and the runtime are pinned together at **25.2.10**. That C# runtime's
`Verifier` has two bugs: `VerifyUnion` reads the union type at the wrong offset, and buffers
over 32 KB hit an internal `Convert.ToInt16` overflow that silently skips verification.
**Decision:**
- C# runs the `Verifier` only on payloads of 32,767 bytes or less without unions.
- Every decode also does a full `UnPack` read inside the guarded decoder, so any malformed
  buffer becomes `ProtocolViolation`. Managed bounds checks make that memory-safe.
- C++ (M0-07, mod) always runs the C++ `Verifier`, which doesn't have these bugs, then
  walks the union messages (Intent, GameEvent, AdminCommand, WorldCatchUp) fully.
- Cross-language golden tests compare decoded fields for FlatBuffers tables, because vtable
  layout may differ between builders. Headers and Replication entries stay byte-exact.
**Consequences:** re-check on every FlatBuffers upgrade. A Dependabot bump must keep flatc and
NuGet versions equal (`tools/flatc/flatc.lock.json`).

### ADR-042 Spike session 1 consequences (2026-10-01)
- **Teams (ADR-014) and avatars (ADR-015) are confirmed in game.** The fallbacks are retired.
  `TeleportPlayerTo` is always called with `force=true`, because without it the game gates it behind
  a research unlock.
- **Ghost budget:** default `max_ghosts = 250`, configurable by the server. Frame cost is
  rendering (~+5 ms per 250 inert S ships on the test PC), not per-frame repositioning.
- **Save files (confirmed 2026-10-02):** the save header `<player money>` and `<account amount>` are **whole credits**; per-transaction fields (logbook `money`, trade-offer `price`) are **cents** (research/save-analyzer-notes.md, user-confirmed balance).
- **Money units:** MD `player.money` and `AddPlayerMoney` use cents; Lua `GetPlayerMoney` uses
  credits. The mod converts at that edge, and the wire stays in whole credits. In-game balances
  clamp at 0, so the server must prevent overdraft in shared wallets, and any debt lives only
  in the server ledger.
- **Team factions have their own in-game money account** (`transfer_money` works both
  ways), so a team pool can be mirrored to the faction account. That's optional, and the
  server ledger stays authoritative.
- **Main menu (corrected):** the `debug` *global* is nil, but `require("debug")` is the path X4Native
  itself uses for its settings menu, and the spike didn't test it. Menu injection keeps the guarded
  probe-first design (mod-design §7.2). Probe order: `require("debug")`, then native `lua_getupvalue`
  through the X4Native-provided `lua_State`, then the standalone menu. V20 is retested in session 2.
  (Probe order superseded by ADR-043: UIX accessor first, row append before the standalone menu.)
- **Destroy:** MD/self-destruct leave wrecks. Use them for kill visuals. Ghost despawn goes through
  the guarded remove.

### ADR-043 Library mods: SirNukes reference only, kuertee UIX optional at runtime (user decision 2026-10-01)
**Context:** research in [research/library-mods.md](research/library-mods.md). SirNukes Mod
Support APIs (MIT, ~77k Workshop subscribers, no 9.00 release) and kuertee UI Extensions
(no license, two content ids, 9.00-compatible) are the two common X4 library mods. Neither
uses the Lua `debug` library. SirNukes wraps `OptionsMenu.displayOptions` late (after its MD
`Lua_Loader` Ready), which hides the `config` upvalue, and replaces the chat globals
`OnlineSendChatMessage` / `OnlineGetChatMessages` / `OnlineGetUserName`.
**Decision:**
- **SirNukes: reference only, mandatory coexistence.** We take no dependency. We may
  reimplement its upvalue-free row-append technique (MIT, attribution if code is copied).
  X4MP must work with it installed.
- **UIX: optional, detected at runtime, narrow use.** Only `OptionsMenu.uix_getConfig()`
  as `config` source 1. Nothing in `content.xml`, no install step, no kHUD or callbacks, no
  copied code, never redistributed.
- **Menu (MOD §7.2):** capture `config` at file load; search several vanilla functions
  (`displayOptions`, `createOptionsFrame`, `displayOption`) and validate the candidate
  (`optionDefinitions.main` array + numeric `optionsLayer`). Source order: UIX accessor →
  `require("debug")` → native `lua_getupvalue` → upvalue-free row append → standalone menu.
  The adapter logs `source=uix|debug|native|append`.
- **Chat (MOD §7.6):** wrappers capture the *current* global at wrap time, delegate, merge
  with the previous `OnlineGetChatMessages`, pass `/…` text through, re-wrap on identity
  change, and unwrap only if the global is still ours. **Join (MOD §7.4):** ignore
  placeholder `OnlineGetUserName()` values; the Join dialog stays in Lua (never SirNukes'
  MD Simple Menu, which routes field values through the MD blackboard).
- **Handshake (PROTO §4.2, MOD §6.4):** `extensions_hash` covers enabled DLCs and
  simulation-affecting extensions only. A shipped **client-only library allowlist**
  (`kuerteeUIExtensionsAndHUD`, `ws_3477279743`, `ws_2042901274`, `ws_3514258146`) and
  `x4native`/`x4mp` are excluded. The full list is still sent; library differences show
  as info, never `ExtensionsMismatch`.
- **Testing:** a compatibility pass before each mod release (dev-setup §5) in four
  configurations: none, SirNukes, UIX, both.
**Consequences:** amends ADR-004 and ADR-042 (probe order). The allowlist lives in the
shared constants file. Session-2 retest items in roadmap §4.1.

### ADR-044 Server-side mod management (user decision 2026-10-01)
**Context:** the user wants the server to track each player's mods at connect, to choose
which mods a session uses (enable/disable), and to make installing them easy for friends.
Modded-game support is wanted later. Extension changes need a game restart, so syncing
must happen before X4 starts. Hosting third-party mods is a licensing and security problem.
**Decision** (design in [mod-management.md](mod-management.md)):
- **X4MP never hosts, downloads or installs mods.** Each session mod entry carries optional
  **source links**: a Nexus page (validated `nexusmods.com/x4foundations/mods/<n>`) and/or a
  Steam Workshop id (URLs derived). Our GUI, the in-game rejection screen and the launcher
  only show and open those links.
- **Phase 1, committed to M1 (server/FakeNode) and M2 (mod):** every node reports its full
  extension list (`ExtensionInfo`: id, name, version, source, enabled, content hash, DLL /
  base-game-replacement flags, class hint) at `ClientHello`; the server stores it per player;
  each session has a mod list/policy (Required / Allowed / Blocked per mod, an enable/disable
  switch, a default for unknown mods, Strict or Warn); mods are classified `Dlc | Sim |
  ClientOnly | Unknown` (unknown = sim); rejections carry a structured `ModPolicyViolation`
  (install / enable / disable / update, with links); "Import from authority" fills the list;
  the session save's `<patches>` is shown as "required by save"; GUI Sessions → Mods page and
  a Players → Mods tab.
- **Phase 2, committed to M6 (launcher):** HTTP session mod manifest; the launcher toggles
  enable state in the profile `content.xml` **only for mods already installed**, with backup,
  journal and restore after the session, warns before enabling mods with DLLs or base-game
  replacements, and opens Workshop/Nexus links for missing mods. It never fetches mods.
- **Phase 3, backlog:** modded-game support (compatibility classes, authority-only MD for
  state-changing mods, extension settings sync, saves that require mods, test plan).
**Consequences:** protocol additions (`ExtensionInfo`, `ModPolicy`, `ModPolicyChanged`,
`Disconnect.mod_violation`; `ClientHello.extensions:[string]` deprecated) go into an M1
schema delta. New tables `player_extension_reports`, `session_mod_policy`,
`session_mod_entries`, `mod_catalog`. Open questions MM1–MM9 in mod-management.md §10 use
their recommended defaults until the user answers.

### ADR-045 Mod-management open questions answered (user, 2026-10-01)
Defaults accepted for MM1–MM4 and MM6–MM9 (mod-management.md §10). **MM5 changed:** who can
see players' mod lists is a session setting, `ModListVisibility` = `AdminsOnly` (default) |
`AdminsAndViewers` | `AllPlayers`. Add a `ModEditor` permission for non-admin GUI accounts, so a
helper can curate the session mod list. All edits go to `audit_log`. This affects the M1-X tasks
(server setting, permission, GUI) and M2-X (an in-game read-only view when `AllPlayers`).

### ADR-046 On-foot player presence (requirement accepted, design pending spike) (user requirement 2026-10-01)
**Status update 2026-10-01: plan APPROVED by the user.** M3b = Tier 0 (HUD presence list) + Tier 1 (MP lounge with full actors and the Talk menu). M3c = Tier 2 (any shared room, with nearest-seat/HUD fallback), gated on the S10 room-matching spike. M5b = Talk-menu economy/team actions (send credits, invite to team, trade).
- **Context:** the user wants players who are on the same station or ship to see each other's
  character in the correct position, and maybe interact. X4 interiors are partly static: dock
  areas, module rooms and bridges come from macros. The rest are dynamic interiors (bar, offices,
  crew quarters), created lazily by MD from `object.seed` and torn down in low attention. Some are
  gated by player progress (the bar needs an unlocked black marketeer). Research:
  [research/on-foot-presence.md](research/on-foot-presence.md).
- **Decision (draft):** accept the requirement. Build it in tiers on one actor pipeline:
  - **Tier 0:** a presence list/HUD ("Alice is on this station, in the bar").
  - **Tier 1:** an "MP lounge". This is a mod-created dynamic interior made only of *vanilla*
    corridor and room macros (fixed macro, door and seed; private; non-persistent), reached by an
    MD teleport. Remote players are rendered there as MD cue actors at exact room-local
    positions, walking with `start_actor_walk`, and with Talk → MP conversation choices.
  - **Tier 2:** general on-foot presence in any shared room, with a room key
    (kind, macro, roomtype, container-space anchor) and fallbacks (snap → nearest NPC slot →
    roster). Enabled only where spike S10.2 proves interiors match.
  - On-foot state is relayed by the server between players who share a container; the authority
    is not involved.
  - Target **M3b = Tiers 0 + 1**, **M3c = Tier 2**, and **M5b = interactions** (credits, team
    invite, trade, emotes, follow).
- **Status:** design pending spike block **S10** (session 2, roadmap §4.2). No schema change yet.
  The proposed messages are `OnFootState`, `PlayerAppearance`, `PresenceRoster` and
  `PlayerInteraction` (0x0302–0x0305), plus the capability `OnFootPresence`.
- **Consequences:**
  - MP actors and lounges join the save-strip path and the load janitor (ADR-023).
  - mod-design §4.7 needs an exception, so a docked ship stays visible while players walk on its
    station.
  - Walking on moving (ghost) ships is out of scope.
  - Forcing progress-gated rooms to exist on every node needs a separate user decision.

### ADR-047 Team diplomacy via X4 diplomacy system (proposal, pending spike) (user idea 2026-10-01)
**Status update 2026-10-01: design APPROVED by the user** (server-owned relation matrix + treaties via our own diplomacy screen; vanilla tab read-only; M5 basic proposals, M6 reputation/policy, rest post-v1).
- **Context:** the user suggested using the game's diplomacy system ("the 9.0 diplomacy update")
  for relations between player teams. Research: [research/diplomacy.md](research/diplomacy.md).
  - Diplomacy arrived in **8.00**; 9.00 only tweaked it. It is a `player`-centric minigame: PHQ
    embassy, agents, one influence value, and MD effects hard-coded to `faction.player`
    (`md/diplomacy.xml:3181, 4591`).
  - Its "events" are crises between two NPC factions. It has no proposals, acceptance or
    treaties between other factions.
  - `set_faction_relation_locked` is **faction-wide** (`common.xsd:35358`), not per pair as
    ADR-016 and MOD §11.6 assume.
- **Decision (draft):**
  - The server's relation matrix stays the only source of truth for team↔team relations.
  - Leaders change it through the existing `RelationChangeRequest` / `RelationProposal`, which
    are extended into treaty proposals: Alliance, Ceasefire/Peace and Trade agreement are
    mutual; Break alliance and Declare war are unilateral with a notice period.
  - A new `RelationChangePolicy=Diplomacy` adds cooldowns.
  - Every node's MD applies the result as unlock → set → relock, with a lock `reason` text.
  - **Reuse** the vanilla Diplomacy "Factions and Relations" tab as the read-only view: teams
    are listed, and the lock reason reads "Set by session diplomacy".
  - **Build** our own Team Diplomacy screen (vanilla `Helper` style and diplomacy icons) for
    proposing and answering. A tab inside `DiplomacyMenu` is optional, if spike S11.6 passes.
  - Vanilla agent actions are **not** repurposed: they need a PHQ and agents, and they change
    only `player`.
  - Team factions are kept out of vanilla diplomacy: diplomacy inactive, events not allowed,
    and an exclusion flag on team pairs.
  - **Never lock `player`.**
  - Team↔NPC relations: per team (`NpcReputationMode=PerTeam`) through the M6 reputation sync,
    with today's fixed copy (ADR-016) until then.
  - NPC↔NPC relation changes (interference, Protocol Null, story) are replicated from the
    authority, and client-side diplomacy events are suppressed.
  - ADR-040 `LoanEnforcement=Diplomacy` becomes a standing score per team pair, plus NPC
    reputation penalties, shown in the same screen.
- **Status:** proposal. It depends on spike block **S11** (session 2, roadmap §4.3). No
  schema change yet. Proposed additions: fields on `RelationChangeRequest`/`RelationProposal`
  (`kind`, `treaty`, `proposal_id`, `note`, `state`), and new `NpcRelationReport`,
  `NpcRelations`, `ReputationDelta` and `TeamNpcRelations`.
- **Consequences:**
  - ADR-016's wording "lock each pair" changes to a per-faction lock on team factions only.
  - Targets:
    - **M5:** minimal in-game proposals.
    - **M6:** per-team NPC reputation, NPC↔NPC replication and the `Diplomacy` policy.
    - **Post-v1:** trade agreements, standing/ADR-040, the optional vanilla-menu tab.

### ADR-048 Per-team HQ and research (requirement accepted, design pending spike) (user requirement 2026-10-01)
**Status update 2026-10-01: APPROVED by the user with all recommended defaults HQ-1..HQ-9** (research per team; one HQ per team; HQ granted at session start; blueprints per team, server-enforced; team leader starts research; resources from team HQ cargo; inherit team keeps the save's progression, other teams get the base set; terraforming/HQ warp post-v1; licences per team). Extended by ADR-049 (team origins).
- **Context:** the user wants *"each faction should be able to have their own HQ and research"*,
  where faction means team. Research: [research/team-hq-research.md](research/team-hq-research.md).
  - The PHQ is an ordinary station of macro `station_pla_headquarters_base_01_macro`. The engine
    allows several copies and non-`player` owners. The HQ plot hands it over with `set_owner`.
  - Research is a `player` singleton: `add_research`/`remove_research` (`common.xsd:19956-19976`),
    `HasResearched`, `research.unlocked`. It only runs on a `player`-owned HQ research module, with
    resources from HQ storage.
  - Its effects are mostly local capabilities: teleport, mod crafting, module-blueprint scanning.
  - Blueprints are `player`-only and **add-only** (no MD or native remove). Licences are per
    faction (`add_licence faction=`).
- **Decision (draft):**
  - A server-owned **TeamProgression** record per team covers research, blueprints, licences and
    the Team HQ (`ResearchScope=PerTeam`, `Shared` as an option).
  - Every node applies its own team's set to its local `player` at join, on team change and on
    deltas: research, licences and encyclopedia entries are diffed both ways, blueprints are
    add-only. The server DB is the truth.
  - **Team HQ:** at most one per team, owned by `x4mp_team_k` on the authority. The inherit team
    gets the save's PHQ (ADR-033). Other teams get a spawned PHQ-macro station
    (`TeamHqMode=GrantAtStart` default). Optionally `set_faction_headquarters`.
  - The **server runs research timers.** The authority consumes resources from the team HQ cargo
    all or nothing (`HqResourceOrder`/`HqResourceConfirm`, the ADR-022 compensate pattern).
    Leaders start research (`ResearchPermission=Leader`).
  - Research catalogue classes:
    - `Team`;
    - `Global`: Xenon crisis (ADR-037);
    - `Authority`: HQ warp, post-v1;
    - `Disabled`: SETA (Q4) and diplomacy-agent research (ADR-047);
    - `AllTeams`: venture modules and hidden gamestart items.
  - Blueprints per team are enforced by **server validation of station plans and builds**; local
    extras are cosmetic. Licences are held by team factions and mirrored to `player`.
  - v1 UI: our own Team Research panel. The vanilla research menu through a locally re-owned
    (player-view) HQ comes later.
  - Terraforming and HQ warp are unavailable in sessions until post-v1. Ventures stay client-local.
    Timelines scenarios are out of scope.
- **Status:** proposal. Depends on spike block **S12** (session 2, roadmap §4.4). No schema
  change yet. Proposed messages (block `0x0900`): `ProgressionCatalog`, `TeamProgression`,
  `TeamProgressionDelta`, `ResearchRequest`/`ResearchResult`, `HqResourceOrder`/`HqResourceConfirm`,
  `HqProvision`, `ProgressionReport`, `HqRequest`, and the capability `TeamProgression`.
  Open questions HQ-1..HQ-9 (research doc §8) use their recommended defaults until the user
  answers.
- **Consequences:**
  - ADR-033 gains a step: record the save's PHQ and `player` research/blueprints for the inherit
    team *before* the re-own.
  - New tables: `progression_catalog`, `team_hq`, `team_research`, `team_blueprints`,
    `team_licences`, `team_progression_version`.
  - The M5 station-build path must validate blueprints.
  - Targets:
    - **M5:** catalogue, snapshot/delta, apply-on-join reconcile, inherited PHQ as the team HQ,
      admin grant/revoke, blueprint reports and plan validation.
    - **M6:** HQ spawn for every team, Team Research panel with server timer and HQ resources,
      per-team licences (with ADR-047 reputation), blueprint fan-out.
    - **Post-v1:** player-view vanilla research menu, trickle resources, HQ warp, team
      terraforming, `OnRequest` HQs.

### ADR-049 Team origins: chosen HQ start sector and race-based starting blueprints (user requirement 2026-10-01)
- **Requirement (user):** each team chooses **where its HQ starts** (which sector). That choice,
  and/or the team's **race/culture**, decides the team's **starting blueprints**. Example: a
  Terran-origin team starts with the Terran solar power plant, not the Commonwealth one.
- **Design direction (to be detailed in docs/research/team-hq-research.md §Origins):**
  - **Origin catalogue, generated from vanilla data**, not hand-written:
    `libraries/gamestarts.xml` (84 starts; each has a `location` sector and a `<blueprints>`
    list; races Argon/Commonwealth, Terran, Split, Boron, Paranid, Teladi, pirate/Hyperion/Envoy
    DLC starts). A build-time tool extracts "origin packages" = {race/culture, default sectors,
    blueprint set, optional starting licences/relations} into a server data file.
  - **Team setting `Origin`** = one of the packages (Argon, Terran, Split, Boron, Paranid,
    Teladi, ...) or `Custom` (admin edits the blueprint set). Default for the inherit team:
    `FromSave` (keeps the save's progression, ADR-048 HQ-7).
  - **Team setting `HqStartSector`**, chosen by the team leader in the lobby or by an admin in the
    GUI from a list filtered to sensible sectors: known/unlocked (ADR-037 global unlocks), not
    Xenon/Kha'ak, optionally restricted to the origin's own space; a minimum distance between
    teams' HQs is configurable. A sector may optionally *suggest* an origin (e.g. Terran space →
    Terran), but race and sector stay separate settings unless the admin enables "origin follows
    start sector".
  - **At session start** the authority spawns each team's HQ (ADR-048) at a free position in the
    chosen sector, owned by `x4mp_team_k`. The server seeds the team's blueprint set from the
    origin package. Clients apply it to their local `player` (add-only, server-enforced; ADR-048).
  - **Starting relations/licences** may also come from the origin (e.g. Terran origin starts
    friendlier with Terran factions) via the M6 per-team NPC reputation (ADR-047). Optional.
- **Milestone:** with ADR-048 (M5 first cut: origin + sector + blueprint seed; M6: relations/licences
  from origin, Custom editor).
- **Spikes (add to S12):** spawn the HQ macro at an arbitrary sector position owned by a team;
  grant a race-specific blueprint set on a client and build with it on a team station.
- **Open questions (recommended defaults):** O-1 may two teams share a start sector? [yes, with
  a min-distance]; O-2 can teams pick the same origin? [yes]; O-3 origin locked after session
  start? [yes, admin can override]; O-4 does the start sector also set a starting ship/location
  for the team's players? [yes, players' avatars spawn at their team HQ].
- **Answered 2026-10-01 (user):** O-1..O-4 accepted as defaults. **Addition:** on a player's
  **first** spawn, the authority also spawns that player's **starter scout ship** docked at (or
  next to) their team HQ, so nobody is stranded on the HQ. The ship macro comes from the team's
  origin package: the S-class scout that the matching vanilla gamestart gives (extracted with the
  blueprints), falling back to the `StarterShip` setting (ADR Q12). It is owned by the player's
  team, flagged as that player's avatar ship (ADR-015), and is a one-time grant per player per
  session (tracked server-side, not re-granted on reconnect; respawn after death follows Q8).

---

### ADR-050 Player portal: player-facing web tab for market knowledge and empire organization (user requirement 2026-10-02)

**Status: requirement accepted, parked post-M5.** Design sketch: [research/player-portal.md](research/player-portal.md).
- **Context:** the user wants a tab players sign in to, showing the trade offers and stations with live
  trade info their team knows about, general knowledge for planning trades and builds, their own assets
  including **fleets and groups of ships**, and a way to keep **naming conventions and notes** about their
  empire across play sessions.
- **Decision:**
  - A separate player area (`/portal/*`) on the existing HTTP server; a player only sees their own team.
    Passwordless sign-in through a one-time link/code issued in game, bound to `player_key`.
    LAN/VPN only, like the admin GUI; admin can disable it (`PlayerPortal.Enabled`).
  - **Market visibility is strict by default** (only what the team could see in game, last-seen values
    otherwise), with an admin setting `PlayerPortal.MarketVisibility = Strict | KnownStations | Everything`.
    The server computes per-team coverage (the engine only does it for `player`); shared with fog of war
    (ADR-038).
  - Static game knowledge comes from the host's own install at runtime; X4MP never ships Egosoft data.
  - Notes and naming conventions live in the server DB, keyed by stable ids (X4 id codes, sector macros),
    team-shared or private. Renames go through the normal `AssetRename` intent (M1-T4 permissions).
  - Phases: P1 knowledge + assets/fleets + notes/naming; P2 market snapshots + visibility; P3 planners.
  - P3 trade-route finder takes the TaterTrader "DeadTater" auto-trade logic as a design reference (user idea 2026-10-02; reference only, own implementation).
- **Consequences:** needs commander/subordinate relationships from the authority (schema change), a market
  snapshot message (Bulk lane), and spike items for the trade-offer API and X4's visibility rule.

### ADR-051 Chosen avatar appearance (race + variants) for remote rendering (user requirement 2026-10-02)

**Status: accepted; implemented with M3b (schema + server) and used by M3b/M3c rendering.** Extends ADR-046 (on-foot
presence) and ADR-049 (team origins).
- **Context:** the user wants each player to choose the race they render as for other players, defaulting to the
  background/race/start sector they chose.
- **Decision:**
  - Each player has an **appearance**: race (Argon, Teladi, Paranid, Split, Boron, Terran, … whatever the game build
    offers) plus **variants** where the game has them (body/gender, outfit/clothing; head/face if exposed).
  - **Default** = the race of the player's team origin (ADR-049 gamestart/origin package); **free choice** afterwards,
    changeable at any time (no admin restriction).
  - **Others only:** it controls how *other* players see you (remote actors in the MP lounge/M3b and shared rooms/M3c,
    plus a race icon in the HUD presence list). Your own local character is unchanged.
  - Remote players are spawned as NPC actors, so any race with NPC character macros works even without a playable model.
  - The server stores the appearance per player (persists across sessions, like the player row), validates it against
    a per-build **appearance catalogue** (allowed character macros + variants, read from the host's install / authority
    at runtime; never shipped), and relays `PlayerAppearance` (Control lane, ADR-046 / on-foot-presence.md §3.1) at join
    and on change. The message carries the chosen race and variant refs rather than mirroring the local character.
  - UI: a picker in the X4MP in-game menu (preview if feasible); later also in the player portal (ADR-050).
- **Consequences / spike items:** list the NPC character macros per race and their variant options (body, clothing,
  heads) for build 611726; confirm spawning a temporary actor with a chosen macro/variant in a room (S10); decide the
  fallback when a macro is missing on a client (e.g. DLC race not enabled → nearest race + log).

## Part 2. Open questions for the user

These are product decisions only. Each has a recommended default that the build will use
unless you say otherwise.

| # | Question | Recommended default |
|---|---|---|
| Q1 | **Fog of war between teams.** Should players see hostile/neutral teams' ships and stations they could not see in single-player? | **No fog in v1.** Everything in a client's interest area replicates like NPCs. Add a `FogOfWar` session option later (filter hostile-team entities beyond radar range). |
| Q2 | **Interest model beyond "current sector + 1 hop".** Do you want map-wide live NPC traffic (big bandwidth/FPS cost) or deeper prefetch? | **Flat tiers: Near / Sector / 1-hop Adjacent / Linger.** Other players are always visible galaxy-wide. Admins can raise `PrefetchDepth` to 2. |
| Q3 | **Max players and teams.** | **8 players, 8 teams** (8 faction slots). `MaxPlayers` editable up to 16 (tested with FakeNode), but authority FPS will limit real games to ~4 clients in different sectors. |
| Q4 | **SETA / time acceleration.** | **Disabled for all nodes during a session.** Only an admin can set a global `time_scale`, and only when every player agrees (vote later). |
| Q5 | **Pause.** | **Local pause menus never pause the world.** Only an admin pause (GUI) pauses the authority and all clients. |
| Q6 | **PvP and friendly fire.** | **PvP allowed only between Hostile teams; friendly fire OFF** (allied/neutral team assets and teammates cannot be targeted). |
| Q7 | **Client players' local missions.** Vanilla mission offers still generate on each client but are not synced. | **Missions are an authority-player-only feature in v1.** Clients can accept missions at their own risk; the UI warns that rewards/targets may not exist on the authority. Revisit in M6+. |
| Q8 | **Player death.** | **Respawn in a new `StarterShip` at the team spawn point**, keeping wallet and team assets. Optional "respawn cost" later. |
| Q9 | **NPCs damaging client players.** Without damage relay, client players are effectively invulnerable to NPCs. | **Yes, build damage relay (M5)** and enable by default when available; until then the GUI states that clients are NPC-invulnerable. |
| Q10 | **Who inherits the save's existing player assets and money?** | **The authority player's team** (`inherit_team`), done once at session creation. |
| Q11 | **Starting credits for new players (PerPlayer mode).** | **100,000 Cr** per new player wallet (`StartingCredits`), save money goes to the inheriting team. |
| Q12 | **Starter ship for joining players.** | The save's starting-ship macro if known, else a fixed S-class fighter (`StarterShip` setting). |
| Q13 | **Commanding team NPC ships from clients** (orders via our UI vs. waiting for vanilla-menu "player-view mode"). | **Our UI first (M6), vanilla property menu later.** Until then team NPC assets run their default AI. |
| Q14 | **Internet play.** | **LAN/VPN only** (Tailscale/ZeroTier) in v1; no TLS. Document port-forwarding as unsupported. |
| Q15 | **Loan enforcement.** | **Overdue only flags** (no seizure, no penalty) in v1; optional auto-repay % chosen by the borrower at accept. |

### User answers (2026-10-01)
| # | Answer |
|---|---|
| Q1 | No fog in v1. Fog of war is wanted later for realism when teams are at war → ADR-038. |
| Q2, Q3, Q4, Q5, Q6 | Accepted defaults. |
| Q7 | **Changed.** Teams play the story together, and universe unlocks (e.g. Boron sectors) apply to all players regardless of team, so no team gets an unfair advantage → ADR-037. |
| Q8, Q9, Q10 | Accepted defaults. |
| Q11 | **Changed.** Starting credits are an admin GUI setting → ADR-039. |
| Q12, Q13 | Not asked separately; defaults stand. |
| Q14 | Accepted default (LAN/VPN only). |
| Q15 | Default for v1, plus later enforcement options (auto-collect, penalties, reputation/diplomacy loss) → ADR-040. |

**Mod management (ADR-044):** open questions MM1–MM9 with recommended defaults are in
[mod-management.md](mod-management.md) §10.

---

## Part 3. Needs in-game verification (M2 spike checklist)

> **Spike session 1 ran on 2026-10-01**, full results in [spikes/session-1-results.md](spikes/session-1-results.md):
> - **PASS:** V01, V02 (with `force`), V03 (budget `max_ghosts=250`; the cost is rendering), V04 (MD cents / Lua credits; no negative balance), V08, V09, V10, V13, V14.
> - **NO:** V17 (no velocity).
> - **V20: inconclusive.** The spike checked only the `debug` global, not `require("debug")`, which X4Native uses.
> - **INCONCLUSIVE:** V12 (spike bug, retest in session 2).
> - **S9:** gates/known flags readable and `set_known` persists; gate activation still to test.
> - **Still open:** V05, V06, V07 (need the native DLL).

Merged and de-duplicated from API §7, MOD §10/§11.10/§12.9, PROTO §25 and REQ PIT items.
Priority: **P0** blocks the design (run as M2-spike, in parallel with M1); **P1** blocks a
milestone feature; **P2** is polish/UX. "Fallback" is what we do if the answer is no.

| ID | Pri | Item | Source | Fallback if it fails |
|---|---|---|---|---|
| V01 | P0 | `x4mp_team_*` factions from `libraries/factions.xml` exist after loading a save made **before** the mod was installed; `set_faction_active` works; `SpawnObjectAtPos2(..., "x4mp_team_k")` succeeds; colours render. | MOD 10.1, 11.10.1-2, API 2.5, PIT-013 | ADR-015 fallback: borrowed real factions, mirror avatars, all teams allied |
| V02 | P0 | `TeleportPlayerTo(ship, true, true, true)` into a freshly spawned `player`-owned ship; then suppress the original player ship safely. | MOD 10.15, 11.10.5 | Everyone flies a ghost-mirror model; no per-player avatars |
| V03 | P0 | Ghost cost & behaviour: `SpawnObjectAtPos2` + `ActivateObject(false)` keeps a ship inert; `SetObjectSectorPos` per frame for 200–500 ghosts (CPU ms, stutter, engine effects, collisions); spawn bursts (VRAM, ID map). | API 7, MOD 10.6, PIT-032 | Lower `max_ghosts`/spawn rate; MD `set_object_active`; update far ghosts at 1–5 Hz |
| V04 | P0 | Money API: `GetPlayerMoney()` units (credits vs cents) and cost; `AddPlayerMoney` with negative values; negative balances tolerated; `event_player_money_updated` reliability; MD `transfer_money` between `player`, team and NPC factions; `faction.money`. | API 2.7, MOD 12.9, PROTO 25.6 | Clamp local money at 0 + team debt; MD-only writes |
| V05 | P0 | Extension reload during save load: X4Native stash survives the "Re-discovery" path; whether the DLL is unloaded; net thread joins within 200 ms; resume is invisible to the server. | MOD 10.3, PIT-007 | Keep DLL loaded (pin module) and keep the socket across re-init |
| V06 | P0 | Save control: `SaveGame` sync vs async and hitch size; wrapping Lua `SaveGame`/`IsSavingPossible` from an addon; quicksave path; MD autosave diff applies; strip-and-restore ghosts around a save. | API 7, MOD 10.8, PROTO 25.3 | X4Native `hook_before` on the exported save function |
| V07 | P0 | Which thread runs X4Native MD typed callbacks on 9.00. | API 1.7, 7 | (design already assumes off-thread) |
| V08 | P1 | Relations: `set_faction_relation` + `_locked` between mod factions and with `player`; targeting colours; police reactions; `set_faction_diplomacy_exclusion`; faction tags (`claimspace`, `nodiplomacyselection`). | MOD 11.10.3-4 | Fixed relations without locking; re-apply every 60 s |
| V09 | P1 | Sector list via MD `find_sector multiple="true"`; macro via `GetComponentData(id,"macro")`; gate/highway neighbour graph; galaxy-map coordinates for the GUI. | API 2.3, MOD 3.1, SRV 9.5 | Static galaxy table extracted from `maps/`; force-directed GUI layout |
| V10 | P1 | Enumeration cost: `GetAllFactionShips` full pass (~90k) and any cheaper per-sector enumeration. | MOD 3.2, 10.7 | Longer index period; newcomer discovery via MD events |
| V11 | P1 | Side effects of suppressing local NPC ships on clients (missions, job respawns); can local job spawners be quieted. | MOD 4.3, 10.4 | 1 Hz sweep + MD diffs on job scripts |
| V12 | P1 | Manifest matching rate for stations in real saves; `universe_id_equal` share; can `$x4mp_netid` MD object variables persist through save/load (exact matching). | PROTO 25.1-2, MOD 10.5 | Keep match-key matching only |
| V13 | P1 | MD `add_cargo`/`remove_cargo exact=` on stations and ships, `result` values; `SetComponentOwner` on ships with crew/orders/commander/subordinates (cleanup needed). | API 2.6, MOD 12.9.5-6 | Restrict trades to ships without subordinates |
| V14 | P1 | Lua→MD shim (`AddUITriggeredEvent` → `event_ui_triggered`) latency and param plumbing for batched actions. | MOD 10.9, API 3.4 | Fewer, larger batches |
| V15 | P1 | Hull/shield set via MD `set_object_hull`/`_shield`; hit callbacks on ghosts (damage relay). | PROTO 25.4-5, MOD 5.1 | Kill-claims only; NPC-invulnerable clients |
| V16 | P1 | `SelfDestructComponent` side effects (wreck, MD events) vs MD `destroy_object explosion="true"`. | MOD 5.1 | `SafeRemove` off-screen, MD destroy on-screen |
| V17 | P1 | Velocity readable via `GetComponentData(id,"speed"/"velocity")`. | API 2.2 | Finite differences (current design) |
| V18 | P1 | Station construction plan read on client and replay on authority (`SpawnStationAtPos` plan id, X4Native plan helpers). | MOD 5.4 | Replicate completed stations only |
| V19 | P1 | Attention model: authority FPS with `native` vs `activate` for client-only sectors. | MOD 3.6, PIT-038 | Keep `native` |
| V20 | P2 | Main-menu injection probes on 9.00 (`debug.getupvalue` available); standalone menu over the start menu; X4Native settings `button` type. | API 3.2, MOD 7.2 | Standalone menu via `/mp` and HUD |
| V21 | P2 | Loading a session save without the mod (team-faction objects). | MOD 1.2, 11.8 | Ship `save="1"` in `content.xml` |
| V22 | P2 | Join dialog password edit box (`textHidden`, not `encrypted`). | API 3.3, MOD 7.4 | Plain box + warning |
| V23 | P2 | HUD frame on layer 3 coexists with cockpit/menus; vanilla chat window outside Ventures; `GetChatAuthorColor2` with arbitrary names. | MOD 7.5-7.6 | Notifications only; own chat menu |
| V24 | P2 | `SetOrderParam` param layouts for Attack/MoveTo/DockAt. | API 2.8, MOD 11.6 | Internal `SetOrderParamInternal` |
| V25 | P2 | `set_faction_identity` runtime rename; licences and docking for team ships at NPC stations; `SetComponentName` on ghosts; `ConvertMoneyString`. | MOD 4.7, 11.10.7-9, 12.9.7 | Placeholder names; own formatter |
| V26 | P2 | Save copied from another machine loads without warnings/blocks. | API 2.9 | (reference did this over scp) |
| V27 | P1 | Coexistence with SirNukes Mod Support APIs and kuertee UIX (ADR-043): which `config` source wins; X4Native's Settings → Extensions page still appears with SirNukes; chat round-trip and SirNukes `/command` with our wrappers. | MOD 7.2, 7.6; research/library-mods §4, §6 | Standalone menu; own chat menu |
| V28 | P1 | Extension-list fields in the start menu (`GetExtensionList()`): meaning of `personal` for user-folder vs Workshop mods; `GetModifiedBasegameUIFilesExtensions()` returns names or ids; whether X4 rewrites the profile `content.xml` on exit. | mod-management 1.2, 1.3 | Native folder scan only; launcher refuses while X4 runs |
| V29 | P2 | `C.OpenWebBrowser(url)` with https Nexus/Workshop URLs and `steam://` links (overlay vs external browser). | mod-management 3.5 | Show the URL as text |
