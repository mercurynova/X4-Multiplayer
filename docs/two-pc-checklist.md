# Two-PC acceptance checklist

This file is the working sheet for a sitting on two machines. It is split by mechanic, and every mechanic has the same set of fields: what is checked, what has to be prepared, the steps, the expected result as a line that must be quoted, where to look and what to attach when it does not match.

The step-by-step scenario for starting the stand lives separately in `docs/in-game-session-9.md` — this file is only the mechanic checks. One rule: a step is passed when the line has been quoted from the log. "It looked right" is not evidence.

Roles on the stand:

- **PC A** — the authority and the server. The session save also lives here.
- **PC B** — the client. It stores nothing; everything comes from A.

## 0. Preparing the stand

**What is checked.** That both machines run the same code and that the ports between them are open.

**Preparation.** Both machines on the same network. A has a fresh server build, the mod is deployed on both.

**Steps.**

1. On both machines run `git rev-parse HEAD` — the hashes must match.
2. On both machines compare `extensions\x4mp\native\x4mp.dll` by size and timestamp — the files must be identical.
3. On A build the server: `dotnet build server\src\X4MP.Server -c Release -o out\win-x64 -p:SkipWebBuild=true`. The flag is mandatory on this machine: Node 18 does not build the React interface, and the server does not need it.
4. On A open the ports. The network profile on this machine is `Public`, so a rule with `-Profile Private` will not work:

```powershell
New-NetFirewallRule -DisplayName 'X4MP game tcp' -Direction Inbound -Protocol TCP -LocalPort 47780 -Profile Any -RemoteAddress LocalSubnet -Action Allow
New-NetFirewallRule -DisplayName 'X4MP game udp' -Direction Inbound -Protocol UDP -LocalPort 47781 -Profile Any -RemoteAddress LocalSubnet -Action Allow
New-NetFirewallRule -DisplayName 'X4MP api'      -Direction Inbound -Protocol TCP -LocalPort 47790 -Profile Any -RemoteAddress LocalSubnet -Action Allow
```

5. On B check reachability: `Test-NetConnection <A address> -Port 47780` — `TcpTestSucceeded : True`.
6. On A start the server with the environment:

```
X4MP__Net__NodeTcpEndpoint=0.0.0.0:47780
X4MP__Net__MaxPlayers=8
X4MP__Teams__JoinMode=AdminAssign
X4MP__Teams__DefaultRelation=Hostile
X4MP__Teams__AllowAssetTransfer=true
X4MP__Economy__TradeScope=Anyone
X4MP__Economy__TradeRequiresProximity=false
X4MP__Economy__CreditMode=PerPlayer
X4MP__Admin__AllowRemote=true
```

`CreditMode=PerPlayer` is only needed for the pool command checks in section 10. If the stand runs in shared mode, the commands `/mp deposit` and `/mp withdraw` must refuse, and that is a check too.

**Expected result.** `git rev-parse HEAD` matches on A and B; `Test-NetConnection` gives `TcpTestSucceeded : True`; `server.out.log` has the start-up line and `Now listening on: http://0.0.0.0:47790`.

**Where to look.** The console and `server.out.log` on A, `x4mp.log` on both machines. The full mod log path is `%LOCALAPPDATA%\X4MP\logs\x4mp.log`, that is `C:\Users\<name>\AppData\Local\X4MP\logs\x4mp.log`; this is not the `Documents` folder. `x4mp.json` and the settlement journal `settlement.json` sit next to it.

**If it does not match.** Attach the output of `git rev-parse HEAD`, the sizes of `x4mp.dll` from both machines, `Get-NetFirewallProfile` and the tail of `server.out.log`.

## 1. Joining and the session roster

**What is checked.** That B joins A's session, sees the roster, and that the chat commands answer.

**Preparation.** The stand from section 0, a session save loaded on A, A joined as authority.

**Steps.**

1. On A, in the Join screen, enter the server address, the name, the authority role and the admin password, then press Connect.
2. On B, in the Join screen, enter the same address and your own name, then press Connect.
3. On A, in the admin interface, assign both players to teams — the `AdminAssign` mode does not assign anyone by itself.
4. On both machines type `/who` in chat.
5. On both machines type `/ping` in chat.

**Expected result.** In `server.out.log` on A: `player 1 (...) joined as Authority, Client, Admin`, then `player 2 (...) joined as Client`, then `session <id>: ... -> Running (first checkpoint stored)` and `teams: applied seq=1 slots=2 relations=...`. In the chat on both machines `/who` shows two player rows, `/ping` answers with a line of the form `Пинг: <N> мс, сервер <адрес>, состояние ingame`.

**Where to look.** `server.out.log` on A, the chat window and `x4mp.log` on both machines.

