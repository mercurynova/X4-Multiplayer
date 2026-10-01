# FakeNode

A headless stand-in for the X4 mod. It speaks the real node protocol (`TcpNodeClient`), so it exercises the
server without a game. Design: `docs/server-design.md` section 6.

## What works today

| command | state |
|---|---|
| `galaxy` | offline: generate the fake galaxy and print stats |
| `client` | live: handshake, Ping/Pong keepalive, Welcome summary, RTT every 5 s |
| `authority` | live: same, requesting the Authority role |
| `swarm --clients K [--with-authority]` | live: K clients (plus one authority) in one process |
| `inspect`, `--verify` | stubbed (print "not available yet", exit 3) until replication lands (M1-05..08) |

## Run a local server plus a swarm

```powershell
# terminal 1: the server (data dir holds the SQLite db and the generated admin password)
$env:X4MP__Net__MaxConnectionsPerIp = '64'   # defaults: 4 connections per IP, 8 players;
$env:X4MP__Net__MaxPlayers = '16'            # a 9-node swarm from one machine needs more
dotnet run --project server/src/X4MP.Server -- --data-dir $env:TEMP\x4mp-data

# terminal 2: 8 clients + 1 authority for 30 seconds
dotnet run --project tools/X4MP.FakeNode -- swarm --clients 8 --with-authority --duration 30
```

Output: one `welcome:` line per node (player id, granted roles, caps, resume grace), then a line every
5 seconds (`connected=9/9 errors=0 pings=... rtt avg=...`) and a final `summary:`. Exit code 0 means no node
failed; 1 means at least one handshake or connection failed; 3 means the requested feature is not built yet.
Ctrl+C ends the run cleanly (nodes send `Disconnect(ClientQuit)`).

## Options

`--server host:port` (default `127.0.0.1:47780`), `--clients K`, `--with-authority`, `--duration N` (seconds;
default: until Ctrl+C), `--name NAME` (single node), `--name-prefix PREFIX` (swarm, default `Bot`, so
`Bot01`..), `--password PW` (session password), `--seed N`.

Player keys are derived from `--seed` and the node name, so re-running against a server with a persistent
database rejoins as the same players. Change `--seed` or `--name-prefix` to appear as new ones (a name stays
bound to the first key that used it, otherwise the server answers `NameTaken`).
