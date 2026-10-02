# X4MP: multiplayer for X4: Foundations (work in progress)

[![CI](https://github.com/mercurynova/X4-Multiplayer/actions/workflows/ci.yml/badge.svg)](https://github.com/mercurynova/X4-Multiplayer/actions/workflows/ci.yml)

> [!WARNING]
> **This project is unfinished and NOT playable.** There is no release, no installer and no
> working in-game mod yet. Please don't download it expecting to play: it won't work, and it
> may change completely at any time. There is no support.

X4MP is a from-scratch attempt at letting several players share one X4: Foundations universe.
One player's game simulates the universe; every game connects to a standalone server, which
owns sessions, saves, teams and a shared economy, and comes with a web admin dashboard.

**Status:** the server, its web dashboard and the test tools are being built and tested with
simulated players (milestone M1). The in-game mod (C++ on X4Native, plus Lua/MD) starts in M2.
See [docs/roadmap.md](docs/roadmap.md) for milestones.

| Part | Tech |
|---|---|
| Server + admin GUI | C# / .NET 10, ASP.NET Core, SignalR, SQLite; React + Vite + TypeScript |
| Game mod (Windows) | C++ on [X4Native](https://github.com/eg3r/X4Native), Lua UI, MD scripts |
| Wire protocol | FlatBuffers |
| Testing | FakeNode (simulated universe + players), xUnit, Vitest, Playwright |

**For developers:** start at [docs/README.md](docs/README.md) (knowledge base),
[docs/architecture.md](docs/architecture.md) (design) and [docs/dev-setup.md](docs/dev-setup.md)
(machine setup; verify a machine with `tools/check-env.ps1`).

## Build and test

Requires the .NET SDK pinned in `global.json`.

```
dotnet build
dotnet test
dotnet run --project server/src/X4MP.Server
```

## Not affiliated with Egosoft

X4: Foundations is a trademark of Egosoft GmbH. This is an unofficial fan project, not
endorsed by or affiliated with Egosoft. It contains no Egosoft game data; you need your own
copy of the game.

## License

GPL-3.0, see [LICENSE](LICENSE). Bundled third-party code keeps its own license
(e.g. `mod/third_party/x4native` is MIT).
