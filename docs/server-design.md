# X4MP Server and Admin GUI: Design

> See docs/architecture.md — it is authoritative where this doc differs.

Status: draft v0.1, consolidated 2026-10-01. Wire names, ports, encoding and timers follow
`docs/protocol.md` and `docs/decisions.md` (ADR-005, ADR-006, ADR-026, ADR-027). The M0/M1
task list in section 8 is superseded by `docs/roadmap.md`.

Scope: the standalone server process (`server/`), its embedded web admin GUI, the
FakeNode simulator (`tools/`), and the M0/M1 work to build them. The X4 mod is out of
scope apart from the contracts the server needs from it.

Locked inputs (from `PLAN.md`): C# / .NET 10 (ADR-001, amended from .NET 8), ASP.NET Core + SignalR for the live GUI,
SQLite persistence, web dashboard served by the server and reachable on the LAN,
server-centric relay. Every X4 instance connects to the server. One instance is the
**authority** and simulates the universe. The others are **clients**. The server never
runs X4.

Lessons carried over from the reference (see `PLAN.md` section 1). The server has to
answer each of these:

| Lesson | Server-side answer |
|---|---|
| Blocking sends dropped the game to 5 FPS | Every send from game logic is a non-blocking `TrySend` onto a per-connection queue. Only the connection's own writer task does I/O (section 2.3). |
| One-sector streaming causes flicker and divergence at sector entry | Tiered interest with adjacent-sector prefetch and hysteresis (section 2.5) |
| Full state every tick costs too much (382 objects at 15 Hz was about 570 KB/s as text) | Binary deltas, per-client baselines, a priority accumulator and a per-client bandwidth budget (section 2.6) |
| Saves moved with scp/SSH | A content-addressed save service with checksums and resumable transfers (section 3) |
| Config through env vars was lost when Steam relaunched X4 | The mod reads a file. The server pushes session settings over the protocol, and the GUI edits them (section 2.10) |
| No auth or versioning in the protocol | Versioned handshake, join password, per-player identity token, role enforcement (sections 2.4 and 7.3) |
| Debugging needed logs from both machines | Nodes can forward log lines to the server, and the GUI log view shows them (section 2.11) |

---

## 1. Solution layout, framework, dependencies, packaging

### 1.1 Repository layout (server-related part)

```
X4MP/
├─ global.json                      # pins .NET SDK 10.0.400, rollForward: latestFeature
├─ Directory.Build.props            # net10.0, Nullable, ImplicitUsings, LangVersion latest,
│                                   # TreatWarningsAsErrors, InvariantGlobalization, Deterministic
├─ Directory.Packages.props         # Central Package Management (one version per package)
├─ X4MP.sln
├─ protocol/                        # owned by the protocol agent: schema/IDL + golden test vectors
│   └─ testdata/*.bin|*.json        # golden vectors shared by C# and C++ tests
├─ server/
│   ├─ src/
│   │   ├─ X4MP.Protocol/           # classlib: message types, framing, codecs, version and capabilities
│   │   ├─ X4MP.Core/               # classlib: domain (sessions, players, relay, interest,
│   │   │                           #   registry mirror, replication, event bus, save store API)
│   │   ├─ X4MP.Transport/          # classlib: TCP (Kestrel ConnectionHandler), UDP, InProc
│   │   ├─ X4MP.Persistence/        # classlib: SQLite repositories, migrations, write-behind queue
│   │   └─ X4MP.Server/             # exe: composition root, admin REST, SignalR hub, auth,
│   │                               #   embedded static GUI, CLI verbs, Windows service
│   ├─ web/                         # React + Vite + TypeScript admin GUI (built into Server)
│   └─ tests/
│       ├─ X4MP.Protocol.Tests/     # codec round-trips, golden vectors, framing fuzz
│       ├─ X4MP.Core.Tests/         # interest, replication, send queue, state machines
│       ├─ X4MP.Server.Tests/       # integration: WebApplicationFactory + real sockets + FakeNode
│       └─ X4MP.LoadTests/          # console harness, run nightly or by hand (not in PR CI)
└─ tools/
    └─ X4MP.FakeNode/               # exe + library: fake authority and fake clients
```

**Dependency direction** (enforced by a test that inspects assembly references):

```
Protocol  <-  Core  <-  Transport  <-  Server
                 ^        Persistence <-'
FakeNode -> Protocol (+ Core types for the deterministic sim helpers only)
```

- `X4MP.Protocol` uses only the BCL, plus whatever serializer `protocol.md` chooses. It
  contains `TcpNodeClient`, a small client-side connector that FakeNode and the tests
  use. This keeps FakeNode from depending on server internals.
- `X4MP.Core` has no ASP.NET dependency, only `Microsoft.Extensions.*.Abstractions`.
  All networking goes through the `INodeListener` and `INodeConnection` interfaces in
  section 2.2, so the protocol and transport can change without touching domain code.
- `X4MP.Transport` adapts concrete transports to those interfaces. The `InProc`
  transport (paired `System.IO.Pipelines.Pipe`s) shows the design is transport-agnostic
  and gives fast deterministic tests.

### 1.2 Target framework

- All C# projects target `net10.0` (LTS, supported to Nov 2028; switched from .NET 8 on
  2026-10-01, see ADR-001).
- `X4MP.Protocol` also targets `netstandard2.1` **only if** the protocol agent wants to
  reuse it elsewhere. Otherwise `net10.0` only.
- RIDs we publish: `win-x64` (primary) and `linux-x64` (the server is cross-platform even
  though the mod is Windows-first).

### 1.3 NuGet dependencies (minimal, each justified)

Runtime (`X4MP.Server` and its libraries):

| Package | Project | Why it is needed / why not built-in |
|---|---|---|
| `Microsoft.Data.Sqlite` | Persistence | SQLite ADO.NET provider. Locked decision. Includes `SQLitePCLRaw.bundle_e_sqlite3`, so no system SQLite is needed. |
| `Dapper` | Persistence | Thin row mapping. We considered EF Core and rejected it as overkill for about 10 tables: it is bigger, needs migration tooling and starts slower. Hand-written SQL with embedded migration scripts is easy to review. |
| `Serilog.AspNetCore` | Server | Structured logging with console and rolling-file sinks (brought in transitively) and config binding. Built-in `ILogger` has no file sink. We add a small custom in-memory ring-buffer sink for the GUI live tail (about 60 lines of code, no package). |
| `Microsoft.Extensions.Hosting.WindowsServices` | Server | `AddWindowsService()`: SCM lifetime, event-log integration, and content root fix-up when running as a service. |
| `Microsoft.Extensions.FileProviders.Embedded` | Server | `ManifestEmbeddedFileProvider` serves the built SPA from inside the single-file exe. |
| `Google.FlatBuffers` | Protocol | Runtime for `flatc --csharp` generated tables (ADR-005). Only the `Replication` entry codec is hand-written with `BinaryPrimitives`. |

Already in the ASP.NET Core shared framework, so **no package** is needed: Kestrel (HTTP and
raw TCP via `ConnectionHandler`), SignalR server, `System.IO.Pipelines`,
`System.Threading.Channels`, `System.Diagnostics.Metrics`, cookie authentication, rate
limiting (`Microsoft.AspNetCore.RateLimiting`), `System.Text.Json` (with source generation),
and `Rfc2898DeriveBytes.Pbkdf2` for password hashing.

Test-only:

| Package | Why |
|---|---|
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Test framework |
| `Microsoft.AspNetCore.Mvc.Testing` | `WebApplicationFactory` for in-process API and hub tests |
| `Microsoft.AspNetCore.SignalR.Client` | Lets tests drive the admin hub |
| `Shouldly` | Readable assertions. We use it instead of FluentAssertions because FluentAssertions changed to a commercial license in v8. |
| `coverlet.collector` | Coverage in CI |

Deferred (not in M0/M1): `OpenTelemetry.Exporter.Prometheus.AspNetCore` for a `/metrics`
scrape endpoint, and `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` if map frames turn
out to be too large as JSON.

Web (`server/web/package.json`):
- Runtime: `react`, `react-dom`, `react-router-dom`, `@microsoft/signalr`, `uplot` (a tiny
  canvas time-series library for sparklines and rate charts, about 45 KB).
- Dev: `vite`, `@vitejs/plugin-react`, `typescript`, `vitest`,
  `@testing-library/react`, `jsdom`, `eslint` (+ `typescript-eslint`),
  `@playwright/test`.
- No UI component library and no state library. The app is small: we use plain CSS with
  custom properties for theming, and a `useHub()` hook over React context.

### 1.4 Single-file publish

`server/src/X4MP.Server/X4MP.Server.csproj` publish properties:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <AssemblyName>x4mp-server</AssemblyName>
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract> <!-- e_sqlite3 -->
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <PublishTrimmed>false</PublishTrimmed>   <!-- SignalR/Dapper/Serilog config use reflection -->
  <PublishReadyToRun>true</PublishReadyToRun>
  <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
</PropertyGroup>
<ItemGroup>
  <EmbeddedResource Include="..\..\web\dist\**\*" LinkBase="wwwroot" />
