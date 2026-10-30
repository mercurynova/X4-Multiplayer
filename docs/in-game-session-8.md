# In-game test session 8 (M5 acceptance: events, wallets, economy)

> **Status (2026-10-29): run, not a plan.** This document is written from a session that was actually executed on one
> machine (server + one game node) on 2026-10-29, and it records what the session found as much as what it measured.
> The session did not reach the AC table: it hit two crashes and one unit bug first, and every one of them is written
> down here with the evidence that proved it. AC items 8, 9, 11 and 12 have nothing to verify yet, because W7
> (M5-27..M5-31) is not implemented.

M5 is "events + in-game economy: kills, death/respawn, trade/cargo, station builds, capture, live relations, wallet
reconciliation, transfers/pool" ([roadmap.md](roadmap.md) row M5, [m5-plan.md](m5-plan.md)). Waves W1-W6 are
code-complete; W7 is not started; W8 is this document plus [m5-exit-report.md](m5-exit-report.md).

| # | Acceptance criterion (mod-design §9) | Wave | State in session 8 |
|---|---|---|---|
| 1 | MD capture ring plus fallbacks; the `md_hook` state is reported | W1 | wired: `[capture]` counters in the log, not yet measured on two nodes |
| 2 | A client kills an NPC ghost: gone within 500 ms on the authority and a second client; an NPC killed by NPCs disappears for all | W2 | **not measured** - needs the second node (8.6) |
| 3 | The client dies, its ghost disappears, it re-joins after reload with no duplicate ghost | W3 | **not measured** - needs the second node |
| 4 | A client sells 500 units: the authority station stock rises by 500 within 10 s, no frame spike over 2 ms | W4 | **not measured** - needs the second node |
| 5 | A client-built station appears for the authority and the other clients at the same position, no duplicate on the builder | W5 | **not measured** - needs the second node |
| 6 | A capture changes the owner on all nodes; the captured ship survives an authority save/load under the right faction | W5 | **not measured** - needs the second node |
| 7 | NPCs on the authority can damage and kill a client player (stretch) | W3 | **not measured** - needs the second node |
| 8 | Chat moderation hooks, `/who`, `/ping`, `/w`, `/t` | W7 | **no implementation** (M5-28 not started); the pass-through wrapper is in `x4mp_chat.lua` |
| 9 | Two hostile teams show red targeting on both sides; allied takes effect within 2 s; `admin_only` switch moves the avatar's owner | W7 | **partly**: `teams: applied seq=1 slots=1 relations=1 active=1 mismatches=0 player_locked=0` ran in game (8.4); no second node, no UI check |
| 10 | A 1M Cr purchase on a client shows in the ledger within 1 s; two simultaneous purchases in Shared mode converge within 2 s; a forced local edit is corrected within 30 s | W6 | **partly measured** - see 8.3: convergence loop proven, correction blocked by a units bug |
| 11 | Send credits, team pool and the Economy screen, first cut | W7 | **no implementation** (M5-29 not started) |
| 12 | Loans and escrowed offers settle atomically, rollback exact, residuals 0 | W7 | **no implementation** (M5-31 not started) |

## 8.0 What was running

| Thing | Value |
|---|---|
| Server | `server\src\X4MP.Server\bin\Debug\net10.0\x4mp-server.exe --data-dir C:\Users\Admin\AppData\Local\Temp\x4mp-s8`, env `X4MP__Net__AdminPassword=s8admin`, TCP 47780 / UDP 47781 / GUI 47790 |
| Game | `X4.exe -debug all -logfile x4mp_dev.log -skipintro`, always started with `-WorkingDirectory 'D:\Steam\steamapps\common\X4 Foundations'` |
| Save | `x4mp_s108_a`, authority `Admin`, team 1 (`auto`), `role=authority`, `admin_password=s8admin` |
| MCP | `POST http://127.0.0.1:7779/mcp`: `is_game_loaded`, `load_game {"name":"x4mp_s108_a"}`, `run_lua` |
| Mod log | **`C:\Users\Admin\AppData\Local\X4MP\logs\x4mp.log`** (UTC, `Z` suffix) - the native `X4MP_CLOG` lines live here |
| Game log | `D:\Admin\Documents\Egosoft\X4\183887795\x4mp_dev.log` - every Lua `[=ERROR=] [X4MP] ...` line |
| Native host log | `D:\Admin\Documents\Egosoft\X4\183887795\x4native\x4native.log` - load / universe_ready / game_saved |

Two log-path traps cost time in this session and are worth knowing:

- `x4native\x4mp\x4mp.log` is **not** the mod log. It holds only the hello pair (`x4native extension log` + `x4mp 0.1.0
  hello`) and never grows. The mod's own log is `%LOCALAPPDATA%\X4MP\logs\x4mp.log`
  ([paths.cpp:68](../mod/native/host/paths.cpp): `p.default_log_file = p.dir / "logs" / "x4mp.log"`, and
  [mod_host.cpp:78](../mod/native/host/mod_host.cpp) uses it when the config names no file).
