# M5 plan: events and the economy, on both sides

Status: plan v1, 2026-10-27 (after M4 code-complete). Owner: lead. Implementers: one agent per task, in a worktree
([execution-plan.md](execution-plan.md) §5, §6.1). In-game tester: the user (two PCs). `docs/in-game-session-7.md` is still
the open M4 run and is a prerequisite for the M5 acceptance sitting, because M5's numbers are measured in the same session
shape.

Read with: [mod-design.md](mod-design.md) §5 (event pipelines), §11.6-11.8 (relations, commands, save hygiene), §12
(credits and economy), [protocol.md](protocol.md) §14 (intents and events), §15 (economy), §16.2 (asset permissions),
[roadmap.md](roadmap.md) M5 and M5b rows, [decisions.md](decisions.md) ADR-019 (credits are int64 whole credits),
ADR-021 (loans), ADR-022 (escrowed trades), and the two schemas that are the wire contract:
`protocol/schema/events.fbs`, `protocol/schema/economy.fbs`.

---

## 0. What is already built (so that no wave rebuilds it)

M5 is a **mod-side milestone**. The server half shipped in M1 and the wire was designed before either side existed.

| Area | Where | State |
|---|---|---|
| Ledger, wallets, double-entry journal, `economy_requests` idempotency, `EconomyAuditor` + freeze | `server/src/X4MP.Core/Economy/*` (M1-E1) | done, property-tested |
| Credit modes Auto/PerPlayer/Shared, pool policy, `CreditDelta` booking with `seq` dedupe, `acked_delta_seq` in `WalletUpdate` | `EconomyService.cs`, `EconomyLedger.cs` (M1-E2) | done |
| Donate, transfer, pool actions, scopes, rate limit | `EconomyService.Transfers.cs` (M1-E3) | done |
| Loans: offer with principal escrow, accept/decline/repay/forgive, overdue, auto-repay | `EconomyService.Loans.cs`, `LoanModels.cs` (M1-E4) | done |
| Escrowed trades, asset locks, `AssetTransferOrder`/`AssetTransferConfirm`, InDoubt, rollback | `EconomyService.Trades.cs`, `TradeModels.cs`, `MirrorTradeWorld.cs` (M1-E5) | done |
| Economy admin REST + hub + web page | `EconomyService.Admin.cs`, `server/web` (M1-E6, E7) | done |
| Teams: registry, relation matrix, fanout, `ReassignPlayerAssets`, member changes | `X4MP.Core/Teams/*` (M1-T1..T5) | done |
| Wire: `Intent` with 9 bodies, `IntentResult` with 15 reject reasons, `GameEvent` with 11 kinds, 18 economy messages | `events.fbs`, `economy.fbs`, generated for C# and C++ | done |
| Server relay: every `IntentBody` validated (including `KillClaim`/`HitReport` interest checks), every `GameEventBody` fanned out with player/world filtering | `RelayModule.Intents.cs`, `RelayModule.Events.cs` | done |
| Chat relay, rate limits, moderation hooks | `RelayModule.Chat.cs` | server side done |

**What is missing is the mod, entirely.** A scan of all 207 C++ sources under `mod/native` finds no reference to
`GameEvent`, `Intent`, `IntentResult`, `WalletUpdate`, `CreditDelta`, `EconomyResult`, `TradeStatus` or `LoanStatus`.
There is no `features/events` and no `features/economy`. Every wave below is therefore about detecting something in the
game, saying it on a wire that already exists, and applying a result that the server already computes.

---

## Open questions for the user (answer these first)

The user is away ("продолжай"), so every wave proceeds with the recommended answer. Changing one later costs a wave, not a
milestone, unless the row says otherwise.

| # | Question | Recommended answer |
|---|---|---|
| Q1 | **How does the mod learn that something happened?** MD hooks are the only source that carries a killer; native has no event stream. | **All three layers, counted separately:** MD hooks primary, `event_player_*` MD fallbacks second, native polling last. AC item 1 asks for the `md_hook` state precisely because a silently dead MD half is the failure mode to avoid. |
| Q2 | **What is "the `md_hook` state"?** | A `hooks=` block in the existing 30 s budget line: which hook files answered, per-hook fire counters since the last line, the age of the last event, and the fallback count. One line tells a tester whether MD is alive. |
| Q3 | **Intent or GameEvent for a client-observed kill?** | `Intent{KillClaim}` upward — the authority applies it. `GameEvent` is A→S only and informational (events.fbs header). A client never sends a `GameEvent`. |
| Q4 | **Who destroys, and with what?** | The authority only. `SafeDestroy` (SelfDestructComponent, explosion) for a ghost within 20 km of a client camera, `SafeRemove` beyond it, both behind PlayerGuard. Clients apply the despawn, they do not destroy. |
| Q5 | **Trade money.** `TradeReport` carries `unit_price` and a comment saying credits are not synced, but §12 makes the ledger authoritative. | **The comment is stale.** `TradeReport` is about the station's stock only. Money moves as `CreditDelta` from the reconciliation loop (12.2), never derived from a price. |
| Q6 | **Station stock broadcast.** There is no `StationStock` message. | `EntityCargo` (0x0205) on the station net id. A client computes the delta against its own matched station and applies it through the actions shim; an unmatched station is skipped and counted, never guessed. |
| Q7 | **Station builds: plan or macro?** | **Macro plus module list first.** `ConstructionDB_CreatePlanDirect`/`AddPlan` are `[VERIFY]`; plan extraction is a stretch inside W5, not a dependency of W5. |
| Q8 | **Damage relay (AC item 7, stretch).** `HitReport` exists in the schema (≤ 4 Hz, DamageSync capability) and `DamageReport 0x0403` is reserved and unused. | **Upward `HitReport` in W3** because the wire is already there and it is cheap. "An NPC kills a client player" closes only if W3 has budget; otherwise it is deferred to M6 with a note in the exit report, not silently dropped. |

---

## 1. Goal, scope and non-goals

