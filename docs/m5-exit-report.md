# M5 exit report

Date: 2026-10-29. Criteria: [roadmap.md](roadmap.md) M5 row and [m5-plan.md](m5-plan.md) section 3. Plan of record:
[m5-plan.md](m5-plan.md) (eight waves, M5-01..M5-34). In-game evidence: [in-game-session-8.md](in-game-session-8.md).

**Read this first.** **M5 is not exited.** Waves W1-W6 are code-complete and every one of their gates is green, and the
milestone's ledger line is now met - a **60-minute auditor session** with purchases on all nodes finished clean (§4.5) -
but the rest of the acceptance line ("MOD §9 M5 acceptance items 2-12; every event in REQ §8.3 checklist executed once
with log evidence", with the roadmap's "yes (2 PCs)" column) needs a two-node session, and session 8 ran **one node plus
the server**. W7 has started: **M5-28 (`/who`, `/ping`) is implemented, green and verified in game**; M5-27 (relations) is
code-complete from the teams feature and needs the two-node run; M5-29..M5-31 (economy screen, notifications, escrowed
settlement) are **not implemented**, so AC 11 and 12 and the UI half of AC 9 have nothing to verify yet.

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
| Mod build | `powershell -NoProfile -ExecutionPolicy Bypass -File mod\build.ps1 -Config relwithdebinfo` | builds; **668 tests, 0 failed** (4 skipped: the real-server/game smokes 648, 649, 659, 668), 89.7 s |
| Mod unit binary, wallet only | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "[wallet]"` | **550 assertions in 16 test cases**, all passed |
| Lua | `python mod\tests\lua\run_lupa.py` | **280 passed, 0 failed** (276 before the session-8 cases: the timer re-entrancy guard, the 1-second start-menu cache, the bind-once FFI symbol; 278 before the two M5-28 chat cases) |
| Deploy | `powershell -NoProfile -ExecutionPolicy Bypass -File mod\tools\deploy.ps1` | `Deployed.`; prints `x4mp is in use (x4mp_live.dll); merging over the running installation` while the game runs |
| `X4MP.Core.Tests` | `dotnet test server\tests\X4MP.Core.Tests\X4MP.Core.Tests.csproj` | **1379 passed, 0 failed** (the seeder fix added one case) |
| `X4MP.Server.Tests` | `dotnet test server\tests\X4MP.Server.Tests\X4MP.Server.Tests.csproj` | **603 passed, 0 failed, 1 skipped** (`SaveTransferTests.TwoHundredMegabyteSaveGoesFromTheAuthorityToThreeClients`), 10 m 57 s |
| Ledger auditor (M5-34) | `X4MP.FakeNode.exe swarm --clients 5 --with-authority --teams 3 --relations ffa --economy heavy --dupe-attack --income-rate 2 --trade --trade-fail 10 --trade-timeout 5 --verify --duration 3600` | **clean after 3614 s**: `invariants: auditor=ok violations=0 ... ledger-sum=0 duplicate-requests=0 ... drift=0`, `verify: ... errors=0`; `POST /api/v1/economy/audit` -> `ok=true, violations=[]` (§4.5) |

The .NET suites were re-run after the §4.4 seeder fix, because that is server code. An earlier run of
`X4MP.Server.Tests` against the same build reported two failures; the rerun above (same code) is green, and the
failing names were not captured - treat that pair as flaky until a run reproduces them.

## 2. Acceptance criteria

| # | Criterion (roadmap M5, MOD §9) | Verdict | Evidence |
|---|---|---|---|
| 1 | MD capture ring plus fallbacks; the `md_hook` state is reported | PASS (CI) | `[capture]` counters in `%LOCALAPPDATA%\X4MP\logs\x4mp.log`; `[=ERROR=] [X4MP] events: event capture armed (MD half md/x4mp_events.xml)` in the dev log |
| 2 | A client kills an NPC ghost: gone within 500 ms on the authority and on a second client; an NPC killed by NPCs disappears for all | PENDING (session 9) | needs the second node; the ghost path itself ran (`ghosts: init (restored 0 ghost records ... spawn export yes, move export yes)`) |
| 3 | The client dies, its ghost disappears, it re-joins after a reload with no duplicate ghost | PENDING (session 9) | needs the second node; `[sess] onfoot: ... roster=2` shows the presence roster is live |
| 4 | A client sells 500 units: the authority station stock rises by 500 within 10 s, no frame spike over 2 ms | PENDING (session 9) | needs the second node; the budget side is proven: `[perf] stats: ... mod_p95=0.214ms mod_max=0.626ms` |
| 5 | A client-built station appears for the authority and the other clients at the same position, no duplicate on the builder | PENDING (session 9) | needs the second node |
| 6 | A capture changes the owner on all nodes; the captured ship survives an authority save/load under the right faction | PENDING (session 9) | needs the second node; the authority checkpoint save ran: `[X4MP] authority: SaveGame called for x4mp_ckpt_1b96d95b2d2cd8cb` |
| 7 | NPCs on the authority can damage and kill a client player (stretch) | PENDING (session 9) | needs the second node |
| 8 | Chat moderation hooks, `/who`, `/ping`, `/w`, `/t` | PASS (session 9) | `/w` and `/t` were already live (`[X4MP] chat: wrappers installed / installed / installed`); M5-28 added `/who` (the roster from `X4MPPlayers.list` in team colours, capped at ten rows) and `/ping` (the last `x4mp.status` topic) in `x4mp_chat.lua:244-271`, texts 512-516 in both pages, cases `test_chat.lua:210,231`. Verified in game on 2026-10-30 through the real `ExecuteDebugCommand` global: `who_ok=true ping_ok=true new_lines=3` -> `Игроков в сессии: 1`, the team-coloured row `#ffff8000#AdminX - вы \| Команда 1 \| в сети \| 40 мс \| - пешком, здесь`, `Пинг: 196 мс, сервер 127.0.0.1:47780, состояние ingame`; a non-ours command added `chat_lines_added=0`. Moderation is server-side and answers with a system line. Lua gate **280 passed, 0 failed**. Details: [in-game-session-8.md](in-game-session-8.md) §8.9 |
| 9 | Two hostile teams show red targeting on both sides; allied takes effect within 2 s; `admin_only` switch moves the avatar's owner | PARTIAL | server + mod half proven: `[X4MP] teams: applied seq=1 slots=1 relations=1 active=1 mismatches=0 player_locked=0`; the UI half needs W7 and the second node |
| 10 | A 1M Cr purchase on a client shows in the ledger within 1 s; two simultaneous purchases in Shared mode converge within 2 s; a forced local edit is corrected within 30 s | PARTIAL - **two defects found and fixed in this wave** | before: `[wallet] ... server=0 target=10000 ... updates=0 ... acked=0`; after the kind fix: `[wallet] local=11000 ... updates=3 ... converged=2 writes=1 failed=0 acked=2`; the write unit was wrong (`+100000` credits moved 10000 -> 11000, §4.3) and the seeder doubled the balance (110000 -> 220000, §4.4); after the seeder fix a fresh session posts only the difference (`team_shared +120000 -> 220000`, `world -120000`) and lands on the save's number. The 1 s ledger latency and the two-simultaneous-purchases half still need the second node |
| 11 | Send credits, team pool and the Economy screen, first cut | NOT IMPLEMENTED | M5-29 not started |
| 12 | Loans and escrowed offers settle atomically, rollback exact, residuals 0 | PARTIAL (server) | the server half is proven by the 60-minute run (§4.5): `trades=[Completed=653,Expired=2,InDoubt=5,RolledBack=91]`, `trade-authority: ... failed=93 withheld=29 silent=5`, `ledger-sum=0`, `inEscrow=14307` held only by the 5 in-doubt orders. The mod-side client for loans/trades (M5-31) is not implemented |

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
| W7 | M5-27..M5-31: relations, chat commands, economy screen, notifications, escrowed settlement | M5-28 done (`/who`, `/ping`, Lua 280/0, verified in game §8.9); M5-27 relations code-complete in `features/teams` and proven to apply on one node (`teams: applied seq=1 ... relations=1`), needs the two-node run; M5-29..M5-31 **not started** |
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

## 5. What M5 needs next

1. A **two-node** sitting (two PCs, or one PC plus `out\fakenode\X4MP.FakeNode.exe client`) to fill AC 2-7, the UI half
   of 9 (red targeting on both sides, allied within 2 s, `admin_only` switch moving the avatar) and the two-node halves
   of 10.
2. **W7 remainder**: M5-27 needs that two-node run; M5-29 (the 12.7 economy screen), M5-30 (notifications,
   `/mp accept|decline`) and M5-31 (escrowed settlement in the mod) are **not implemented**. The mod's net layer decodes
   no `Trade*`/`Loan*`/`Transfer*` frame today even though `protocol/schema/economy.fbs` carries them and the server
   side is complete, so this is a new mod-side economy client plus UI, not a patch.
3. `x4mp_ui_standalone.lua` re-enabled only after its close path gets the same re-entrancy guard; it stays commented
   in the installed `ui.xml` today.
