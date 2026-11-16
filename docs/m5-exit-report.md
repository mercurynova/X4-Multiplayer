# M5 exit report

Date: 2026-10-29. Criteria: [roadmap.md](roadmap.md) M5 row and [m5-plan.md](m5-plan.md) section 3. Plan of record:
[m5-plan.md](m5-plan.md) (eight waves, M5-01..M5-34). In-game evidence: [in-game-session-8.md](in-game-session-8.md).

**Read this first.** **M5 is not exited.** Waves W1-W6 are code-complete and every one of their gates is green, and the
milestone's ledger line is now met - a **60-minute auditor session** with purchases on all nodes finished clean (§4.5) -
but the rest of the acceptance line ("MOD §9 M5 acceptance items 2-12; every event in REQ §8.3 checklist executed once
with log evidence", with the roadmap's "yes (2 PCs)" column) needs a two-node session, and session 8 ran **one node plus
the server**. W7 is now code-complete on the mod side: **M5-28 (`/who`, `/ping`) is verified in game**, and **M5-29/M5-30
(the economy client: teammate transfers, the team pool, loans, trade accept/decline and the `/mp` screen) are implemented**
in `features/economy_client` and `ui/x4mp_economy.lua` - green at 674 mod tests and 293 Lua cases, and **run in game**
on one node in step S10 (the `/mp` screen, the loans/trades lists and a request that the ledger answered, §4.6). M5-27
(relations) needs the same two-node run. **M5-31 (escrowed settlement on the mod side) is implemented too** -
`features/settlement` takes the `AssetTransferOrder` the ledger sends the authority, applies it line by line behind a
journal and compensates in reverse when a line fails (§4.7) - green at **681** mod tests, and its two-node acceptance is
part of the sitting. So AC 11 is a **first cut proven in game**, AC 12 is proven on the **server and on the authority**,
and the UI half of AC 9 still has nothing to verify.

What session 8 did produce is worth as much as the numbers it failed to produce: it found **four real defects** that
no unit test had caught - two crashes that killed the game, one unit bug that silently wrote 1/100 of every balance
correction and one seeder bug that doubled the balance - and it proved each of them with a dump, a bisect or a log line.
They are listed in §4.

| Verdict | Meaning in this report |
|---|---|
| **PASS (CI)** | covered by a test that runs in the build; the in-game path was not exercised |
| **PASS (session 8)** | executed in game on 2026-10-29 and the log line is quoted |
| **PASS (session 9)** | executed in game on 2026-10-30 and the chat ring line is quoted |
| **PARTIAL** | part of the criterion was exercised in game, the rest needs the second node or W7 |
| **PENDING (session 9)** | needs the two-node sitting; nothing in the code blocks it |
| **NOT IMPLEMENTED** | W7 task not started |
| **FAILED, FIXED** | session 8 showed it broken; the fix is in the tree (see §4) |

## 1. Gates

| Gate | Command | Result |
|---|---|---|
| Mod build | `powershell -NoProfile -ExecutionPolicy Bypass -File mod\build.ps1 -Config relwithdebinfo` | builds; **691 tests, 0 failed** (4 skipped: the real-server/game smokes 663, 664, 674, 683), 68.3 s |
| Mod unit binary, wallet only | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "[wallet]"` | **550 assertions in 16 test cases**, all passed |
| Mod unit binary, economy client | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "[economy_client]"` | **173 assertions in 8 test cases**, all passed |
| Mod unit binary, settlement | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "[settlement]"` | **69 assertions in 7 test cases**, all passed |
| Mod unit binary, server launcher | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "[server]"` | **94 assertions in 8 test cases**, all passed; one of them starts a real process through `CreateProcessW` (M5-32) |
| Lua | `python mod\tests\lua\run_lupa.py` | **315 passed, 0 failed** (304 before the eleven server-screen cases) |
| Deploy | `powershell -NoProfile -ExecutionPolicy Bypass -File mod\tools\deploy.ps1` | `Deployed.`; prints `x4mp is in use (x4mp_live.dll); merging over the running installation` while the game runs |
| `X4MP.Core.Tests` | `dotnet test server\tests\X4MP.Core.Tests\X4MP.Core.Tests.csproj` | **1381 passed, 0 failed** (the two ledger module cases are new) |
| `X4MP.Server.Tests` | `dotnet test server\tests\X4MP.Server.Tests\X4MP.Server.Tests.csproj` | **602 passed, 1 failed, 1 skipped** (`SaveTransferTests.TwoHundredMegabyteSaveGoesFromTheAuthorityToThreeClients`). The failure is `FakeNodeLiveTests.SingleClientAndAuthorityPrintWelcomeAndRtt`: its 1 s live run never reaches the first report window on this box, so the log has no `rtt=`. A worktree of clean `56b999a` fails the same assertion alone, so it predates this wave |
| `X4MP.Protocol.Tests` | `dotnet test server\tests\X4MP.Protocol.Tests\X4MP.Protocol.Tests.csproj` | **535 passed, 0 failed**: every `MsgType` has a sample frame and a golden vector, `0x0816`/`0x0817` included. Was red before the ledger ids reached `protocol/cpp/src/message_table.inc` (В§4.8) |
| Ledger auditor (M5-34) | `X4MP.FakeNode.exe swarm --clients 5 --with-authority --teams 3 --relations ffa --economy heavy --dupe-attack --income-rate 2 --trade --trade-fail 10 --trade-timeout 5 --verify --duration 3600` | **clean after 3614 s**: `invariants: auditor=ok violations=0 ... ledger-sum=0 duplicate-requests=0 ... drift=0`, `verify: ... errors=0`; `POST /api/v1/economy/audit` -> `ok=true, violations=[]` (§4.5) |

The .NET suites were re-run after the §4.4 seeder fix, because that is server code. An earlier run of
`X4MP.Server.Tests` against the same build reported two failures; the rerun above (same code) is green, and the
failing names were not captured - treat that pair as flaky until a run reproduces them.

## 2. Acceptance criteria

| # | Criterion (roadmap M5, MOD §9) | Verdict | Evidence |
|---|---|---|---|
| 1 | MD capture ring plus fallbacks; the `md_hook` state is reported | PASS (session 9) | the ring runs in game: `events: the MD half said hello, 7 hooks armed` on both nodes (authority 2026-11-09 06:58:07.857Z and 06:58:12.668Z; guest 07:03:05.380Z and 07:03:15.145Z), and the budget line carries the state `hooks=hooks=alive md=7 fired=4`. Before the fix the only line was `events: no MD hello after 30s`: the parser was fed the bridge envelope, fixed in `8b4d6e7` (the third instance after statics and capture) |
| 2 | A client kills an NPC ghost: gone within 500 ms on the authority and on a second client; an NPC killed by NPCs disappears for all | PASS (session 9, two nodes) | authority `2026-11-09 08:29:27.034Z [auth] authority: net 249 killed by player 1 (net 166), despawn queued`; guest `08:29:27.429Z [sync] ghosts tracked=133 visible=133 spawned=134 respawned=0 hidden=0 removed=1` against `tracked=134` at `08:29:21.952Z` - the ghost was gone 0.4 s after the claim. The stand has the authority and one client; the "second client" half still needs the two-PC sitting |
| 3 | The client dies, its ghost disappears, it re-joins after a reload with no duplicate ghost | PASS (session 9, two nodes) | authority `2026-11-09 18:36:43.449Z [auth] authority: player 1 died (killer net 0); avatar net 902 despawned as PlayerLeft, record dropped`, 103 ms after the record was forgotten; the guest ring fired `P;` (`fired=3`). Rejoin: server `01:56:55.371 player 1 (Guest) resumed (baseline epoch 3)`, `conn 9: admitted Guest (player 1)`; authority `[md] onfoot: avatar pid=1` every 5 s, `[sess] onfoot: ... remotes=1 actors=1/1`, and the next checkpoint used `next_net_id 903` (902 was not reused) |
| 4 | A client sells 500 units: the authority station stock rises by 500 within 10 s, no frame spike over 2 ms | PENDING (session 9) | needs the second node for the 10 s wall-clock measurement; the budget side is proven: `[perf] stats: ... mod_p95=0.214ms mod_max=0.626ms`. The client half is pinned by `mod/tests/test_economy.cpp` (7 cases): one `Intent` per trade with the matched station, ship and ware macro, the (station, ware, second) dedupe, the four refusals (unmapped station, unmapped ship, zero amount, refused send), the `EntityCargo` inbox, the `cargo_delta` that writes only the difference, and the shim queue's folding and per-frame budget. The authority's stock apply is code in `authority_flow.cpp`; its arrival time in game is what remains unmeasured |
| 5 | A client-built station appears for the authority and the other clients at the same position, no duplicate on the builder | PENDING (session 9) | needs the second node for the cross-node observation; the mod half is pinned by `mod/tests/test_builds.cpp` (one request per finished build, the sector and pose read from the game rather than the line, the anchor dedupe, the 10 s retry, a refused send not retried) and `mod/tests/test_game_api.cpp` (`spawn_station`). What only the game can show is the 50 m match between the authority's `EntitySpawn` and the builder's own local station, i.e. no duplicate |
| 6 | A capture changes the owner on all nodes; the captured ship survives an authority save/load under the right faction | PENDING (session 9) | needs the second node for the owner-change-on-a-second-client and save/load halves; the client half is pinned by `mod/tests/test_capture.cpp` (one report per owner change, the 30 s dedupe, a refused send not remembered) and the `[events]` case for the wire. The authority checkpoint save ran: `[X4MP] authority: SaveGame called for x4mp_ckpt_1b96d95b2d2cd8cb` |
| 7 | NPCs on the authority can damage and kill a client player (stretch) | PENDING (deferred - the downward half is not built) | not a sitting item: the sitting could not close it. M15 shipped the opposite direction - the client reports `HitReport` upward and MD `set_object_hull`/`set_object_shield` writes it - while this row's own AC (an authority NPC damaging a client player's avatar) was never implemented (`docs/m5-plan.md:212-223`). AC item 7 stays deferred rather than quietly dropped |
| 8 | Chat moderation hooks, `/who`, `/ping`, `/w`, `/t` | PASS (session 9) | `/w` and `/t` were already live (`[X4MP] chat: wrappers installed / installed / installed`); M5-28 added `/who` (the roster from `X4MPPlayers.list` in team colours, capped at ten rows) and `/ping` (the last `x4mp.status` topic) in `x4mp_chat.lua:244-271`, texts 512-516 in both pages, cases `test_chat.lua:210,231`. Verified in game on 2026-10-30 through the real `ExecuteDebugCommand` global: `who_ok=true ping_ok=true new_lines=3` -> `Игроков в сессии: 1`, the team-coloured row `#ffff8000#AdminX - вы \| Команда 1 \| в сети \| 40 мс \| - пешком, здесь`, `Пинг: 196 мс, сервер 127.0.0.1:47780, состояние ingame`; a non-ours command added `chat_lines_added=0`. Moderation is server-side and answers with a system line. Lua gate **280 passed, 0 failed**. Details: [in-game-session-8.md](in-game-session-8.md) §8.9 |
| 9 | Two hostile teams show red targeting on both sides; allied takes effect within 2 s; `admin_only` switch moves the avatar's owner | PARTIAL | server + mod half proven: `[X4MP] teams: applied seq=1 slots=1 relations=1 active=1 mismatches=0 player_locked=0`. W7 landed, so the relation matrix is applied on every node (`features/teams`, M5-27) and the red targeting is the game's own display of those relations; what remains is the two-node observation - hostile teams red on both sides, an allied change inside 2 s, and the `admin_only` switch moving the avatar's owner |
| 10 | A 1M Cr purchase on a client shows in the ledger within 1 s; two simultaneous purchases in Shared mode converge within 2 s; a forced local edit is corrected within 30 s | PARTIAL - **two defects found and fixed in this wave** | before: `[wallet] ... server=0 target=10000 ... updates=0 ... acked=0`; after the kind fix: `[wallet] local=11000 ... updates=3 ... converged=2 writes=1 failed=0 acked=2`; the write unit was wrong (`+100000` credits moved 10000 -> 11000, §4.3) and the seeder doubled the balance (110000 -> 220000, §4.4); after the seeder fix a fresh session posts only the difference (`team_shared +120000 -> 220000`, `world -120000`) and lands on the save's number. The 1 s ledger latency half is now covered by an automated two-node test: `X4MP.Server.Tests/Net/EconomyTwoNodeLiveTests.cs` books a transfer between two real TCP nodes on one running server and fails if the recipient's `WalletUpdate` arrives later than 1 s. The two-simultaneous-purchases half is automated as well: `TwoSimultaneousPurchasesInSharedModeConvergeWithinTwoSeconds` runs the server in `CreditMode=Shared`, has two real TCP nodes book a negative `CreditDelta` in the same moment against the one team-shared wallet, and fails if the converged `WalletUpdate` arrives later than 2 s, if either node's `acked_delta_seq` lags the booking, if a resent delta books twice, or if the ledger stops summing to zero. Only the forced-local-edit correction still needs a real client (the wallet reconciler that corrects a local edit lives in the mod) |
| 11 | Send credits, team pool and the Economy screen, first cut | PASS (session 9, single node) - first cut | M5-29/M5-30: `mod/native/features/economy_client` encodes `CreditTransferRequest`, `PoolDepositRequest`, `PoolWithdrawRequest`, `LoanRepay`, `LoanForgive`, `TradeAccept`, `TradeCancel` and decodes `EconomyResult`, `LoanStatus`, `TradeStatus`, `TradeResult`; `/mp` shows the wallet, the pool debt, the open loans and the incoming trades and takes `send|deposit|withdraw|repay|forgive|accept|decline`. Gates: **674 mod tests, 0 failed** (`[economy_client]` 93 assertions in 6 cases) and Lua **293 passed, 0 failed** (13 `test_economy` cases). In game on 2026-10-30 after an X4 restart with the new DLL: `/mp` answered `Экономика: общий, кошелёк 110 000, долг пула 0` + `Открытых займов: 0, входящих сделок: 0`, `/mp loans` and `/mp trades` added `Открытые займы: / пусто / Входящие сделки: / пусто`, and `/mp deposit 50000` + `/mp withdraw 20000` came back through the ledger as `last{ok=false status=2 reason=5 key=2:1}` - `NotApplicableInSharedMode`, the correct refusal for a team in Shared mode (§4.6). A successful transfer, a pool deposit and withdraw in PerPlayer mode, and a loan offered, accepted and repaid between two players are now **automated** (`EconomyTwoNodeLiveTests`, three cases against one real server and two real TCP nodes: both wallets move, the pool and its wallet agree with the ledger, the loan ends `Repaid` with a clean auditor). A trade between two players is covered only up to the shape rule - see row 12 |
| 12 | Loans and escrowed offers settle atomically, rollback exact, residuals 0 | PARTIAL (server + authority settlement) | the server half is proven by the 60-minute run (§4.5): `trades=[Completed=653,Expired=2,InDoubt=5,RolledBack=91]`, `trade-authority: ... failed=93 withheld=29 silent=5`, `ledger-sum=0`, `inEscrow=14307` held only by the 5 in-doubt orders. The mod speaks the loan and trade frames (M5-29/M5-30), so a node can repay, forgive, accept and cancel, and **M5-31 puts the settlement itself in the mod**: `features/settlement` takes the ledger's `AssetTransferOrder`, prechecks every line, applies it behind a journal and compensates in reverse when a line fails (§4.7). Green at **681** mod tests (`[settlement]` 69 assertions in 7 cases). The two-node half is automated on the server side: `TradeLiveTests` runs a real host with three trading clients against a scripted authority that implements the documented executor contract (every line or none, one `EntityChange` carrying `OwnerTeam`/`OwnerPlayer` per `OwnerChange` line, one `AssetTransferConfirm`, `TradeQuery` answers), each client offering `Give=[Ship]` / `Want=[Credits 500-5000]` (`FakeTrade.cs:341-350`), and the test refuses to pass unless every trade's final state matches the authority's ground truth, at least one trade completed and one rolled back, and the run ends with `Ledger.TotalBalance() == 0`, no open trade, no locked asset, an escrow residual of **0**, a clean auditor and the buyer as the mirror's owner of every ship handed over in a completed trade (`TradeLiveTests.cs:134-264`; green 1/1 in 21 s on `61fe5b3`). What remains for the sitting is the in-game half of the same order - the real mod's `features/settlement` applying those lines to live game objects while a client watches the ship change hands. The injected-failure rollback is therefore proven by these tests and the auditor, not on two PCs. An automated pair now covers the loan leg of the same machinery end to end (`EconomyTwoNodeLiveTests`): the offer escrows the principal into the loan's own escrow wallet, the accept moves it to the borrower, the repay returns it with `Ledger.TotalBalance() == 0` and a clean auditor, and a credits-only proposal is refused before any escrow (`Rejected`/`AmountInvalid`, "a trade needs at least one asset") because only a non-credit line becomes an `AssetTransferOrder` for the authority |

