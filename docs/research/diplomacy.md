# X4 Diplomacy and team diplomacy (research + design proposal)

Status: research and proposal, 2026-10-01. Pending spike **S11** (roadmap §4.3). ADR draft:
[ADR-047](../decisions.md). Nothing here changes the `.fbs` schema yet.

User idea (verbatim): *"Another idea for inter player faction relations is the 9.0 diplomacy
update. You could use that section to deal with inter faction diplomacy."*

Sources:
- Game files in `x4-unpacked/` (X4 9.00 build 611726). Paths below are relative to it unless they
  start with `docs/` or `mod/`.
- The vendored X4Native SDK (`mod/third_party/x4native/v9.0.0-611726/sdk`).
- Spike session 1 (`docs/spikes/session-1-results.md`).
- A web search. Its items are marked **[UNVERIFIED]** because the wiki and patch-note pages
  could not be fetched; only search summaries were read.

---

## 0. Summary

- **Diplomacy arrived in 8.00 ("Diplomacy Update"), not 9.00.** 9.00 ("Empire Update")
  only tweaked it **[UNVERIFIED]**: shorter actions for skilled agents, interference
  achievements, and fixes to Dal's agent hiring and Diplomatic Outpost missions. The 9.00
  files contain the full system: `md/diplomacy.xml` (7,874 lines), `libraries/diplomacy.xml`,
  `md/npc_agent.xml`, `md/story_diplomacy_intro.xml` and
  `ui/addons/ego_detailmonitor/menu_diplomacy.lua`.
- **It is a single-player, `player`-centric minigame:**
  - It runs through the Player HQ embassy, agents recruited from NPC factions, and one global
    influence value.
  - Every action's effect is written in MD against `faction.player`, for example
    `add_faction_relation faction="faction.player"` (`md/diplomacy.xml:3181`).
  - Diplomatic **events** are crises between two NPC factions that the player only
    influences.
  - None of it models two non-player factions negotiating with each other.
- **Our team factions can appear in the menu but cannot really take part.**
  - The "Factions and Relations" tab lists every library faction except `player`, with its
    relation, diplomacy state and lock reason (`menu_diplomacy.lua:1188-1275`). An activated
    `x4mp_team_k` should show up there, under "Unreceptive Factions" unless diplomacy is
    activated for it **[S11.1]**.
  - Agent actions can't target teams usefully:
    - they target the faction HQ station (`isfactionhq`);
    - they need a PHQ embassy and agents;
    - they change only `player` relations.
- **Key correction to our design:** `set_faction_relation_locked` is **faction-wide**, not per
  pair (`libraries/common.xsd:35358`; the action has only `faction` and `locked`). ADR-016 and
  MOD §11.6 say "lock each pair". In practice:
  - Locking `x4mp_team_k` freezes that team's relations to **every** faction, NPCs included.
  - Locking `player` would freeze the local human's reputation gains. We must never lock
    `player`.
- **Recommended design** (§6):
  - Keep the **server's relation matrix as the source of truth** and drive changes through
    the existing `RelationChangeRequest` / `RelationProposal` flow, extended into treaty
    proposals.
  - Show the result in the **vanilla Diplomacy "Factions and Relations" tab** for free. Its
    lock reason text says relations are set by the session.
  - Do the proposing and accepting in **our own "Team Diplomacy" screen**, built with
    vanilla `Helper` widgets and the vanilla diplomacy icons and banners.
  - Later, if the injection spike passes, add that screen as an extra sidebar tab inside
    the vanilla Diplomacy menu.
  - Vanilla agent actions are left alone. They are not repurposed.
- **Milestones:**
  - M5: minimal in-game proposals.
  - M6: per-team NPC reputation, which MOD §11.6 already places in M6.
  - Post-v1: treaties, standing and ADR-040 diplomacy enforcement.

---

## 1. What the 9.00 diplomacy system is

### 1.1 Version and UI

