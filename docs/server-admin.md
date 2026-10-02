# X4MP server: administration guide

How to install, run and operate `x4mp-server`. Written so a teammate can run a server from this page alone. Design
background is in [server-design.md](server-design.md) (sections 1.4, 1.5, 2.9, 4.2, 4.3, 5) and
[dev-setup.md](dev-setup.md) (section 7). To try a server without X4, use the load/test tool described in
[fakenode.md](fakenode.md).

Contents: 1 Install · 2 First run and login · 3 Data directory · 4 Ports and firewall · 5 Networks (LAN, VPN,
allow-list) · 6 Windows service and systemd · 7 Configuration · 8 GUI pages · 9 Backups · 10 Logs · 11 Troubleshooting.

## 1. Install and publish

The server is one self-contained executable (no .NET install needed on the target). Build it on a machine with the .NET 10
SDK (and Node 22+ for the GUI):

```
dotnet publish server/src/X4MP.Server -c Release -p:PublishProfile=win-x64    # out/win-x64/x4mp-server.exe (about 60 MB)
dotnet publish server/src/X4MP.Server -c Release -p:PublishProfile=linux-x64  # out/linux-x64/x4mp-server
```

Release builds run `npm ci` + `npm run build` in `server/web` first and embed the result. `-p:SkipWebBuild=true` skips
that step: the existing `server/web/dist` is embedded, or, when there is none, a "GUI not available" placeholder page
(the server and its API still work; `/healthz` is linked from the placeholder). Publish also leaves some `*.pdb` files and
`appsettings.json` beside the exe; only the exe is needed. Copy it anywhere (on Linux `chmod +x x4mp-server`).

Commands:

| Command | What it does |
|---|---|
| `x4mp-server` or `x4mp-server run` | Runs in the console. Ctrl+C stops it gracefully. |
| `x4mp-server version` | Prints server version, protocol range and git build hash. |
| `x4mp-server help` (or `-h`) | Usage. |
| `x4mp-server service install` / `uninstall` | Windows service, elevated (section 6). |

Options: `--data-dir <dir>` (alias `--data`), `--port <n>` (HTTP port, default 47790), `--service` (set by
`service install`), `--name <service name>` (service verbs, default `X4MP`). Both `--opt value` and `--opt=value` work.
Anything else is passed to ASP.NET Core, which also takes configuration as `--Section:Key=value` (for example
`--X4MP:Net:MaxPlayers=16`) and `--urls`.

## 2. First run and first login

```
x4mp-server.exe --data-dir C:\x4mp-data
```

1. The server creates the data directory, the SQLite database `x4mp.db`, `logs/`, `saves/`, `uploads/`, applies migrations
   and logs `Now listening on ...` plus a banner with the data directory.
2. On the very first start (no admin users) it creates the user **`admin`** with a random 20-character password. The
   password is printed once to the console and written to `<data-dir>/initial-admin-password.txt` (owner-only). It is
   never written to the log.
3. Open `http://localhost:47790/` (or `http://<LAN address>:47790/` from another machine) and sign in as `admin` with
   that password. The server **forces a password change** at first login (at least 12 characters, `X4MP:Admin:MinPasswordLength`).
   When you have changed it, `initial-admin-password.txt` is deleted.
4. `GET http://localhost:47790/healthz` returns `status`, `version`, the protocol range and uptime as JSON (no login).

Lost the admin password: stop the server, delete the admin row or the whole `x4mp.db` (this loses everything), start
again: the bootstrap runs again whenever no admin user exists. There is no recovery e-mail.

Roles: `Admin` (everything) and `Viewer` (read-only: every page, no kick/ban/settings/chat/saves actions). There is one
admin user for now (no user-management UI or API yet). Scripts and teammates who only need to look use **API tokens**,
created through the API as an admin (`POST /api/v1/tokens` with `{"name":"ci","role":"Viewer"}`; `GET` lists, `DELETE
/api/v1/tokens/{id}` revokes; there is no GUI for them yet). The token is returned once, looks like `x4mp_...`, and is used
as `Authorization: Bearer <token>`. Changing the password of the user who created a token revokes it. `GET /api/v1/audit`
returns the admin audit log (logins, setting changes, token events).

Sign in through the API instead of the browser (the `X-X4MP: 1` header is required on every non-GET call, a CSRF guard):

