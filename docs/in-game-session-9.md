# Session 9 run sheet: the two-node acceptance (M5-33)

Date: prepared 2026-10-30, to be executed on two PCs. Criteria: [roadmap.md](roadmap.md) M5 row,
[mod-design.md](mod-design.md) section 9 M5 items 2-12, [m5-plan.md](m5-plan.md) W8. Results go into
[m5-exit-report.md](m5-exit-report.md) section 2 and a new `docs/in-game-session-9.md` once the run is done.

This sheet exists because session 8 ran **one game node plus the server**. Everything that needs a second
node is listed here as a numbered step with the action, the log line that proves it and where to copy it
from. A step is only PASS when the line is quoted; "it looked right" is not evidence.

## 1. Machines and roles

| | PC A (host) | PC B (client) |
|---|---|---|
| Role in the session | **authority** (holds the save, applies stock, owns NPCs) | **client** |
| Server | runs here (`x4mp-server.exe`) | connects to PC A:47780 |
| X4 | 9.00 with the same DLC as PC B | same build, same DLC |
| Mod | same `x4mp.dll` and `extensions\x4mp` tree | identical (compare `x4mp.dll` SHA-256) |

Both PCs: X4 **Settings > Extensions > Protected UI Mode OFF**, and the extensions **X4Native** and
**X4 Multiplayer** enabled. The mod refuses a mismatched protocol or manifest, so the two builds must come
from the same `git rev-parse HEAD`.

## 2. Prepare PC A (do this before the session)

1. **Build the server fresh.** The `out\win-x64\x4mp-server.exe` in the tree is from 2026-10-20 and predates
   the seeder fix (section 8.8 of the session-8 record), so rebuild:
   ```powershell
   dotnet build server\src\X4MP.Server -c Release -o out\win-x64
   Get-Item out\win-x64\x4mp-server.exe | Select-Object LastWriteTime   # must be today
   ```
   Stop any running `x4mp-server` first, or the build fails with MSB3021/MSB3027.
2. **Build and deploy the mod** (`mod\build.ps1 -Config relwithdebinfo`, then
   `mod\tools\deploy.ps1`). After every deploy, re-comment the standalone screen in the **installed**
   `extensions\x4mp\ui.xml:28` - the deploy copies the repo file, which has it enabled, and the enabled
   module crashes the save load:
   ```powershell
   $p = 'D:\Steam\steamapps\common\X4 Foundations\extensions\x4mp\ui.xml'
   $t = [System.IO.File]::ReadAllText($p)
   $t = $t.Replace('<file name="ui/x4mp_ui_standalone.lua" />', '<!-- <file name="ui/x4mp_ui_standalone.lua" /> -->')
   [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($false)))
   ```
   Do not use `Set-Content`: PowerShell 5.1 writes ANSI and X4 needs UTF-8.
3. **Open the firewall** (elevated, LAN profile):
   ```powershell
   New-NetFirewallRule -DisplayName "X4MP game TCP" -Direction Inbound -Protocol TCP -LocalPort 47780 -Profile Private -Action Allow
   New-NetFirewallRule -DisplayName "X4MP game UDP" -Direction Inbound -Protocol UDP -LocalPort 47781 -Profile Private -Action Allow
   New-NetFirewallRule -DisplayName "X4MP admin"    -Direction Inbound -Protocol TCP -LocalPort 47790 -Profile Private -RemoteAddress LocalSubnet -Action Allow
   ```
   `Get-NetIPAddress -AddressFamily IPv4 | Where-Object InterfaceAlias -notmatch 'Loopback'` gives the LAN
   address PC B types into the Join dialog.