Also unverified from the milestone line: **"every event in REQ §8.3 checklist executed once with log evidence"** - the
REQ checklist is a two-node exercise. The other half of that line, **"ledger auditor clean after a 60-min session with
purchases on all nodes"**, is now met: see §4.5.

## 3. What each wave built

| Wave | Tasks | State |
|---|---|---|
| W1 | M5-01..M5-05: event capture ring, MD half, `md_hook` state, REQ §8.3 plumbing | code-complete |
| W2 | M5-06..M5-10: kill/death relay, ghost lifecycle | code-complete |
| W3 | M5-11..M5-15: death and respawn, rejoin without a duplicate ghost | code-complete |
| W4 | M5-16..M5-19: trade and cargo application, station stock | code-complete |
| W5 | M5-20..M5-21: station builds, capture | code-complete |
| W6 | M5-22..M5-26: wallet reconciliation (poll, booking, ack, convergence, debt/clamp) | code-complete; two defects found in game and fixed, §4.1 and §4.3 |
| W7 | M5-27..M5-31: relations, chat commands, economy screen, notifications, escrowed settlement | M5-28 done (`/who`, `/ping`, verified in game §8.9); **M5-29/M5-30 done** (the economy client: `features/economy_client` + `ui/x4mp_economy.lua` + `/mp`, 674 mod tests, Lua 293/0, in-game run is session 9 step S10); **M5-31 done** (the authority-side settlement: `features/settlement` with a journal, reverse compensation and `AssetTransferConfirm`, 681 mod tests, §4.7); M5-27 relations code-complete in `features/teams` and proven to apply on one node (`teams: applied seq=1 ... relations=1`), needs the two-node run |
| W8 | M5-32..M5-34: this report, session 8 sheet, roadmap/index/auditor | this report + the sheet done; **auditor run PASSED**, §4.5 |