- Renaming `extensions\x4mp` to `extensions\x4mp.off` does **not** disable the extension - X4 still loads it from the
  renamed folder. To disable it, the folder has to leave `extensions\` entirely. That mistake invalidated one control
  experiment before it was caught.

## 8.1 Crash 1: the money poll recursed through the helper flush (`0xc00000fd`)

**Symptom.** Loading `x4mp_s108_a` ended at

```
[General] ... X4Native: MD signalling game_loaded to Lua
```

and the process exited. X4 wrote `X4_900_<date>.dmp`; `python mdmp.py <dmp>` reports
`0xc00000fd STACK_OVERFLOW`, `address ...\lua51_64.dll+0x61f14`, with no `x4native.dll` / `x4mp.dll` frame in the
walked stack.

**Bisect.** Each iteration: rewrite the installed `extensions\x4mp\ui.xml` (comment a `<file name="ui/x4mp_X.lua" />`
line, write with `[System.IO.File]::WriteAllText` + `UTF8Encoding($false)`, first bytes must be `60,63,120`), relaunch
X4, `load_game {"name":"x4mp_s108_a"}`, watch for `is_game_loaded`:

| Active UI modules | Result |
|---|---|
| all 21 | crash |
| all except `x4mp_ui_standalone.lua` | crash |
| only `ui/x4mp_bridge.lua` | loaded |
| all except `ui_standalone`, `hud`, `onfoot`, `money` (17) | loaded |
| all except `ui_standalone` + `money` (19, hud and onfoot on) | loaded |

**Cause.** `x4mp_money.lua` armed its self-re-arming timer for `t` - a time that is already due - and the callback
re-armed itself from inside the flush, so `arm -> flush -> callback -> schedule -> arm` never unwound. The hud and
onfoot loops arm at `t + interval` and were innocent *in that crash*.

**Fix.** `mod/extension/x4mp/ui/x4mp_money.lua`: `local FRAME_S = 0.01` and the arm is `t + FRAME_S`, with a comment
naming the trap. Pinned by two assertions in `mod/tests/lua/test_money.lua` (the armed time must be strictly in the
future, at load and on the re-arm).

## 8.2 Crash 2: the same recursion, delayed, and why a future arm was not enough

After the fix the game loaded with 20 modules and hosted a session, and then died **141 s after the authority
checkpoint**, again `0xc00000fd` in `lua51_64.dll`. Correlation in `x4mp_dev.log` (game time):

```
2737.04 [X4MP] authority: SaveGame called for x4mp_ckpt_1b96d95b2d2cd8cb
2737.04 *** Context:md.X4Native.x4native_GameSaved: X4Native: MD signalling game_saved to Lua
2737.04 [X4MP] hud: loop restarted (gfx_ok)
2737.04 [X4MP] onfoot: loop restarted (timer lost)
2737.04 [X4MP] money: loop restarted (the timer was lost)
```

A save drops every pending `Helper` timer; all three loops re-armed at the same instant, and the stack blew up ~140 s
later. **Not ours?** A control run with `extensions\x4mp` moved out of `extensions\` played the same save for
**601 s without a crash and without a dump**, so the recursion is in the mod, not in X4 or x4native.

**The dump says what the recursion is.** Scanning the 1 MB stack region of every dump of the day (09:16:47, 09:23:12,
09:32:46, 10:27:53, 10:39:10, 10:46:32, 10:55:43) for 8-byte words that land inside a loaded module gives the same four
addresses in all seven, and the same depth within a dozen frames:

```
8043  X4.exe+0xaf7150          (10:46:32, crash at game_loaded)
8043  lua51_64.dll+0x2c95      (luaD_call)
8042  lua51_64.dll+0x4c00c     (luaV_execute)
8042  X4.exe+0xaf7262
```

~8040 levels of `X4.exe C function -> Lua VM -> X4.exe`: a Lua callback that calls an X4 C function which
synchronously runs the same Lua callback. `X4.exe+0xaf7150` is not in the export table (2378 exports parsed, none near
`0xaf71xx`), so the function cannot be named; the shape is what identifies it.

**Bisect with a watch long enough to see the crash.** Every earlier "loaded" run had been killed ~30 s after loading,
so nothing was ever proven stable; this crash needs 140 s - 6 min. Each row below is a full load + play:

| Active UI modules | Watch | Result |
|---|---|---|
| all 21 | ~3 min | crash |
| all except `ui/x4mp_ui_standalone.lua` | ~3 min | crash |
| only `ui/x4mp_bridge.lua` | 722 s | clean, no dump |
| `bridge` + `money`, `ffi.C.IsStartmenu` stubbed to `false` | 721 s | clean, no dump |
| `bridge` + `money`, unpatched | 170 s | crash during load |

The fourth row is the decisive one, and its log says why it was clean: the install-only diagnostic tail
`[X4MP] money: diagnostic: isStartMenu stubbed to false (no ffi.C call per poll)`. **The crash is the FFI call in the
money poll, not the timer arm.** `x4mp_money.lua` polls `GetPlayerMoney()` every frame, and every poll asked
`X4MPBridge.isStartMenu()`, which ran `pcall(require, "ffi")`, `pcall(ffi.cdef, "bool IsStartmenu();")` and
`pcall(ffi.C.IsStartmenu)` from scratch each time. That is ~100 calls a second into the game's own UI Lua state, and
it re-enters the engine callback pair the dump shows; the `inArm` re-entry guard could not have prevented it, because
nothing was re-entering our own arm call.

**Fix.** Two changes, both in the repo:

- `mod/extension/x4mp/ui/x4mp_money.lua:33-45`: the start-menu answer is cached for `MENU_S = 1` second
  (`inStartMenu()` returns `O.inMenu` while `t - O.menuAt < MENU_S`), so the game is asked at most once a second
  instead of once a frame.
- `mod/extension/x4mp/ui/x4mp_bridge.lua:322-337`: `bindStartMenu()` resolves `require("ffi")`, `ffi.cdef` and
  `ffi.C.IsStartmenu` once and keeps the function pointer; `B.isStartMenu()` then only calls it.

Pinned by two Lua cases: `test_money.lua` "the start-menu check is asked once a second, not once a frame" (99 polls
inside one second ask zero times, the next second asks once) and `test_bridge.lua` "isStartMenu: the ffi symbol is
bound once, the answer stays live" (`ffi.cdef` runs exactly once across three calls, the answer still follows the
live value).

**Hardening kept from the earlier attempt.** The first diagnosis was the timer arm, and the future-dated arm alone
cannot help if X4 runs a due callback inside the arm call for another reason, so all three loops also refuse to
re-enter. Same shape in `x4mp_money.lua`, `x4mp_hud.lua` (`:526`) and `x4mp_onfoot.lua` (`:304`):

```lua
if O.inArm then return end                       -- re-entered from inside our own arm call: never recurse
...
local delay = FRAME_S                            -- hud: H.config.interval, onfoot: O.interval
for _ = 1, 4 do
  local ranNow = false
  O.inArm = true
  Helper.addDelayedOneTimeCallbackOnUpdate(function()
    if O.inArm then ranNow = true; return end    -- Helper ran it synchronously inside the arm call
    ...work...; pcall(schedule)
  end, false, t + delay)
  O.inArm = false
  if not ranNow then return end
  delay = delay * 10
