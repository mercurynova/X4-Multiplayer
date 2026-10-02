# Notes from the user's X4 save analyzer (reference only)

Source: `reference-analyzer/` (the user's Python save analyzer, GPL-3.0, ~8.8k lines; local-only
reference). **Never copy its code and never ship its `src/x4analyzer/data/*.csv`** (extracted Egosoft
data). This doc records *facts and lessons in our own words*. Paths below are relative to
`reference-analyzer/`; `sp` = `src/x4analyzer/saveparser.py`, `fr` = `frames.py`, `gd` = `gamedata.py`.
The analyzer targets game 9.00 (same line as our pinned build 611726); its test save is `modified="1"`.

Where I could, I cross-checked a claim against the user's real save
(the user's own `quicksave.xml.gz`, game 9.00 build 611726, read-only grep); those are marked **[checked]**.

---

## 1. Save file format

### 1.1 Reading
- Saves are XML, normally gzipped (`.xml.gz`); the analyzer opens `.gz` with gzip and plain `.xml` directly
  (`sp:85-88`). One streaming `lxml.iterparse` pass, `start`+`end` events, `recover=True, huge_tree=True`
  (`sp:118-121`). Elements are cleared on `end` and older siblings deleted (`sp:363-368`); ~18 s and ~270 MB
  peak for a 73 MB `.gz` (`CLAUDE.md:34`); a 1 GB save in seconds (`README.md:8-9`). Our C# `XmlReader`
  streaming approach is the same idea.
- Ancestry is tracked with explicit stacks (tag stack, component stack, "nearest station/ship" stack,
  sector-macro stack) rather than a DOM (`sp:105-116`).
- Save folder: `Documents\Egosoft\X4\<numeric id>\save\` (`config.py:36-45`, `config.py:136-146`), possibly under
  OneDrive (`config.py:36-42`). A file with `temp` in its name means the game is mid-save and the analyzer
  refuses (`config.py:152-153`) - our save watcher should also ignore in-progress temp files.
- `<info>` header (`sp:339-353`): `<game guid= version= time= modified=>` (version is `900`, `time` is game
  seconds, `modified="1"` when mods are active), `<save date=>`, `<player name= money=>`. **[checked]** the real
  save has `<game id="X4" version="900" build="611726" modified="1" time="71345.25" code= original= originalbuild=
  start="x4ep1_gamestart_terran1" seed= guid=...>` and `<player name="Raibu Hariken" location="{20004,480011}"
  money="5447419"/>` (so the header also has `build`, `start`, `seed`, and a text-ref `location`).
- Player faction display name: `<faction id="player"><custom><name name=.../>` (`sp:355-357`).

### 1.2 MONEY UNITS (open X4MP follow-up: "verify save money cents")

**Finding: the `<info><player money=>` header value is already in whole credits, NOT cents. Our
`SaveJanitor` currently does `cents / 100` (`server/src/X4MP.Core/Saves/SaveJanitor.cs:124-129`), which looks wrong
by a factor of 100.** Conversely, per-transaction amounts (trade `price`, log `money`) ARE cents.

Evidence, in the analyzer:
- `export.py:273`: `"credits": round(save.player_money),  # already in credits, not cents` - the
  header value is passed through with no division, with an explicit comment.
- `sp:353`: `d.player_money = float(elem.get("money"...))` - read raw; every other money field is divided by 100
  (`sp:319` offer price, `fr:528` trade price, `export.py:190`, `logparse.py:71,162`).
- `CLAUDE.md:66`: "Money in save files is in cents; divide by 100 (trade `price`, log `money`)" - the
  scope there is the per-transaction fields, not the header.
- Test fixture is ambiguous: `tests/test_saveparser.py:13` uses `money="123456"` but asserts nothing about it.
  `tests/test_logparse.py:62,76`: log `money=1234500` becomes 12345 Cr (cents confirmed for log entries).

Evidence, in the user's real save **[checked]**:
- Log entry `title="Guild bonus: 19,477 Cr x 3 ... = 58,431 Cr"` has `money="5843100"` (= 58,431 Cr x 100): log money
  is cents. Mission reward entry `money="7791000"` (77,910 Cr).
- Market offer `<trade ware="energycells" price="1057">` (10.57 Cr, matches typical energy-cell prices): offer prices
  are cents.
- `<player money="5447419">` and the player's `<account id="[0x11a]" amount="5447419"/>` carry the *same*
  number. Read as credits that is 5.4 M Cr at ~20 h of play (consistent with 78 k Cr mission payouts); read as
  cents it would be 54 k Cr, inconsistent with those payouts. Other accounts: `amount="65080" own="1"`,
  `amount="265333" min="450450" max="675675" own="1"` (station accounts; unit appears to be credits too,
  `min`/`max` look like manager's wanted band).

Action: confirm with the user's in-game balance for `quicksave`/`#005` (should read about 5,447,419 Cr), then
fix the save service to treat `PlayerMoney` as credits (and fix the `SaveModels.cs:218` doc comment); update
`roadmap.md:144` follow-up (2). Keep V04's MD-cents / Lua-credits API finding separate: that is the live API,
this is the on-disk header. Also note accounts live in `<account id=... amount=...>` elements (the player's
is `[0x11a]`); the analyzer never reads them, so which account is "the player wallet" is unverified.

### 1.3 Objects, ids and ownership (`sp:125-193`, `fr:86-110`)
- Everything in the universe is nested `<component class= id= macro= name= code= owner= knownto= connection=
  spawntime= basename= contested=>`. Classes of interest: `galaxy > cluster > sector > station | ship_xs/s/m/l/xl |
  gate | satellite | buildstorage | collectablewares/recyclable/lockbox | npc` (`sp:21-24,93`, `CLAUDE.md:49`
  `ship_xs` is new in v9). Nested containers: `<connections><connection connection="..."><component .../>`
  (fixture `tests/test_saveparser.py:22-86`).
- **Component id** is `id="[0x...]"` (hex in brackets); it is the stable key inside one save and is what
  trades/orders/commanders reference. **Player-visible id code** is the `code` attribute, format
  `ABC-123` (`logparse.py:19` regex `[A-Z]{3}-[0-9]{3}`); NPCs also have codes (`NPC-001` in the fixture).
  Both are per-save-stable; for portal notes keyed by ABC-123 see §6.
- `owner` = faction id string (`player`, `argon`, `xenon`, `ownerless`, ...) (`sp:188`, `refdata.py:20-31`).
  Player assets = `owner == "player"` and class `station` or `ship_*` (`fr:114-119`).
- `knownto="player"` marks objects the player has revealed (`sp:189`). **This is the only persisted
  player-knowledge in the save** (`docs/player-view-plan.md:23-31`): their save had 1,342/1,736 stations, 125/152
  sectors and 8,090/14,420 NPC ships revealed. `known=` (no `to`) also appears on gates/sectors/clusters with
  different semantics; use `knownto` only (`player-view-plan.md:168-170`).
- Display names: station/ship `name` may be empty (most NPC ones) or a text ref `{page,id}`; resolve via the t-file
  (`fr:94-97`, `refdata.py:72-77`). Unnamed objects get a synthesized label: `basename` attr if present, else
  "Shipyard/Wharf/Equipment Dock" if it has build modules, else "<main product> Factory" (`fr:177-201`).
- NPC crew: `<component class="npc" owner="player" name= code=>` with `<skills piloting= engineering= boarding=
  management= morale=>` (`sp:133-136,244-246`, `fr:138-150`). Role assignment lives in `<control><post id=
  component=/>` where ids are `aipilot`, `engineer`, `manager`, `shiptrader` (`sp:208-214`, `fr:231-258`).
  Crew counts: `<person role="service|marine|passenger|prisoner">` under a ship (`sp:144-147`).
- Workforce: `<workforces><workforce race= amount=>` per station (`sp:216-221`).
- Orders: `<order order=<name> default=1|0 state=>` under ship/station (`sp:297-303`).

### 1.4 Commander / subordinate (fleet) relationships (`CLAUDE.md:50`, `sp:195-206`, `fr:130-138`)
- **Hierarchy encoding:** follower has `<connection connection="commander" id=X><connected connection="[C]"/>`; the
  commander has `<connection connection="subordinates" id="[C]"/>`. Join on the connection id `[C]`:
  leader = object that owns the `subordinates` connection whose id equals the follower's `connected@connection`
  (verified by fixture `tests/test_saveparser.py:55,69-70,146-147`: `commander_links == [("[0x30]","[0xC1]")]`).
  Restrict to player-owned pairs.
- **Pitfall:** flat `<subordinate>` elements in the save are the NPC job system, NOT player fleets (`CLAUDE.md:50`).
- The group assignment attribute is misspelled `assignmment` in the save (`CLAUDE.md:53`). Wing/group naming is
  not parsed by the analyzer.
- Trade attribution lesson: only the *current* hierarchy is saved, no history, so past trades may show under a
  commander the ship did not have then (`CLAUDE.md:53`). Stations can be commanders (the fixture station owns the
  `subordinates` connection of a docked ship - station-assigned traders).

### 1.5 Cargo and stock (`sp:248-258`)
- `<cargo><ware ware=<id> amount=/>` under the nearest station/ship/build-storage ancestor. Build-storage cargo is
  attributed to the storage, not the station, which cleanly separates construction stock (`sp:91-98`).
- Ammo/drone reserves live in `<supplies><wares>` and must NOT be counted as cargo (`sp:258-262`).
- Free-floating wares: classes `recyclable`, `collectablewares`, `lockbox` with `<wares><ware>` (`sp:258-269`).

### 1.6 Station trade offers (buy/sell lists, prices) (`sp:311-320`)
- Under a station (or build storage): `<trade><offers>...<trade id= buyer=|seller= ware= price= amount= desired=
  flags=>`. Side = `buyer` attr present means the **station buys** (a buy offer); `seller` present means it sells.
  `price` is **cents per unit** (fixture: `price="100"` parsed as 1.0 Cr, `tests/test_saveparser.py:47-48,156`).
  **[checked]** real offers also carry `partner=` and `flags="...|fixedprice|invertfactionrestriction|
  sellermoneyvirtual|buyermoneyvirtual"`. `amount` is the open volume, `desired` the target level.
- Offers sit in sub-blocks of `<offers>` (e.g. `<production>`); the analyzer matches any `<trade>` with `ware` and
  buyer/seller inside an `offers` ancestor.
- **No per-offer timestamps, price snapshots, or scan/staleness state exist anywhere in the save** (verified by an
  attribute sweep, `player-view-plan.md:26-28`). Therefore "last seen" market values (ADR-050 `Strict`) must come
  from our own periodic snapshots, not from a save.

### 1.7 Trade/economy logs
- `<economylog>`: `<entries type="trade"><log time= type="trade" ware= buyer= seller= price= v=>` plus
  `<removed><object id= owner= name= code=>` (a catalog of destroyed objects so old trades still resolve names)
  (`sp:331-337`, fixture `tests/test_saveparser.py:88-97`).
  - **Two flavors** (`CLAUDE.md:48`): (a) real transaction: has `buyer`+`seller`+`price`+`v` (`v` = units,
    `price` = cents/unit; `fr:455-457,528-531`); (b) owner-only `<log owner= ware= v=>`, where **`v` is the station's
    stock level after the trade, not a traded amount**; traded volume must be derived from positive deltas between
    consecutive snapshots per (owner, ware); summing `v` overcounts ~40x (`CLAUDE.md:55`, `fr:380-392`).
  - `buyer`/`seller` are object ids; subordinates trade for commanders (proxy logic `fr:466-491`).
  - The economylog is a rolling window (~19.5 h in their save, `continuous-construction-demand.md:136-137`).
- `<log><entry time= category= title= text= money= entity= component=>`: the player's logbook. Categories include
  `upkeep` and `missions`; text newlines are the literal marker `[\012]` (`logparse.py:7-8`). Rolling window too, so the
  analyzer persists history in per-GUID caches (`CLAUDE.md:36`).

### 1.8 Sectors, resources, gates in the save
- Sector: `owner`, `contested="1"`, `knownto`. Resource areas `<resourceareas><area yieldid="sphere_large_ore_high_slow"
  yield=N>`; ware parsed out of the `yieldid` with a regex over `ore|silicon|nividium|ice|hydrogen|helium|methane|
  rawscrap|scrap` (`sp:26-30,322-329`, `CLAUDE.md:47`). v5.10's per-ware `recharge` is gone.
- 220 gate components exist in their save with sector ancestry (`analytics-ideas.md:27-28`), but the full gate graph
  comes from game data (§2.4) so unrevealed links are covered.
- Satellites: `class="satellite" owner="player"` (343 in their save); player licences in `<faction id="player">
  <licences><licence type= factions=>`; faction trade-subscription licence type string is unverified
  (`player-view-plan.md:28-31,64-69`).
- Faction relations exist in the save's faction data but are unparsed (`analytics-ideas.md:117-118`).

### 1.9 Stations: modules and build plans (`sp:223-243`, `CLAUDE.md:58,64`)
- Module list: `<construction><sequence><entry id= index= macro=>` on stations (built + queued) and on build
  storages (`<queue><build type="expand"><sequence>`). **The same entry ids repeat twice** (construction sequence and
  expand queue) and include unbuilt entries; dedupe by (host, entry id). A built module component carries
  `construction="[entryid]"`; `state="construction"` means still building (`sp:160-165`). Station module count =
  max entry `index` (`fr:166-177`).
- Loadouts: `<groups><shields|turrets|engines macro=>` inside entries (`sp:305-309`).
- New-station construction sites are free-floating `buildstorage` components with no station ancestor
  (`CLAUDE.md:57`).
- `<insufficient>/<shortage>` amounts under `<build><resources>` are NOT per-ware quantities (disproved in game);
  build demand = the build storage's open buy offers (`CLAUDE.md:57`, `sp:270-289`).

---

## 2. Game data extraction (from the install, not from saves)

### 2.1 Archive reading (`catalog.py`)
- Each `NN.cat` is a text index: one file per line `<path> <size> <mtime> <md5>`, path may contain spaces, so split
  from the right with 3 splits (`catalog.py:31-48`). The paired `NN.dat` is the payloads concatenated in index order;
  **offset = running sum of previous sizes** (`catalog.py:7-8,43-46`). (`x4cat_extract.py` in our `tools/` already does this.)
- Base cats = `NN.cat` (digits only, ignore signature `_sig.cat`); extension cats = `ext_NN.cat` under
  `extensions/<ext>/` (`catalog.py:51-56,79-90`). Later entries override earlier: base in numeric order, then
  extensions in load order; **loose files on disk override everything** (`catalog.py:10-12,98-102`).
- Extension-internal paths are exposed as `extensions/<name>/<path>` (`catalog.py:87-90`).
- Default extension set = folders `ego_dlc_*` sorted by name; with `--include-mods` official first then all others
  (`catalog.py:70-74`, `gd:417-430`). Modded runs need the same enabled set as the save.

### 2.2 Source files and fields (all in `gd`)
| Data | Source file(s) in the virtual FS | Fields used |
|---|---|---|
| Text | `t/0001-l044.xml` (English) + each extension's version (`gd:70-74`) | `<page id><t id>text` |
| Factions | `libraries/factions.xml` (`gd:77-109`) | `id, shortname, name, primaryrace, <color ref>`; colour chain `color ref -> libraries/colors.xml <mapping id ref> -> <color id r g b>` |
| Wares | `libraries/wares.xml` (`gd:112-131`) | `id, name, group, transport, volume, tags, <price average>, <component ref>` (module/ship macro link) |
| Recipes | `wares.xml` `<production method= time= amount=><primary><ware ware= amount=>` (`gd:304-343`) | method defaults to `default`; other methods per race (`terran`, `boron`, `split`...) |
| Production modules | `assets/structures/**/macros/*.xml` `properties/production/queue(ware,method)` or `queue/item`, or `properties/products/ware` (processing) (`gd:242-301`) | + `properties/workforce@max`, `identification@name` |
| Module capacity | same macros, `properties/workforce@capacity,max` (housing / workers), `properties/cargo@max,tags` (`gd:346-372`) | |
| Ships | `assets/units/size_*/macros/*.xml`, class in `ship_xs/s/m/l/xl` (`gd:236-239,375-414`) | `identification@name,makerrace`, `hull@max`, `physics@mass`, `cargo@max`, `people@capacity`, `purpose@primary`; price = ware average price keyed by macro minus `_macro` |
| Cluster positions | `maps/xu_ep2_universe/galaxy.xml` `<connection ref="clusters"><macro ref><offset><position x y z>` (`gd:149-169`) | |
| Sector membership + offsets | `maps/xu_ep2_universe/*clusters.xml` `macro class="cluster" > connection ref="sectors" > macro ref, offset/position` (`gd:171-199`) | |
| Names/descriptions of maps | `libraries/mapdefaults.xml` `<dataset macro><properties><identification name description>` (`gd:134-146`) | |
| **Gates** | `galaxy.xml` `<connection ref="destination">` (`gd:202-233`) | both endpoints are zone paths embedding `<Cluster_01_Sector001>_connection`; regex `/([A-Za-z0-9_]*_Sector\d+)_connection/` + `_macro` gives the sector macros |

### 2.3 Pitfalls
- **DLC files are `<diff>` patches** (add/replace/remove with `sel`), not full documents. Scanning `root.iter(tag)` over
  base + extension versions handles added elements (`gd:36-56`), but additions *inside existing wares* use
  `<add sel="//ware[@id='workunit_busy']"><production method="boron">`; missing those made Terran energy
  production 3.5x too high (`gd:327-341`, `CLAUDE.md:56`). A proper implementation must apply diff ops (xpath
  `sel`) rather than regex-scan; the analyzer's approach is a shortcut. (Note: `replace`/`remove` ops are ignored by it.)
- **Macros must be lower-cased at every boundary**: the save and game files differ in case (`CLAUDE.md:67`, `gd:272-274`).
- **Text refs** `{page,id}` appear everywhere (ware names, faction names, station basenames, even save `name=`
  attributes and the header `location`). They nest (`{1001,2}` inside another string) and carry `(comments)` that the
  game strips, with literal parentheses escaped `\(` `\)`; resolve recursively to depth ~8, strip comments only at
  the top level, keep unresolvable refs visible (`textdb.py:1-8,56-71` per file; resolve at `textdb.py:~165-184`).
  Text files can also be extension `<diff>` form; the loader just iterates `page`/`t` elements (`textdb.py:~145-160`).
- The "economy" tag on wares (`tags` contains `economy`) identifies tradeable economy commodities (`refdata.py:129-130`).
- Mod-added ships have arbitrary macro filenames, so match all `*.xml` in `units/size_*/macros` and filter by class,
  not filename prefix (`gd:376-380`).
- Queue forms vary: `<queue ware= method=/>`, multi-ware `<queue><item ware= method=/>`, or `production@wares`
  list; scrap processors use `<products>` with the "processing" recipe scaled by amount (`gd:242-285`).
- Special owners not in `factions.xml`: `ownerless` (sector owner in saves); player is special-cased to `PLA` and
  ownerless to `NIL` in their short-code scheme (`refdata.py:30-33,123-126`).

### 2.4 Build / version handling
- The analyzer does **not** key data to a game build; it ships one CSV set "v9.0 + all official DLC" and lets the user
  regenerate after a game update (`README.md` Game data; `CLAUDE.md:28`). A per-user data dir overrides packaged
  copies (`refdata.py:80-95`). The save header's `version`/`build` (§1.1) are available but unused for data
  selection. For ADR-050 P1 we should key the cache by build + enabled-extension set (`player-portal.md:25-28`).
- Their data dir lives under `%LOCALAPPDATA%\x4analyzer` (`config.py:24-34`).

### 2.5 Sector graph (`sectorgraph.py`)
- Adjacency = gate/accelerator pairs from `galaxy.xml` plus **all sector pairs within the same cluster**
  (treated as mutually reachable via superhighways), BFS gives gate-hop distance (`sectorgraph.py:1-47`).
  Useful for trade-route finder / "build near" (P3) and for fog-of-war distance (ADR-038).

---

## 3. Economy and market logic worth knowing

(Authoritative statements: `CLAUDE.md:54-60`; plans: `docs/*.md` in the analyzer.)

- **Consumption capacity** = module recipe inputs + population needs. Workforce upkeep comes from the per-race
  `workunit_busy` recipes in wares.xml (e.g. 200 workers consume 75 foodrations + 45 medicalsupplies per 600 s)
  (`CLAUDE.md:56`). Capacity excludes workforce production bonuses (`CLAUDE.md:60`).
- **Build demand** = build storages' open buy offers (§1.6/1.9). **Understocked** = buyers holding < 25% of target
  (stock + wanted); **Fill %** = buyer-side Σheld / Σ(held+wanted) (`CLAUDE.md:59`).
- **Construction-plan estimate** for a site with no funded orders: Σ default-method recipes of unbuilt entries
  (module ware found via wares `component` == macro) + loadout equipment, minus cargo on site; validated within ~1%
  against the in-game "required" numbers, which are gross of delivered cargo and pro-rate partially built modules
  (`CLAUDE.md:58`).
- **Continuous construction demand** (`docs/continuous-construction-demand.md`): estimators from one save:
  (A) yard intake = positive stock deltas at stations with build modules (cleanest, trader-relevant);
  (B) yard draw = negative deltas minus station module consumption (contaminated by resale otherwise);
  (C) spawn-mechanistic: every ship has `spawntime`, so one save holds the whole campaign's spawn timeline; spawns x
  build recipes gives an *upper bound* because many NPC spawns are job-system respawns that consume no market
  materials; (D) construction-only wares (claytronics): producer outflow = absorption. Build storages emit no
  stock events (no economylog entries) (`:55-103`). Caveat: survivorship bias (destroyed ships leave the save).
  Relevant to X4MP M5: when we sync/seed stations we cannot rely on NPC spawn numbers being material-backed.
- **Build advisor** (`CLAUDE.md:41`): score "ware W in sector S" with BFS-hop-discounted factors (demand,
  competition, input supply incl. mining yield, hostile distance, workforce food); weights are client-side sliders;
  every score expands into its reasons (user-confirmed design principle `analytics-ideas.md:8-11`).
- **Station P&L / audit** (`CLAUDE.md:42`): trade attribution by station code (including subordinate proxy); station
  value = sum of module ware average prices. Audit: input starvation, output pile-up, storage saturation (module
  cargo capacity), idle ships (only default orders), staffing/crew gaps. Idle detection = ship has only a default
  order (`sp:297-303`).
- Units: station account transfers appear in the log as "Received surplus ... Credits" (title text, credits) vs
  newer wording with `money` attr (cents) (`logparse.py:110-175`).

### 3.1 Their player-view plan vs our ADR-050 P2 (`docs/player-view-plan.md`)
Their design restricts views to what the player could know, with two layers:
1. **Reveal masking (exact):** hide anything with `knownto != "player"` (`:12-14`).
2. **Live/stale coverage (approximation):** a known station is *live* if the sector holds a player ship, station or
   satellite, or the owner faction is covered by a trade-subscription licence; else *stale* (values unreliable)
   (`:73-82`). Sector-granular; radius-accurate coverage is a deferred phase 2 (`:149-156`).
Mapping to us: `Strict` = their live/stale split, but because we sample the market ourselves we can show last-seen
values with age (they cannot). `KnownStations` = their reveal masking. `Everything` = their default omniscient
mode. Their known weakness (satellite at one gate "covers" a huge sector) applies to our per-team coverage service
too (ADR-038). Mind that stock/capacity columns also leak information (scan state is not saved).

### 3.2 Idea catalogue for portal P3 (`docs/analytics-ideas.md`)
Trade route finder (HIGH feasibility: spread x min(volume) discounted by gate distance, score decomposes into
factors), blueprint ROI (module build recipes + blueprint price vs current best offers), mining site advisor
(resource yield x distance to buyers x NPC miner density), fleet readiness (hull, crew, engineer gaps, mk1 equipment
audit), loss heatmap, supply-chain sankey. Their principle: ranked recommendations with expandable factors, never an
opaque score (`:8-11`). Autosave watch mode: a daemon notices new autosaves and appends compact snapshots (prices,
stocks, ownership, net worth), because the game itself keeps only rolling windows (`:135-145`).

---

## 4. Log parsing (`logparse.py`)

Reads the save's `<log><entry>` rows, filtered to `category=="upkeep"` (excluding title "Trade Completed") or
`category==""` (`fr:292-303`). All are English-text regexes, localization/version sensitive; each returns an empty
frame on no match and dumps 3 sample strings with a warning on drift (`logparse.py:1-9,28-36`). Wordings:
- Ship construction/repair/resupply sales: titles `Ship constructed|repaired|resupplied`, text
  `<FAC> <ship> (<CODE>) finished construction|repairing|resupplying at station: <station> (<CODE>). They have paid <N> Cr.`
  with `money` attr in cents (`logparse.py:43-84`; marked unverified against v9 wording, `CLAUDE.md:51`).
- Destroyed: title `... in sector <S> was destroyed by <K>.` (`logparse.py:87-107`).
- Station manager transfers: `Received surplus of <N> Credits from <manager>` (older; credits in text) and
  `Received surplus from <station> in <sector>` (newer; `money` cents) (`logparse.py:110-174`).
- Pirate harassment: `Pirate Harassment`, text `<ship> <CODE> in <sector>[\012]Accosted by <faction> pirate ship[\012]<FAC> <pirate> <CODE>.[\012]Response: <r>`
  (`logparse.py:177-210`); police: `Police Interdiction ... Ordered by <faction> police to stop ...` (`:213-243`).
- Mission rewards have `category="missions"` with `money` in cents **[checked]**.

Use for X4MP: (a) diagnostics: when log parsing "drifts" the dump-samples-on-mismatch pattern is a good model for our
save-info/importer warnings; (b) the X4 `debuglog.txt` itself is not parsed by the analyzer, so there is nothing to
borrow for mod-log diagnostics; (c) only `time` is game seconds (not wall clock).

---

## 5. Gotchas and lessons (authors' hard-won knowledge)

1. Money: cents in log `money`, trade/offer `price`; header `<player money>` and account `amount` are credits (§1.2).
2. Stations list their build plan twice; dedupe by entry id or capacity doubles (`CLAUDE.md:64`).
3. `<insufficient>` blocks are not per-ware quantities; use buy offers (`CLAUDE.md:57`).
4. Owner-only economylog `v` = stock-after-trade, not volume (`CLAUDE.md:55`).
5. `<subordinate>` elements are NPC jobs, not fleets (`CLAUDE.md:50`); hierarchy only exists at save time.
6. Macros are lower-cased on all sides (`CLAUDE.md:67`); mod-name casing differs.
7. DLC wares are patched via `<diff>`; sub-elements added into existing wares are easy to miss (`CLAUDE.md:56`).
8. Saves are `modified="1"` when mods are on; every join against reference data must tolerate unknown
   macro/faction/ware instead of crashing (`CLAUDE.md:35`).
9. Mod-added items have free-form macro names; filter by class, not file name (`gd:376-380`).
10. Free-floating build storages have no station ancestor (`CLAUDE.md:57`).
11. Log/economylog are rolling windows; keep a GUID-keyed history cache with idempotent merge (`CLAUDE.md:36`).
12. Don't read player-hidden info into player-facing views (`spoilers_hide`, `CLAUDE.md:68`): relevant to the
    portal.
13. pandas `itertuples()` mangles dotted columns (`CLAUDE.md:65`); irrelevant to C# but shows that they used `a.b`
    naming ported from an R script (the R original used a DOM and ~16 GB; streaming fixed it, `sp:3-4`).
14. "Game is saving, try again" (file name contains `temp`) (`config.py:152-153`).

---

## 6. How X4MP should use this

| Finding | X4MP task / ADR |
|---|---|
| **Header money is credits, not cents** (§1.2, `export.py:273`, plus the real-save check) | Fix `SaveJanitor.cs:124-129` (`cents/100` -> credits) and the `SaveModels.cs:218` comment; update `roadmap.md:144` follow-up (2); confirm with the user's in-game balance for `quicksave`/`#005` (about 5,447,419 Cr). Keep log/offer/trade prices as cents. Also decide whether the seed should read the player `<account>` instead. |
| Static data from the host's install: catalog format, file list, diff patches, text refs (§2) | ADR-050 P1 knowledge section. Extract at runtime from the host install keyed by build + enabled-extension hash; implement real `<diff>` application (the analyzer's regex-scan shortcut misses `replace`/`remove`); text-ref resolver with recursion + comment stripping. Never persist or ship the extracted tables in the repo. |
| Fleet hierarchy encoding (§1.4) | ADR-050 P1 commander field: add `commander_id` to `EntitySpawn/EntityChange`. In-game (Lua/MD) the field is "commander" on a ship; the save join is `connected@connection` == leader's `subordinates@id`. Stations can command ships too. |
| Trade offers in saves (§1.6) | ADR-050 P2 snapshot shape: station id, side (buy/sell), ware, price (cents; convert for display), amount, desired, flags (`fixedprice`, `invertfactionrestriction`). Saves hold no staleness, so last-seen ages must be ours. |
| `knownto="player"` as the only persisted knowledge; sector-level coverage model (§1.3, §3.1) | ADR-038 shared per-team visibility service and `Strict`/`KnownStations` modes; a save import can seed the host team's known set but not other teams'. |
| Stable keys: `code` (`ABC-123`) and sector macro (§1.3) | Notes/naming (P1): key notes by code and sector macro as already planned. Caveat: codes are per-object stable but unverified across ship destroy/rebuild or "same name different object"; spike. |
| Sector graph BFS (§2.5) | P3 trade-route finder and ADR-038 distance rules. |
| Market formulas: understocked <25%, fill %, per-race workforce upkeep, Cr/h at average price (§3) | P3 planners / M5 economy balance tests. |
| Continuous construction demand estimators (§3) | M5: expectation that NPC yards absorb materials only for real builds; ties to `docs/continuous-construction-demand.md` in X4MP (if it differs). |
| Build-plan duplication, build storage ownership, `built_refs` logic (§1.9) | M4 world streaming / station mirror: avoid double-counting modules when reading plans. |
| Log wording tables (§4) | Diagnostics; low priority, wording-fragile. |

### Open questions for in-game spikes / a real-save check
1. Is the save header `<player money>` equal to the player's in-game balance (credits)? Is the player account
   `[0x11a]` the wallet we should read? (Also compare after a known purchase.)
2. Are `<account amount>` values for NPC stations credits too, and what do `min`/`max` mean?
3. Which Lua/MD calls return a station's offers (price in cents or credits?) and how costly are they (P2, matches V-list)?
4. Exact trade-visibility rule in game: satellite/ship radius vs sector, docked rule, trade-subscription licence type string
   (their open item `player-view-plan.md:64-69`).
5. Commander/subordinate access from Lua/MD (`GetCommander`-style), and whether wing/group names are readable.
6. Does `code` (`ABC-123`) stay stable across save/load and ownership changes?
7. Station `knownto` for team factions (`x4mp_team_N`): engine only computes knowledge for `player` (`player-portal.md` §2.2).
8. Real `<diff>` semantics needed for P1: which wares/modules actually use `replace`/`remove` in DLCs for 9.00?

Licence reminder: the analyzer is GPL-3.0; use only the facts above, re-derive everything in our own code.