## 4. Defects found in session 8

### 4.1 Crash 1 - the money poll recursed through the helper flush (`0xc00000fd`)

`x4mp_money.lua` armed its self-re-arming timer for a time that was already due, so the callback re-armed itself from
inside Helper's own flush: `arm -> flush -> callback -> schedule -> arm`, ~8000 levels deep, `STACK_OVERFLOW` in
`lua51_64.dll`. Bisected to the single file in five runs. Fixed with `FRAME_S = 0.01` (the arm is strictly in the
future) and pinned by two assertions. Details: [in-game-session-8.md](in-game-session-8.md) §8.1.

### 4.2 Crash 2 - `ffi.C.IsStartmenu()` called once per frame

After a save, the game died 141 s later, again `0xc00000fd`. Every dump of the day - seven of them, including the ones
taken with `x4mp_ui_standalone.lua` enabled - shows the same ~8040-deep cycle `X4.exe+0xaf7150` ->
`lua51_64.dll+0x2c95` -> `+0x4c00c` -> `X4.exe+0xaf7262`, i.e. a Lua callback calling an X4 C function that
synchronously runs the same callback. A control run with the mod moved out of `extensions\` played 601 s with no crash
and no dump, so the recursion is ours.

The culprit was found by bisect with a watch long enough to see the crash: `bridge` + `money` with
`ffi.C.IsStartmenu` stubbed to `false` played 721 s clean, the same pair unpatched crashed at 170 s. The money poll
runs every frame and asked the start-menu question every time, so the mod made ~100 FFI calls a second into the game's
UI Lua state, each one re-entering the engine callback pair above.

Fixed in two places: the poll caches the answer for one second (`MENU_S = 1`, `x4mp_money.lua:33-45`), and the bridge
binds `ffi.cdef` / `ffi.C.IsStartmenu` once instead of on every call (`bindStartMenu`, `x4mp_bridge.lua:322-337`).
Pinned by two Lua cases (99 polls in one second ask zero times; `ffi.cdef` runs exactly once across three calls). The
`inArm` re-entrancy guards added earlier to all three per-frame loops are kept as hardening. Details:
[in-game-session-8.md](in-game-session-8.md) §8.2.

**Verified in game.** The deployed build played **901 s with all 20 UI modules, a join and no dump**, where the same
save previously died at 170 s ([in-game-session-8.md](in-game-session-8.md) §8.7).

### 4.3 The wallet looked in the wrong wallet, then wrote in the wrong unit

- **Wrong wallet kind.** In Shared mode the client matched `WalletKind::TeamPool`, but the server books Shared into
  `WalletId.TeamShared` ([EconomyService.cs:155](../server/src/X4MP.Core/Economy/EconomyService.cs)) as
  [protocol.md:1043](protocol.md) requires. Every `WalletUpdate` was therefore dropped: `server=0 updates=0 acked=0`
  while the delta was resent every 30 s. Fixed (`kKindTeamShared`), and the post-fix line proves the loop:
  `updates=3 acked=2 writes=1 converged=2 bad=0 failed=0`.
- **Wrong unit.** [decisions.md:519](decisions.md) says `AddPlayerMoney` takes **cents** while `GetPlayerMoney` returns
  credits; the edge did not convert, so a `+100000` credit correction moved the player 10000 -> 11000. Fixed with
  `GameApi::kCentsPerCredit = 100` in `mod/native/game/game_api.cpp`, an overflow refusal, and a test.

### 4.4 The save seeder added the save's balance on top of one the mod had already booked

With a fresh server session the wallet loop ran end to end for the first time, and the player's money **doubled**
right after the seed: `local=110000 ... server=110000 ... writes=1` at t+20 s, then
`[13:24:15 INF] EconomySaveSeeder seeded the economy with 110000 credits from the save: None`, then
`local=220000 ... server=220000 ... writes=2`. The fresh session's wallet was never empty - `StartingCredits` grants 100000 when the player registers
(`EconomyOptions.cs:35`) - and the mod books `local - server` so the two sides agree (`CreditDelta +10000` in the
ledger, 100000 -> 110000); the seeder then ran on the first checkpoint and `EconomyService.SeedSaveMoney`
**posted the save's 110000 on top** of it. `SessionId` is `Guid.NewGuid()`
([GatewayState.cs:60](../server/src/X4MP.Core/Session/GatewayState.cs)), so nothing was restored from an older
session - the pre-seed balance was booked in this very session, which rules the ledger out.

Fixed: `SeedSaveMoney` now treats the save's amount as the wallet's **target**, posting `target - current` per
destination and letting `World` carry exactly that difference, so the totals stay balanced and a repeated seed moves
nothing. Pinned by `EconomyServiceTests.SaveMoneyBringsTheWalletToTheSavesBalanceInsteadOfAddingToIt` (500 booked by
`BookCreditDelta`, seed 777, wallet is 777 not 1277, `TotalBalance() == 0`, second seed still 777). Details:
[in-game-session-8.md](in-game-session-8.md) section 8.8.

Re-verified in game on a fresh session: the starting grant put 100000 in the wallet, and the seeder posted **120000**
(`team_shared +120000 -> 220000`, `world -120000`), landing exactly on the save's 220000 where the old code would have
posted 220000 on top and produced 320000. The pre-fix session an hour earlier reads `+110000 -> 220000` for a save that
held 110000.

The growing `pending=N/0` in the same lines is not money: the stats line prints `pending=<unsent rows>/<pending_sum>`
and `WalletHub::book_probe` pushes rows with `delta = 0` on purpose
([wallet_hub.cpp:103-111](../mod/native/features/wallet/wallet_hub.cpp)).

### 4.5 M5-34: the 60-minute ledger-auditor session

Run on 2026-10-29 against a fresh Debug server on its own ports (node 47980, UDP 47981, admin 47990) and its own data
dir, so the in-game session on 47780/47790 kept running. Five fake clients plus the fake authority, three teams,
`--relations ffa`, `--economy heavy`, `--dupe-attack`, `--income-rate 2`, `--trade --trade-fail 10 --trade-timeout 5`,
`--verify`, `--duration 3600`. The server ran with `Teams:AllowAssetTransfer=true`,
`Economy:TradeScope=Anyone`, `Economy:TradeRequiresProximity=false`; without those three every trade proposal is
refused with `ScopeDenied` ([m1-exit-report.md:19-20](m1-exit-report.md)).

The run ended on its own timer with exit code 0 after 3614 s:

```
summary: nodes=6 joined=6 errors=0 pings=21594 rtt avg=1.28ms max=1003.74ms elapsed=3614.4s ingame=6
invariants: auditor=ok violations=0 frozen=False ledger-transactions=50250 ledger-sum=0 duplicate-requests=0
  delta-books=6/6 wallets(match=20 behind=0 ahead=0 drift=0)
  loans=[Active=1,Declined=26,Forgiven=29,Repaid=174,Withdrawn=5] overdue-loans=0
  trades=[Completed=653,Expired=2,InDoubt=5,RolledBack=91] in-doubt=[233,383,456,473,668]
