# FakeNode: the headless X4 stand-in

`tools/X4MP.FakeNode` is a console program that speaks the real node protocol (the same `TcpNodeClient` the game mod's
protocol is tested against) so you can exercise an X4MP server without X4: a fake **authority** that streams a
synthetic galaxy and answers save requests, and fake **clients** that join, fly, chat, trade and check what the server
replicates to them. Use it to try a server, load-test it, inject network faults and reproduce bugs. Design:
[server-design.md](server-design.md) section 6; server setup: [server-admin.md](server-admin.md).

## 1. Quick start

Build once (or use `dotnet run --project tools/X4MP.FakeNode -- <args>` each time), start a server, run a swarm:

```powershell
dotnet build tools/X4MP.FakeNode -c Release -o out/fakenode        # out/fakenode/X4MP.FakeNode.exe

# terminal 1: a server. A swarm from one machine needs more than the default 4 connections per IP and 8 players.
$env:X4MP__Net__MaxConnectionsPerIp = '64'
$env:X4MP__Net__MaxPlayers = '16'
out/win-x64/x4mp-server.exe --data-dir $env:TEMP\x4mp-data              # or: dotnet run --project server/src/X4MP.Server -- --data-dir ...

# terminal 2: 4 verifying clients plus one authority for 60 seconds
out/fakenode/X4MP.FakeNode.exe swarm --clients 4 --with-authority --verify --duration 60
```

Without `--with-authority` the server has no world: clients connect and wait. Exit code 0 means success; see section 6.
A different server address/port: `--server 192.168.1.20:47780`. Running two servers or a second developer's server on the
same machine: give them other ports (`X4MP__Net__NodeTcpEndpoint=0.0.0.0:47980`, `X4MP__Net__UdpPort=47981`, `--port 47990`)
and point FakeNode at it with `--server 127.0.0.1:47980`. For UDP you do not need to tell FakeNode the UDP port: the server
announces it in the handshake.

Players of a swarm are real players to the server: they appear on the GUI Players page, the session starts when the fake
authority has uploaded its first checkpoint, and you can kick, mute or ban them from the GUI. Player keys derive from `--seed`
and the node name, so re-running rejoins as the same players; change `--seed` or `--name-prefix` to appear as new ones (a
name stays bound to the first key that used it, otherwise the server answers `NameTaken`).

## 2. Commands

| Command | What it does |
|---|---|
| `swarm` | `--clients K` clients in one process, plus one authority with `--with-authority`. The normal way to test. |
| `authority` | One fake authority: sends the string table and galaxy metadata, walks to in-game, honours `CaptureSet`, streams `WorldUpdate`, answers `RequestSave` with a fake save and manifest, handles asset transfer orders. |
| `client` | One fake player (`--name`). Joins (downloads the save, verifies the SHA-256, "loads" it), flies, sends `PlayerState` at 20 Hz, receives interest and replication. |
| `inspect` | One client that walks the join path and prints every decoded frame (`--no-join` only listens). |
| `fuzz` | A hostile peer: garbage, truncated, oversized and wrong-phase frames, floods. Checks the server stays up. |
| `galaxy` | Offline: generate the fake galaxy for a `--seed` and print its stats. |
| `help` | Usage. |

Against a server with no session actor (a bare gateway, as in some tests) a node only keeps its connection alive with
Ping/Pong. Options accept `--name value` and `--name=value`. `--verify` and the other boolean flags take no value.

## 3. Options

### Core