</ItemGroup>
```

- An MSBuild target `BuildWeb` runs before `BeforeBuild` when `'$(SkipWebBuild)' != 'true'`.
  It runs `npm ci && npm run build` in `server/web` (incremental: inputs are `web/src/**`,
  `package-lock.json`; output is `web/dist/index.html`). CI builds the web app once in its
  own job and passes `-p:SkipWebBuild=true`. If `web/dist` is missing, the build embeds a
  placeholder `index.html` that says "GUI not built". The server still runs.
- Command: `dotnet publish server/src/X4MP.Server -c Release -r win-x64 -o out/win-x64`.
  The result is one `x4mp-server.exe`, expected around 45 to 60 MB compressed.
- AOT is not used, because SignalR and Dapper are not AOT-friendly (revisit on .NET 10 if startup size matters).

### 1.5 Runtime modes and CLI

`x4mp-server [verb] [options]`. We parse with plain `args` handling and no
System.CommandLine package. The verb set is small.

| Verb | Behaviour |
|---|---|
| *(none)* / `run` | Console host. Ctrl+C performs a graceful shutdown: it notifies nodes, flushes the DB and closes the session. |
| `service install [--name X4MP] [--data <dir>]` | Calls `sc.exe create <name> binPath= "<exe> run --service --data <dir>" start= auto` and sets the recovery policy (restart after 10 s). Must run elevated. If it is not elevated, it prints the exact `sc.exe` commands instead. |
| `service uninstall` | `sc.exe stop` then `sc.exe delete`. |
| `set-admin-password [--user admin]` | Prompts without echo and writes the PBKDF2 hash to the DB. This is the recovery path for a lost password. |
| `create-token --name ci [--role viewer]` | Creates an API bearer token and prints it once. |
| `version` | Prints server version, protocol version range and build hash. |

- `--service` (or detected `WindowsServiceHelpers.IsWindowsService()`) calls
  `builder.Services.AddWindowsService(o => o.ServiceName = "X4MP")`.
- **Data directory** (DB, saves, logs, generated TLS cert): `--data`, else `X4MP_DATA`,
  else `%ProgramData%\X4MP` when running as a service, else `./data` next to the exe.
  Its layout is `x4mp.db`, `saves/`, `logs/`, `uploads/` (temporary parts), `certs/`.
- On Linux, the same binary runs as a console process and includes a sample systemd unit
  in the docs (`Type=notify` is not needed).

---

## 2. Internal architecture

### 2.1 Process overview

```
                       ┌─────────────────────────── x4mp-server ───────────────────────────┐
 X4 authority ──TCP──▶ │ Kestrel:47780 ConnectionHandler┐                                  │
 X4 clients   ──TCP──▶ │                                 ├─▶ NodeGateway ─▶ SessionActor ───┤
 (opt.)       ──UDP──▶ │ UdpTransport:47781 ─────────────┘   (handshake,     (one per       │
                       │                                      auth, roles)    session:      │
                       │   per-connection: Reader loop ▶ inbound Channel     registry,     │
                       │                   SendQueue(lanes) ▶ Writer loop    interest,     │
                       │                                                     replication)  │
                       │                                                         │         │
                       │      EventBus (Channels) ◀───────────────────────────────┘         │
                       │        │          │              │             │                   │
                       │   Persistence  AdminBroadcaster  AuditLog   MetricsSampler         │
                       │   (SQLite WB)  (SignalR, 1-4Hz)                                    │
                       │                                                                    │
 Browser ──HTTP/WS───▶ │ Kestrel:47790 /api/v1/*  /hubs/admin  /files/saves/*  /  (SPA)     │
                       └────────────────────────────────────────────────────────────────────┘
```

Default ports (ADR-006): node TCP **47780**, node UDP **47781**
(optional realtime lane), admin HTTP(S) **47790**. Each one can be configured separately and
bound to its own address.

**Threading model.** Threads are not shared across sessions, and there are no locks
around domain state:

1. **I/O tasks.** Each connection has one reader task and one writer task (both async,
   on the thread pool).
2. **SessionActor.** One logical single-threaded loop per session. It owns all mutable
   session state: players, the registry mirror, interest sets and replication baselines.
   Inputs arrive on one `Channel<SessionInput>` (inbound messages, timer ticks, admin
   commands, connection events). Nothing else touches its state, so it needs no locks.
   M1 supports exactly one active session. The design allows N.
3. **Admin side.** It never reads actor state directly. It either (a) reads the last
   immutable `SessionSnapshot` the actor publishes about every 250 ms (a `volatile`
   reference swap), or (b) posts a command with a `TaskCompletionSource` reply, for
   example `KickPlayer`.
4. **Background services** (`IHostedService`): persistence writer, metrics sampler, admin
   broadcaster, log ring buffer, and save-store janitor.

If profiling later shows the actor's fan-out encode is the bottleneck, the replication
step (2.6) can be split into a parallel per-client phase. The baselines are per-client,
so parallelism will not need shared writes.

### 2.2 Transport abstraction (transport-agnostic boundary)

These types live in `X4MP.Core/Net/`. The protocol agent owns frame contents. The server
only needs this shape:

```csharp
public enum Lane : byte { Control = 0, Realtime = 1, Bulk = 2 }   // see 2.3

public interface INodeListener : IAsyncDisposable
{
    string Name { get; }                                 // "tcp", "udp", "inproc"
    IAsyncEnumerable<INodeConnection> AcceptAsync(CancellationToken ct);
}

public interface INodeConnection : IAsyncDisposable
{
    ConnectionId Id { get; }                             // monotonically increasing long
    EndPoint RemoteEndPoint { get; }
    ConnectionStats Stats { get; }                       // Interlocked counters (2.11)
    CancellationToken Closed { get; }

    /// Reads one decoded frame; returns null on orderly close. Called only by the reader loop.
    ValueTask<InboundFrame?> ReadAsync(CancellationToken ct);

    /// NEVER blocks and NEVER throws for flow-control reasons.
    SendResult TrySend(OutboundFrame frame);

    /// Pull-model hint for realtime producers: true if the realtime lane is below its low watermark.
    bool CanAcceptRealtime { get; }

    /// Optional second path (UDP) bound after handshake; Realtime lane prefers it when present.
    void AttachDatagramPath(IDatagramPath path);

    void Close(DisconnectReason reason, string? detail = null);
}

public enum SendResult { Queued, Coalesced, DroppedLane, ClosedOverflow, Closed }

public readonly record struct InboundFrame(ushort MessageType, byte Flags, uint Seq,
                                           IMemoryOwner<byte> Payload, long ReceivedTicks);

/// Ref-counted encoded frame so one encode can fan out to N connections.
public sealed class OutboundFrame
{
    public Lane Lane { get; }
    public ushort MessageType { get; }
    public ulong CoalesceKey { get; }        // 0 = never coalesce
    public ReadOnlyMemory<byte> Bytes { get; } // fully framed bytes (header+payload)
    public OutboundFrame AddRef();             // Interlocked increment
    public void Release();                     // returns ArrayPool buffer at 0
}
```

Framing is behind `IFrameCodec` from `X4MP.Protocol`:

```csharp
public interface IFrameCodec
{
    int MaxFrameSize { get; }                                         // e.g. 1 MiB (config)
    bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out InboundFrame frame); // throws ProtocolViolation
    OutboundFrame Encode<T>(Lane lane, in T message, ulong coalesceKey = 0) where T : IMessage;
}
```

**TCP implementation.** We use Kestrel's raw-TCP `ConnectionHandler`, configured with
`options.Listen(ip, 47780, l => l.UseConnectionHandler<NodeConnectionHandler>())`. This
gives us `IDuplexPipe` (System.IO.Pipelines), memory pooling, connection limits and
graceful shutdown with no custom socket code. `TcpNodeConnection` wraps
`ConnectionContext`. `NoDelay = true`. Pipe options: `PauseWriterThreshold = 1 MiB`,
`ResumeWriterThreshold = 512 KiB`.

**UDP implementation.** `UdpTransport` uses one `Socket` and a receive loop with
`ReceiveFromAsync` and pooled buffers. Datagrams carry the 64-bit `UdpToken` issued in the
TCP handshake, and the transport maps token to connection. It is used only for the
`Realtime` lane, and only if both sides advertise the `udp-realtime` capability. If the
token is wrong or unknown, the datagram is dropped silently and counted. TCP remains the
connection of record. If UDP fails, the connection falls back to TCP.

**InProc implementation.** A pair of `Pipe`s, used by tests and by FakeNode's `--inproc`
mode.

### 2.3 Per-connection send queue and backpressure (non-blocking sends are mandatory)

`SendQueue` holds three lanes. Producers (the actor, admin commands, the save service)
only call `TrySend`, which is O(1) and never awaits. One **writer loop** per connection
drains the queue in strict priority order **Control > Realtime > Bulk**, copies the bytes
into `PipeWriter`, and calls `FlushAsync`. Only the writer awaits flushes, so a slow
socket stalls only its own writer.

| Lane | Contents | Policy | Overflow |
|---|---|---|---|
| Control | handshake, ping/pong, session state, chat, game events (kill, capture, trade), despawns, admin notices | Reliable FIFO. Soft cap 8 MiB, hard cap 32 MiB (ADR-026). | Hard cap reached, or the oldest item has waited more than `SlowConsumerTimeout` (default 15 s): `Close(SlowConsumer)`. Clients reconnect and resync. |
| Realtime | replication frames (deltas/keyframes), player-ship states | **Pull model**: the producer checks `CanAcceptRealtime` (pending < 64 KiB low watermark) before building a frame. If a frame with the same `CoalesceKey` is still pending, it is **replaced** (latest wins). | Above the high watermark (256 KiB): `DroppedLane`. The replication layer keeps its baselines, so nothing desyncs (2.6). |
| Bulk | in-band save chunks, log backfill | Windowed. Sent only when the other lanes are empty. | Producer is flow-controlled by window acks (section 3). It never overflows by design. |

Why this is safe for deltas: a realtime frame advances per-client baselines only when
the writer **flushes** it to the socket (TCP) or the peer **acks** it (UDP), never on
enqueue (ADR-012, protocol.md §10.3). A frame replaced by coalescing is a full replication
frame against the same baseline, so the newer one supersedes the older. Over TCP, an
accepted frame is guaranteed to be delivered in order or the connection dies. On reconnect,
baselines are reset and a keyframe is sent. Over UDP, baselines advance on **acks** (the
protocol must carry an ack field. This requirement goes to protocol.md).

Stats tracked per lane: queued bytes and frames, coalesced count, dropped count, max queue
depth, and the age of the oldest item. The writer also tracks flush time (p50/p99) and
bytes per second. All of these appear on the Diagnostics screen.

### 2.4 Session manager, node lifecycle, roles

**Node connection state machine** (owned by `NodeGateway` until the node is admitted to
a session, then by the `SessionActor`):

```
Accepted ─▶ Handshaking ─(Hello ok, version ok, password ok, not banned)─▶ Admitted
   │             │ timeout 10 s / bad version / bad auth ─▶ Rejected(reason) ─▶ Closed
Admitted ─▶ AwaitingTeam (2.13; skipped if sticky/auto) ─▶ SyncingSave ─▶ Verifying ─▶ Loading ─▶ Matching ─▶ CatchingUp ─(NodeReady)─▶ InGame
InGame ─(socket lost)─▶ Detached (grace = ReconnectGrace, default 60 s, resume token valid)
Detached ─(ClientHello w/ resume_token)─▶ InGame (baselines reset)   |  grace expired ─▶ Left
(canonical NodePhase names: protocol.md §6.2; Disconnect code ClientReload keeps the slot silently)
```

- **ClientHello** (protocol.md §4, after `ServerHello` with nonce) carries: protocol version range, mod version, game build,
  role request (`authority` | `client` | `admin` | `observer`), player name, player
  identity token, HMAC `auth_proof` (never the password), capability flags, and an optional resume token.
- **Version negotiation**: protocol major must match; the session uses the lower minor;
  otherwise `Disconnect(ProtocolMismatch)`. The game build must be in
  `SupportedGameBuilds` (pinned `900-611726`) and equal the authority's, and the mod build
  must equal the authority's; violations are always rejected (ADR-004).
- **Identity**: the mod generates a random 256-bit `playerKey` once and stores it in its
  config file. The server stores `SHA-256(playerKey)` in `players`. A name stays bound to
  the first key seen with it. A different key using a taken name is rejected
  (`NameTaken`), and an admin can release the name.
- **Roles**:
  - `authority`: at most one per session. Its claim is accepted if the session has no
    authority, the node's player id is the session's designated authority, or an admin
    promotes it. Only the authority may send world-state, galaxy-metadata,
    authoritative-event and save-upload messages.
  - `client`: may send its own player-ship state, chat, action requests (build, trade,
    kill reports) and telemetry. The server enforces **entity ownership**: a client may
    only update entity ids assigned to it (its player ship and spawned ghosts it owns).
    Violations are dropped and counted, and after N violations the client is kicked.
  - `admin` / `observer`: protocol-level tools (FakeNode inspector, future CLI). Observers
    get replication with an explicit subscription and may not send state.
- A **role/message matrix** (a table in `X4MP.Core/Session/MessagePolicy.cs`) is the
  single source of truth. Unit tests assert that every message type has a policy entry.

**Session state machine** (`SessionActor`):

```
Idle ─(admin: select save + Start)─▶ WaitingForAuthority ─(authority admitted)─▶
AuthorityLoading ─(GalaxyMetadata + first checkpoint)─▶ Running ◀─────────────┐
Running ─(authority detached)─▶ AuthorityLost ─(reconnect within grace)───────┘
AuthorityLost ─(grace expired / admin stop)─▶ Stopping ─▶ Ended (history row written)
Running ─(admin: Stop)─▶ Stopping (request final save from authority, notify clients) ─▶ Ended
```

- Canonical `SessionPhase` (incl. `Paused`, `Migrating`): protocol.md §6.1.
- In `AuthorityLost`, clients get `SessionState{phase=AuthorityLost}`. Replication stops, but
  player-to-player relay (positions, chat) continues so players still see each other.
- **Authority handoff** (later milestone; design hook only): admin "Promote" sends
  `RequestSave` to the old authority, waits for the upload, distributes it, sends
  `AuthorityAssign{grant=true}` to the new node; other clients continue (protocol.md §6.7), with reload as fallback. In M1 the API returns
  501 for it, but the state machine has the transitions.
- **Autosave**: every `AutosaveMinutes` (15), the actor sends `RequestSave{Autosave}` to the
  authority. The authority (mod) cleans ghosts, saves, sends the `SaveStarted` journal
  marker, and uploads the save plus manifest (section 3, ADR-008). The new save becomes
  the session's current save, so late joiners get a recent universe.

### 2.5 Interest management

Goal: each client receives enough of the world for smooth play, without sector-entry
churn. The authority captures only what someone needs.

**Inputs**
- `GalaxyMetadata` from the authority, sent once per save and cached by save sha256 in
  SQLite: clusters, sectors (id, macro, name, galaxy position, owner), zones if useful, and
  the gate/highway adjacency graph.
- Each client's player-ship state (sector id, position, velocity), from its own updates.
- Explicit subscriptions: admin map views (via the authority-capture path, at low rate)
  and observers.

**Interest tiers per client** (all values are hot-editable settings):

| Tier | Region | Default rate | Purpose |
|---|---|---|---|
| T0 Near | entities within `NearRadius` (default 15 km) of the player ship | 20 Hz | combat and docking fidelity |
| T1 Sector | rest of the player's current sector | 5 Hz | visible traffic |
| T2 Adjacent | sectors one gate or highway hop away (`PrefetchDepth`, default 1) | 1 Hz | **pre-warm, so entering a sector shows converged positions (fixes reference flicker)** |
| T3 Linger | previous sector for `LingerSeconds` (default 20) after leaving | 1 Hz | hysteresis against gate ping-pong |

- On a sector change, the actor recomputes the client's interest set. Entities that enter
  it get a full-state spawn, and entities that leave it get a `Despawn{reason=OutOfInterest}`
  on the Control lane. The client mod uses this to hide ghosts. The mod decides how
  "hide" maps to X4 objects.
- **Capture set to authority**: after any interest change, at most once per 500 ms, the
  actor sends the authority `CaptureSet{ sectors: [(sectorId, tierRateHz)], players: [...] }`.
  This is the union over all clients, plus admin map subscriptions at 1 Hz. The authority
  captures and streams **only** those sectors, at the max rate requested per sector. This
  is the main bandwidth and CPU saving for the authority (the reference streamed about
  85k ships).
- **Galaxy summary**: separately, at 0.2 Hz, the authority sends `GalaxySummary` per-sector aggregates
  (ship count by class/faction, station count). These feed the GUI galaxy map without
  streaming entities.
- Player ships are **always** in every client's interest set at a minimum of 2 Hz
  (galaxy-wide), so the in-game player list and map markers stay correct.

Data structures (inside the actor):
- `SectorGraph`: adjacency list, with a precomputed k-hop neighbour cache.
- `Dictionary<SectorId, HashSet<ConnectionId>> sectorSubscribers` (T1..T3).
- T0 uses a per-sector uniform grid (cell = `NearRadius`) rebuilt incrementally on
  entity move. A query covers 27 cells. Grids exist only for sectors with T0 interest.

### 2.6 Relay, fan-out and delta replication

**Entity registry mirror** (`X4MP.Core/World/EntityRegistry`): the server keeps the latest
known state of every entity the authority streams (only captured sectors) plus all player
ships.

```csharp
public struct EntityState            // shape owned by protocol.md (32 B wire struct); server needs these fields
{
    public uint  NetId;              // authority-assigned net_id (ADR-010); UniverseIDs are per process
    public uint  MacroIdx;           // index into session string table (interned macro name)
    public ushort FactionIdx;
    public ushort SectorId;          // u16 index into GalaxyMetadata sector table
    public Vector3 Pos;              // sector-local metres (wire: i32 1/64 m)
    public Vector3 RotEuler;         // yaw/pitch/roll (wire: i16)
    public Vector3 Vel;
    public byte  Class;              // ship S/M/L/XL, station, other
    public byte  HullPct, ShieldPct;
    public uint  Flags;              // player, ghost, docked, boarding-exempt, ...
}
sealed class EntityRecord { EntityState State; uint Version; long LastUpdateTick; ConnectionId Owner; }
```

- `Dictionary<uint, EntityRecord>` (by `net_id`) plus a per-sector index. Each inbound
  `WorldUpdate{tick, sectorId, entities[]}` from the authority is decoded straight into the
  records. Records are pooled, and the hot path does not allocate. `Version` increases when
  a field changes beyond the quantization threshold.
- Sectors leave the capture set after `CaptureEvictSeconds` (default 60) with no
  subscribers. Their entities are evicted from the mirror then.

**Replication tick** (actor timer, `TickRateHz`, default 20):

For each client in `InGame` with `conn.CanAcceptRealtime`:
1. Walk the client's interest set. Each candidate entity accumulates
   `priority += tierWeight * dtSinceLastSent`, and entities with
   `Version == baseline.Version` are skipped.
2. Choose the highest priorities until the **per-client byte budget** for this tick is
   used up (`BandwidthBudgetKBps / TickRateHz`, default 256 KB/s, so 12.8 KB per tick).
   This uses a reusable binary heap.
3. Encode a `Replication{server_tick, server_time_us, authority_game_time, entries}` frame. Each entry is
   delta-encoded against the per-client baseline (field bitmask plus absolute values of the masked fields,
   protocol.md §10.2). An entity with no baseline first gets a reliable `EntitySpawn`.
4. `TrySend` with `CoalesceKey = ReplicationKey`. If the result is `Queued` or
   `Coalesced`, lastSent is updated; baselines advance on flush/ack. If it is `DroppedLane`, nothing
   changes and the priority keeps growing.

Per-client memory: `Dictionary<uint, Baseline>` of about 40 B per entity in interest.
5,000 entities times 16 clients is about 3 MB.

**Pass-through relay** (no mirror):
- Player-ship states from clients are written into the mirror (owner = that client) and
  replicated to others like any entity at T0/T1 rates, and to the **authority** at 20 Hz
  regardless of interest, so the authority can place ghosts for NPC interaction.
- Chat, game events and action requests are validated by `MessagePolicy`, then
  re-broadcast on the Control lane: either to all clients, or for events only to clients
  with interest in the event's sector plus the authority. Intents from clients
  (`Intent{StationBuildRequest, TradeReport, KillClaim, CaptureReport, ...}`) go **only to the authority**. The
  authority applies them and emits authoritative events, which fan out. The server stamps
  every event with `serverSeq` and persists game events to `session_events`.
- One encode per broadcast: `OutboundFrame` is ref-counted and shared across connections.

**Keyframe and late join**: when a client enters `InGame`, or after reconnect or any
desync request, its baselines are cleared and spawns for the whole interest set are
re-sent near-first, spread by the budget, each sector followed by `SectorComplete`
(the mod suppresses local NPCs only after it; ADR-011).

**Desync guard**: every 5 s, the actor sends each client
`InterestChecksum{server_tick, count, xor_hash}` for its Near/Sector set. The client
compares it with its own view and sends `ResyncRequest` on a mismatch (the mod can implement
this later, and FakeNode implements it in M1).

### 2.7 Event bus

`X4MP.Core/Events/EventBus`: typed, in-process, built on bounded
`Channel<DomainEvent>` per subscriber.

```csharp
public abstract record DomainEvent(DateTimeOffset At, SessionId? Session);
public sealed record NodeConnected(...)       ; public sealed record NodeDisconnected(...)
public sealed record PlayerJoined(...)        ; public sealed record PlayerLeft(...)
public sealed record SessionStateChanged(...) ; public sealed record AuthorityChanged(...)
public sealed record ChatPosted(...)          ; public sealed record GameEventOccurred(...) // kill, capture, trade, build
public sealed record AdminActionTaken(...)    ; public sealed record SaveStored(...)
public sealed record ProtocolViolation(...)   ; public sealed record AlertRaised(Severity, string Code, string Text)
```

- `Publish` is synchronous and non-blocking. It calls `TryWrite` to each subscriber.
- Subscriber options: `FullMode = DropOldest` for UI-facing subscribers, and
  `Wait` with a large capacity (10k) for persistence and audit. A full
  persistence channel raises an `AlertRaised` but still never blocks the actor: it falls
  back to drop-and-count, which is logged.
- Subscribers: `PersistenceWriter`, `AdminBroadcaster` (to SignalR), `AuditLogger`, and
  `AlertEvaluator` (for example: authority FPS < 15 for 30 s, a client with p99 RTT > 300 ms,
  or slow-consumer disconnects).

### 2.8 Persistence (SQLite)

- One database file, `data/x4mp.db`, with `PRAGMA journal_mode=WAL; synchronous=NORMAL;
  foreign_keys=ON; busy_timeout=5000`.
- **Write-behind**: one `PersistenceWriter` background service owns the write
  connection. It drains its channel in batches (up to 500 items or 250 ms) inside a
  transaction. Reads (admin API) use short-lived pooled connections, which WAL allows to
  run concurrently.
- **Migrations**: embedded `Migrations/0001_init.sql`, `0002_...sql`. They are applied in
  order at startup inside a transaction, tracked in `schema_version`, and tested in CI
  against an empty DB and against a DB at the previous version.

Schema v1:

```sql
CREATE TABLE schema_version (version INTEGER NOT NULL);
CREATE TABLE players (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  key_hash BLOB NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
  last_ip TEXT, total_seconds INTEGER NOT NULL DEFAULT 0, notes TEXT,
  is_muted INTEGER NOT NULL DEFAULT 0, mute_until TEXT);
CREATE TABLE bans (
  id INTEGER PRIMARY KEY, player_id INTEGER REFERENCES players(id),
  ip_cidr TEXT, reason TEXT NOT NULL, created_by TEXT NOT NULL,
  created_at TEXT NOT NULL, expires_at TEXT, revoked_at TEXT,
  CHECK (player_id IS NOT NULL OR ip_cidr IS NOT NULL));
CREATE TABLE saves (
  id INTEGER PRIMARY KEY, sha256 TEXT NOT NULL UNIQUE, size_bytes INTEGER NOT NULL,
  display_name TEXT NOT NULL, original_file_name TEXT, source TEXT NOT NULL, -- 'authority'|'admin-upload'
  uploaded_by TEXT, uploaded_at TEXT NOT NULL, game_version TEXT, mod_version TEXT,
  save_time TEXT, player_name TEXT, meta_json TEXT, pinned INTEGER NOT NULL DEFAULT 0);
CREATE TABLE sessions (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, state TEXT NOT NULL,
  save_id INTEGER REFERENCES saves(id), current_save_id INTEGER REFERENCES saves(id),
  authority_player_id INTEGER REFERENCES players(id), settings_json TEXT NOT NULL,
  created_at TEXT NOT NULL, started_at TEXT, ended_at TEXT, end_reason TEXT);
CREATE TABLE session_players (
  session_id INTEGER REFERENCES sessions(id), player_id INTEGER REFERENCES players(id),
  joined_at TEXT NOT NULL, left_at TEXT, leave_reason TEXT, role TEXT NOT NULL);
CREATE TABLE session_events (
  id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, ts TEXT NOT NULL, server_seq INTEGER,
  type TEXT NOT NULL, player_id INTEGER, sector_id INTEGER, data_json TEXT);
CREATE INDEX ix_events_session_ts ON session_events(session_id, ts);
CREATE TABLE chat_messages (
  id INTEGER PRIMARY KEY, session_id INTEGER, ts TEXT NOT NULL, from_player_id INTEGER,
  from_admin TEXT, channel TEXT NOT NULL, text TEXT NOT NULL);
-- Teams (2.13). Teams are session-scoped; presets are applied by rewriting these rows in one transaction.
CREATE TABLE teams (
  id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  name TEXT NOT NULL, color TEXT NOT NULL, faction_slot INTEGER NOT NULL,
  leader_player_id INTEGER REFERENCES players(id), locked INTEGER NOT NULL DEFAULT 0,
  max_members INTEGER, join_pw_hash BLOB, created_at TEXT NOT NULL, deleted_at TEXT,
  UNIQUE(session_id, name), UNIQUE(session_id, faction_slot));
CREATE TABLE team_members (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  player_id INTEGER NOT NULL REFERENCES players(id),
  team_id INTEGER NOT NULL REFERENCES teams(id), role TEXT NOT NULL DEFAULT 'member',
  since TEXT NOT NULL, assigned_by TEXT NOT NULL,          -- 'auto'|'lobby'|'admin:<user>'
  PRIMARY KEY (session_id, player_id));                    -- one team per player per session
CREATE TABLE team_relations (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  team_a INTEGER NOT NULL REFERENCES teams(id), team_b INTEGER NOT NULL REFERENCES teams(id),
  relation INTEGER NOT NULL,                               -- -1 hostile, 0 neutral, 1 allied
  updated_at TEXT NOT NULL, CHECK (team_a < team_b), PRIMARY KEY (session_id, team_a, team_b));
CREATE TABLE team_assets (                                 -- player-relevant assets only (ships, stations)
  session_id INTEGER NOT NULL, entity_id INTEGER NOT NULL, team_id INTEGER, owner_player_id INTEGER,
  kind TEXT NOT NULL, macro TEXT, updated_at TEXT NOT NULL, PRIMARY KEY (session_id, entity_id));
-- Economy (2.14). Append-only ledger; balances are a cache verified by EconomyAuditor.
CREATE TABLE wallets (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('player','team_shared','team_pool','escrow','world')),
  owner_id INTEGER NOT NULL,                     -- player id | team id | loan/trade id | 0 (world)
  balance INTEGER NOT NULL CHECK (balance >= 0 OR kind IN ('player','team_shared','world')), -- only game spend/forced reversal may overdraw
  version INTEGER NOT NULL DEFAULT 0,
  frozen INTEGER NOT NULL DEFAULT 0, frozen_reason TEXT, overdrawn INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT NOT NULL, PRIMARY KEY (session_id, kind, owner_id));
CREATE TABLE ledger_tx (
  id TEXT PRIMARY KEY,                           -- ULID
  session_id INTEGER NOT NULL, ts TEXT NOT NULL, kind TEXT NOT NULL, actor TEXT NOT NULL,
  request_id TEXT, ref_type TEXT, ref_id INTEGER,
  reverses_tx TEXT REFERENCES ledger_tx(id), reversed_by_tx TEXT REFERENCES ledger_tx(id), note TEXT);
CREATE UNIQUE INDEX ux_ledger_reverses ON ledger_tx(reverses_tx) WHERE reverses_tx IS NOT NULL; -- reverse at most once
CREATE INDEX ix_ledger_tx_session_ts ON ledger_tx(session_id, ts);
CREATE TABLE ledger_entries (
  tx_id TEXT NOT NULL REFERENCES ledger_tx(id), seq INTEGER NOT NULL,
  wallet_kind TEXT NOT NULL, wallet_owner_id INTEGER NOT NULL,
  amount INTEGER NOT NULL, balance_after INTEGER NOT NULL, PRIMARY KEY (tx_id, seq));
CREATE INDEX ix_entries_wallet ON ledger_entries(wallet_kind, wallet_owner_id);
CREATE TRIGGER ledger_tx_no_delete BEFORE DELETE ON ledger_tx BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;
CREATE TRIGGER ledger_entries_no_update BEFORE UPDATE ON ledger_entries BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;
CREATE TABLE economy_requests (                  -- idempotency / anti-dupe
  session_id INTEGER NOT NULL, player_id INTEGER NOT NULL, request_id TEXT NOT NULL,
  type TEXT NOT NULL, payload_hash BLOB NOT NULL, result_json TEXT NOT NULL, ts TEXT NOT NULL,
  PRIMARY KEY (session_id, player_id, request_id));
CREATE TABLE loans (
  id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, lender_player_id INTEGER NOT NULL,
  borrower_player_id INTEGER NOT NULL, principal INTEGER NOT NULL, interest_bp INTEGER NOT NULL,
  amount_due INTEGER, outstanding INTEGER, due_kind TEXT NOT NULL, due_at TEXT, due_game_seconds INTEGER,
  auto_repay_pct INTEGER NOT NULL DEFAULT 0, state TEXT NOT NULL, offer_expires_at TEXT NOT NULL,
  created_at TEXT NOT NULL, accepted_at TEXT, closed_at TEXT, close_reason TEXT, closed_by TEXT);
CREATE INDEX ix_loans_state ON loans(session_id, state);
CREATE TABLE trade_offers (
  id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, proposer_player_id INTEGER NOT NULL,
  counterparty_player_id INTEGER NOT NULL, proposer_leg_json TEXT NOT NULL, counterparty_leg_json TEXT NOT NULL,
  locked_entity_id INTEGER,                      -- ship/station locked by this trade
  state TEXT NOT NULL, expires_at TEXT NOT NULL, execute_attempts INTEGER NOT NULL DEFAULT 0,
  failure_reason TEXT, reversed INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL, accepted_at TEXT, settled_at TEXT, resolved_by TEXT);
CREATE UNIQUE INDEX ux_trade_entity_lock ON trade_offers(session_id, locked_entity_id)
  WHERE locked_entity_id IS NOT NULL AND state IN ('Proposed','Escrowed','Executing','InDoubt');
CREATE TABLE economy_events (                    -- audit trail of every state transition and rejection
  id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, ts TEXT NOT NULL, type TEXT NOT NULL,
  actor TEXT NOT NULL, ref_type TEXT, ref_id INTEGER, from_state TEXT, to_state TEXT,
  reason TEXT, data_json TEXT);
CREATE INDEX ix_econ_events ON economy_events(session_id, ts);
-- Team settings live in sessions.settings_json under "teams"; economy settings under "economy".
-- Admin economy actions (adjust, freeze, reverse, forgive, cancel, resolve) are ALSO written to audit_log.
CREATE TABLE galaxy_cache (save_sha256 TEXT PRIMARY KEY, metadata_blob BLOB NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE config_overrides (key TEXT PRIMARY KEY, value_json TEXT NOT NULL,
  updated_at TEXT NOT NULL, updated_by TEXT NOT NULL);
CREATE TABLE admin_users (id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE,
  pw_hash BLOB NOT NULL, pw_salt BLOB NOT NULL, pw_iter INTEGER NOT NULL,
  role TEXT NOT NULL, created_at TEXT NOT NULL, must_change INTEGER NOT NULL DEFAULT 0);
CREATE TABLE api_tokens (id INTEGER PRIMARY KEY, name TEXT NOT NULL, token_hash BLOB NOT NULL UNIQUE,
  role TEXT NOT NULL, created_at TEXT NOT NULL, last_used_at TEXT, revoked_at TEXT);
CREATE TABLE audit_log (id INTEGER PRIMARY KEY, ts TEXT NOT NULL, actor TEXT NOT NULL,
  action TEXT NOT NULL, target TEXT, data_json TEXT, remote_ip TEXT);
```

Retention (janitor, daily): `session_events` and `chat_messages` older than
`RetentionDays` (default 30) are deleted. Unpinned saves beyond `SaveRetentionCount`
(default 10) that no session references are deleted from disk and DB.

### 2.9 Configuration

Two layers:

1. **Boot config** (`appsettings.json` next to the exe, plus `appsettings.Local.json`, plus
   `X4MP__*` env vars, plus CLI): bind addresses and ports, data dir, TLS cert, log file
   path, and `Admin:AllowRemote`. **Changes require a restart.** The GUI shows them
   read-only, with a "restart required" note.
2. **Runtime settings** (GUI-editable): stored in `config_overrides`. They are layered via
   a custom `IConfigurationProvider` (`SqliteOverridesConfigurationProvider`, about 80
   lines) that calls `OnReload()` after a save, so `IOptionsMonitor<T>` consumers update
   live.

Settings are declared once in C#, using attributes that drive validation and the GUI form:

```csharp
[SettingsSection("Session")]
public sealed class SessionOptions
{
    [Setting("Server name", Hot = true)]                      public string ServerName { get; set; } = "X4MP Server";
    [Setting("Message of the day", Hot = true, MaxLen = 500)] public string Motd { get; set; } = "";
    [Setting("Join password", Hot = true, Secret = true)]     public string? JoinPassword { get; set; }
    [Setting("Max players", Hot = true), Range(1, 64)]        public int MaxPlayers { get; set; } = 8;
    [Setting("Reconnect grace (s)", Hot = true), Range(5, 600)] public int ReconnectGraceSeconds { get; set; } = 60;
    [Setting("Authority-lost grace (s)", Hot = true), Range(10, 1800)] public int AuthorityGraceSeconds { get; set; } = 120;
    [Setting("Autosave every (min, 0=off)", Hot = true), Range(0, 240)] public int AutosaveMinutes { get; set; } = 15;
    // Game build pinning is not a setting: SupportedGameBuilds (boot) + always-on equality (ADR-004)
}
// ReplicationOptions: TickRateHz, BandwidthBudgetKBps, NearRadiusM, SectorRateHz,
//   AdjacentRateHz, PrefetchDepth, LingerSeconds, CaptureEvictSeconds
// NetOptions (boot): NodeTcpEndpoint, NodeUdpEndpoint, MaxFrameBytes, HandshakeTimeoutSeconds,
//   MaxConnectionsPerIp, SlowConsumerTimeoutSeconds
// AdminOptions (boot): HttpEndpoint, HttpsEndpoint, CertPath, AllowRemote, CorsOrigins
// GameplayOptions (hot, forwarded to nodes in SessionSettings): AllowClientStationBuilds,
//   SyncTrades, SyncCaptures, ClientSimMode (thin|hybrid default), GhostRenderMode, ChatEnabled
// TeamOptions (hot, per session; see 2.13): JoinMode, AutoAssign, AllowCreateInLobby, LobbyTimeoutSeconds,
//   MaxTeams, DefaultRelation, AssetPolicy, AllowFriendlyFire, AllowAssetTransfer, MoveAssetsWithPlayer
// EconomyOptions (hot, per session; see 2.14): CreditMode, TeamPoolEnabled, Donate/Loan/TradeScope, limits, ...
// LoggingOptions (hot): MinimumLevel, per-namespace overrides, NodeLogForwarding (off|warn|all)
// RetentionOptions (hot): RetentionDays, SaveRetentionCount
```

- `GET /api/v1/settings/schema` reflects these attributes into a JSON schema-like
  description. The GUI renders the forms from it, so a new setting needs no GUI change.
- A change to `GameplayOptions` is pushed to all nodes as a `SessionSettings` message.
- Every change writes an `audit_log` row with the old and new values. Secrets are redacted.

### 2.10 Logging

- Serilog is configured from `appsettings.json` (`Serilog` section).
- Sinks:
  - Console, with a colored template including `{SourceContext}`, `{ConnectionId}` and
    `{Player}`.
  - Rolling file at `data/logs/server-.log`: daily, 14 files retained, 50 MB size limit
    with roll-over.
  - **RingBufferSink**, our own: a 20k-event in-memory buffer feeding the GUI live tail.
- Log context enrichment: `ConnectionId`, `PlayerName`, `SessionId` via `LogContext.PushProperty`
  in the reader loop.
- **Node log forwarding** (capability `LogForward`): nodes may send `LogForward` batches
  (Control lane, rate-limited to 50 lines/s per node). They are written with
  `Source = node:<player>` to a separate rolling file, `data/logs/nodes-.log`, and to the
  ring buffer. This replaces "send me your log file" debugging.
- The hot path never logs per frame at Information level. Per-message diagnostics sit
  behind `Verbose` plus a per-connection "trace" toggle in Diagnostics, which samples
  1 in N frames.

### 2.11 Metrics

- `Meter("X4MP.Server")` instruments:
  - Counters: `x4mp.net.bytes{dir,lane}`, `x4mp.net.frames{dir,type}`,
    `x4mp.net.dropped{lane,reason}`, `x4mp.net.disconnects{reason}`.
  - Histograms: `x4mp.session.tick_ms`, `x4mp.replication.encode_ms`, `x4mp.net.rtt_ms`.
  - Observable gauges: connections, entities in mirror, queue depth.
- `ConnectionStats` is a per-connection class of `long` fields updated with `Interlocked`:
  bytes and frames in/out per lane, frames in per message type (fixed array indexed by
  type id), drops, coalesces, RTT EWMA and jitter, last pong, and queue high-water marks.
- **RTT** is measured server-side by the server's own `Ping{t}` / `Pong{t}` every 1 s on
  the Control lane. Using server time avoids clock-sync issues. Clock offset estimation
  (NTP-style, using `Pong{t, nodeTime}`) is exposed for the authority-tick latency figure.
- **Node telemetry** (`NodeStats` message, every 2 s): FPS, frame time p95, game time,
  ghost count, queue depths, RTT, clock offset, memory, and the feature/health report
  (`md_hook`, `team_factions`). These are shown
  per player on the GUI.
- `MetricsSampler` (1 Hz) computes rates from the counters into ring buffers: 600 samples
  per series, so 10 minutes. The Dashboard and Diagnostics sparklines read from these.
- M2 and later: optional OpenTelemetry Prometheus endpoint at `/metrics`, behind admin auth
  or localhost only.

### 2.12 Performance targets (M1 load test gates)

The reference environment is 1 authority, 16 clients, 40 captured sectors and 20,000
entities in the mirror, at a 20 Hz tick, on a 4-core desktop:

- Actor tick (ingest plus replication for 16 clients): p99 < 15 ms.
- Server process CPU < 100% of one core, working set < 500 MB.
- Zero allocations per entity in the steady-state ingest path (measured with
  `dotnet-counters` and a BenchmarkDotNet micro-benchmark in LoadTests).
- A slow-reader client (FakeNode `--slow-reader`) has **no** measurable effect on the other
  clients' tick latency.

### 2.13 Teams and factions

Sessions group players into **teams**. Several players can share one team, which means
one in-game faction with shared assets. Teams can also be separate factions with
configurable diplomacy between them. The server owns the team model. The mod applies it
in the game (factions, relations, ownership). Code lives in `X4MP.Core/Teams/`.

#### Domain model

```csharp
public readonly record struct TeamId(int Value);

public sealed record Team(
    TeamId Id, SessionId Session,
    string Name,               // 1-24 chars, unique per session
    string Color,              // "#RRGGBB"; used by GUI map and forwarded to mod for HUD/ghost tint
    byte FactionSlot,          // 0..MaxFactionSlots-1; maps to a mod-defined X4 faction (see below)
    long? LeaderPlayerId,      // optional; leader may command any team asset under OwnerAndLeader policy
    bool Locked,               // lobby mode: players cannot self-join
    int? MaxMembers,
    string? JoinPasswordHash); // optional per-team password in lobby mode

public enum TeamRelation : sbyte { Hostile = -1, Neutral = 0, Allied = 1 }

/// Symmetric N×N matrix; diagonal is implicitly "Self" (same faction). Stored sparse; default = DefaultRelation.
public sealed class TeamRelationMatrix
{
    public TeamRelation Get(TeamId a, TeamId b);
    public TeamRelationMatrix With(TeamId a, TeamId b, TeamRelation r); // immutable, versioned
    public uint Version { get; }
}

public sealed record TeamMembership(long PlayerId, TeamId Team, TeamRole Role, DateTimeOffset Since);
public enum TeamRole : byte { Member, Leader }

// Credit/wallet types (CreditMode, Wallet, Loan, TradeOffer, ...) are defined in 2.14.
public enum TeamAssetPolicy : byte { SharedCommand, OwnerOnly, OwnerAndLeader }
public enum TeamJoinMode : byte { Auto, Lobby, AdminAssign }
public enum AutoAssignStrategy : byte { SingleTeam, Balance, NewTeamPerPlayer }
```

**Faction slots.** X4 relations and ownership work per *faction*, so each team maps to one
**faction slot** 1..8. The mod ships `x4mp_team_1` .. `x4mp_team_8` (`MaxFactionSlots` = 8).

The mapping is **symmetric** (ADR-014): team k is `x4mp_team_k` on every node, the
authority included; the game's `player` faction owns only each node's local human avatar.
Team assets are commanded through server-gated `Intent{AssetOrder}`.

The server only transmits `{teamId, factionSlot, color, name}`. The remapping is done by
the mod. When a team changes slot, every node gets a `TeamTable` update and the authority
re-owns that team's assets.

**Relations.** At session start, and on every change, the server sends
`TeamRelations{version, entries[(teamA, teamB, relation)]}` to all nodes. The mod sets the
faction relations Allied = +0.75, Neutral = 0, Hostile = -1.0, locked (ADR-016).
The matrix is symmetric. Relations toward NPC factions are untouched, and every team
starts with the save's player relations. A relation change mid-session is applied live.
The authority is authoritative for its consequences, such as ships that start or stop
fighting.

**Credits.** Credit mode, wallets (per-player, team shared, team pool), donations, loans
and escrowed trades are specified in **2.14 Economy**. The team-relevant rule is
`CreditMode=Auto`: a session with exactly one team uses one shared wallet automatically.
Any other team count uses per-player wallets, and admins can override it per session.
Cross-team economy actions are gated by the team relation matrix.

**Presets** (applied atomically. Each one replaces the team table, memberships and
matrix, and asks for confirmation when the session is Running):

| Preset | Teams | Relations | Assignment |
|---|---|---|---|
| Everyone co-op (one faction) | 1 team, slot 1 | n/a | all players → team 1. `JoinMode=Auto`, `AutoAssign=SingleTeam` |
| All separate but allied | 1 team per player | all Allied | `AutoAssign=NewTeamPerPlayer` |
| Free-for-all | 1 team per player | all Hostile | `AutoAssign=NewTeamPerPlayer` |
| Two teams (versus) | 2 teams | Hostile | `AutoAssign=Balance` |
| Custom | as edited | as edited | as configured |

`NewTeamPerPlayer` creates a team on join (name = player name, next free slot, colour from
the palette). It fails with `NoFactionSlot` once all slots are in use.

#### Join-time behaviour (`Teams.JoinMode`)

The node state machine (2.4) gains a state between `Admitted` and `SyncingSave`:

```
Admitted ─▶ AwaitingTeam ─(team assigned)─▶ SyncingSave ─▶ Loading ─▶ InGame
```

- **Sticky first.** If the player has a membership from this session (it survives
  reconnects and server restarts) or from the session's previous run, it is reused, and
  `AwaitingTeam` is skipped.
- **Auto**: assigned immediately by `AutoAssignStrategy`. `SingleTeam` puts the player in
  the first team. `Balance` puts them in the unlocked team with the fewest members, with
  the lowest id as tie-break. `NewTeamPerPlayer` creates a team.
- **Lobby**: the `Welcome` message includes the full `TeamTable` (id, name, colour, members,
  locked, max members, password-protected). The in-game Join dialog shows it, and the client
  replies `TeamChoice{teamId, password?}`. The server validates it (not locked, not full,
  password HMAC correct) and answers `TeamRequestResult{Ok|Rejected, reason}`. If
  `Teams.AllowCreateInLobby` is set, a client may send `TeamCreateRequest{name}`. Timeout:
  after `LobbyTimeoutSeconds` (default 300) the node falls back to Auto.
- **AdminAssign**: the node waits in `AwaitingTeam`. It receives `SessionState` but no save
  or replication, and the GUI shows it under "Unassigned" with a toast. An admin assigns
  the player via REST or drag-and-drop. There is no timeout. The player can leave.
- The authority must also be on a team before `AuthorityLoading`, because its team receives
  the save's pre-existing assets (`inherit_team`, ADR-033). In AdminAssign mode the GUI flags this as blocking.

**Moving a player mid-session**:
1. The server updates the membership and broadcasts `TeamMemberChanged`.
2. It sends `ReassignPlayerAssets{playerId, fromTeam, toTeam, scope}` to the authority.
   The scope comes from `Teams.MoveAssetsWithPlayer`: `ShipOnly` (default), `AllOwned`,
   or `None`.
3. The authority changes ownership in game (`SetComponentOwner`) and emits the
   authoritative ownership events, which update the mirror.
4. The moved client re-applies its relations and resyncs (no other ownership changes,
   ADR-014).

Moving the **authority's** player is allowed only while the session is not Running;
otherwise the request gets 409 (ADR-017).

#### Asset ownership and command permission enforcement

The entity mirror (2.6) gets two fields:
- `OwnerTeamId`, derived from the faction in the authority's stream via the slot map.
  NPC factions get `TeamId.None`.
- `OwnerPlayerId`: the player who bought or captured or built the asset, from
  authoritative events (`EntitySpawn`/`EntityChange{OwnerTeam|OwnerPlayer}`). It is
  persisted in `team_assets` for player-relevant assets: ships and stations, not drones
  or missiles.

Every client message that **commands or modifies an asset** passes through
`AssetPermissionPolicy` in the actor before being forwarded to the authority. These are the
`Intent` bodies `AssetOrder`, `StationBuildRequest`, `TradeReport`, `AssetRename`,
`AssetGift`, `KillClaim`/`HitReport`, `CaptureReport`, plus trade asset items (protocol.md §16.2).

```
allowed(sender, entity, action) =
     entity.OwnerTeamId == sender.Team
  && ( policy == SharedCommand
    || entity.OwnerPlayerId == sender.PlayerId
    || (policy == OwnerAndLeader && sender.Role == Leader)
    || entity.OwnerPlayerId == null )                       // unowned team assets: any member
  && action-specific checks:
       AssetOrder attack target: allowed if relation(sender.Team, target.OwnerTeamId) == Hostile,
                               or the target is NPC (game rules apply). Attacking an Allied/Neutral
                               team's asset is rejected unless Teams.AllowFriendlyFire.
       ActTransferAsset:       destination team must be Allied, and Teams.AllowAssetTransfer must be set.
       ActTrade at a station owned by another team: relation != Hostile.
```

- A rejected command is not forwarded. The sender gets
  `IntentResult{Rejected, reason=NotYourAsset|PolicyDenied|HostileRequired|...}` and a
  `PermissionDenied` session event is recorded (not counted as a protocol violation;
  rate-limited logging).
- The **authority re-checks nothing**, because the server is the single gate. The mod's
  local UI should still grey out commands using the same rules (shared `TeamTable` and
  `TeamPolicy` messages).
- Clients never receive commands for assets of other teams. Hostile and neutral teams'
  assets are replicated like NPC entities. Interest (2.5) is unchanged; whether to hide
  hostile assets (fog-of-war) is open question 9.
- Unit tests cover the full policy × relation × action matrix (table-driven).

#### Settings (`TeamOptions`, hot unless noted)

`JoinMode`, `AutoAssign`, `AllowCreateInLobby`, `LobbyTimeoutSeconds`, `MaxTeams`
(≤ `MaxFactionSlots`), `DefaultRelation` (Neutral), (credit/economy settings: see `EconomyOptions` in 2.14), `AssetPolicy`,
`AllowFriendlyFire`, `AllowAssetTransfer`, `MoveAssetsWithPlayer`. Team-related
`GameplayOptions` are forwarded to nodes inside `TeamPolicy`.

### 2.14 Economy: wallets, donations, loans, trades

The server is the **single source of truth for credits**. All economy state is owned by
the session actor through `X4MP.Core/Economy/EconomyService`, so every mutation is
serialized: there are no concurrent balance races by construction. Every mutation is
committed to SQLite **synchronously** (a small transaction that bypasses the write-behind
batcher) **before** it is acknowledged, so an acknowledged action survives a crash.

The server ledger is authoritative. The mod keeps each human's local money equal to
their spendable balance using Lua `GetPlayerMoney` + `AddPlayerMoney` and `CreditDelta{seq}`
/ `WalletUpdate{acked_delta_seq}` (mod-design.md §12.2, ADR-019). Wire amounts are whole credits.

#### Domain model

```csharp
public enum CreditMode : byte { Auto, PerPlayer, Shared }            // session setting, default Auto
public enum EffectiveCreditMode : byte { PerPlayer, Shared }          // Auto => Shared iff exactly one team
public enum WalletKind : byte { Player, TeamShared, TeamPool, Escrow, World }
public readonly record struct WalletId(WalletKind Kind, long OwnerId); // player/team/escrow-ref id; World = 0

public sealed class Wallet                       // balances are long credits (no fractions)
{
    public WalletId Id; public long Balance; public bool Frozen; public string? FrozenReason;
    public long Version;                         // optimistic check on every write (defence in depth)
}

/// Double-entry: every transaction has >= 2 entries whose amounts sum to zero.
/// Game income/spend books against the World wallet (the only wallet allowed to go negative).
public sealed record LedgerTransaction(
    Ulid Id, SessionId Session, DateTimeOffset At, TxKind Kind, string Actor,      // player:<id>|authority|admin:<u>|system
    string? RequestId, RefType? RefType, long? RefId,                              // loan/trade/game event
    Ulid? Reverses, Ulid? ReversedBy, string? Note, IReadOnlyList<LedgerEntry> Entries);
public sealed record LedgerEntry(WalletId Wallet, long Amount /*signed*/, long BalanceAfter);
public enum TxKind : byte { Donate, PoolDeposit, PoolWithdraw, LoanEscrow, LoanDisburse, LoanRepay, LoanAutoRepay,
    LoanRefund, TradeEscrow, TradeSettle, TradeRefund, GameIncome, GameSpend, AdminAdjust, Reversal, ModeMigration, TeamMove }

