# Research: on-foot player presence (seeing other players inside stations and ships)

Status: research note and design proposal, 2026-10-01. Nothing here is decided until the lead
takes it into `decisions.md` (ADR-046 draft) and `mod-design.md`. Anything that is not confirmed
from game data, the X4Native SDK or a primary web page is marked **[UNVERIFIED]**. Claims that rest
only on X4Native's reverse-engineering notes (not on game data we hold) are marked **[RE]**.

**Requirement (user, verbatim):** "If two or more players are on the same station/ship they should
be able to see each other's player on that ship in the correct position and even maybe interact
with them."

**Sources and path conventions**
- `xu/` = `C:\Personal\X4 Mult\x4-unpacked\` (extracted game data, build 611726).
  - `xu/libraries/common.xsd` and `xu/libraries/md.xsd` are the MD/AI action schema. Line numbers
    point at the `<xs:element name="...">` line.
  - `xu/libraries/scriptproperties.xml` is the MD property list.
  - `xu/md/*.xml` are the vanilla MD scripts.
- `sdk/` = `mod/third_party/x4native/v9.0.0-611726/sdk/` (vendored X4Native SDK).
- `X4N-WI` = X4Native `docs/rev/WALKABLE_INTERIORS.md` at tag `v9.0.0-611726`:
  https://github.com/eg3r/X4Native/blob/v9.0.0-611726/docs/rev/WALKABLE_INTERIORS.md
  (reverse-engineering notes on build 600626/607977, not vendored).
- `X4N-PE` = X4Native `docs/rev/PLAYER_ENTITY_API.md` at the same tag:
  https://github.com/eg3r/X4Native/blob/v9.0.0-611726/docs/rev/PLAYER_ENTITY_API.md

---

## 1. Summary

| Question | Answer | Confidence |
|---|---|---|
| Can we read the local player's on-foot state (container, room, room-local position, heading)? | **Yes.** `GetPlayerContainerID`, `GetPlayerOccupiedShipID` (0 when walking), MD `player.room` / `player.entity.position`, `GetPositionalOffset(entity, 0)` (room-local), `GetCameraRotation`, and the MD events `event_object_changed_room` (X4Native typed id 81) and `event_entity_transport_finished`. | High (API exists). Exact values still need spike S10.1. |
| Are interiors the same on two machines with the same save? | **Mostly.** Static walkable geometry (dock areas, build-module rooms, capital-ship bridges) comes from macros and construction plans. Dynamic interiors (bar, offices, security, infrastructure, casino, crew quarters, brig) come from `object.seed + roomtype index` through `get_room_definition` / `create_dynamic_interior`. The seed is a *persistent* property, so it is in the save. **But** some rooms exist only if a player-progress or ownership condition holds: the bar needs the black marketeer to be unlocked. Rooms are also created lazily at `attention ≥ nearby` and destroyed on `event_object_interiors_despawning`. Stations or ships spawned *after* the save (built stations, ghost capital ships) get different seeds. | Medium. Same-save determinism is strongly implied by the code but not tested (S10.2). |
| Can we show another player as an actor? | **Yes, with MD.** `create_cue_actor` (with `macro=` = the remote player's character macro, a custom `<name>` and an owner) → `add_actor_to_room object=room` with `<position>`/`<rotation>` in room space → `start_actor_walk target=room` with `<position>` for animated walking to an arbitrary navmesh point (vanilla does exactly this at `xu/md/npc_state_machines.xml:9483`). | Medium-high. Walking at retarget rates of 2–4 Hz is untested (S10.4). |
| Interaction | Name label is free (the actor's name and title override). **"Talk" → a vanilla conversation with our own choices** works through the `customhandler` trait plus `event_conversation_started` / `add_player_choice` / `open_conversation_menu`. Vanilla does the same for diplomats. Emotes: `start_actor_sequence` and facial `set_actor_emotion`, with a small known-good set. | Medium (S10.7). |
| What does the authority need? | **Nothing.** On-foot state is relayed by the server between nodes whose players share a container. The authority is involved only as a regular player. | High (design choice). |

| User idea: an "MP lounge" room where players are rendered | **Feasible without new 3D assets.** Our MD creates a private dynamic interior with *fixed* vanilla corridor and room macros (e.g. `room_gen_bar_01_macro`), a fixed door and a fixed seed, on any station that can hold dynamic interiors. Players get there by an MD teleport from our menu or the Talk menu. Room-local coordinates are identical on every node by construction, and saves without the mod stay loadable, because only vanilla macros are referenced. See §3.6. | Medium-high (S10.11–S10.15). |

**Feasibility: medium-high** for "see each other in the right place in shared rooms, with a
walking animation". **Medium** for exact positions in every room, because some rooms do not
exist identically on both nodes. In those cases we fall back to a nearby NPC slot, or to a
HUD line ("Alice is on this station, in the bar").

**Recommended tiers** (§3.0): **Tier 0**, a presence list/HUD, plus **Tier 1**, the MP lounge
with full rendering and Talk interaction, ship first in M3b. **Tier 2**, general on-foot
presence in any room, follows once S10.2 confirms determinism. All three share one actor
pipeline, so Tier 1 is a strict subset of Tier 2's code.

---

## 2. Findings

### 2.1 Reading the local player's on-foot state

**Is the player walking, and where?**

| Signal | What it gives | Source |
|---|---|---|
| `UniverseID GetPlayerContainerID(void)` | Station or ship the player is inside; 0 in open space. Walks the parent chain for the container class, so it returns the **innermost** container (a ship docked inside a station returns the ship) **[RE]** | `sdk/x4_game_func_list.inc:994`; X4N-PE §2 |
| `UniverseID GetPlayerOccupiedShipID(void)` | Ship whose pilot seat the player sits in; **0 when walking** | `sdk/x4_game_func_list.inc:1007`; `xu/libraries/scriptproperties.xml:2416` (`player.occupiedship`) |
| `bool IsPlayerOccupiedShipDocked(void)`, `bool CanPlayerStandUp(void)` | Seated in a docked ship / can get up | `sdk/x4_game_func_list.inc:1438`, `:85` |
| MD `player.room` | "Player room" (a `room` component) | `xu/libraries/scriptproperties.xml:2420` |
| MD `player.container`, `player.station`, `player.ship`, `player.platform` | Container context, station, ship, and landing pad the player stands on | `xu/libraries/scriptproperties.xml:2413`, `:2419` |
| MD `player.entity` | "Player component" (the actor) | `xu/libraries/scriptproperties.xml:2421` |
| MD `player.spacesuit` | Non-null while on EVA in space (not on foot inside) | `xu/libraries/scriptproperties.xml:2417` |
| Lua global `GetPlayerRoom()` | Room entity (class "room"), or nil. A bare Lua global, not FFI **[RE]**. It does not appear in vanilla Lua, so treat it as unverified | X4N-PE §2 |
| `UniverseID GetEnvironmentObject(void)` | "Current room" cached field, used by the docked menu. X4Native notes it sometimes returns 0 **[RE]** | `sdk/x4_game_func_list.inc:539`; `xu/ui/addons/ego_detailmonitor/menu_docked.lua:1232` |
| room properties `type` (roomtype), `dynamicinterior`, `walkablemodule`, `dockarea`, `buildmodule`, `isprivate`, `iswalkable`, `actors` | Room classification for the room key | `xu/libraries/scriptproperties.xml:1179-1200` |
| entity `walkablemodule`, `dockarea`, `buildmodule`, `floortags` | Which walkable module the actor stands on | `xu/libraries/scriptproperties.xml:1403-1406`, `:1384` |

Rule for "walking": `occupiedship == 0 and spacesuit == null and room != null`. "Seated in a
cockpit" is `occupiedship != 0`. If `occupiedship` is docked and `container` is a station, the
player is sitting in a docked ship.

**Position and rotation inside the room**

- MD `entity.position` / `entity.rotation` are "relative to parent", and the player actor's parent
  is the room. `relativeposition.{$component}` gives coordinates in any other space, such as the
  container (`xu/libraries/scriptproperties.xml:55-59`).
- Native: `UIPosRot GetPositionalOffset(UniverseID positionalid, UniverseID spaceid)` returns the
  room-local position with `spaceid = 0` (relative to the direct parent), or the offset in the
  space of a given component (`sdk/x4_game_func_list.inc:1019`; used in
  `xu/ui/addons/ego_detailmonitor/menu_mapeditor.lua:731`). X4N-PE §3 says to use
  `GetPlayerID()` (the actor, parented to the room) for room-local walking coordinates.
  `GetPlayerObjectID()` returns the positional "avatar" entity **[RE]**. Which of the two gives the
  body transform is exactly what S10.1 measures. `docs/requirements.md` PIT-011 already notes that
  `GetPlayerObjectID` is the character, not the ship, when walking.
- Facing: the body yaw from the actor rotation, and the camera look direction from
  `Rotation GetCameraRotation(void)` (`sdk/x4_game_func_list.inc:315`) **[UNVERIFIED which one
  matches the visible body heading]**.
- Cost: reading positions is a direct component-table lookup, under 0.1 ms for 200 entities
  **[RE]** (X4N-PE §4). Our per-frame on-foot read is one entity, so it is negligible.

**Camera / first person**

The walk state is a `FirstPersonController`, with the input states `INPUT_STATE_FP_WALK` and the
MD lock actions `lock_firstperson_walk_input` / `..._look_input` (`xu/libraries/md.xsd:3756`,
`:3778`; X4N-WI §5). We do not need the camera mode itself. "Walking" from §2.1 above is enough.
In the cockpit, `SetPlayerCameraCockpitView` and similar exist (`sdk/x4_game_func_list.inc:1897`),
but they are not needed.

**Events**

| Event | Use | Source |
|---|---|---|
| `event_object_changed_room` (object, `param` = new room, `param2` = previous room). X4Native typed id **81**, `ChangedRoomData{new_room, previous_room}` | Immediate send on room change, without polling | `xu/libraries/common.xsd:13255`; `sdk/x4_md_events.h:776-800` |
| `event_entity_entered` / `event_entity_left` (space = room, zone, or the ship/station) | Entering or leaving a container | `xu/libraries/common.xsd:14917`, `:14936`; `sdk/x4_md_events.h:2351`, `:2381` |
| `event_entity_transport_finished` (entity, `param` = previous room) | Transporter or elevator transition finished | `xu/libraries/common.xsd:14955` |
| `event_player_started_control` / `event_player_stopped_control` | Sat down in / got up from the pilot seat | `xu/libraries/common.xsd:15990`, `:16001` |
| `event_player_changed_activity` | Activity enum (scan, travel, …), **not** walking. Not useful here | `xu/libraries/common.xsd:15265` |
| `event_object_interiors_despawning` (object = controllable) | Local dynamic interiors are about to be torn down | `xu/libraries/common.xsd:17268`; `sdk/x4_md_events.h:3102` |

Plan: capture state natively every frame (reads only), send on change, and use the typed
`changed_room` hook for the immediate send. A 250 ms poll of the container and room is the
fallback if the hook doesn't fire (same belt-and-braces pattern as sector entry,
`docs/mod-design.md` §4.3).

### 2.2 Interior identity across instances

**What interiors are made of**
- All walkable geometry sits under a `WalkableModule`, which is a station module or a capital-ship
  module. The hierarchy is Container → WalkableModule → Room → actors **[RE]** (X4N-WI §1-2).
  Class ids: `room` = 84 and `walkablemodule` = 120 in build 611726 (`sdk/x4_game_class_ids.inc:106`,
  `:142`; X4N-WI's 83/118 are from an older build).
- **Static rooms** come from the macros of the container and its modules. Examples are dock areas,
  landing pads, build-module rooms, the control room and capital-ship bridges or dock areas. A
  station's modules come from its construction plan, which is in the save. Given the same save,
  the same station has the same modules at the same offsets, so its static rooms are identical by
  construction. We already rely on stations matching (ADR-009).
- **Dynamic interiors** are corridor + room pairs that MD attaches at a `window` connection with
  `create_dynamic_interior` (`xu/libraries/common.xsd:25471`; X4N-WI §14-15).
  `xu/md/npc_instantiation.xml` creates them per station when its attention reaches `nearby`:
  - bar for the black marketeer (`:696`, `:740`)
  - manager office (`:825`, `:859`)
  - security office (`:961`, `:982`)
  - infrastructure office (`:1049`, `:1070`)
  - casino / gambling den (`:1124`)
  - crew quarters and brig on L/XL ships (`:1264`, `:1331`)
  - Mission scripts add more: the agent bar in `npc_agent.xml:914`, and the faction-rep room in
    `npc_factionrepresentative.xml:132`.

**Determinism of dynamic interiors**
- The seed is `this.$Object.seed + lookup.roomtype.list.indexof.{roomtype.bar}`
  (`xu/md/npc_instantiation.xml:731`, and the same pattern at `:848`, `:974`, `:1062`). The
  casino uses `this.$Object.seed`.
- `seed` is documented as "**Persistent** pseudo-random seed" (`xu/libraries/scriptproperties.xml:48`),
  so it is saved with the object.
- The corridor and room macros come from `get_room_definition` filtered by race (= owner's
  `primaryrace`), tags and seed (`:732-737`). The engine picks `match[seed % count]` from the
  RoomDB (X4N-WI §18). The corridor door is picked from `seed % connection_count` when no door is
  given (X4N-WI §14, §16). The room transform is computed from the module's `window` connection.
- **Conclusion:** for an object that exists in the save with the same owner and the same
  extensions, the same room type gets the same corridor macro, room macro, door and transform on
  every machine. Our handshake already enforces identical DLC and extension hashes (ADR-004),
  and that matters because the RoomDB is built from game data. **[UNVERIFIED in game → S10.2]**
- **Most rooms have only one possible macro anyway.** The RoomDB comes from
  `xu/libraries/rooms.xml`, where an id maps to a group with tags and races, and
  `xu/libraries/roomgroups.xml`, where a group maps to macros:
  - Every non-corridor group has exactly one macro. For example, `bar_gen` →
    `room_gen_bar_01_macro` (`roomgroups.xml:60-62`). The DLC diffs map `bar_bor`, `bar_spl` and
    `bar_ter` to the same macro (`xu/extensions/ego_dlc_*/libraries/roomgroups.xml`).
  - Only corridors vary: `corridor_arg` has 6 macros, `corridor_arg_administration` has 3.
  - So when a bar exists on both nodes, it is the **same macro**, and room-local coordinates map
    1:1 even if the seed, the corridor or the attachment point differ. That makes "room-local
    position + room macro" a robust key for room types with one macro, and leaves the seed
    question only for corridors.

**Where determinism breaks**

| Case | Why | Effect |
|---|---|---|
| Room exists only after an unlock or under ownership conditions | Bar: only if `shadyguy.tradesvisible` or there are instantiation requesters (`xu/md/npc_instantiation.xml:712`). Manager office: only with a trade NPC or if player-owned (`:830`). Casino: only for player-owned stations with welfare modules (`:1127`). Mission rooms: only while the mission runs. | Room missing on one node |
| Owner differs between the nodes | `get_room_definition race=owner.primaryrace` | Different macros |
| Object spawned after the save | Stations built during the session, and **all ships on clients, which are ghosts** (`SpawnObjectAtPos2`, `docs/mod-design.md` §4.4), get fresh seeds | Different crew quarters / brig |
| Lazy lifetime | Created on attention change (`:699`), removed on `event_object_interiors_despawning` (`:795`, `:814`) unless `persistent="true"` (`xu/libraries/common.xsd:25471` attributes) | Exists only while a player is near; that is fine for us, because we only render when the local player is in the same container |
| Patch/version | Cue `version` patches may change formulas | Covered by the pinned build (ADR-004) |

**What identifies a room stably.** UniverseIDs are per process and never go on the wire
(`docs/x4-api-notes.md` §2.1). Proposed **room key**:
1. `kind`: Static, Dynamic, Platform, or Unknown.
2. `room_macro`: the room's macro (`component.macro`, `xu/libraries/scriptproperties.xml:30`).
3. `roomtype`: an `X4RoomType` value 0–21 (`sdk/x4_manual_types.h:470`; strings in `sdk/x4n_rooms.h`).
4. `anchor`: the room origin in container space, rounded to 0.1 m (`room.relativeposition.{container}`).
   Two identical modules in one station are told apart by this.
5. For Static rooms, a hash of the chain of macro names from the container down to the room
   (`.parent` walk). This is a sanity check.

Resolution on the receiver: among the rooms of the local container (MD `find_room object=container
multiple=true`, `xu/libraries/common.xsd:27320`), first match by kind, macro and anchor within
0.5 m. If that fails, match by roomtype and macro. If that fails, match by roomtype only, and the
position becomes approximate (fallback F2). If it still fails, the room is unresolved (fallback F3).

### 2.3 Representing another player on foot

**Creating the actor**

| Action | Notes | Source |
|---|---|---|
| `create_cue_actor cue=... name=...` with `<select race/tags/faction>` **or** the `macro=` attribute (createnpc attribute group), plus `<name name="...">`, `<owner exact="...">` and `seed=` (appearance + name) | Creates an actor tied to an MD cue. `remove_cue_actor` removes it ("the actor will be removed unless referenced outside the MD"). An X Rebirth-era forum note says cue actors are not timed out in low attention, unlike platform actors **[UNVERIFIED for X4]** | `xu/libraries/md.xsd:2544`, `:3102`; `xu/libraries/common.xsd:11278` (createnpc group), `:6840` (macro/ref attributes); https://forum.egosoft.com/viewtopic.php?t=384272&start=180 |
| Vanilla examples | Diplomat: create_cue_actor → `set_entity_traits missionactor customhandler subtitlename` → `set_entity_overrides title icon` → placed in a room slot (`xu/md/diplomacy.xml:747-752`). Boarding speaker placed at a waypoint (`xu/md/boarding.xml:316-324`) | |
| `create_npc_template` + `create_npc_from_template` (with `placementobject` = room, `<position>`) | Alternative that adds a person to the object's people list. A forum report says the template actors were awkward to use. **Not recommended**, because it pollutes station crew | `xu/libraries/common.xsd:25869`, `:25758`; https://forum.egosoft.com/viewtopic.php?t=434607 |
| `create_platform_actor room=...` | Platform population; timed out in low attention | `xu/libraries/md.xsd:2838` |
| `CreateNPCFromPerson(NPCSeed, controllable)` (FFI) | Instantiates an existing crew person. Not useful for us | `sdk/x4_game_func_list.inc:135` |

**Appearance.** Character macros live in `xu/libraries/character_macros.xml` (286 macros). Each
defines race and gender (`identification race= female=`) and head/torso models. They include the
player-character macros `character_player_*`, for example `character_player_custom_m_cau_macro`.
The sender reads its own `player.entity.macro`, and the receiver passes it as `macro=` to
`create_cue_actor`. If that macro fails, it uses `<select race="..." tags="tag.crew">` with the
matching gender as the fallback **[UNVERIFIED that player macros instantiate as NPCs → S10.3]**.
Clothing: `<clothing ware=...>` exists in the createnpc group (`xu/libraries/common.xsd:11278`).
The player's clothing theme (`GetPlayerClothingTheme`) could map to it later.

**Placement and movement**

| Primitive | Behaviour | Source |
|---|---|---|
| `add_actor_to_room actor object=$room` + `<position>` (+ `<rotation>`) | Teleports the actor to any position in the room. Vanilla uses it for "low attention" NPCs and as the fallback when pathing fails (`xu/md/npc_state_machines.xml:7154-7166`, `xu/md/npc_instantiation.xml:3025-3033`). The dev script `xu/md/gs_scientist.xml:28` even moves `player.entity` this way | `xu/libraries/common.xsd:18987` |
| `add_actor_to_room actor slot=$slot` | Snaps to an NPC slot and triggers its default animation (sit at the bar, stand at a terminal) | same |
| `start_actor_walk actor target=$room speed=` + `<position value=… space=$room>` + `<rotation>` | **Walks with animation to an arbitrary position** on the navmesh. Vanilla uses it at `xu/md/npc_state_machines.xml:9483-9491`. Documented as working "as long as the player can watch it", which suits us. `stop_actor_walk` stops; `event_npc_walk_finished` fires on arrival | `xu/libraries/common.xsd:33137`, `:33181`, `:12877` |
| `check_walk_path` | Pre-checks that a path exists | `xu/libraries/common.xsd:31512` |
| Entity properties `walkspeed`, `runspeed`, `slowwalkspeed`, `iswalking` | Pick walk or run by remote speed | `xu/libraries/scriptproperties.xml:1385-1388` |
| `SetPositionalOffset(UniverseID, UIPosRot)` (FFI/native) | Possible per-frame smooth placement relative to the parent. Unknown whether it works on actors or fights the walk controller **[UNVERIFIED → S10.4 mode C]** | `sdk/x4_game_func_list.inc:1912` |
| `set_actor_lookat component`, `clear_actor_lookat` | Head and eyes track a component, such as the local player | `xu/libraries/common.xsd:33271` |
| `start_actor_sequence type behavior` | Animation sequences. Vanilla types include `idle`, `conversation`, `facepalm01`, `turnleft90`, `turnright90`, `turnright180`, `sitdown`, `standup` and `busy` (grep of `xu/md`) | `xu/libraries/common.xsd:33192` |
| `set_actor_emotion` | Facial emotes. `emotedb.xml` says only angry, smile, sad and fear reliably work | `xu/libraries/common.xsd:33240`; `xu/libraries/emotedb.xml:3-4` |
| `set_actor_current_chair` / `animate_chair` | Seat the actor in a chair slot | `xu/libraries/common.xsd:33106`, `:18746` |

Elevators: NPCs ride them through `ElevatorManager` in `xu/md/npc_state_machines.xml:10082-10101`.
Transporters are teleports between rooms (`GetValidTransporterTargets2`, `GetRoomForTransporter`,
`sdk/x4_game_func_list.inc:1233`, `:1058`; X4N-WI §6). For a remote player, both reduce to "the
room key changed", and we re-place the actor in the new room.

**Name label.** The actor's name comes from `<name name="Alice"/>`. Use
`set_entity_overrides title="'X4MP · Team 2'" icon=...` (`xu/libraries/common.xsd:35956`) for a
title. X4 shows the name and title when the player looks at an NPC. A floating 3D label above an
actor is not available from MD or Lua **[UNVERIFIED]**. The HUD player list (`mod-design` §7.7)
covers distance and room.

**Cost per actor.** Stations already show dozens of animated NPCs, and we add at most 7. Expected
cost is well under 1 ms **[UNVERIFIED → S10.8]**. Movement goes Lua → MD
(`AddUITriggeredEvent`), which costs about 8.5 ms latency per round trip (V14, session 1). Batch
every actor's updates into one event per tick, and limit walk retargets to at most 4 Hz.

**Save pollution.** Cue actors are MD state, so they are saved. Rules:
1. Strip all MP actors in the same frame-before-save path as ghosts. The S6 save control
   (`docs/mod-design.md` §6.2) owns this.
2. Mark every MP actor three ways: a blackboard variable `$x4mp_player = <player_id>` (entity
   variables are used by vanilla, e.g. `$PlacedSpeaker.$Stay`, `xu/md/boarding.xml:323`; V12's
   failure was on ships and stations, so retest), the owning cue `md.X4MP_OnFoot.Actors`, and the
   title override.
3. On load, a janitor removes any entity in `md.X4MP_OnFoot` cue actors, or any entity with
   `$x4mp_player`.
4. Consider `set_entity_traits temporary="true"` (`xu/libraries/common.xsd:7885` group) **[UNVERIFIED
   whether temporary actors are skipped by the save → S10.8]**.

`tools/savescan` currently checks `[MP] ` object names. Extend it to cue actors or the
`$x4mp_player` variable, rather than prefixing actor names with `[MP]`, because the prefix would
show in the name tag.

### 2.4 Interaction options (cheapest first)

1. **Name tag:** free (§2.3).
2. **Talk → MP conversation menu.** Set `customhandler="true"` on our actor. Vanilla's
   `DefaultComm` then skips it (`xu/md/conversations.xml:9-14`; the trait is documented in
   `xu/libraries/common.xsd:7885`). Our cue reacts to `event_conversation_started actor=$a`
   (`xu/libraries/common.xsd:12690`, X4Native typed id 120). It adds `add_player_choice
   section=... text=...` entries (`:20598`), such as "Message Alice", "Send credits…", "Invite to
   my team" and "Wave". `event_conversation_next_section` (id 118) then dispatches to the native
   side, and `open_conversation_menu menu="X4MP..."` (`:31009`) can open our own Lua menu, the
   way vanilla opens `MapMenu` (`xu/md/conversations.xml:586`). The player starts the conversation
   with the normal on-foot "talk" interaction. The interact menu's `comm` action also goes through
   `Helper.closeMenuForNewConversation(menu, "default", entity, …)`
   (`xu/ui/addons/ego_interactmenu/menu_interactmenu.lua:1973-1986`). Open questions: whether the
   choices render with no NPC voice line, and whether a custom text string works without a
   `{page,line}`. **[UNVERIFIED → S10.7]** kuertee's Extended Conversation Menu shows the
   shared-entry pattern if conflicts appear (https://www.nexusmods.com/x4foundations/mods/382).
3. **Our own interact menu entry:** the vanilla interact menu lists object actions from the engine
   (`menu_interactmenu.lua:6770-6790`). Injecting a Lua action for an NPC target needs UIX or a
   menu wrapper (ADR-043). Talk is cheaper.
4. **Emotes:** the remote player picks an emote from our menu or a chat command (`/wave`). It is
   relayed, and the receiver plays `start_actor_sequence` (body) or `set_actor_emotion` (face).
   The available set is small and needs S10.5 to establish.
5. **Follow / join:** "Go to Alice" can reuse transporter logic later. Later still: teleport the
   local player to Alice's room with `add_actor_to_room actor="player.entity"` +
   `set_player_entity_position` (`xu/libraries/common.xsd:35603`). This is out of v1 scope.
6. **Vanilla conversation with a custom actor: feasible.** Diplomats and the boarding speaker
   prove the pattern. Voice lines are optional, and we use none.

### 2.5 Edge cases

| Case | Handling |
|---|---|
| Remote player seated in the pilot seat (`occupiedship != 0`) | **v1:** no actor. The HUD shows "Alice (cockpit, docked at X)". **Later:** for a capital-ship bridge we share, seat the actor in the pilot chair slot (`sit_pilot` tag, `set_actor_current_chair`). |
| One player in space, one docked | No on-foot rendering. The space player is a normal ship ghost. The docked player appears in the presence roster only. **Inconsistency today:** `mod-design` §4.7 despawns a docked player's ship ghost (`docked_inside`). For players walking on the same station, keep the docked ship ghost visible, parked at its pad. That is a mod-design change. |
| Player inside a ship that is docked at a station | `container` = the ship (innermost). Interest = same innermost container. Also send `outer_container` so that "same station, different docked ship" still shows in the roster. Rendering needs the ship's interior on both nodes. On clients that ship is a ghost or an avatar, see the next row. |
| Walking inside another player's capital ship | On the other node that ship is a **ghost** (fresh seed, inert). Static bridge and dock-area rooms come from the macro, so they should match. Crew quarters and brig won't. Whether the local player can even dock at or walk into a kinematic ghost is unknown. **Defer:** v1 supports shared stations and own-team capital ships parked or docked at a station. It does not support walking on a moving ship. |
| Transporter / elevator | Remote: room key change → snap (teleport) the actor into the new room with a short hide (100–300 ms) and no interpolation (`Teleport` flag). Local: `event_entity_transport_finished` → immediate send. |
| Room exists for one player but not the other | Example: a bar unlocked only for Alice. Resolution fails → F2: same roomtype missing → F3: roster "Alice is in the bar". Optional later (risky): create our own *private, non-persistent* dynamic interior with the vanilla seed formula, so Bob sees the same bar without unlocking the marketeer. It touches gameplay, so it needs a user decision. |
| Interior despawns while the actor is in it | On local `event_object_interiors_despawning` (or the room's `event_object_destroyed`), remove our actors first. Vanilla rescues its own NPCs to the control room (`xu/md/npc_instantiation.xml:779-790`). We just despawn. |
| Remote on EVA (spacesuit) | It is a space object. Treat it as `PlayerState` with the spacesuit macro (M3 ship-ghost path), not on-foot. |
| Hostile-team actor on a station | The actor's owner is the team faction, so the relation colour is right. Watch for station-security reactions **[UNVERIFIED]**. Fallback: owner = station owner, team shown in the title only. |
| Authority | The authority's human is just another participant. The authority universe holds no client actors. NPC reactions to remote on-foot players (police scans, conversations) are out of scope. |

---

## 3. Design proposal

### 3.0 Tiers

| Tier | What players get | Depends on | Complexity |
|---|---|---|---|
| **Tier 0: presence list/HUD** | "Alice is on Argon Trading Station, in the bar", "Bob is in his cockpit, docked at X". Shown in the player list and as a HUD line when sharing a container | `OnFootState` capture (mode, container, roomtype) + `PresenceRoster` | Low. Needs no actors and no room matching |
| **Tier 1: MP lounge** | A dedicated "Multiplayer Lounge" interior on a station. Inside it, everyone sees everyone as actors at exact positions, walking, with Talk interaction | Tier 0 + actor pipeline (§3.3) + lounge creation (§3.6) | Medium. One known room, so no resolver and no fallbacks inside it |
| **Tier 2: general on-foot presence** | Actors in any shared room: dock areas, corridors, bars, offices, capital-ship bridges. Falls back to F1–F3 | Tier 1 + RoomResolver (§2.2) + S10.2 determinism | Medium-high. Room matching, progress-gated rooms, interior lifetime |

**Recommendation:** build Tier 0 and Tier 1 first (M3b). They meet the requirement in a
controlled place ("the players can meet and see each other"), and their risk is fully covered by
spikes on one PC. Turn on Tier 2 room by room as S10.2/S10.9 prove determinism. Static dock
areas and single-macro room types such as bars and offices come first, corridors and ship
interiors last. Tier 2 then reuses everything from Tier 1 and only adds the resolver and the
fallbacks.

### 3.1 Data model

**`OnFootState`** (C→S, Realtime lane; the server stamps `player_id` and relays it as is):

| Field | Type | Notes |
|---|---|---|
| `seq` | uint | Latest-wins |
| `sample_time_us` | ulong | Server-clock estimate (same as `PlayerState`) |
| `mode` | enum `OnFootMode : ubyte` | `None` (not on foot; sent once, then nothing), `Walking`, `Seated` (cockpit/chair), `InTransit` (transporter/elevator), `Conversation`, `Menu` (busy at a terminal/trader) |
| `container_net_id` | uint | Innermost container (station or ship). Stations use matched net_ids (ADR-009); ships use the ghost/avatar net_id |
| `outer_container_net_id` | uint | Station when the container is a docked ship; else 0 |
| `room` | `RoomKey` | See below; null when `mode=Seated` in a cockpit |
| `px, py, pz` | int | **Room-local** position, 1/1024 m (±2,097 km range, more than enough) |
| `cx, cy, cz` | int | Container-local position, 1/64 m, for validation and fallback (rough placement when the room is unresolved) |
| `yaw` | short | Body heading in room space (same quantisation as `EntityState`) |
| `look_pitch` | byte | Optional head pitch, for `lookat` later |
| `anim` | enum `OnFootAnim : ubyte` | `Idle, Walk, Run, Crouch, Jump, Sit, Talk, Emote` |
| `emote_id` | ubyte | Valid with `anim=Emote` |
| `flags` | ubyte | `Teleport` (snap: room change or transporter), `Keyframe` |

**`RoomKey`** (struct, 16 B): `kind:ubyte` (Static/Dynamic/Platform/**Lounge**/Unknown), `roomtype:ubyte`
(X4RoomType, 21 = none), `macro_ref:uint` (string table, `_ref` convention, common.fbs:6),
`anchor_dm:[short x3]` (room origin in container space, decimetres), `path_hash:ushort`
(FNV-1a of the macro chain, truncated).

**Rate.** 10 Hz while moving (above 0.05 m or 5° change), 1 Hz keepalive when idle, and an
immediate send on room, mode or container change. The 20 Hz `PlayerState` is suspended while on
foot (ship is docked). About 40 B per sample, so at most 400 B/s per walking player.

**`PlayerAppearance`** (Control lane, once per change): `character_macro_ref`, `race`,
`is_female`, `clothing_ware_ref`. Sent at join and on change, and relayed to all players.

**`PresenceRoster`** (S→C, Control lane, on change, at most 1 Hz): per player
`{player_id, mode, container_net_id, outer_container_net_id, roomtype, room_name_ref}` for the
HUD/list fallback. It is sent galaxy-wide, because other players are always visible
(Q2 default).

**`PlayerInteraction`** (Control lane, C→S→C): `{to_player, kind (Wave/Emote/Poke/InviteTeam/
OpenTrade), arg}`. The server validates it (rate limit, team policy) and forwards it. Credits and
team invites reuse the existing economy/team messages; this is only the in-world trigger and
notification.

### 3.2 Server relay rules

- The server keeps `last_onfoot[player]`.
- **Interest = same innermost container:** forward `OnFootState` from A to every B whose last
  state has `container_net_id == A.container_net_id`, and B is on foot or seated.
- Forward also when A's previous container equalled B's container. Then B receives
  the leave state (`mode=None` or the new container) and despawns.
- No world-interest or authority involvement. The authority's node gets these messages only when
  its own human shares the container.
- Roster: recompute on any container, mode or roomtype change, and broadcast at most 1 Hz.
- Telemetry: count relayed on-foot messages per player and validation drops. Validation: the
  container must be a known net_id, and the position must be within 2 km of the container.

### 3.3 Client representation (the "MP character")

The mod's native side owns the state. MD does the actor work through one batched
`AddUITriggeredEvent("x4mp","onfoot", json)` per tick (V14 path), and the results come back through
`raise_lua_event`. Per remote player P:

1. **Spawn** when P's container == the local player's container, P is Walking or InTransit, and
   P's room resolves (§2.2).
   - MD `create_cue_actor cue=md.X4MP_OnFoot.Actors macro=<P.macro> seed=<hash(player_key)>`
     with `<name name=P.name>` and `<owner exact=faction.x4mp_team_k>`.
   - `set_entity_traits customhandler=true missionactor=true`.
   - `set_entity_overrides title=…`, and `$x4mp_player = P.id`.
   - `add_actor_to_room object=$room` with position and rotation.
   - Register it in an `ActorRegistry` (the ghost-registry pattern: registered before first use,
     mirrored to the stash).
2. **Move.** Interpolate 150 ms behind, as for ghosts (§4.5 of mod-design).
   - Error > 3 m, room change, or `Teleport` flag → re-place with `add_actor_to_room` (snap).
   - Moving → `start_actor_walk target=$room speed=clamp(v)` with `<position space=$room>` at the
     target point 300–500 ms ahead. Re-issue at ≤ 4 Hz, only when the target moved > 0.5 m.
   - Stopped → `stop_actor_walk` and face the yaw via `<rotation>` (or `turnleft90`/`turnright90`
     sequences).
   - Mode B/C variants come from S10.4.
3. **Despawn** on any of:
   - P's container != local container, or `mode=None` / `Seated`.
   - P disconnected (frozen 5 s, then removed).
   - The local player leaves the container, or the local interior despawns.
   - Remove with `remove_cue_actor`, plus `remove_actor_from_room destroy=true` as backup
     (`xu/libraries/common.xsd:31353`).
4. **Save:** strip before save, re-create after (with the ghosts, S6). Janitor on load.

### 3.4 Interaction scope

- **v1 (M3b):** name and title tag; HUD/roster line; Talk → MP conversation with "Message"
  (opens chat with a whisper prefilled) and "Wave". Wave plays a body sequence on every viewer
  and a notification on the target.
- **Later (M5b, after the economy and teams in game):** "Send credits…" (donate flow, ADR-019/020
  gates), "Invite to team" (team join flow), "Propose trade" (escrow, ADR-022), an emote list,
  "Go to" / follow via transporter, seated pilot-chair representation on shared capital ships,
  and `set_actor_lookat` toward the local player while talking.

### 3.5 Fallbacks (in order)

| Id | Trigger | Behaviour |
|---|---|---|
| F0 | All good | Animated walking to room-local targets |
| F1 | `start_actor_walk` unusable (stutter, attention, pathing) | Snap mode: `add_actor_to_room` with position and rotation at 2–4 Hz, idle animation (S10.4 mode A) |
| F2 | Room unresolved but a room of the same `roomtype` exists locally | Place the actor at the nearest free NPC slot (`find_npc_slot object=$room excludefilled=true`, `xu/libraries/common.xsd:27557`) or waypoint, idle/sit animation, moving only on slot changes. Position is approximate but in the right place type |
| F3 | No matching room locally (e.g. bar not unlocked) | No actor. HUD/roster line: "Alice is on this station, in the bar". Optionally a waypoint marker on the station later |
| F4 | Container not shared or not matched | Roster only (galaxy-wide list) |

The MP lounge (§3.6) is the controlled alternative to F2/F3. When a room can't be matched,
the HUD can offer "Alice is in the bar. Meet in the MP lounge?"

### 3.6 Tier 1: the "MP lounge" (user idea, evaluated)

**Idea:** a dedicated room on stations, added by our extension. Other players are rendered only
inside it.

**Option A: a new room type or new assets.**
- X4 libraries can be extended by extensions. The DLCs add their own `libraries/rooms.xml` and
  `roomgroups.xml` (`xu/extensions/ego_dlc_boron|split|terran/libraries/`), so a mod can add a
  room id/group with its own tags, such as `tag.x4mplounge`, through the standard `<diff>`
  mechanism.
- A *new room macro* with its own geometry needs new 3D assets: an XMF mesh, a navmesh, NPC slot
  and chair connections, a `window`/door connection for the corridor, and lighting. That is a real
  asset-modding project (Egosoft tags guide: `dynamicroom`, `npc`, `sit_*`, `walk_*`,
  `door`; https://wiki.egosoft.com/X4%20Foundations%20Wiki/Modding%20Support/Assets%20Modding/Guides/Tags%20and%20flags/).
- It also makes saves depend on the mod. A save containing the room references our macro, and
  without the extension the room cannot be resolved **[UNVERIFIED how X4 handles a missing room
  macro on load]**.
- **Not recommended for v1.**

**Option B: a new buildable station module with a walkable interior.**
- It would work only on player-built stations, needs a module macro, a ware and blueprint, and a
  construction-plan entry.
- Same asset and save-dependency costs as Option A, plus economy and balance questions.
- **Not recommended.**

**Option C: a mod-created dynamic interior made of vanilla macros. Recommended.**
- MD `create_dynamic_interior` takes a fixed `corridor` macro, `room` macro, `door` (a
  connection name, so no seed choice is needed), `name`, `roomtype`, `seed`, `persistent` and
  `private` (`xu/libraries/common.xsd:25471`; door handling in X4N-WI §15). It works on any object
  with `canhavedynamicinterior`, which requires a `window` connection
  (`xu/libraries/scriptproperties.xml:363`). That is the same requirement vanilla checks for its
  bar and offices.
- Candidate macros:
  - room `room_gen_bar_01_macro` (bar look, chairs and NPC slots, `xu/index/macros.xml:1617`)
  - room `room_gen_playeroffice_01_macro` (`:2303`)
  - room `room_gen_venturerroom_01_macro`, used by Ventures (`:1857`)
  - a fixed corridor such as `room_arg_corridor_04_macro` (`xu/libraries/roomgroups.xml:23-26`)
- Choose the room type so vanilla does not adopt it. `roomtype.livingroom`, or none (21), are
  better than `bar`, because the vanilla bar cue checks `shadyguy.room.type == roomtype.bar`
  (`xu/md/npc_instantiation.xml:705`). Use `private="true"` so vanilla NPC placement does not find
  its slots ("contained NPC slots can only be found by directly querying the room",
  `xu/libraries/scriptproperties.xml:1183`).
- Name: `'Multiplayer Lounge'` (custom text) **[UNVERIFIED whether a plain string works for
  `name`, or whether a `{page,line}` text entry from our `t/` file is needed]**.

**Determinism and position mapping.**
- The room macro is fixed, so **room-local coordinates are identical on every node by
  construction**. Lounge NPC slots and chairs are at the same room-local offsets everywhere.
- The lounge's position on the station (which window, where the corridor attaches) may differ
  between nodes. That doesn't matter, because nothing outside the room is rendered in Tier 1.
- The room key becomes `{kind = Lounge, container_net_id}`. There is no resolver, no fallback
  and no seed dependency.
- Mapping is trivial: send room-local position and yaw, and apply them as-is.

**Where it exists.**
- **On demand on any station** that has `canhavedynamicinterior` and is not Xenon/Kha'ak/ownerless
  (same filter as vanilla, `xu/md/npc_instantiation.xml:703`).
- Each node creates its own copy locally when its player asks to enter the lounge, or when a
  remote player is in that station's lounge and the local player is on the same station.
- Created with `persistent="false"`, so it disappears with the station's interiors in low
  attention, and removed by us when nobody is in it.
- Optional admin setting: limit lounges to chosen "hub" stations (team HQs, a session meeting
  point).
- Ships: crew-quarters dynamic interiors exist on L/XL ships (`:1264`), so a lounge on an
  own-team capital ship is possible later. The ghost-ship caveats from §2.5 apply.

**Getting in and out.**
- **MD teleport (guaranteed):** `add_actor_to_room actor="player.entity" object=$LoungeRoom`
  with a fixed `<position>` and `<rotation>` at a spawn point (the dev script
  `xu/md/gs_scientist.xml:28` does this for the player). Exit goes back to the station's
  transporter room or dock area the same way.
  - Trigger it from our MP menu ("Go to MP lounge"), from a HUD prompt ("Alice is in the lounge,
    join?"), or from the Talk choices of any MP actor.
- **Transporter list:** the transporter menu lists transporter slots of walkable modules and
  rooms reported by `GetValidTransporterTargets2` (`xu/ui/addons/ego_detailmonitor/menu_transporter.lua:144-200`).
  Whether a mod-created dynamic interior appears there is **[UNVERIFIED → S10.12]**. It is
  nice-to-have only.
- **Interact menu entry:** needs UIX or a menu wrapper (ADR-043). Not needed.
- **Walking in through the corridor door** works if the interior is attached like vanilla's.
  The door leads to wherever vanilla's corridors lead **[UNVERIFIED]**.

**Rendering inside.** This is the §3.3 actor pipeline with `RoomKey{kind=Lounge}`:
spawn at the exact room-local position, `start_actor_walk target=$LoungeRoom <position>`, emotes,
and seats via the lounge's chair slots (`add_actor_to_room slot=`, `set_actor_current_chair`).
Talk → MP conversation. Tier 1 doesn't need F1–F3, except F1 snap mode if walking misbehaves.

**Save compatibility.**
- Only vanilla macros are referenced. A save that contains a lounge still loads without
  X4MP: the room is an orphan interior that no vanilla cue removes, but it is harmless
  **[UNVERIFIED → S10.14]**.
- Policy anyway:
  - remove lounges, and MP actors, before every authority checkpoint (S6 strip path)
  - never let a save capture the local player inside a lounge. Client saves are blocked
    already (ADR-023). If the authority's human is in a lounge at checkpoint time, keep the room
    (vanilla-only content) and accept the orphan, or move them to the dock area first. That is a
    UX question, so the default is to keep the room.
  - the load janitor removes any interior named "Multiplayer Lounge" that has no MP session.

**Cost and complexity compared with Tier 2.**

| | Tier 1 lounge | Tier 2 general |
|---|---|---|
| Room identity | Trivial (fixed macro) | Resolver + fallbacks |
| Position mapping | Exact by construction | Exact only where rooms match |
| Determinism risk | None inside the room | Progress-gated rooms, owner race, post-save objects, ghost ships |
| New assets | None | None |
| Save risk | Low (vanilla macros, our own strip) | Low (actors only) |
| Immersion | Lower: players must go to the lounge | Full |
| Extra code beyond Tier 0 | Lounge create/remove cue, teleport in/out, actor pipeline | Actor pipeline + RoomResolver + F1–F3 + interior-lifetime handling |
| Spikes | S10.3/4/7/8 + S10.11–S10.15, all on one PC | + S10.1/2/6/9 and the two-PC S10.10 |

**Risks specific to the lounge:**
- the interior overlaps or collides with vanilla interiors at the same window (vanilla runs
  several per station, so the engine likely handles it **[UNVERIFIED → S10.11]**)
- vanilla scripts reacting to an unknown interior (`npc_slots_validated`)
- a plain-string name
- the transporter list not showing it (teleport covers that).

---

## 4. Protocol and schema impact (description only; `.fbs` files unchanged)

- New player-range messages in `message_ids.fbs`, proposed: `OnFootState = 0x0302` (Realtime;
  the relay uses the same table with a `player_id` field the server stamps, as `ChatMessage` does with
  `from_player`), `PlayerAppearance = 0x0303` (Control), `PresenceRoster = 0x0304` (Control),
  `PlayerInteraction = 0x0305` (Control).
- New types in `world.fbs`: `table OnFootState`, `struct RoomKey`, `enum OnFootMode`,
  `enum OnFootAnim`.
- New capability bit `OnFootPresence` (bit 9 in `common.fbs` `Capability`). The server relays only
  to nodes that negotiated it.
- `PlayerState` is unchanged. Its `Docked` flag (common.fbs:98) keeps meaning "ship docked". While
  on foot the client stops sending `PlayerState` and the last ship sample stands.
- Protocol doc: a new §"On-foot presence" with quantisation and relay rules; an update to the
  bandwidth table (+≤ 0.4 kB/s per walking player).
- Golden vectors for `RoomKey` packing if it is ever hand-packed. As a FlatBuffers struct it is not
  hand-packed (ADR-005).

## 5. Mod-design impact

- §4.7: the "on foot or docked → hide ghost (`docked_inside`)" rule gets an exception. While the
  local player shares the station, keep the docked ship visible at its pad, or add an EntitySpawn
  for docked player ships at their dock position **[needs design]**.
- New §4.11 "On-foot presence":
  - `OnFootCapture` (native, per frame: reads `GetPlayerContainerID`, `GetPlayerOccupiedShipID`,
    room via MD/Lua query on change, `GetPositionalOffset`; typed hook id 81)
  - `RoomResolver` (room-key cache per container, built on container entry and invalidated on
    `interiors_despawning` / `changed_room` to a new room id)
  - `ActorRegistry` (stash-mirrored like `GhostRegistry`)
  - `md/x4mp_onfoot.xml` (spawn/move/despawn/conversation cues, batched)
  - `md/x4mp_lounge.xml` (Tier 1: create/remove the lounge interior, teleport the player in and out)
- §6.2 save hygiene: MP actors are stripped and restored with ghosts, and the janitor checks
  `$x4mp_player`.
- §7.7 player list: show mode, container name and room type (roster).
- §7.6 chat: "Message" from the conversation pre-fills a whisper.
- §8.5 self-test: add "on-foot read OK" (container and room resolve while walking).
- §10 risks: add the items in §7 below.
- PIT-011 remains: ship-related code must not use `GetPlayerObjectID` while walking.

## 6. Spike plan: in-game session 2, block S10

Same setup as `docs/spikes/session-1.md`: launch options `-debug all -logfile x4mp_spike.log`,
Protected UI off, and a test save copy. Install the session-2 spike extension
(`mod\spikes\x4mp_spike`, extended with `md/x4mp_spike_onfoot.xml` and Lua helpers; built by a
Sonnet task). Most of S10 needs **no native DLL** and **one PC**. Remote players are simulated by
a **mirror actor** that replays the local player's own recorded on-foot track 3 s late. Log
format: `[X4MP-SPIKE] S10.n PASS|FAIL|INFO|MEASURE key=value…`. Expected time: about 60 minutes.

**Before S10:** use a save where the player owns at least one S/M ship and, if possible, an L/XL
ship (capital-ship tests are optional). Dock at a large NPC station that has a trader corner, and
ideally a bar with the black marketeer unlocked.

| Id | Experiment | Procedure (user steps) | Pass criterion | If it fails |
|---|---|---|---|---|
| **S10.1** | Read on-foot state | Spike logs at 2 Hz: container, `GetPlayerRoom()`, `GetEnvironmentObject()`, occupied ship, MD `player.room` (+`.type`, `.macro`, `.dynamicinterior`, `.walkablemodule`), `player.platform`, `GetPositionalOffset(GetPlayerID(),0)` and `(GetPlayerObjectID(),0)`, MD `player.entity.position/rotation`, `GetCameraRotation()`; events changed_room / transport_finished / started/stopped_control. **User:** dock; get up; walk the pad → elevator → corridor → trader corner → (bar) → transporter; sit back in the pilot seat. Note the times you entered each place. | Container and room identified everywhere. Room-local position moves at walking speed (1–6 m/s) and equals MD `position` within 1 cm. Yaw follows the body. Each room change logs `changed_room` within 1 frame. | If `GetPlayerRoom` fails, use MD `player.room` via the shim. If the native position is wrong, use MD `relativeposition` at 10 Hz |
| **S10.2** | Room-key determinism | On each new room, log the RoomKey candidates: kind, macro, roomtype, anchor (`relativeposition.{container}`), parent macro chain, station `seed`, `dynamicinterior` name, plus `find_room multiple` over the station (all rooms). **User:** visit 2 stations; fly > 50 km away (interiors despawn), come back and revisit; quit, **reload the same save**, revisit both. Optional: repeat on a second PC with the same save. | Identical key sets per station across revisit, reload and (optional) PC. Log which rooms exist in each run. | Key on roomtype+macro only. Accept the F2 approximation more often |
| **S10.3** | Spawn an MP character | **User:** while standing in a corridor, run `/x4mpspike actor` (or the spike's hotkey menu). Spike: `create_cue_actor macro=player.entity.macro` named "Spike Alice", owner `x4mp_team_1`, `add_actor_to_room` 2 m in front. Repeat with `<select race=… tags=tag.crew>`. **User:** look at it and note appearance, name, title. Wait 5 min, walk away into another room and come back. | Actor visible with the player's look (or the fallback crew look). Name and title shown on look. Survives 5 min. Vanilla does not move or remove it. | Fallback selection only; title-only name |
| **S10.4** | Movement modes | Mirror actor replays your track 3 s late, 1 min per mode: **A** teleport `add_actor_to_room` 5 Hz; **B** `start_actor_walk target=room <position>` retarget 2 Hz and 4 Hz; **C** `C.SetPositionalOffset` every frame (FFI). **User:** walk an S-curve in the trader corner, then run, then stand still and turn. Rate each mode 1–5 for smoothness, walk animation, foot sliding. | One mode with a walking animation, rated ≥ 4, logged path error < 1 m (p95) and < 2 m (max) | Use the best of A–C. If none is ≥ 3, F2 slot mode only |
| **S10.5** | Facing and emotes | Spike turns the actor to 0/90/180/270° (rotation on walk end and on placement, `turnleft90`), plays `idle`, `conversation`, `facepalm01`, `nod01`, `sitdown`/`standup` at a chair slot; facial `smile`/`angry`; `set_actor_lookat player.entity`. **User:** note what visibly happens. | Yaw within ±15°. ≥ 2 body gestures and ≥ 1 face emote work. Look-at works | Wave = notification only |
| **S10.6** | Transitions and teardown | Spike moves the actor corridor ↔ trader corner ↔ dock area via teleport. **User:** use a transporter while the mirror follows; then undock and fly away (> 50 km) with the actor present; then dock at an L/XL ship (if available) and repeat S10.3 on its bridge. | Re-placement in new rooms works. On `interiors_despawning` our cleanup cue fires and logs 0 leftover actors. Bridge placement works or is clearly reported | Despawn on any room change outside a known set. Capital ships out of scope |
| **S10.7** | Conversation | Actor with `customhandler=true`. **User:** walk up and talk to it. Spike adds 3 choices ("Message", "Wave", "Open MP menu") with no NPC lines. Third choice → `open_conversation_menu` with a spike Lua menu. | Choices render. Each choice logs `next_section` with its param. Menu opens and returns. Vanilla default comm does not appear | NPC line from a generic page. Else our own Lua menu bound to a key while looking at the actor |
| **S10.8** | Cost and save hygiene | Spike spawns 8 actors in the bar or trader corner walking to random targets every 3 s. Log frame-time delta (native QPC if the DLL is present, else Lua) against 0 actors. **User:** save (spike strips actors first in run 1, not in run 2), reload each save. Also test `set_entity_traits temporary=true`. | < 0.5 ms/frame for 8 walking actors. Run 1 reload: 0 MP actors. Run 2: actors found and removed by the janitor. Document whether `temporary` actors are saved | Reduce to F1 snap mode; rely on the janitor |
| **S10.9** | Interior differences | On 5 stations, log the dynamic interiors present and their condition inputs (`shadyguy.tradesvisible`, `tradenpc`, owner, `canhavedynamicinterior`). | Catalogue of "progress-gated" rooms for the F3 list | (info only) |
| **S10.10** (optional, needs M3 build and 2 PCs) | Two-player smoke test | Both players dock at the same station and walk together for 10 min. | Each sees the other's actor in the right room. Position error < 1 m. No leftovers after both leave | Pick fallbacks |
| **S10.11** | Create the MP lounge | Spike (on a docked NPC station): `create_dynamic_interior object=player.station corridor=room_arg_corridor_04_macro room=room_gen_bar_01_macro door=<first corridor connection name> name='Multiplayer Lounge' roomtype=livingroom seed=4711 private=true persistent=false`. Repeat with `room_gen_playeroffice_01_macro` and `room_gen_venturerroom_01_macro`. Log interior, corridor and room ids, the room's container-space transform, the vanilla interiors present, and any errors. **User:** nothing yet, just stay docked. | Interior created for at least 1 macro on 2 different stations (incl. one with a vanilla bar present). No error lines. Vanilla interiors are unaffected | Try another window/module (`module=` attribute) or `private=false`. Else Tier 1 = a reused vanilla room (e.g. the bar) |
| **S10.12** | Enter and leave the lounge | Spike menu "Go to lounge": `add_actor_to_room actor=player.entity object=$Lounge` at a fixed spawn point. "Leave": back to the dock area. **User:** use both; also open the station's transporter and note whether "Multiplayer Lounge" is listed; walk out through the lounge door and report where it leads. | Teleport in and out works with no stuck player or black screen. Transporter listing and door behaviour reported | MD teleport only; lock the door if it leads nowhere sensible |
| **S10.13** | Lounge determinism | Log room-local positions of all lounge NPC slots and chairs (`find_npc_slot multiple`) and the room macro. Reload the save and recreate. Optional: second PC. | Room-local slot offsets identical across runs (expected by construction). Container-space anchor logged for information only | (should not fail; if it does, Tier 1 uses slot snapping) |
| **S10.14** | Lounge save safety | Run 1: save with the lounge present and the player outside; run 2: with the player inside. **Disable the spike extension**, load both saves. Then re-enable, load, and confirm the janitor removes the orphan lounge. | Both saves load without the mod with no errors. The orphan room is harmless and the player is not stuck. The janitor cleans up with the mod | Always remove lounges before saves; move the authority player out first |
| **S10.15** | Lounge with actors and talk | Put the S10.4 mirror actor and 3 more actors in the lounge (walking, seated at chair slots), plus the S10.7 conversation. **User:** walk around for 5 min and rate it. | Same pass criteria as S10.4/S10.7, inside the lounge. Seats work | F1 snap mode |

Pass of S10.1–S10.4 + S10.8 makes Tier 2 feasible as designed. S10.11–S10.15 + S10.3/4/8 make
Tier 1 feasible, and they can run first because they don't depend on interior determinism.
S10.7 gates interaction v1. Recommended run order, if time is short: S10.1, S10.3, S10.4,
S10.11, S10.12, S10.14, S10.7, S10.8, then the rest.

## 7. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Dynamic interiors differ between nodes (progress-gated rooms, owner race, post-save objects) | High for some rooms | Wrong or no position | Room-key matching + F2/F3; S10.2/S10.9 quantify it |
| `start_actor_walk` retargeting stutters (start/stop animations) or refuses at low attention | Medium | Choppy movement | Mode A/C; longer look-ahead; at most 4 Hz retarget |
| MD-driven movement adds load or latency (8.5 ms round trip) | Low | Lag | Batch per tick; interpolation delay 150–300 ms is fine for walking |
| Actors leak into saves | Medium | Save pollution (ADR-023) | Strip with ghosts, janitor, savescan rule, S10.8 |
| Vanilla scripts touch our actor (npc_instantiation placement, `eject_npcs`, conversation defaults) | Low-medium | Actor moved or removed | Cue actor without npc state machine; `customhandler`; re-place on drift |
| Player character macros don't instantiate as NPCs | Low-medium | Wrong look | `<select race/tags>` fallback by race and gender |
| Hostile-team actor triggers station security or relation effects | Low | Odd reactions | Owner = station owner, team shown in title |
| Walking on other players' capital ships (ghosts on clients) | High | Not supported in v1 | Defer; roster only |
| Docked-ship ghost hidden (`docked_inside`) while both players walk the pad | Certain under current design | Visual inconsistency | Mod-design §4.7 exception (§5) |
| X4 patch changes RoomDB, room seeds or npc_instantiation | Low (pinned build) | Keys drift | ADR-004; re-run S10.2 per build |
| Mod-created lounge interior conflicts with vanilla interiors or does not attach on some stations (Tier 1) | Low-medium | Lounge unavailable there | S10.11 across stations; alternative macros or module; admin-chosen hub stations |
| Conversation UI requires NPC voice lines | Medium | No talk menu | Generic voice page, or own menu bound to a key |

## 8. Milestone recommendation

- **M3b: on-foot presence, Tiers 0 + 1** (after M3; needs station net_ids, so either after M4's
  manifest matching or with an ADR-009 match-key stopgap for `container_net_id`). Scope:
  - `OnFootState`/`PlayerAppearance`/`PresenceRoster` schema
  - server relay + roster + FakeNode on-foot bot (scripted walk tracks)
  - native capture (Tier 0)
  - lounge create/remove + teleport in/out (Tier 1)
  - `md/x4mp_onfoot.xml` actor spawn/move/despawn (F1 snap fallback)
  - save strip + janitor (actors and lounges)
  - HUD roster line
  - name/title tag
  - Talk → "Message"/"Wave".
  - **Acceptance (2 PCs):**
    - roster correct within 1 s on 3 stations
    - two players meet in the lounge on 2 stations for 20 min, each sees the other walking and
      seated
    - p95 position error < 0.5 m
    - zero MP actors or lounges in the authority's checkpoint or any reloaded save
    - < 0.5 ms/frame mod cost for on-foot.
- **M3c: general on-foot presence, Tier 2** (after M3b, gated on S10.2/S10.9): RoomResolver,
  fallbacks F2/F3, interior-lifetime handling.
  - Enable it per room class:
    1. static dock areas and pads
    2. single-macro dynamic rooms (bar, offices)
    3. corridors
    4. own-team capital ships parked at stations.
  - **Acceptance:** two players walk together on 3 stations for 20 min (pad, corridor, trader
    corner, one dynamic room), and each sees the other in the right room. p95 error < 1 m in
    matched rooms, with F2/F3 used and logged elsewhere.
- **M5b: on-foot interaction** (after M5 economy and teams in game):
  - Send credits, Invite to team, Propose trade from the conversation
  - emote list
  - Go to / follow
  - seated representation on shared capital-ship bridges
  - walking on own-team capital ships parked at stations.
- Not planned: walking on moving ghost ships; NPC reactions to remote on-foot players on the
  authority; forcing progress-gated rooms to exist (needs a user decision).

## 9. Web sources consulted

- Egosoft wiki, Tags and Flags (room/slot/navmesh/transporter tags such as `dynamicroom`,
  `sit_pilot`, `walk_forbidden`, `transporter`):
  https://wiki.egosoft.com/X4%20Foundations%20Wiki/Modding%20Support/Assets%20Modding/Guides/Tags%20and%20flags/
- Community MD event list (`event_object_interiors_despawning`, `event_npc_walk_finished`, …):
  https://gist.github.com/NodusCursorius/56f55f267a5f0f5509b6f46c6a1d3703
- Moving the player into a room with `add_actor_to_room actor="player.entity"` (mod issue):
  https://github.com/radlinsky/x4-gunnery-control/issues/146
- Cue actors vs platform actors in low attention (X Rebirth era, same engine family):
  https://forum.egosoft.com/viewtopic.php?t=384272&start=180
- NPC template actor problems: https://forum.egosoft.com/viewtopic.php?t=434607
- kuertee Extended Conversation Menu: https://www.nexusmods.com/x4foundations/mods/382
- Prior multiplayer work, none of which syncs on-foot players:
  <previous-multiplayer-mod-repo>, https://github.com/carrascodev/x4-mods
- Ventures was asynchronous, with no live presence:
  https://www.pcgamer.com/x4-foundations-goes-online-but-dont-expect-multiplayer/
- X4Native RE notes (walkable interiors, player entity API): see the source list at the top.
