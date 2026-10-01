# Per-team HQ and research (research + design proposal)

Status: research and proposal, 2026-10-01. Pending spike **S12** (roadmap §4.4). ADR draft:
[ADR-048](../decisions.md). Nothing here changes the `.fbs` schema yet.

User requirement (verbatim): *"each faction should be able to have their own HQ and research.
That may be something to look into since that is an important part of the game."*
In this document "faction" means a player **team** (`x4mp_team_k`, ADR-014).

Sources:
- Game files in `x4-unpacked/` (X4 9.00 build 611726). Paths are relative to it unless they
  start with `docs/` or `mod/`. Extra files for this research were extracted with
  `tools/x4cat_extract.py` (all of `md/`, `libraries/`, DLC `md/` + `libraries/`, the detail-monitor
  and interact-menu Lua, and the PHQ station/research-module macros).
- The vendored X4Native SDK (`mod/third_party/x4native/v9.0.0-611726/sdk`).
- Spike session 1 (`docs/spikes/session-1-results.md`).
- Web: the Egosoft wiki pages
  [Blueprints](https://wiki.egosoft.com/X4%20Foundations%20Wiki/Manual%20and%20Guides/Objects%20in%20the%20Game%20Universe/Blueprints/)
  (fetched) and
  [Player Headquarters Plot](https://wiki.egosoft.com/X4%20Foundations%20Wiki/Manual%20and%20Guides/Objects%20in%20the%20Game%20Universe/Plots/Player%20Headquarters%20Plot/)
  (search summary only), and the fandom page
  [A Grand Experiment](https://x4-foundations-wiki.fandom.com/wiki/A_Grand_Experiment) (search
  summary only). Web-only facts are marked **[UNVERIFIED]**.

---

## 0. Summary

- **The PHQ is an ordinary station of a special macro**, `station_pla_headquarters_base_01_macro`
  (build set `headquarters_player`), with a fixed, invulnerable research module
  `landmarks_player_hq_01_research_macro` (`production research="1"`). The engine does not treat it
  as unique: the plot creates it `ownerless`, `civilian` or `player`, and the Terran DLC places a
  `pioneers`-owned copy. `player.headquarters` is whatever `player`-owned HQ-macro station
  exists. The plot never calls `set_faction_headquarters` for `player`; it only calls `set_owner`.
- **Research is a `player` singleton.** `add_research` / `remove_research` (MD) and `AddResearch` /
  `HasResearched` (exports) take no faction, and `ware.research.unlocked` is documented as "researched
  by the player". Research only runs on a **`player`-owned** HQ research module: the UI looks up
  `GetHQs("player")`, and the production events exist only for player-owned modules. The resources
  come from the HQ's own cargo.
- **Research gates local capabilities**, not universe state: teleportation, equipment-mod crafting,
  module-blueprint scanning, HQ warp, SETA, diplomacy agent slots. Most effects happen in the local
  player's UI and actions. That makes **per-team research feasible**: the server owns each team's
  research set, and every node applies its own team's set to its local `player` with
  `add_research`/`remove_research`.
- **The HQ can be per team.** Each team gets one team-owned HQ station (`x4mp_team_k`) on the
  authority. That is the save's PHQ for the inherit team (ADR-033) and a spawned PHQ-macro station
  for every other team. Because research can't run on a non-`player` module, the **server runs the
  research timer**. The authority takes the resources from the team HQ's cargo.
- **Blueprints are `player`-only and add-only.** There is `add_blueprints` / `LearnBlueprint`, but
  no removal in MD or the SDK. Per-team blueprints are therefore enforced **on the server**
  (construction-plan validation), and local blueprint lists may show extras. **Licences are per
  faction** (`add_licence faction=`), so they map to team factions directly.
- **Recommended:** a server-owned **TeamProgression** record (research, blueprints, licences, HQ)
  per team. Apply-on-join reconciles it into the local `player`. A first cut ships in M5, the full
  version in M6, and the vanilla research menu, terraforming and HQ warp come after v1.

---

## 1. How the PHQ works

### 1.1 Acquisition (the HQ plot)

- The HQ plot is `md/x4ep1_mentor_subscription.xml` (9,778 lines; Boso Ta and Dal Busta), cue `Start`
  (`:53`). It only runs in the main galaxy (`:56`). The default HQ sector is Grand Exchange,
  `cluster_01_sector001_macro` (`:66`).
- During the plot the HQ is created **ownerless** (`:2978`, comment "make sure that an ownerless
  station is fine") or **civilian** (`:5196`, Heretic's End variant). It is handed to the player
  with `<set_owner object="$HQ" faction="faction.player" overridenpc="true"/>` (`:3184` "Claiming the
  HQ", `:3541` Terranborn variant), followed by the `SetupHQ` library (`:722`).
- `SetupHQ` restores the construction state, makes the control room and research module
  operational, and creates `player`-owned defence and engineer control entities (`:722-770`).
- Alternate starts:
  - The custom start `x4ep1_gamestart_scientist` starts *at* the HQ
    (`libraries/gamestarts.xml:2109,2131`).
  - Custom-gamestart story states are `story_hq_boso` / `story_hq_dal` (`gamestarts.xml:2689-2696`).
  - For those, `Initialise` looks up a `player`-owned HQ with `find_station_by_true_owner` and
    creates one in Grand Exchange if none exists (`x4ep1_mentor_subscription.xml:655-680`).
  - Terran DLC: `extensions/ego_dlc_terran/md/story_hq_discovery.xml:24-40` uses an HQ in
    `cluster_114_sector001_macro`. It is owned by `faction.pioneers` and spawned by `god.xml`, or
    created there for a custom-start skip.
- Research is unlocked separately by cue `UnlockResearch` (`:625-643`). It sets the flag
  `player.entity.$x4ep1_hq_research_unlocked` (`:631`) and renames the research module.
- The cue state `md.X4Ep1_Mentor_Subscriptions.UnlockResearch` gates later content:
  - diplomacy intro (`md/diplomacy.xml:372`);
  - Paranid plot (`md/story_paranid.xml:955-982`);
  - Boron plot (`extensions/ego_dlc_boron/md/story_boron.xml:2016`);
  - Hatikvah/pirate plot (`extensions/ego_dlc_pirate/md/story_thefan.xml:686`);
  - Unbihexium (`extensions/ego_dlc_mini_02/md/story_unbihexium.xml:388`);
  - terraforming (`md/terraforming.xml:596-610`).
- Debug cues create an HQ directly (`create_station macro=station_pla_headquarters_base_01_macro
  constructionplan='x4ep1_playerheadquarters' owner=faction.player`, `:364-378`). That is the same
  recipe we would use to spawn team HQs.

### 1.2 Is it a unique macro? How does the game know "the player's HQ"?

- Macro: `assets/structures/macros/station_pla_headquarters_base_01_macro.xml`. It is a `station`
  class macro with `build plotsize="10000"`, build set `headquarters_player` and the PHQ icons.
  Construction plans `x4ep1_playerheadquarters` (just the research module, `fixed="1"`) and
  `x4ep1_playerheadquarters_with_dock` are at `libraries/constructionplans.xml:2242-2250`.
- **Not unique at engine level.** Several copies can exist: the Terran `pioneers` HQ
  (`story_hq_discovery.xml:33`), the plot's ownerless or civilian copy, and the debug cues.
- Script properties:
  - `station.isheadquarters`: "true if this station macro is tagged as a headquarters. Normally only
    true for the Player HQ" (`libraries/scriptproperties.xml:867`).
  - `station.isfactionheadquarters` (`:868`).
  - `faction.headquarters`: "Given faction's headquarters station or null" (`:1877`).
  - `player.headquarters`: "Player's headquarters station or null" (`:2427`).
- MD `set_faction_headquarters faction= station= headquarters=` (`libraries/common.xsd:20381`)
  "will fail if the station's owner is not the correct faction". NPC faction logic uses it
  (`md/factionlogic.xml:571,653`). The HQ plot never calls it for `player`.
- Lua/FFI: `GetHQs(result, len, factionid)` and `GetNumHQs(factionid)` (SDK
  `x4_game_func_list.inc:610,820`) are **per faction**. The research, terraforming, diplomacy,
  player-info and interact menus call them with `"player"`
  (`ui/addons/ego_detailmonitor/menu_research.lua:86`, `menu_terraforming.lua:199`,
  `menu_diplomacy.lua:2147,4107`, `menu_playerinfo.lua:1670`,
  `ui/addons/ego_interactmenu/menu_interactmenu.lua:5274`). The diplomacy menu also calls them with
  NPC faction ids (`menu_diplomacy.lua:1642`). `IsHQ(componentid)` is at `:1389`.
- **Inference [to verify, S12.3]:** `player.headquarters` / `GetHQs("player")` returns the
  `player`-owned stations whose macro is tagged as an HQ (the `headquarters_player` set), and
  `faction.headquarters` for NPCs is the `set_faction_headquarters` value. Evidence: ownership
  transfer alone makes `player.headquarters` non-null in the plot (`:3184` is followed by
  `player.headquarters` checks such as `:871`). The research menu also loops over **all** HQs it gets
  back (`menu_research.lua:89-94`), so more than one player HQ looks tolerated.
- X4Native exposes the MD event `on_faction_headquarters_changed`
  (`sdk/x4_md_events.h:2462-2490`).

### 1.3 What hangs off the PHQ

| Feature | How it depends on the PHQ | Evidence |
|---|---|---|
| **Research** | Research module on the HQ, menu via `GetHQs("player")`, resources from HQ storage | §2 |
| **Embassy / diplomacy agents** | Embassy room on the PHQ; agents dock at `player.headquarters`; gift inventory on `player.headquarters.defencenpc` | `md/diplomacy.xml:261-297,510-524,1872`; `docs/research/diplomacy.md` §1.2 |
| **Terraforming** | Unlocked after `UnlockResearch`; the project menu needs the `player` HQ **in the planet's cluster** (`hqismissing`), so the HQ has to be warped there | `md/terraforming.xml:596-626`; `menu_terraforming.lua:198-205,498` |
| **HQ warp (mass teleportation)** | Research `research_warp_hq_01/02`, `research_high_mass_teleportation`; cue `Warp_HQ_To_Destination` moves the HQ and **consumes** `research_warp_hq_02` with `remove_research` | `x4ep1_mentor_subscription.xml:8836-8856`; `wares.xml:9446,9786` |
| **Ventures** | Venture modules are granted to everyone at game start (`add_research research_module_venture` + `add_blueprints`); the venture plot speaks about the HQ, and a debug cue spawns venture modules on it | `md/setup.xml:1114-1123`; `md/story_ventures.xml:127-147` |
| **Docks / build / staff** | Dock modules are added to the HQ by construction (`x4ep1_playerheadquarters_with_dock`); the defence and engineer computers are `player` actors | `constructionplans.xml:2245`; `x4ep1_mentor_subscription.xml:722-770` |
| **Story gates** | Many plots check `UnlockResearch` complete | §1.1 |
| **Profile userdata** | Completing tracked research writes signed profile userdata (`set_userdata storystate`), which unlocks custom-gamestart options across saves | `x4ep1_mentor_subscription.xml:83-111,6078-6087` |

### 1.4 More than one HQ, non-player owners, ownership changes

- **Several HQ-macro stations:** yes (§1.2). Whether `GetHQs("player")` returns two when the player
  owns two is **[to verify, S12.3]**. The menu code iterates over the list, which suggests it does.
- **Non-player owner:** yes. There are pioneers-, ownerless- and civilian-owned copies in vanilla. A
  non-player owner gets **no research** (the production events and UI are `player`-only, §2.1).
  `set_faction_headquarters` can make it that faction's `faction.headquarters`.
- **Ownership change:** the plot hands the HQ over with `set_owner` and then repairs state on load:
  `patch sinceversion=4` runs `SetupHQ` only `if $HQ.isplayerowned` (`:148-160`).
- What happens to in-progress research, the HQ flags and `player.headquarters` when a `player` HQ is
  re-owned *away* is **[to verify, S12.3]**. That is exactly what ADR-033 does today, re-owning
  every `player` asset to the inherit team. `docs/research/diplomacy.md` §5 already notes that this
  leaves `player.headquarters` null on every node.

---

## 2. How research works

### 2.1 Research wares, prerequisites, time and cost

- Research items are wares with `transport="research"` and `tags="research"` (44 in base
  `libraries/wares.xml`, plus DLC: Terran `research_tf_tech`; Pirate `research_condensate_sample*`,
  `research_erlking_core`, `research_module_welfare_2*`; Mini 02 `research_ship_gen_m_corvette_02`;
  Timelines `research_ship_*` ×6).
- Shape (`wares.xml:9731-9739`, `research_teleportation`):
  `<research time="600"><primary><ware ware="advancedelectronics" amount="100"/>…</primary>
  <research><ware ware="research_…"/></research></research>`. `time` is in seconds. `primary` lists
  the resources consumed, and the inner `<research>` lists the precursors. Script properties:
  `ware.isresearchable`, `research.unlocked`, `research.precursors`, `research.requiredprecursors`,
  `research.resources` (`scriptproperties.xml:1928,1971-1974`).
- Mission-only precursors (`tags="missiononly research"`, e.g. `research_seta_pre`,
  `research_mod_*_pre`, `research_module_welfare_1_pre`) are granted by plot missions with
  `add_research`. Examples: `x4ep1_mentor_subscription.xml:7027,7297,7594,7941,8168`, and
  `story_research_welfare_1.xml:2062`.
- Hidden gamestart research: `research_radioreceiver`, `research_sensorbooster`,
  `research_tradeinterface` (`tags="hidden research"`, `wares.xml:9711-9784`, granted by
  `gamestarts.xml:1404-1406`).
- **Where it runs:** the research module is a `production` module with `research="1"`
  (`landmarks_player_hq_01_research_macro.xml`). `StartResearch(wareid, researchmoduleid)` puts the
  research ware in the module's production queue (`menu_research.lua:708`, SDK `:2022`).
  `ClearProductionItems` cancels it (`:719`).
- Progress is the module's `cycleprogress`. In state `waitingforresources`, the menu compares each
  resource with `GetAmountOfWareAvailable(ware, module)`, so **resources come from the HQ's own
  storage** (`menu_research.lua:322-339`).
- The money shown is `GetEstimatedResearchPrice(hq, ware)` (`:485`), which is what the HQ will
  spend buying those resources from its own account (`money`/`productionmoney`, `:291,626-628`).
- A research can only start if all wares in the previous tree column are completed
  (`isResearchAvailable`, `:917-929`) and a research module is free.
- Visibility: the tree only shows wares that are `IsKnownItem("researchables", id)` (`:105`). MD
  reveals them with `add_encyclopedia_entry type="researchables"`
  (`x4ep1_mentor_subscription.xml:35,268`).
- Events (all **player-owned modules only**): `event_player_production_started/finished/cancelled`
  with a `research` flag (`common.xsd:15212-15250`), and `event_player_research_unlocked`
  ("the player has unlocked an item of research", `common.xsd:15253`). X4Native mirrors these in
  `sdk/x4_md_events.h:5168-5229`.

### 2.2 Where completed research is stored, and how to read it

- **One `player` research database.** The menu comment says "ensure any completed research items have
  been added to the player research database" (`menu_research.lua:92`).
- Reads:
  - MD `ware.<id>.research.unlocked`;
  - Lua `C.HasResearched(wareid)` (no faction parameter, `menu_research.lua:118`, SDK `:1283`);
  - `C.CanResearch()` (SDK `:89`, presumably "the HQ research is unlocked" **[to verify, S12.1]**);
  - the HQ plot flag `player.entity.$x4ep1_hq_research_unlocked` and the cue state
    `md.X4Ep1_Mentor_Subscriptions.UnlockResearch.state`;
  - running research: `container.research` / `productionmodule.research` (warelists,
    `scriptproperties.xml:697,1125`).
- There is no per-faction research store. Research is not a blackboard value or a ware flag on a
  faction.

### 2.3 Grant and revoke

- MD `<add_research ware=/>` "Add research for the player" and `<remove_research ware=/>` "Remove
  completed research from the player" (`common.xsd:19956-19976`).
- Vanilla uses both:
  - the Xenon crisis adds and removes `research_xenon_crisis_01/02`
    (`md/crisis_xenon_khaak_combo.xml:219-230,2628`);
  - the HQ warp consumes `research_warp_hq_02` (`x4ep1_mentor_subscription.xml:8856`);
  - diplomacy grants `research_diplomacy_network` and the agent slots (`md/diplomacy.xml:240-242,487,544`);
  - gamestarts grant teleportation (`md/setup_gamestarts.xml:1659`).
- Native: `AddResearch(const char* wareid)` (SDK `x4_game_func_list.inc:41`). No native remove was
  found. Use MD `remove_research`.
- Whether `add_research` fires `event_player_research_unlocked` and whether a revoke rolls back all
  side effects is **[to verify, S12.2]**. A blueprint learned while a research was held stays after
  the revoke, because blueprints can't be removed (§4.3).

### 2.4 What completed research changes

| Research | Effect | Where the effect lives |
|---|---|---|
| `research_teleportation`, `_range_01..03` | Player teleport to own objects; range. Session 1 found `CanTeleportPlayerTo` returns `"research"` without it | Local player (UI + `TeleportPlayerTo`) |
| `research_mod_{engine,shield,ship,weapon}_mk1..3` | Crafting equipment mods (`mod_*` wares list `research_mod_*` as a production precursor, e.g. `wares.xml:8018-8020`) | Local crafting/UI; installed mods then live on the ship |
| `research_module_{build,defence,dock,habitation,production,storage,venture}` | Module wares carry `<research time="10"><research><ware ware="research_module_dock"/>` (e.g. `wares.xml:4050-4054`). With the research done, scanning a module's **data leaks** can yield its blueprint. Shipyard, wharf, equipment-dock and ship blueprints can't be scanned ([wiki Blueprints](https://wiki.egosoft.com/X4%20Foundations%20Wiki/Manual%20and%20Guides/Objects%20in%20the%20Game%20Universe/Blueprints/)) | Local player (scanning gives a blueprint) |
| `research_module_welfare_1/2` | Welfare modules (plot-gated) | Local blueprints |
| `research_warp_hq_01/02`, `research_high_mass_teleportation` | HQ relocation (needed for terraforming); about 20 M Cr of resources **[UNVERIFIED]** ([fandom](https://x4-foundations-wiki.fandom.com/wiki/A_Grand_Experiment)) | **Authority** (it moves a station) |
| `research_seta` | SETA device | Local; SETA is disabled in sessions (Q4) |
| `research_diplomacy_network`, `research_interference_network`, `research_agentslot_01/02` | Diplomacy agents/slots (`player.agents.research.{n}`, `scriptproperties.xml:2433`) | Vanilla agent diplomacy is unavailable in sessions (ADR-047) |
| `research_xenon_crisis_01/02` | Xenon/Kha'ak crisis story | Universe story → ADR-037 global |
| `research_equipment_xenon`, DLC items (`research_erlking_core`, `research_ship_*`, `research_tf_tech`, …) | Blueprints/equipment from DLC plots | Local blueprints; story-driven |

**Per faction or global?** Always global to `player`, on each node separately. The authority's
`player` is the authority human, and each client's `player` is that client.

---

## 3. Mapping to teams

### 3.1 Options

| Option | Description | Verdict |
|---|---|---|
| **A. Per team** | Server holds each team's research/HQ state; every node applies its own team's set to its local `player` (grant on join, keep in sync) | **Recommended.** Matches "each faction … their own HQ and research", matches team-owned assets, and needs only `add_research`/`remove_research` on each node |
| B. Shared | One research set for the whole session (union) | Simple fallback and a session setting (`ResearchScope=Shared`). It is the natural result when there is one team (v1 default population, ADR-037) |
| C. Per player | Each human researches alone | Rejected. The HQ and its resources are team assets (ADR-014), and progress would split inside a team |

### 3.2 HQ ownership model

- **On the authority:** each team has at most one **Team HQ**, a station of macro
  `station_pla_headquarters_base_01_macro` owned by `x4mp_team_k` and fully simulated:
  - **Inherit team:** the save's PHQ. ADR-033 already re-owns it from `player` to
    `x4mp_team_<inherit>`, so it just gets tagged as that team's HQ.
  - **Other teams:** spawned on demand with the debug-cue recipe (`create_station` + plan
    `x4ep1_playerheadquarters`, plus a dock sequence like `:479`), owner `x4mp_team_k`, in a sector
    the admin or leader picks. A plot-size rule keeps HQs apart.
  - Optionally `set_faction_headquarters faction=x4mp_team_k station=$hq`, so `faction.headquarters`
    and `isfactionheadquarters` work for our MD and the diplomacy Factions tab.
  - The HQ's account, trade and resource buying work like any team station (economy ADR-034
    divergence accepted).
- **On clients (all teams):** the Team HQ is a matched static or ghost under `x4mp_team_k`, like
  every team asset (mod-design §11.3).
- **"Looks like my HQ" for the local player:** the vanilla research, terraforming and diplomacy
  menus only consider `GetHQs("player")`, so a team-owned station is **never** the local player's
  HQ. Two ways forward:
  1. **v1 (recommended): our own Team Research panel** inside the Multiplayer screen, in Helper
     style. It reads the tree from `GetWareData(…, "researchprecursors", "resources", …)` like the
     vanilla menu (`menu_research.lua:99-140`), shows status from the server, and sends requests.
     It needs no `player` HQ.
  2. **Later (player-view, mod-design §11.3 M6): re-own the team HQ static to `player` locally**
     on team members' clients only. Then `GetHQs("player")` finds it, and the vanilla research menu
     opens with its module. `StartResearch`/`ClearProductionItems` are intercepted
     (X4Native `hook_before`) and forwarded to the server, never run locally. Local completion
     comes only from the server (`add_research`). Spike S12.4 tests whether this works and what
     side effects follow: notifications, `player.headquarters`-driven MD such as Boso dialogue,
     embassy, terraforming.

### 3.3 Who researches, where resources come from, who runs the timer

- **Who:** the `ResearchPermission` team policy, `Leader` (default) | `Officers` | `Members`.
  Requests go to the server, which checks policy, precursors, free slot and HQ existence.
- **Timer:** the **server** runs it, because the engine can't research on a non-`player` module.
  The duration is the ware's `time` from a `ProgressionCatalog` the authority reports at session
  start, so DLC research is included. The timer runs only while the session phase is Running and
  freezes on admin pause (Q5). One active research per HQ research module, so 1 by default
  (`ResearchSlots`).
- **Resources:** taken from the **Team HQ's cargo on the authority**, with
  `remove_cargo exact=` and its `result` (V13 PASS). The server sends an `HqResourceOrder`. The
  authority prechecks every ware, removes them all, and confirms the actual amounts. If anything is
  short it puts back what it took and replies `Insufficient`, using the same compensate pattern as
  `AssetTransferOrder` (ADR-022).
  - **Default `ResearchResourceMode = ConsumeAtStart`** (all or nothing).
  - Alternative `Trickle` mirrors vanilla `waitingforresources`: it pulls resources as they arrive
    and the timer pauses until all are present. Later.
  - Teammates or traders fill the HQ's cargo the normal way, by trading or delivering to the team
    station.
- **Completion:** the server marks the ware complete, bumps the team's progression version and
  broadcasts it. Each member's node runs `add_research`, and also
  `add_encyclopedia_entry type="researchables"` for the next tree entries so the tree reveals them.
  The authority node does the same if its human is on that team.
- **Cancel:** the server stops the timer. Resources are not refunded by default (vanilla behaviour
  on cancel is **[to verify, S12.2]**).

### 3.4 The HQ plot in a shared session

- The plot is a **save-wide singleton**: one `md.X4Ep1_Mentor_Subscriptions` cue tree, one
  `$HQ`, one Boso Ta (`md.$PersistentCharacters.$BosoTa`). It can't run once per team.
- Under ADR-037 it runs only on the authority, and clients' story MD stays suppressed. In v1
  (one team) that means "together".
- **Recommended default `TeamHqMode = GrantAtStart`:**
  - Every team gets a Team HQ with research unlocked when the session is created, or when a new
    team is created.
  - If the save has a PHQ, the inherit team keeps it and the others get spawned ones.
  - This is fair (no team waits on a plot it can't play) and simple.
- Other modes:
  - `InheritOnly`: only the inherit team gets the save's PHQ, as today.
  - `OnRequest`: a team leader asks for an HQ (admin approval and/or a credit cost) and picks a
    sector.
  - `Off`.
- **If the vanilla plot later hands an HQ to `player` on the authority** (the save hasn't finished
  the plot): the authority re-owns new `player` assets to the authority human's team. That team
  gets the plot HQ as its Team HQ if it has none, otherwise it is kept as an ordinary station.
  This is open question HQ-3. Story progress that follows (`UnlockResearch` complete) is global
  under ADR-037.
- **Is the HQ unique, so teams compete for it?** Not recommended. The engine allows several copies,
  and the requirement says each team has its own.

### 3.5 Interaction with ADR-037

- Research is **per team**: a capability, not a universe change. Universe unlocks (sectors, gates,
  plot flags) stay **global**. This doesn't conflict, but two classes of research are
  story-driven:
  - **Story grants:** mission-only `*_pre` wares, DLC plot research, Xenon crisis. When the
    authority's story MD grants research to its `player`, the authority mod sees it
    (`event_player_research_unlocked` or X4Native) and reports it as `ProgressionReport{source=Story}`.
  - **Catalogue classes** (the server's `ProgressionCatalog.class`):
    - `Team`: the default. Researched at the HQ or granted by a team's story.
    - `Global`: `research_xenon_crisis_*` and anything else tied to shared universe state. Applied to
      every team (ADR-037).
    - `Authority`: `research_warp_hq_*`, `research_high_mass_teleportation`. Their effect is on the
      authority-simulated HQ (post-v1, §4.4).
    - `Disabled`: `research_seta` (Q4), `research_diplomacy_network`, `research_interference_network`,
      `research_agentslot_*` (ADR-047 keeps vanilla agent diplomacy out).
    - `AllTeams`: `research_module_venture` and the hidden gamestart items (granted to everyone,
      `setup.xml:1121`).
- A story grant of class `Team` goes to the team that is "playing" that story. In v1 that is the
  authority human's team, or everyone if there is one team. Per-team story ownership is ADR-037
  follow-up work.

---

## 4. Other per-faction progression

### 4.1 Licences: per faction, natively

- `add_licence` / `remove_licence` take `faction=` (default `player`) (`common.xsd:20467-20520`).
  Faction properties `haslicence.<type>.{$faction}`, `canholdlicence`, `licences`, `heldlicences`
  (`scriptproperties.xml:1856-1861`).
- Events `on_licence_added/lost` (`sdk/x4_md_events.h:3202-3250`) fire for the player.
- **Mapping:**
  - Authority: team factions hold their licences (`add_licence faction=x4mp_team_k`). This matters for
    team ships docking and police reactions (V25).
  - Each client: its local `player` mirrors its team's licences.
  - The server holds the per-team list. Licences are reputation-gated (`minrelation`), so they follow
    the ADR-047 per-team NPC reputation (M6).

### 4.2 Reputation that gates blueprints

- Blueprints are bought from faction representatives and need a non-enemy relation, plus licences
  for some **[UNVERIFIED]** (wiki). With `NpcReputationMode=PerTeam` (ADR-047, M6), the client's
  `player` relation equals its team's NPC relation, so the vanilla purchase gating just works.
- The credit spend goes through the wallet (`CreditDelta`), and the purchase is reported as a
  blueprint gain (§4.3).

### 4.3 Blueprints: `player`-only and add-only

- `add_blueprints wares= macros= object= method=` "Add blueprints to player's blueprint library"
  (`common.xsd:38872`).
- `player.blueprints.{$ware}.*` (`scriptproperties.xml:2467-2483`).
- Events `event_player_blueprint_added` (`common.xsd:16324`) and
  `event_player_collected_blueprint` (`:15516`); X4Native `on_player_blueprint_added`
  (`sdk/x4_md_events.h:4812-4840`) and `on_collected_blueprint` (`:1032`).
- Native `LearnBlueprint(wareid)` (SDK `:1506`) and `GetBlueprints` (`:278`).
- `GetWareBlueprintOwners(wareid)` (`:1243`) lists the factions that sell a ware (static `<owner>`
  lists).
- **There is no remove_blueprints in MD and no native remove.** Consequences:
  - A client that loads the authority's checkpoint inherits the authority human's blueprints. A
    team switch can't take blueprints away.
  - So **per-team blueprints must be enforced on the server**. Every station construction plan or
    build request from a client is checked against `team_blueprints` before the authority builds it
    (M5 station builds, mod-design §5.4, V18). Local blueprint lists may show extras. That is
    cosmetic: the build is refused with a clear message.
  - NPC-faction construction for team factions on the authority presumably doesn't check `player`
    blueprints **[to verify, S12.6]**. If it does, the authority human's `player` blueprints would
    gate other teams' builds, and the authority needs a temporary grant.
- **Sync flow:**
  - A client gains a blueprint (purchase, data-leak scan, mission). Its mod reports
    `ProgressionReport{blueprint, source}`.
  - The server adds it to the team set and pushes it to teammates, who apply it with
    `add_blueprints` / `LearnBlueprint`.

### 4.4 Terraforming, HQ warp, ventures, Timelines

- **Terraforming:**
  - Planet state is per cluster (`cluster.terraforming.*`, `scriptproperties.xml:1290-1299`), which
    is universe state, so global under ADR-037.
  - Projects need the `player` HQ in that cluster (`menu_terraforming.lua:198-205`). After ADR-033
    no node has a `player` HQ, so **terraforming is unavailable in sessions until post-v1**.
  - A later design: projects owned by a team, run on the authority against that team's HQ, one
    active project per planet (teams compete), resources from the team HQ, state replicated as a
    global unlock.
- **HQ warp:** moves an authority-simulated station. Post-v1, with research class `Authority`. A
  team request makes the authority run a warp routine on that team's HQ, which then replicates as a
  normal station move.
- **Ventures:** venture modules are granted to all at start (`setup.xml:1114-1123`). Ventures are an
  online feature of Egosoft's servers, not part of the shared universe. Leave them client-local and
  unsynced, with no team semantics. Their behaviour in a session is **[UNVERIFIED]**.
- **Timelines DLC:**
  - The Timelines scenarios are separate gamestarts/galaxies. The HQ plot and terraforming check
    `xu_ep2_universe_macro`. They are out of scope for sessions.
  - Timelines research that reaches the main galaxy (`research_ship_*`, from
    `extensions/ego_dlc_timelines/md/story_research_abandoned_ships.xml:748`) is ordinary `Team`
    research.

---

## 5. Recommended design: server-owned TeamProgression

### 5.1 Session settings (server, GUI Sessions → Settings → Progression)

| Setting | Values | Default |
|---|---|---|
| `ResearchScope` | `PerTeam` \| `Shared` | `PerTeam` (when there is one team it behaves like `Shared`) |
| `TeamHqMode` | `GrantAtStart` \| `OnRequest` \| `InheritOnly` \| `Off` | `GrantAtStart` |
| `ResearchPermission` (team policy) | `Leader` \| `Officers` \| `Members` | `Leader` |
| `ResearchResourceMode` | `ConsumeAtStart` \| `Trickle` (later) | `ConsumeAtStart` |
| `ResearchSlots` | 1–4 | 1 |
| `BlueprintScope` | `PerTeam` \| `Shared` | `PerTeam` (server-enforced) |
| `InitialProgression` | `FromSave` (authority's `player` research and blueprints go to the inherit team; other teams get the save's `AllTeams` items) \| `FromSaveAllTeams` \| `Empty` | `FromSave` |

### 5.2 Data model (SQLite, server)

- `progression_catalog(session_id, ware_id, kind {Research,Blueprint}, class
  {Team,Global,Authority,Disabled,AllTeams}, time_s, precursors_json, resources_json,
  missiononly, hidden, dlc)`. Reported by the authority at session start, with server overrides for
  `class`.
- `team_hq(session_id, team_id, net_id, macro, sector_macro, origin
  {InheritedPhq, Spawned, PlotHandover}, research_unlocked, faction_hq_set, created_at)`. At most
  one row per team.
- `team_research(session_id, team_id, ware_id, state {Active, Completed}, source
  {Researched, Story, Admin, Inherited, AllTeams, Global}, started_at, completes_at,
  remaining_ms, resources_taken_json, completed_at, by_player)`.
- `team_blueprints(session_id, team_id, ware_id, method, source {Inherited, Purchased, Scanned,
  Mission, Admin}, at, by_player)`.
- `team_licences(session_id, team_id, licence_type, licence_faction, source, at)`.
- `team_progression_version(session_id, team_id, version)`. Monotonic, and bumped on every change.
- Every change is written to `audit_log`. The admin GUI gets a Teams → Progression tab (view, grant,
  revoke, export).

### 5.3 Protocol messages (described only; proposed id block `0x0900` "Progression")

| Message | Direction | Content |
|---|---|---|
| `ProgressionCatalog` | authority → server | Research and blueprint-relevant wares with time, precursors, resources and tags; sent at session start and on DLC/extension change |
| `TeamProgression` | server → node | Snapshot for the node's team: `team_id`, `version`, completed research[], active research[] (`ware`, `ends_at`, `paused`), blueprints[], licences[], `hq` (`net_id`, sector, `research_unlocked`), plus `global[]`/`disabled[]` lists. Sent at join (after `NodeReady`) and on team change |
| `TeamProgressionDelta` | server → team members (and the authority) | `version`, `base_version`, add/remove lists. A gap triggers a snapshot request |
| `ResearchRequest` | client → server | `start` / `cancel`, `ware`, `request_id` |
| `ResearchResult` | server → client | `Accepted` / `Rejected{NoHq, NotPermitted, Precursors, SlotBusy, Insufficient{ware, have, need}, Disabled}` |
| `HqResourceOrder` / `HqResourceConfirm` | server ↔ authority | `order_id`, `hq net_id`, ware amounts, `mode=Consume\|Refund`; the confirm carries the actual amounts or `Insufficient` (compensated) |
| `HqProvision` | server → authority | Spawn or tag a Team HQ (`team_id`, sector, plan) or set `faction.headquarters`; the confirm returns the `net_id` |
| `ProgressionReport` | node → server | Local gains: blueprint (purchase/scan/mission), licence, story-granted research (authority only for `Story`), with `source` and context (`netid`, credit `seq`) |
| `HqRequest` | client (leader) → server | `OnRequest` mode: ask for or relocate an HQ (later) |

Capability flag `TeamProgression`. The `Intent`/`IntentResult` patterns, rate limits and
`ProtocolViolation` rules apply as for economy requests.

### 5.4 Flows

**Session creation (authority):**
1. The authority reports `ProgressionCatalog`, its `player` research and blueprints, and the save's
   HQ (`player.headquarters` *before* the ADR-033 re-own).
2. The server seeds the inherit team (`InitialProgression=FromSave`) and records the PHQ as that
   team's HQ.
3. The ADR-033 re-own runs. The HQ becomes `x4mp_team_<inherit>`, and `HqProvision` sets
   `faction.headquarters`.
4. With `GrantAtStart`, the server sends an `HqProvision` spawn for every other active team, in
   admin-chosen sectors (the default rule picks the team's spawn sector).

**Join / apply-on-join (every node, the authority included):**
1. After `NodeReady`, the node receives `TeamProgression`.
2. The mod enumerates the catalogue's research wares and computes the diff against
   `HasResearched`:
   - `add_research` for missing `Team`/`Global`/`AllTeams` items;
   - `remove_research` for extras that are not `Global`/`AllTeams`;
   - `add_encyclopedia_entry researchables` for completed items and their direct successors;
   - `add_blueprints` for missing team blueprints (extras can't be removed, so they are logged);
   - `add_licence`/`remove_licence` on `player` to match.
3. The mod logs a one-line summary (added/removed counts) and acks the `version`.
4. The diff runs in one MD batch through the actions shim (V14, about 8.5 ms).

**Team change:** a new `TeamProgression` arrives and the same reconcile runs. Blueprints stay
(cosmetic, §4.3).

**Research start → completion:**
1. Panel → `ResearchRequest`.
2. The server validates it → `HqResourceOrder` to the authority → `HqResourceConfirm`.
3. The server stores `Active` with `completes_at` and broadcasts a delta, so teammates' panels show
   progress.
4. When the timer ends: `Completed`, delta → each member's node runs `add_research` and
   `add_encyclopedia_entry` for the successors → a toast.

**Local gains:** blueprint, licence or story research on any node → `ProgressionReport`. The server
classifies it, stores it in the reporter's team (or globally) and fans out a delta. Client reports
of `Story` research are ignored (story runs on the authority, ADR-037).

**Enforcement points (server):**
- station plans and builds (blueprints);
- our "teleport to team asset" feature (teleport range research);
- Team HQ actions (warp, later).

Local state is advisory. The server DB is the truth.

### 5.5 Milestones

- **Session 2:** spike block S12 (§6).
- **M5:**
  - catalogue, data model, `TeamProgression` snapshot and delta;
  - apply-on-join reconcile for research and licences;
  - inherited PHQ tagged as the inherit team's HQ;
  - admin grant/revoke in the GUI;
  - blueprint `ProgressionReport` and server plan validation (with M5 station builds).
  - That alone makes the inherit team's research "theirs" and stops other teams' clients from
    inheriting it.
- **M6:**
  - `GrantAtStart` HQ spawning for every team;
  - Team Research panel with the server timer and `HqResourceOrder`;
  - per-team licences on team factions (with the ADR-047 M6 reputation sync);
  - blueprint fan-out to teammates.
- **Post-v1:**
  - vanilla research menu through a player-view HQ (S12.4);
  - `Trickle` resources;
  - HQ warp;
  - team terraforming;
  - `OnRequest` HQ mode with relocation;
  - embassy/agents per team (if ever).

---

## 6. Spike S12 (in-game session 2, MD + Lua, no DLL needed)

Same flow as session 1 (`docs/spikes/session-1.md`): extension `x4mp_spike` v2, log tag
`[X4MP-SPIKE]`, `-debug all -logfile x4mp_spike.log`, Protected UI off, test save in a new slot.

- **Save A:** a save **with a PHQ and research unlocked**, ideally with some research done and one
  in progress.
- **Save B:** an early save with no PHQ.

Each step is a menu or hotkey action in the spike menu and logs its results. Expected time: 30–45
minutes.

| ID | Experiment | Procedure | Pass criterion | If it fails |
|---|---|---|---|---|
| S12.1 | Read research state | Save A. Log `GetWares("",true,"","")` with `HasResearched` and `IsKnownItem("researchables")` per ware; `CanResearch()`; `GetHQs("player")`; `IsHQ`; MD `player.headquarters`, `$HQ.isheadquarters`, `isfactionheadquarters`, `player.entity.$x4ep1_hq_research_unlocked`, `UnlockResearch.state`; the active research (`$hq.research`, module `cycleprogress`) | All values readable; completed list matches the vanilla research menu (user compares) | Read via MD only (`ware.research.unlocked`) |
| S12.2 | Grant and revoke via MD | Save A (or B). `add_research research_teleportation` then `remove_research`; after each, log `HasResearched`, `CanTeleportPlayerTo(<player-owned ship>)` reason, whether `event_player_research_unlocked` fired, and what the menu shows. Repeat with `research_mod_weapon_mk1` (user checks crafting at a workbench) and `research_module_dock` (user notes whether data-leak scans now drop blueprints). Save, reload, re-read. Start and cancel a research in the vanilla menu, and note whether resources come back | Grant and revoke both take effect immediately and survive save/reload; event behaviour and cancel behaviour documented | Grant-only; reconcile by reload from a fresh checkpoint on team change |
| S12.3 | HQ ownership | Save A. (a) `set_owner $phq x4mp_team_1`: log `player.headquarters`, `GetHQs("player")`, `GetHQs("x4mp_team_1")`, the active research state, the research menu, Boso/plot errors in the log. (b) `set_faction_headquarters x4mp_team_1 $phq`: log `faction.x4mp_team_1.headquarters`, `isfactionheadquarters`, the event. (c) Set the owner back to `player`; does research resume? (d) Save B: `create_station` HQ macro + plan `x4ep1_playerheadquarters` owned by `x4mp_team_2`, and a second one owned by `player`; log `GetHQs("player")` count and the research menu module list | (a) no crash, state documented; (b) works; (c) restores; (d) both spawn, team-owned copy is inert, `GetHQs` counts reported | Inherited PHQ stays `player`-owned on the authority for the inherit team (an exception to ADR-033) |
| S12.4 | Team station as the local player's HQ (player-view) | Save B, a spawned team-2 HQ. Locally `SetComponentOwner(hq,"player")`, open the research menu (user), check the module list and start button. Don't press Start: `C.StartResearch` is an FFI call that Lua can't wrap, so intercepting it needs the X4Native `hook_before` later. Re-own back to team 2 and check for stray notifications or MD errors | Vanilla research menu opens on a locally re-owned team HQ without errors | Own Team Research panel only (the v1 plan anyway) |
| S12.5 | Research on a non-player module | Save B, team-2 HQ (owned by the team): `StartResearch(ware, module)` from Lua; log the module `research` list and progress after 60 s | Documented (expected: no research) | Confirms the server-timer design |
| S12.6 | Blueprint ownership | Log `player.blueprints.{module_arg_dock_m_01}.any.exists`; `add_blueprints` for one module; check that no remove path exists. Then, with `player` lacking the blueprint, have the MD build a module of that ware on a `x4mp_team_1` station (`create_construction_sequence` + build) | `add_blueprints` works; team-faction construction works without `player` blueprints (or the dependency is documented) | Temporary `player` grant on the authority during team builds |
| S12.7 | Licences on team factions | `add_licence faction=x4mp_team_1 licencefaction=argon type=station_gen_basic` (and another type); log `faction.x4mp_team_1.haslicence…`, `heldlicences`; `remove_licence` | Add/remove work on team factions | Licences mirrored only on `player` locally |
| S12.8 | Encyclopedia reveal | Save B: `add_research` + `add_encyclopedia_entry type="researchables"` for a tree root and one successor; user opens the research menu (with an HQ from S12.3d) | Entries appear with the right completed state | Own panel shows the full catalogue |

**What the user sends back:** the log lines, a screenshot of the research menu for S12.1 / S12.4 /
S12.8, and notes for S12.2 (crafting, scan drops) and S12.3.

---

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Local `player` research diverges from the server (console cheats, story MD on clients, missed delta) | Wrong capabilities shown locally | Versioned deltas with gap detection; reconcile on join, on team change and every 5 min; server-side enforcement points (§5.4) |
| Research effects are local UI/features only (crafting, teleport, scan drops) | A cheater can unlock local features | Accepted for co-op. Anything that changes shared state (builds, HQ warp, our teleport) is validated by the server |
| Blueprints can't be removed | Clients inherit the authority human's blueprints from the checkpoint; team switch keeps them | Server plan validation; GUI shows the "team blueprints" list; cosmetic only |
| Authority's save: `player` research = authority human's team; the server DB holds every other team | Restoring the save without the server loses other teams' progress; a joiner inherits the authority team's research | Server DB is authoritative and is backed up with the session (ADR-007/008 checkpoints include the DB rows); reconcile removes extras on join |
| Authority's own `player` research changes the authority's simulation | Probably none: team assets are NPC-like | S12.6; class `Authority` items run only through server requests |
| PHQ re-owned to a team breaks HQ-plot MD (`isplayerowned` patches, Boso dialogue, `player.headquarters` null) | Plot errors on the authority | S12.3; ADR-037 runs the plot on the authority only; offer an `InheritOnly` exception if needed |
| Spawned PHQ copies conflict with vanilla scripts that `find_station … macro=station_pla_headquarters_base_01_macro` (the Terran discovery plot, `Initialise`) | A plot picks up a team HQ | Spawn team HQs only after those cues have run, or use `find_station_by_true_owner faction.player` guards (S12.3d logs what the plot finds) |
| Resource duplication (cargo removed but research not recorded, or the reverse) | Free or lost research | All-or-nothing order with confirm; idempotent `order_id`; refund on server abort |
| Exploit: fake `ProgressionReport` (blueprints, licences) from a modified client | Unearned blueprints | Purchases must match a `CreditDelta`; scans are rate-limited and audited; `TrustClientBlueprintReports` setting (default on, LAN co-op) |
| Exploit: switch teams to collect another team's blueprints | Blueprint leakage | Team switch is admin-only by default (ADR-017); blueprints are server-enforced |
| Profile userdata (`set_userdata storystate`) written on clients | Profile unlocks from MP sessions | Harmless; documented |
| UIX or other mods replace `menu_research` | Player-view path breaks | v1 uses our own panel |

---

## 8. Open questions for the user (recommended defaults)

| # | Question | Recommended default |
|---|---|---|
| HQ-1 | Is research per team or shared by the whole session? | **Per team** (`ResearchScope=PerTeam`); with one team it is shared anyway |
| HQ-2 | Does every team get its own HQ, or is the HQ unique so that teams compete for it? | **One HQ per team** |
| HQ-3 | Does each team play the HQ plot, or is the HQ granted? | **Granted at session start** (`GrantAtStart`). The save's PHQ goes to the inherit team. The vanilla plot runs only on the authority (ADR-037); if it later hands an HQ to the authority's team, that becomes their HQ if they have none |
| HQ-4 | Are blueprints per team? | **Per team, enforced by the server** on builds; local lists may show extras |
| HQ-5 | Who may start research? | **Team leader** (`ResearchPermission=Leader`) |
| HQ-6 | Where do research resources come from? | **The team HQ's cargo**, consumed at start, all or nothing |
| HQ-7 | What does each team start with? | The inherit team gets the save's research and blueprints; other teams get only the `AllTeams` items (venture modules, hidden gamestart research). Option: `FromSaveAllTeams` |
| HQ-8 | Terraforming and HQ warp in v1? | **No** (post-v1, team-owned projects on the authority) |
| HQ-9 | Licences per team? | **Yes**, on team factions and mirrored to `player`, together with ADR-047 M6 reputation |
