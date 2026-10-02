# TaterTrader / DeadTater: algorithm notes (reference study)

Source: `reference-tatertrader/` (Nexus mod "TaterTrade", content.xml v705, 2024-10-30; authors Ludsoe, DeadAir;
author retired 2025-09-23, GPL-3.0 per LICENSE). **Reference only. X4MP must not copy code.** Everything below is
paraphrased. Line refs are `file:line` inside that clone (`aiscripts/deadtater.xml` = `dt`, `aiscripts/tatertrade.xml` = `tt`,
`aiscripts/order.assist.xml` = `as`, `md/tater_central_queue.xml` = `q`, `md/setup_tater_mod_changes.xml` = `st`).
Context: ADR-050 P3 trade-route finder, `docs/research/player-portal.md` section 2.2 and phase P3.

## 1. Structure

| File | Role |
|---|---|
| `dt` (2111 lines) | **DeadTater** order: one big script, one `attention` block, label-based state machine. Newer, queue-aware. |
| `tt` (1784 lines) | **TaterTrade** order: older (Ludsoe v4, DeadAir v5+), has station-trader mode, ware presets, faction bans, include/exclude sectors, discounts, `fasttrade`. No central queue. |
| `as` | XML diff patching vanilla `order.assist`: when a subordinate's commander has a Tater order, the subordinate re-runs the same script with the commander's params (`as:3-6`, `as:112` for TaterTrade, `as:213` for DeadTater). |
| `md/tater_central_queue.xml` | Global per-faction FIFO queues + two tick cues that release ships (section 3). |
| `md/setup_tater_mod_changes.xml` | Lifetime-profit bookkeeping from trade-completed events (`st:~60-100`). |
| `t/*.xml`, `libraries/icons.xml` | Strings (page 3282837) and icon. Not relevant. |

DeadTater order declaration: id `DeadTater`, category `trade`, `infinite=true`, `allowinloop=false`, not allowed on laser towers (`dt:4,74`).
Handlers: sector change, attack, missile lock, scanned, inspected, abandoned/lockbox, resupply, target invalid, tide (`dt:77-88`).
`init` (runs once) sets the command to freetrade, removes a temporary account, precomputes min-fill (`dt:89-116`).

### DeadTater parameters (I count 20, not 21; `dt:7-64`)

| Param | Default | Meaning |
|---|---|---|
| `range` | job main sector, else current sector | "Home" sector; all distances measured from here (`dt:7`) |
| `minbuy` / `minsell` | 0 | Min gate distance for buy / sell sectors (internal, no UI) (`dt:11,18`) |
| `maxbuy` / `maxsell` | max(commander trade-NPC mgmt skill, commander pilot mgmt skill, own pilot piloting), floor 1 | Max gate jumps for buy / sell sectors (`dt:12,19`) |
| `distancecheck` | false | Apply per-jump penalty when comparing deals (`dt:26`) |
| `distancecheckpercent` | 5 (UI 1-10) | Penalty per jump: multiplier `(1 - p/100)^jumps` (`dt:27`) |
| `preferownfaction` | false | Pass 1: both buyer and seller must be own faction (`dt:34`) |
| `preferownfactionsell` | false | Pass 2: only the buyer must be own faction (`dt:36`) |
| `preferownfactionbuy` | false | Pass 3: only the seller must be own faction (`dt:38`) |
| `ignoretraderules` | false | Don't apply trade-rule restriction checks when a player station is involved (`dt:40`) |
| `autowares` | true if player-owned | Auto-add all economy wares the ship's cargo types can carry (`dt:42`) |
| `illegalwares` | false | Include wares illegal in policed sectors (`dt:44`) |
| `minfill` | false | Require offers whose volume reaches a % of free hold (`dt:46`) |
| `minfillpercent` | 25 (UI 5-95, step 5) | Percent of free capacity (`dt:48`) |
| `ignoreshipbuyers` | true | Skip offers whose counterparty is a ship ("trading with ships is still terrible") (`dt:55`) |
| `enablelogbook` | true if player-owned | Log each chosen deal (`dt:57`) |
| `warebasket` | ship's ware basket | Manual ware list / job input (`dt:59`) |
| `transfercreditoption` | false | Pre-pay seller station from the owner faction when buying from own station (experimental, notes say it does not stop reason-32 errors) (`dt:63,2043-2050`) |
| `usequeue` | true | Use central queue (`dt:64`) |