end
O.timerPending = false
log("the loop paused until the next event")
```

Pinned by a new Lua case in `mod/tests/lua/test_money.lua` ("a Helper that runs the callback inside the arm call
cannot recurse"): the stub calls the callback inside `addDelayedOneTimeCallbackOnUpdate`, and the test asserts exactly
4 arms, no poll and `timerPending == false`.

**Verification.** The fix was deployed to `extensions\x4mp` (20 modules active, `ui/x4mp_ui_standalone.lua` still
commented) and the session was re-run: load, join as authority, and play. See 8.7 for the result.

## 8.3 Wallet: the node looked in the wrong wallet, then wrote in the wrong unit

**8.3.1 The wrong wallet kind (fixed).** With the session Running and 10000 credits seeded, the client loop reported

```
[wallet] local=10000 ... server=0 target=10000 pending=4/10000 updates=0 ... acked=0 ... booked=1 sent=4
```

`sent` grew every 30 s and no `WalletUpdate` ever matched. The server had booked the delta (`economy_delta_seq`
`(1,1,6)`, `ledger_tx` row `GameIncome player:1 CreditDelta`, `wallets` `team_shared 120000`), so the mismatch was on
the client: `wallet_hub.cpp:255` chose `kKindTeamPool` in Shared mode, while the server books Shared into
**TeamShared** ([EconomyService.cs:155](../server/src/X4MP.Core/Economy/EconomyService.cs):
`if (AppliedMode == EffectiveCreditMode.Shared && AppliedTeamOf(playerId) is { } team) return WalletId.TeamShared(team);`).
[docs/protocol.md:1043](protocol.md) says the same: "`Shared`: a `TeamShared` wallet per team, and no pool".
`kKindTeamPool` became `kKindTeamShared`, and the negative test now uses `TeamPool` with another owner.

**After the fix, same session, verbatim from `%LOCALAPPDATA%\X4MP\logs\x4mp.log`:**

```
03:53:11.221Z [net] wallet: the link is back, 2 unacked delta(s) go again
03:53:24.709Z [wallet] local=11000 known=1 changes=2 echoes=0 applies=1 quiet=0 bad=0 expected=100000 server=111000 target=111000 pending=1/0 updates=3 stale=1 converged=2 writes=1 failed=0 acked=2 dropped=0 booked=2 sent=3 ...
03:53:28.152Z wallet: local 11000 -> 122000 (target 122000)
03:53:54.689Z [wallet] local=11110 ... updates=5 stale=1 converged=3 writes=2 failed=0 acked=4 ... booked=3 sent=5
```

`updates`, `acked`, `writes` and `converged` are non-zero and `bad=0` / `failed=0`: the ledger answers, the delta is
acked and the convergence path runs. **This is the M5-23 loop working end to end.**

**8.3.2 The wrong unit (fixed, not yet re-measured).** The convergence write still did not move the game's money the
way it should: a `+100000` credit adjustment moved the player from 10000 to **11000**, i.e. by 1/100.
[decisions.md:519](decisions.md) already had the answer: "MD `player.money` and `AddPlayerMoney` use **cents**; Lua
`GetPlayerMoney` uses credits. The mod converts at that edge, and the wire stays in whole credits." The edge did not
convert. `GameApi::add_player_money` (`mod/native/game/game_api.cpp`) now multiplies by
`GameApi::kCentsPerCredit = 100` and refuses a delta whose cents would overflow, with the unit and the session-8
symptom in the header comment (`game_api.h:218-228`) and a test that pins both the factor and the refusal.

## 8.4 What the session did prove (quoted lines)

| Feature | Line |
|---|---|
| HUD alive | `[=ERROR=] 2731.13 [X4MP] hud: view Helper6=X4MPHud[hud=1,pc=1] frames=1` |
| Teams applied | `[General] 2732.23 [X4MP] teams: applied seq=1 slots=1 relations=1 active=1 mismatches=0 player_locked=0` |
| Chat wrappers | `[=ERROR=] 2734.98 [X4MP] chat: wrappers installed / installed / installed` |
| Authority sector map | `[=ERROR=] 2720.85 [X4MP] authority: sector map sent: 152 sectors, 0 dropped (MD announced 152)` |
| Checkpoint | `[=ERROR=] 2737.04 [X4MP] authority: SaveGame called for x4mp_ckpt_1b96d95b2d2cd8cb` |
| Server admission | `[10:21:38 INF] conn 1: admitted Admin (player 1) as Authority, Client, Admin` |
| Economy seeded | `[10:21:58 INF] seeded the economy with 10000 credits from the save: None` |
| Session Running | `[10:21:58 INF] ...: AuthorityLoading -> Running (first checkpoint stored)` |
| Presence roster | `[sess] onfoot: linked=true reports=160 bad=0 out=121 failed=0 no_clock=0 ... roster=2 remotes=0 actors=0/0` |
| Budget | `[perf] stats: fps=8.0 frame_p95=146.65ms mod_p95=0.214ms mod_max=0.626ms connected=1 rtt=0.4ms udp=active ...` |

`mod_p95=0.214 ms` is well inside the 2 ms budget the AC uses for trade application, and the UDP lane came up
(`udp=active`, `udp_loss=0.0%`).

## 8.5 What is still missing before the AC table can be filled

1. **The second node.** Every AC except 10 needs two game nodes; `out\fakenode\X4MP.FakeNode.exe client` exists and
   was not used yet.
2. **W7 remainder.** M5-28 is done (`/who`, `/ping`); M5-27 needs the two-node run; M5-29..M5-31 (economy screen,
   notifications, escrowed settlement in the mod) are not started, so AC 11 and 12 (mod side) and the UI half of 9
   have no implementation.
3. ~~**`x4mp_ui_standalone.lua`**~~ - **fixed on 2026-10-30, §8.11.** The close path and the delayed reopen now carry a
   re-entrancy guard, the module is enabled in the repo and in the installed `ui.xml`, and it no longer has to be
   re-commented after a deploy. The dumps taken while it was enabled (09:16, 09:23, 09:32) showed the *same*
   four-address cycle as 8.2 - an engine callback re-entered from Lua.
4. ~~**The 60-minute auditor session** (M5-34)~~ - **done**, §8.10.

## 8.6 The log greps

```
# the wallet loop, every 30 s
Select-String -Path "$env:LOCALAPPDATA\X4MP\logs\x4mp.log" -Pattern '\[wallet\]' | Select-Object -Last 2