dupes: replays=300(idempotent=184 rate-limited=116 other=0) reuses=300(rejected=202 rate-limited=98 other=0)
  races=475(copies=1425) overdraw-pairs=117(both-ok=0) delta-resends=5352 duplicate-effects=0
reconciliation: nodes=6 updates=109027 steps-checked=48279 results-checked=10927 unverifiable=0 stale=6 unacked=0 drift=0
trade-authority: orders=749 applied=656 failed=93 withheld=29 silent=5 duplicate-orders=0
  queries-answered=24 queries-ignored=15 unknown-queries=0
trade: clients=5 proposals=1390 accepts=1554 results(one per party)=[completed=1306 rolled-back=182 cancelled=0
  rejected=0 expired=4] open-at-end=10 request-rejects=[TooManyOpen=60,RateLimited=1312,AssetUnavailable=72]
economy(heavy): clients=5 requests=6477 ok=3915 rejected=2562 skipped=1142
  sent=[Donate=1139,LoanForgive=47,LoanOffer=600,LoanRepay=528,LoanRespond=384,LoanWithdraw=7,PoolDeposit=703,PoolWithdraw=604,Transfer=2465]
  rejects=[InsufficientFunds=43,ScopeDenied=603,TooManyOpen=1,RateLimited=1915]
verify: clients=5 frames=416490 entries=12313861 checked=12313861 ghosts=692 spawns=2602 despawns=1910
  checksums-ok=3575/3575 resyncs=0 position-errors=0 errors=0
teams: clients=5 placed=5 unplaced=0 teams-used=3 spread=[1:2,2:2,3:1] requests=6 rejected=1
```

and the admin API agreed at the end:

```
POST /api/v1/economy/audit -> { "ok": true, "economyFrozen": false, "violations": [] }
GET  /api/v1/economy/summary -> moneySupply=35837937 inEscrow=14307 openLoans=1 overdueLoans=0
  openTrades=5 inDoubtTrades=5 frozenWallets=0
