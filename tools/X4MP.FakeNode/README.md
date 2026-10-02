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

## Teams (M1-F3)

| option | meaning |
|---|---|
| `--team <id\|name>` | client/swarm: join this team. A lobby (`Teams.JoinMode=Lobby`) gets a `TeamChoice`; a name that does not exist is created when `Teams.AllowCreateInLobby` is on. A node that Auto already placed elsewhere asks for a move once in game (`TeamChangeRequest`; the server needs `Teams.AllowSelfTeamChange`) |
| `--team-pick lobby-random` | client/swarm: answer the lobby with a random open team (not locked, not full, no password); with creating allowed, "a new team named after the player" is one more choice |
| `--teams N` | swarm: client i joins the team called `Team (i mod N)+1` (created on first use when the lobby allows it). The authority tags its ships for team ids 1..N (implies `--team-assets`) |
| `--relations coop\|allied\|ffa\|twoteams` | swarm: a layout. Implies `--teams` (coop 1, twoteams 2, allied/ffa one per client) and prints the server settings it needs (`server-settings=[...]`) |
| `--commander shared\|own\|foreign` | works across teams: `own` = team-common ships of the client's team plus the ones it owns, `shared` = a teammate's, `foreign` = any other team's |

The server has no REST for teams yet (M1-T5), so a swarm cannot create presets through an admin token. A layout is
reached through the server's settings instead (the printed `--X4MP:Teams:...` arguments: `JoinMode=Lobby`,
`AllowCreateInLobby=true`, `AutoAssign=Balance`, `DefaultRelation=Allied|Hostile`); the clients then place themselves
through the lobby. Tests drive `TeamModule` in-process (`CreateTeamAsync`, `AssignPlayerAsync`, `SetRelationAsync`).
With `AutoAssign=Balance` the authority becomes the first member of `Team 1` (a node without the Client role cannot answer a lobby).

```powershell
$env:X4MP__Net__MaxConnectionsPerIp = '64'
dotnet run --project server/src/X4MP.Server -- --data-dir $env:TEMP\x4mp-data --X4MP:Teams:JoinMode=Lobby --X4MP:Teams:AllowCreateInLobby=true --X4MP:Teams:AutoAssign=Balance
dotnet run --project tools/X4MP.FakeNode -- swarm --clients 6 --with-authority --teams 2 --commander foreign --verify --duration 25
dotnet run --project tools/X4MP.FakeNode -- swarm --clients 6 --with-authority --team-pick lobby-random --verify --duration 15   # on a fresh server
```

Summary lines: `teams: clients=6 placed=6 unplaced=0 teams-used=2 spread=[1:3,2:3] requests=7 rejected=1`, then
`commander(foreign): ... rejected=282 forwarded-to-authority=0`, `npc-hostility: relation-changes=N hostile-team-pairs=[1-2]
engaged-ship-pairs=... ships-at-war=... sectors-with-fights=...` and, after an admin move, `reassign: requests=1
assets-moved=36 owner-changes-seen-by-clients=12`.

- **Asset owners (fake authority).** Ships are tagged for the team ids t[0..n-1] with `k = (id/8) mod n`: id mod 4 = 1 is
  team-common to t[k], 2 is owned by a player of t[k] (a real member of the team table for id mod 8 = 2, the made-up
  player 65000 for id mod 8 = 6), 3 is team-common to t[k+1], 0 stays NPC. An owner is fixed when first needed (a spawn
  record or a reassign) and then changes only through `ReassignPlayerAssets`.
- **`ReassignPlayerAssets`.** The authority moves every ship the player owns in the old team to the new team and sends one
  `EntityChange` (`OwnerTeam` bit; the player stays the owner) per ship. The server mirror follows and forwards the change
  to clients that hold the ship, whose ghosts (and `--commander` picks) follow.
- **Fake NPC hostility.** `FakeAuthority.Hostility()` answers which team pairs are Hostile in the matrix the node holds
  (relations plus `DefaultRelation`) and how many ship pairs of such teams share a sector. It follows a
  `TeamRelations` push at once (0-20 ms after `SetRelationAsync` in `TeamSwarmLiveTests`).