public enum ActionScope : byte { Off, Teammates, Allied, Anyone }        // gate per action type (Allied = same or Allied team)

public sealed record Loan(
    long Id, long LenderPlayerId, long BorrowerPlayerId, long Principal,
    int InterestBasisPoints,                     // flat interest on principal: 500 = 5%
    long AmountDue,                              // principal * (1 + bp/10000), rounded up, fixed at acceptance
    long Outstanding, DueKind DueKind, DateTimeOffset? DueAtUtc, long? DueGameSeconds,
    int AutoRepayPercent,                        // 0..100 of the borrower's positive GameIncome diverted to repay
    LoanState State, DateTimeOffset OfferExpiresAt, string? CloseReason);
public enum DueKind : byte { None, RealTime, GameTime }  // GameTime uses the authority's reported game clock
public enum LoanState : byte { Offered, Active, Overdue, Repaid, Declined, Withdrawn, Expired, Forgiven, Cancelled }

public sealed record TradeOffer(
    long Id, long ProposerPlayerId, long CounterpartyPlayerId,
    TradeLeg ProposerGives, TradeLeg CounterpartyGives,       // M1: exactly one leg is Credits
    TradeState State, DateTimeOffset ExpiresAt, int ExecuteAttempts, string? FailureReason);
public abstract record TradeLeg;
public sealed record CreditsLeg(long Amount) : TradeLeg;
public sealed record WaresLeg(ulong FromShipId, ulong ToShipId, string Ware, int Quantity) : TradeLeg;
public sealed record ShipLeg(ulong ShipId) : TradeLeg;
public sealed record StationLeg(ulong StationId) : TradeLeg;  // reserved; rejected with NotSupportedYet in M1
public enum TradeState : byte { Proposed, Escrowed, Executing, Completed, Declined, Withdrawn, Expired,
    Failed /*-> refunded*/, InDoubt, Cancelled }