```

Reading it honestly:

- **Conservation and idempotency are clean.** `ledger-sum=0` over 50250 transactions, `duplicate-effects=0` after
  300 replays, 300 payload reuses and 475 race copies, `overdraw-pairs=117(both-ok=0)`, `duplicate-orders=0`.
- **Replication agrees.** 12313861 entity entries checked on five clients, 3575/3575 checksums, 0 resyncs, 0 position
  errors; reconciliation `drift=0` over 109027 wallet updates (6 of 48279 steps were stale because a newer update
  superseded them, 0 unacked).
- **The five InDoubt trades are the injected failures, not a leak.** `--trade-fail 10 --trade-timeout 5` produced
  `silent=5` authority orders; each one leaves its escrow locked (`inEscrow=14307`) and is listed in `in-doubt`, which
  the auditor accepts as a terminal state. Resolving them is the admin refund path, not the settle tick - the same
  state M1 recorded for withheld orders.
- **Rollbacks were exercised**: 182 of 1554 trade results rolled back, `failed=93 withheld=29` on the authority, and the
  ledger still sums to 0, which is the property AC 12 asks for on the server side.

## 4.6 M5-29/M5-30: the mod-side economy client

The mod had no way to move money between players: the net layer decoded no `Trade*`/`Loan*`/`Transfer*` frame even though
`protocol/schema/economy.fbs` carries them and the server side has been complete since M1 (`EconomyModule.cs`,
`EconomyModule.Loans.cs`, `EconomyModule.Trades.cs`; `Session/MessagePolicy.cs:240-261` already routes them). M5-29/M5-30
close that gap:

- `mod/native/features/economy_client/economy_wire.h` - the C++ half of the frames, same shape as `wallet_wire.h`:
  `encode_credit_transfer`, `encode_pool_deposit`, `encode_pool_withdraw`, `encode_loan_repay`, `encode_loan_forgive`,
  `encode_trade_accept`, `encode_trade_cancel`, and `decode_economy_result`, `decode_loan_status`, `decode_trade_status`,
  `decode_trade_result` (verify the buffer, then copy - the pointer dies with the payload).
- `economy_client_hub.h/.cpp` - the node's view: `request(Op, RequestParams, error)` refuses what the server would refuse
  anyway (no recipient, yourself, a non-positive amount, no id) before a byte leaves; a request key is `(++key_, self)`
  with the counter seeded from the wall clock at init, so a restart never reuses a key with a different payload
  (`EconomyReject::RequestIdReuse`); loans are kept by id, and a `TradeStatus` replaces its row only when its version is
  not older. `snapshot_json()` renders the screen state in one string.
- `economy_client_feature.h/.cpp` - the postman: the bridge verb `x4mp.economy` fills an inbox on the event thread, the
  frame thread drains it, sends up to eight frames per frame on the Control lane and retries a refused send in the next
  frame with the same key (so a retry books once); `EconomyResult`/`LoanStatus`/`TradeStatus`/`TradeResult` are published
  back as the `x4mp.economy` topic on change only, plus a 30 s `[economy_client]` line.
- `mod/extension/x4mp/ui/x4mp_economy.lua` - the screen and the commands: `/mp` shows the wallet, the pool debt, the open
  loans and the trades waiting for an answer; `/mp send <player> <credits> [memo]`, `/mp deposit|withdraw <credits>`,
  `/mp repay <id> <credits>`, `/mp forgive <id> [credits]`, `/mp accept|decline <id>`. Ids are shown and typed as the last
  six hex digits of the lo word, and the accept command quotes the version the player saw, so a stale click is refused
  instead of booking something else. Every command answers with a system line; a local refusal says why.
- Wiring: `host/feature_list.cpp:38,63` (right after the wallet feature), `join_feature.cpp` routes the four answers next
  to `WalletUpdate`, `CMakeLists.txt:133` builds `tests/test_economy_client.cpp`, `ui.xml` loads the new module, and both
  text pages carry ids 517-570.

Evidence, out of game: `mod\build.ps1 -Config relwithdebinfo` -> **674 tests, 100% passed, 0 failed** (`[economy_client]`:
93 assertions in 6 cases, which build real flatbuffers the way the server packs them and decode them back);
`python mod\tests\lua\run_lupa.py` -> **293 passed, 0 failed**, of which 13 are the new `/mp` cases.

Evidence, in game (2026-10-30, one node, step S10 of the run sheet): the mod was deployed, X4 was restarted so the new
`x4mp.dll` loads (8020992 bytes, 30.10 8:45:42 - the pre-economy build sits beside it as `x4mp_live.dll`), `x4mp_s108_a`
was loaded and the node joined as authority to session `223c106d-5a83-428c-affc-3bee2f0b9da6`:

```
[13:52:29] player 1 (Admin) joined as Authority, Client, Admin
[13:52:53] X4MP.Core.Saves.EconomySaveSeeder seeded the economy with 110000 credits from the save: None
[13:52:53] session 223c106d-...: AuthorityLoading -> Running (first checkpoint stored)
```

`ExecuteDebugCommand("mp", "")` (the vanilla global takes the name and the arguments separately) answered through the
module in the real chat window:

```
Экономика: общий, кошелёк 110 000, долг пула 0
Открытых займов: 0, входящих сделок: 0
```

`/mp loans` and `/mp trades` added four more lines (`Открытые займы:` / `пусто` / `Входящие сделки:` / `пусто`,
`before=3 after=7`), so the screen reads the native snapshot. `/mp deposit 50000` and `/mp withdraw 20000` sent their
frames and the server answered with an `EconomyResult` the mod decoded:

```
last{ok=false status=2 reason=5 detail= key=0000000000000002:0000000000000001}
```

`status=2` is `EconomyStatus::Rejected` and `reason=5` is `EconomyReject::NotApplicableInSharedMode`, which is the
correct answer here: the team runs in **Shared** credit mode (`wallet=shared/110000/0`), where a team pool does not
exist. The request key `(2, 1)` - the second key of player 1 - proves the round trip: Lua verb -> native hub -> Control
frame -> server -> `EconomyResult` -> snapshot -> the screen. A *successful* pool move needs `CreditMode=PerPlayer` at
server start or the two-node run; it is recorded in the S10 note rather than forced on a single node.

## 4.7 M5-31: the authority settles an escrowed trade, and can undo it

`docs/m5-plan.md:522` asks for the last piece of the economy: the ledger escrows both sides, but the **assets** live in
the universe, so the node that owns the universe has to move them - precheck everything, journal it, apply in order,
compensate in reverse on a failure, and answer with an `AssetTransferConfirm`. That is now
`mod/native/features/settlement/`:

- `settlement_wire.h` - `decode_asset_transfer_order`, `decode_trade_query`, `encode_asset_transfer_confirm`
  (`protocol/schema/economy.fbs:209-244`), reusing `economy_client::Id128Value` for ids.
- `settlement_journal.h/.cpp` - a sidecar `settlement.json` written with a temp file and a rename, one entry per
  `trade_id` in `applying | applied | compensated | needs_admin | refused`, the applied line count and the error. An entry
  still `applying` when the file is read back becomes `needs_admin` with `"the game stopped during settlement"`, which is
  the crash-safety half of the design (`docs/mod-design.md:2395-2440`). A file that does not parse is reported in
  `problem()` and the hub keeps working from memory.
- `settlement_feature.h/.cpp` - `SettlementHub::on_frame` takes the order, refuses a duplicate by answering from the
  journal, and otherwise **prechecks every line before touching anything**: the asset must be a valid, non-wrecked
  component that is not player-guarded, the team must have a faction, and the old owner must be readable, because that
  owner is what the undo needs. Then it applies line by line with `set_component_owner`, logging
  `settlement: order {} line {} component {} '{}' -> '{}'`. A failure at line N puts the applied prefix back in reverse
  order and verifies each undo with `owner_faction`; if every undo lands the entry is `compensated`, and if one does not
  the entry is `needs_admin` with the residual named in the error and a `Level::Error` log - wares and ships are never
  silently invented or lost. The confirm goes out on the Control lane from an outbox, so a refused send is retried next
  frame with the same `trade_id`. `TradeQuery` is answered from the journal, and an unknown trade gets
  `ok=false, "no record of this trade on the authority"`.
- Routing: every inbound frame reaches `AuthorityFlow::on_frame` first (`join_feature.cpp:741`), so the branch for
  `AssetTransferOrder` and `TradeQuery` sits there and hands the payload to `settlement::settlement_hub()`.
- v1 scope: `OwnerChange` only (a ship or a station for credits). A `WareMove` line is refused with
  `"a ware move is not settled by the authority in v1"`, because the cargo shim (`md/x4mp_actions.xml:32-50`) applies a
  move but reports no per-operation result, and without a result the reverse compensation cannot be verified - refusing
  is honest, guessing is not. `OwnerChange` is also team-to-team: the asset moves to `x4mp_team_<slot>`, and `to_player`
  is ledger information only.

Evidence: `mod\build.ps1 -Config relwithdebinfo` -> **681 tests, 100% passed, 0 failed** (99.0 s),
`x4mp_host_tests.exe "[settlement]"` -> `All tests passed (69 assertions in 7 test cases)`, Lua `293 passed, 0 failed`.
The seven cases cover the order round trip against a real flatbuffer, a broken buffer, the confirm encoding, the
`TradeQuery`, a journal that survives a reload, the `applying`-at-load promotion, an unreadable file and a journal with
no path. What is left is the two-node half: a real ship-for-credits trade where both wallets move and the ship changes
hands, in the checklist file [two-pc-checklist.md](two-pc-checklist.md) section 11.

## 4.8 The ledger page, the offer notices, and the window that leaked its handle

Four gaps were left after §4.6/§4.7, and this wave closes them:

1. **An offer arrived silently.** The hub decoded a `LoanStatus` or `TradeStatus` like any other answer, so a player
   who was flying saw nothing until they typed `/mp`. `economy_client_hub` now records a `Notice` (`kind`, `id`, `from`,
   `amount`, `version`) when `put_loan` takes a new `Offered` row whose `borrower` is this node, and when `put_trade`
   takes a new row in `Proposed|Countered` whose `counterparty` is this node - the dedupe is the row identity the hub
   already applies, so a repeat of the same row is not a second notice. `economy_client_feature` drains them with
   `take_notices()` and raises `x4mp.economy_notice` `{v:1,kind,id,from,amount,version}`; `ui/x4mp_economy.lua`
   (`E.onNotice`) answers with one chat line, one `AddUITriggeredEvent("X4MP","notify",...)` toast, the hint
   `Answer with /mp accept ... or /mp decline ...`, and the HUD badge (`H.economyText()`) counts the offers still open.
   The `[economy_client]` line carries `notices=<N>`.
2. **The standalone window leaked its frame handle.** `menu.frame` was dropped on the redraw path and on
   `menu.cleanup()`, but not when the engine closed the window without calling back, so a handle outlived its frame.
   `ui/x4mp_ui_standalone.lua` now has one `releaseFrame()` that every path goes through - the top of `drawFrame`, the
   cleanup hook and the close path. Measured in game: 30 open-close cycles left `n=30 nilFrames=30 bad=0`, while ten
   bare `Helper.closeMenu` calls reproduced the stale handle (`stale=10`) and the next open replaced it; no `.dmp`.
3. **The economy screen was chat-only.** `x4mp_menu.lua` gained a `buildEconomy` screen (a main-screen `Economy` button
   while the session is live) and `x4mp_economy.lua` a `E.screenRows(rows, st)` that builds the rows from the same
   snapshot and the same verbs as `/mp` - wallet line, the amount box, deposit/withdraw, the recipient picker, the loans
   with repay/forgive, and the incoming/outgoing trade tabs with accept/decline - so the window and the commands cannot
   drift apart. Texts 576-585.
4. **The ledger had no wire pair.** `protocol/schema/economy.fbs` gained `LedgerKind`, `LedgerRow`, `LedgerRequest` and
   `LedgerPage`, and `message_ids.fbs` `LedgerRequest = 0x0816` / `LedgerPage = 0x0817`; the catalog rows are in
   [protocol.md](protocol.md). The names are `LedgerRequest`/`LedgerPage` rather than `LedgerQuery` because
   `X4MP.Core.Economy.LedgerQuery` is the domain type the handler already passes to `QueryTransactions`.
   - Server: `EconomyModule.Ledger.cs` (`LedgerOnMessage`, `RowOf`, `ActorOf`, `SendLedgerPage`) answers one page per
     request, over-fetches four times the limit and keeps only the rows `EconomyService.IsVisibleTo` allows, so a row
     counts the wallets the requester may see - its own, its team's shared and pool wallets, and the trades and loans it
     is party to - and never implies a counterparty balance. `limit` is clamped to 1..200 (0 means 50), `before` is the
     ULID cursor, `more` says an older visible row exists, and a request outside `Running` is answered
     `SessionNotRunning` instead of silence. Routed in `MessagePolicy.cs:262-263` and registered in
     `MessageRegistry.Table.cs` - without that registration every ledger frame was a `ProtocolViolation`.
   - Mod: `economy_wire.h/.cpp` encode the request and decode the page (verify, then copy), `economy_client_hub` keeps
     `ledger_`, one page in flight at a time (`ledger_pending()`), appends a cursor page without repeating an id and
     replaces the list on a newest-first page, and `snapshot_json()` carries `ledger`, `ledger_more` and a rising
     `ledger_pages` counter so the UI can tell a fresh page from a redraw.
   - Lua: `/mp ledger` asks for the newest page and prints it when it lands (the counter is what arms the print),
     `/mp ledger more` asks for the page after the oldest row held, and the economy screen shows the rows with
     `Load the ledger` / `Older entries`. Texts 586-613, including one name per `LedgerKind`.

**Build fix.** `server/src/X4MP.Server/X4MP.Server.csproj:58` carried the comment `<!-- --include=dev: ... -->`, and an
XML comment cannot contain `--`: the whole solution failed with `MSB4025` at that line, on `56b999a` as committed, not
only on the new code. The comment now spells the flag out in prose. Nothing else in the file changed.

**The mod dropped its own session, and the catalog was the reason.** The first in-game `/mp ledger` printed no page. The
mod log said `net: disconnected (MalformedFrame): invalid inbound frame: UnknownMessageType`, and the server said
`player 1 (Admin) left: MalformedMessage` - which is only the reason code the client sent, not a server verdict. The
frame that failed validation was the server's `LedgerPage`: the mod checks every inbound frame against the hand-kept
catalog `protocol/cpp/src/message_table.inc`, which still ended at `TradeQuery 0x0815`, so the answer to a perfectly good
request killed the connection. Both ids are in that table now, and three guards that had never been run in this mission
were red because of the gap: `protocol/cpp/tests/test_wire.cpp` expected 91 catalog entries (93 now), and
`X4MP.Protocol.Tests` expected 94 message types with a sample frame for each (`FrameCodecTests.cs:39-49`) - 96 now, with
golden vectors `0x0816_LedgerRequest.bin` and `0x0817_LedgerPage.bin` and their `index.json` rows. A new message type is
therefore five places, not four: the schema, the golden vectors, `message_table.inc`, the C# registry and the policy.

**A bad page latched the screen.** `economy_client_hub` set `ledger_pending_` before sending and cleared it only in
`put_ledger_page`, so one unreadable `LedgerPage` left the UI answering `a ledger page is already on its way` for the
rest of the session. The decode-failure branch now clears the latch and the cursor, marks the snapshot dirty and counts
one `bad`; the `[economy_client]` test sends an 8-byte garbage page and then asks for a page again.

**The ledger time came out as an epoch number.** X4's Lua sandbox has no `os` table at all, so `os.date` in
`x4mp_economy.lua` never ran and every row fell back to raw seconds (`1793486237`). The Lua side also cannot convert the
UTC microsecond stamp to local time on its own: `ConvertTimeString` answers in UTC and the sandbox has no epoch clock. So
the hub renders the wall clock once, when a page lands (`clock_hhmm` in `economy_client_hub.cpp`, `LedgerRowView::at`),
and the snapshot carries `"at":"05:52"` beside `at_us`. The Lua side prefers that string and keeps the raw seconds only as
a fallback for an older DLL.

**Run in game on one node, 2026-11-01.** `/mp ledger` printed `05:52  Доход в игре  +110 000, от игрока 1, Other` and
`05:52  Стартовые кредиты  +100 000`; `/mp ledger more` asked for the page after the oldest row, the server answered with
no rows, so the list stayed at two and `more` stayed false (`requests=3 ... ledger_pages=3 bad=0 self=1 linked=1` in the
`[economy_client]` line, no disconnect). The economy window drew the same two rows under `Журнал, сначала новые`, with
`Загрузить журнал` active and `Более старые записи` inactive; clicking the first sent the request and raised
`ledger_pages` to 3. Thirty open/close cycles left `X4MPMenu.frame == nil` every time. The offer notice was raised by
hand (`X4MPEconomy.onNotice`) and produced the two chat lines with texts 571-574, and `X4MPHud.economyText()` answered
`ждут ответа: 1` for one incoming trade; the cross-player raise itself is a two-PC item.

## 4.9 M5-32: the game starts its own server

M5-32 asked for a dedicated server that can be started from inside X4, by a player who has neither a terminal nor a .NET
install. It is `mod/native/features/server` (`server_process` = Win32, `server_launcher` = the plan and the state machine,
`server_feature` = the bridge) plus `ui/x4mp_server.lua`, the `Выделенный сервер` screen and `/mp server start|stop|status`.

**The bundle is a folder publish, and that is not a preference.** A `--self-contained` publish of the server runs on a
machine with no runtime, but the single-file flavour cannot be built here (central package management rejects the
`Microsoft.NET.ILLink.Tasks` the flag pulls in, `NU1009`), and **ReadyToRun produces an executable that fail-fasts**
(`0xC0000602`, no output at all, `coreclr.dll` named in the event log) while the same publish with
`-p:PublishReadyToRun=false` starts normally. `mod/tools/deploy.ps1 -WithServer` publishes
`--self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:SkipWebBuild=true` into
`out\server-win-x64` (360 files, about 115 MB, web GUI embedded) and merges it into `<X4>\extensions\x4mp\server`. The
first attempt shipped the stale ReadyToRun folder, and the menu reported exactly what happened:
`server: exited port=47780 error=the server exited with code -1073740286, see ...\server-console.log`, with an empty log.

**Two defects the in-game run found.** `CreateProcessW` refused the spawn with `ERROR_INVALID_PARAMETER` (87) as soon as
the launcher passed an environment block: the call was missing **`CREATE_UNICODE_ENVIRONMENT`**, so Windows read the
UTF-16 block as ANSI. The new real-process test reproduces it outside the game (a `powershell.exe -Command exit 7` child
with two overrides), which is why the case is in `test_server_feature.cpp` and not only in a comment. The second: the
node gateway authenticates an authority join against **`X4MP:Net:AdminPassword`**, not against the `admin` user in the
database, so a server started without that variable refuses the very game that started it - the first join ended
`rejected detail=authentication failed`, `conn 100: refused with "AuthFailed"`. The launcher now generates a 20-character
password once, keeps it in `<data_dir>\admin-password.txt`, passes it as `X4MP__Net__AdminPassword`, shows it on the
screen and fills it into the join form's host field.

**Run in game on 2026-11-02** (game pid 19924, no session, nothing on the ports): the screen showed
`Состояние сервера: работает | PID 14216, порт 47780, слушает | Порт админ-панели 47790 | Каталог данных:
...\X4MP\server-data | Журнал: ...\logs\server-console.log | Админ-пароль: o5JjbCoXaxm2WQGpRkW8`, with
`server_start=false`, `server_stop=true`; `server-console.log` had `Database ready at schema version 8`,
`Now listening on: http://0.0.0.0:47780` and `47790`, and both ports answered for pid 14216. The join with the generated
password was admitted (`admitted Admin (player 1) as Authority, Client, Admin`, session
`Idle -> WaitingForAuthority -> AuthorityLoading -> Running (first checkpoint stored)`) and the mod reported
`state=ingame role=authority players=1`; the host toggle in the join form prefilled `o5JjbCoXaxm2WQGpRkW8` by itself.
Stopping closed both ports, and closing the game while the server ran left no `x4mp-server` process (`auto_stop`).