4. **Start the server** with the settings this session needs:
   ```powershell
   $env:X4MP__Net__NodeTcpEndpoint = '0.0.0.0:47780'
   $env:X4MP__Net__MaxPlayers = '8'
   $env:X4MP__Teams__JoinMode = 'Lobby'
   $env:X4MP__Teams__AllowCreateInLobby = 'true'
   $env:X4MP__Teams__AutoAssign = 'Balance'
   $env:X4MP__Teams__DefaultRelation = 'Hostile'
   $env:X4MP__Teams__AllowAssetTransfer = 'true'
   $env:X4MP__Economy__TradeScope = 'Anyone'
   $env:X4MP__Economy__TradeRequiresProximity = 'false'
   $env:X4MP__Admin__AllowRemote = 'true'     # default is already true; set it only if something turned it off (no TLS on the GUI)
   out\win-x64\x4mp-server.exe --data-dir C:\X4MP\session9 --port 47790
   ```
   The first start prints and writes `<data-dir>\initial-admin-password.txt`; sign in once on
   `http://<LAN address>:47790/` and change it (12+ characters). `GET /healthz` must answer `status: ok`.
   Without the three `Teams`/`Economy` trade settings every trade proposal is refused with `ScopeDenied`
   ([m1-exit-report.md:19-20](m1-exit-report.md)).

## 3. Prepare PC B

1. Copy `extensions\x4mp` and `extensions\x4native` from PC A (or run the same deploy there), enable both
   extensions, Protected UI Mode OFF, and re-comment `ui.xml:28` if the deploy ran there.
2. Start X4. In the X4MP menu choose **Join a server**: address `<PC A LAN IP>:47780`, the player name,
   team `auto` (or the team the step asks for), role **client**, and the **admin password only if this
   player should be authority** - a normal client leaves it empty.
3. The client downloads the session save in band (M2) and loads it; the menu shows the server, the role and
   the ping. `x4mp.log` on PC B must show `session ... -> Running`.

## 4. Steps, one per acceptance item

Run them in this order. Between steps, keep both logs open; every step names the line to quote.

### S0 - the session is up

| Where | Line |
|---|---|
| A + B | `[X4MP] chat: wrappers installed / installed / installed` in the dev log |
| A | `admitted Admin (player 1) as Authority, Client, Admin` in `server.out.log` |
| B | `admitted <name> (player 2) as Client` |
| A + B | `[sess] onfoot: ... roster=2` in `x4mp.log` |
| A | `teams: applied seq=1 slots=2 relations=...` |

`/who` in the chat window on both PCs must list both players (M5-28), `/ping` must show the latency.
This is also the first half of AC 9 (relations applied).

### S1 - AC 2: a client kills an NPC ghost

1. On A, fly to a sector with NPC traffic and note the NPC. On B, be in the same sector and able to see it.
2. B kills the NPC with the player's guns.
3. Within 500 ms the ghost must be gone (exploded) on A and on B.

Quote: on A `authority: net <N> killed by player <id> (name ...)`; on B the ghost despawn counter in
`ghosts:` (or `[kill]` if the client sink logs the applied event). The 500 ms is measured from the
`Intent` timestamp in the server log to the despawn line - if it is not in the log, record the frame
numbers instead and say so.

### S2 - AC 2b: an NPC killed by NPCs on the authority

On A, let the local NPCs fight and kill one; the ghost must disappear for both. Quote the same authority
line with no player id, plus the despawn on B.

### S3 - AC 3: death and rejoin

1. B dies (e.g. ram an asteroid). Its ghost must disappear on A within a second.
2. B reloads the game and rejoins the same session.
3. No duplicate ghost: A's `ghosts:` counters show one ghost for that player, not two.

Quote on A the removal line and then `ghosts: init (restored ...)` / the roster line after the rejoin.

### S4 - AC 4: sell 500 units, stock +500 within 10 s, no frame over 2 ms

1. B docks at a station A also sees and sells 500 units of one ware.
2. On A the station stock rises by exactly 500 within 10 s.

Quote on A:
`authority: trade at net <N> ware '<ware>' x500 applied to component <id> (applied=... deferred=... dropped=... unknown=... stock ops=...)`
and on A + B `[perf] stats: ... mod_p95=... mod_max=...` - `mod_max` must stay below 2 ms.

### S5 - AC 5: a client-built station

1. B builds a station (or completes a build). 2. The station appears on A and on B at the same position,
with no duplicate on B.

