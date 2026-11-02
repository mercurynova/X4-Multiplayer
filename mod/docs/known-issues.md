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