TaterTrade extras (`tt:8-109`): `home` (sector or station), `returnhome`, `discount`/`discount2` (price multipliers % on own-station buy/sell prices),
`bypass` (ignore restrictions on owned stations), `fasttrade` (skip sell-offer scan once a good one exists, `tt:1431`), `stationmode`/`blackmarket`
(station supply logic, `tt:1020`), ware presets (legal/illegal/shipbuilding/hightech/refined/bio/size classes), `excludedsectors`/`includedsectors`
(`tt:859-916`), per-faction ban toggles, `scanspeed`.

## 2. Trade-selection algorithm (DeadTater)

Loop is `beginning` -> sector scan -> sell cargo -> pass 1/2/3/free trade -> `setupdeals` -> create orders -> back to `beginning` (`dt:119`, `dt:1951`, `dt:2079`).
The order is a *planner*: it never moves the ship itself; it creates ordinary buy and sell trade orders and lets the engine fly them.

### 2a. Candidate sectors (`dt:146-428`)
1. Two separate sector lists from `range`: buy sectors (min/maxbuy) and sell sectors (min/maxsell), via the engine's "sectors in range" action; player ships
   only consider **known** sectors (`dt:180-200`).
2. Each candidate sector (clusters expanded into sectors) is rejected if any of: owner faction relation <= -0.32 (the "kill on sight" band) to the ship's owner; sector on the
   ship's travel or activity blacklist; engine gate distance (blacklist-aware) is negative or beyond the max; or no jump path exists. Every intermediate
   sector/cluster on the jump path is re-checked for relation and travel blacklist (`dt:255-293`). Already-validated sectors are skipped to save time.
3. Tiny waits (5 ms queued, 50-200 ms unqueued, 1 ms in path checks) spread work across frames (`dt:210-213,219,280`).

### 2b. Ware list (`dt:570-615`)
Manual basket plus, if `autowares`, every economy ware whose transport tag (container/solid/liquid) matches the ship's hold capacities (`dt:577-596`). Empty list -> wait 2 s, restart.
Ware list is shuffled each pass (`dt:676,1662`) so ships don't all scan the same ware first. Illegal wares are dropped per sector when the sector's police faction
considers them illegal to the ship owner, unless `illegalwares` (`dt:683`).

### 2c. Step 0: sell what we already carry (`dt:430-569`)
For every ware in cargo, find buy offers in sell sectors, then keep up to the 5 best by `relativeprice` (descending; shuffled first for tie-break randomness, `dt:510-514`),
score by `distance scale * min(cargo amount, offer amount) * unit price` and create a single trade order for the best (`dt:524-562`). Done = restart loop.
Buyers must pass: operational, docking size fits ship, relation-to-ship ok for docking, trades "known to this owner" (visibility), not blacklisted for object-activity (`dt:474-483`);
buyers must have free storage for that ware (fixes stations with no storage, `dt:494`); ship buyers optionally skipped (`dt:492`).

### 2d. Passes (own-faction, sell-prefer, buy-prefer, free trade)
Same skeleton four times (`dt:637` pass 1, `dt:966` pass 2, `dt:1298` pass 3, `dt:1624` free trade): the first pass that finds any deal wins and we jump to `setupdeals`.
They differ only in the owner filter on `match_buyer`/`match_seller` (own faction required on both sides, buy side only, sell side only, none) (`dt:705,775,1103?,1434`).
Free trade starts with `Grofit=1`, the others with 0.

Per ware, per pass:
1. Gather **buy offers** (stations that buy the ware) from all sell sectors and **sell offers** from all buy sectors. Optional min-fill filter: total offer volume >= minfill% of free hold
   and not above free hold (`dt:684-704`).