```

**Wallets per mode.**
- `PerPlayer`: one `Player` wallet per player, plus an optional `TeamPool` per team
  (`Economy.TeamPoolEnabled`, default on).
- `Shared`: one `TeamShared` wallet per team, which all members spend from.
- `Escrow` wallets are created per loan offer or trade (owner id = loan/trade id) and must
  be zero when the offer reaches a terminal state.
- Mode switches and team moves migrate balances in a single `ModeMigration` or `TeamMove`
  transaction:
  - **PerPlayer → Shared**: team shared = sum of member wallets + pool.
  - **Shared → PerPlayer**: an even split across members, with the remainder to the pool,
    or to the leader if the pool is disabled.
  - **A player moving teams in PerPlayer mode**: their wallet goes with them.
  - **A player moving teams in Shared mode**: they bring nothing and take nothing.
  - A migration while Running needs admin `confirm` after a preview. Open loans and
    trades keep referencing *players*, so they survive mode changes. In Shared mode they
    settle against the player's effective wallet (the team shared wallet).

**Action scopes.** Each action type is gated by a session setting with the values
`Off | Teammates | Allied | Anyone`: `Economy.DonateScope` (default Teammates),
`Economy.LoanScope` (default Teammates), and `Economy.TradeScope` (default Allied).

`scopeAllows(scope, a, b)` works as follows:
- `Teammates`: same team.
- `Allied`: same team, or `relation(teamA, teamB) == Allied`.
- `Anyone`: every pair except where an admin has frozen either wallet.

The scope is checked at **offer time and again at accept/execute time**, because
relations can change in between. If an action is no longer allowed, the offer is
cancelled and its escrow refunded. In Shared mode, actions between two members of the
same team are rejected with `SameWallet`.

#### Actions

**Donate (gift).** `DonateRequest{requestId, to: Player(id) | TeamPool(teamId), amount}`
is a one-way transfer posted as one `Donate` transaction. Withdrawing from your own
team's pool uses `PoolWithdrawRequest`, which follows `Economy.PoolWithdrawPolicy`
(`AnyMember` | `LeaderOnly` | `Disabled`) and the optional daily limit per player.
`PoolDeposit` is a donation to your own team's pool.

**Loan.**

```
Offered ──accept──▶ Active ──outstanding=0──▶ Repaid
  │  │ decline ─▶ Declined      │  due passed & outstanding>0
  │  └ lender withdraws ─▶ Withdrawn         ▼
  └ offer expiry ─▶ Expired   Overdue ──outstanding=0──▶ Repaid
Active|Overdue ──admin forgive──▶ Forgiven     Active|Overdue|Offered ──admin cancel──▶ Cancelled
```

1. **Offer.** `LoanOffer{request_key, borrower, principal, repay_total, due_in_s, offer_ttl_s, auto_repay_pct}`
   (wire carries `repay_total`; the GUI shows it as interest %, capped by `MaxLoanInterestBp`).
   The principal is moved from the lender's wallet into `Escrow(loan)` immediately
   (`LoanEscrow`), so acceptance can never fail for lack of funds. Interest is a flat fee
   on the principal. Compounding is deliberately not supported (it is simpler to explain
   in the GUI). The cap is `Economy.MaxLoanInterestBp` (default 5000 = 50%).
2. **Accept** (`LoanRespond{loanId, accept}`): escrow goes to the borrower
   (`LoanDisburse`), `AmountDue` and `Outstanding` are fixed, and the state becomes
   `Active`. **Decline, withdraw or expiry** refund the escrow to the lender
   (`LoanRefund`).
3. **Repay** (`LoanRepay{request_key, loan_id, amount}`): the amount is capped at
   `Outstanding`.
4. **Auto-repay:** on every positive `GameIncome` booked to the borrower,
   `floor(income * autoRepayPct / 100)` goes to the lender (`LoanAutoRepay`), oldest loan
   first, inside the same transaction as the income. Booking income in Shared mode means
   the team wallet pays.
5. **Due:** `RealTime` is a UTC timestamp. `GameTime` is compared against the authority's
   game clock (`NodeStats.game_time`). The game clock does not advance while
   the session is not Running. An actor timer (every 10 s) moves loans whose due point
   has passed with `Outstanding > 0` to `Overdue`. Overdue only flags the loan (GUI badge
   and a notice to both players). It accrues no penalty in M1. Repayment still works.
6. **Admin forgive** sets `Outstanding = 0` and the state `Forgiven`. **Admin cancel**
   voids the loan. For `Offered` it refunds the escrow. For `Active`/`Overdue` it closes
   the loan with no further obligation. Optionally, `reverseDisbursement=true` attempts a
   `Reversal` of the disbursement, which only works if the borrower still has the funds,
   unless `force` is set. Both write the audit log.
7. **Limits:** `Economy.MaxOpenLoansPerPlayer` (default 5, counted as both lender and
   borrower) and `Economy.MaxLoanPrincipal`.

**Trade (escrowed swap).** M1 supports credits ↔ wares and credits ↔ ship. Stations are
reserved in the model and rejected. **Consolidated (ADR-022):** item lists with
`TradeCounter`/versions per protocol.md §15.6; execution is one per-trade
`AssetTransferOrder{lines[]}` to the authority (precheck, apply, compensate) answered by
one `AssetTransferConfirm`. Below, read `TradeExecute`/`TradeExecuted`/`TradeStatusQuery`
as `AssetTransferOrder`/`AssetTransferConfirm`/`TradeQuery`, and `Executing` as
`Transferring`. Canonical states: Proposed, Countered, Accepted, Escrowed, Transferring,
InDoubt, Completed, RolledBack, Cancelled, Expired, Rejected.

```
Proposed ──counterparty accept──▶ Escrowed ──server sends TradeExecute──▶ Executing
   │ decline/withdraw/expire ─▶ Declined/Withdrawn/Expired (refund escrow if any)
Executing ──authority TradeExecuted{ok}──▶ Completed  (escrow → seller: TradeSettle)
Executing ──TradeExecuted{fail,reason}──▶ Failed       (escrow → buyer: TradeRefund; unlock asset)
Executing ──no reply in ExecuteTimeout (30 s)──▶ query TradeStatusQuery{tradeId} (x3)
          ──still unknown / authority lost──▶ InDoubt  ──admin resolve: complete | refund──▶ Completed | Failed
Proposed|Escrowed ──admin cancel──▶ Cancelled (refund)
```

1. **Propose** (`TradeProposal{request_key, counterparty, give[], want[], ttl_s}`). The
   server pre-validates:
   - Scope, and that exactly one leg is `CreditsLeg`.
   - Ship ownership: the ship's `OwnerPlayerId` is the giving player. If the asset belongs
     to the team, `AssetPermissionPolicy` must allow the transfer.
   - The asset is not locked by another open trade (**one active trade per entity**,
     enforced by a unique index).
   - Wares: `FromShipId` is owned by the giver, and `ToShipId` by the receiver.
   - Optional proximity (`Economy.TradeRequiresProximity`, default true: both ships in
     the same sector per the mirror).

   If the **proposer** gives credits, they are escrowed now. The asset is **locked**:
   `AssetPermissionPolicy` then rejects orders, transfers or other trades on that entity
   while the trade is open.
2. **Accept**: the server re-validates everything and escrows the counterparty's credits
   if the counterparty is the payer. The state becomes `Escrowed`, then immediately
   `Executing`. `TradeExecute{tradeId, legs, buyerTeamSlot, sellerTeamSlot}` is sent to
   the authority.
3. **Authority execution** must be **idempotent by tradeId**. The authority keeps a set
   of executed trade ids that survives its own reconnects. It performs the in-game
   transfer atomically from its point of view: moving the cargo, or changing ship owner
   to the buyer's faction plus the owner player. It replies
   `TradeExecuted{tradeId, ok, reason?, resultingOwnership[]}`. On success the server
   updates `team_assets` and the mirror owner fields.
4. **Exactly one settlement**: the server accepts a `TradeExecuted` only in state
   `Executing` (or `InDoubt`). Duplicates are ignored and logged. Settlement and the state
   change happen in one DB transaction.
5. **Rollback**: on failure, the escrowed credits are refunded to the payer and the asset
   lock is released. Nothing in game changed, by the authority's contract.

**Game income and spending.** The authority sends `CreditDelta{playerId, amount, reason, refEventSeq}`
for trades with NPCs, builds, bounties and so on. The server books it as
`GameIncome`/`GameSpend` against `World`. Auto-repay is applied to income. Spending that
overdraws a wallet is still booked, because the game already spent the money. It sets
the wallet `Overdrawn` and raises an alert, and outgoing player actions from that wallet
are blocked until the balance is ≥ 0.

#### Validation rules (all in the actor; one rejection reason per request)

1. **Identity and state**: the request comes from an authenticated client on its own
   behalf (the `actor` is always derived from the connection, never from the payload).
   The session must be Running. The sender's node must be in `InGame`.
2. **Idempotency / anti-dupe**: every economy request carries a client-generated
   `requestId` (128-bit). The first processing stores `(session, player, requestId) →
   result` in `economy_requests`. A replay returns the stored result and does nothing
   else. A requestId reused with a different payload hash is rejected with
   `RequestIdReuse` and counted as a violation.
3. **Amounts**: integer, `0 < amount ≤ Economy.MaxSingleTransfer` (default 10^12), no
   overflow (checked arithmetic). The source wallet balance must be ≥ amount.
   Pending offers are already removed from the balance into escrow, so no separate
   hold accounting is needed.
4. **Wallet state**: neither wallet is frozen, and the source is not overdrawn.
5. **Permissions**:
   - The action scope (above).
   - Spending from a `TeamShared` wallet toward another team requires
     `Economy.SharedWalletSpend` (`AnyMember` default, or `LeaderOnly`).
   - Pool withdrawals follow their policy.
   - Asset legs pass `AssetPermissionPolicy`.
   - Self-dealing (sender = recipient) is rejected.
6. **Rate limits**: 5 economy requests per 10 s per player. At most
   `Economy.MaxOpenOffersPerPlayer` (default 10) open loan offers plus trade proposals.
7. **Conservation invariant**: after each transaction, `Σ entries = 0` is asserted in
   code. A background `EconomyAuditor` (every 60 s, and on demand) checks that
   `Σ all wallet balances (incl. World and Escrow) = 0`, that every terminal loan or
   trade has a zero escrow, and that the stored balances equal the ledger sums. A
   violation raises a critical alert and **freezes economy actions** for the session
   until an admin acknowledges it.
8. **Reversal** (admin only): this creates a `Reversal` transaction with the negated
   entries and links `Reverses`/`ReversedBy`. A transaction can be reversed at most once.
   The reversal is rejected if it would make a non-World wallet negative, unless `force`
   is set (which marks the wallet overdrawn). For trade settlements, a reversal refunds
   credits only. The admin may additionally send the authority `AssetReturn{tradeId}`
   (best effort, reported back). The trade's state stays `Completed`, with a
   `reversed` flag.

#### Protocol messages (canonical names, protocol.md §20 / ADR-027)

- Client → server: `CreditTransferRequest`, `DonateRequest`, `PoolDepositRequest`,
  `PoolWithdrawRequest`, `LoanOffer`, `LoanRespond`, `LoanRepay`, `LoanForgive`,
  `LoanCancel`, `TradeProposal`, `TradeCounter`, `TradeAccept`, `TradeCancel`, `CreditDelta` (self).
- Server → client: `EconomyResult`, `WalletUpdate`, `LoanStatus`, `TradeStatus`,
  `TradeResult`, `ServerNotice` (overdue, frozen).
- Server ↔ authority: `AssetTransferOrder`, `AssetTransferConfirm`, `TradeQuery`,
  `CreditDelta`. `NodeStats` carries `game_time`. `AssetReturn` is a later admin
  best-effort action.
- All are Control lane. `MessagePolicy` restricts `AssetTransferConfirm` and
  `CreditDelta` for other players to the authority.

#### Settings (`EconomyOptions`, per session, hot unless noted)

`CreditMode` (Auto), `TeamPoolEnabled` (true), `PoolWithdrawPolicy` (AnyMember),
`PoolWithdrawDailyLimitPerPlayer` (null), `SharedWalletSpend` (AnyMember),
`DonateScope` (Teammates), `LoanScope` (Teammates), `TradeScope` (Allied),
`MaxSingleTransfer`, `MaxLoanPrincipal`, `MaxLoanInterestBp` (5000),
`MaxOpenLoansPerPlayer` (5), `MaxOpenOffersPerPlayer` (10), `OfferDefaultTtlMinutes` (30),
`TradeRequiresProximity` (true), `TradeExecuteTimeoutSeconds` (30),
`StartingCredits` (null = from the save).

Changing `CreditMode` while Running needs `confirm` (migration preview). Tightening a
scope cancels open offers that no longer qualify, refunds them, and notifies the players.

---

## 3. Save-file distribution service

### 3.1 Store

- Saves are **content-addressed**: `data/saves/<sha256>.xml.gz`. File names are never
  derived from user input, which rules out path traversal.
- Upload temp files go in `data/uploads/<uploadId>.part`, with a JSON sidecar
  (`.part.json`: expected size, received ranges, uploader, started_at). These are garbage
  collected after 24 h.
- On finalize, the server:
  1. Computes SHA-256 incrementally while receiving, and compares it to the declared hash.
  2. Checks the gzip magic, decompresses only the first 256 KB, and confirms the root
     element is `<savegame`. It extracts `<info>` (game version, save time, player name,
     money if present, and the mod list if present) into `meta_json`.
  3. Moves the file atomically into place, inserts the `saves` row, and publishes
     `SaveStored`.
- Upload size limit is `MaxSaveBytes`, default 1 GiB. X4 saves are usually 30 to 200 MB
  compressed.

### 3.2 Sources

- **Authority upload (in-band, primary)**: after `RequestSave`, or after a manual save by
  the authority player, the mod sends `SaveStarted` (journal marker) and then
  `SaveUploadBegin{checkpoint_id, kind=Save|Manifest, size, sha256, name, ghosts_cleaned}`.
  The server replies `SaveUploadAccept{uploadId, chunkSize=256 KiB, resumeOffset}` and the
  mod streams `SaveChunk{uploadId, offset, bytes}` on the Bulk lane. The server sends
  `SaveChunkAck{offset}` every 4 chunks, with an 8-chunk window. Finish with
  `SaveUploadEnd`. **If `ghostsCleaned == false`, the server refuses to make the save the
  session's current save** (the save is stored and flagged). This is the reference lesson
  about ghosts being baked into saves.
- **Admin upload (HTTP)**: from the GUI Saves screen, as a chunked resumable upload (3.4).
- If the authority starts with a save the server has never seen, the server asks it to
  upload one before clients are admitted (`RequestSave{SessionStart}`).

### 3.3 Client download

1. After a client is admitted, the server sends
   `SessionSaveInfo{saveId, sha256, size, displayName, httpUrl?, downloadToken}`.
2. The client checks for a local copy named `x4mp_<sha256[0..12]>.xml.gz` in its X4 save
   folder. That is a mod concern, but the naming is part of the contract. It verifies the
   SHA-256, caching the hash keyed by mtime and size.
3. If the copy is missing or does not match, the client downloads using **either**:
   - **In-band** (default; the mod needs no HTTP stack): `SaveDownloadRequest{sha256, offset}`
     returns windowed `SaveChunk`s on the Bulk lane. The window is 8 x 256 KiB and is acked.
     To resume, request again with the current offset. The Bulk lane only drains when
     Control and Realtime are idle, so it does not hurt the gameplay of players already in
     game, which share the server uplink but not the per-connection queue.
   - **HTTP** (optional capability `save-http`): `GET /files/saves/{sha256}` with header
     `Authorization: X4MP-Download <token>`. The response supports `Range` (enable range
     processing), uses `ETag = "sha256"`, and supports `If-Range`. Tokens are random
     128-bit values, valid for 1 h, and bound to the connection's player.
4. The client verifies the full SHA-256 and atomically renames the file into place. It
   reports `SaveReady{sha256}`, the server moves the client to `Loading`, the client loads
   the save, and then sends `NodeReady`.

The upload, download and progress percentage for every transfer are visible on the GUI
(Sessions & Saves, and per player on Players).

The server-wide outbound save bandwidth cap is `SaveBandwidthCapMBps` (default 0 =
unlimited). It is enforced with a token bucket shared by all Bulk producers.

### 3.4 HTTP endpoints for saves (admin)

```
POST   /api/v1/saves/uploads                 {fileName,size,sha256}       -> 201 {uploadId, chunkSize, receivedBytes}
PUT    /api/v1/saves/uploads/{uploadId}      Content-Range: bytes a-b/size (raw body) -> 204 {receivedBytes}
GET    /api/v1/saves/uploads/{uploadId}      -> {receivedBytes, size}       (resume point)
POST   /api/v1/saves/uploads/{uploadId}/complete -> 201 SaveDto | 422 {error:"HashMismatch"|"NotASave"}
DELETE /api/v1/saves/uploads/{uploadId}
GET    /api/v1/saves                         -> SaveDto[]
GET    /api/v1/saves/{id}/download           (admin cookie; Range supported)
PATCH  /api/v1/saves/{id}                    {displayName?, pinned?}
DELETE /api/v1/saves/{id}                    (409 if referenced by an active session)
```

The browser computes SHA-256 incrementally with a small WASM-free JS implementation
(`hash-wasm` is not used; we vendor a tiny streaming SHA-256, about 3 KB, in
`web/src/lib/sha256.ts`), because WebCrypto `digest` cannot stream. Uploading 8 MiB chunks
lets the browser resume after a refresh, since the upload id is kept in `localStorage`.

---

## 4. Admin REST API and SignalR hub

### 4.1 Conventions

- Base path `/api/v1`. JSON only, with camelCase via `System.Text.Json` source-generated
  contexts.
- Errors use RFC 7807 `ProblemDetails`, with a `code` extension (`NotFound`,
  `ValidationFailed`, `Conflict`, `SessionNotRunning`, ...).
- Endpoints are written as Minimal APIs grouped per feature: `MapGroup("/api/v1/players")`.
  `.RequireAuthorization("Admin")` is the default. Read-only endpoints allow `Viewer`.
- **TypeScript contract**: `X4MP.Server.Tests` contains `TsContractGenerator`, a reflection
  pass over `X4MP.Server.Api.Dtos` that emits `web/src/api/generated.ts`. CI runs it and
  fails if `git diff --exit-code` shows changes. This avoids NSwag/TypeGen dependencies.

### 4.2 Authentication and authorization

- **Admin users**: on first start with an empty `admin_users` table, the server creates
  `admin` with a random 20-character password. It prints the password once to the console
  and writes it to `data/initial-admin-password.txt` (the file ACL is restricted to the
  current user or service account, and it is deleted when the password is changed). The
  account is marked `must_change`.
- **Hashing**: `Rfc2898DeriveBytes.Pbkdf2(password, salt16, 600_000, SHA256, 32)`, with the
  iteration count stored per row so it can be upgraded later. Comparison uses
  `CryptographicOperations.FixedTimeEquals`.
- **Browser session**: ASP.NET Core cookie authentication. The cookie is `x4mp_admin`,
  `HttpOnly`, `SameSite=Strict`, `Secure` when served over HTTPS, with an 8 h sliding
  expiry. On API paths, the login redirect is replaced with a 401.
- **CSRF**: SameSite=Strict, plus a requirement that every non-GET API request carry the
  header `X-X4MP: 1`. Browsers cannot send that cross-origin without a CORS preflight, and
  we reject the preflight.
- **API tokens** (scripts and CI): `Authorization: Bearer x4mp_<base64url 32 bytes>`. The
  token is stored as a SHA-256 hash and has a role. This is a custom
  `AuthenticationHandler` (about 60 lines).
- **Roles**: `Admin` (everything) and `Viewer` (all GETs plus hub read topics. No kick, ban,
  settings, chat send or save operations).
- **Rate limiting**: `POST /auth/login` is limited to 5 per minute per IP (fixed window), and
  the account locks for 5 min after 10 failures.
- The SignalR hub uses the same cookie or bearer token. Browsers pass the bearer via the
  `access_token` query string, which we accept on `/hubs/admin` only.

### 4.3 Binding, CORS, TLS

- The default admin bind is `http://0.0.0.0:47790`, so it is reachable on the LAN as
  PLAN.md requires.
- On first run the console prints the LAN URLs and a warning that traffic is plain HTTP.
- `Admin:AllowRemote=false` binds to `127.0.0.1` only.
- An optional IP allow-list middleware (`Admin:AllowedNetworks`, CIDR list) is provided.
  The default is loopback plus private RFC1918 ranges. Requests from public IPs get 403
  unless they are explicitly allowed.
- **HTTPS**: `Admin:HttpsEndpoint` uses a user-supplied PFX, or with
  `Admin:GenerateSelfSigned=true` the server creates a self-signed cert into `data/certs/`
  (using `CertificateRequest`, no dependency). The GUI shows the cert fingerprint so users
  can verify it.
- **CORS**: off (same-origin SPA). In the `Development` environment only, it allows
  `http://localhost:5173` (Vite dev server), with credentials.
- Security headers on every response: `Content-Security-Policy: default-src 'self';
  connect-src 'self' ws: wss:; img-src 'self' data:; style-src 'self' 'unsafe-inline'`,
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `X-Frame-Options: DENY`.

### 4.4 REST endpoints

