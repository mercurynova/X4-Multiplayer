# FakeNode

A headless stand-in for the X4 mod. It speaks the real node protocol (`TcpNodeClient`), so it exercises the
server without a game. Design: `docs/server-design.md` section 6.

## What works today

| command | state |
|---|---|
| `galaxy` | offline: generate the fake galaxy and print stats |
| `authority` | live: the fake authority. Sends the string table and `GalaxyMetadata`, walks the join pipeline to in-game, honours `CaptureSet` (index pass, `EntitySpawn`, `SectorComplete`, `WorldUpdate` at the requested sector and focus rates), `NodeStats`, `GalaxySummary` |
| `client` | live: a fake player. Joins, flies (`wander`, `patrol`, `explore`), sends `PlayerState` at 20 Hz, receives the server's interest and replication. With `--verify` it checks every `Replication` entry against ground truth |
| `swarm --clients K [--with-authority]` | live: K clients (plus one authority) in one process |
| `inspect` | stubbed (prints "not available yet", exit 3) |

Against a server **without** a session actor (a bare gateway, as in some tests) a node only keeps its connection
alive with Ping/Pong; the play-your-role behaviour starts when the server announces a session (`SessionState`).

## The save pipeline (M1-12)

Against a server that runs the save service (it says so with the `SaveHttp` bit in `ServerHello.server_caps`; a bare
session actor does not) the join is the real one, byte for byte:

- **Authority.** It sends the string table, walks to in-game and then answers `RequestSave`: it builds a fake save
  (deterministic gzip of an X4-style `<savegame><info>` document plus pseudo-random filler up to `--save-mb`, so the
  transfer moves real bytes; `FakeSaveGenerator`) and a real `X4MF` manifest of the fake stations, sends
  `GalaxyMetadata` (keyed by the save's hash) and the `SaveStarted` journal marker, then uploads both files in-band on
  the Bulk lane with the 8-chunk window, honouring `SaveChunkAck` and the resume offset of `SaveUploadAccept`
  (`FakeAuthoritySaves`). The session starts from that checkpoint.
- **Client.** It waits in `SyncingSave` for `SessionSaveInfo` (the server holds it until a checkpoint is current),
  downloads the save and the manifest with `SaveDownloadRequest{offset}` (resuming from its `.part` file), acks every 4
  chunks, verifies the SHA-256, reports `LoadStatus` / `SaveReady`, "loads" for 50 ms, matches the manifest
  (`ManifestReport`), takes the `StringTableAdd` replay and the `WorldCatchUp`, and sends `NodeReady`
  (`FakeSaveClient`). It waits for the server to confirm each phase (`FakePhaseTracker`, from `RosterUpdate`) before the
  next frame, because the server checks every frame against the phase it has recorded.
- Output: `checkpoint stored: ...` for the authority, `joined with the save after 0.2s: ... bytes downloaded and
  verified ...` per client, and `ingame=N` in the report and summary lines.

`--save-mb N` sets the size of the fake save (default 4, at most 4096). Saves live in a temporary directory that is
deleted at the end of the run. For a 200 MB transfer on a local server:

```powershell
dotnet run --project tools/X4MP.FakeNode -- swarm --clients 3 --with-authority --save-mb 200 --duration 60
```

The same pipeline is driven by the tests in `X4MP.Server.Tests/Saves` (kill at 50% and resume, hash mismatch, non-gzip,
`ghosts_cleaned=false`, manifest policy, catch-up, ...). `X4MP_LONG_TESTS=1` runs the 200 MB acceptance there.

## Run a local server plus a swarm

```powershell
# terminal 1: the server (data dir holds the SQLite db and the generated admin password)
$env:X4MP__Net__MaxConnectionsPerIp = '64'   # defaults: 4 connections per IP, 8 players;
$env:X4MP__Net__MaxPlayers = '16'            # a swarm from one machine needs more
dotnet run --project server/src/X4MP.Server -- --data-dir $env:TEMP\x4mp-data

# terminal 2: 4 verifying clients + 1 authority for 60 seconds
dotnet run --project tools/X4MP.FakeNode -- swarm --clients 4 --with-authority --verify --duration 60
```

Output: one `welcome:` line per node, `in game` lines when a node finished the join pipeline, a line every
5 seconds (`connected=5/5 errors=0 pings=... rtt avg=...`), and at the end one `verify:` line per client, an aggregate
`verify:` line and the final `summary:`:

```
[Bot01] verify: ghosts=113 spawns=113 despawns=0 frames=2402 entries=85492 checksums=11/11 resyncs=0 errors=0 checked=85492 stale=0 tombstoned=0
verify: clients=4 frames=7412 entries=222841 checked=222841 ghosts=445 spawns=532 despawns=87 checksums-ok=44/44 resyncs=0 position-errors=0 errors=0
summary: nodes=5 joined=5 errors=0 pings=300 rtt avg=0.40ms max=10.60ms elapsed=60.0s
```

Exit code 0 means no node failed and verification found nothing; 1 means a handshake or connection failed or
verification found at least one error; 3 means the requested feature is not built yet. Ctrl+C ends the run cleanly
(nodes send `Disconnect(ClientQuit)`).

## What `--verify` checks

Ground truth is a pure function of `(seed, game time)` (`FakeWorld`), so a client can compute where every ship should
be at the time an entry was sampled (`Replication.authority_game_time + TIME`):

- **Position, rotation, velocity, sector** of every entry within quantisation tolerance (`ReplicationVerifier`). Entries
  are merged against the client's own baseline, so a delta-encoding bug shows up as a wrong value.
- **Spawn before state**: an entry for an id that is neither a ghost nor despawned less than 5 s ago is an error.
  A *partial* entry that arrives before a ghost's first full entry is stale (a frame that was already queued when the
  entity despawned and spawned again; the Control lane overtakes the Realtime lane): it is ignored and counted
  (`stale=`), the full entry the server always sends first follows.
