# FakeNode

A headless stand-in for the X4 mod. It speaks the real node protocol (`TcpNodeClient`), so it exercises the X4MP server
without a game: a fake authority, fake clients that fly and verify what the server replicates, trading, failure
injection (latency, slow readers, reconnects, fuzzing) and a frame inspector.

**Full documentation: [docs/fakenode.md](../../docs/fakenode.md)** (every command and flag, how to read the summary lines,
exit codes, the e2e harness). Server setup: [docs/server-admin.md](../../docs/server-admin.md).

Quick start (a swarm from one machine needs more than the default 4 connections per IP):

```powershell
# terminal 1
$env:X4MP__Net__MaxConnectionsPerIp = '64'; $env:X4MP__Net__MaxPlayers = '16'
dotnet run --project server/src/X4MP.Server -- --data-dir $env:TEMP\x4mp-data

# terminal 2: 4 verifying clients + 1 authority for 60 seconds; exit code 0 = pass
dotnet run --project tools/X4MP.FakeNode -- swarm --clients 4 --with-authority --verify --duration 60
```

Commands: `swarm`, `authority`, `client`, `inspect`, `fuzz`, `galaxy`, `help`. Run `fakenode help` for the option list.