| Method and path | Role | Body / Query | Returns |
|---|---|---|---|
| `POST /auth/login` | anon | `{username,password}` | 204 + cookie, or 401 |
| `POST /auth/logout` | any | | 204 |
| `GET /auth/me` | any | | `MeDto{username, role, mustChangePassword}` |
| `POST /auth/change-password` | any | `{current,new}` | 204 |
| `GET /server` | Viewer | | `ServerInfoDto` |
| `GET /dashboard` | Viewer | | `DashboardSnapshotDto` (same as the hub push) |
| `GET /sessions` | Viewer | `?state=&limit=` | `SessionSummaryDto[]` |
| `GET /sessions/current` | Viewer | | `SessionDetailDto` or 204 |
| `POST /sessions` | Admin | `CreateSessionRequest{name, saveId, settings?}` | `SessionDetailDto` |
| `POST /sessions/{id}/start` | Admin | `{authorityPlayerId?}` | 202 |
| `POST /sessions/{id}/stop` | Admin | `{requestFinalSave: bool, message?}` | 202 |
| `POST /sessions/{id}/request-save` | Admin | | 202 (authority saves and uploads) |
| `POST /sessions/{id}/promote` | Admin | `{playerId}` | 501 in M1 |
| `GET /sessions/{id}/events` | Viewer | `?type=&since=&limit=` | `SessionEventDto[]` |
| `GET /players` | Viewer | `?online=&q=` | `PlayerDto[]` |
| `GET /players/{id}` | Viewer | | `PlayerDetailDto` (incl. history, bans, live stats) |
| `POST /players/{id}/kick` | Admin | `{reason}` | 202 |
| `POST /players/{id}/mute` | Admin | `{minutes?: number, reason}` | 204 |
| `DELETE /players/{id}/mute` | Admin | | 204 |
| `POST /players/{id}/teleport-view` | Admin | `{sectorId}` | 501 in M1 (hook for in-game camera snap) |
| `PATCH /players/{id}` | Admin | `{notes?, releaseName?: bool}` | `PlayerDto` |
| `GET /bans` | Viewer | `?active=true` | `BanDto[]` |
| `POST /bans` | Admin | `CreateBanRequest{playerId?, ipCidr?, reason, durationMinutes?}` | `BanDto` (online targets are kicked) |
| `DELETE /bans/{id}` | Admin | | 204 (revoke) |
| `GET /sessions/{sid}/teams` | Viewer | | `TeamsStateDto` (teams, members, unassigned, matrix, policy, wallets) |
| `POST /sessions/{sid}/teams` | Admin | `CreateTeamRequest{name, color?, factionSlot?, maxMembers?, locked?, password?}` | `TeamDto` (409 `NoFactionSlot` / `NameTaken`) |
| `PATCH /sessions/{sid}/teams/{tid}` | Admin | `{name?, color?, factionSlot?, leaderPlayerId?, locked?, maxMembers?, password?}` | `TeamDto` |
| `DELETE /sessions/{sid}/teams/{tid}` | Admin | `?moveMembersTo={tid2}` (required if the team has members) | 204 (409 if the team owns assets and no target is given) |
| `PUT /sessions/{sid}/teams/members/{playerId}` | Admin | `{teamId \| null, role?, moveAssets?: "ShipOnly"\|"AllOwned"\|"None"}` | `TeamMemberDto` (null = back to Unassigned; 409 for the authority while Running) |
| `PUT /sessions/{sid}/teams/members` | Admin | `BulkAssignRequest{assignments:[{playerId, teamId}]}` | `TeamsStateDto` (atomic, for multi-drag) |
| `PUT /sessions/{sid}/teams/relations` | Admin | `{entries:[{teamA, teamB, relation}]}` | `TeamRelationsDto` (bumps the version; pushed to nodes) |
| `POST /sessions/{sid}/teams/preset` | Admin | `{preset:"Coop"\|"AlliedSeparate"\|"FreeForAll"\|"TwoTeams", confirm: bool}` | `TeamsStateDto` (requires `confirm` while Running) |
| `PATCH /sessions/{sid}/teams/policy` | Admin | `{joinMode?, autoAssign?, creditPolicy?, assetPolicy?, allowFriendlyFire?, ...}` | `TeamPolicyDto` |
| **Economy (2.14)** | | | |
| `GET /sessions/{sid}/economy/summary` | Viewer | | `EconomySummaryDto` (mode, totals, money supply, open loans/trades, overdue, in-doubt, auditor status) |
| `GET /sessions/{sid}/economy/policy` / `PATCH` | Viewer / Admin | `EconomyPolicyPatch` (+ `confirm` for a CreditMode change while Running) | `EconomyPolicyDto`, or 409 `{migrationPreview}` |
| `GET /sessions/{sid}/economy/wallets` | Viewer | `?kind=&q=` | `WalletDto[]` |
| `GET /sessions/{sid}/economy/wallets/{kind}/{ownerId}` | Viewer | | `WalletDetailDto` (balance, flags, recent transactions, open loans/trades) |
| `POST /sessions/{sid}/economy/wallets/{kind}/{ownerId}/adjust` | Admin | `{amount (signed), reason}` | `LedgerTxDto` (`AdminAdjust` vs World) |
| `POST /sessions/{sid}/economy/wallets/{kind}/{ownerId}/freeze` | Admin | `{frozen: bool, reason}` | `WalletDto` (frozen wallets cannot send or receive player actions; game deltas still book) |
| `GET /sessions/{sid}/economy/transactions` | Viewer | `?wallet=kind:id&kind=&actor=&refType=&refId=&since=&before=&limit=` | `LedgerTxDto[]` (cursor-paged) |
| `GET /sessions/{sid}/economy/transactions/{txId}` | Viewer | | `LedgerTxDto` with entries and the reversal link |
| `POST /sessions/{sid}/economy/transactions/{txId}/reverse` | Admin | `{reason, force?: bool, returnAsset?: bool}` | `LedgerTxDto` (reversal), or 409 `WouldOverdraw` / `AlreadyReversed` / `NotReversible` (escrow-internal) |
| `GET /sessions/{sid}/economy/ledger.csv` | Admin | `?since=&until=` | CSV export |
| `GET /sessions/{sid}/economy/loans` | Viewer | `?state=&playerId=` | `LoanDto[]` |
| `GET /sessions/{sid}/economy/loans/{id}` | Viewer | | `LoanDetailDto` (with repayments and events) |
| `POST /sessions/{sid}/economy/loans/{id}/forgive` | Admin | `{reason}` | `LoanDto` |
| `POST /sessions/{sid}/economy/loans/{id}/cancel` | Admin | `{reason, reverseDisbursement?: bool, force?: bool}` | `LoanDto` |
| `GET /sessions/{sid}/economy/trades` | Viewer | `?state=&playerId=` | `TradeOfferDto[]` |
| `GET /sessions/{sid}/economy/trades/{id}` | Viewer | | `TradeDetailDto` (legs, escrow, attempts, events) |
| `POST /sessions/{sid}/economy/trades/{id}/cancel` | Admin | `{reason}` | `TradeOfferDto` (only Proposed/Escrowed: refund and unlock) |
| `POST /sessions/{sid}/economy/trades/{id}/resolve` | Admin | `{outcome:"complete"\|"refund", reason}` | `TradeOfferDto` (only InDoubt) |
| `GET /sessions/{sid}/economy/events` | Viewer | `?type=&refType=&refId=&since=` | `EconomyEventDto[]` (incl. rejected requests) |
| `POST /sessions/{sid}/economy/audit` | Admin | `{acknowledge?: bool}` | `AuditorReportDto` (runs the invariant check now; acknowledging unfreezes after a breach) |
| `GET /chat` | Viewer | `?sessionId=&before=&limit=` | `ChatMessageDto[]` |
| `POST /chat` | Admin | `{text, channel:"all"\|"player", toPlayerId?, asBroadcast: bool}` | 202 |
| `GET /galaxy` | Viewer | | `GalaxyDto` (sectors, gates, positions; cached per save) |
| `GET /galaxy/sectors/{id}` | Viewer | | `SectorDetailDto` (summary, players inside) |
| `GET /logs` | Viewer | `?level=&source=&q=&before=&limit=` | `LogEntryDto[]` (ring buffer; file search is a later milestone) |
| `GET /logs/download` | Admin | `?date=` | file stream of the rolling log |
| `GET /settings` | Viewer | | `SettingsDto{sections:{[name]:{[key]:value}}}` (secrets masked) |
| `GET /settings/schema` | Viewer | | `SettingsSchemaDto` |
| `PATCH /settings` | Admin | `{"Session.MaxPlayers": 12, ...}` | `SettingsDto` or 400 with per-key errors |
| `GET /diagnostics/connections` | Viewer | | `ConnectionStatsDto[]` |
| `POST /diagnostics/connections/{id}/trace` | Admin | `{enabled, sampleEvery}` | 204 |
| `GET /diagnostics/metrics` | Viewer | `?series=a,b&window=600` | `MetricSeriesDto[]` |
| `GET /tokens` / `POST /tokens` / `DELETE /tokens/{id}` | Admin | | API token management |
| `GET /audit` | Admin | `?limit=` | `AuditEntryDto[]` |
| `GET /healthz` (no prefix) | anon | | `200 "ok"` (for service monitors; no details) |

### 4.5 Key DTOs (C#, mirrored to TS)

```csharp
public sealed record ServerInfoDto(string Version, string ProtocolRange, string BuildHash,
    DateTimeOffset StartedAt, string[] NodeEndpoints, string[] AdminUrls, bool Https);

public sealed record DashboardSnapshotDto(
    DateTimeOffset At, SessionSummaryDto? Session, int PlayersOnline, int MaxPlayers,
    AuthorityStatusDto? Authority, IReadOnlyList<PlayerLiveDto> Players,
    TrafficDto Traffic, int EntitiesInMirror, int SectorsCaptured,
    double TickP99Ms, IReadOnlyList<AlertDto> ActiveAlerts);

public sealed record SessionSummaryDto(long Id, string Name, SessionState State,
    string? SaveName, string? SaveSha256, DateTimeOffset? StartedAt, TimeSpan Uptime, int Players);

public sealed record AuthorityStatusDto(long PlayerId, string Name, double Fps, double FrameP99Ms,
    double SimSpeed, int CapturedEntities, long AuthorityTick, double LatencyMs);

public sealed record PlayerLiveDto(long PlayerId, long ConnectionId, string Name, NodeRole Role,
    NodeState State, string RemoteAddress, double RttMs, double JitterMs, double Fps,
    double KBpsIn, double KBpsOut, uint? SectorId, string? SectorName, Vec3Dto? Position,
    double? SpeedMs, int InterestEntities, TransferProgressDto? SaveTransfer,
    bool Muted, TimeSpan Connected);

public sealed record TrafficDto(double KBpsIn, double KBpsOut, double FramesInPerSec,
    double FramesOutPerSec, long DroppedRealtimeLast60s, int SlowConsumerKicksLastHour);

public sealed record PlayerDto(long Id, string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    TimeSpan TotalPlaytime, bool Online, bool Muted, BanDto? ActiveBan, string? LastIp, string? Notes);

public sealed record BanDto(long Id, long? PlayerId, string? PlayerName, string? IpCidr, string Reason,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Active);

public sealed record SaveDto(long Id, string Sha256, long SizeBytes, string DisplayName, string Source,
    DateTimeOffset UploadedAt, string? GameVersion, string? SaveTime, string? PlayerName,
    bool Pinned, bool GhostsCleaned, bool InUse);

public sealed record GalaxyDto(string SaveSha256, IReadOnlyList<ClusterDto> Clusters,
    IReadOnlyList<SectorDto> Sectors, IReadOnlyList<GateLinkDto> Links);
public sealed record SectorDto(uint Id, string Macro, string Name, uint ClusterId, Vec2Dto MapPos,
    string? OwnerFaction);
public sealed record GateLinkDto(uint FromSector, uint ToSector, string Kind); // gate|highway|accelerator

// Teams (2.13). PlayerLiveDto and PlayerDto also gain `long? TeamId`; GalaxyFrame players carry teamId.
public sealed record TeamDto(long Id, string Name, string Color, int FactionSlot, long? LeaderPlayerId,
    bool Locked, int? MaxMembers, bool HasPassword, int MemberCount, int AssetCount);
public sealed record TeamMemberDto(long PlayerId, string Name, long? TeamId, string Role, bool Online,
    string AssignedBy, DateTimeOffset Since);
public sealed record TeamRelationsDto(uint Version, IReadOnlyList<TeamRelationEntryDto> Entries,
    string DefaultRelation);
public sealed record TeamRelationEntryDto(long TeamA, long TeamB, string Relation); // "Allied"|"Neutral"|"Hostile"
public sealed record TeamPolicyDto(string JoinMode, string AutoAssign, string AssetPolicy,
    bool AllowFriendlyFire, bool AllowAssetTransfer, bool AllowCreateInLobby, string MoveAssetsWithPlayer,
    int MaxTeams, int MaxFactionSlots);
// Economy (2.14)
public sealed record WalletDto(string Kind, long OwnerId, string OwnerName, long Balance, bool Frozen,
    string? FrozenReason, bool Overdrawn, DateTimeOffset UpdatedAt);
public sealed record LedgerTxDto(string Id, DateTimeOffset At, string Kind, string Actor, string? RequestId,
    string? RefType, long? RefId, string? Reverses, string? ReversedBy, string? Note,
    IReadOnlyList<LedgerEntryDto> Entries);
public sealed record LedgerEntryDto(string WalletKind, long WalletOwnerId, string WalletName, long Amount, long BalanceAfter);
public sealed record LoanDto(long Id, long LenderId, string Lender, long BorrowerId, string Borrower,
    long Principal, int InterestBp, long? AmountDue, long? Outstanding, string DueKind,
    DateTimeOffset? DueAt, long? DueGameSeconds, int AutoRepayPercent, string State, bool Overdue,
    DateTimeOffset CreatedAt, DateTimeOffset? AcceptedAt, DateTimeOffset? ClosedAt, string? CloseReason);
public sealed record TradeLegDto(string Type /*credits|wares|ship|station*/, long? Amount, string? Ware,
    int? Quantity, ulong? FromShipId, ulong? ToShipId, ulong? AssetId, string? AssetName);
public sealed record TradeOfferDto(long Id, long ProposerId, string Proposer, long CounterpartyId, string Counterparty,
    TradeLegDto ProposerGives, TradeLegDto CounterpartyGives, string State, DateTimeOffset ExpiresAt,
    int ExecuteAttempts, string? FailureReason, bool Reversed, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt);
public sealed record EconomyPolicyDto(string CreditMode, string EffectiveCreditMode, bool TeamPoolEnabled,
    string PoolWithdrawPolicy, long? PoolWithdrawDailyLimitPerPlayer, string SharedWalletSpend,
    string DonateScope, string LoanScope, string TradeScope, long MaxSingleTransfer, long? MaxLoanPrincipal,
    int MaxLoanInterestBp, int MaxOpenLoansPerPlayer, int MaxOpenOffersPerPlayer, int OfferDefaultTtlMinutes,
    bool TradeRequiresProximity, int TradeExecuteTimeoutSeconds, long? StartingCredits);
public sealed record EconomySummaryDto(string EffectiveCreditMode, long MoneySupply /* -World balance */,
    long InEscrow, int OpenLoans, int OverdueLoans, long OutstandingDebt, int OpenTrades, int InDoubtTrades,
    int FrozenWallets, AuditorReportDto LastAudit);
public sealed record AuditorReportDto(DateTimeOffset At, bool Ok, bool EconomyFrozen, IReadOnlyList<string> Violations);
public sealed record EconomyEventDto(long Id, DateTimeOffset At, string Type, string Actor, string? RefType,
    long? RefId, string? FromState, string? ToState, string? Reason);
public sealed record TeamsStateDto(IReadOnlyList<TeamDto> Teams, IReadOnlyList<TeamMemberDto> Members,
    IReadOnlyList<TeamMemberDto> Unassigned, TeamRelationsDto Relations, TeamPolicyDto Policy,
    IReadOnlyList<WalletDto> Wallets);

public sealed record ChatMessageDto(long Id, DateTimeOffset At, string From, bool FromAdmin,
    string Channel, string Text);
public sealed record LogEntryDto(long Seq, DateTimeOffset At, string Level, string Source,
    string Message, string? Exception, IReadOnlyDictionary<string, string>? Props);
public sealed record ConnectionStatsDto(long ConnectionId, string? Player, NodeRole Role,
    string Transport, string Remote, TimeSpan Age, double RttMs, double JitterMs,
    LaneStatsDto Control, LaneStatsDto Realtime, LaneStatsDto Bulk,
    IReadOnlyList<MessageRateDto> TopMessageTypes, double FlushP99Ms, int ProtocolViolations);
public sealed record LaneStatsDto(double KBpsOut, double FramesPerSec, int QueuedFrames,
    int QueuedBytes, long Coalesced, long Dropped, int HighWaterBytes, double OldestItemMs);
public sealed record MessageRateDto(string Type, string Dir, double PerSec, double KBps);
```

### 4.6 SignalR hub `/hubs/admin`

Strongly typed: `class AdminHub : Hub<IAdminClient>`. Topics are SignalR **groups**, so
the server only pushes what an open screen needs. Group membership is cleaned up on
disconnect.

```csharp
// client -> server
Task<DashboardSnapshotDto> SubscribeDashboard();                  // joins "dashboard"; returns current snapshot
Task UnsubscribeDashboard();
Task<GalaxyDto?> SubscribeGalaxy();                                // "galaxy": players + sector aggregates
Task SubscribeSector(uint sectorId);                               // "sector:{id}": entity positions; adds admin
                                                                   //   interest -> CaptureSet (1 Hz) (max 2 per admin)
Task UnsubscribeSector(uint sectorId);
Task<IReadOnlyList<LogEntryDto>> SubscribeLogs(LogFilterDto filter); // returns backfill (last 500), then live
Task UnsubscribeLogs();
Task SubscribeDiagnostics();                                       // "diag": per-connection stats 1 Hz
Task UnsubscribeDiagnostics();
Task SubscribeChat();                                              // "chat"
Task SendChat(SendChatRequest req);                                // Admin role only ([Authorize(Roles="Admin")])
Task<TeamsStateDto> SubscribeTeams();                              // "teams"; returns current state
Task UnsubscribeTeams();
// (team mutations go through REST, so they are audited and validated in one place)

// server -> client (IAdminClient)
Task Dashboard(DashboardSnapshotDto s);                    // 1 Hz
Task PlayerChanged(PlayerLiveDto p);                       // on join/leave/state change (immediate)
Task PlayerRemoved(long connectionId);
Task SessionChanged(SessionSummaryDto s);                  // immediate
Task GalaxyFrame(GalaxyFrameDto f);                        // 1 Hz: players[{id,name,sectorId,pos,heading}],
                                                           //   sectorAgg[{sectorId,ships,stations}], interest[{playerId,sectorIds,tier}]
Task SectorFrame(SectorFrameDto f);                        // 4 Hz: packed entities (see below)
Task LogBatch(IReadOnlyList<LogEntryDto> entries);         // batched every 250 ms, max 200/batch, filtered server-side
Task Diagnostics(IReadOnlyList<ConnectionStatsDto> c);     // 1 Hz
Task Chat(ChatMessageDto m);
Task SaveTransfer(TransferProgressDto t);                  // 2 Hz while any transfer is active
Task Alert(AlertDto a);
Task SettingsChanged(SettingsDto s);
// teams group (also: dashboard group receives TeamMemberChanged so player tables recolour)
Task TeamUpserted(TeamDto t);                              // create or edit
Task TeamDeleted(long teamId, long? movedMembersTo);
Task TeamMemberChanged(TeamMemberDto m);                   // assign, move, unassign, role change
Task TeamRelationsChanged(TeamRelationsDto r);
Task TeamPolicyChanged(TeamPolicyDto p);
Task TeamsReset(TeamsStateDto s);                          // after a preset or bulk assign
Task PlayerAwaitingTeam(TeamMemberDto m);                  // AdminAssign mode: toast + badge
Task PermissionDenied(PermissionDeniedDto d);              // {playerId, entityId, action, reason}; rate-limited 5/s
// economy group (SubscribeEconomy/UnsubscribeEconomy; SubscribeEconomy returns EconomySummaryDto)
Task WalletChanged(WalletDto w);                           // coalesced per wallet, max 4 Hz
Task LedgerPosted(LedgerTxDto tx);                         // every committed transaction (GUI filters client-side)
Task LoanChanged(LoanDto l);                               // any state/outstanding change, incl. Overdue
Task TradeChanged(TradeOfferDto t);
Task EconomyEvent(EconomyEventDto e);                      // incl. rejected requests (rate-limited 10/s)
Task EconomySummary(EconomySummaryDto s);                  // 1 Hz while the group has members
Task EconomyAlert(AlertDto a);                             // invariant breach, overdraft, InDoubt trade
```

Economy mutations (adjust, freeze, reverse, forgive, cancel, resolve, policy) are REST
only. That keeps validation, audit and idempotency in one place: admin mutating requests
accept an optional `Idempotency-Key` header, stored the same way as player `requestId`s.

`SectorFrameDto` keeps the JSON small by using parallel arrays:
`{sectorId, tick, ids: number[], x: number[], z: number[], yaw: number[], cls: number[], flags: number[], playerIds: (number|null)[]}`.
Positions are rounded to 10 m. Entities are capped at `MapMaxEntities` (default 3000),
with players and stations always included. Per-viewer cost at 3000 entities and 4 Hz is
about 150 KB/s of JSON. Switching this hub to MessagePack later cuts that roughly 3x.