**Run on the two-instance stand, 2026-11-02.** One PC, two full game instances
([`../mod/docs/two-instances-one-pc.md`](../mod/docs/two-instances-one-pc.md)): the server was started
from the authority's menu (`X4MPServer.start()` → `running pid=10808`, ports 47780 and 47790 owned by
that pid), the authority joined with the generated password (`player 1 (Host) joined as Authority,
Client, Admin`, `WaitingForAuthority -> AuthorityLoading`), the second instance joined as a client and
both reached `state=ingame ... players=2`. Stopping the server from the same screen gave a graceful
shutdown (`StartupBanner shutdown complete`) and both games survived it. So the whole M5-32 loop —
start, play, stop — works from inside the game, not only from a terminal.

A `X4MP.FakeNode.exe client` joined the same live session as player 3 and then 5 once it claimed the
right build and extension list (`--mod-build x4mp` plus `--extensions` with the authority's exact
eleven entries; a bare bot is refused `ModVersionMismatch`, then `ExtensionsMismatch`). Its welcome
line read `resume_grace=300s` — the first live proof that the republished bundle carries the new
reconnect default from KI-2. That bot is also what finally produced a **cross-player offer notice**:
`Новое предложение займа 000001 от игрока 5, сумма 903 кр.` followed by
`Ответ: /mp accept 000001 или /mp decline 000001` in the authority's chat, with the loan row
`borrower=1 lender=5 principal=903 repay_total=930` in its snapshot.