Quote `builds: station '<macro>' bound to net <N> (component <id>)` on both, and the spawn/position line.

### S6 - AC 6: capture, and the capture survives a save/load

1. B boards and captures a ship. The owner changes on A and B.
2. A takes a checkpoint save, both reload it, and the ship is still under the capturing player's faction.

Quote `capture: ...` on both and `authority: checkpoint <id> stored` on A, then the post-load owner line.

### S7 - AC 7 (stretch): NPC damage to a client player

On A, let NPCs attack B's ship/player; B's hull must drop from the relayed damage. Quote the damage
lines on both and the hull value before/after.

### S8 - AC 9: hostile and allied relations, and an `admin_only` team switch

1. With two teams hostile, each side's targeting must be red on **both** screens.
2. An admin flips the relation to allied; it must take effect on both nodes within 2 s.
3. With `X4MP__Teams__JoinMode=AdminAssign` (restart the server with it - this is the admin-only mode the
   AC calls `admin_only`), an admin moves a player to the other team in the GUI; the avatar's owner must
   move with it (`ReassignPlayerAssets`).

Quote `teams: apply seq=<n> ... relations=<n>` on both, with the timestamps of the two lines (the delta is
the 2 s), and on A the reassign line from the server log.

### S9 - AC 10: the wallet on two nodes

1. B buys something for about 1M Cr; the ledger shows it within 1 s (watch the admin GUI or
   `GET /api/v1/economy/transactions`).
2. Two purchases at the same moment in Shared mode converge within 2 s.
3. Force a local edit on B (admin adjust or a cheat) and it must be corrected within 30 s.

Quote the `[wallet]` line on B (`local=... server=... target=... writes=... acked=...`), the ledger rows,
and the server's `WalletUpdate`/`CreditDelta` lines.

### S10 - AC 11 and 12

Blocked until M5-29 (economy screen) and M5-31 (mod-side escrow) land. The server halves are proven: AC 12
by the 60-minute auditor run ([m5-exit-report.md](m5-exit-report.md) section 4.5), AC 11's wallet/pool by
the W6 loop. Do not mark them PASS on this run.

## 5. What to send back

From **both** PCs, after the last step:

```powershell
# the mod log (events, wallet, ghosts, teams, perf)
Copy-Item "$env:LOCALAPPDATA\X4MP\logs\x4mp.log" "\\PC-A\share\session9\pcB-x4mp.log"
# the Lua dev log
Copy-Item 'D:\Admin\Documents\Egosoft\X4\<id>\x4mp_dev.log' ...
# the server console log and the ledger
Copy-Item 'C:\X4MP\session9\server.out.log' ...
```

Plus, on A, a ledger dump (`ledger_tx`, `ledger_entries`, `wallets` for the session) and the audit answer
`POST /api/v1/economy/audit` -> `{ "ok": true, "violations": [] }`.

Then fill a copy of this table into `docs/in-game-session-9.md`:

| AC | Verdict | Quoted line | File |
|---|---|---|---|
| 2 | | | |
| 3 | | | |
| 4 | | | |
| 5 | | | |
| 6 | | | |
| 7 | | | |
| 9 | | | |
| 10 | | | |
| 11 | blocked (M5-29) | | |
| 12 | blocked (M5-31) | | |

## 6. Traps that cost time in session 8

- The save in the tree carries **220000** Cr for the authority (the pre-fix seeder doubled it). The fix stops
  further growth but does not roll the number back; do not read that as a new defect.
- The mod log timestamps are **UTC** (`Z`); the dev log is local time.
- The console renders Cyrillic and `§` as mojibake. The files are UTF-8; check with
  `[System.IO.File]::ReadAllBytes` + `Encoding.UTF8.GetString`, not with the console.
- A deploy while X4 runs is fine (`merging over the running installation`), but a changed Lua file needs
  `/reloadui` or a restart; a changed DLL needs a restart.
- If the client cannot join: check `Test-NetConnection <PC A IP> -Port 47780`, then the server's
  `admitted`/`refused` lines, then the mod version (`manifest` mismatch is refused with the exact lists).