`AdminBroadcaster` (a hosted service) owns all pushes. It reads `SessionSnapshot`s and
`EventBus` events, and it **skips** groups with zero members. A slow browser cannot hurt
the server: we set the SignalR `MaximumParallelInvocationsPerClient` default, and on the
server side the broadcaster uses `Clients.Group(...).X(...)` without awaiting per client.
Kestrel's WebSocket output buffer limits are left at their defaults, and if a client's
buffer stays full the server closes that browser connection. The SPA reconnects
automatically (`withAutomaticReconnect`).

---

## 5. Web GUI

### 5.1 Stack choice: React + Vite + TypeScript

| Criterion | React + Vite + TS | Blazor WASM | Blazor Server |
|---|---|---|---|
| Payload embedded in exe | ~200 KB gz | 2-5 MB runtime | small, but a second SignalR circuit per tab |
| Live canvas map at 4 Hz with 3k entities | direct Canvas 2D, cheap | JS interop per frame or a canvas lib; heavier | every render diff goes over the wire, bad for the map |
| SignalR client | official `@microsoft/signalr` | built in | built in |
| Server load per admin tab | none beyond the hub | none beyond the hub | circuit state and render diffs on the server, competing with the relay |
| Language sharing | DTOs via generated TS (4.1) | shared C# DTOs | shared C# DTOs |
| Contributor pool and tooling (Vitest, Playwright, HMR) | very large | smaller | smaller |

**Decision: React 18 + Vite + TypeScript, built to static files and embedded into the exe.**
Blazor Server is ruled out because it would put UI rendering work on the same process
whose job is low-latency relay. Blazor WASM's only real advantage is shared C# DTOs, and
the generated TS contract covers that.

Structure:

```
server/web/
├─ index.html, vite.config.ts (proxy /api,/hubs,/files -> https://localhost:47790 in dev)
└─ src/
   ├─ main.tsx, App.tsx (router, auth gate, layout)
   ├─ api/ generated.ts (DTOs), http.ts (fetch wrapper: credentials, X-X4MP header, ProblemDetails)
   ├─ hub/ HubProvider.tsx (one HubConnection, auto-reconnect, topic ref-counting), useTopic.ts
   ├─ lib/ sha256.ts, format.ts (bytes, durations), uploads.ts (resumable chunk uploader)
   ├─ components/ Table, Sparkline (uPlot), StatTile, Badge, Dialog, ConfirmButton, Toast
   ├─ map/ GalaxyCanvas.tsx, SectorCanvas.tsx, camera.ts (pan/zoom), hitTest.ts
   └─ pages/ Dashboard, Players, PlayerDetail, Teams, Economy, Galaxy, Sessions, Chat, Logs, Settings, Diagnostics, Login
```

- Routing: `/`, `/players`, `/players/:id`, `/teams`, `/economy`, `/economy/:tab`, `/map`, `/map/:sectorId`, `/sessions`, `/chat`,
  `/logs`, `/settings`, `/diagnostics`, `/login`. The server falls back to
  `MapFallbackToFile("index.html")` for any non-`/api`, non-`/hubs`, non-`/files` GET.
- Embedded serving: `UseStaticFiles(new StaticFileOptions { FileProvider = new
  ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot") })`. Hashed assets get
  `Cache-Control: public, max-age=31536000, immutable`. `index.html` gets `no-cache`.
- Theme: dark by default (people run this beside a game), with a light toggle, using CSS
  variables. The layout is responsive down to tablet width, so a phone on the LAN can kick
  someone. The map is desktop-first.
- Accessibility basics: semantic tables, focus outlines, and status communicated by text
  and not only by color.
- Destructive actions (kick, ban, stop session, delete save) use `ConfirmButton`, a
  two-step inline confirm with a reason field where the API needs one.

### 5.2 Global frame

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ X4MP ▸ My Server        Session: "Friday Run"  ● RUNNING  01:42:13   👤 admin ▾      │
├───────────────┬──────────────────────────────────────────────────────────────────────┤
│ ▣ Dashboard   │                                                                      │
│ ☺ Players  4  │                          (page content)                              │
│ ⚑ Teams    1! │                                                                      │
│ ¤ Economy  2! │                                                                      │
│ ◎ Map         │                                                                      │
│ ▤ Sessions    │                                                                      │
│ ✉ Chat     •  │                                                                      │
│ ≡ Logs        │                                                                      │
│ ⚙ Settings    │                                                                      │
│ ⌁ Diagnostics │                                                                      │
│               │                                                                      │
│ hub: ● live   │  [!] Alert: Authority FPS below 15 for 30 s                    [x]   │
└───────────────┴──────────────────────────────────────────────────────────────────────┘
```

(The GUI uses an icon set. The glyphs above are placeholders.)

### 5.3 Dashboard

```
┌ Dashboard ───────────────────────────────────────────────────────────────────────────┐
│ ┌Session──────────┐ ┌Players────────┐ ┌Authority──────────┐ ┌Traffic──────────────┐  │
│ │ RUNNING         │ │ 4 / 8         │ │ Alice  58 FPS     │ │ ▲ 1.9 MB/s ▼ 240KB/s │  │
│ │ Friday Run      │ │ 1 loading     │ │ sim 1.0x  12ms p99│ │ ▁▂▃▅▆▅▆▇ (10 min)    │  │
│ │ save_017 (88MB) │ │               │ │ 18,204 entities   │ │ drops 0 /60s         │  │
│ │ up 01:42:13     │ │               │ │ 31 sectors capt.  │ │ tick p99 6.2 ms      │  │
│ │ [Stop] [Save]   │ │ [Broadcast…]  │ │ lat 4 ms          │ │                      │  │
│ └─────────────────┘ └───────────────┘ └───────────────────┘ └──────────────────────┘  │
│ Players                                                                              │
│ ┌──────────┬───────────┬────────┬──────┬──────┬─────────┬────────────────┬───────┐   │
│ │ Name     │ Role      │ State  │ Ping │ FPS  │ ▼ KB/s  │ Sector         │       │   │
│ ├──────────┼───────────┼────────┼──────┼──────┼─────────┼────────────────┼───────┤   │
│ │ Alice    │ AUTHORITY │ InGame │  2ms │ 58   │   12    │ Argon Prime    │ [⋯]   │   │
│ │ Bob      │ client    │ InGame │ 31ms │ 72   │  148    │ The Reach      │ [⋯]   │   │
│ │ Carol    │ client    │ Loading│ 44ms │  -   │    3    │ -  (save 64%)  │ [⋯]   │   │
│ │ Dan      │ client    │ InGame │ 19ms │ 61   │  171    │ Argon Prime    │ [⋯]   │   │
│ └──────────┴───────────┴────────┴──────┴──────┴─────────┴────────────────┴───────┘   │
│ ┌Ping (ms, per player)─────────────────┐ ┌Recent events──────────────────────────┐   │
│ │ uPlot multi-line, 10 min             │ │ 21:04 Bob destroyed Xenon K (Reach)   │   │
│ │                                      │ │ 21:03 Carol joined (downloading save) │   │
│ └──────────────────────────────────────┘ │ 21:01 Autosave stored save_017 ✓      │   │
│                                          └───────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

Data comes from the `dashboard` topic (1 Hz), `PlayerChanged` (immediate), and events
(`GET /sessions/current/events` plus live game events).

### 5.4 Players

```
┌ Players ─────────────────────────────── [search____] [● online ○ all] [+ Ban IP…] ───┐
│ ┌────────┬────────┬───────────────┬──────────┬────────────┬──────┬─────────────────┐ │
│ │ Name   │ Status │ Address       │ Playtime │ Last seen  │ Flags│ Actions         │ │
│ ├────────┼────────┼───────────────┼──────────┼────────────┼──────┼─────────────────┤ │
│ │ Bob    │ ● 31ms │ 192.168.1.23  │ 41h 12m  │ now        │      │ [Kick][Mute][Ban]│ │
│ │ Eve    │ ○      │ 192.168.1.40  │ 2h 03m   │ 3 days ago │ BAN  │ [Unban]         │ │
│ └────────┴────────┴───────────────┴──────────┴────────────┴──────┴─────────────────┘ │
├ Player: Bob ─────────────────────────────────────────────────────────────────────────┤
│ Role client · Mod 0.3.1 · Game 9.00 (611726) · Connected 00:52:10 · TCP+UDP          │
│ Ship: Nemesis Vanguard (#1272361) · The Reach · pos (12.1, 0.3, -4.8) km · 312 m/s   │
│ Interest: T0 214 ents · T1 1,830 · T2 3 sectors · budget use 71%                     │
│ ┌RTT/jitter────────────┐ ┌Bandwidth in/out──────┐ ┌FPS──────────────┐               │
│ └──────────────────────┘ └──────────────────────┘ └─────────────────┘               │
│ Sessions: Friday Run (now), Test 3 (Sep 28, 3h)…   Bans: none   Notes: [________]    │
│ [Kick…] [Mute 10m|1h|∞] [Ban…] [Release name] [Show on map]                          │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

The Ban dialog takes a reason (required), a duration (1 h / 1 d / 7 d / permanent / custom),
and a checkbox "also ban IP (/32)".

### 5.4a Teams & Factions

```
┌ Teams & Factions ── session "Friday Run" (RUNNING) ───────────────────────────────────┐
│ Preset: [Everyone co-op (one faction)] [All separate but allied] [Free-for-all]       │
│         [Two teams (versus)]   current: Custom                     [+ New team]       │
│ Join mode (•) Auto [Balance ▾] ( ) Lobby [☐ allow create] ( ) Admin assigns           │
│ Credits: Auto → per player (2 teams) [change in Economy ▸]  Asset command [Shared ▾] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│ ┌Unassigned (1)──────┐ ┌■ Argon Wolves  slot 0 ✎🔒🗑┐ ┌■ Teladi Traders slot 1 ✎🗑┐ │
│ │ ⠿ Carol  ○ waiting │ │ ★ Alice (authority)  ●     │ │ ★ Dan                ●    │ │
│ │                    │ │ ⠿ Bob                ●     │ │ ⠿ Eve                ○    │ │
│ │ drag onto a team → │ │ 2/4 · 37 assets · 1.2M Cr  │ │ 2/∞ · 12 assets · 410k Cr │ │
│ └────────────────────┘ └────────────────────────────┘ └───────────────────────────┘ │
│   ⠿ = drag handle · ★ = leader (click to toggle) · ● online ○ offline                 │
│   Moving a player while RUNNING: dialog "Move assets: (•) ship only ( ) all ( ) none"  │
├ Relations (symmetric; click a cell to cycle Allied → Neutral → Hostile) ─────────────┤
│                    │ Argon Wolves │ Teladi Traders │ Pirates      │                   │
│ Argon Wolves       │      —       │   ALLIED  ▲    │  HOSTILE ✖   │                   │
│ Teladi Traders     │   ALLIED  ▲  │      —         │  NEUTRAL ○   │                   │
│ Pirates            │  HOSTILE ✖   │   NEUTRAL ○    │     —        │                   │
│ Default for new teams [Neutral ▾]                 [Discard] [Apply relations (2)]      │
├ Team pools ────────────────────────────────────────────────────────────────────────────┤
│ Argon Wolves pool 1,204,330 Cr   Teladi Traders pool 410,000 Cr        (Economy ▸)      │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- Drag and drop uses native HTML5 DnD with a keyboard alternative: a row "Move to…"
  menu. A drop calls `PUT /teams/members/{id}`, with an optimistic UI that rolls back on
  error. A multi-select drag uses the bulk endpoint.
- Relation edits are staged locally and applied as one `PUT /teams/relations`. The cells
  show text and icons, not colour alone.