| Option | Meaning |
|---|---|
| `--server host:port` | Server node endpoint (default `127.0.0.1:47780`) |
| `--clients K` (`--count`) | swarm: number of fake clients (default 1) |
| `--with-authority` | swarm: also connect one authority |
| `--duration N` (`--seconds`) | Stop after N seconds and print the summary (default: run until Ctrl+C) |
| `--name NAME`, `--name-prefix P` | Single node name (`client`), swarm prefix (default `Bot`, giving `Bot01`...) |
| `--password PW` | The server's join password (`Net.JoinPassword`) |
| `--seed N` | Universe seed (default 42) and player-key derivation |
| `--behavior wander\|patrol\|explore` | How clients fly |
| `--verify` | Check every `Replication` entry against ground truth; exit code 1 on any error (section 5) |
| `--sectors N --ships N --tick HZ --fps N` | Fake universe and authority shape (defaults 152 sectors, 10225 ships, 20 Hz, 60 fps) |
| `--save-mb N` | authority: size of the fake save it uploads on request (default 4, max 4096), so the transfer moves real bytes |
| `--udp` | Use the UDP realtime lane (binds with `UdpHello`; falls back to TCP after 3 s; needs the server's UDP port open) |

### Teams

| Option | Meaning |
|---|---|
| `--team <id\|name>` | client/swarm: join this team (lobby: `TeamChoice`; a missing name is created when `Teams.AllowCreateInLobby`; a node already placed elsewhere asks for a move, needs `Teams.AllowSelfTeamChange`) |
| `--team-pick lobby-random` | Answer the lobby with a random open team |
| `--teams N` | swarm: client i joins `Team (i mod N)+1` (1..8). The authority tags its ships for those teams. Needs `Teams.JoinMode=Lobby` + `AllowCreateInLobby` to place clients |
| `--relations coop\|allied\|ffa\|twoteams` | swarm: a layout; implies `--teams` and prints the server settings it needs (`server-settings=[...]`): start the server with them, for example `--X4MP:Teams:JoinMode=Lobby --X4MP:Teams:AllowCreateInLobby=true --X4MP:Teams:AutoAssign=Balance` |
| `--commander shared\|own\|foreign` | Clients send `AssetOrder`s every 0.5 s: `own` = team-common ships of their team, `shared` = a teammate's (allowed under `AssetPolicy=SharedCommand`), `foreign` = another team's (always rejected) |
| `--team-assets` | authority: give its ships team owners (implied by `--commander`, `--teams`, `--trade`; set it when the authority runs in a separate process) |

### Mods

| Option | What |
|---|---|
| `--extensions FILE` | client/swarm (currently every node, authority included): send the extensions in a JSON file as `ClientHello.extension_list` (and the matching `extensions_hash`). The file is an array of `{id, name, version, source, enabled, workshopId, classHint, contentHash, ...}` (enums by name, `enabled` defaults to true). A mismatch with the session mod policy gets `ExtensionsMismatch` with the exact install/enable/disable/update lists and links. Presets (`--extensions-preset`) come with M1-X5. |

### Economy and trade

| Option | Meaning |
|---|---|
| `--trade` | Clients propose ship-for-credits trades to each other and accept incoming ones (implies `--team-assets`). Needs a trade scope that allows it (`Economy.TradeScope`, `Teams.AllowAssetTransfer`) |
| `--trade-fail PCT` | authority: fail PCT percent of `AssetTransferOrder`s (the server must compensate and unlock) |
| `--trade-timeout PCT` | authority: withhold the confirmation of PCT percent of orders (a third of those never answer a `TradeQuery` either: in doubt) |

### Failure injection

| Option / command | Meaning |
|---|---|
| `--latency MS`, `--jitter MS` | Add MS ms (+-jitter) to every TCP chunk and UDP datagram in each direction (round trip +2*MS) |
| `--loss PCT` | with `--udp`: drop PCT percent of UDP datagrams in each direction |
| `--slow-reader R`, `--slow-clients N` | The first N clients (default 1) read slowly once in game: R is bytes/s (`4096`) or `pause=3/45` (read 3 s, stop 45 s). The server must close only them (`SlowConsumer`); they are left out of `--verify` |
| `--disconnect-every S` | Every S seconds clients drop the socket without a goodbye and resume with the token; the ghost table restarts |
| `--reload-every S` | The same via `Disconnect(ClientReload)`, 0.4 s away, resume and `NodeReady` again |
| `fuzz` | `--duration S --seed N --clients K --fuzz-mode all\|handshake\|session\|flood --local-ip 127.0.0.2 --rotate-ip`. With `--local-ip` the temp ban a fuzzer earns hits only its own address; `--rotate-ip` moves on to the next loopback address after each ban |
| `inspect` | `--filter T1,T2` (message types), `--max-frames N`, `--no-join` |

```powershell
fakenode swarm --clients 4 --with-authority --verify --slow-reader pause=2/120 --duration 75   # poll /api/v1/diagnostics/connections: its queue grows, then it is closed
fakenode swarm --clients 4 --with-authority --verify --udp --disconnect-every 10 --reload-every 15 --duration 60
fakenode swarm --clients 4 --with-authority --verify --latency 100 --jitter 20 --udp --duration 40
fakenode swarm --clients 4 --with-authority --verify --udp --loss 5 --duration 40
fakenode fuzz --duration 60 --clients 3 --seed 7 --local-ip 127.0.0.2 --rotate-ip     # while a swarm runs on 127.0.0.1
fakenode inspect --duration 10 --filter SessionState,RosterUpdate
```

A loopback server buffers megabytes before its send queue notices a stopped reader at the default traffic (about 30 KB/s per
client). To see the `SlowConsumer` close within half a minute give the server more to send:
`X4MP__Interest__MaxGhosts=3000`, `X4MP__Replication__BandwidthBudgetKBps=4000`, and `swarm ... --sectors 6 --ships 6000`.

Team and trade runs, with the server started with the settings `--relations` prints:

```powershell
out/win-x64/x4mp-server.exe --data-dir $env:TEMP\x4mp-data --X4MP:Teams:JoinMode=Lobby --X4MP:Teams:AllowCreateInLobby=true --X4MP:Teams:AutoAssign=Balance
fakenode swarm --clients 6 --with-authority --teams 2 --commander foreign --verify --duration 25
fakenode swarm --clients 6 --with-authority --team-pick lobby-random --verify --duration 15      # on a fresh server
fakenode swarm --clients 4 --with-authority --teams 2 --trade --trade-fail 10 --trade-timeout 5 --duration 40
```

A 200 MB save transfer: `swarm --clients 3 --with-authority --save-mb 200 --duration 60`.

## 4. The join pipeline it plays

Against a server that runs the save service (the `SaveHttp` bit in `ServerHello.server_caps`) the join is the real one:

- **Authority**: sends the string table, walks to in-game, answers `RequestSave` with a deterministic fake save (gzip of an
  X4-style `<savegame><info>` document plus filler) and an `X4MF` manifest of the fake stations, sends `GalaxyMetadata` and
  the `SaveStarted` journal marker, then uploads both files on the Bulk lane (8-chunk window, resume offsets). The session
  starts from that checkpoint.
- **Client**: waits in `SyncingSave` for `SessionSaveInfo`, downloads the save and manifest (resuming from its `.part`
  file), verifies the SHA-256, reports `LoadStatus`/`SaveReady`, "loads" for 50 ms, matches the manifest, takes the string
  table replay and the world catch-up, and sends `NodeReady`.

Fake saves live in a temp directory deleted at the end of the run.

## 5. What `--verify` checks

Ground truth is a pure function of `(seed, game time)`, so a client computes where every ship should be when an entry was
sampled:

- Position, rotation, velocity and sector of every entry, within quantisation tolerance (merged against the client's own
  baseline, so a delta-encoding bug shows as a wrong value).
- Spawn before state: an entry for an id that is neither a ghost nor recently despawned is an error. A partial entry that
  precedes a ghost's first full entry is counted as `stale` and ignored.
- Desync guard: `InterestChecksum{count, xor}` against the ghost set; a mismatch sends `ResyncRequest` (`resyncs=`), one that
  survives three checksums is an error.
- Stale ghosts: a ghost that gets no entry for 45 s is an error.

## 6. Reading the output

One `welcome:` line per node, `in game` lines when a node finished joining, then every 5 s a progress line, and at the end:

```
t=15s connected=4/4 errors=0 pings=60 rtt avg=0.43ms max=2.06ms ingame=4
[Bot01] joined with the save after 0.3s: 4299272 bytes downloaded and verified (ee95f9d17f2c), 528 strings, 0 catch-up entries
[Bot01] verify: ghosts=113 spawns=113 despawns=0 frames=733 entries=25120 checksums=3/3 resyncs=0 errors=0 checked=25120 stale=0 tombstoned=0
verify: clients=3 frames=1504 entries=38890 checked=38890 ghosts=299 spawns=299 despawns=0 checksums-ok=9/9 resyncs=0 position-errors=0 errors=0
udp: nodes=4 bound=4 fallback=0 off=0 datagrams-rx=2872 datagrams-tx=3274 simulated-drops=0 rx-loss-max=0.0%
summary: nodes=4 joined=4 errors=0 pings=80 rtt avg=0.40ms max=2.06ms elapsed=20.0s ingame=4
```

| Line | Reads as |
|---|---|
| `connected=a/b`, `ingame=N` | Nodes connected / expected, nodes that completed the join |
| `verify:` per client and aggregate | `checked` = entries compared with ground truth; `errors`, `position-errors` must be 0; `resyncs` and `stale` are tolerated noise; `checksums-ok=x/y` |
| `udp:` (with `--udp`) | `bound` nodes on UDP, `fallback` fell back to TCP, `off` server had UDP off; `simulated-drops` from `--loss` |
| `latency:`, `slow-reader:`, `resume:` | Failure-injection results: slow readers closed by the server and whether the others had errors; reconnects, refusals and keyframes after each resume (none within 10 s is an error) |
| `teams:` `commander(..):` `npc-hostility:` `reassign:` | Team runs: `placed/unplaced`, `spread=[team:count]`, rejected commands, hostile pairs, asset moves |
| `trade:` and `trade-authority:` | Trade runs: proposals and accepts sent by the clients; orders received, applied, failed, withheld and silent at the authority |
| `summary:` | Final result; `errors=0` and `joined=nodes` is a pass |

Exit code 0: no node failed and verification found nothing. 1: a handshake or connection failed, or verification found an
error (or, for `fuzz`, the server stopped answering). 2: bad command line. 3: feature not available. Ctrl+C ends cleanly
(nodes send `Disconnect(ClientQuit)`).

If every node fails at once with `RateLimited` or resets, check the server limits (`Net.MaxConnectionsPerIp`, `MaxPlayers`)
and see the troubleshooting table in [server-admin.md](server-admin.md).

## 7. Asset owners and hostility (how the fake authority behaves)

With `--team-assets`, ships are tagged for team ids t[0..n-1] with `k = (id/8) mod n`: id mod 4 = 1 is team-common to t[k],
2 is owned by a player of t[k], 3 is team-common to t[k+1], 0 stays NPC. `ReassignPlayerAssets` moves the player's ships when
an admin moves the player to another team and sends one `EntityChange` per ship. Fake NPC hostility follows `TeamRelations`
pushes at once. The authority puts an empty `WorldUpdate` with the tick's game time before the spawns it emits, so the server
knows when the spawn states were sampled.

## 8. As a library, and the tests

`FakeClientSession`, `FakeAuthority`, `FakeSaveClient` and `FakeAuthoritySaves` have no sockets of their own; `LiveRunner`
drives them over TCP. Tests (`server/tests/X4MP.FakeNode.Tests`, the replication tests in `X4MP.Core.Tests`) drive them in
virtual time with the real server modules. `X4MP_LONG_TESTS=1` enables the 200 MB save acceptance run.

## 9. Browser end-to-end tests

The Playwright suite in [`server/web/e2e/README.md`](../server/web/e2e/README.md) starts a real server on its own ports
(HTTP 47890, node TCP 47880, UDP 47881) with FakeNode bots as the players and drives the GUI against it:
`dotnet build X4MP.sln`, then `cd server/web && npm ci && npx playwright install chromium && npm run e2e`.