**Goal.** The world stops being only positions. A kill made on one screen happens on every screen; a player who dies
rejoins without leaving a duplicate ghost; a trade moves the station's stock on every node; a captured ship changes owner
everywhere and survives a save; and money is one number, owned by the server, that every node's local `player` balance
converges to within a round trip. The cost is bounded: a 500-unit stock application stays under 2 ms of authority frame
time, and a wallet converges within 30 s even after a forced local edit.

**In scope**

| Area | What M5 delivers |
|---|---|
| Capture | MD capture ring for the nine hooks §5 names, native fallbacks, `md_hook` state reported |
| Kills | `KillClaim` upward, authority `SafeDestroy`/`SafeRemove`, `Despawn{destroyed}` + `KillEvent` outward, authority-side kills as state |
| Death | `PlayerDeath` upward, ghost removed everywhere, rejoin without a duplicate, `HitReport` damage relay (stretch) |
| Trade | `TradeReport` upward, `EntityCargo` for the ship, authority stock application through the actions shim, `EntityCargo` for the station outward, NPC faction counter-flow |
| Builds | Build detection with a 10 s diff, `StationBuildRequest`, authority spawn, global `EntitySpawn`, no duplicate on the builder |
| Capture | `CaptureReport`, boarding inert exemption, `SetComponentOwner`, `EntityChange{owner}`, save/load survival |
| Wallets | `GetPlayerMoney` poll, §12.2 reconciliation loop, `CreditDelta` with reasons, credit modes, overdraft policy |
| Transfers | Send credits, team pool deposit/withdraw, loans and trade offers visible and actionable from the game |
| Relations | Live relation matrix apply within 2 s, `admin_only` team switch moving the avatar owner |
| Chat | Moderation hooks, `/who`, `/ping`, `/w`, `/t` |
| UI | Economy screen first cut, notifications, `/mp accept|decline` |
| Acceptance | `m5-exit-report.md` quoting every number from a log, `in-game-session-8.md` run sheet |

**Non-goals (deliberately later)**