- Applying a preset while Running shows a confirm dialog that summarizes the
  consequences ("4 players will be moved; 37 assets re-owned; credit mode Auto switches
  to Shared: balances merged"), including the economy migration preview from 2.14.
- Team colours are used everywhere: player tables, map markers and the interest overlay.
  On the Map, a layer toggle "colour by team / by player" is added.

### 5.4b Economy

```
┌ Economy ── Friday Run · mode: Auto → PER PLAYER · supply 8.41M Cr · escrow 350k · ⚠ 1 in-doubt ┐
│ [Overview] [Wallets] [Transactions] [Loans 2!] [Trades 1!] [Policy]       auditor ✓ 21:05:00 │
├ Wallets ─────────────────────────────────────────────────────────── [search____] [kind ▾] ────┤
│ Wallet                    │ Kind      │   Balance │ Flags        │                           │
│ Bob                       │ player    │   912,400 │              │ [Ledger][Adjust…][Freeze] │
│ Carol                     │ player    │    -4,100 │ OVERDRAWN    │ [Ledger][Adjust…][Freeze] │
│ Argon Wolves pool         │ team_pool │ 1,204,330 │              │ [Ledger][Adjust…][Freeze] │
│ Eve                       │ player    │    51,000 │ ❄ FROZEN     │ [Ledger][Adjust…][Unfreeze]│
├ Transactions (live) ───── filter: wallet [Bob ▾] kind [all ▾] actor [all ▾] [⤓ CSV] ─────────┤
│ 21:04:55 Donate        Bob → Dan              -50,000 / +50,000   req 7f3a…  [⋯][Reverse…]   │
│ 21:04:31 LoanRepay     Dan → Bob (loan #12)   -20,000 / +20,000   auto 25%   [⋯]             │
│ 21:03:10 TradeSettle   escrow#9 → Dan (ship)  -300,000 / +300,000            [⋯][Reverse…]   │
│ 21:02:00 GameIncome    World → Bob           +120,000             trade NPC  [⋯]             │
│ 20:58:12 Reversal  ↺ of 01HZ…                                     admin:jack [⋯]             │
├ Loans ─────────────────────────────────────────────────────────── [state: open ▾] ───────────┤
│ #  │ Lender → Borrower │ Principal │ Int. │ Due              │ Outstanding │ State   │         │
│ 12 │ Bob → Dan         │  200,000  │  5%  │ 22:00 (real)     │   90,000    │ Active  │[Forgive…][Cancel…]│
│ 14 │ Alice → Carol     │  100,000  │  0%  │ day 3 12:00 (game)│  100,000   │ OVERDUE │[Forgive…][Cancel…]│
├ Trades ─────────────────────────────────────────────────────────── [state: open ▾] ──────────┤
│ #  │ Proposer gives        │ Counterparty gives │ State      │ Attempts │                      │
│ 9  │ Dan: 300,000 Cr       │ Bob: ship "Kestrel"│ Completed  │ 1        │ [⋯][Reverse…]        │
│ 11 │ Eve: 40 Energy Cells  │ Carol: 6,000 Cr    │ ⚠ IN DOUBT │ 3        │ [Resolve: complete|refund]│
│ 13 │ Bob: 20,000 Cr        │ Alice: 100 Ore     │ Proposed   │ 0        │ [Cancel…]            │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
 Policy tab: Credit mode (•) Auto ( ) Per player ( ) Shared  [migration preview before save]
   Team pool ☑  withdraw [Any member ▾] daily limit [____]   Shared-wallet spend [Any member ▾]
   Donate [Teammates ▾]  Loans [Teammates ▾]  Trades [Allied teams ▾]   (Off/Teammates/Allied/Anyone)
   Max transfer [____]  Max loan [____]  Max interest [50 %]  Open loans/player [5]  Open offers [10]
   Offer TTL [30 min]  Trade needs same sector ☑  Execute timeout [30 s]  Starting credits [from save]
```

- The Overview tab shows the summary tiles (money supply, escrow, debt, overdue, in-doubt),
  a balance-over-time sparkline per wallet (from the ledger), and the auditor status with
  [Run audit now].
- Every row's [⋯] opens a detail drawer with the full double-entry legs, request id,
  related loan or trade timeline (`economy_events`) and the reversal link.
- Reverse, Adjust, Freeze, Forgive, Cancel and Resolve all need a reason, use
  `ConfirmButton`, and show a preview such as "Dan's balance would become -12,000;
  tick *force* to allow". The Reverse action on a trade settlement also offers
  "☐ ask authority to return the asset".
- The badges in the navigation count overdue loans plus in-doubt trades. An auditor
  breach shows a red banner on every page.
- The Player detail page (5.4) gets an "Economy" panel with wallet, open loans and trades,
  and recent transactions.

### 5.5 Galaxy / sector map

```
┌ Map ─────────── Galaxy ▸ The Reach        [layers: ☑players ☑interest ☑gates ☐NPCs] ─┐
│ ┌──────────────────────────────────────────────────────────┐ ┌Legend / selection──┐ │
│ │        ⬡ Hatikvah          ⬡ Argon Prime ◆Alice          │ │ ◆ Alice (auth)     │ │
│ │           \                 /  ║    ◆Dan                 │ │ ◆ Bob  ▒ interest  │ │
│ │   ⬡ Silent Witness ── ⬡ Two Grand ══ ⬡ The Reach ◆Bob    │ │ ◆ Dan              │ │
│ │                          │        ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒       │ │ ── gate ══ highway  │ │
│ │                    ⬡ Black Hole Sun   ▒(T2 prefetch)▒    │ │ heat: ship count    │ │
│ │                                                          │ │                    │ │
│ │  wheel=zoom  drag=pan  click sector=open  dbl=follow     │ │ The Reach          │ │
│ └──────────────────────────────────────────────────────────┘ │ 412 ships, 9 stns  │ │
│                                                               │ players: Bob       │ │
│                                                               │ [Open sector view] │ │
│                                                               └────────────────────┘ │
├ Sector view: The Reach (top-down X/Z, 4 Hz) ─────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────────┐  Filter: ☑S ☑M ☑L ☑XL   │
│ │  ·  ·      ▪station        ·   ◆Bob→                     │  ☑stations ☐ghost-only  │
│ │     ·   ·        ( T0 near radius ring around Bob )      │  entities 1,830 (shown  │
│ │  ▪        ·  ·        ·                ·  ⊙gate          │  1,830 / cap 3,000)     │
│ └──────────────────────────────────────────────────────────┘  hover: id/macro/owner  │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

- **Galaxy layer** (topic `galaxy`, 1 Hz): sectors are drawn at `MapPos` as hexes (sized by
  cluster), with gate and highway edges, a heat tint from ship count, and player markers
  with a heading tick. Interest regions are drawn as a translucent outline in each
  player's color, shaded by tier.
- **Sector layer** (topic `sector:{id}`): Canvas 2D, `requestAnimationFrame`, with linear
  interpolation between the last two frames for smoothness. Points are culled by zoom.
  A hit-test grid supports hover and click. Opening a sector view **adds admin interest**,
  so if no player is there the authority starts capturing it at 1 Hz (capped at 2 open
  sector views per admin, and the GUI says so).
- Works without the authority: before `GalaxyMetadata` arrives, the page shows "waiting
  for authority galaxy metadata".

### 5.6 Sessions & Saves

```
┌ Sessions & Saves ────────────────────────────────────────────────────────────────────┐
│ Current session                                                                       │
│  Friday Run · RUNNING · authority Alice · save_017 · started 19:21 · autosave 15 min  │
│  [Request save now] [Stop session…] [Promote authority… (disabled: M2+)]              │
│                                                                                       │
│ New session  Name [__________]  Save [save_017 ▾]  Authority [any ▾]  [Create & Start]│
│                                                                                       │
│ Saves                                              [⇪ Upload .xml.gz] (drag & drop)   │
│ ┌──────────────┬───────┬───────────┬────────────┬──────────┬────────┬──────────────┐ │
│ │ Name         │ Size  │ Source    │ Game ver.  │ Saved at │ Status │              │ │
│ ├──────────────┼───────┼───────────┼────────────┼──────────┼────────┼──────────────┤ │
│ │ save_017 📌  │ 88 MB │ authority │ 9.00       │ 21:01    │ IN USE │ [⬇][✎][🗑]   │ │
│ │ save_016     │ 87 MB │ authority │ 9.00       │ 20:46    │ ⚠ ghosts│ [⬇][✎][🗑]   │ │
│ │ mystart      │ 61 MB │ upload    │ 9.00       │ Sep 29   │        │ [⬇][✎][🗑]   │ │
│ └──────────────┴───────┴───────────┴────────────┴──────────┴────────┴──────────────┘ │
│ Transfers:  ⇣ Carol  save_017  64% ████████░░░░ 12.4 MB/s  ETA 0:05   (in-band)      │
│             ⇡ upload mystart.xml.gz  100% ✓ sha256 verified                          │
│                                                                                       │
│ Session history  (name · dates · duration · players · end reason)  [view events]      │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

### 5.7 Chat / broadcast

```
┌ Chat ─────────────────────────────────────────────────── channel [All ▾] ─────────────┐
│ 21:02 Bob: anyone at the Reach shipyard?                                              │
│ 21:03 Alice: omw                                                                       │
│ 21:04 [ADMIN] Server restarts at 22:00                                                 │
│ 21:05 [SYSTEM] Carol joined                                                            │
│                                                                                        │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ To: (•) everyone ( ) player [Bob ▾]   [☐ show as on-screen broadcast banner]           │
│ [Type a message…                                                     ] [Send]          │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### 5.8 Logs

```
┌ Logs ───────────────────────────────────────────────────────────────────────────────┐
│ Level [≥ Info ▾]  Source [all ▾ | server | node:Alice | node:Bob]  [search______]   │
│ [⏸ Pause] [⤓ Download today] [Clear view]                  live · 1,204 lines        │
├─────────────────────────────────────────────────────────────────────────────────────┤
│ 21:04:12.331 INF Session   Player Bob joined (conn 17, 192.168.1.23)                │
│ 21:04:12.402 WRN Net       conn 12 realtime lane dropped 3 frames (queue 270 KiB)   │
│ 21:04:13.010 INF node:Alice [x4mp] save uploaded save_017 sha=4be1…                 │
│ 21:04:15.556 ERR Protocol  conn 19 frame too large (2.1 MiB > 1 MiB) → closed       │
│   ▸ (click row: properties + exception)                                             │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

Filters are applied on the **server** (via the `SubscribeLogs(filter)` argument) so that
the push volume stays small. The view is virtualized (it renders only visible rows, with
a 5k-row client cap).

### 5.9 Settings

```
┌ Settings ─────────────────────────────────────────────────────────────────────────────┐
│ [Session] [Replication] [Gameplay] [Logging] [Retention] [Network*] [Admin & Security] │
│ Session                                                                               │
│   Server name            [My Server_____________]                                     │
│   Message of the day     [Welcome! Be nice.______]                                    │
│   Join password          [••••••••] [change]                                          │
│   Max players            [ 8 ]                                                        │
│   Reconnect grace (s)    [ 60 ]          Autosave every (min) [ 15 ]                   │
│ Replication (live)                                                                    │
│   Tick rate (Hz) [20]  Bandwidth/client (KB/s) [256]  Near radius (m) [15000]         │
│   Prefetch depth [1]   Linger (s) [20]                                                │
│ * Network: restart required (read-only here; edit appsettings.json)                   │
│ Admin & Security: change my password · API tokens [+ New] · admin users · audit log   │
│                                              [Discard]  [Save changes (3)]            │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

The page is generated from `/settings/schema`. Validation errors from `PATCH` are shown
inline per field.

### 5.10 Diagnostics

```
┌ Diagnostics ──────────────────────────────────────────────────────────────────────────┐
│ Server: tick p50 2.1 / p99 6.2 ms · encode p99 1.8 ms · mirror 18,204 ents · GC gen2 3 │
│ ┌conn┬player┬transport┬ RTT  ┬ ctrl q ┬ rt q    ┬ bulk q ┬ out KB/s┬ drops┬ flush99┐ │
│ │ 12 │Alice │ TCP     │ 2ms  │ 0      │ 1 / 9K  │ 0      │   35    │ 0    │ 0.3ms │ │
│ │ 17 │Bob   │ TCP+UDP │ 31ms │ 0      │ 0       │ 0      │  148    │ 0    │ 0.4ms │ │
│ │ 19 │Carol │ TCP     │ 44ms │ 0      │ 0       │ 8 win  │ 12,700  │ 0    │ 2.1ms │ │
│ └────┴──────┴─────────┴──────┴────────┴─────────┴────────┴─────────┴──────┴───────┘ │
│ Selected conn 17 (Bob)                                [☐ trace 1/100 frames to log] │
│ ┌Message rates──────────────────────────────┐ ┌Queue depth / drops (10 min)───────┐ │
│ │ type            dir  /s     KB/s          │ │ uPlot                              │ │
│ │ Replication     out  20.0   141.2         │ │                                    │ │
│ │ PlayerState     in   20.0     1.1         │ └────────────────────────────────────┘ │
│ │ Ping/Pong       both  1.0     0.0         │                                       │
│ │ Chat            in    0.0     0.0         │  violations: 0 · coalesced: 112        │
│ └───────────────────────────────────────────┘                                       │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 6. FakeNode simulator (`tools/X4MP.FakeNode`)

Purpose: develop and test the server and GUI end-to-end **without X4**, including
correctness checks, load and failure injection. It is used as an exe and as a library
(integration and load tests reference it directly).

### 6.1 CLI

```
x4mp-fakenode authority --server 127.0.0.1:47780 [--seed 42] [--clusters 30] [--sectors-per-cluster 1-3]
                        [--ships-per-sector 50-800] [--stations-per-sector 2-15] [--tick 20]
                        [--save-size 64MB] [--events-per-min 6] [--fps 60] [--name FakeAuthority]
x4mp-fakenode client    --server ... --name Bob [--count 8 --name-prefix Bot] [--behavior wander|patrol|explore]
                        [--udp] [--verify] [--slow-reader 32KBps] [--latency 80ms±20] [--loss 1%]
                        [--disconnect-every 120s] [--chat-every 30s]
                        [--team <name>|--team-pick lobby-random] [--commander shared|own|foreign]
                        [--economy idle|casual|heavy] [--dupe-attack] [--loan-default 20%]
x4mp-fakenode swarm     --server ... --clients 16 [all authority/client options]   # one process
                        [--teams 3 --relations ffa|allied|coop|versus]            # creates teams via admin API token
x4mp-fakenode authority ... [--trade-fail 10%] [--trade-timeout 5%] [--income-rate 5000/min]
x4mp-fakenode fuzz      --server ... [--seconds 60]           # malformed/oversized/out-of-role frames
x4mp-fakenode inspect   --server ... --sector <id>            # observer role; prints received entities
```

`--inproc` replaces `--server` in tests: it starts the server in the same process via the
InProc transport.

### 6.2 Fake universe (deterministic)

- `FakeGalaxy.Generate(seed)`: clusters on an axial hex grid. Sectors get plausible names
  from syllable tables, and a few real-sounding ones for familiarity. Gate links are a
  random spanning tree plus about 20% extra edges, with highways between some neighbours.
  The result is emitted as `GalaxyMetadata`.
- `FakeWorld`: a **deterministic, tick-based simulation** that is a pure function of
  `(seed, tick)` for NPCs.
  - Ships follow waypoint loops inside a sector at class-dependent speeds (S 300 m/s,
    M 200, L 120, XL 80). Each ship has a seed-derived chance per minute to fly to a gate
    and jump (it changes sector and enters at the paired gate). Stations are static.
  - Positions are computed in closed form from (seed, tick) per ship segment, so any tick
    can be evaluated without stepping history.
- The authority behaves the way the real mod should:
  - Sends `ClientHello{requested_roles=Authority}`.
  - Uploads a fake save if asked. The save is a generated gzip of `<savegame><info .../>`
    plus random padding of `--save-size`, and its sha is stable per seed.
  - Sends `GalaxyMetadata`.
  - Honors `CaptureSet`: it only streams requested sectors, each at its requested rate.
  - Sends `GalaxySummary`, `NodeStats` (FPS jitter around `--fps`), random game
    events (kills remove a ship from the sim, trades, captures change faction), and log
    lines.
  - Answers `RequestSave` (with `SaveStarted` + manifest) and applies client `Intent`s.
- Fake clients:
  - Send `ClientHello{requested_roles=Client}`, download the save (in-band, or HTTP if `--http-save`) and
    verify the sha, then report `NodeReady`.
  - Fly their own player ship (`wander` = random waypoints, `patrol` = a loop through
    3 sectors, `explore` = a random walk over gates, which stresses interest changes) and
    send `PlayerState` at 20 Hz.
  - Chat, and send occasional `Intent{KillClaim}` on nearby entities.
- **Teams** (2.13):
  - The fake authority honours `TeamTable` and `TeamRelations`. It maps slots to fake
    factions, gives each team a few owned ships (owner player = a member) and reports
    `EntityChange{OwnerTeam|OwnerPlayer}`.
  - It applies `ReassignPlayerAssets`. NPCs attack Hostile-team ships but not Allied ones,
    so the sim emits kill events consistent with the matrix.
  - Clients answer the lobby with `--team` / `--team-pick`, or sit in `AwaitingTeam`
    until an admin assigns them.
  - `--commander` makes a client send `Intent{AssetOrder}`s to its own assets, team assets or
    foreign assets, to exercise `AssetPermissionPolicy`. Foreign orders must be rejected
    100% of the time.
- **Economy** (2.14):
  - The fake authority emits `CreditDelta` income and spend for each player
    (`--income-rate`, seeded).
  - It executes `AssetTransferOrder` idempotently by trade id: it moves fake cargo or changes
    ship ownership. With `--trade-fail` it returns failures, and with `--trade-timeout` it
    withholds replies, which exercises the InDoubt path and `TradeQuery`.
  - Clients with `--economy` make random donations, pool deposits and withdrawals, loan
    offers, acceptances and repayments (some never repaid, `--loan-default`, to create
    overdue loans), and trade proposals and acceptances, all within the configured scopes.
  - `--dupe-attack` replays requests with the same `requestId`, sends concurrent
    double-spends from two connections of the same player (reconnect race), reuses a
    requestId with a different payload, and tries trades on a locked ship.

### 6.3 End-to-end verification (`--verify`)

Because NPC motion is a pure function of `(seed, tick)`, a verifying client can compute
the **ground truth** for any `authorityTick` it receives:
- **Position check**: for every replicated entity, the error between the received
  (dequantized) state and the truth at that tick must be within the quantization epsilon.
  This catches delta-encoding bugs.
- **Interest check**: every entity in its T0/T1 truth set must be present within
  `1 s + 2 / rate` after entering interest. No entity outside interest plus linger may
  remain after its despawn deadline.
- **Event check**: kills and captures must arrive and must be consistent with the sim.
- **Checksum check**: compares `InterestChecksum` and exercises the `Resync` path.
- **Economy check**: each client keeps its own expected balance from the
  `EconomyResult`/`WalletUpdate` stream. At the end, the swarm controller calls
  `POST /economy/audit` and `GET /economy/wallets`, and asserts:
  - the conservation invariant holds;
  - no duplicate effects from replays happened (each requestId has exactly one ledger
    transaction);
  - escrow is zero for terminal offers;
  - every rejected cross-scope or foreign-asset action was rejected.
- Results go to stdout as JSON lines plus a final summary. The process exit code is
  non-zero on violations, which lets CI use it.

Note: events change the sim (killed ships disappear). Verifiers learn these from the
authoritative event stream. Since the sim is otherwise pure, they can apply them
deterministically.

### 6.4 Failure injection

- `--latency/--loss`: a delay or drop layer in the client's send/receive path. Loss
  applies to the UDP path only.
- `--slow-reader N`: the client reads from its socket at N bytes/s. This must trigger
  realtime drops, and eventually a `SlowConsumer` close if Control backs up, without
  affecting other clients.
- `--disconnect-every`: an abrupt socket close followed by a reconnect with the resume
  token, which must get a keyframe.
- `fuzz`: random frame types, truncated headers, oversized lengths, role-violating messages
  (a client sending `WorldUpdate`) and handshake floods. The server must stay up and count
  violations.

---

## 7. Testing strategy, CI, security

### 7.1 Test pyramid

| Layer | Project | What | Runs |
|---|---|---|---|
| Unit | `X4MP.Protocol.Tests` | codec round-trip for every message, **golden vectors** from `protocol/testvectors` (the C++ mod uses the same vectors), framing edge cases (split across reads, max size, zero length), property-based random messages | every PR |
| Unit | `X4MP.Core.Tests` | `SendQueue` (priority, coalescing, watermarks, overflow → close, never blocks); `InterestManager` (tiers, prefetch, linger, capture-set union); replication (priority accumulator, budget, baseline-advance-only-on-accept, keyframe after reset); session and node state machines (table-driven transitions); `MessagePolicy` completeness; ban matching (CIDR, expiry); settings validation; **teams**: join modes, presets, relation matrix symmetry, `AssetPermissionPolicy` full policy × relation × action table; **economy**: double-entry Σ=0 for every TxKind, scope × relation table, idempotent replay, loan and trade state machines (all transitions incl. expiry/overdue on a fake clock), mode migration arithmetic (remainders), reversal rules, auditor detects injected corruption | every PR |
| Integration | `X4MP.Server.Tests` | `WebApplicationFactory<Program>` with ephemeral ports and a temp data dir; **FakeNode authority + 3 clients over real TCP**: join → save download → InGame → replication verified → kick → ban → reconnect rejected. Admin REST (auth, roles, CSRF header, ProblemDetails), SignalR topics receive pushes, settings PATCH hot-reloads, migrations on empty and previous-version DBs, save upload resume and hash mismatch, InProc transport parity test (same scenario as TCP) | every PR (< 3 min) |
| Contract | `TsContractGenerator` | generated TS DTOs must match the committed file | every PR |
| Frontend unit | Vitest + Testing Library | formatters, uploader resume logic, sha256 against known vectors, hub topic ref-counting, Settings form from schema | every PR |
| E2E | Playwright | published server + `fakenode swarm --clients 4 --verify` → login (initial password from file), dashboard shows 5 nodes, map shows players, kick removes the player, chat round-trip, upload a save, change a setting, apply the Free-for-all preset and drag a player between teams, reverse a donation and freeze a wallet in Economy | every PR (Linux, about 5 min) |
| Property | `X4MP.Core.Tests` (no FsCheck: seeded random generator) | 100k random economy operations (donate, loan, trade, reversal, mode switch, team move) followed by the auditor: the invariant always holds, and no non-World balance goes negative without `force` or game spend | every PR (bounded to 20 s) |
| Load | `X4MP.LoadTests` | 16 clients / 20k entities for 10 min; gates from 2.12; BenchmarkDotNet micro-benchmarks for ingest and replication encode | nightly + manual |
| Soak | FakeNode swarm, 4 h with `--disconnect-every` and the slow reader | memory growth < 5%, no handle leaks | weekly / pre-release |

Conventions: the tests own `TimeProvider` (built into .NET 8+). `SessionActor`, timers
and grace periods use an injected `TimeProvider`, so tests use a fake clock and run state
machines without sleeping.

### 7.2 CI (GitHub Actions)

`.github/workflows/ci.yml` (on PR and push to main):

```yaml
jobs:
  web:
    runs-on: ubuntu-latest
    steps: [checkout, setup-node@v4 (20, cache npm, server/web/package-lock.json),
            npm ci, npm run lint, npm run typecheck, npm run test -- --run, npm run build,
            upload-artifact web-dist (server/web/dist)]
  dotnet:
    needs: web
    strategy: { matrix: { os: [windows-latest, ubuntu-latest] } }
    steps: [checkout, setup-dotnet@v4 (global.json), download-artifact web-dist -> server/web/dist,
            dotnet restore --locked-mode, dotnet format --verify-no-changes,
            dotnet build -c Release -p:SkipWebBuild=true -warnaserror,
            dotnet test -c Release --no-build --collect:"XPlat Code Coverage" --logger trx,
            git diff --exit-code server/web/src/api/generated.ts,
            upload test results + coverage]
  e2e:
    needs: [web, dotnet]
    runs-on: ubuntu-latest
    steps: [publish server linux-x64 (SkipWebBuild, web-dist artifact), publish fakenode,
            start server (data in $RUNNER_TEMP) & swarm --verify, npx playwright install --with-deps chromium,
            npx playwright test, upload playwright-report on failure, upload server logs always]
```

`release.yml` (on tag `v*`): publish `x4mp-server` and `x4mp-fakenode` for `win-x64` and
`linux-x64` as single-file. The zip contains the exe, `appsettings.json` and `README-server.md`.
It generates `SHA256SUMS`, creates a GitHub Release, and attaches an SBOM
(`dotnet CycloneDX` as a tool step, not a project dependency).

Other CI pieces:
- `nightly.yml` runs the load tests on `ubuntu-latest` and posts the numbers as a job summary.
- Dependabot for nuget, npm and github-actions (weekly).
- `CodeQL` for C# and JS.
- Lock files: `packages.lock.json` (`RestorePackagesWithLockFile=true`) and `package-lock.json`.

### 7.3 Security notes

Threat model: LAN party or friends over a VPN (Tailscale, ZeroTier). This is not an
internet-facing service. We harden it anyway, because a port-forward will happen.

- **Game protocol**:
  - Frame length is validated against `MaxFrameBytes` before allocating.
  - The handshake must complete within 10 s.
  - `MaxConnectionsPerIp` (default 4) and a global handshake rate limit (token bucket,
    20/s) apply.
  - Unknown message types count as violations. After 20 violations per minute the
    connection is closed and the IP is temp-banned for 5 min.
  - Role and ownership enforcement is described in 2.4. Strings are length-capped (names
    3 to 24 chars, `[\p{L}\p{N} _\-.]`, chat 256 chars, with control chars stripped).
  - Numeric fields are validated (no NaN or infinity positions, since a NaN from one
    client must not poison others).
- **Secrets**: the join password and player keys are only ever stored hashed. Passwords
  never cross the wire: HMAC challenge-response over the `ServerHello` nonce (protocol.md
  §4.3). Optional TLS (`SslStream`, `tls` capability) is deferred.
- **Admin**: see 4.2 and 4.3. That covers PBKDF2, the strict cookie, the CSRF header, the
  login rate limit, private-network default allow-list, CSP, and the audit log for every
  mutating action. Logs never contain passwords, tokens, or the Authorization or Cookie
  headers (a Serilog destructuring policy redacts them).
- **Economy and teams**:
  - The actor derives identity from the connection and never from payload fields.
  - All amounts are checked 64-bit integers, so there are no floats.
  - Replays are stopped by `requestId` plus a payload hash.
  - Double-spend is impossible because the single-writer actor validates and commits
    synchronously before acking.
  - Escrow is used before any asset movement, and one active trade per entity is
    enforced by a unique index.
  - The authority's trade execution is idempotent by trade id.
  - The ledger is append-only (DB triggers).
  - A periodic conservation audit freezes the economy on a breach.
  - Every admin money action is audited with a reason.
  - A compromised **authority** can still mint credits via `CreditDelta`. That is
    inherent to the topology. Mitigations: per-minute income anomaly alerts
    (`Economy.IncomeAlertPerMinute`) and admin reversal.
- **Files**: saves are content-addressed. Uploads are size-capped and type-sniffed. The
  server never decompresses more than 256 KB for metadata, which avoids a zip bomb. It
  never executes or parses anything else from saves. Downloads require tokens bound to a
  player.
- **Supply chain**: few dependencies, lock files, Dependabot, CodeQL, and release checksums.
  Self-contained publish means no machine-wide runtime is needed.
- **Windows service** runs as `NT SERVICE\X4MP` (a virtual account), not LocalSystem.
  `service install` sets that account and grants it the data dir ACL.

---

## 8. M0 and M1 implementation task list

> Superseded by `docs/roadmap.md` (merged, renumbered, dependency-annotated). Kept for
> detail; where names differ, use the canonical names (ADR-027). M0-8 below is replaced
> by roadmap M0-02..M0-07 (FlatBuffers codegen).

Each task is sized at 0.5 to 2 days for one developer. **AC** = acceptance criteria.
Dependencies are in brackets.

### M0: Skeleton, build, CI, protocol v0 (server side)

| # | Task | AC |
|---|---|---|
| M0-1 | Repo scaffolding: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `X4MP.sln`, all projects from 1.1 (empty), lock files, `.gitignore`. | `dotnet build` and `dotnet test` succeed on Windows and Linux with zero warnings. The project-reference direction test passes. |
| M0-2 | Web scaffold: Vite + React + TS app with the Login page and layout shell, ESLint, Vitest, Playwright config, and the dev proxy. | `npm run lint/typecheck/test/build` all pass. `dist/` is produced. |
| M0-3 | Embed SPA into Server: `BuildWeb` MSBuild target, `ManifestEmbeddedFileProvider`, SPA fallback, cache headers, `/healthz`. [M0-1, M0-2] | `dotnet run` serves the SPA at `http://localhost:47790/` from the embedded resources (`web/dist` deleted after the build to prove it). A deep link like `/players` returns `index.html`. |
| M0-4 | Single-file publish profiles for win-x64 and linux-x64, plus CLI verbs `run` and `version`. [M0-3] | `dotnet publish` produces one exe (plus nothing else required). Running it from an empty folder creates `./data` and serves the GUI. `version` prints the version and build hash. |
| M0-5 | Windows service support: `AddWindowsService`, data dir resolution, `service install/uninstall`. [M0-4] | On a Windows VM, `service install` (elevated) then `sc start X4MP` serves the GUI. Data lands in `%ProgramData%\X4MP`. Stop is graceful (log line "shutdown complete"). Uninstall removes it. |
| M0-6 | Serilog setup: console, rolling file, RingBufferSink, request logging, and the redaction policy. [M0-1] | Logs appear in the console and `data/logs`. A unit test confirms the ring buffer caps at N entries and that the redaction policy masks `Authorization`. |
| M0-7 | Persistence foundation: connection factory, WAL pragmas, migration runner, `0001_init.sql` (2.8), and `PersistenceWriter` with batching. [M0-1] | Starting against an empty dir creates the DB at version 1. A restart is idempotent. The test "migrate from v0 empty" passes. Writer batches 1,000 inserts in under 1 transaction-second. |
| M0-8 | **Protocol v0 in C#** (follow protocol.md): frame header, `IFrameCodec`, message types needed for M1 (Hello, Welcome/Reject, Ping/Pong, SessionInfo, SessionSaveInfo, Save* chunks, NodeReady, GalaxyMetadata, CaptureSet, WorldUpdate, GalaxySummary, PlayerState, Replication, Despawn, Chat, GameEvent, Act*, NodeStats, LogLines, Kick, SessionSettings, InterestChecksum, Resync). [protocol.md] | Round-trip tests for every message. Golden vectors are checked in under `protocol/testvectors` and pass. Malformed-input tests throw `ProtocolViolation` and never `IndexOutOfRange`. |
| M0-9 | CI workflows (`ci.yml` web, dotnet matrix, e2e placeholder that only runs a smoke test that the published exe serves `/healthz`), Dependabot, CodeQL. [M0-1..4] | A PR shows green checks on both OSes. Breaking formatting or a test fails CI. |
| M0-10 | `TsContractGenerator` and the `generated.ts` diff check. [M0-1] | Adding a DTO property without regenerating fails CI. |

**M0 exit**: a fresh clone builds, tests and publishes a single exe that serves an empty
GUI shell. CI is green, and the protocol v0 codec is in place with vectors.

### M1: Server with handshake, sessions, relay, admin API and GUI, all driven by fake nodes

**Networking and core**

| # | Task | AC |
|---|---|---|
| M1-1 | `INodeListener`/`INodeConnection` and `SendQueue` (three lanes, coalescing, watermarks, pull hint, overflow close) plus the writer loop. | Unit tests cover priority order, coalescing replace, `DroppedLane` above the high watermark, `SlowConsumer` close at the Control hard cap or timeout. A benchmark shows `TrySend` does not allocate and is < 200 ns. **A test with a non-reading peer proves producers never block.** |
| M1-2 | TCP transport via Kestrel `ConnectionHandler`, plus the InProc transport. [M1-1, M0-8] | An echo integration test over both. 1,000 connect/disconnect cycles leak no handles (counted). |
| M1-3 | `NodeGateway`: handshake, version negotiation, join password, identity (name ↔ key hash), ban check, per-IP limits, handshake timeout. [M1-2, M0-7] | Integration tests: success, wrong password, version mismatch, name taken, banned player, banned CIDR, 11 s silent connection closed, fifth connection from an IP refused. |
| M1-4 | `MessagePolicy` role and ownership matrix, plus violation counting and temp-ban. [M1-3] | Every message type has an entry (reflection test). A client sending `WorldUpdate` gets a violation and is closed after the threshold. The fuzz run from M1-14 survives. |
| M1-5 | `SessionActor` with the state machine (2.4), ping/RTT, telemetry ingest, reconnect grace with resume token, `TimeProvider`. [M1-3] | Table-driven transition tests with a fake clock. With real TCP, an authority drop leads to `AuthorityLost` then `Running` on reconnect within grace, and `Ended` after grace. A client resume inside grace keeps the player row and receives a keyframe. |
| M1-6 | Entity registry mirror plus GalaxyMetadata cache plus sector graph. [M1-5] | Ingesting 20k entities at 20 Hz has zero steady-state allocations (benchmark). A sector change updates the index. Galaxy metadata is persisted per save sha and reloaded on restart. |
| M1-7 | Interest manager (tiers, prefetch, linger, near grid) and `CaptureSet` emission. [M1-6] | Unit tests: crossing a gate yields the correct spawn/despawn sets. Linger keeps the old sector for N s. The capture set is the union of all clients plus admin subscriptions, debounced to 500 ms. |
| M1-8 | Replication: priority accumulator, budget, delta vs baseline, keyframe on reset, `InterestChecksum`. [M1-7, M1-1] | `--verify` FakeNode clients report 0 position errors over 5 min. With the budget lowered to 32 KB/s, the near entities still update at ≥ 10 Hz. Dropped realtime frames cause no verify errors. |
| M1-9 | Pass-through relay: PlayerState to others and the authority, chat (mute-aware), Act* to the authority, authoritative events fanned out with interest filtering, all persisted. [M1-5] | Integration: client A chat reaches B and C but not after A is muted. An `ActKill` reaches only the authority. The authority's `GameEvent` reaches clients with interest and lands in `session_events`. |
| M1-10 | Event bus and subscribers (persistence, audit, alert evaluator). [M1-5, M0-7] | Publishing never blocks, even with a stalled subscriber (test). Alerts fire for an authority FPS < 15 for 30 s (fake clock). |
| M1-11 | Save service: content-addressed store, in-band upload with `ghostsCleaned` gating, in-band windowed download with resume, HTTP Range download with tokens, metadata sniffing, janitor. [M1-5, M0-7] | A 200 MB fake save goes from authority to server to 3 clients, with sha verified. Killing a client at 50% and reconnecting resumes from its offset (bytes transferred < 60% of the total on the second attempt). Hash mismatch is rejected. A non-gzip file gets 422. A `ghostsCleaned=false` save is not made current. During a transfer, the other clients' realtime p99 latency stays within +10 ms. |
| M1-12 | Metrics: `Meter` instruments, `ConnectionStats`, `MetricsSampler` ring buffers. [M1-1] | `dotnet-counters monitor X4MP.Server` shows the instruments. `/diagnostics/metrics` returns 600-sample series. |
| M1-13 | Configuration: options classes with `[Setting]`, the SQLite overrides provider with reload, schema endpoint, and `SessionSettings` push to nodes. [M0-7] | A PATCH of `Replication.TickRateHz` takes effect in under 1 s without a restart (verified by the measured tick rate). An invalid value gives a 400 with a per-key error. A boot-only key is rejected with "restart required". The change is audited. |
| M1-14 | **FakeNode**: galaxy generator, deterministic world, authority, clients, behaviors, `--verify`, swarm, slow-reader, latency/loss, disconnect, fuzz, inspect. Also team behaviours (`--team`, `--team-pick`, `--commander`, faction-aware fake NPC hostility) and economy behaviours (`--economy`, `--dupe-attack`, `--trade-fail`, `--trade-timeout`, `--income-rate`, `--loan-default`) as in 6.2. [M0-8; grows alongside M1-3..11, M1-T*, M1-E*] | `x4mp-fakenode swarm --clients 8 --verify` runs for 10 min against the server with no violations. `fuzz` for 60 s leaves the server up with violations counted. `--slow-reader` triggers drops only for that client. `swarm --teams 3 --relations ffa --economy heavy --dupe-attack --trade-fail 10% --trade-timeout 5% --verify` runs for 10 min with: 0 economy invariant violations, 0 duplicate effects, 100% of foreign-asset orders rejected, and every InDoubt trade either resolved via `TradeStatusQuery` or listed for admin resolution. |

**Teams (2.13)**

| # | Task | AC |
|---|---|---|
| M1-T1 | Team domain: `Team`, membership, `TeamRelationMatrix`, presets, faction-slot allocator, persistence (`teams`, `team_members`, `team_relations`, `team_assets`), and the `TeamOptions` settings. [M1-5, M0-7] | Unit tests: presets produce the documented tables. The matrix is symmetric and versioned. Slot exhaustion gives `NoFactionSlot`. Memberships survive a server restart (sticky). |
| M1-T2 | Join-time assignment: the `AwaitingTeam` node state, Auto (SingleTeam/Balance/NewTeamPerPlayer), Lobby (`TeamList`, `TeamChoice`, locked/full/password, timeout fallback), AdminAssign, and the authority-must-have-a-team gate. [M1-T1, M1-3] | Integration with FakeNode: in each mode, 6 clients end up in the expected teams. In Lobby, a wrong team password is rejected and a locked team is refused. In AdminAssign, a client receives no save or replication until assigned. A reconnecting client keeps its team without passing through `AwaitingTeam`. |
| M1-T3 | Team protocol fan-out: `TeamTable`, `TeamRelations`, `TeamPolicy`, `TeamMemberChanged` to all nodes, and `ReassignPlayerAssets` to the authority on a mid-session move, plus a `Resync` for the moved client. [M1-T1, M1-9] | A relation change reaches every FakeNode within 1 s, and the fake authority's NPC hostility changes accordingly (kill events follow). Moving the authority's player while Running gives 409. |
| M1-T4 | `AssetPermissionPolicy`: mirror `OwnerTeamId`/`OwnerPlayerId`, ownership events, gating of `ActOrder`/`ActBuild`/`ActTrade`/`ActTransferAsset`, `ActRejected`, and `PermissionDenied` events. [M1-T1, M1-6, M1-4] | The table-driven test covers every policy × relation × action cell. FakeNode `--commander foreign` gets 100% rejections with nothing forwarded to the authority (counter). `--commander shared` under `OwnerOnly` is rejected for a teammate's ship but allowed for the leader under `OwnerAndLeader`. |
| M1-T5 | Teams REST and hub (4.4, 4.6) plus the **Teams & Factions page** (5.4a): team cards, drag-and-drop with keyboard alternative, relation grid, presets with confirm, join mode, unassigned lobby badge. [M1-T1..T4, M1-18] | Playwright: apply "Free-for-all" with 4 FakeNode clients and see 4 teams, all Hostile. Drag Bot2 onto Bot1's team and see the membership change pushed to the fake client within 1 s. Set a cell to Allied, apply, and see `TeamRelationsChanged` received. In AdminAssign mode a new bot appears under Unassigned, and dragging it to a team lets it download the save. |

**Economy (2.14)**

| # | Task | AC |
|---|---|---|
| M1-E1 | Ledger core: wallets (all kinds), double-entry `LedgerTransaction`, synchronous commit path, append-only triggers, `economy_requests` idempotency, the `EconomyAuditor` and economy freeze. [M0-7, M1-5] | Property test (100k operations) keeps the invariant. Killing the process between ack and the next operation loses no acked transaction (kill -9 test). A replayed requestId returns the identical result and creates no second transaction. A requestId reused with a different payload is rejected. Injected DB corruption is detected by the auditor, which freezes the economy. |
| M1-E2 | Credit modes: Auto/PerPlayer/Shared resolution, team pool, pool withdraw policy and daily limit, migrations on a mode switch or team move, `CreditDelta` booking (game income/spend, overdraft flag), `StartingCredits`. [M1-E1, M1-T1] | Applying the "Everyone co-op" preset in Auto gives one shared wallet holding the sum of prior balances, and switching back to 2 teams splits it with the remainder to the pool (exact arithmetic test). Changing the mode while Running without `confirm` gives 409 with a preview. An overdrawn wallet blocks outgoing player actions. |
| M1-E3 | Donate and pool actions with scopes (Off/Teammates/Allied/Anyone), rate limits and validation rules 1 to 6. [M1-E2, M1-T3] | A scope × relation table test. A FakeNode donation across Hostile teams with `DonateScope=Allied` is rejected with `ScopeDenied`. Changing the relation to Allied makes it succeed. The sixth request in 10 s gets `RateLimited`. |
| M1-E4 | Loans: offer with escrow, accept, decline, withdraw, expiry, repay, auto-repay from income, real-time and game-time due dates, overdue timer, admin forgive and cancel (with optional reversal), limits. [M1-E1, M1-E3] | State-machine tests cover every transition on a fake clock. Auto-repay of 25% on a 1,000 income repays exactly 250. A game-time due date does not advance while the session is paused. Overdue appears in the GUI and hub within 10 s of the deadline. |
| M1-E5 | Trades: propose and accept with escrow, asset locks (unique index), proximity check, `TradeExecute`/`TradeExecuted`, timeout to `TradeStatusQuery` to InDoubt, admin resolve or cancel, refund on failure, ownership update on success, settle-once guarantee. [M1-E1, M1-T4] | With `--trade-fail 10%`, every failed trade is refunded and the asset unlocked. With `--trade-timeout 5%`, trades go InDoubt and are resolvable from the GUI. Duplicate `TradeExecuted` messages settle once. A second trade on a locked ship is rejected. A completed ship trade changes `OwnerPlayerId` in the mirror and `team_assets`. |
| M1-E6 | Economy admin REST and hub (4.4, 4.6): wallets, ledger query and CSV, adjust, freeze, reverse (with `WouldOverdraw`, `AlreadyReversed`, `force`, `returnAsset`), loans, trades, summary, events, policy. Every admin action audited. [M1-E1..E5, M1-15] | At least one happy-path and one error-path test per endpoint. A reversal links both transactions, and a second reversal gives 409. A frozen wallet rejects FakeNode donations in both directions while game `CreditDelta` still books. `audit_log` has a row with a reason for every admin money action. |
| M1-E7 | **Economy page** (5.4b): overview tiles, wallets, live transactions with filters, loans, trades, policy tab with migration preview, detail drawers, confirm flows, navigation badges, auditor banner. [M1-E6, M1-18] | Playwright with `--economy casual`: transactions stream live. Reversing a donation from the GUI restores both balances. Freezing Bot3 makes its next donation fail (FakeNode logs `WalletFrozen`). An overdue loan shows a badge. Resolving an InDoubt trade as "refund" restores the payer's balance. |

**Admin API and GUI**

| # | Task | AC |
|---|---|---|
| M1-15 | Auth: admin user bootstrap (initial password file), login/logout/me/change-password, cookie, CSRF header, bearer tokens, roles, login rate limit, private-network allow-list, security headers. [M0-7] | Integration: unauthenticated requests get 401. A Viewer kick gets 403. A POST without `X-X4MP` gets 400. The sixth login per minute gets 429. The first login forces a password change. A request from a public IP gets 403 by default. |
| M1-16 | REST endpoints from 4.4 (excluding the 501 stubs and the team and economy groups, which are in M1-T5 and M1-E6) and DTOs, with ProblemDetails. [M1-5..13, M1-15] | Each endpoint has at least one happy-path and one error-path integration test. Kick or ban of an online FakeNode client disconnects it within 1 s, and a ban blocks reconnect. |
| M1-17 | `AdminHub` with topics and `AdminBroadcaster` (dashboard 1 Hz, galaxy 1 Hz, sector 4 Hz with admin interest, logs batched and filtered, diagnostics, chat, transfers, alerts). [M1-16] | A SignalR client test receives each push type. Groups with no members cost nothing (the broadcaster skips them, verified with a counter). A sector subscription makes the FakeNode authority start streaming that sector. |
| M1-18 | GUI shell: auth gate, `HubProvider`, layout, alerts toast, theme. [M0-2, M1-17] | Login, forced password change and logout work. After a server restart the hub auto-reconnects and the status indicator shows the state. |
| M1-19 | Dashboard page. [M1-18] | With a FakeNode swarm, the tiles and player table update live. The sparklines show 10 min of history after a reload, backfilled from `/diagnostics/metrics`. |
| M1-20 | Players page and detail (kick, mute, ban, unban, notes, release name). [M1-18] | A Playwright test: kick Bot3, the row shows disconnected, and the FakeNode logs `Kicked`. Banning Bot4 with an IP blocks reconnect. Unban allows it. |
| M1-21 | Map: galaxy canvas (sectors, links, players, interest overlay, heat) and sector canvas (4 Hz, interpolation, hover, filters, cap). [M1-18] | A Playwright test: player markers move between frames, clicking a sector opens the sector view with entities > 0, and the interest overlay changes when a FakeNode client jumps. Rendering 3,000 entities stays ≥ 50 FPS in Chromium (measured on a CI-sized machine, with a reported, non-fatal threshold). |
| M1-22 | Sessions & Saves page (create, start, stop, request save, resumable upload with sha, list/rename/pin/delete, transfer progress, history). [M1-18, M1-11] | Playwright: upload a 50 MB fake save, reload the page at 50%, and the upload resumes and completes with sha verified. Starting a session with it makes FakeNode clients download that save. |
| M1-23 | Chat page. [M1-18] | Admin to everyone reaches all FakeNode clients (they log it). Client chat appears live. History loads on open. |
| M1-24 | Logs page (live tail with server-side filter, pause, download). [M1-18] | Filtering `source=node:Bot1` shows only that node's forwarded lines. Pause stops rendering but keeps buffering (up to the cap). |
| M1-25 | Settings page (schema-driven, per-section save, validation, tokens, audit view). [M1-18, M1-13] | Changing max players to 2 makes the third FakeNode client get rejected with `ServerFull`. A bad value shows an inline error. |
| M1-26 | Diagnostics page (connection table, per-connection message rates, queue charts, trace toggle). [M1-18, M1-12] | With `--slow-reader` on Bot2, its realtime drops and queue depth visibly rise while the others stay at 0. Turning on trace logs sampled frames for that connection. |
| M1-27 | E2E CI job: published server plus `fakenode swarm --verify` (including `--teams 3 --economy casual --dupe-attack`) plus Playwright suite. [M1-19..26, M1-T5, M1-E7] | Green in CI in < 10 min. Artifacts (server log, FakeNode JSON, Playwright report) are uploaded on failure. |
| M1-28 | Load test harness and baseline numbers (2.12), plus a nightly workflow. [M1-8, M1-14] | The nightly job reports tick p99, CPU, memory and bytes/client for 1 authority, 16 clients and 20k entities. It fails if the tick p99 exceeds 15 ms. |
| M1-29 | Docs: `docs/server-admin.md` (install, ports, firewall, service, first login, LAN/VPN advice) and `docs/fakenode.md`. | A new contributor can run the server plus a swarm and see players on the map by following the docs alone, which we test by having a teammate do it. |

**M1 exit criteria** (all observable without X4):
1. One command (`x4mp-server` plus `x4mp-fakenode swarm --clients 8 --verify`) gives a
   running session. The GUI shows 9 nodes with ping, FPS and bandwidth, the map shows live
   positions and interest regions, and verification reports zero errors over 30 minutes.
2. Kick, ban, mute, broadcast, settings changes, save upload and distribution, and session
   start, stop and autosave all work from the GUI and are audited.
3. A slow-reader client and a 60 s fuzz run do not degrade the other clients (tick p99
   within the budget).
4. The CI pipeline (unit, integration, E2E) is green on Windows and Linux. A release tag
   produces single-file binaries.
5. **Teams**: all three join modes and all presets work from the GUI with FakeNode
   clients. Relation changes reach every node within 1 s. Commands on assets of another
   team are always rejected server-side.
6. **Economy**: a 30-minute `swarm --teams 3 --economy heavy --dupe-attack --trade-fail 10%
   --trade-timeout 5%` run ends with the auditor clean, no duplicated effects, and every
   trade either Completed, refunded, or InDoubt and resolvable from the GUI. Ledger view,
   wallet freeze, reversal, loan forgive and cancel, and trade resolve all work from the
   GUI and are audited.

---

## 9. Open questions (for consolidation with other design docs)

> Resolved in `docs/decisions.md`: Q1 → ADR-012; Q2 → ADR-007; Q3 → ADR-028; Q4 → ADR-010;
> Q5 → V09; Q6 → one session in v1; Q7 → ADR-006 (47780/47781/47790); Q8 → ADR-019 + V04;
> Q9 → user question Q1; Q10 → ADR-014 + V01; Q11 → ADR-022 + V13; Q12 → ADR-021.

1. **Who computes deltas?** This design has the server keep the mirror and do per-client
   deltas and interest (2.6). The authority sends full quantized states for captured
   sectors only. protocol.md needs to agree on `WorldUpdate` (full state per entity,
   sector-batched) versus `Replication` (per-client delta) as separate messages, plus an
   ack field for the UDP lane.
2. **Save transfer path for the mod**: in-band chunks (simplest for C++) versus HTTP (needs
   WinHTTP or libcurl in the mod). This design supports both, with in-band as the default.
   The mod design should confirm.
3. **Join auth**: plain password in Hello versus HMAC challenge-response. We recommend
   challenge-response. TLS for the game protocol is deferred.
4. **Entity id stability**: we assume X4 UniverseIDs match across instances loading the
   same save (the reference relied on this). Entities spawned after load (new ships)
   differ per instance. Does the authority id space become canonical, with client mods
   mapping ids? This matters for the ownership rules.
5. **Galaxy map positions**: can the mod read sector and cluster galaxy-map coordinates
   via the X4 API? If not, the map falls back to a force-directed layout of the gate
   graph. x4-api-notes.md should confirm.
6. **One session or many?** M1 implements one active session per server. Is that enough
   long term?
7. **Ports**: 7778/7779/7790 were proposed (now 47780/47781/47790). They conflict with the reference mod (7777/7778)
   if both are installed. Should we move to an unusual range (for example 47780+)?
8. **Credits in game**: X4 exposes no credit read/write API (PLAN.md). Can the mod show
   and enforce server balances? Options: an MD/Lua money cue if one exists, or a HUD-only
   display plus blocking purchases. If neither is possible, how do in-game purchases
   reconcile with the ledger (`CreditDelta` must be detectable)? The server design does
   not depend on the answer, but gameplay value does.
9. **Fog of war between teams**: should Hostile or Neutral teams' assets be filtered out of
   interest unless they are near? Today they replicate like NPCs.
10. **Faction slots**: how many custom `x4mp_team_*` factions can the mod define, and can
    relations and ownership be changed at runtime for them? This sets `MaxFactionSlots`.
    Also, can the per-viewer "my team = player faction" remapping work, or must every
    team (including one's own) be a custom faction?
11. **Wares and ship transfer in game**: what proximity and docking conditions does the
    authority need to move cargo between player ships or change ship ownership atomically?
    This defines `TradeExecuted` failure reasons.
12. **Loan interest model**: flat fee only (M1). Is periodic or compound interest, or an
    overdue penalty, wanted later?
