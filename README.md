# X4MP

Multiplayer for X4: Foundations: a .NET 10 dedicated server, a native mod, and a web admin GUI.

- Documentation index: [docs/README.md](docs/README.md)
- Developer setup: [docs/dev-setup.md](docs/dev-setup.md); verify your machine with `tools/check-env.ps1`.

## Build and test

Requires the .NET SDK pinned in `global.json`.

```
dotnet build
dotnet test
dotnet run --project server/src/X4MP.Server
```
