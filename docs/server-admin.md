# X4MP server: administration guide

How to install, run and operate `x4mp-server`. Design background is in
[server-design.md](server-design.md) (sections 1.4, 1.5, 4.3) and [dev-setup.md](dev-setup.md) (section 7).

## 1. Install and run

Build the single-file executable (self-contained, so no .NET install is needed on the target machine):

```
dotnet publish server/src/X4MP.Server -c Release -p:PublishProfile=win-x64    # out/win-x64/x4mp-server.exe (about 58 MB)
dotnet publish server/src/X4MP.Server -c Release -p:PublishProfile=linux-x64  # out/linux-x64/x4mp-server
```

Release builds run `npm ci` + `npm run build` in `server/web` first (incrementally) and embed the result in the exe.
Add `-p:SkipWebBuild=true` to skip that step (the existing `server/web/dist` is embedded; if there is none, a
"GUI not built" placeholder page is embedded).

Commands:

| Command | What it does |
|---|---|
| `x4mp-server` or `x4mp-server run` | Runs in the console. Ctrl+C stops it gracefully. |
| `x4mp-server version` | Prints server version, protocol range and git build hash. |
| `x4mp-server service install` | Installs the Windows service (elevated). |
| `x4mp-server service uninstall` | Stops and removes it (elevated). |

Options: `--data-dir <dir>`, `--port <n>` (default 47790), `--name <service name>` (service verbs, default `X4MP`).
The ASP.NET Core options (for example `--urls`) are still accepted.

## 2. Ports

| Port | Proto | Purpose |
|---|---|---|
| 47780 | TCP | Game nodes: control and bulk transfer (not listening yet; arrives with the transport milestone). |
| 47781 | UDP | Game nodes: realtime position lane (same). |
| 47790 | TCP (HTTP) | Admin web GUI, `/healthz`, HTTP save fallback. Binds `0.0.0.0` so it is reachable on the LAN. |

The GUI is plain HTTP: use a trusted LAN or VPN. Set `X4MP:Admin:AllowRemote=false` in `appsettings.json` (next to the
exe) to bind loopback only, and `X4MP:Admin:Port` to change the port.

Firewall (run elevated; LAN/VPN only, from dev-setup.md section 7):

```
New-NetFirewallRule -DisplayName "X4MP" -Direction Inbound -Protocol TCP -LocalPort 47780,47790 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "X4MP UDP" -Direction Inbound -Protocol UDP -LocalPort 47781 -Profile Private -Action Allow
```

## 3. Data directory

Holds `x4mp.db` (SQLite, WAL mode), `logs/` (rolling `server-YYYYMMDD.log`), and later `saves/`, `uploads/`, `certs/`.
Resolved in this order:

1. `--data-dir <dir>`
2. `X4MP_DATA_DIR` environment variable (fine for the server; the env-var ban applies to the game mod only)
3. `%ProgramData%\X4MP` when running as a service
4. `./data` next to the exe when running in a console

Back up by stopping the server and copying the directory (or `x4mp.db` plus `x4mp.db-wal`/`-shm` if it is running).
Never commit it; it is git-ignored.

## 4. Windows service

`service install` (run from an elevated prompt) executes, with service name `X4MP`:

```
sc.exe create X4MP binPath= "\"<exe>\" run --service --name X4MP --data-dir \"%ProgramData%\X4MP\"" start= auto obj= "NT SERVICE\X4MP" DisplayName= "X4MP multiplayer server"
sc.exe description X4MP "..."
sc.exe failure X4MP reset= 86400 actions= restart/10000/restart/10000/restart/10000
icacls "%ProgramData%\X4MP" /grant "NT SERVICE\X4MP:(OI)(CI)M"
```

If the prompt is not elevated, it prints those commands instead of running them. The service runs as the virtual
account `NT SERVICE\X4MP` (no password), so keep the exe somewhere that account can read (for example
`C:\Program Files\X4MP\`, not a user profile folder). Services start with `--service`, which switches on the Windows
service host (`AddWindowsService`) and the `%ProgramData%\X4MP` data dir default.

`service uninstall` runs `sc.exe stop X4MP` (a not-running service is fine) and `sc.exe delete X4MP`. The data dir
is left in place.

### Manual verification (Windows VM, elevated PowerShell)

Automated tests cover the command construction and argument parsing only; nothing installs a service in CI.

1. Publish win-x64 and copy `x4mp-server.exe` to `C:\Program Files\X4MP\`.
2. `& "C:\Program Files\X4MP\x4mp-server.exe" service install`. Expect the four commands echoed and "installed".
3. `sc.exe qc X4MP` shows `SERVICE_START_NAME : NT SERVICE\X4MP` and `START_TYPE : AUTO_START`.
   `sc.exe qfailure X4MP` shows three restart actions at 10000 ms.
4. `sc.exe start X4MP`; then `(Invoke-RestMethod http://localhost:47790/healthz).status` is `ok`, and
   `http://localhost:47790/` in a browser shows the GUI. `C:\ProgramData\X4MP` now contains `x4mp.db` and `logs\`.
5. `sc.exe stop X4MP`, then check the last lines of `C:\ProgramData\X4MP\logs\server-*.log` contain `shutdown complete`.
6. Kill the process (`taskkill /F /FI "SERVICES eq X4MP"`): the service restarts itself after about 10 s.
7. `& "C:\Program Files\X4MP\x4mp-server.exe" service uninstall`; `sc.exe query X4MP` reports "does not exist".

## 5. First run

From an empty folder:

```
x4mp-server.exe
```

It creates `./data` (database plus `logs/`), applies database migrations, logs where the GUI is, and serves it at
`http://localhost:47790/` (and `http://<LAN address>:47790/` for other machines). Check
`http://localhost:47790/healthz`: it returns `status`, `version`, the protocol range and uptime as JSON.

Admin login and the first-run admin password arrive with the auth task; until then the GUI shell is served without
a login.
