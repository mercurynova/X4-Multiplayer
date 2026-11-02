# x4mp-server

The dedicated server for X4MP, a multiplayer mod for X4: Foundations. It keeps the session: the
player roster, ghost ships, the economy ledger, save transfer and the admin interface. The game
connects to it as a node; the server never runs the game itself.

This file ships inside the release archives. For the long version — services, the GUI settings
page, the REST API, backups — read [`server-admin.md`](server-admin.md) in the repository.

## Which archive do I want?

| Archive | Contains | Use it when |
|---|---|---|
| `x4mp-server-<tag>-win-x64.zip`, `...-linux-x64.tar.gz` | one self-contained executable | you run the server on its own machine or VPS |
| `x4mp-server-<tag>-win-x64-bundle.zip` | a folder of files | you are wiring the server into the mod by hand |
| `x4mp-mod-<tag>.zip` | the mod, the native extension, the server bundle, the launcher and the player scripts | **the normal install**: unzip into the X4 folder |

The last one is the usual path: the server bundle lands in `<X4>\extensions\x4mp\server`, the
in-game menu starts it, and `x4mp-launcher.exe` sits at the top of the zip for the player who
has to change their mods before joining. Nothing else needs unpacking.

## Running the standalone server

Windows:

```powershell
.\x4mp-server.exe --data-dir C:\x4mp-data
```

Linux:

```bash
chmod +x x4mp-server          # the tar.gz keeps the executable bit; a zip would not
./x4mp-server --data-dir ~/x4mp-data
```

The first start prints the admin password once and writes it to
`<data-dir>/initial-admin-password.txt`. Sign in at `http://localhost:47790/` as `admin`, then
change the password — the file is deleted when you do. `GET /healthz` answers without a login and
is the quickest way to see that the server is alive.

Useful commands: `x4mp-server version` (version, protocol range, git build hash), `x4mp-server help`,
`x4mp-server service install` on Windows for a service. Ctrl+C stops it gracefully.

## Ports

| Port | Protocol | What | Setting |
|---|---|---|---|
| 47780 | TCP | game nodes: control and bulk transfer | `X4MP:Net:NodeTcpEndpoint` |
| 47781 | UDP | realtime lane, when enabled | `X4MP:Net:UdpPort` |
| 47790 | HTTP | GUI, `/healthz`, REST API | `server.port` is the node port; the HTTP port is the node port plus ten |

Only 47780 needs to be reachable from the internet for players. 47790 is for you.

## Configuration

`appsettings.json` next to the executable is read at start; environment variables override it with
the `X4MP__Section__Key` form (`X4MP__Net__AdminPassword`, `X4MP__Net__NodeTcpEndpoint`, …). The
in-game launcher passes the node endpoint, the UDP port and the admin password that way, and leaves
everything else to this file — which is where session-wide switches live, for example:

```json
{
  "X4MP": {
    "Economy": {
      "CreditMode": "PerPlayer"
    }
  }
}
```

`CreditMode` matters for two players in one team: the default `Auto` gives a single-team session one
shared wallet, and then every transfer and loan between those players is refused as `SameWallet`.

## Where things live

| What | Default | Override |
|---|---|---|
| Data folder, standalone | `data` next to the executable | `--data-dir <dir>`, or the `X4MP_DATA_DIR` environment variable |
| Data folder, Windows service | `%ProgramData%\X4MP` | same |
| Data folder, started from the game | `%LocalAppData%\X4MP\server-data` | `server.data_dir` in `x4mp.json` |
| Database, saves, uploads | inside the data folder | — |
| Admin password, net side | `<data folder>/admin-password.txt` when the game starts the server | `X4MP:Net:AdminPassword` |

Back up the data folder: it holds `x4mp.db` and every save the session produced. The console log is
written by whatever starts the server — the game sends it to `<data folder>\logs\server-console.log`,
a standalone run prints to its own console unless the logging section of `appsettings.json` says
otherwise.