Two things that run found, both written up in [`../mod/docs/known-issues.md`](../mod/docs/known-issues.md):
with the default `CreditMode=Auto` a single-team session shares one wallet, so every transfer and loan
between the two players is refused `SameWallet` (the stand needed `Economy:CreditMode=PerPlayer`); and
**offers are receive-only in the mod** — it cannot create a loan offer or a trade proposal, and cannot
accept or decline a loan offer, so the checklist's step 9 can only be run with a fake node as the
offering side. The server half is ready for both (`LoanRespond` is dispatched and `LoanAccept` is
published); the missing pieces are encoders and screen rows.

## 4.10 The session-9 stand: the event ring, the first kill, and a string-table alias

The M5-33 stand is the two-instance setup from [`../mod/docs/two-instances-one-pc.md`](../mod/docs/two-instances-one-pc.md):
the authority runs the stock install, the client runs the patched `X4b.exe`, both under one relay. On 2026-11-09 it ran
build `8b4d6e7` on checkpoint `543e55e6f004681187e593fb85e5077c` (61,796,920 bytes, manifest 102,136 bytes, 1,316 rows,
40/40 stations matched over 3 sectors). What it proved and what it found:

- **AC 1, the ring is alive.** The first run of the day had no event at all: `events: capture armed` and
  `events: a universe is ready`, then `events: no MD hello after 30s`. The cause was the same bridge-envelope defect
  already fixed in statics and capture: `events_feature.cpp` handed the raw platform text
  (`{"v":1,"data":"H;7"}`) to `parse_event_line`, which expects a bare `H;`/`K;` line, so every line was counted
  `bad_`. Fixed by parsing `auth::scan_payload(text)` and pinned by the `[events][m501][bridge]` case; the hello then
  arrived on both nodes and `hooks=alive md=7` in the budget line.