- **Desync guard**: `InterestChecksum{count, xor}` is compared with the ghost set (persistent entities are not ghosts).
  A mismatch sends `ResyncRequest` and counts (`resyncs=`); a mismatch that survives three checksums is an error.
  The hash is `splitmix64(net_id)` XORed over the held ghosts (`InterestHash` in X4MP.Protocol).
- **Stale ghosts**: a ghost that gets no entry for 45 s (three times the longest keyframe interval) is an error.

## The fake authority and sample times

`WorldUpdate` carries `game_time` and the server mirror stamps every state change with it, but an `EntitySpawn` state has
no time. The fake authority therefore puts an empty `WorldUpdate{states: []}` with the tick's game time in front of the
spawns it emits, so the server knows exactly when the spawn states were sampled. Entities inside a `CaptureSet` focus
sphere stream at the sphere's rate (the server asks for 20 Hz around each player), the rest of a sector at the sector rate.

## Options

`--server host:port` (default `127.0.0.1:47780`), `--clients K`, `--with-authority`, `--duration N` (seconds;
default: until Ctrl+C), `--name NAME` (single node), `--name-prefix PREFIX` (swarm, default `Bot`, so
`Bot01`..), `--password PW` (session password), `--seed N`, `--behavior wander|patrol|explore`, `--verify`,
`--sectors N --ships N --tick HZ --fps N` (universe and authority shape), `--save-mb N` (authority: size of the fake save).

Player keys are derived from `--seed` and the node name, so re-running against a server with a persistent
database rejoins as the same players. Change `--seed` or `--name-prefix` to appear as new ones (a name stays
bound to the first key that used it, otherwise the server answers `NameTaken`).

## As a library

`FakeClientSession` (the receiving half of a client: ghost set, verifier, checksum, resync), `FakeAuthority`,
`FakeSaveClient` and `FakeAuthoritySaves` (the join and checkpoint halves of the save pipeline; they send through a
`TcpNodeClient` you hand them and take every received frame through `HandleAsync` / `Handle`) have no sockets of their own. `LiveRunner` drives them over TCP; the replication tests in `X4MP.Core.Tests` drive them in virtual time together
with the real server modules, so five minutes of game time run in a few seconds.