- No NPC AI, combat, orders or economy replication. Streamed ships stay visual and positional (M4's rule).
- No open-market trade offers: v1 offers are directed, per ADR-022.
- No damage to structures and no station destruction.
- No mod-side economy. The ledger is the server's; the mod holds a balance and a pending queue, nothing else.
- No removal of a player's ship by the mod, ever (§6.1). Death is the game's job.
- No M5b talk-menu actions on remote players (send credits / invite / trade from the talk menu). That is its own milestone row.
- No story/unlocks (ADR-037) and no player portal (ADR-050).

---

## 2. Waves

### W1 — Capture: the MD half, the bus and the hook state (M5-01..M5-05)

| Task | Work | AC |
|---|---|---|
| M5-01 | `md/x4mp_events.xml`: capture `Killed`, `event_player_killed_object`, `event_object_destroyed`, `event_player_trade_completed`, `event_player_build_finished` and `_components`, `EntityChangedOwner`, `BoardingOperationStarted`/`Removed`, `event_player_money_updated`. Each pushes one compact line over the existing bridge, never at frame rate. | Every hook §5 names is wired and produces exactly one line per occurrence, with the component id exactly as MD prints it |
| M5-02 | `features/events/`: the bus. `eventId` = (node epoch, monotonic counter), a dedupe ring of the last 256 ids, a per-frame budgeted outbound queue, and a ring of the last 64 events for `[events] dump`. | A duplicate `eventId` is dropped once and counted; 500 events in one frame send at most K per frame and drop none |
| M5-03 | `md_hook` state: hook files that answered, per-hook counters, age of the last event, fallback count, printed as a `hooks=` block in the 30 s budget line. | With the MD half removed the line says `hooks=dead` within one interval instead of doing nothing silently |
| M5-04 | Fallback detectors, each on its own timer: `event_player_killed_object`, `event_object_destroyed object="player.occupiedship"`, a 2 s cargo diff, a 10 s station diff. A detector that fires while its hook also fired is deduped by (kind, subject, second). | With the hooks removed the fallbacks produce the same events one poll later, and never twice |
| M5-05 | `Intent`/`GameEvent`/`IntentResult` encode and decode in C++ over the generated structs, plus a round-trip test against the server relay's decoder. | Every body the mod sends decodes on the server unchanged; an unknown body is skipped and counted, not fatal |

**AC (wave).** AC item 1: the MD capture ring plus fallbacks exist and the `md_hook` state is reported.

**Status 2026-10-28: W1 done (code + tests), commit `5f89931`.**

**Built.**

- `md/x4mp_events.xml`: nine `instantiate="true"` cues, all read-only. Two hellos (`event_universe_generated`,
  `event_game_loaded`) raise `H;7`; then one cue each for a player-owned kill (`K;victim;macro;owner;killer`), a
  player-owned destruction (`P;` when the victim is `player.occupiedship`, else `X;component`), a completed trade
  (`T;station;ware;signed_amount;unit_price`), a finished build (`B;station;macro;`), a money change (`M;value`), and
  boarding start/end (`S;component`, `E;component`). Every line goes out as `raise_lua_event "'x4mp.event'"`.
- `ui/x4mp_events.lua`: forwards each line to native through the new `event` bridge verb and counts
  `received / sends / skipped / bad` in the global `X4MPEvents`.
- `ui/x4mp_bridge.lua`: `B.VERBS.event = true` — the whitelist rejects unknown verbs, so without this line every event
  would be dropped with `unknown_verb`.
- `ui.xml`: `ui/x4mp_events.lua` registered (MD files are auto-discovered from `md/`).
- `features/events/`: `event_types.h` (the line grammar and `parse_id`, hex or decimal), `event_bus.h` (dedupe over
  kind/subject/amount/second, 256 slots; queue cap 1024; 32 per frame; ring of 64 for inspection), `hook_state.h` (the
  `hooks=` block), `events_hub.h`, `events_feature.{h,cpp}` (bridge subscription, bounded inbox, one 30 s
  "no MD hello" warning, optional sink, `submit()` for fallbacks), `event_wire.h` (six intent encoders plus
  `IntentResult`/`GameEvent` decoders).
- The 30 s budget line gained a trailing `hooks=` field: `hooks=alive md=7 fired=41 age=1.2s fb=2 dup=0 queued=0`, and
  `hooks=dead md=0 fired=0 age=none fb=0 dup=0 queued=0` when nothing has ever arrived.

**Verified:** `game_build` exit 0, ctest **642/642** (7 new `host.M5-*` cases), Lua **257/257** (5 new). The MD file is
well-formed and every event element name was checked against the game's own `libraries/common.xsd`. The wave's own
numbers are in session 8: `hooks=alive md=7` in the budget line, and one line per occurrence.

**Not done, deliberately.**

- **Capture is not in the MD ring.** `event_object_changed_owner` requires an `object` or `group` source, and a global
  listener is the one shape the API notes call unverified. M5-20 detects captures through the native
  `EntityChangedOwner` typed hook (type 175), which sees every owner change without a source.
- **M5-04's detectors are not in this wave.** The dedupe they rely on is built and tested, but each detector lands with
  the wave that consumes its event: the kill fallback in W2, the cargo diff in W4, the station diff in W5. Writing them
  now would mean writing three detectors with no consumer and no way to test them against a real game.
- **XSD validation was not run.** Compiling the game's 1.7 MB `common.xsd` with `lxml` hangs for over two minutes on
  this machine, in the project validator and in a direct `XMLSchema` call alike. Well-formedness plus the structural Lua
  test stand in; the semantics of three attributes (`$Trade.ware` concatenation, `event.param.buildanchor` on the plain
  build event, and whether `event_player_owned_killed_object` also fires for NPC-on-NPC kills) are marked `[VERIFY]` in
  the MD header and are checked in session 8.

### W2 — Kills (M5-06..M5-09)

| Task | Work | AC |
|---|---|---|
| M5-06 | Client kill detection: `Killed` or the fallback, victim UniverseID → NetId through NetMap, `Intent{KillClaim{target, killer, weapon}}` with a fresh `request_key`. | A ghost kill sends exactly one claim, naming the claimant's own ship as killer when it is known |
| M5-07 | Authority validation and destruction: the id must exist, must not be a player, must be in interest; `SafeDestroy` (explosion) when within 20 km of a client camera, `SafeRemove` otherwise, both behind PlayerGuard. | A claim for an unknown, player or out-of-interest id is rejected with the right `RejectReason` and destroys nothing |
| M5-08 | Result outward: `EntityDespawn{reason=Destroyed, killer}` plus `KillEvent` for the feed; a client explodes an on-screen ghost and removes an off-screen one. | A client kill removes the ghost within 500 ms on the authority and on a second client, and the same net id is never despawned twice |
| M5-09 | Authority-originated kills: a tracked entity that becomes invalid during a read despawns once as `Destroyed`, with no intent and no double report. | An NPC killed by NPCs on the authority disappears for every client with exactly one despawn |

**AC (wave).** AC item 2.

**Status 2026-10-28: W2 done (code + tests), commit `663da9f`.**

**Built.** The client half is `features/events/kill_report.h`: a `KillReporter` that takes one `MdEvent`, resolves the victim's component through `GhostHub::net_id_of_local` (the netmap lookup the kill line needs), and sends exactly one `Intent{KillClaim{target, killer, weapon_macro_ref}}` with `request_key = Id128{lo = monotonic counter, hi = self player id}`. The killer is `selfship_hub().own_net_id()` — the player's own ship is not bound in the netmap (`NetKind::Self` is unused in the codebase), so it comes from the self-ship hub and is 0 when unknown. The `hooks=` block in the 30 s budget line gained `claims=` and `unmapped=`, so a tester sees a claim that never went out and why.

The authority half is `AuthorityFlow::on_intent`, reached from a new `MsgType::Intent` branch in `on_frame` before the checkpoint uploader. It decodes with the verified `kill_claim_of`, and answers exactly one `IntentResult`: `Unsupported` for a body the authority does not apply, `InvalidParameters` for a claim with no component, `UnknownEntity` for an id the index does not hold, `NotInInterest` when the row's sector is not in the `CaptureSet`, `NotPermitted` when `safe_remove` returns `BlockedPlayerGuard`, and `Conflict` for the other blocked outcomes. On success it calls `game::safe_remove`, `UniverseIndex::erase` (new: without it the row lingers until the next scan pass and `step()` keeps polling a dead component), `EntityStream::on_killed` (new: the same despawn as `on_row_removed(.., true)` but carrying `killer_net_id`), and sends a `GameEvent{KillEvent}` with zeroed `event_seq`/`server_time_us` because the server stamps both (`RelayModule.Events.cs`). Five counters — `intent_bad_`, `intent_unsupported_`, `intent_unknown_`, `intent_out_of_interest_`, `intent_player_`, `intent_failed_`, `kills_applied_` — are logged with every rejection.

M5-09 needed no new code: a tracked entity that becomes invalid is already detected at scan close (`end_scan` names the dropped rows, `is_valid_component` classifies them, `on_row_removed(net_id, true)` despawns once).

**Verified:** `game_build` exit 0, no compiler errors, 107206 ms (log `.game-loop/logs/build-1793164727126.log`); ctest **645/645 passed** (the 642 of W1 plus `host.M5-06`, `host.index (M5-07)`, `host.stream (M5-07)`); Lua **257/257** unchanged.

**Not done, deliberately.**

- **No mod-side explosion.** The design's `SafeDestroy` (SelfDestructComponent) does not exist in this mod, and `game/ghosts_api.h` says so explicitly: the only removal path is `safe_remove`. The authority removes the object and sends `Despawn{reason=Destroyed, killer}`; the explosion is played by each client from that reason. The 20 km / on-screen distinction therefore lives on the client, where the camera is, not on the authority.
- **The kill fallback (M5-04) is not here.** `Killed` is an X4Native typed hook, not an MD event; the MD ring uses `event_player_owned_killed_object`, which the schema documents as also firing when the player kills something. A pure-MD global listener for every kill is the `[UNVERIFIED]` item from `x4-api-notes.md` §4, so W2 ships the hook path and the W2 `[VERIFY]` note below covers it.
- **`weapon_macro_ref` is 0.** The MD kill line does not carry a weapon macro yet; the field is in the wire and a later wave can fill it without a protocol change.
- **`IntentResult` is not consumed on the client.** W2 logs the authority's answer server-side; the client's optimistic removal is the despawn that arrives on the same lane. Reconciling a rejected claim on the originating client is W3's rejoin path.

**`[VERIFY]` for session 8:** whether `event_player_owned_killed_object` fires for a player killing a *ghost* (a locally spawned object owned by another faction), and whether the kill line's component prints as the same hex id the ghost registry bound. If the hook does not fire for ghosts, the fallback is a native poll of the victim's validity, which is the M5-04 detector.

### W3 — Death, rejoin, damage (M5-10..M5-12)

| Task | Work | AC |
|---|---|---|
| M5-10 | Player death detection on the client: `Killed` where the victim is in PlayerGuard, or `event_object_destroyed object="player.occupiedship"`; send `Intent{PlayerDeath}` and let the game run its own death flow. The mod never removes the player's ship. | The client's own death sends one `PlayerDeath` and the mod touches no object |
| M5-11 | Ghost removal and rejoin: the authority and the other clients despawn that player's ghost; the server keeps the slot and the reloaded client gets `Welcome{resume}`; the janitor must not resurrect a ghost from a stale registry. | After death and reload the session holds one ghost for that player, not two, and no orphan in the `[sync] ghosts` line |
| M5-12 | Damage relay (stretch): the authority watches the avatar's hull and shield and sends `HitReport` at most 4 Hz; the client applies it to its own ship through MD `set_object_hull`/`set_object_shield` (**[VERIFY]** names). | An NPC damages a client player and the client's hull drops; hull 0 goes through the W3 death path |

**AC (wave).** AC item 3; item 7 only if M5-12 lands.

**Status 2026-10-28: W3 done for M5-10 and M5-11 (code + tests); M5-12 deferred as the stretch item the plan already marked it.**

**Built.**

- **Client (M5-10).** `features/events/death_report.h` — `DeathReporter` with `DeathReportDeps { send_control, self_player_id }` and
  `DeathReportStats { sent, send_failed }`. `EventsFeature` feeds it every `EventKind::PlayerDeath`, which is the MD `P;` line the
  capture ring already raises when `$Killed == player.occupiedship`. The reporter sends `Intent{PlayerDeath{killer}}` with a fresh
  `request_key` and **touches no object**: no removal, no hull change, no name change. The game runs its own death flow.
- **Wire.** `event_wire.h` gained `encode_player_died_event(...)` (a `GameEvent{PlayerDiedEvent}`) and `player_death_of(...)`, the
  same shape as the existing `encode_kill_event` / `kill_claim_of` pair.
- **Authority (M5-11).** `AuthorityFlow::on_intent` now handles `IntentBody::PlayerDeath` *before* the kill-claim path. It resolves
  the dying player's avatar net id through `AvatarHub::net_id_of_player`, then `UniverseIndex::erase`, `EntityStream::on_player_left`
  (a one-shot `DespawnEntry{reason=PlayerLeft, killer=0}`), and `AvatarDirector::forget_player` — which drops the record **without
  removing the ship**, so a reloaded client cannot resurrect a stale ghost from it. Exactly one `IntentResult` goes back and one
  `GameEvent{PlayerDiedEvent}` goes to the server (which stamps `event_seq` and `server_time_us`). Counter: `players_died_`.
- **Plumbing.** `AvatarHub` gained `set_player_lookup_fn` / `net_id_of_player` / `set_forget_fn` / `forget_player`, installed and
  cleared by `AvatarsFeature` next to the snapshot and protect functions; `EntityStream::on_player_left` mirrors `on_killed`.

**Verified:** `game_build` exit 0 (`build-1793165718472.log`); ctest **647/647** including the two new cases
`host.M5-10` and `host.stream (M5-11)`; Lua **257/257**.

**Not done, deliberately.**

- **M5-12 (damage relay) is not implemented.** The plan marks it a stretch, and the MD action names it would need
  (`set_object_hull` / `set_object_shield`) are still `[VERIFY]`. AC item 7 stays open and is reported as deferred in the exit
  report rather than quietly dropped.
- **A rejected `PlayerDeath` is not reconciled on the client.** The client's death belongs to the game; a rejection only means no
  despawn was broadcast, which the next roster/state update corrects. Same rule as W2's rejected kill claim.
- **The reloaded client's own registry restore** is only exercised in session 8; the authority-side stale-record path is covered by
  `forget_player` and pinned by the `M5-11` stream test.

### W4 — Trade and station stock (M5-13..M5-17)

| Task | Work | AC |
|---|---|---|
| M5-13 | Detection: `event_player_trade_completed` primary, a 2 s cargo diff fallback that only accepts non-empty reads. | A sale of 500 units produces exactly one report; a failed cargo read produces none |
| M5-14 | `Intent{TradeReport{station, ship, ware_ref, amount}}` upward, and `EntityCargo` for the own ship at most 1 Hz on change. | The report names the matched station net id; the cargo state line is at most 1 Hz |
| M5-15 | The authority applies the stock change through the MD actions shim (`add_cargo`/`remove_cargo` with `exact=`), batched into one `AddUITriggeredEvent` per frame under a per-frame ware budget. | 500 units land within 10 s with no frame over 2 ms in the `authority_ms` slot of the budget line |
| M5-16 | Broadcast and client apply: the authority sends `EntityCargo` for the station; each client computes the delta against its matched station and applies it through the same shim. | A buy reduces the stock on every node; an unmatched station is skipped and counted, never guessed |
| M5-17 | NPC counter-flow: `transfer_money` to the station owner's faction so the NPC economy does not leak, resolved node-relative. | A player purchase credits the owner faction's account with exactly the trade's worth, once (a player sale debits it) |

**AC (wave).** AC item 4.

**Status 2026-10-28: W4 done for M5-13..M5-17 (code + tests).**

**Built.**

- **Station identity, on both nodes.** The checkpoint manifest still holds avatar rows only
  (`mod/native/core/authority/checkpoint_messages.h:58`), so no station has a manifest net id today and the M4 matcher binds
  nothing. W4 therefore uses the deterministic fallback the on-foot container hash already documented
  (`mod/native/features/onfoot/onfoot_types.cpp:85`): `onfoot::matched_container_id(station_match_key(sector, macro, x, y, z))`,
  a 50 m bucket. `StaticsFeature` now runs on the authority as well; with no manifest it binds no M4 rows but still records
  every scanned station through `StaticsHub::note_station` (`features/statics/statics_hub.h:81`), which is the component ⇄ net
  id map the authority apply and the client apply both read (`station_component` / `station_net_id`, `:82-83`). The authority
  scans its own player's sector plus any sector a trade names (`request_station_scan`, `features/statics/statics_feature.h:35`).
  Follow-up: when M4 AC item 1 lands (station rows in the manifest) the authority needs the manifest file too, or the two sides
  disagree on a station id exactly once, when the manifest arrives.
- **Cargo read (M5-13), native and not a Lua round trip.** `GetNumCargo` / `GetCargo` with `tags=""` (`game/game_api.h:108`),
  wrapped as `GameApi::cargo` (`:189`) returning `CargoPod{ware, amount}`, capped at `kMaxCargoLines` 4096. `GetNumCargo == 0`
  cannot be told apart from a failed read, so an empty result means "no change" everywhere; the plan's "only non-empty reads
  count" is implemented as that rule instead of as a second probe.
- **Detection (M5-13/M5-14).** `TradeReporter` (`features/economy/trade_report.h:48`) turns the existing MD `T;` line into
  `Intent{TradeReport{station, ship, ware_ref, amount, unit_price, ware}}`: the station component is resolved to the scanned net
  id, the ware is sent both as an FNV ref and as the macro the client knows (part 1's wire change), and a ring of 8 dedupes on
  `(station, ware, second)`. The 2 s docked cargo diff is a detector of *lost* reports, not a second source: without the hook
  there is no station to name, so a change with no trade sent is counted `diff_missed` and logged, never sent.
- **Own ship cargo (M5-14).** `EntityCargo` on the own net id at most 1 Hz and only when the cargo signature changes
  (`EconomyFeature::report_own_cargo`, `features/economy/economy_feature.cpp:149`).
- **Authority (M5-15).** `AuthorityFlow::on_trade_report` (`authority_flow.cpp:333`) validates, resolves the station, and either
  applies at once or defers: an unknown net id asks for a scan of the reporting ship's sector and holds the trade for 5 s
  (`kTradeRetryUs`), one `IntentResult` arriving later either way. `apply_trade` (`:377`) queues a `CargoOp` (a buy removes
  station stock), schedules a full `EntityCargo` 30 ms later (`send_station_cargo`, `:412`, wares named through the session
  string table so clients can resolve `ware_ref`), and sends `GameEvent{TradeEvent}` (`encode_trade_event`,
  `features/events/event_wire.h:209`) for the server to stamp. `retry_trades` (`:434`) runs from `step` and also drops the
  snapshots and retries that came due.
- **Shim (M5-15/M5-16).** New `ui/x4mp_actions.lua` + `md/x4mp_actions.xml`: one `AddUITriggeredEvent` per verb per frame,
  `add_cargo` / `remove_cargo` with `exact=`, `transfer_money`; the queue folds per `(component, ware)` and the frame budget is
  64 wares and 8 money ops, over-budget work staying queued. ADR-034 holds: no `AddTradeWare`, no `DropCargo`.
- **Client apply (M5-16).** `EconomyFeature::apply_snapshots` (`economy_feature.cpp:111`) reads the local stock with the same
  `GameApi::cargo`, computes the difference with `cargo_delta` (`features/economy/stock_apply.h:28`), and queues only the
  differing wares. A net id the statics scan does not know is skipped and counted `cargo_skipped`; a station whose cargo cannot
  be read is `cargo_bad` and left alone. Nothing is guessed.
- **NPC counter-flow (M5-17).** The scan's 7th field carries the station owner's faction id, read through the same `@$St.owner.id` idiom proven on ships in `md/x4mp_capture.xml`; `StationRow.owner` parses it, `note_station` keeps the owner a pass could not read (and still learns a captured pinned station's new owner without touching its id), and `owner_of` is the accessor. `apply_trade` resolves the payer through `team_of_player` (the reporting player's team faction, `"player"` when there is no team) and queues the trade's counter-flow through `economy::economy_hub()`, counting `trades_paid_`. The direction is the opposite of the player's (docs/mod-design.md 5.3 and 12.2): a buy of 12 units at 3 Credits debits the payer's faction 36 and credits the owner 36, while a sale of 500 at 21 moves 10500 the other way, the owner paying the player's faction. `trade_money_op` refuses an empty payer or owner, a payer that is its own station, a zero amount, a zero price, and a negative price: the mod never guesses an addressee.

**Verified:** build green, no compiler errors; ctest **652/652 passed** (656 registered, the 4 network smokes not run, the
same as W3) in 97.6 s; new cases `[economy][m517]` (the counter-flow's direction and its six refusals) and `[statics][m517]`
(the 7th scan field, a silent pass keeping the owner, a captured pinned station learning a new owner); Lua **263/263**,
including `mod/tests/lua/test_actions.lua`.

**Not done, deliberately.**

- **A station id is the M3 hash, not a manifest id** (see the first bullet). This is why the authority can apply a trade at all
  in W4, and it is also the reason the two sides must switch to manifest ids together, in one commit, when M4 AC item 1 lands.

**`[VERIFY]` for session 8:** whether a nested list survives `AddUITriggeredEvent` into `event.param3` and `do_for_each`; whether
MD coerces a component id out of a Lua number (the scan path already prints hex text, the actions path sends numbers); whether
`add_cargo` on a station the authority's player does not own applies at all — the V13 spike only proved it on the player's own
ship — and `transfer_money` to an NPC faction, which M5-17 needs; whether `@$St.owner.id` reads a station's owner (the same
idiom is proven on ships in `md/x4mp_capture.xml:48`).

### W5 — Builds and capture (M5-18..M5-21)

| Task | Work | AC |
|---|---|---|
| M5-18 | Build detection: `event_player_build_finished` and `_components`, plus a 10 s player-faction station diff against the `on_universe_ready` baseline. | A finished client build produces exactly one request, and the diff does not repeat it |
| M5-19 | Authority spawn: `SpawnStationAtPos` with the plan when the `ConstructionDB_*` helpers work, macro plus modules otherwise, owned by the requesting player's team faction. Emit `EntitySpawn` static **globally** (stations are few and the map view must agree) and `IntentResult{result_net_id}`. The requesting client keeps its local station; the two are matched by (sector, macro, 50 m) so nothing duplicates. | AC item 5: the station appears for the authority and the other clients at the same position, with no duplicate on the builder |
| M5-20 | Capture detection: `EntityChangedOwner` where the new owner is the local `player` and the entity is a ghost; `BoardingOperationStarted`/`Removed` temporarily re-activate the ghost so the local operation can run and re-inert it afterwards; send `Intent{CaptureReport}`. | A capture sends one report, and the ghost is inert again once boarding ends |
| M5-21 | Authority apply: `SetComponentOwner` to the capturer's team faction (node-relative), broadcast `EntityChange{owner_team}`, and make the captured ship survive an authority save/load under the right faction. | AC item 6, including the save/load cycle |

**AC (wave).** AC items 5 and 6.

**Status:** W5 done for M5-18, M5-19, M5-20 and M5-21 (code + tests). The only piece of the wave that is
not implemented is boarding re-activation inside M5-20 (see Not done).

**Built.**
- **M5-18, the client half** — `features/builds/build_report.{h,cpp}`: a `B` line from `md/x4mp_events.xml`
  (`event_player_build_finished`, `component` = the build anchor, `macro` = the station macro) becomes one
  `Intent{StationBuildRequest}`. The sector index and the pose come from the game, not from the line:
  `GameApi::object_position(anchor)` plus `context_by_class(anchor, "sector")` and
  `GalaxyMap::index_of(sector)`, so nothing is guessed. A build whose sector the map cannot name yet is
  held in a pending list and retried for 10 s (`kRetryMs`), then dropped and counted, never sent with a
  zero sector. Dedupe is by build anchor (512-entry ring). A refused send is **not** retried: a retry
  would spawn the station twice, so it is counted as `failed` and the next `B` line is the only way back.
  The construction plan stays empty (`M5 Q7`: macro plus modules first).
- **M5-19, the authority half** — `AuthorityFlow::on_station_build` (`authority_flow.cpp`): the reporting
  player's avatar net id resolves to the `UniverseIndex` row, the row's component gives the sector
  `UniverseID`, the owner is the reporting player's team faction (`team_hub().faction_of_team`), and
  `GameApi::spawn_station` calls `SpawnStationAtPos` with an empty plan id. The net id is
  `onfoot::matched_container_id(station_match_key(sector macro, macro, x, y, z))` — the same key the
  statics scan already uses — so every node that scans the new station computes the same id; a hash that
  lands on 0 or `0xFFFFFFFF` falls back to `allocate_net_id()`. The station is then pinned in the hub
  (`pin_station`, so a later scan cannot overwrite the authority's id with a hash), upserted into the
  index as `EntityKind::Station`, answered with `IntentResult{Accepted, result_net_id}` and announced as
  `EntitySpawn{PlayerBuilt, owner_team, owner_player}` through the one `EntitySpawnBuilder`. Every
  failure path answers with a reject reason instead of a silent drop.
- **`GameApi::spawn_station`** wraps the SDK export `SpawnStationAtPos`
  (`x4_game_func_list.inc:2014`, `kGameFnCount` now 33), converts the pose to degrees on the write side
  like every other spawn, and refuses an empty macro, a zero sector, a missing export or a call off the
  main thread.
- **`StaticsHub`** now carries the sector macro and a pin per station component
  (`sector_macro_of`, `pin_station`, `note_station(..., sector_macro, pinned)`), because M5-16's stock
  apply and M5-19's spawn both need to name a station by component.

- **M5-21, the authority half** — `AuthorityFlow::on_capture_report` (`authority_flow.cpp`): the target
  net id must be a live `UniverseIndex` row with a component, inside the current capture set and not the
  player's own guarded ship; the new owner is the reporting player's team faction, resolved from the
  session's own `TeamModel` (`teams::team_hub().model().teams()`, members list) — **never from a value
  the client sends**, because `CaptureReport` carries no team and a client must not be able to hand a
  ship to another team. `SetComponentOwner` runs on the main thread, the index row is updated to the new
  owner, `EntityChange{owner, owner_team, owner_player}` is queued (`entity_stream` learned the
  `kChangeOwnerTeam`/`kChangeOwnerPlayer` bits), `IntentResult{Accepted, result_net_id}` is sent, and
  `GameEvent{CaptureEvent}` carries the old owner, the old team, the new team and the player. The same
  helper fixes M5-19, which had been stamping the *host's* team on every builder's station.
- **Wire** — `capture_report_of` (decoder) and `encode_capture_event` (`event_wire.h`), pinned by the
  `[events]` case: a capture report decodes, a foreign buffer is refused, and every `CaptureEvent` field
  survives the wire.

- **M5-20, the client half** — `features/capture/`: a `CaptureReporter` that turns one owner change into one
  `Intent{CaptureReport{target, new_owner_ref}}`, and a `CaptureFeature` that owns the typed hook. The
  detection path is the one the plan named (`docs/m5-plan.md:137-139`): the MD ring cannot see an owner
  change, so the platform grew `IPlatform::subscribe_owner_change`
  (`mod/native/host/platform.h`), implemented in `X4Platform` on top of the generated typed subscription
  `x4n::md::on_entity_changed_owner_after` (event type 175, `x4_md_events.h:2291-2310`) with a static
  trampoline slot and an `x4n::off()` in the destructor. The callback carries only the component, so the
  faction is read back through the new `GameApi::owner_faction` (`GetOwnerDetails`,
  `x4_game_func_list.inc:956`, `kGameFnCount` now 34) and compared against `"player"`: a capture is a ghost
  (`GhostHub::net_id_of_local` non-zero) whose new owner is the local player, nothing else. A change that is
  not a capture is counted (`not_ghost`, `not_player`, `unknown_owner`, `bad`), never reported; a refused
  send is counted and **not** remembered, so the next owner change of the same ship can still report. The
  same capture is reported at most once in a 30 s window (`kDedupeMs`), because the hook can fire more than
  once for one hand-over. The game-thread callback pushes the component into a 256-entry ring, the frame
  drains at most 64, so a burst cannot turn into work inside the game callback.

**Verified.** `mod\build.ps1 -Config relwithdebinfo` → `100% tests passed, 0 tests failed out of 650`
registered tests, 4 skipped (the real-server smoke and live cases), with the wave's cases present in the
generated list: four in `tests/test_builds.cpp`, three in `tests/test_capture.cpp`, and one each for
`spawn_station` and `owner_faction` in `tests/test_game_api.cpp`. `x4mp_host_tests.exe "[builds]"` → 53
assertions in 4 test cases; `x4mp_host_tests.exe "[capture],[game]"` → 206 assertions in 18 test cases;
M5-21's assertions extend the existing `[events]` case, now 1250 assertions.
`python mod\tests\lua\run_lupa.py` → 263 passed, 0 failed (no Lua changed this wave).

**Not done.**
- **Boarding re-activation is not implemented.** M5-20 asks for the ghost to be re-activated while a local
  boarding operation runs, and the typed `BoardingOperationStarted` event cannot support it: its payload
  (`x4_md_events.h:298-310`) carries only a `boarding_operation` id and no ship, so the mod cannot tell
  which ghost to re-activate. Until that is solved (a different event, or a scan of the boarded ship) the
  captured ghost stays inert for the whole operation, and only the owner change at the end is reported.
- **`StationBuiltEvent` is not emitted**; only `EntitySpawn` and the `IntentResult` are sent.
- The construction plan id is empty (Q7), so a built station comes back as its macro plus modules.

**[VERIFY] for session 8.**
- The `B` line's `$buildanchor` really is the finished station (or the build anchor) and its component id
  survives the MD-to-native trip.
- `SpawnStationAtPos` with an empty construction plan id spawns the station the plan previewed, and the
  owner faction string the authority passes is accepted by the game.
- The authority's `EntitySpawn` position (quantised) and the builder's own local station agree inside the
  50 m key, so the builder binds its station to the authority's net id and no duplicate appears.
- `pin_station` survives a statics rescan on both the builder and a third node.
- `SetComponentOwner` accepts an `x4mp_team_<slot>` faction for a ship the authority does not own, and the
  captured ship survives an authority save/load under that faction.
- The typed `EntityChangedOwner` hook really fires for a captured ghost on the client, `GetOwnerDetails`
  returns `"player"` for the ship the local player just took, and the 30 s dedupe is not so long that a
  second capture of the same hull in the same minute goes unreported.

### W6 — Wallets (M5-22..M5-26)

| Task | Work | AC |
|---|---|---|
| M5-22 | Money reading: `x4mp_bridge.lua` polls `GetPlayerMoney()` every frame and pushes `x4mp.money {value}` to native on change only; `event_player_money_updated` is a wake-up and a reason hint, never the value source. | A change in the game is seen by native within one frame; a quiet frame costs nothing on the wire |
| M5-23 | Reconciliation loop exactly as §12.2: `pending[]` of `(seq, delta, reason)`, `expectedAdjust` to swallow the echo of our own `AddPlayerMoney`, converge to `serverBalance + Σpending` on every `WalletUpdate`. | A forced local edit is corrected within 30 s; our own write is never booked as a player delta |
| M5-24 | `CreditDelta{account, seq, d, reason, ref}` upward with the reason taken from the same frame's events, resent with the same `seq` after a reconnect. | A reconnect mid-purchase loses no delta and the server books it once |
| M5-25 | Credit modes on the node: resolve Auto/PerPlayer/Shared from `SessionSettings`, point "spendable" at `wallet:<p>` or `pool:<team>`, and request a `WalletUpdate` refresh every 30 s. | Switching to Shared makes every member's local money equal to the pool balance within 2 s |
| M5-26 | Shared-pool overdraft: negative pool if the game tolerates it, otherwise clamp local money at 0 and surface the team debt in the HUD and the economy screen. | Two simultaneous purchases in Shared mode leave all members at the same final balance within 2 s |

**AC (wave).** AC item 10.

**Status 2026-10-29: W6 started; M5-22 done (code + tests), M5-23..M5-26 open.**

**Built.**

- **The read has to be Lua-side, and that is why the poll exists.** The vendored game function table has `AddPlayerMoney` and no
  getter at all (`mod/third_party/x4native/v9.0.0-611726/sdk/x4_game_func_list.inc:40`), so native can write money but never read
  it. `ui/x4mp_money.lua` therefore polls `GetPlayerMoney()` once per frame and pushes `x4mp.money {"v":1,"value":N}` on change
  only: the value is rounded to whole credits (`math.floor(v + 0.5)`, the game hands back a float), a missing function, a
  non-number or a NaN counts as `bad` and is never sent as 0, the start menu is skipped, and a refused send is retried on the next
  poll instead of being lost. The per-frame timer is the hud's re-arming one-time callback with the same stale guard, so another
  mod's raising callback cannot kill the loop for good. `event_player_money_updated` is deliberately not the source: it does not
  fire for every change, so it can only ever be a reason hint (that is M5-24's job).
- **The verb is not queueable** (`ui/x4mp_bridge.lua`, `B.VERBS.money`): a balance that arrives late is a wrong balance, and the
  next poll re-reads the truth anyway.
- **`features/wallet`** is the native half. `wallet_hub.h` `parse_money_value` reads the body (an object with an integral `value`,
  any key order, extra fields ignored, floats/strings/null/oversized bodies refused) and `WalletHub::observe`
  (`wallet_hub.cpp:28`) is the 12.2 state machine: the first reading of a session is a `Baseline`, the same value is `Quiet`, a
  change that equals what `note_apply` asked the game for is an `Echo` and clears the expectation, anything else is the player's
  own `Change`. A real change never eats an outstanding expectation, so an echo that lands after it is still recognised as ours;
  two writes before one poll are recognised as their sum. `note_apply` is what M5-23 will call around `AddPlayerMoney`.
- **`WalletFeature`** subscribes `x4mp.money` into a mutex inbox (64 bodies, oldest dropped), drains it in `on_frame` after
  `universe_ready`, and logs one greppable line every 30 s: `[wallet] local=... known=... changes=... echoes=... applies=...
  quiet=... bad=... expected=...`. A new universe drops the baseline (`on_game_loaded`), because the next account is not this one.
  It is registered after `EconomyFeature`, which will book its changes upward in M5-23.

**Verified.** build green, no compiler errors; ctest **655/655 passed** (659 registered, the 4 network smokes not run, the same as
W3) in 82.4 s; Lua **271/271** (8 new `test_money` cases). New native cases: `[wallet][m522]` "a money body is a JSON object with
an integral value", "...the first reading is a baseline, our own write is an echo, the rest is the player's", "...a zero balance at
start is a baseline, not a quiet frame" (`mod/tests/test_wallet.cpp`).

**`[VERIFY]` for session 8:** that `GetPlayerMoney()` exists as a Lua global and returns credits in the same unit `AddPlayerMoney`
takes; that a per-frame poll is not measurable in the frame time; and that an `AddPlayerMoney` written by native comes back as one
change on the next poll (M5-23's whole echo rule rests on that).

### W7 — Relations, chat and the economy screen (M5-27..M5-31)

| Task | Work | AC |
|---|---|---|
| M5-27 | Live relations: apply `TeamRelations` on every node within 2 s, and `ReassignPlayerAssets` for an `admin_only` team switch. | AC item 9: two hostile teams show red targeting on both sides; allied takes effect within 2 s; a team switch moves the avatar's owner |
| M5-28 | Chat: moderation hooks and `/who`, `/ping`, `/w`, `/t` in `x4mp_chat.lua`, keeping the existing pass-through for every other command. | Each command answers with a system line; an unknown command still reaches the vanilla handler |
| M5-29 | Economy screen first cut (12.7): header with wallet, pool and mode badge; send credits with a recipient picker; deposit and withdraw; a loans table with Repay and Forgive; trade tabs Incoming/Outgoing with Accept and Decline; the last 50 ledger entries. | Every section renders live data and every action reaches the server API with an audit trail |
| M5-30 | Notifications: HUD badge, a transient notification, a system chat line, and `/mp accept <id>` / `/mp decline <id>`. | An incoming offer is visible three ways and can be accepted from the chat line |
| M5-31 | Escrowed settlement on the authority (12.6, stretch): `AssetTransferOrder` → precheck everything, journal, apply in order, compensate in reverse on failure, `AssetTransferConfirm`. | AC item 12: a ship-for-credits offer settles atomically, an injected failure after the first op rolls back exactly, residuals are 0 |

**AC (wave).** AC items 8, 9 and 11; item 12 if M5-31 lands.

### W8 — Acceptance and exit (M5-32..M5-34)

| Task | Work |
|---|---|
| M5-32 | `docs/m5-exit-report.md`: every AC number quoted with the log line it came from, and the M4 numbers re-quoted wherever M5 moved them |
| M5-33 | `docs/in-game-session-8.md`: the two-client run sheet, one step per AC item, with the exact log greps and what to send back |
| M5-34 | Roadmap status, the `docs/README.md` index, and the ledger auditor clean after a 60-minute session with purchases on all nodes |

**AC (wave).** Every roadmap M5 acceptance number is quoted from a log, not asserted.

---

## 3. Rules that bind every wave

- **The mod never writes money for offers, transfers or loans** (§12.6). Only the ledger changes; every node converges
  through the W6 loop. A wave that "fixes" a balance locally is a bug, not a shortcut.
- **A node never applies its own optimistic result twice.** Idempotency is `request_key` upward and a set of applied
  `eventId`s downward; applying "remove NetId 42" twice is a no-op by construction.
- **The player's ship is never removed by the mod** (§6.1). PlayerGuard is checked at every mutation, not at the call site.
- **Every new counter goes in a log line the tester can grep.** M5's acceptance is a table of numbers with sources; a
  counter that only exists in a debugger does not count.
- **No new MD work at frame rate.** MD pushes on an event, native polls on a timer, and the two never fight: a hook and its
  fallback dedupe by (kind, subject, second).

---

## 4. Gates

| Gate | Command | Passing means |
|---|---|---|
| Mod build | `game_build` (relwithdebinfo) or `mod\build.ps1 -Config relwithdebinfo -NoTest` | exit 0, zero errors |
| Host tests | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "<filter>"` | every new `[events]`/`[kill]`/`[trade]`/`[wallet]` case green |
| Full ctest | `ctest --output-on-failure` from `mod\build\msvc-x64-relwithdebinfo` | no regression against the M4 baseline of 635 tests / 4 skipped |
| Lua | `python mod\tests\lua\run_lupa.py` | 252+ green, including the new chat commands and economy screen |
| Server economy | `dotnet test X4MP.Core.Tests --filter "FullyQualifiedName~Economy"` and the `X4MP.Server.Tests` economy/trade filters | green; the auditor stays clean |
| Server build | `dotnet build X4MP.Server` | zero warnings (the repo treats them as errors) |
| In game | `game_deploy` + `game_reload`, X4Mcp `POST http://127.0.0.1:7779/mcp`, log `%LOCALAPPDATA%\X4MP\logs\x4mp.log` | the AC's own log line, quoted |

A wave is closed when the build is clean, ctest and Lua are green, the new tests pin the AC, and the commit is local.

---

## 5. Definition of done for M5

1. AC items 2-12 each executed once in session 8, with the log line quoted in `docs/m5-exit-report.md`.
2. The ledger auditor is clean after a 60-minute session with purchases on all nodes.
3. Every event in the REQ §8.3 checklist executed once with log evidence.
4. No regression in the M4 gates: 635-test ctest baseline, the 30 s budget line, the UDP lane counters, record/replay.
5. M5b (talk-menu actions on remote players) is explicitly out of scope here and stays its own row.