**If it does not match.** Attach the tail of `server.out.log`, the `/who` and `/ping` answers from both machines and `x4mp.log` from the moment of joining.

## 1a. The server starts from the game

**What is checked.** That the server starts and stops from the menu, without a console, and that the authority joins that same server.

**Preparation.** No `x4mp-server` running, ports 47780/47781/47790 free, the extension built with `-WithServer`.

**Steps.**

1. In game, open the X4MP window and on the main screen press «Выделенный сервер» (dedicated server).
2. Press «Запустить сервер» (start server) and wait for the state «работает» (running).
3. On the screen check the pid, both ports, the data folder, the log and the admin password.
4. Go back to the Join screen, enable host and confirm that the admin password filled itself in, then press Connect.
5. Press «Остановить сервер» (stop server).
6. Start the server again and close the game.

**Expected result.** After step 2, `%LocalAppData%\X4MP\logs\server-console.log` contains `Database ready at schema version 8`, `Now listening on: http://0.0.0.0:47790` and `47780`; in `server.out.log` at step 4 — `admitted <имя> (player 1) as Authority, Client, Admin` and `-> Running (first checkpoint stored)`; at step 5 both ports close and there is no `x4mp-server` process; at step 6, after the game exits, there is no `x4mp-server` process either, because `auto_stop` is on.

**Where to look.** The «Выделенный сервер» screen, `%LocalAppData%\X4MP\logs\server-console.log`, the process list, `x4mp.log` with the line `[host] server: starting pid=... port=47780`.

**If it does not match.** If the state is `exited` straight away with code `-1073740286`, the extension picked up a build with ReadyToRun: rebuild the bundle with `-p:PublishReadyToRun=false` and deploy again. If the authority gets `authentication failed`, the screen must show the same password as in `<data folder>\admin-password.txt`.

## 2. The NPC ghost ship and its destruction

**What is checked.** That an NPC ship created on A is visible on B and that a hit from B kills it on A with no divergence.

**Preparation.** Both players in the same system, the NPC ship spawned on A.

**Steps.**

1. On A spawn an NPC ship and wait until it appears in the entity roster.
2. On B find the ship visually and confirm that it moves the same way as on A.
3. On B attack the ship until it is destroyed.
4. Wait for the ship to disappear on both machines.

**Expected result.** On A in `x4mp.log`: `authority: net <N> killed by player <id> (name ...)`. On B the disappeared-ghost counter increases within 500 ms, and the ship vanishes from the world on both machines.

**Where to look.** `x4mp.log` on A and B, the HUD ghost counters.

**If it does not match.** Attach the `authority: net ... killed` line from A, the tail of `x4mp.log` from B, and state after how many seconds the ship disappeared.

## 3. Death and return without a duplicate ghost

**What is checked.** That after a player dies and reconnects a second ghost ship does not appear.

**Preparation.** The stand from section 1, both players in game.

**Steps.**

1. On B destroy your own ship and wait for the death.
2. On B reconnect to the session.
3. On A and B count B's ships in the world.

**Expected result.** On A `x4mp.log` has one line about the player leaving and one about the return, the world has exactly one ship of B; on B the ghost counter does not grow after the return.

**Where to look.** `x4mp.log` on both machines, the player roster.

**If it does not match.** Attach the join and leave lines from A and the ghost counter from B before and after the reconnect.

## 4. Selling 500 units of a ware

**What is checked.** That a cargo sale made on B is applied on the authority and that the station stock adds up.

**Preparation.** B has cargo, the station has demand for that ware.

**Steps.**

1. On B sell 500 units of the ware.
2. On A wait for the line about the application.
3. On both machines compare the station stock and the cargo hold.

**Expected result.** On A in `x4mp.log`: `authority: trade at net <N> ware '<ware>' x500 applied to component <id> (applied=... deferred=... dropped=... unknown=... stock ops=...)`, where `applied` is not zero and `dropped=0`. In the same session `[perf] stats: ... mod_p95=... mod_max=...` — `mod_max` stays below 2 ms.

**Where to look.** `x4mp.log` on both machines, the station trade window.

**If it does not match.** Attach the `authority: trade ...` line from A and `[perf] stats` from both machines.

## 5. A station built by the client

**What is checked.** That a station built on B gets its identifier on A and stays the same entity on both machines.

**Preparation.** B has a construction ship or the right to build.

**Steps.**

1. On B start and finish building a station.
2. On A wait for the binding line.
3. Compare the position and the owner of the station on both machines.

**Expected result.** On both machines in `x4mp.log`: `builds: station '<macro>' bound to net <N> (component <id>)`, the identifiers match, the station stands at one point.

**Where to look.** `x4mp.log` on both machines, the map.