```powershell
$b = 'http://localhost:47790'; $h = @{ 'X-X4MP' = '1' }
$s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod "$b/api/v1/auth/login" -Method Post -ContentType 'application/json' -Headers $h -WebSession $s `
  -Body (@{ username = 'admin'; password = (Get-Content <data-dir>\initial-admin-password.txt).Trim() } | ConvertTo-Json)
Invoke-RestMethod "$b/api/v1/auth/me" -WebSession $s     # mustChangePassword: true until you POST /api/v1/auth/change-password {current,new}
```

Login protection: 5 attempts per minute per IP (further ones get `429 RateLimited` with `Retry-After`), a failed login is
delayed 0.5-2.5 s, and 10 consecutive failures lock that username for 5 minutes (`429 AccountLocked`). The state is in
memory: restarting the server clears it. The values are `X4MP:Admin:LoginMaxPerWindow`, `LoginWindowSeconds`,
`LockoutFailures`, `LockoutMinutes`, `FailureDelayMs`, `SessionHours` (default 8).

## 3. Data directory

Resolved in this order:

1. `--data-dir <dir>`
2. configuration key `X4MP:DataDir` (appsettings or `X4MP__DataDir`)
3. environment variable `X4MP_DATA_DIR` (fine for the server; the no-env-var rule is for the game mod only)
4. `%ProgramData%\X4MP` when running as a Windows service (`--service`)
5. `data/` next to the exe in a console

What is in it:

| Path | Content |
|---|---|
| `x4mp.db` (+ `-wal`, `-shm`) | SQLite (WAL): admin users, API tokens, audit log, settings overrides, players, bans, sessions, teams, economy ledger, save index |
| `saves/` | Checkpoint saves and manifests uploaded by the authority, stored under their SHA-256 (`<hash>.xml.gz`, `<hash>.x4mf`) |
| `uploads/` | Partial (resumable) uploads; deleted by the janitor after 24 h (`Saves.UploadRetentionHours`) |
| `logs/` | `server-YYYYMMDD.log`, rolling (section 10) |
| `initial-admin-password.txt` | Only until the first password change |

Never commit it. Several servers on one machine each need their own data directory and their own ports.

## 4. Ports and firewall

| Port | Proto | Purpose | Setting |
|---|---|---|---|
| 47780 | TCP | Game nodes: control and bulk (the X4 mods connect here) | `X4MP:Net:NodeTcpEndpoint` (`address:port`) |
| 47781 | UDP | Game nodes: realtime position lane. Optional: `UdpPort=0` turns it off and everything runs over TCP | `X4MP:Net:UdpPort` |
| 47790 | TCP (HTTP) | Admin GUI, REST API, SignalR, `/healthz`, HTTP save-download fallback | `--port` or `X4MP:Admin:Port` |

`X4MP:Admin:AllowRemote=false` binds the HTTP port to loopback only (default is all interfaces). Players need 47780 (and
47781 if you want UDP) reachable; only admins need 47790, so on an internet-facing host do not open 47790 to the world.
The GUI is plain HTTP (see section 5).

**Windows** (elevated PowerShell; `Private` profile = LAN; use `-Profile Any` only for a VPN adapter you trust):

```powershell
New-NetFirewallRule -DisplayName "X4MP game TCP" -Direction Inbound -Protocol TCP -LocalPort 47780 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "X4MP game UDP" -Direction Inbound -Protocol UDP -LocalPort 47781 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName "X4MP admin"    -Direction Inbound -Protocol TCP -LocalPort 47790 -Profile Private -RemoteAddress LocalSubnet -Action Allow
# the same with netsh:
netsh advfirewall firewall add rule name="X4MP game TCP" dir=in action=allow protocol=TCP localport=47780 profile=private
```

**Linux** (ufw; restrict to your LAN/VPN range):

```
sudo ufw allow from 192.168.0.0/16 to any port 47780 proto tcp
sudo ufw allow from 192.168.0.0/16 to any port 47781 proto udp
sudo ufw allow from 192.168.0.0/16 to any port 47790 proto tcp
```

Port forwarding on a router for 47780/47781 is only needed for friends outside your network; prefer a VPN.

## 5. LAN, VPN and the private-network allow-list

Recommended: play on a LAN or on a VPN (Tailscale, ZeroTier, WireGuard). X4MP does not offer TLS yet, so do not expose the
GUI to the public internet.

The admin API (REST and the SignalR hub, **not** the game port 47780) only answers clients whose source address is:

- loopback (always),
- unless `X4MP:Admin:AllowPrivateNetworks=false`: RFC1918 (`10/8`, `172.16/12`, `192.168/16`), link-local, CGNAT
  `100.64.0.0/10` (Tailscale and most VPNs), IPv6 `fc00::/7` and `fe80::/10`,
- plus any CIDR in `X4MP:Admin:AllowedNetworks` (array), for example a public address you trust.

Everything else gets `403`. Example (`appsettings.json` next to the exe):

```json
{ "X4MP": { "Admin": { "AllowedNetworks": ["203.0.113.7/32"], "AllowPrivateNetworks": true } } }
```

or `X4MP__Admin__AllowedNetworks__0=203.0.113.7/32`. An invalid CIDR stops the server at startup. A server behind a reverse
proxy sees the proxy's address, so list that. The game port is protected separately (join password, per-IP connection
limit, handshake and violation rate limits, temp bans; section 7).

## 6. Running as a service

### Windows service

`service install` (from an elevated prompt) runs, with service name `X4MP`:

```
sc.exe create X4MP binPath= "\"<exe>\" run --service --name X4MP --data-dir \"%ProgramData%\X4MP\"" start= auto obj= "NT SERVICE\X4MP" DisplayName= "X4MP multiplayer server"
sc.exe description X4MP "..."
sc.exe failure X4MP reset= 86400 actions= restart/10000/restart/10000/restart/10000
icacls "%ProgramData%\X4MP" /grant "NT SERVICE\X4MP:(OI)(CI)M"
```

If the prompt is not elevated it prints these commands instead of running them. The service runs as the virtual account
`NT SERVICE\X4MP`, so keep the exe where that account can read it (for example `C:\Program Files\X4MP\`, not a user
profile). It restarts itself 10 s after a crash. `--service` switches on the Windows service host and the
`%ProgramData%\X4MP` data dir. Put configuration in `appsettings.json` next to the exe or in the machine environment
(`X4MP__...`, needs a service restart). `service uninstall` stops and deletes the service and leaves the data dir.

Manual verification (Windows VM, elevated PowerShell; nothing installs a service in CI):

1. Publish win-x64 and copy `x4mp-server.exe` to `C:\Program Files\X4MP\`.
2. `& "C:\Program Files\X4MP\x4mp-server.exe" service install`: expect the four commands echoed and "installed".
3. `sc.exe qc X4MP` shows `SERVICE_START_NAME : NT SERVICE\X4MP`, `START_TYPE : AUTO_START`; `sc.exe qfailure X4MP` shows
   three restart actions at 10000 ms.
4. `sc.exe start X4MP`; `(Invoke-RestMethod http://localhost:47790/healthz).status` is `ok`; `C:\ProgramData\X4MP` now
   has `x4mp.db` and `logs\`. The initial password is in `C:\ProgramData\X4MP\initial-admin-password.txt` (a service has no console).
5. `sc.exe stop X4MP`: the last log line is `shutdown complete`. `taskkill /F /FI "SERVICES eq X4MP"` restarts it after ~10 s.
6. `service uninstall`; `sc.exe query X4MP` reports "does not exist".

### Linux (systemd)

There is no `service install` on Linux. Create a user and a unit (`/etc/systemd/system/x4mp.service`):

```ini
[Unit]
Description=X4MP multiplayer server
After=network-online.target
Wants=network-online.target

[Service]
User=x4mp
ExecStart=/opt/x4mp/x4mp-server run --data-dir /var/lib/x4mp
Environment=X4MP__Net__ServerName=My X4MP server
Restart=on-failure
RestartSec=10
LimitNOFILE=65536
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

```
sudo useradd --system --home /var/lib/x4mp --create-home x4mp
sudo install -m 755 x4mp-server /opt/x4mp/x4mp-server
sudo systemctl daemon-reload && sudo systemctl enable --now x4mp
sudo cat /var/lib/x4mp/initial-admin-password.txt      # first login; also in 'journalctl -u x4mp'
```

## 7. Configuration

Settings come from, later wins: built-in defaults, `appsettings.json` next to the exe, environment variables
`X4MP__Section__Key` (double underscore; arrays `X4MP__Admin__AllowedNetworks__0`), command line `--X4MP:Section:Key=value`, then
the overrides saved from the Settings page (stored in `x4mp.db`).

Each setting is **Live** (the Settings page changes it at once, no restart) or **Boot** (read once at startup; the page
shows it read-only and `PATCH /api/v1/settings` answers `RestartRequired`: change it in appsettings/env and restart). The
list and types come from `GET /api/v1/settings/schema` (`requiresRestart` says Boot); values from `GET /api/v1/settings`.
Keys in the API are `Section.Name`, for example `Net.MaxPlayers`, `Economy.StartingCredits`.

| Section (`X4MP:...`) | What it holds | Notable keys |
|---|---|---|
| `Net` | Game listener, limits, identity. **All Boot.** | `ServerName`, `MaxPlayers` (8), `JoinPassword`, `AdminPassword` (in-game admin), `NodeTcpEndpoint`, `UdpPort`, `MaxConnectionsPerIp` (4), `ResumeGraceSeconds` (60), `SupportedGameBuilds` (`900-611726`), `RequiredModVersion`, `ModBuildStrict`, `ExtensionsMismatchIsWarning`, handshake/violation/ban limits |
| `Session` | Session name, timers | `SessionName`, `AuthorityGraceSeconds` (120), `HeartbeatTimeoutMs` |
| `Teams` | Team rules | `JoinMode` Auto/Lobby/AdminAssign, `AutoAssign`, `MaxTeams`, `DefaultRelation`, `AssetPolicy`, `AllowSelfTeamChange` |
| `Economy` | Wallets, trades, loans | `CreditMode`, `StartingCredits`, `DonateScope`, `TradeScope`, `LoanScope`, `TeamPoolEnabled` |
| `Saves` | Checkpoints and transfers | `AutosaveMinutes` (15), `JoinCheckpointPolicy`, `MaxSaveBytes` (1 GiB), `SaveBandwidthCapMBps`, `HttpFallback`, `SaveRetentionCount` (10) |
| `Replication`, `Interest`, `Relay` | World streaming, interest radii, chat/intent limits | `TickRateHz`, `BandwidthBudgetKBps`, `NearRadiusM`, `MaxGhosts`, `ChatEnabled` |
| `Mods` | Who sees players' mod lists | `ModListVisibility` |
| `Alerts` | Authority FPS alert | `AuthorityFpsThreshold`, `AuthorityFpsSeconds` |
| `Admin` | Bind and auth (appsettings/env only, not on the Settings page) | `Port`, `AllowRemote`, `AllowedNetworks`, `AllowPrivateNetworks`, `MinPasswordLength`, login limits |
| `Logging` | Log files (appsettings/env only) | `RetainedFileCount` (14), `FileSizeLimitBytes` (50 MB), `RingBufferCapacity` (20000) |

Things people ask:

- **Player cap**: `X4MP__Net__MaxPlayers=16` (Boot, 1..64, default 8). A full session rejects further players with `SessionFull`.
- **Join password**: `X4MP__Net__JoinPassword=...` (Boot); players enter it in the mod. Empty = open server.
- **Starting credits, trade rules, teams**: Live; use the Economy and Teams pages or `PATCH /api/v1/settings`.
- **A swarm from one machine** needs more than the 4 connections per IP: `X4MP__Net__MaxConnectionsPerIp=64` (see [fakenode.md](fakenode.md)).
- Settings changes and logins are recorded in the audit log (`GET /api/v1/audit`).

## 8. GUI pages

Nav order; every page is read-only for `Viewer`. Live data arrives over SignalR, so pages update without refresh.

- **Dashboard**: server health, session phase, authority FPS, players online, throughput sparklines, alerts.
- **Players**: roster with team, ping and state; per-player detail; kick, ban (and unban), mute, note, change team.
- **Map**: galaxy overview and per-sector view of what the authority streams (ships/stations, interest sets), for debugging.
- **Sessions & Saves**: create/start/stop the session, request a checkpoint, the saves library (pin, delete, download, upload
  a save by chunked upload), and live transfers.
- **Teams & Factions**: teams, members, the relation grid (allied/neutral/hostile), presets and team policy.
- **Economy**: wallets, ledger transactions (reverse, adjust, freeze), loans, trades, economy policy, CSV export.
- **Chat**: player chat log and broadcast to everyone.
- **Logs**: live server log with level/text filter, and log download.
- **Settings**: every setting from the schema grouped by section, Live ones editable, Boot ones read-only (secrets such as
  the join password are write-only). Your own password is changed on the change-password page the GUI sends you to.
- **Diagnostics**: per-connection RTT, send queues, drops, message rates, packet trace toggle; use it to spot a slow client.

## 9. Backups

The state is the data directory. Cold backup: stop the server (Ctrl+C or `sc.exe stop X4MP` / `systemctl stop x4mp`) and
copy the whole folder. Hot backup: copy `x4mp.db`, `x4mp.db-wal` and `x4mp.db-shm` together (or run
`sqlite3 x4mp.db ".backup out.db"`) plus `saves/`. Restore by putting them back into an empty data dir with the server
stopped. Saves are content-addressed, so copying `saves/` incrementally is safe; the janitor keeps the newest
`Saves.SaveRetentionCount` unpinned saves (pin the ones you want to keep). The authority's own X4 save folder is separate
and not managed by the server.

## 10. Logs

- Files: `<data-dir>/logs/server-YYYYMMDD.log`, rolled daily and at 50 MB, 14 kept (`X4MP:Logging:*`).
- Console: the same lines (service: use `journalctl -u x4mp` or the files). Minimum level via `Logging:LogLevel:Default`.
- The Logs page shows the last 20000 lines in memory; the download button gives the file.
- Useful lines: `Now listening on`, `shutdown complete`, `Created admin user`, `setting ... changed by ...`.
- Passwords, tokens and join/admin secrets are never logged; the audit log records who changed what.

## 11. Troubleshooting

| Symptom | Check |
|---|---|
| Server exits at start, "address already in use" / `Failed to bind` | Another process holds 47780/47790 (`netstat -ano \| findstr :47790`, `ss -ltnp`). Change `--port` / `X4MP__Net__NodeTcpEndpoint=0.0.0.0:48780`, or stop the other server (two on one machine need separate ports **and** data dirs). A busy UDP port: set `X4MP__Net__UdpPort=0` or another port. |
| Browser cannot reach `:47790` | Firewall (section 4); `AllowRemote=false`; wrong address. `403` page = your IP is outside the allow-list (section 5). |
| `429 RateLimited` / `AccountLocked` on login | Wait for `Retry-After` (<= 60 s; 5 min for a locked user) or restart the server to clear the in-memory throttle. Scripts: use an API token, not repeated logins. |
| `403` on every call after first login | You still must change the initial password (`mustChangePassword: true`). |
| `400 CsrfHeaderMissing` | Send `X-X4MP: 1` on POST/PUT/PATCH/DELETE (the GUI does). |
| Player rejected, "build mismatch" (`GameVersionMismatch`) | The game build must be in `Net.SupportedGameBuilds` (`900-611726`) **and** equal the authority's. |
| Player rejected, "mod version" (`ModVersionMismatch`) | Everyone needs the same X4MP mod version as the authority (or `Net.RequiredModVersion`); `ModBuildStrict` also compares the mod build. |
| Player rejected, "extensions" (`ExtensionsMismatch`) | Third-party mod list differs; fix the mods or set `ExtensionsMismatchIsWarning=true` to only log it. |
| `SessionFull` | `Net.MaxPlayers` reached (default 8, Boot: restart to change). Kick someone or raise it. |
| `AuthFailed` / `NameTaken` | Wrong join password; the name is bound to another player's key (Players page). |
| `RoleUnavailable` | A second authority tried to connect; only one X4 instance is the authority. |
| Several test bots from one PC get refused or reset | `MaxConnectionsPerIp` (default 4) or the per-IP failed-auth/violation limits; raise `X4MP__Net__MaxConnectionsPerIp`. A temp IP ban after protocol violations lasts `Net.TempBanMinutes` (5). |
| GUI says "GUI not available" | The exe was published with `SkipWebBuild` and no `server/web/dist`. Publish again without it. |
| Players join but nothing moves | Is the authority connected and the session started (Sessions page)? Check Diagnostics for drops and the log for `AuthorityLost`. |

When asking for help, include `x4mp-server version`, the latest `logs/server-*.log`, and the Diagnostics page state.