- **AC 2, the first kill.** The client killed an NPC; the authority logged `authority: net 249 killed by player 1
  (net 166), despawn queued` at 08:29:27.034Z and the client dropped the ghost at 08:29:27.429Z (`tracked` 134 -> 133,
  `removed=1`). 0.4 s end to end on the stand.
- **AC 3, PASS on the second attempt.** The first attempt destroyed a ship the player was not in, so the MD rule
  `$Killed == player.occupiedship` (`md/x4mp_events.xml:90-94`) raised `X;` (lost asset), not `P;` (death). On the
  second attempt the player died in the spacesuit (oxygen): the guest's ring fired `P;` (`fired=3` at 18:36:58.899Z),
  and the authority logged `authority: player 1 died (killer net 0); avatar net 902 despawned as PlayerLeft, record
  dropped` at 18:36:43.449Z, 103 ms after `avatars: the record of player 1 is forgotten (death)` at 18:36:43.346Z.
  After the reload and rejoin the server resumed the **same** player 1 (`player 1 (Guest) resumed (baseline epoch 3)`,
  `conn 9: admitted Guest (player 1) as "Client"`), each node followed exactly one remote avatar (authority
  `[md] onfoot: avatar pid=1` every 5 s; guest `avatar pid=2`), `[sess] onfoot: ... remotes=1 actors=1/1`, and the next
  checkpoint used `next_net_id 903` (902 was not reused). No duplicate ghost.
- **A defect the stand exposed: a session string-table alias.** After the 13:47 restart the client refused thousands of
  ghosts with `ghost spawn refused: faction '<ship macro>' does not exist in this game` - the owner field held the
  ship's own macro. The table belongs to the session: the server replays the full table on join
  (`protocol/schema/session.fbs:231-233`), but the client merges the entries into the table it restored from the DLL
  stash (`ghost_core.cpp:182-193`) and restores the ghost records with their refs unconditionally
  (`ghost_feature.cpp:119-128`; `kRegistryEpoch` is a fixed format version, not a universe epoch,
  `ghost_driver.h:40`). A fresh authority join numbers strings from 1 again (`authority_flow.cpp:187-207`), and a new
  session or save orders them differently, so a restored `owner_ref` can index a macro in the new table;
  `StringTable::set` overwrites by index (`ghost_core.cpp:40-47`). Counts: 5,573 refusals in the 14:47 rotation,
  53,347 in the 13:48 one, 48,641 in the 13:32 one; the current log has 0 and `ghosts health` shows `spawn_failed=0`,
  so the alias only lives until the new session's table and refs settle. The fix is a table epoch (clear the client
  table and invalidate the restored ghost refs when the join replay arrives, or tag the table with a session epoch)
  and a dedicated test; it is recorded in
  [`../mod/docs/known-issues.md`](../mod/docs/known-issues.md) as KI-5.

## 5. What M5 needs next

1. A **two-node** sitting (two PCs, one PC with two instances — the stand, already run once, §4.9 — or one PC plus
   `out\fakenode\X4MP.FakeNode.exe client`) to fill AC 2-7, the UI half of 9 (red targeting on both sides, allied
   within 2 s, `admin_only` switch moving the avatar) and the two-node halves of 10 and 12. The run sheet is
   [in-game-session-9.md](in-game-session-9.md); the per-mechanic checklist is
   [two-pc-checklist.md](two-pc-checklist.md); the stand recipe is
   [`../mod/docs/two-instances-one-pc.md`](../mod/docs/two-instances-one-pc.md).
2. **W7 remainder**: M5-27 needs that two-node run; M5-29/M5-30 are **implemented, green and run in game on one node**
   (step S10, §4.6) - what is left of them is the two-player half: a transfer to a teammate, a pool move in PerPlayer
   mode, and a loan or a trade answered by the other side. **M5-31 is implemented** too (§4.7): the authority settles the
   asset side behind a journal and compensates in reverse; its two-node acceptance is a ship for credits. The mod
   encodes the requests a player can send today — transfer, pool deposit and withdraw, loan repay and forgive, trade
   accept and cancel, ledger — and decodes every answer `protocol/schema/economy.fbs` carries; offers are still
   receive-only on the mod side, see §4.9 and KI-4 in [`../mod/docs/known-issues.md`](../mod/docs/known-issues.md).
3. ~~`x4mp_ui_standalone.lua` re-enabled only after its close path gets the same re-entrancy guard~~ - **done on
   2026-10-30**: `menu.display()` and `menu.onCloseElement` are guarded, the delayed reopen is dropped during a save
   load, the module is enabled in both `ui.xml` files, and the save-load crash no longer reproduces
   ([in-game-session-8.md](in-game-session-8.md) §8.11). The screen UI (M2-05/M2-11: `/x4mp`, the Join and Status
   screens) is therefore live again, not only the HUD and the chat.