**If it does not match.** Attach both `builds: station ...` lines and map screenshots with coordinates.

## 6. Capturing a ship and reloading the save

**What is checked.** That a capture made on B reaches the checkpoint and survives a reload.

**Preparation.** A capturable ship near B.

**Steps.**

1. On B capture the ship.
2. On A wait for the checkpoint.
3. On A reload the session save and join again.
4. Check the ship owner on both machines.

**Expected result.** On both machines the line `capture: ...`, on A `authority: checkpoint <id> stored`; after the reload the ship belongs to the same team as before.

**Where to look.** `x4mp.log` on both machines, `server.out.log` on A.

**If it does not match.** Attach the `capture:` and `checkpoint ... stored` lines, plus the ship owner before and after the reload.

## 7. Player damage to an NPC

**What is checked.** That damage dealt to an NPC on B reaches the authority. This is a stretched check: if it fails, acceptance is not blocked, but the result has to be recorded.

**Preparation.** An NPC ship within B's sight.

**Steps.**

1. On B deal damage to the NPC without killing it.
2. On A look at the state of the same ship.

**Expected result.** The hull value on A dropped by the same amount; `x4mp.log` on A has a line about the applied damage.

**Where to look.** `x4mp.log` on both machines, the ship information.

**If it does not match.** Attach the ship hull on both machines before and after and the log tails.

## 8. Team relations: red highlight, ally and owner transfer

**What is checked.** That hostile teams see each other in red, allied ones within two seconds, and that an admin can transfer an owner only in `admin_only` mode.

**Preparation.** Two players in different teams, `DefaultRelation=Hostile`.

**Steps.**

1. On A and B look at the colour of each other's markers.
2. On A, through the admin interface, move B to an allied team.
3. Measure after how many seconds the marker on A stopped being red.
4. On A try to transfer the ship owner to the other player.

**Expected result.** Before the move both markers are red; after it both markers are allied within two seconds; the owner transfer goes through only with `admin_only` and is refused otherwise, with a refusal in the log.

**Where to look.** The screen, `x4mp.log` on both machines, `server.out.log` on A.

**If it does not match.** Attach the time of the move and the time of the colour change, screenshots of the markers, the refusal line from the log.

## 9. Economy: latency, simultaneous purchases, repair after a save edit

**What is checked.** That the balance catches up with the server within a second, that two simultaneous purchases in shared mode do not create money, and that a hand edit of the save is corrected within 30 seconds.

**Preparation.** `CreditMode=Shared` on the server, both players in game.

**Steps.**

1. On A and B at the same time buy the same ware for an amount close to the remaining balance.
2. Compare the balance on both machines.
3. On A change the balance in the save by hand and reload the save.
4. Wait for the reconciliation and compare the balance again.

**Expected result.** The balance on both machines is the same and comes back to normal within one second; with simultaneous purchases one goes through and the second is refused with the reason `InsufficientFunds`, the total balance does not grow; after the manual edit the balance returns to the ledger value within 30 seconds.

**Where to look.** The wallet HUD block on both machines, `x4mp.log`, `server.out.log` on A.

**If it does not match.** Attach the balances before and after each step from both machines and the reconciliation lines from `server.out.log`.

## 10. Economy between players: transfer, pool, loan, trade

**What is checked.** That a transfer moves two wallets, that the pool commands work in `PerPlayer` mode, and that a loan and a trade finish with an answer from the other side.

**Preparation.** Both players in the same team. For the pool commands the server is started with `Economy:CreditMode=PerPlayer`.

**Steps.**

1. On B type `/mp send <A name> 10000 проверка`.
2. On both machines type `/mp` and compare the wallets.
3. On B type `/mp deposit 50000`, then `/mp withdraw 20000`.
4. On A type `/mp loans` and `/mp repay <id> 5000`.
5. On A offer a trade, on B type `/mp trades`, then `/mp accept <id>`.
6. On both machines look at the `[economy_client]` line in the log.
7. On B type `/mp ledger`, then `/mp ledger more`.
8. Open the economy window on B and press «Загрузить журнал» (load ledger), then «Более старые записи» (older entries).
9. On A offer a loan while B is in game and see what B sees with no windows open.

**Expected result.** After the transfer B is down 10000, A is up 10000, and both machines show the same total; `/mp` prints the line `Экономика: <режим>, кошелёк <сумма>, долг пула <сумма>`; in `PerPlayer` mode the deposit and the withdrawal go through, in shared mode they must return `EconomyStatus::Rejected` with the reason `NotApplicableInSharedMode`; after `/mp repay` the loan changes state to `repaid`; after `/mp accept` the trade reaches `completed`. In the log on both machines: `[economy_client] requests=<N> sent=<N> sendfail=0 results=<N> rejected=<N> loans=<N> trades=<N> trade_results=<N> ledger_pages=<N> bad=0 notices=<N> self=<id> linked=<N>`.