# the same for the event/economy counters
Select-String -Path "$env:LOCALAPPDATA\X4MP\logs\x4mp.log" -Pattern '\[capture\]|\[kill\]|\[trade\]|\[economy\]'

# the Lua half, every module's own line
Select-String -Path 'D:\Admin\Documents\Egosoft\X4\183887795\x4mp_dev.log' -Pattern '\[X4MP\]' | Select-Object -Last 20

# the server's admission / checkpoint / economy
Get-Content 'C:\Users\Admin\AppData\Local\Temp\x4mp-s8\server.out.log' -Tail 20
```

The `[wallet]` line is cut at ~272 characters by the log writer, so its tail (`unsent= events= wallet=: switched=
probes= debt= clamped=`) never reaches the file; `Select-String` shows the cut line as is.

## 8.7 Verification run after the fix

The FFI fix was deployed (20 modules active, `ui/x4mp_ui_standalone.lua` still commented) and the session was re-run
from a cold start: launch, load `x4mp_s108_a`, join as authority, then play. Verbatim from the run:

```
launched pid=22088 at 13:01:57
menu after 140s
load: {"id":1,"jsonrpc":"2.0","result":{"content":[{"text":"ok:x4mp_s108_a","type":"text"}],"isError":false}}
LOADED after 170s
join: {"id":2,"jsonrpc":"2.0","result":{"content":[{"text":"ok:true / nil","type":"text"}],"isError":false}}
alive 120s
alive 240s
alive 361s
alive 481s
alive 601s
alive 721s
alive 841s
alive at end: True elapsed=901s
=== dumps newer than the run start
(none)
```

**901 s of play with all 20 UI modules, a join and no dump.** Before the fix the same save died at 170 s, 141 s after
a checkpoint and within 3 minutes in every other run of the day. The last `[X4MP]` lines show the modules alive to the
end (`bridge ready (api present, trigger=gfx_ok)`, `authority: sector map sent: 152 sectors, 0 dropped`,
`hud: view Helper6=X4MPHud[hud=1,pc=1] frames=1`), and the newest dump on disk is still the 12:39:25 one from the run
*before* the fix.

**One thing this run did not prove.** The wallet loop stayed at `server=0 ... updates=0 acked=0` because the server's
session was already `Ended`: the server log's last entry is `[10:38:21 INF] X4MP.Core.Session.NodeGateway conn 2:
refused with NotJoinable: the session is Ended`, and this run never saved a checkpoint (no `SaveGame called` line), so
the session never left `AuthorityLoading`. The Lua `join` returns `ok:true` as soon as the native side accepts the
verb; admission is the server's answer. The wallet half of AC 10 therefore needs a fresh server session, not another
game run.

## 8.8 Wallet: the save's balance is a target, not an addition (server fix)

With a fresh server session the wallet loop finally ran end to end, and it exposed a second defect on the server side.
Verbatim from `%LOCALAPPDATA%\X4MP\logs\x4mp.log` and `server2.out.log`:

```
[wallet] local=110000 ... server=110000 target=110000 pending=1/0 updates=2 converged=1 writes=1 failed=0 acked=1
[13:23:53 INF] conn 2: admitted Admin (player 1) as Authority, Client, Admin
[13:24:15 INF] EconomySaveSeeder seeded the economy with 110000 credits from the save: None
[13:24:15 INF] session 1bf543d5-...: AuthorityLoading -> Running (first checkpoint stored)
[wallet] local=220000 ... server=220000 target=220000 updates=3 converged=1 writes=2 failed=0 acked=2
```

The player's money **doubled**: 110000 before the seed, 220000 after it. The order explains it, and the server's own
ledger shows both steps (`ledger_tx`, session 3): `StartingCredits team_shared +100000 -> 100000` at 06:23:53, the
mod's `CreditDelta +10000 -> 110000` at 06:23:57 (it books `local - server`, so the two sides agree), then
`SaveMoney +110000 -> 220000` at 06:24:15. The fresh wallet is therefore never empty - the module grants
`EconomyOptions.StartingCredits` = 100000 when the player registers
([EconomyOptions.cs:35](../server/src/X4MP.Core/Economy/EconomyOptions.cs)) - and the seeder **posted the save's full
110000 on top** of a balance that already matched the game. `SessionId` is `Guid.NewGuid()` ([GatewayState.cs:60](../server/src/X4MP.Core/Session/GatewayState.cs)),
so no ledger was restored from an older session - the pre-seed 110000 was booked in this very session.

The units are not involved: `AddPlayerMoney` now takes credits and multiplies by `kCentsPerCredit` at the edge, which
is why the earlier `+100000` credit correction moved 10000 -> 110000 instead of 10000 -> 11000.

**Fix.** `EconomyService.SeedSaveMoney` now treats the save's amount as the wallet's **target**: it posts
`target - current` to each destination and lets `World` be the counter-account for exactly that difference, so the
ledger stays balanced, a repeated seed moves nothing, and a wallet the mod has already filled is not filled twice.
The regression test `EconomyServiceTests.SaveMoneyBringsTheWalletToTheSavesBalanceInsteadOfAddingToIt` books 500 for
the authority through `BookCreditDelta` (the mod's own path), seeds 777, and asserts the wallet is 777 - not 1277 -
with `TotalBalance() == 0`, then repeats the seed and asserts 777 again. `dotnet test` on `X4MP.Core.Tests`:
**1379 passed, 0 failed**.

`pending=N/0` is not a money defect either. The stats line prints `pending=<unsent rows>/<pending_sum>`
([wallet_feature.cpp:207-208](../mod/native/features/wallet/wallet_feature.cpp)), and `WalletHub::book_probe`
([wallet_hub.cpp:103-111](../mod/native/features/wallet/wallet_hub.cpp)) pushes a row with `delta = 0` on purpose
("nothing to book: the server answers with acked_delta_seq and moves no money"). Those rows add 0 to the sum and never
move the target, so the growing count is ack bookkeeping, not money.

**Re-verified in game.** `server-rejoin.ps1` started a fresh server (session `f03853d3-28ae-4943-a53c-5838cfa24e4c`,
new ledger, same data dir) and the still-running game (pid 22088, local money 220000) rejoined. The mod's counters are
cumulative for the process, so its `[wallet]` line is not the proof here - the server's own ledger is. From
`x4mp.db` (`ledger_tx`, `ledger_entries`, `wallets`):

```
01M638JHD0BZJ4ANVQA3VDNDMB  07:21:45  StartingCredits  team_shared +100000 -> 100000   world -100000
01M638K6X23WWB36REE2DC903E  07:22:07  SaveMoney        team_shared +120000 -> 220000   world -120000
wallets (session 4): team_shared/1 = 220000, world/0 = -220000
```

The fresh wallet was not empty: the module grants `EconomyOptions.StartingCredits` = 100000 when the player registers
([EconomyOptions.cs:35](../server/src/X4MP.Core/Economy/EconomyOptions.cs)). The old additive seed would have posted
the full 220000 on top and produced 320000; it posted 120000 and landed exactly on the save's 220000, with `world` as
the exact mirror so the totals stay at 0. Compare session 3, an hour earlier on the pre-fix build:

```
01M6358JJF4GJZZMTYQ6437EWF  06:23:53  StartingCredits  team_shared +100000 -> 100000
01M6358PD3CPA9XEVJQ7RGSV5R  06:23:57  CreditDelta      team_shared  +10000 -> 110000   (the mod's correction)
01M63598F80ZY295BRFH6W6ZH6  06:24:15  SaveMoney        team_shared +110000 -> 220000   (the save held 110000)
```

The wallet ended at 220000 for a save that held 110000. Session 4 ends at the save's own number.

In game, the wallet line then held still for 280 s - `local=220000 ... server=220000 target=220000 pending=52/0
updates=3 writes=2 failed=0 acked=2` - the mod had nothing to converge, and no dump appeared (the newest is still
`X4_900_2026_10_29_12_39_25.dmp`, from before the crash fix).

## 8.9 M5-28 in game: `/who` and `/ping`

The chat module was deployed with the two new branches (`x4mp_chat.lua:244` and `:262`) and `ui/x4mp_ui_standalone.lua`
was re-commented in the installed `ui.xml:28` right after the deploy (the repo copy has it enabled). A fresh Debug
server started (pid 19204, data dir `%TEMP%\x4mp-s8`, `X4MP__Net__AdminPassword=s8admin`), the game launched as
pid 19328 with `-debug all -logfile x4mp_dev.log -skipintro`, `x4mp_s108_a` loaded after 170 s, and the node joined as
authority (`ok:true / nil`). Ten seconds later the status topic already read `ingame|ping=196|players=1`, and the log
showed the wrappers installed: `[X4MP] chat: wrappers installed / installed / installed`.

Both commands were then sent through the **real vanilla global** - the wrapper, not the module directly:

```lua
local before = #X4MPChat.ring
local ok1 = pcall(ExecuteDebugCommand, "who", "")
local ok2 = pcall(ExecuteDebugCommand, "ping", "")
-- then read X4MPChat.ring[before + 1 .. #X4MPChat.ring]
```

Result: `who_ok=true ping_ok=true new_lines=3`. The three lines, decoded from the ring (the game runs the Russian text
page, so these are the l007 texts; the console renders them as mojibake, the bytes are valid UTF-8):

```
Игроков в сессии: 1
#ffff8000#AdminX - вы  |  Команда 1  |  в сети  |  40 мс  |  -  пешком, здесь
Пинг: 196 мс, сервер 127.0.0.1:47780, состояние ingame
```

The escape codes are the module's own: `\027#ffffcc33#` is the X4MP author colour and `\027#ffff8000#...\027X` is the
player's team colour (0xffff8000), with `- вы` marking the local player. A command that is not ours, `/x4mp status`,
added `chat_lines_added=0` - the wrapper returned false and the previous handler still ran.

No crash: no `X4_900_*.dmp` newer than the run start, and the game was alive when the script finished at 08:01:40.
This closes the in-game half of AC 8: each command answers with a system line, and an unknown command still reaches the
vanilla handler. The two negative branches (no roster yet, no ping) are covered by the Lua cases, because a live
session always has both - Lua gate **280 passed, 0 failed**.

## 8.10 M5-34: the 60-minute ledger-auditor session

The M5 exit gate asks for a clean ledger auditor after a 60-minute session with purchases on every node
([m5-plan.md:528-534](m5-plan.md), [mod-design.md:1874-1880](mod-design.md)). The run used FakeNode only: one X4
holds 3.7 GB of this machine's 24 GB and leaves 4.5 GB free, so a second real node does not fit - the same reason
AC 2-7 are still open. It ran on its own ports and data directory so it could not touch the in-game session: node TCP
47980, UDP 47981, admin HTTP 47990, data dir `%TEMP%\x4mp-audit`, and the fresh Debug server
(`server/src/X4MP.Server/bin/Debug/net10.0/x4mp-server.exe`; the Release `out/win-x64/x4mp-server.exe` is from 20.10
and predates the seeder fix of section 8.8).

The server needs the three trade settings, or every proposal is refused with `ScopeDenied`
([m1-exit-report.md:19-20](m1-exit-report.md)): `X4MP__Teams__JoinMode=Lobby`, `AllowCreateInLobby=true`,
`AutoAssign=Balance`, `AllowAssetTransfer=true`, `X4MP__Economy__TradeScope=Anyone`,
`TradeRequiresProximity=false`, plus `MaxConnectionsPerIp=64` and `MaxPlayers=16` for a six-node swarm.

The swarm, five fake clients and the fake authority:

```powershell
X4MP.FakeNode.exe swarm --server 127.0.0.1:47980 --clients 5 --with-authority --teams 3 --relations ffa `
  --economy heavy --dupe-attack --income-rate 2 --trade --trade-fail 10 --trade-timeout 5 --verify `
  --duration 3600 --admin-url http://127.0.0.1:47990 --admin-user admin --admin-password <pw>
```

`--trade-fail 10 --trade-timeout 5` makes the authority fail and withhold real orders, so the escrow has to
compensate; `--dupe-attack` replays and races every request; `--economy heavy` is 6477 economy requests in the hour.
Result, `swarm exit=0` after 3614 s (`%TEMP%\x4mp-audit\swarm.log`):

```
summary: nodes=6 joined=6 errors=0 pings=21594 rtt avg=1.28ms max=1003.74ms elapsed=3614.4s ingame=6
invariants: auditor=ok violations=0 frozen=False ledger-transactions=50250 ledger-sum=0 duplicate-requests=0
  delta-books=6/6 wallets(match=20 behind=0 ahead=0 drift=0)
  loans=[Active=1,Declined=26,Forgiven=29,Repaid=174,Withdrawn=5] overdue-loans=0
  trades=[Completed=653,Expired=2,InDoubt=5,RolledBack=91] in-doubt=[233,383,456,473,668]
dupes: replays=300(idempotent=184 rate-limited=116 other=0) reuses=300(rejected=202 rate-limited=98 other=0)
  races=475(copies=1425) overdraw-pairs=117(both-ok=0) delta-resends=5352 duplicate-effects=0
reconciliation: nodes=6 updates=109027 steps-checked=48279 results-checked=10927 unverifiable=0 stale=6 unacked=0 drift=0
trade-authority: orders=749 applied=656 failed=93 withheld=29 silent=5 duplicate-orders=0 queries-answered=24
  queries-ignored=15 unknown-queries=0
trade: clients=5 proposals=1390 accepts=1554
  results(one per party)=[completed=1306 rolled-back=182 cancelled=0 rejected=0 expired=4] open-at-end=10
  request-rejects=[TooManyOpen=60,RateLimited=1312,AssetUnavailable=72]
economy(heavy): clients=5 requests=6477 ok=3915 rejected=2562 skipped=1142
  sent=[Donate=1139,LoanForgive=47,LoanOffer=600,LoanRepay=528,LoanRespond=384,LoanWithdraw=7,PoolDeposit=703,PoolWithdraw=604,Transfer=2465]
  rejects=[InsufficientFunds=43,ScopeDenied=603,TooManyOpen=1,RateLimited=1915]
verify: clients=5 frames=416490 entries=12313861 checked=12313861 ghosts=692 spawns=2602 despawns=1910
  checksums-ok=3575/3575 resyncs=0 position-errors=0 errors=0
```

and the admin API at the end of the run:

```
POST /api/v1/economy/audit -> { "ok": true, "economyFrozen": false, "violations": [] }
GET  /api/v1/economy/summary -> moneySupply=35837937 inEscrow=14307 openLoans=1 overdueLoans=0 outstandingDebt=1621
                                openTrades=5 inDoubtTrades=5 frozenWallets=0 economyFrozen=false
```

How to read it:

- **Conservation and idempotency are clean.** `ledger-sum=0` after 50250 transactions, `duplicate-requests=0`, and
  `duplicate-effects=0` although 300 replays, 300 reused request ids and 475 race copies (1425 copies) went in. The
  117 overdraw pairs produced `both-ok=0`: the second debit of a pair never passed.
- **Replication agrees with the server.** 48279 reconciliation steps and 10927 checked results on six nodes with
  `drift=0`, `unacked=0`, `unverifiable=0`. The `stale=6` are steps a newer update superseded, not mismatches.
- **Rollbacks stay atomic.** 182 of 1554 trade results rolled back (the injected `--trade-fail`), the ledger still
  sums to 0, and `duplicate-orders=0` with `orders=749 applied=656 failed=93 withheld=29 silent=5`.
- **The 5 InDoubt trades are the `silent=5` case** - an authority that never answered, so the order cannot be resolved
  either way. They hold the `inEscrow=14307` credits; the auditor reports them as a terminal state, not a violation,
  and the admin refund path is what closes them. `overdue-loans=0` with one `Active` loan at the end is the same
  picture for loans.
- **The economy was live, not idle:** 2465 transfers, 1139 donations, 703 pool deposits, 600 loan offers and 1554
  trade accepts - purchases on every node, as the gate asks.

What this does not prove: the run is headless. It proves the server's ledger, escrow and replication under load, not
the mod's in-game screens (AC 11, the UI half of AC 9) and not the two-node game behaviour of AC 2-7 and 10. Those
still need the second machine.

## 8.11 The standalone screen is back: the close path got its guard (2026-10-30)

The last single-PC defect of M5 is closed. `x4mp_ui_standalone.lua` is enabled again in both `ui.xml` files, and the
scenario that produced three stack-overflow dumps now runs clean.

**What was measured first.** The documented candidate (8.5 item 3) was that `menu.onCloseElement` calls
`Helper.closeMenu`, which calls `menu.onCloseElement` again. That is not what happens. In the running game, with the
window up (`shown=true|frame=true|rows=11`):

```
pcall(Helper.closeMenu, X4MPMenu, "close") -> ok:closeOk=true|err=nil|shown=nil|frame=table: 0x020919946ec0
pcall(X4MPMenu.onCloseElement, "close")    -> ok:directOk=true|err=nil|frameBefore=true|frameAfter=nil
```

`Helper.closeMenu` clears `shown` and leaves `menu.frame` set, so it does **not** call our callback. The cycle is the
callback being entered again from the engine's own close path while the frame is being torn down.

**The fix** (`mod/extension/x4mp/ui/x4mp_ui_standalone.lua`):

- `menu.display()` is a guarded wrapper over the old body (now `drawFrame`): `menu.inDisplay` makes a re-entered draw
  return at once, and the flag is cleared on every path, including an error.
- `menu.onCloseElement` sets `menu.closing` before it touches the frame and clears it after `menu.cleanup()`, so a
  re-entered close returns immediately and the outer call finishes the job. `Helper.closeMenu` runs under `pcall`.
- The delayed reopen inside `menu.restoreStartMenu()` is dropped when `X4MPBridge.loadingSave` is set or when the window
  is open again - a callback queued before a session save started loading must not switch the game's screen afterwards.

**Evidence, in game (game pid 34784, save `x4mp_s108_a`):**

```
open via /x4mp        -> shown=true|rows=11|title=Сетевая игра
close via renderer    -> shown=nil|frame=nil, game alive
30 draw+close cycles  -> ok=30|err=0|first=nil|closing=nil|inDisplay=nil
8 open/close cycles, both close paths -> every one ok:true|nil|shown=nil|frame=false
delayed reopen forced (overStartMenu=true) -> reopen ran, OptionsMenu shown=true, game alive
save load with the window open -> load finished, is_game_loaded=true, renderer=standalone, wallet=per_player
```

The log agrees: `[X4MP] standalone: closed, reopening start menu (OpenMenu OptionsMenu, saved return record no)`,
`[X4MP] standalone ui loaded`, and the load itself (`Loading saved game 'x4mp_s108_a', first pass` / `second pass`).
No `.dmp` exists anywhere under `D:\Admin\Documents\Egosoft\X4\183887795`, where the three session-8 dumps were.

The Lua gate stayed at `293 passed, 0 failed` - no Lua test covers this module, the proof is the run above.