2. Only if buy offers exist, gather sell offers (saves queries, `dt:750`).
3. Sort each side by `relativeprice` (a game-provided 0..1-ish price position within the ware's min/max band): buy offers high to low, sell offers low to high; keep the **top 5 each** (`dt:821-830`).
4. Evaluate the cross-product (max 25 pairs):
   - skip offers no longer `available`;
   - skip same station on both sides (`dt:929`);
   - `amount = min(ship free space for ware, buy-offer amount, sell-offer amount)`; for player ships in free trade also capped by half the player's money divided by unit cost (`dt:845,1833-1838`);
   - unit prices are `unitprice/100` (engine stores credits x100);
   - restriction check applies only if the ship is player-owned, `ignoretraderules` is false and one side is an own-owner station: the offer's restriction list is treated as a whitelist (inverted=0) or blacklist (inverted=1) against the counterparty owner (`dt:850-892`);
   - distance (only if `distancecheck`): gate distance ship->seller plus seller->buyer, using the blacklist-aware path; -1 = unreachable -> scale 0; else scale = `(1 - p/100)^jumps` (`dt:895-927`);
   - **score** = `amount * (buy price - sell price) * distance scale` (profit in credits, then penalised); pick the max (`dt:934`). Quirk: it compares the scaled score but stores the *unscaled* profit as the new bar (`dt:939`), so later candidates are compared against an optimistic number. Don't replicate.
5. Across wares the best single deal wins (variables persist across the ware loop).

TaterTrade differs (`tt`): scores by a `Rating` ("offer priority") times the same per-jump factor (`tt:1154,1293,1527-1536`), with a `discount` factor on prices for own stations
and an average-price-based buy cost when the buyer is own-station (`tt:976`); has `fasttrade` pruning (`tt:1431`).

### 2e. Commit and failure handling (`dt:1951-2105`)
1. Affordability check (player ships): money > unit sell cost * amount (`dt:1959-1963`).
2. Both offers still `available`.
3. `clamp_trade_amount` is asked twice (buy side with buyer ship + seller, sell side with buyer station) and **any reduction aborts the whole attempt and restarts the loop** (`dt:1968-2012`). This was added to avoid "error-prone" trades (comment `20240423`).
4. Write logbook line (variants for own station supply, own station sale, own-to-own transport, normal trade with expected profit and lifetime profit) (`dt:2017-2036`).
5. Create two trade orders (buy at seller, sell at buyer) with `immediate=true` (`dt:2051-2052`). Comment: reading offers after order creation throws errors, so orders are created last (`dt:2041`).
6. No deal found: escalating back-off. Fail 1: wait ~10-27 s (skill-dependent, seeded per ship). Fails 2-6: that wait times the failure count. Fail >= 7: logbook "request assistance" (with counts of buy sectors, sell sectors, wares) and wait ~20-39 s times the count (`dt:2070-2102`). Command shows "standing by".
7. Blacklists are respected everywhere (sector travel/activity, object-activity on stations, relation floor) but the script never *adds* to a blacklist; there is no per-station penalty memory. "Failure" is only "no candidate pairs" or "stale offer".

## 3. Distribution and shared state

### Central queue (`q`, used by DeadTater when `usequeue`)
- Global MD table `global.$DATaterQueueTable` with four members: `$CheckSpaceQueue` and `$TradeLogicQueue` (tables: faction -> ordered list of ships), plus `$FactionCheckSpaceQueue` and `$FactionTradeLogicQueue` (ordered lists of factions that currently have waiting ships) (`q:6-10`).
- Rebuilt on new game and on every load (`q:~17-40`); queue lists are created for every claimspace/economic faction minus visitor/hidden/no-trade-offer factions (`q:~60-90`).
- A ship enqueues itself (unique) under its **true owner** faction before each of its four heavy scans (sector validation `dt:149-160`, cargo sale `dt:437-449`, each pass `dt:647-660`, `dt:977-987`, `dt:1309-1319`, `dt:1635-1645`), then waits for a "proceed" object signal or a timeout.
- Two MD cues tick: the sector-check queue every 2 s, the trade-logic queue every 5 s. Each tick walks the faction list and releases **only the head ship of each faction's queue** (round robin across factions, one ship per faction per tick), signalling the ship and removing it; a faction with an empty queue is dropped from the list (`q:~95-160`).
- Timeout fallback so a lost signal can't hang a ship: `max(position * 3 s, 60 s)` for sector checks and `max(position * 6 s, 60 s)` for trade logic (`dt:161`, `dt:449`). (The `max` against 60 s means the fallback is always at least 60 s, i.e. it's a safety net, not the pacing.)
- Net effect: for one owner, at most one expensive scan every 5 s (trade logic), so offers are re-read **between** ships' selections; a ship that picked a deal has already created its orders (which reserve offer volume in the engine), so the next ship sees reduced availability. That is the whole "don't pile onto one station" mechanism. There is no explicit station reservation table in the mod; the engine's own offer amounts/`available` flags do the work.
- Without the queue, ships sleep a random 50-200 ms between sector checks (`dt:212`).

### Other randomisation
Shuffle before sort (random tie-break among equal `relativeprice`), shuffle of ware list, seeded random waits after failures (`dt:510,676,2078`).