**Ledger and notices.** `/mp ledger` asks for a page and, when it arrives, prints `Журнал, сначала новые` and one line per transaction of the form `12:04  Перевод  +10 000, от игрока 2`; the lines run from newest to oldest, a rolled-back transaction is marked `сторнировано`, and if the server said there are older records, `Есть более старые записи: /mp ledger more` is added. `/mp ledger more` asks for the next page by the cursor of the oldest line, repeating nothing. The economy window shows the same lines, the «Загрузить журнал» button asks for a fresh page, «Более старые записи» for the next one, and it is active only when the server said there are some. A loan or trade offer from A reaches B with no windows open: a toast, the line `Новое предложение займа ...` or `Новое предложение сделки ...` in the chat, the hint `Ответ: /mp accept <id> или /mp decline <id>` and a counter on the HUD badge `ждут ответа: <N>`; a repeat of the same offer does not pop up a second time. If the session drops exactly at the moment of `/mp ledger` and `x4mp.log` shows `invalid inbound frame: UnknownMessageType`, the machines run different builds: compare the timestamp of `extensions\x4mp\native\x4mp.dll` on both.

**Where to look.** The chat window and the HUD on both machines, `x4mp.log`, `server.out.log` on A.

**If it does not match.** Attach the output of all `/mp` commands from both machines, the `[economy_client]` line and the tail of `server.out.log`.

## 11. Settlement on the authority: a ship for credits

**What is checked.** That the "ship for credits" exchange either completes entirely or does not happen at all: both sides frozen, the ship changes owner, the credits move, nothing is left over.

**Preparation.** A has a ship, B has credits; both in the same system; `AllowAssetTransfer=true`, `TradeScope=Anyone`, `TradeRequiresProximity=false`.

**Steps.**

1. On A offer a trade: give the ship, receive the credits.
2. On B accept the trade via `/mp trades`, then `/mp accept <id>`.
3. On both machines look at the ship owner and the balances.
4. On A look at the `[settlement]` line in the log.

**Expected result.** The ship belongs to team B, the credits are debited from B and credited to A, the escrow is empty. In the log on A: `settlement: order <id> line 0 component <id> '<старый владелец>' -> '<новый владелец>'`, then `settlement: trade=<id> applied=1 compensated=0 state=applied` and the summary line `[settlement] orders=1 applied=1 compensated=0 refused=0 needs_admin=0 queries=<N> duplicates=0 sendfail=0 bad=0`.

Important: the owner changes to a team, not to a player. The ship goes to team B; there is no personal ownership in this version.

**Where to look.** `x4mp.log` on A, `server.out.log` on A, the trade window on both machines.

**If it does not match.** Attach all `settlement:` lines from A, the `[settlement]` line, the ship owner before and after and the balances of both sides.

## 12. Rollback on a mid-way failure

**What is checked.** That when the second operation fails, the first is cancelled and nothing is left over.

**Preparation.** This failure cannot be reproduced by hand on two real PCs: the server has no setting for an artificial refusal, that injection exists only in the auditor.

**Steps.** There are no manual steps. The evidence comes from two sources:

1. Mod unit tests: `x4mp_host_tests.exe "[settlement]"` — seven cases, including the rollback and the journal write.
2. Auditor: a run of `--trade --trade-fail 10 --trade-timeout 5 --verify` on FakeNode, where the invariants show `trades=[...RolledBack=...]` and `ledger-sum=0`.

**Expected result.** `All tests passed (69 assertions in 7 test cases)`; in the auditor invariants `RolledBack` is greater than zero and `ledger-sum=0`.

**Where to look.** The test output and the auditor report, section 4.5 in `docs/m5-exit-report.md`.

**If it does not match.** Attach the test output and the auditor invariants.

## Summary table

| Section | Mechanic | Acceptance | Result | Evidence line |
|---|---|---|---|---|
| 0 | Stand preparation | — | | |
| 1 | Joining and the session roster | S0 | | |
| 2 | NPC ship and destruction | AC 2, 3 | | |
| 3 | Death and return without a duplicate | AC 4 | | |
| 4 | Selling 500 units | AC 5 | | |
| 5 | Station built by the client | AC 6 | | |
| 6 | Capture and reload | AC 7 | | |
| 7 | Damage to an NPC | AC 8 | | |
| 8 | Team relations | AC 9 | | |
| 9 | Economy and reconciliation | AC 10 | | |
| 10 | Economy between players | AC 11 | | |
| 11 | Settlement on the authority | AC 12 | | |
| 12 | Rollback on failure | AC 12 | | |
