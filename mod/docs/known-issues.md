# Known issues found on the two-instance stand

What was found while running an authority and a client on one PC
([`two-instances-one-pc.md`](two-instances-one-pc.md)), 2026-11-02, X4 9.00 build 611726,
server and mod at the same HEAD. Every entry quotes the line it rests on; nothing here is
"looked wrong".

Severity is about the cost to a player or to a bug report, not about the amount of code involved.

| # | Issue | Severity | Status |
|---|---|---|---|
| KI-1 | `money: send refused` / `send money failed: no_api` printed on every start | Low (log noise, but as `[=ERROR=]`) | **Fixed** 2026-11-02, see below |
| KI-2 | Reconnect grace (60 s, and 120 s for the authority) is shorter than an X4 boot (2–3 min), so a restart never resumes | Medium (every restart costs a manual re-join) | **Fixed** 2026-11-02, default is now 300 s |
| KI-3 | Every transfer, pool move and loan between two players in the same team is refused as `SameWallet`, because the default `CreditMode=Auto` gives a one-team session one wallet | High (the whole economy is unusable in the normal two-player case) | **Explained** 2026-11-03: the refusal names `Economy:CreditMode=PerPlayer`; the default stays, workaround below |
| KI-4 | Offers were receive-only: the mod could not create a loan offer or a trade proposal, and could not accept or decline a loan offer | High (the checklist's own step 9 could not be performed by a player) | **Loans fixed** 2026-11-03; trade proposals still open, see below |
| — | Log rotation, timer-restart messages, restricted-function errors | Not bugs | Explained below so they stop being re-reported |

## KI-1: spurious money send at startup

**What happens.** Every start writes an error pair into the game log, in both instances, before
the bridge is available:

```
95: [=ERROR=] 24.10 [X4MP] send money failed: no_api
98: [=ERROR=] 24.10 [X4MP] money: send refused
104: [=ERROR=] 24.10 [X4MP] bridge ready (api present, trigger=gfx_ok)
```

The third line is the point: the bridge becomes ready **after** the balance was already refused.
The player is in no way affected — the next poll sends the true balance — but the game log now
carries `[=ERROR=]` lines on a healthy start, which is exactly how people learn to ignore errors.

**Reproduce.** Start any instance with the mod deployed. Counted once per process, in both
modes (normal and portable), at 24.10 s and 23.95 s after launch.

**Cause.**

1. `ui/x4mp_money.lua:129` — `pcall(O.poll)` runs at file load, on purpose:
   "The first reading happens at load, not one frame later: a player who joins a running session
   knows the balance at once."
2. `O.poll` (`x4mp_money.lua:48-53`) skips only when the game is in the start menu. Early in
   init `inStartMenu()` (`:37-45`) falls back to `false`, because `B.isStartMenu` is not bound
   yet, so the guard does not fire and `read()` (`:26-31`) returns a real balance.
3. `B.send("money", …)` (`:69`) reaches `rawSend` (`ui/x4mp_bridge.lua:339-347`), which returns
   `false, "no_api"` while `B.getApi()` is nil.
4. `money` is deliberately **not** in the bridge's queueable set
   (`ui/x4mp_bridge.lua:80`: `ui_ready`, `request_status`, `extensions` only). The reason is
   documented in the module: a balance that arrives late is a wrong balance, and the next poll
   re-reads the truth anyway.
5. So `B.send` logs `send money failed: no_api` (`x4mp_bridge.lua:361`), returns `false`, and
   `O.poll` adds `send refused` (`x4mp_money.lua:75`). `O.sent` is not advanced, so no value is
   lost — the next successful poll sends the same balance.
6. Both messages go through `log()`, which is `pcall(DebugError, …)` (`x4mp_money.lua:23`),
   hence the `[=ERROR=]` prefix for what is an expected pre-init state. The native side logs
   nothing about it: the mod log (`x4mp.log`) contains neither `no_api` nor `send refused`.

**Suggested fix.** In `O.poll`, bail out before reading the balance when the bridge has no API
yet — the next timer tick retries:

```lua
if type(B) ~= "table" or type(B.getApi) ~= "function" or B.getApi() == nil then
    O.skipped = O.skipped + 1
    return
end
```

That keeps the "first reading at load" behaviour for a join into a running session (where the
API exists) and removes the false error on a cold start. Cheaper alternative: keep the flow and
demote the two messages to a non-error channel, or log them once — but the bail-out also avoids
doing the FFI `GetPlayerMoney` call for nothing.

**Fixed 2026-11-02.** The bail-out above is what `O.poll` does now
(`ui/x4mp_money.lua`, right after the start-menu guard): with no `B.getApi()` it counts
`O.skipped`, returns before `read()`, and writes nothing to the game log. The next tick retries,
so a join into a running session still reads at load. Covered by
`mod/tests/lua/test_money.lua` — "a poll before the bridge has an API neither reads nor logs an
error" asserts that `GetPlayerMoney` is not called and that the debug channel gains no line.

## KI-2: reconnect grace is shorter than the game boot

**What happens.** Restarting both instances always ends the session for both players instead of
resuming it:

```
01:32:24.152 player 1 (Host) detached: "SocketLost"
01:32:24.200 player 2 (Client) detached: "SocketLost"
01:33:24.152 player 2 left: ResumeGraceExpired
01:33:24.200 player 1 left: ResumeGraceExpired
```

60 s after the drop, both slots are gone and the session is back to `WaitingForAuthority`. The
game, meanwhile, needs 160 s (authority) and 195 s (client) to reach `Entering startmenu` at
these settings, and a save load comes after that. The resume window is closed before the first
window can even reconnect.

**Why both are 60 s.** The client slot uses `NetOptions.ResumeGraceSeconds` (`SessionActor.Nodes.cs:241`),
and the authority slot uses `Math.Max(ResumeGraceSeconds, AuthorityGraceSeconds)`
(`SessionActor.Nodes.cs:447`) — 120 s, which is still less than the 160 s boot measured here.
Values as shipped:

- `server/src/X4MP.Core/Net/NetOptions.cs:141-142` — `[Setting("Reconnect grace (s)", Min = 5, Max = 600)] public int ResumeGraceSeconds { get; set; } = 60;`
- `server/src/X4MP.Core/Session/SessionContracts.cs:35-36` — `[Setting("Authority grace: … (s)", Scope = SettingScope.Live, Min = 5, Max = 3600)] public int AuthorityGraceSeconds { get; set; } = 120;`

Note the scope difference: `AuthorityGraceSeconds` is `Live`, `ResumeGraceSeconds` has no scope
and therefore defaults to `Boot` (`Settings/SettingAttributes.cs:36-37` — "Default
`SettingScope.Boot`"). Changing the reconnect grace needs a server restart; changing the
authority grace does not.

**Workaround for the stand.** Set both windows well above the boot time — the maximum allowed is
600 s for the reconnect grace and 3600 s for the authority grace — and restart the server once
after raising `Net.ResumeGraceSeconds`. With a warm 300 s window a restart of both instances
resumes instead of rejoining, which also keeps the session's player ids and team slots stable
between runs.

**Suggested fix.** Raise the default of `ResumeGraceSeconds` to something a real client can
survive (300 s is the honest number for a game that spends minutes loading), or make the grace
start from the first connection attempt after a drop rather than from the socket loss. Either
way the boot-time dependency should be written down next to the setting, because 60 s reads as
generous until you time an actual X4 start.

**Fixed 2026-11-02.** The default is 300 s (`NetOptions.ResumeGraceSeconds`), with the reason in
a comment next to it. The authority slot keeps the longer of the two windows, so it now waits
300 s as well. The setting is `Boot`-scoped: a server started before this change needs one
restart. The tests that had the old number baked in now either pin the window they exercise
(60 s in `SessionActorTests`, which tests the arithmetic) or read the configured default
(`NodeGatewayTests`, `SessionActorIntegrationTests`).

## KI-3: the economy is dead in the normal two-player case

**What happens.** Two players join, land in team 1 (the default), and every `/mp send`,
`/mp deposit`, `/mp withdraw` and every loan offer comes back refused as `SameWallet` — "both
players use the same wallet". The ledger stays empty and nothing explains it. On the stand a
fake client sent nine `Donate` and two `LoanOffer` requests and the server rejected all eleven:

```
economy(casual): clients=1 requests=11 ok=0 rejected=11 skipped=3 sent=[Donate=9,LoanOffer=2] rejects=[SameWallet=11]
```

The transfer path itself is fine — the refusal is about the wallet, not the payload.

**Why.** `CreditMode` defaults to `Auto` (`server/src/X4MP.Core/Economy/EconomyOptions.cs:30-31`),
and `Auto` resolves to Shared when the session has a single team. In Shared mode the wallet is
per team, so a move between two members of that team is a move to yourself:
`EconomyService.Transfers.cs:33-34,127`, `EconomyService.Loans.cs:99,214,297` and
`EconomyService.Trades.cs:822` all answer `EconomyReject.SameWallet`.

**Workaround.** Run the session with `Economy:CreditMode=PerPlayer`. The launcher passes only the
three `X4MP__Net__*` values (`native/features/server/server_launcher.cpp:89-94`), so the setting has
to be in the bundle's `appsettings.json`:

```json
  "X4MP": {
    "Economy": {
      "CreditMode": "PerPlayer"
    }
  }
```

Verified on the stand: with that file the same bot request produced a real loan offer, a ledger
row and the offer notice described in `../../docs/m5-exit-report.md` section 4.9.

**Suggestion.** Either make `PerPlayer` the default when the session has one team, or put the
reason into the refusal ("players share one wallet — start the server with
`Economy:CreditMode=PerPlayer`"). The rejection is correct; it just reads as a broken feature.

**Explained 2026-11-03 (the second half of the suggestion).** The default stays: a one-team
session sharing one wallet is a documented design decision (`docs/mod-design.md:2267`,
`docs/architecture.md:223`), not an accident, and flipping it would move every existing
single-team ledger to a different layout. What changed is what the player reads. A new constant
`EconomyService.SameWalletDetail` (`server/src/X4MP.Core/Economy/EconomyService.cs:79-86`) is
the detail of every `SameWallet` refusal — transfer (`EconomyService.Transfers.cs:127`), loan
offer/accept/repay (`EconomyService.Loans.cs:99`, `:214`, `:297`) and trade
(`EconomyService.Trades.cs:822`) — and it says: "both players use the same wallet; set
Economy:CreditMode=PerPlayer to give every player their own". The mod already prints the detail
next to the reason (`ui/x4mp_economy.lua:146`, `T(564)`), and the screen header already says
whether the wallet is shared (`:122`, `T(558)`). `SameWalletRefusalNamesTheSettingThatGivesEachPlayerAWallet`
(`server/tests/X4MP.Core.Tests/Economy/EconomyActionsTests.cs`) pins both the transfer and the
loan detail; `X4MP.Core.Tests` is 1382 passed, 0 failed.

Still a workaround, not a cure: on a one-team default session the transfer, pool and loan
actions fail until the server is started with `Economy:CreditMode=PerPlayer`.

## KI-4: offers were receive-only

**What happened.** The economy screen and `/mp` commands could move money, deposit into the
pool, repay and forgive a loan, and accept or cancel **a trade**. They could not

- create a loan offer or a trade proposal, and
- accept or decline a **loan** offer.

**Evidence (before the fix).** The verb took exactly these ops (`ui/x4mp_economy.lua:5`,
`E.send` at `:183-193`): `send|deposit|withdraw|repay|forgive|accept|cancel`. The native half
encoded only `credit_transfer`, `pool_deposit`, `pool_withdraw`, `loan_repay`, `loan_forgive`,
`trade_accept`, `trade_cancel` and `ledger_request`
(`native/features/economy_client/economy_wire.h:199-260`) — there was no `LoanOffer`,
`LoanRespond` or `TradeProposal` encoder. `Op::Accept` mapped to `encode_trade_accept`
(`economy_client_hub.cpp:312-326`), and `/mp accept` / `/mp decline` resolved their argument
through `resolveTrade` (`x4mp_economy.lua:246-259`), which scans trades only. So the hint the
notice prints for a **loan** offer, `Ответ: /mp accept 000001 или /mp decline 000001` (`T(574)`,
printed by `E.onNotice` at `:527`), answered "no such offer" (`T(531)`).

That is why the checklist's step 9 ("on A offer a loan while B is in game") could not be
performed as written: on the stand the offer had to come from `X4MP.FakeNode.exe` with
`--economy casual`.

**Fixed 2026-11-03 (loans).** The lender now makes an offer and the borrower answers it, both
from chat and from the screen:

- `native/features/economy_client/economy_wire.h:171-187` declares `LoanOfferData` and
  `LoanRespondData`; `:258` and `:269` encode them with `P::CreateLoanOffer` and
  `P::CreateLoanRespond`. The offer always carries the server's `offer_ttl_s` default (300 s):
  the player chooses the term, not how long the offer stands.
- `Op::LoanOffer` and `Op::LoanRespond` exist in the enum (`economy_client_hub.h:29`), in
  `op_name` (`economy_client_hub.cpp:64-65`), in `parse_op` (`:79-80`), and as two cases in
  `request()` (`:339` and `:358`). They repeat the server's own checks before a frame leaves the
  node (`EconomyService.Loans.cs:63-86`): a borrower, not yourself, a positive principal,
  `repay_total >= principal`, `auto_repay_pct <= 100`.
- `RequestParams` gained `repay_total`, `due_in_s`, `auto_repay_pct` and `accept`
  (`economy_client_hub.h:35-50`), and `parse_request` reads the JSON keys `repay_total`, `due`,
  `auto` and `accept` (`economy_client_feature.cpp:68-81`).
- `ui/x4mp_economy.lua`: `E.send` forwards the four new keys (`:187`); `/mp offer <player>
  <credits> [repay] [days] [memo]` sends `loan_offer` (`:485`); `resolveLoanOffer` (`:260`)
  finds an offer the borrower may answer, and `/mp accept` / `/mp decline` send `loan_respond`
  before falling back to the trade resolvers (`:472-474`); the screen has an "Offer a loan"
  button next to "Send credits" (`:366`, text `T(968)`), and an offer that asks for less than it
  lends is refused with `T(969)`.
- Help text `T(570)` in both languages names `offer`.

**Still open (trade proposals).** The server accepts a proposal only in one shape:
`CheckShape` (`EconomyService.Trades.cs:882`) requires **exactly one credits item and at least
one non-credits asset** per trade. A credits-only proposal is therefore illegal, and the mod has
no asset picker yet: a proposal needs a ware (with its source container) or a ship, chosen from
the player's own property. `TradeProposal` also needs `ttl_s` and, for ships, the
`TradeShipsEnabled` gate. Until that form exists, a trade proposal still has to come from
`X4MP.FakeNode.exe`; the accept/decline half of trades has always worked and is unchanged.

## Not bugs (checked, do not re-report)

- **`x4mp.log` grows to ~18 MB.** Rotation is implemented: 20 MiB per file, 5 files kept
  (`native/core/log/log.h:68-73`, `log.cpp:87-109`).
- **`money: loop restarted (the timer was lost)`** (`x4mp_money.lua:95`), **`onfoot: loop
  restarted (timer lost)`**, **`hud: loop restarted (gfx_ok)`** — by design after a menu
  transition; the timers are lost with the Lua context and are re-armed.
- **`Restricted function OnlineGetVersionIncompatibilityState() … Aborting call!`** and
  **`X4Native settings menu: __X4NATIVE_API not set - skipping`** — these appear only while
  Protected UI Mode is **on**. Turn it off in `config.xml` (`<uisafemode>false</uisafemode>`)
  and they disappear; see the instruction, step 4.
- **The game's `-logfile` stops growing after the main menu.** Observed on both instances: the
  file ends just after `Entering startmenu` and does not change while the mod keeps exchanging
  packets. Use the mod log (`[perf] stats: … connected=1 …`) to judge liveness.
- **`Get-Process X4` shows one instance only.** The second instance's process name is whatever
  the copied executable is called (`X4b`) — a naming artifact of the stand, not a hidden process.
- **The session sits in `AuthorityLoading` after both players join.** Expected: the session waits
  for the authority to load a save. It only looks stuck because nothing in the UI says so, which
  is a UX note rather than a defect.