### Fleet awareness
`as` patches the vanilla assist order so a subordinate whose commander's default order is TaterTrade/DeadTater runs the same script with the commander's parameter values (copying each param from the commander's order), i.e. the order is "inherited", not coordinated. `st` treats such subordinates as Tater-managed for profit tracking (`st:~75-100`). `dt:~131-142` also picks the commander's management skill for the default range.

### Shared/global state
`global.$DATaterQueueTable` (queues), `global.$DALifetimeGrofit` (table: ship -> lifetime credits), `global.$DATaterStartTimes` (ship -> first trade time) (`st`). Cleanup on ship destruction and a 30-min sweep against live player ships. Profit tracked on `event_player_trade_completed`: buyer ship -> subtract, seller ship -> add (`st:~60-100`).

## 4. Game API facts worth keeping

- **Offer queries**: `find_buy_offer` (stations that buy) and `find_sell_offer` (stations that sell), with `tradepartner=ship`, a `space` (sector), `wares=ware`, `multiple=true`; sub-filters `match_buyer`/`match_seller` with `match_content/match_dock(size, trading)`, `match_relation_to(relation=dock)`, `match tradesknownto=<owner>` (offer visible to that faction, the **visibility filter**), `match_use_blacklist`, `owner=`, and volume filters `totalvolume`/`mintotalvolume` (`dt:474-483,701-713`). Note the comment that `offervolume` "wasn't blocking trades below required amount correctly" so they used `totalvolume` (`dt:1688`).
- Offer object fields read: `ware`, `unitprice` (x100), `amount`, `offeramount`, `desiredamount`, `minamount`, `stocklevel`, `relativeprice`, `quantityfactor`, `available`, `buyer`/`seller`, `restriction.factions` + `restriction.inverted` (`dt:538,857`). Offers go stale: always re-check `available` right before ordering.
- **Distances**: `ship.gatedistance.{target}.{blacklistgroup}.{ship}` (blacklist-aware) returns -1 when no safe route. Raw `distanceto` is metres (debug only). `get_jump_path` with `useblacklist` for route sector enumeration (`dt:266`).
- **Order creation**: `create_trade_order(tradeoffer, amount, immediate)` per side; `clamp_trade_amount` validates amount vs. credits/space/stock and returns a reason code (`dt:1968`).
- **Relation**: `faction.relationto.{faction}`; -0.32 is the kill-on-sight threshold used to exclude sectors (`dt:255`). Illegality: `ware.illegalto.{faction}.{faction}`, `sector.policefaction`.
- **Ware catalogue**: `get_ware_definition flags=economy tags=tag.container|solid|liquid` (`dt:583`); faction sets via `get_factions_by_tag` (claimspace/economic/visitor/hidden/notradeoffer) (`q`).
- **Cost**: each pair evaluation is cheap; the dominant cost is `find_*_offer` x sectors x wares x ships. This is why the mod caps to top-5 per side, shuffles, adds ms waits and uses the queue. High game speed makes it worse (comment `dt:65`).
- **Gotchas in comments**: stations with zero storage still advertise offers (filter by free cargo, `dt:494`); ship buyers (carriers/auxiliaries) are "terrible" trade partners; own-faction station restrictions must be hand-checked (whitelist/blacklist inversion, `dt:853-856`); "reason code 32" errors when a station cannot afford to buy (`dt:2043`); reading offers after creating the order errors (`dt:2041`); player money guard of 50% per trade (`dt:1836`).

## 5. How X4MP should use this

### 5a. Server-side trade-route finder (ADR-050 P3)
**Inputs** (all already planned in player-portal.md section 2.2): market snapshot rows per station (ware, side buy/sell, price, amount, min/max, stock, restriction, captured-at), a **team visibility mask** (`Strict` / `KnownStations` / `Everything`: only offers the team could see in game, the analogue of `tradesknownto`), the galaxy graph (gate distances per sector pair, from static data), team-relation matrix (to drop hostile sectors, same -0.32 idea mapped to our hostile status), a ship/fleet profile (cargo by transport class, speed, docksize), and optional per-team filters (ware list, max jumps, own-stations-only, exclude sectors).

**Algorithm we would adopt**
- Same shape: for each ware, take buy-side offers and sell-side offers, keep top-N each by relative price (N=5 is a good UI default, make it configurable; on a server we can afford much larger N and exact cross-product since there is no frame budget).
- Gate-distance filter and per-jump decay, but **replace the multiplicative decay with an explicit profit-per-time score**: `profit = amount * (sell_price - buy_price)`, `time = travel_time(ship speed, jump path) + dock overhead`, `score = profit / time`. Show per-jump as a secondary column. Multiplicative `(1-p)^jumps` is a proxy for this and is unitless; we can do better because we have ship speed.
- Amount = min(hold capacity for that transport class, offer-side amounts, **stock-aware cap**: do not exceed seller stock minus a safety margin, nor buyer free capacity), and for team-credit realism cap by wallet/pool (mirror the mod's "half the money" guard as a setting).
- Hard filters: unreachable path, hostile relation, trade restrictions (apply restriction whitelist/blacklist semantics exactly: inverted flag), illegal wares per police faction, buyer with no free storage, ship-class docking size, same station on both sides, ship buyers off by default.
- Passes: expose the own-faction passes as a **mode** in the UI (Team to team / Sell to team / Buy from team / Free), instead of hidden fall-through.
- Tie-break: random is fine for ships, but on a dashboard rank deterministically (score, then profit, then fewer jumps) so results are stable between refreshes.
- Fix the mod's quirk (scaled vs. unscaled comparison). Keep one scored number and compare like with like.

**Distribution, server-side**: because we know the whole team's ships, we can do real **allocation** rather than a queue: solve a small assignment (greedy by score, decrementing the offer's remaining amount after each assignment, so two ships never claim the same volume). This replaces the mod's time-staggering, which only works because the engine reserves volume when orders are created.

**Dashboard presentation of reasoning** (per ship or fleet):
- Top 5 routes as cards: ware, buy station (sector, price, stock), sell station (sector, price, demand), jumps, est. time, amount, est. profit, profit/hour.
- "Why this one": bullet list generated from the score parts (price spread vs. average price, volume vs. cargo, distance penalty, stock at snapshot age).
- "Why not" panel: counts and examples of excluded candidates by reason (hostile sector, no path, restricted, no storage, stale snapshot, wallet cap), mirroring the mod's filter reasons so users can tune parameters.
- Snapshot age per row with a staleness badge; competing traders (see below) shown as "assigned to ship X" chips.
- Optional "apply" action: emit a trade order intent for the ship through the normal permission-checked intent path (not automatic).

### 5b. What a server-side version cannot know
- **Live competing traders on other teams** (and NPC traffic): offer volume changes between snapshots; we only see what snapshots show. Mitigation: snapshot age, a reservation table for our own team's assigned routes, optimistic-lock style re-validation at order time.
- Engine-side `available` flag and `clamp_trade_amount` results (credits of the station, storage that just filled, trade-rule edge cases): must be re-checked in game by the mod when the order is created; treat server output as **advice**.
- Blacklists/threat levels, patrols, real pathing (the engine's blacklist-aware `gatedistance`), fog-of-war knowledge on an individual's save, ship pilot skill ranges.
- Station wallet state (reason-code-32 problem) unless snapshots include it.
- True per-offer reservation by orders already issued by other players' ships; only inferable from order lists if the portal exposes them.

### 5c. Running the mod itself in MP
- It is a pure AI order (MD + aiscript) with global MD state; it creates ordinary trade orders. It must run **where the AI runs: on the authority** (ADR-044 phase 3 `AuthorityOnlyMD`-class mod). Client-side installs do nothing for ships simulated on the authority.
- Issues to plan around: (1) license: GPL-3.0 per repo `LICENSE`; X4MP cannot bundle it into non-GPL code, but can list it as an optional, user-installed mod (we link, we do not host, ADR-044). (2) `global.$DATaterQueueTable` etc. are save-game state keyed by object ids on the authority; fine, but a client's trade-completed events must reach the authority for `st` profit tracking. (3) It reads player money (`player.money`) for its 50% guard: with per-player wallets (CreditMode) that value is the authority's player, not the owner of the ship; the guard would be wrong. (4) Faction queues key on `trueowner`, so teams with separate factions (`x4mp_team_N`) get independent round-robin lanes naturally. (5) Sector knowledge (`known=true`) for player ships uses the authority's player knowledge, not each team's; with ADR-038 per-team visibility this over-reveals. (6) Performance: the scan cost scales with ships x sectors x wares, and the authority is already doing the simulation.
- Recommendation: treat the mod as an optional convenience (allowed/blocked via the session mod list), and build our own server finder as the supported, team-aware path.