- **Version.**
  - Introduced in **8.00**: public beta July 2025, three pillars (Embassy room at the PHQ,
    Agents, Diplomatic Interference) **[UNVERIFIED]**
    (https://www.pcgamer.com/games/sim/x4-foundations-massive-diplomacy-update-adds-an-actual-embassy-room-to-your-hq-where-you-can-entreat-with-aliens-from-across-the-galaxy-and-establish-yourself-as-space-machiavelli/,
    https://www.gamingonlinux.com/2025/07/the-big-diplomacy-update-for-x4-foundations-is-now-in-beta/).
  - 9.00 is the "Empire Update" (https://steamcommunity.com/games/392160/announcements/detail/528744981882470469)
    with small diplomacy changes **[UNVERIFIED]**.
  - In-file evidence: the code comment "With 8.00 we disabled the story triggering
    PLOT_PARANID_WAR" (`md/diplomacy.xml:7620`).
- **Menu.** `DiplomacyMenu` is a top-level menu entry ("diplomacy",
  `INPUT_ACTION_OPEN_DIPLOMACY_MENU`, `ego_detailmonitorhelper/helper.lua:9777`). Its left
  bar has four modes (`menu_diplomacy.lua:205-210`):

  | Mode | Text | Shown when |
  |---|---|---|
  | `factions` | "Factions and Relations" (`t/0001-l044.xml:1511`) | always |
  | `agents` | "Agents" | story feature `x4ep1_diplomacy_agent` |
  | `embassy` | "Agent Actions" | same |
  | `event` | "Diplomatic Events" | `x4ep1_diplomacy_interference` |

  The features are plain player-entity flags: `player.entity.$x4ep1_diplomacy_agent = true`
  is set when the first agent is recruited (`md/npc_agent.xml:416`), and the interference
  flag is set when an event starts (`md/diplomacy.xml:7488`).
- **Factions tab.** It lists `GetLibrary("factions")` minus `player`. Per faction it shows
  `isdiplomacyactive`, `isrelationlocked` and `relationlockshortreason`
  (`menu_diplomacy.lua:1188-1199`). Factions without active diplomacy sort under
  **"Unreceptive Factions"** (`:1212-1214`, text `t/0001-l044.xml:3835`). Locked relations
  get a lock and the lock reason (`:1265`, `:1697`). The details view lists each faction's
  relations to the others by band (`:1870-1885`).

### 1.2 Embassy and agents

- The **embassy** is the Boron office room on the PHQ
  (`room_gen_boronoffice_01_macro`, `md/story_diplomacy_intro.xml:9415-9461`). Faction
  **diplomats** move onto the HQ when relations are friendly
  (`md/diplomacy.xml:678-904`, `:701`).
- **Agents** are NPCs recruited from a faction's diplomat and re-owned to `player`
  (`md/npc_agent.xml:410-420`, `recruit_diplomacy_agent`).
  - The default limit is 2, expandable by research (`md/diplomacy.xml:120`, `:1225-1265`).
  - Experience levels are Unskilled to Expert, and ranks run Recruit to Spymaster
    (`libraries/diplomacy.xml:1125-1140`).
  - An agent can be given a ship. Ship DPS, prestige and speed shorten actions
    (`libraries/diplomacy.xml:1152-1178`).
- **Influence** is a single player value (`GetPlayerInfluence()`, `apply_player_influence`)
  with levels none 0 to veryhigh 33 (`libraries/diplomacy.xml:3-12`). Higher influence
  inflates money costs (`influencecostcurve`, `:1141-1150`).
- **Which factions take part** is driven by an MD table,
  `md.Diplomacy.Start.$DiplomacyFactionTable`, with `$DiplomatAvailable`, `$EventCapable`
  and `$InateAgentExperience` per faction (`md/diplomacy.xml:37-81`). DLC setup scripts
  extend it (`extensions/ego_dlc_boron/md/setup_dlc_boron.xml:39-43`). Every 30 s the cue
  `Diplomacy_ActiveFactionCheck` applies `set_faction_diplomacy_active` and
  `set_faction_diplomacy_events_allowed` to factions in that table (`:638-657`).

### 1.3 Agent actions (`libraries/diplomacy.xml`)

The table shows influence (I), credits, base duration, cooldown, success chance and effect.

| id (line) | Category | Cost | Time | Success | Effect (MD) |
|---|---|---|---|---|---|
| `cultivate_influence` (19) | negotiation | 5,000 Cr | 900 s / cd 1200 s | 100 % | +4 influence |
| `improve_relations_low` (35) | negotiation | 1 I, 250,000 Cr | 1800 s | 100 % | `add_faction_relation player→F +0.00256` (`md/diplomacy.xml:3181`) |
| `improve_relations_medium` (50) | negotiation | 4 I, 150,000 Cr + bribe ware | 3600 s | 70 % | +0.0064 (`:3400`) |
| `negotiate_ceasefire` "Armistice" (70) | negotiation | 20 I, 4,000,000 Cr + bribe | 1800 s / cd 3600 s | 40 % | F→player set to `dock.min + 0.001` (`:3619-3620`) |
| `active_station_information` / `active_ship_information` (89/109) | negotiation | 2 I + wares | 10800 s | 100 % | live sensor data at the target |
| `negotiate_trade_deal` (129) | negotiation | 2 I, 50,000 Cr + bribe | 1800 s | 70 % | unlock `diplomaticdiscount` at the station |
| `declare_war` "War Declaration" (152) | negotiation | 0 | 300 s / cd 600 s | 100 % | player→F set to `kill.min`, +12 influence (`:4591`) |
| `build_intelligence_network` (168) | espionage | bribe | 1800 s | 50 % | influence |
| `steal_blueprint_*` (187–270) | espionage | 7–20 I, 0.125–15 M Cr | 1800–3600 s | 30–50 % | blueprint |
| `spy_station` / `spy_ship` (271/293) | espionage | 7/12 I | 18000 s | 70 % | sensor data |
| `acquire_inventory_item_*` (315–370) | espionage | 2–7 I | 900–2400 s | 30–80 % | inventory items |
| `initiate_diplomatic_interference` (373) | interference | 12 I + caviar | 1800 s | 100 % | starts a **diplomatic event** between a chosen **faction pair** (`<factionpair>`, `:384-389`; MD `:6727-6737`) |
| `enable_protocol_null` / `disable_protocol_null` (392/401) | interference | 7/0 I | 1800/600 s | 100 % | random NPC↔NPC events every few hours (`md/diplomacy.xml:7798-7840`) |
| `enable_factionlogic_hereticsend` (410) | interference | 0 | 900 s | 100 % | story/faction-logic trigger (Heretic's End) |

Notes on the table:
- Most negotiation and espionage targets are filtered by relation range and
  `excludeanyfactiontags=tag.nodiplomacyselection` (`libraries/diplomacy.xml:84, 182…364`).
  The HQ-targeted actions need `isfactionhq=true` (`:45, :65, :81, :161`).
- Failing a risky action can injure or kill the agent (`Risk_EvaluateOutcome`,
  `md/diplomacy.xml:2142`).
- Costs come off `faction.player` money, influence, and the PHQ defence NPC's inventory or the
  player's own inventory (`CostDeduction_DiplomaticAction`, `md/diplomacy.xml:1852-1900`).

### 1.4 Diplomatic events

- **Events** (`libraries/diplomacy.xml:423-1097`) are crises between **two NPC factions**.
  - The type depends on their relation band (`GenerateDiplomaticEvent`,
    `md/diplomacy.xml:2626-2673`): ally, friend, neutral, enemy or hostile. Examples are
    `neutral_trade_dispute` and `hostile_peace_talks`.
  - Each event runs 2–4 h (`duration` 7200–14400) and has options *cooperation*, *status
    quo* or *escalation*.
  - Each option has a cost, a risk, a weight and a resulting relation value
    (`<relation value="-0.11"/>`, `:1013`).
  - The player can assign an agent and pick a preferred option, which raises its weight
    (`SetDiplomacyEventOperationOption`, `menu_diplomacy.lua:503-523`).
  - On conclusion, MD sets `Faction1↔Faction2` to the outcome's relation
    (`md/diplomacy.xml:7614-7618`). Police arrangements break if the two fall below friend
    (`:7627-7638`).
- **Exclusions.** `set_faction_diplomacy_exclusion` blocks a pair with flags `base_1..20`,
  `dlc*_*` (`common.xsd:4810ff`). Vanilla uses it for story-locked pairs
  (`md/diplomacy.xml:104-117`) and to hold a pair while an event runs (`:7485`, cleared at
  `:7540`).

### 1.5 What does *not* exist in vanilla

- Player-to-player negotiation.
- Proposals that another party accepts.
- Alliance or trade treaties as objects.
- Any party except `player` initiating actions.

"Ceasefire" and "war" are one-sided player actions against an NPC faction's HQ.

---

## 2. Implementation: engine vs MD vs Lua

| Layer | What it does | Hookable? |
|---|---|---|
| **Library** `libraries/diplomacy.xml` (+ `diplomacy.xsd`) | Data: actions, events, gifts, ranks, curves. DLCs extend it with `<diff>` (`extensions/ego_dlc_terran/libraries/diplomacy.xml`) | **Yes**: our extension can `<add>` new actions and events |
| **Engine** | Operation objects, timers, cooldowns, the agent list and `CanStartDiplomacyAction`. Exposed to Lua as FFI (`menu_diplomacy.lua:130-183`) and to MD as actions (`create/start/complete_diplomacy_action_operation`, `create/start/evaluate/complete_diplomacy_event_operation`, `set_diplomacy_action_hidden`, `recruit_diplomacy_agent`, …; `common.xsd:32494-32830`) and events (`event_diplomacy_action_operation_{created,started,aborted,completed}`, `event_diplomacy_event_operation_{…,option_chosen}`; `common.xsd:12193-12300`) | MD events, plus X4Native typed callbacks `on_diplomacy_*_before/after` (`sdk/x4_md_events.h:1633-1845`) |
| **MD** `md/diplomacy.xml` | **All effects**: one instantiated cue per action id, listening to `event_diplomacy_action_operation_started` with a `check_value` on `action.id`, then cost, success, duration, agent movement and rewards (e.g. `:3006-3190`). Event generation and conclusion (`:2626`, `:7468-7740`), Protocol Null | **Yes**: a custom action id gets its own cue in our MD. Vanilla cues ignore unknown ids |
| **Lua** `menu_diplomacy.lua` | Presentation. Starts actions with the global `StartDiplomacyActionOperation(actionid, agentid, params, gift)` (`:464`); picks event options with `C.SetDiplomacyEventOperationOption` | The `menu` table is registered in the global `Menus` (`:262-268`), so its functions (`createLeftBar`, `createInfoFrame`, `buttonTogglePlayerInfo`) can be wrapped from another addon. Its `config` (left bar) is a file-local upvalue, the same problem as MOD §7.2 |

Relations primitives:
- `set_faction_relation`, `add_faction_relation`, `reset_faction_relation` and
  `set_faction_relation_locked` (`common.xsd:35318-35390`).
- Script properties `isrelationlocked`, `isdiplomacyactive`, `arediplomacyeventsallowed` and
  `isdiplomacyexcluded.{$f}` (`libraries/scriptproperties.xml:1848-1851`).
- `set_faction_headquarters` (`common.xsd:20381`, "fails if the station's owner is not the
  correct faction").
- `set_faction_known` (`:20343`).
- X4Native: `x4n::faction::get_relation()` reads any pair (`sdk/x4n_faction.h:8`, which warns
  that reads can clean up expired boosts) and `on_faction_relation_changed`
  (`sdk/x4_md_events.h:2523-2574`).

**Conclusion:** the actions are MD-driven, so they can be **replicated or replaced** but not
**reused for teams**, because each effect hard-codes `faction.player`. The cue structure is
also verbose: 20 near-identical action cues of about 220 lines each.

---

## 3. Can `x4mp_team_1..8` take part?

| Question | Answer | Evidence / status |
|---|---|---|
| Listed in the Diplomacy "Factions and Relations" tab? | **Probably yes, once active.** The tab iterates `GetLibrary("factions")` minus `player`. Whether the library filters by `active`/`knowntoplayer`/the `hidden` tag is engine-side | `menu_diplomacy.lua:1188-1199`; `knowntoplayer` property `scriptproperties.xml:1833`; **[S11.1]** |
| Shown as "receptive" (diplomacy active)? | Only if we call `set_faction_diplomacy_active`. Vanilla's 30 s check touches only factions in `$DiplomacyFactionTable`, so it won't reset ours | `md/diplomacy.xml:638-657`; **[S11.3]** |
| Lock shown with our reason text? | Yes in principle: `set_faction_relation_locked` takes `reason`/`shortreason` `'{page,line}'` "shown to player", and the tab displays `relationlockshortreason` | `common.xsd:35358-35390`, `menu_diplomacy.lua:1265,1697`; **[S11.1]** |
| Target of agent actions? | **No, not usefully.** (1) HQ actions need a station with `isfactionhq`. Teams have none unless we call `set_faction_headquarters` on a team station. (2) Effects only change `player`↔F. (3) Agents need a PHQ embassy and `player`-owned agents, and in a session the save's PHQ is re-owned to the inherit team (ADR-033), so no node has a `player` HQ. (4) The spike factions carry `nodiplomacyselection`, which excludes them from ceasefire and espionage target lists | `libraries/diplomacy.xml:45-84`, `md/diplomacy.xml:1866-1869`; spike `factions.xml` tags |
| Participants in diplomatic events / interference? | Possible: the faction-pair dropdown lists every faction with `arediplomacyeventsallowed` except `player` (`menu_diplomacy.lua:2441-2450`). **Not wanted** for teams: an event outcome would `set_faction_relation` between two teams behind the server's back (`md/diplomacy.xml:7618`), unless locked | **Keep `set_faction_diplomacy_events_allowed false`** for teams |
| Needed faction properties | No `diplomacy` tag exists. Participation is runtime state (`set_faction_diplomacy_active`, `…_events_allowed`), not a library tag. Relevant tags: `nodiplomacyselection` (excluded from action target lists), `hidden` (likely hides from UI lists **[S11.1]**), `claimspace`. `hasownaccount` is unrelated (money; confirmed V04). Optional `<icon banner="…">` for the faction banner in the menu (`factions.xsd:39`, e.g. `factions.xml:93`) | `factions.xsd:182-208` |

**Bottom line:**
- Teams are **displayable** in vanilla diplomacy, and lock reasons give a clean way to say
  "managed by the session".
- They are not **negotiable** through vanilla diplomacy without rewriting its MD.

---

## 4. Can the vanilla UI be repurposed for team ↔ team relations?

Options considered:

| # | Approach | Verdict |
|---|---|---|
| A | **Custom agent actions** via a `libraries/diplomacy.xml` diff (`x4mp_propose_alliance`, `x4mp_declare_war`, … with `<factionpair>` or station params), handled by our MD cue on `event_diplomacy_action_operation_started` that forwards to the server and immediately `complete_diplomacy_action_operation`s | **Rejected for v1.** Every action requires an `<agent>` (`diplomacy.xsd:175`) and the Agent Actions tab requires `x4ep1_diplomacy_agent`, a PHQ and recruited agents. No session node has these by default. We could fake the unlock flag (`npc_agent.xml:416`), but then the vanilla agent UI would show with no agents |
| B | **Custom diplomatic event** as the "incoming proposal" UI: `create_diplomacy_event_operation event="x4mp_treaty" faction=team_a otherfaction=team_b agent=null` (Protocol Null passes a null agent, `md/diplomacy.xml:7834`) | **Spike only (S11.4).** The event tab needs `x4ep1_diplomacy_interference`. Choosing an option goes through an agent (`SetDiplomacyEventOperationOption(agentid, …)`). Outcomes are weighted random, not a deterministic accept |
| C | **Inject a "Teams" tab** into `DiplomacyMenu` by wrapping `menu.createLeftBar`/`createInfoFrame`, and render our own panel in that mode | **Later (S11.6).** Same fragility class as MOD §7.2. UIX also replaces `menu_diplomacy` (`docs/research/library-mods.md` §3.3), so injection must coexist with UIX |
| D | **Own "Team Diplomacy" screen**, opened from the Multiplayer screen and a chat command, styled like vanilla (`Helper` tables, `diplomacy_banner_*` and `mapst_factionrelation` icons, relation colours `text_ally`/`text_enemy`/…). The vanilla Factions tab stays the read-only "world view" | **Recommended** |

**NPC relations: per team or shared?** X4 has one `player` faction, but every team is a real
faction with its own relation row. That makes **per-team NPC reputation** possible. See §6.3.

---

## 5. Conflicts

1. **Relation lock is faction-wide** (`common.xsd:35358`; spike: locking `x4mp_team_1` blocked
   `set_faction_relation(team1, player)`, log lines 221-223).
   - Locking a team freezes **all** its relations. That includes NPC reputation from team AI
     kills and anything vanilla diplomacy would do.
   - Applying a change is always *unlock → set → relock*, in one MD action block on every
     node. MOD §11.6 already does this.
   - **Never lock `player`.** That would stop mission and kill reputation for the local
     human and break vanilla diplomacy rewards (`md/diplomacy.xml:3181`). The `player`↔team
     pairs are still protected, because the team side is locked **[S11.2: confirm that a lock
     on one side blocks changes requested from the other side]**.
   - Per-team NPC reputation (§6.3) works by unlocking briefly on the authority.
2. **Vanilla diplomacy acts on `player`, our teams are `x4mp_team_k`.**
   - Any vanilla action a human manages to run on a client changes only that client's local
     `player` relation. Under ADR-016 that's overwritten at the next sync.
   - Under §6.3 it is captured as a reputation delta for the team.
   - Costs (`transfer_money from=faction.player`) show up as local money changes and are
     already reconciled by the economy (`event_player_money_updated`, ADR-019).
3. **NPC↔NPC relations now change at runtime.** Interference, Protocol Null and story all
   `set_faction_relation` between NPC factions (`md/diplomacy.xml:7618`). Our design
   replicates only team relations.
   - **Authority:** report NPC↔NPC changes, from the native `on_faction_relation_changed`
     or an MD cue on `event_faction_relation_changed`.
   - **Clients:** apply them, and **must not generate their own**. Clients run the same
     `md/diplomacy.xml`, so Protocol Null (if the save has it researched) would roll events
     locally.
   - Suppression on clients: mark every `$DiplomacyFactionTable` entry `EventCapable='no'`
     through vanilla's own `Diplomacy_ActivateFactionLibrary` (`:595-616`), so the 30 s
     check stops re-enabling events. Then `set_faction_diplomacy_events_allowed false`
     **[S11.5]**. This belongs with the general client NPC-suppression work (V11).
4. **Authority vs client.**
   - The server decides team relations. MD on **every** node applies `TeamRelations`
     (MOD §11.6).
   - Diplomacy *simulation* (events, Protocol Null, agent actions that touch NPC state) runs
     **only on the authority**. Its NPC↔NPC results replicate as in item 3.
   - Our team-diplomacy MD is just "apply matrix/relations" and stays idempotent.
5. **Vanilla agent diplomacy in a session.**
   - The PHQ, agents and influence are `player` singletons on the authority's save.
   - After ADR-033's `inherit_team` re-own, `player.headquarters` is null on every node, so
     the embassy can't be used.
   - v1: leave it unavailable and document it. Optionally hide the vanilla actions with
     `set_diplomacy_action_hidden` on clients.
   - Post-v1 option: a session setting that lets the **authority** run vanilla agent actions
     for one team. Not planned.

---

## 6. Design proposal

### 6.1 End to end: team ↔ team diplomacy

```
Leader opens Team Diplomacy (own screen; or tab inside DiplomacyMenu after S11.6)
  └─ picks other team + treaty: Alliance | Ceasefire | Peace (to Neutral) | Break alliance | Declare war | Trade agreement
Mod → server: RelationChangeRequest{request_key, other_team, relation, (+ kind, terms, note)}
Server (SessionActor, TeamDiplomacyService):
  validate leader role, RelationChangePolicy, cooldowns, treaty rules
  unilateral (Declare war, Break alliance):   schedule apply after NoticeSeconds (default 60; vanilla war = 300 s)
  mutual (Alliance, Ceasefire/Peace, Trade):  create Proposal{id, from, to, kind, expires}; reply TeamRequestResult{Pending}
     → RelationProposal{...} to the other team's leader (and members, read-only)
Other leader: Accept / Decline / Counter → RelationChangeRequest{kind=Accept|Decline, proposal_id}
Server applies: matrix.With(a,b,r) → version++ → persist → audit
  → TeamRelations{full=false, changed pairs} to all nodes + GameEvent{TeamEvent{kind, a, b, by}}
Every node's MD actions shim: unlock x4mp_team_a/b → set_faction_relation (+ player↔team on members' nodes) → relock (reason = "{X4MP page, 'Set by session diplomacy'}")
Authority is authoritative for consequences (fights start/stop); clients only see.
```

**Treaty rules (server, `TeamDiplomacyOptions`; defaults are proposals):**

| Treaty | From → to | Who | Rule |
|---|---|---|---|
| Alliance | Neutral → Allied | mutual | Both leaders, the proposal expires in 120 s (already in PROTO §14.5) |
| Ceasefire / Peace | Hostile → Neutral | mutual | Optional minimum ceasefire time (`MinCeasefireMinutes`, default 10) before war can be declared again |
| Break alliance | Allied → Neutral | unilateral | `NoticeSeconds` (default 60) warning broadcast first |
| Declare war | Neutral → Hostile | unilateral | `NoticeSeconds`, plus a cooldown after peace. Hostile from Allied needs Break alliance first |
| Trade agreement | flag on a Neutral pair | mutual | Not an X4 relation. Widens economy scopes (ADR-020) so trades/escrow treat the pair as `Allied` for trade only. Docking at a Neutral team's stations follows X4's dock range at relation 0 **[UNVERIFIED]** |

This maps onto `RelationChangePolicy` as a new value **`Diplomacy`**:
- `AdminOnly` stays the default (ADR-017).
- `LeadersMutualAlly` becomes a subset of `Diplomacy`.
- Admins always override through `SetRelationCmd`.

**Protocol mapping** (no `.fbs` edits now; a schema delta for the milestone that builds it):

| Need | Existing message | Additions (proposed) |
|---|---|---|
| Propose / unilateral change | `RelationChangeRequest` 0x0705 {request_key, other_team, relation} | `kind` (Propose, Accept, Decline, Withdraw, Unilateral), `treaty` (Alliance, Ceasefire, BreakAlliance, DeclareWar, TradeAgreement), `proposal_id` (for answers), `note` (≤ 140 chars) |
| Notify the other leader | `RelationProposal` 0x0708 {from_team, to_team, relation, expires_in_s} | `proposal_id`, `treaty`, `proposer_player`, `note`, `state` (Open, Accepted, Declined, Expired, Withdrawn); also sent to members read-only |
| Result to proposer | `TeamRequestResult` 0x0706 {status Ok/Pending/Rejected, reason} | reasons `Cooldown`, `NotLeader`, `TreatyNotAllowed`, `ProposalExpired` |
| Apply everywhere | `TeamRelations` 0x0701 | (unchanged) optional per-pair `flags` (TradeAgreement) and `pending_until` (notice period) |
| Log / toast | `GameEvent{TeamEvent}` | `treaty`, `by_player` |
| NPC↔NPC replication (§5.3) | — | **new** `NpcRelationReport` A→S {faction_a, faction_b, value, reason} and `NpcRelations` S→N {version, entries} |
| Per-team NPC reputation (§6.3) | — | **new** `ReputationDelta` C→S {faction, delta, reason} and `TeamNpcRelations` S→N {team, entries[(faction, value)]} |

### 6.2 MD apply path

There is one MD library in our extension: `X4MP_ApplyRelations(entries)`, called by the
native side through the Lua→MD shim (V14: ~1 frame). For each entry:

```xml
<set_faction_relation_locked faction="$TeamA" locked="false"/>
<set_faction_relation_locked faction="$TeamB" locked="false"/>
<set_faction_relation faction="$TeamA" otherfaction="$TeamB" value="$Value" reason="relationchangereason.…"/>
<!-- + player ↔ team rows for this node's own team (ADR-016) -->
<set_faction_relation_locked faction="$TeamA" locked="true" reason="'{92000,201}'" shortreason="'{92000,202}'"/>
<set_faction_relation_locked faction="$TeamB" locked="true" reason="'{92000,201}'" shortreason="'{92000,202}'"/>
```

- `set_faction_diplomacy_active false` and `set_faction_diplomacy_events_allowed false`
  apply to all team factions.
- `set_faction_diplomacy_exclusion` applies to every team pair and team↔NPC pair with a
  spare flag, e.g. `base_20`. That keeps interference and Protocol Null away from teams even
  if a faction's events get enabled.
- The idempotent re-apply on `TeamRelations{full=true}` stays as in MOD §11.6.

### 6.3 Team ↔ NPC relations policy

| Option | Behaviour | Pros | Cons |
|---|---|---|---|
| **N1 Fixed copy** (today, ADR-016) | Each team copies the save's `player` relations at session creation, locked | Trivial, already planned | Missions and kills give no reputation; diplomacy is meaningless toward NPCs |
| **N2 Shared session reputation** | One reputation row for the whole session. Every player's deltas apply to all teams | Simple mental model in co-op | Rival teams share each other's crimes. Odd in versus |
| **N3 Per-team reputation** (recommended, `NpcReputationMode=PerTeam`) | A client's `player` relation change (`event_player_relation_changed`, or the native `on_faction_relation_changed` filtered to `player`) → `ReputationDelta` → server validates (rate limit, per-reason caps, faction must be NPC) → authority runs `add_faction_relation x4mp_team_k ↔ F` (unlock/relock) → server pushes `TeamNpcRelations` to that team's members → each sets its local `player`↔F to the team value. Authority-side team AI effects (team ships killing NPCs) apply to the team faction directly while it is briefly unlocked, or are re-derived by the server | Teams have their own diplomacy. Reputation is real gameplay. ADR-040 penalties become possible | Needs the M6 reputation sync (already planned, MOD §11.6). The lock-window races need tests |

**Recommendation.**
- Make N3 the target, delivered with the M6 reputation sync. With one team, N3 = N2
  automatically, which matches the `CreditMode=Auto` precedent.
- Keep N1 until then.
- Story-driven relation changes (plot scripts setting `player`↔F) count as reputation deltas
  of the team that played the mission (ADR-037: story is per team).

### 6.4 Reuse vs build

| Reuse from vanilla | Build ourselves |
|---|---|
| Diplomacy menu **Factions and Relations** tab as the read-only galaxy view: teams appear with colours, relation band and lock reason ("Set by session diplomacy") | Team Diplomacy screen (list of teams, relation, standing, open proposals, treaty buttons, history) |
| `set_faction_relation` / `_locked` with `reason`/`shortreason`; relation bands and UI values (`relation.{x}.uivalue`, −30..+30) for display | Server `TeamDiplomacyService` (proposals, expiry, notice timers, cooldowns, trade agreement flags, audit) |
| Icons and banners: `diplomacy_banner_armistice`, `_declarewar`, `_negotiatediscount`, `_improverelations`; `mapst_factionrelation`; relation colours | Protocol additions (§6.1) and the GUI matrix editor (exists, M1-T5) plus a proposals log |
| Notification and logbook patterns from `Generate_ActionLogbookEntry` (`md/diplomacy.xml:2334`) | NPC↔NPC relation replication and client suppression of diplomacy events |
| `set_faction_diplomacy_exclusion` to shield team pairs | Per-team reputation sync (M6) |

### 6.5 Fallbacks

- **Teams don't appear in the Factions tab, or appear broken** (S11.1 fails):
  1. Add the `hidden` tag so vanilla ignores them.
  2. Show team↔team and team↔NPC relations only in our Team Diplomacy screen, styled like
     vanilla.
- **Lock reason not shown:** the lock still works (V08). The screen text explains it.
- **Injection into `DiplomacyMenu` fails** (S11.6): keep the standalone screen, opened from
  the Multiplayer screen, `/diplo`, and a Multiplayer row in the top-level menu.
- **Faction-wide lock blocks per-team reputation in practice** (S11.2): leave team factions
  **unlocked** and run a watchdog on the authority. On `event_faction_relation_changed`
  for a team↔team or player↔team pair that differs from the matrix, re-apply it. Clients do
  the same locally.

### 6.6 ADR-040 loan enforcement through diplomacy

- When `LoanEnforcement=Diplomacy`, the server keeps a **standing** score per team pair
  (−30..+30, the same scale as X4's UI relation value) and applies:
  - On overdue: standing(borrower→lender) −5, and with N3 also an `add_faction_relation`
    −0.002…−0.01 toward chosen NPC factions (configurable, "creditor guild" flavour).
  - On default: standing −15. When standing ≤ −20, the server auto-issues **Break alliance**
    (Allied→Neutral) or, if `AllowDefaultWar`, schedules **Declare war** with notice.
- The Team Diplomacy screen shows standing and the reason as a logbook line. This replaces
  any separate "enforcement UI".

---

## 7. Spike S11 (in-game session 2, MD + Lua, no DLL needed)

The extension is `x4mp_spike` v2, the same install, log tag and flow as session 1
(`docs/spikes/session-1.md`). It adds a `libraries/diplomacy.xml` diff with one test event
`x4mp_test_treaty` and one test action `x4mp_test_action` (`<factionpair>`, no cost). The
save should ideally have the PHQ with an embassy and at least one agent. Without them, S11.4
logs SKIP.

| ID | Experiment | Procedure | Pass criterion | If it fails |
|---|---|---|---|---|
| S11.1 | Teams in the Diplomacy menu | Activate `x4mp_team_1..3`, `set_faction_identity` names, relations as V08, lock team 1 with `reason='{90444,901}'`. **User:** open Diplomacy → Factions and Relations; screenshot. Repeat with `set_faction_known` true/false and with the `hidden` tag on team 3 | Teams 1–2 listed (section noted), relation colours right, team 1 shows the lock and our reason; team 3 hidden | Fallback §6.5 (hide teams, own screen) |
| S11.2 | Lock semantics | With team 1 locked: (a) `set_faction_relation player→team1`; (b) `set_faction_relation team1→argon`; (c) `add_faction_relation team1 argon`; (d) unlock + set + relock in one action block, value read back the same frame; (e) does `event_faction_relation_changed` fire for (d) (log params); (f) with `player` locked, `add_faction_relation player argon` blocked? (then unlock) | (a)–(c) blocked, (d) applied, (e) fires with readable params; (f) documented | Watchdog instead of locks (§6.5) |
| S11.3 | Diplomacy activation of mod factions | `set_faction_diplomacy_active team2 true`, `…_events_allowed true`; wait 70 s (two vanilla 30 s checks); log `isdiplomacyactive`, `arediplomacyeventsallowed`. **User:** team 2 now outside "Unreceptive"? listed in the interference faction-pair dropdown? Then set both false and confirm vanilla doesn't re-enable | Flags stick both ways; UI reflects them | Accept: teams always "unreceptive" |
| S11.4 | Custom diplomacy content | (a) `x4mp_test_action` appears in Agent Actions (if agents exist); starting it fires our cue on `event_diplomacy_action_operation_started` and `complete_diplomacy_action_operation` ends it cleanly. (b) `create_diplomacy_event_operation event='x4mp_test_treaty' faction=team1 otherfaction=team2 agent=null`; **user** sees it in Diplomatic Events and tries to pick an option | (a) round trip works; (b) event visible. Note whether an option can be chosen without an agent | Approach A/B stay rejected (expected) |
| S11.5 | Client suppression of NPC diplomacy events | Run `Diplomacy_ActivateFactionLibrary` with `EventCapable=false` for every `$DiplomacyFactionTable` entry, then `set_faction_diplomacy_events_allowed false`; wait 70 s; log the flags for argon/teladi/paranid; restore | Flags stay false after vanilla's checks | Client MD patch that guards `ProtocoltNull_GenerateEvent` (diff on `md/diplomacy.xml`) |
| S11.6 | Inject a tab into `DiplomacyMenu` | From our Lua: find `DiplomacyMenu` in `Menus`; wrap `createLeftBar` to add a button (mode `x4mp_teams`) and `createInfoFrame` to draw a one-row table in that mode; also try `require("debug")` upvalue for `config.leftBar` (R1). Log which worked. **User:** open the menu, click the new tab | The tab renders and switching back to vanilla tabs works; no Lua errors in the log | Standalone screen (§6.5) |
| S11.7 | NPC↔NPC change detection | `set_faction_relation argon↔teladi` (+ restore). Log `event_faction_relation_changed` params and whether a `player`-side change also fires `event_player_relation_changed` | Both events fire with faction ids and the value | Poll relations every 10 s on the authority (`x4n::faction::get_relation`) |

**What the user sends back:** the log lines, screenshots for S11.1, S11.3, S11.4(b) and S11.6,
and notes. Expected time is 20–30 minutes.

---

## 8. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Faction-wide lock blocks reputation and team AI consequences | Per-team rep impossible while locked | Unlock window on the authority, or watchdog mode (S11.2) |
| Clients run vanilla diplomacy MD (Protocol Null) | NPC↔NPC relations diverge per node | Suppress on clients (S11.5) + `NpcRelations` replication |
| `menu_diplomacy` injection breaks on patches / with UIX | Lost tab | Standalone screen is the primary path; the tab is optional |
| Vanilla UI shows teams oddly (no banner, "unreceptive", empty licences) | Cosmetic confusion | Banner icon in our factions diff; lock reason text; `hidden` fallback |
| Relation drift from PvP between non-hostile teams (friendly fire) | Matrix and game disagree | Locks (V08) plus `AllowFriendlyFire` gate (SRV §2.13) |
| Treaty spam / griefing (war–peace cycling) | Annoyance, exploit of combat AI | Cooldowns, notice periods, admin override, audit |
| `GetLibrary("factions")` order/filter unknown | Teams not listed | S11.1 decides |
| Web facts unverified (8.00 vs 9.00 changes) | Mis-scoped expectations | Doesn't matter for the design. The 9.00 files are the truth |

## 9. Milestone recommendation

- **M3 (unchanged):** relations from the GUI matrix applied and locked. Use the per-faction
  lock (unlock → set → relock), and never lock `player`.
- **M5:**
  - Live relation changes from leaders, with the existing `RelationChangeRequest` /
    `RelationProposal` and policy `LeadersMutualAlly`.
  - A minimal in-game Team Diplomacy panel (teams, relation, propose/accept/decline) inside
    the Multiplayer screen.
  - Lock reason text so the vanilla Factions tab explains itself.
  - Team factions excluded from vanilla diplomacy events.
- **M6:**
  - Per-team NPC reputation (N3) with the reputation sync.
  - NPC↔NPC relation replication and client suppression of diplomacy events.
  - Policy `Diplomacy` with treaty kinds, notice periods and cooldowns (schema delta).
- **Post-v1:**
  - Trade agreements as economy-scope flags.
  - Standing and ADR-040 `LoanEnforcement=Diplomacy`.
  - Optional tab inside the vanilla Diplomacy menu (if S11.6 passes).
  - Optional authority-run vanilla agent diplomacy.