- **Test hooks.** `LiveRunOptions.OnAuthority` and `OnClientReady` hand a test the `FakeAuthority` and a `FakeClientHandle`
  (`SendOrderAsync(netId, sector)` sends one targeted `AssetOrder` and returns the server's answer).

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

## Failure injection (M1-F2)

| option / command | meaning |
|---|---|
| `--latency MS` `--jitter MS` | add MS ms (+-jitter) to every TCP chunk and every UDP datagram in each direction (round trip +2*MS). TCP keeps its order, UDP datagrams may reorder with jitter. Summary line `latency:` |
| `--slow-reader R` `--slow-clients N` | the first N clients (default 1) read their socket slowly **once in game**: R is bytes per second (`4096`) or a pattern `pause=3/45` (read 3 s, then stop reading for 45 s, repeat). The node uses a 4 KB receive window so TCP flow control reaches the server early. It must not disturb the others: slow readers are left out of `--verify`, the summary line `slow-reader:` counts how many were closed by the server and whether the other clients had errors. A node that the server closed (reset after `SlowConsumer`) is the expected outcome, not an error |
| `--disconnect-every S` | every S seconds a client drops its socket without a goodbye and resumes with the resume token. The ghost table restarts (the server clears the baselines and re-sends spawns and keyframes); `resume:` summary: reconnects, refusals, keyframes received after each resume (a resume without a keyframe within 10 s is an error) |
| `--reload-every S` | the same through `Disconnect(ClientReload)`, 0.4 s away ("reload"), resume, `NodeReady` again. (The server never moves a node back from InGame, so "redo the join path" is the resume handshake plus `NodeReady`) |
| `fuzz` | `--duration S --seed N --clients K --fuzz-mode all\|handshake\|session\|flood --local-ip 127.0.0.2 --rotate-ip`. A seeded hostile peer: garbage and truncated frames, oversized and zero lengths, bad flags and lanes, unknown types, role/lane/phase violations, invalid FlatBuffers, half-open floods, and bursts of more than 20 policy violations. Prints the Disconnect codes the server answered with and whether the server still answers a handshake (`server-alive=`; exit 1 when not). With `--local-ip` the temp ban a fuzzer earns hits only its own address; `--rotate-ip` moves on to the next loopback address so the fuzzing continues after each ban |
| `inspect` | one client that walks the join path (`--no-join` only listens) and prints every decoded frame: `[ time] Lane Type (bytes) Type{field=...}`. `--filter T1,T2` and `--max-frames N` |

```powershell
fakenode swarm --clients 4 --with-authority --verify --slow-reader pause=2/120 --duration 75   # poll /api/v1/diagnostics/connections: its queue grows, then it is closed
fakenode swarm --clients 4 --with-authority --verify --udp --disconnect-every 10 --reload-every 15 --duration 60
fakenode swarm --clients 4 --with-authority --verify --latency 100 --jitter 20 --udp --duration 40
fakenode fuzz --duration 60 --clients 3 --seed 7 --local-ip 127.0.0.2 --rotate-ip     # while a swarm runs on 127.0.0.1
fakenode inspect --duration 10 --filter SessionState,RosterUpdate
```

A loopback server buffers megabytes before its send queue notices a reader that stopped, at the default traffic of about 30 KB/s per
client. To see the `SlowConsumer` close within half a minute give the server more to send, for example
`X4MP__Interest__MaxGhosts=3000` and `X4MP__Replication__BandwidthBudgetKBps=4000` on the server and `swarm ... --sectors 6 --ships 6000`.

## Economy behaviours (M1-F4)

| option | meaning |
|---|---|
| `--economy idle\|casual\|heavy` | clients take part in the credit economy: teammate transfers, donations, pool deposits/withdrawals, loan offers (to teammates or anyone), loan answers (accept 85%), repayments (partial, then full), forgiving and withdrawing. `casual` is one action per ~8 s, `heavy` one per ~3 s (the server allows 5 requests per 10 s per player, so heavy sits close to the limit and gets some `RateLimited`). `casual` and `heavy` also trade (`--trade`: a proposal every 15 s / 8 s). `idle` sends nothing but reconciles. A client spends only a small share of its balance per request and picks recipients from the roster (in-game clients) |
| `--dupe-attack` | clients also replay a request key (same bytes), reuse a key with another payload, send one request three times back to back (race) and send two transfers that together overdraw the wallet. Every answer must be idempotent or a rejection (`RequestIdReuse`, `RateLimited`). A reuse that gets `Ok`, or two `Ok`s of one key with different balances, is a **duplicate effect**: counted, printed as `DUPLICATE EFFECT: ...` and an error. The authority with `--income-rate` also resends `CreditDelta`s it already sent (same `seq`, sometimes another amount). Implies `--economy casual` |
| `--loan-default` | borrowers accept every offer and never repay; the offers fall due after 4 to 6 s, so loans go **Overdue** (`/api/v1/economy/loans`, the Loans tab badge). Implies `--economy casual` |
| `--income-rate R` | the authority books R `CreditDelta{seq}` per second **and player** (75% income, 25% spend); every client books R/4 of its own local changes. Both honour `--dupe-attack` (resends) and stop in the last `TradeQuietSeconds` of a timed run |
| `--admin-url URL` `--admin-user U` `--admin-password P` | end of the run: sign in to the admin API (the account must have its initial password already changed), run the auditor, scan the ledger and compare it with the nodes |

Every node reconciles (`EconomyReconciler`): consecutive wallet versions are one ledger transaction, so an acknowledged `CreditDelta` must move the wallet by exactly its amount (a duplicate `seq` by nothing), `acked_delta_seq` never goes backwards or past what was sent, the authority (it sees every wallet) checks that transfers, pool moves, donations, loans and trades conserve credits, `EconomyResult` balances equal the update of the same wallet version, and a player wallet is never negative except after game spending. A gap in the versions is counted as `unverifiable`, not as drift. Escrow wallets are left out (the wire clamps their id to 16 bits).

Summary lines: `economy(mode):` (requests, rejects by reason, loans), `dupes:` (replays, reuses, races, `duplicate-effects`), `income:`, `reconciliation:` (`drift` is an error) and, with `--admin-url`, `invariants:` (auditor, ledger sum, requests booked twice, each node's deltas as a prefix sum of what it sent, wallet drift against the server, loans by state, trades by state, `in-doubt=[ids]`). Any problem makes the exit code 1.

```powershell
# 3 teams that are hostile to each other, the heavy economy under attack, trades that fail and time out, verified replication
fakenode swarm --clients 6 --with-authority --teams 3 --relations ffa --economy heavy --dupe-attack --income-rate 0.5 `
  --trade-fail 10 --trade-timeout 5 --verify --duration 600 --admin-url http://127.0.0.1:47790 --admin-password <the admin password>
fakenode swarm --clients 4 --with-authority --loan-default --duration 60        # overdue loans after ~10 s
```

The server for the first command needs `X4MP__Net__MaxPlayers=16`, the lobby settings the swarm prints (`--relations ffa`) and, for money to cross teams, `X4MP__Economy__DonateScope=Anyone`, `LoanScope=Anyone`, `TradeScope=Anyone` and `TradeRequiresProximity=false`.
