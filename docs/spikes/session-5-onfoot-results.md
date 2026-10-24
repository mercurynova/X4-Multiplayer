# Session 5 - on-foot presence spikes (S10)

Spike kit: `mod/spikes/x4mp_spike/` (`md/x4mp_spike_onfoot.xml`, `ui/x4mp_spike_onfoot.lua`), deployed to
`extensions/x4mp_spike/` in the game folder. Driver scripts and log greps live in the session scratch folder; the game
log is `D:\Admin\Documents\Egosoft\X4\183887795\x4mp_dev.log` and is truncated on every X4 start.

This file records what was measured, what failed and which engine facts came out of it. It is a lab notebook: the
design consequences live in `docs/research/on-foot-presence.md`.

**Reconstructed on 2026-10-24** after an editing accident destroyed the original file; the numbers and quotes were
rebuilt from the session log and notes. Sections S10.1, S10.2, S10.3, S10.4, S10.7, S10.11 and S10.12 are complete;
the "Other MD facts learned" list is the part most likely to have lost detail.

---

## S10.1 - room and container keys (answered)

Measured in a station interior while on foot.

- The player's room is a real component with a **stable component id** (`500218`, `500212`, `500543`, `500544` observed
  across the station's rooms), and interiors are containers with ids `500213` and `500164`. Both are reachable from MD
  (`player.room`, `player.room.macro.id`, `player.room.type`, `player.container`).
- **`player.position` does not exist** in MD. The position is `player.entity.position`, the rotation
  `player.entity.rotation.yaw`. 0 hits for `player.position` in 221 vanilla MD files, 2 hits for
  `player.entity.position` (`md/scenario_tutorials.xml:3223`, `md/showcases.xml:939`).
- Position error between the MD view and the Lua FFI view (`C.GetPositionalOffset`) was **0 cm** (`err_player_cm=0`), so
  either side can be used as the authority for a sample.
- Max walking speed measured: **55.865 m/s** (sprint along a long corridor), which is the number the relay's
  `OnFootRangeMetres` validation has to tolerate.
- An actor crosses MD -> Lua as a real **UniverseID**: `raise_lua_event param="$A"` -> `ConvertStringTo64Bit` ->
  `actor_id_received tag=alice raw=1563269 id=1563269 position_readable=true`. So Lua can read
  (`C.GetPositionalOffset`) **and write** (`C.SetPositionalOffset`) an actor's position, which is what makes the
  movement mode C viable.

## S10.2 - room-key determinism (answered: PASS, with decimetre anchors)

Run twice on the same station: once normally (`label=st2`) and once after saving to `save_002` and reloading it
(`label=st2b`). The station is `station_gen_factory_base_01_macro`, `container_seed=2932786549622728197`, owner argon,
**27 rooms** both times.

- `keyset_begin`/`keyset_end` list every room of the station in one call (`find_room object=<container> multiple="true"`),
  so walking the station is not needed for the keyset itself - only for the room-change lines.
- Per room the spike logs `room_macro`, `roomtype`, `kind` (static/dynamic), `dynamic_name`, `walkablemodule`, the anchor
  (`relativeposition.{container}`, in container space, metres) and `walkable`.
- **Raw anchors are not stable**: 7 of 27 keys differed between the two runs by <= 0.5 mm (`0.120055` vs `0.119629`,
  `339.941` vs `339.94`, `50.3117` vs `50.312`). Macros, roomtypes, kinds and the room count were identical.
- **Quantising the anchor to 1 dm makes the key sets identical**: `only in st2: 0`, `only in st2b: 0`,
  `RESULT: identical key sets`. This confirms `RoomKey.anchor_dm` (decimetres) in section 3.3 - the resolver must not
  key on raw metres.
- `roomtype` can be **null** (`control_room_macro`, the dock area macro), so a key cannot rely on roomtype;
  `room_macro` + quantised anchor is the reliable pair.
- Anchors are container-space and can be kilometres from zero (`-2059.785, -1176.006, 0.1658` for the M dock area).
- The station already had a **dynamic** interior (`room_arg_corridor_01_macro`, `kind=dynamic`), which is exactly the
  F3 risk: dynamic rooms may differ between players, so they need the same determinism check on a second PC.
- The parent-macro `chain=` walk produced an empty string on every line - it needs a separate look before the resolver
  depends on it.

**Conclusion:** the room resolver is viable and needs no new spike code. Key = room macro + anchor quantised to
decimetres (+ kind, roomtype when known). Still open: the same comparison on a second PC, and a revisit after the
interiors despawn (> 50 km) rather than a reload.

## S10.3 - spawn an MP character (answered, with three defects found by eye)

What works (all of it logged and reproduced):

- `create_cue_actor name="$A" cue="namespace"` with a `<select>` child creates a real, persistent NPC owned by
  `faction.x4mp_team_1`, placed in the player's room (`room_type=manager`, `same_room_as_player=1`).
- The name (`<name name="'Spike Alice'"/>`), the title override ("X4MP spike") and the faction (MP Team 1) all show up
  correctly in game.
- Entity variables on the created actor are writable and readable after the fact (`marker_var_readback=1`), and
  `append_to_list md.$X4MP_OF_Actors` works, so the spike can find its own actors after a reload.
- A created actor can be talked to: the vanilla conversation opens with our choices.

Three defects the tester reported (verbatim):

> Spike Alice деформированы руки и ноги скорее всего использованы анимации паранидов или что типа того.
> Оба висят в воздухе и не двигаются (только анимация ожидания).
> Оба имеют титул X4MP spike и фракцию MP Team 1

| Symptom | Cause | Fix |
|---|---|---|
| Alice's limbs deformed | created from a bare catalogue `macro="character_argon_male_civilian_01_macro"` - the model is animated for another body type | create from a character definition: `<select .../>` (or `ref=`), never a bare `macro=`. Confirmed by the tester: Bob, created with `<select>`, was correct: "у Spike Bob конечности в порядке" |
| Both floated | the placement `<position>` used the player's **eye** height as y | place on the floor (`y - 1.72 m`, vanilla uses `1.77 - race.height`) or, better, on a room slot |
| Neither moved | `OF_Walk` resolved the actor through an MD table that never held anything (see below), so no walk was issued | bookkeeping is a list + entity variables; movement is position + yaw from the network (S10.4) |

### MD tables are unusable in this mdscript (measured, not guessed)

`set_value name="md.$X" exact="table[]"` runs without an error, but the result is not a usable table:

```
Error in MD cue ... OF_Janitor: Failed to set table[].lit to value 7
Error in MD cue ... OF_Janitor: Failed to set table[].var to value 8
Error in MD cue ... OF_Janitor: Failed to set table[].lit to value 9     (global.$)
Error in MD cue ... OF_Janitor: Property lookup failed: md.$X4MP_OF_ByTag.count
TT step=6 local_lit=null    step=7 md_seeded_seed=null    step=8 md_seeded_lit=null
TT step=9 global_seeded_seed=null    step=10 compkey=0x7fd43 count=null
```

That is: literal keys, variable keys and component keys all fail, for a **local** `$LT`, for `md.$` and for `global.$`,
and `table[$seed = 5]` does not produce a readable table either. Vanilla syntax is not the problem (`exact="table[]"`
appears 594 times, e.g. `md/boarding.xml:1139`, and `set_value name="$Table.{$key}"` e.g. `md/boarding.xml:1195`).

What does work, and is what the spike now uses: **lists** (`append_to_list`, `.count`, `.{i}`, `remove_value`,
`indexof`, `do_for_each`) and **entity variables** (`$A.$x4mp_tag`, `$A.$x4mp_spike`, `$A.$x4mp_temp`,
`$A.$x4mp_spawn`). An actor is found by scanning `md.$X4MP_OF_Actors` and comparing `@$C.$x4mp_tag`.

### Spawn pipeline as it now stands

`create_cue_actor` + `<select tags="tag.crew" race="player.entity.race"/>` -> `find_npc_slot` (`tag.stand`, then
`tag.npc_generic`) -> `add_actor_to_room actor=$A slot=$Slot` -> entity variables. If the room has no free slot, the
fallback is `add_actor_to_room object=$Room` with a room-local position at floor height (`$Base.y - 1.72m`) and
`rotation yaw`.

Measured runs:

| Run | Room | Slots | Placement | Note |
|---|---|---|---|---|
| 1 | manager's office | `tag.stand` 0 -> fallback `any_free` 4 | slot 4 / slot 2 | actors stood **inside each other at the console**, playing the console animation |
| 2 | manager's office | `stand=0 npc_generic=0 service=0 wait=0 control=0 free_total=0` | `position_fallback`, `foot_y=0.002` | on the floor, apart, no work animation; walk impossible |
| 3 | security office | same, all zero | `position_fallback`, `foot_y=-0.047` | same; `<select>` returned a **female** pirate for a male argon player |

Walking itself was proven in run 1: `walk_issued tag=bob target=slot slot_dist_m=5.18848 speed=1.3`,
`moved_from_spawn_m=1.92969 iswalking=1` after 2 s.

**Not every room can host that pipeline.** A staffed office (manager, security) has every slot occupied by vanilla NPCs,
so `free_total=0` and the floor fallback is the only option; a walk demo needs a corridor, bar or lounge with a free
`tag.stand`/`tag.npc_generic` slot. A corridor reported zero slots too, so the walk test ultimately only worked where a
slot happened to be free.

## S10.4 - moving a remote avatar (answered by numbers, three modes)

A mirror actor replays the player's own track with a 3 s delay. Mode A teleports it with `add_actor_to_room` at 5 Hz,
mode B asks `start_actor_walk` to follow at 2 and 4 Hz, mode C writes `C.SetPositionalOffset` every frame.

| Mode | Commands | p50 | p95 | max | Verdict |
|---|---|---|---|---|---|
| A - `of_place` 5 Hz | 188 / 40 s | 0 | 0.879 m | 3.458 m | fallback only, visible stepping |
| B - `start_actor_walk` 2 Hz | 40 / 20 s | 5.961 m | 6.113 m | 6.202 m | unusable: no free slot, actor never moved |
| B - `start_actor_walk` 4 Hz | 76 / 20 s | 6.113 m | 6.113 m | 6.113 m | same |
| C - `SetPositionalOffset` per frame | 1270 / 40 s | 0.047 m | **0.163 m** | 6.113 m (first frame) | **the answer**, ~32 commands/s, no measurable frame cost |

`frame_ms_avg` was 33-35 ms in every mode, i.e. the spike's own overhead was not separable from the game's frame time.

**The mirror floated** in all three modes, because `C.GetPositionalOffset` and `player.entity.position` return **eye**
height (1.72 m above the floor) and the track fed that straight into the actor. Fixed with `EYE_TO_FOOT = 1.72` in the
Lua track recorder (vanilla places NPCs at `y = 1.77 - race.height`, `md/showcases.xml:939`).

### Tester verdict (verbatim, after the run)

> во всех случаях персонаж весел в воздухе в А он телепортировался ща мной в B он весел на месте С он скользил по
> воздуху до меня. Мне не очень понятно зачем мы создаём управление NPC по точкам когда мы мы должны просто привязывать
> координаты игрока и вектор взгляда и дать анимацию ходьбы или бега

All three modes floated because that run still used the Lua from before the `EYE_TO_FOOT` fix; the fix is now deployed.
The verdict also settles the design question: **no NPC pathfinding for remote avatars.** Bind the player's position and
look vector, and only then worry about the locomotion animation. `docs/research/on-foot-presence.md` §3.3 was rewritten
accordingly.

- A teleported actor **slides in the idle pose**. MD has no `set_actor_state` at all, `start_actor_sequence` only plays
  semantic sequences (`type='facepalm01'`, `behavior='stand_terminal_forward'`, `type='busy'`, ...) and the walk/run
  animation is entered only through `start_actor_walk` with a slot target. Spike **mode D** therefore starts one
  `start_actor_walk` (to any slot, the actor is teleported away immediately) and then overrides the position every
  frame - the experiment answers whether the walk animation survives a per-frame `SetPositionalOffset`.
- If it does not, animation has to be driven from the X4Native extension, and v1 ships with the idle pose.

## S10.7 - conversation choices (partly answered, one form failed)

- `add_player_choice` inside a vanilla conversation **works**: the option "Open MP menu" appeared on a spike actor, and
  a plain string `section` works.
- The tester then chose it and reported: "Сними можно поговорить но при выборе пункта MP menu диалог зависает и не даёт
  выйти из него". The choice ran `open_conversation_menu menu="X4MPSpikeOnFootMenu"`, and the conversation deadlocked
  with no way out.
- Cause: the spike menu was registered **without** `conversationMenu = true`, so closing it left the conversation open
  behind an empty UI. Vanilla never closes a conversation menu from MD either: `open_conversation_menu` appears 48 times
  and there is no `close_conversation_menu`.
- Fix applied: the menu is registered with `conversationMenu = true` and closed with
  `Helper.closeMenuAndReturn(menu, ...)` when it was opened from a conversation (`helper.lua:1402` reads the flag,
  `:1794` is `closeMenuAndReturn`). **To be re-tested** - this is the remaining half of S10.7.

## S10.11 - create the MP lounge (answered: works, one macro name was wrong)

Historical: the MP lounge was **dropped** on 2026-10-24 by user decision (see below). Kept because the engine facts are
useful for any future dynamic interior.

```
lounge_create_begin variant=bar owner=argon canhavedynamicinterior=1
  corridor_macro=room_arg_corridor_06_macro corridor_race=argon bar_macro=room_gen_bar_01_macro
  doors=1 first_door=<macroslot:room_arg_corridor_06_macro,con_room_01> rooms_before=66 seed=4711
PASS lounge_created variant=bar interior=Multiplayer Lounge room_macro=room_gen_bar_01_macro
  room_type=livingroom room_private=1 room_walkable=1 room_x_in_station=212.211 room_y=293.554 room_z=4600.389
  rooms_after=68
PASS lounge_created variant=office room_macro=room_gen_playeroffice_01_macro room_type=livingroom rooms_after=68
FAIL lounge_create_failed variant=venturer interior=0 corridor=0 room=0
```

- `create_dynamic_interior` works: interior + corridor + room, exactly +2 rooms in the station, `room_type=livingroom`,
  `room_private=1`, `room_walkable=1`, and removal returns the station to 66 rooms cleanly.
- The vanilla lookup chain works: `get_room_definition tags="tag.corridor" race=$Obj.trueowner.primaryrace doors=` gave
  `room_arg_corridor_06_macro` plus one door; `tags="tag.bar"` gave `room_gen_bar_01_macro`.
- `room_gen_venturerroom_01_macro` does not exist (or is not a room macro): all three outputs were 0 with **no engine
  error line**, so treat an all-zero result as the failure signal. A mod must fetch the room macro with
  `get_room_definition` instead of hardcoding a name.
- The mojibake station name in the log is a log-encoding artefact, not a defect.

## S10.12 - enter and leave the lounge (menu works; teleport target was wrong)

- The spike menu registers and displays (`menu_registered`, `menu_displayed mode=lounge`), the buttons work, and the
  step waits for the tester.
- Pressing "Go to lounge" teleported the player **under the floor**: "нажал на Go to lounge меня телепортировали под
  текстуры и я падал вниз потом снова телепортировали теперь я застрял в полу коридора".
- Cause: the Lua defaulted the entry point to `0, 0, 0`, and `add_actor_to_room` takes a **room-local** position -
  `0,0,0` is the room origin, inside/below the floor mesh.
- Vanilla never moves the player to the origin. All eight `add_actor_to_room actor="player.entity"` calls in the game
  use a room-local position with **y = 1.808 m (eye height, not the floor)**, a small x/z (`|x| <= 2.1 m`,
  `z` between -1.1 and -4.4 m) and an explicit `<rotation yaw=...>` - `md/inituniverse.xml:441` is the canonical
  `x=0 y=1.808m z=0 yaw=180deg`.
- Fix: the default entry point is now `x=0, y=1.808, z=-3.0`, the move sets `yaw=180deg`, and a failed move (player
  not in the lounge room afterwards) is undone immediately by putting the player back into the room and position saved
  in `md.$X4MP_OF_ReturnRoom/ReturnPos/ReturnYaw`, logging `lounge_go_recovered`. **To be re-tested.**

## Tier 1 (the MP lounge) dropped - user decision

> не нужен нам лаунж игроки должны видеть друг друга где угодно в пределах прогрузки (как NPC)

So S10.11-S10.15 are historical. Avatars must appear wherever both players are loaded, which means the fallback ladder
F0-F4 plus the room resolver (`docs/research/on-foot-presence.md` §3.5/§3.3), and S10.2 becomes the linchpin.

---

## Other MD facts learned

- **A cue with no conditions and no `<delay>` never runs its own action list.** The janitors run because they have a
  cue-level `<delay>`; the root bookkeeping cue had to be reduced to a pure container.
- **A condition on the root cue stopped the whole mdscript from instantiating** (`event_game_loaded`, and also
  `event_cue_signalled cue="md.Setup.Start"`): no child cue fired at all. Child cues only instantiate when their parent
  activates, so a never-firing parent kills the whole tree.
- **An error in one action does not abort the rest of the action list** - steps 7-10 all logged after their failures.
- **An empty list is truthy** in MD. Test emptiness with `not @$List.count`.
- **A manager's office has no `tag.stand` slots**; vanilla places the faction representative with `tags="tag.service"`
  (`md/diplomacy.xml:2275`). The slot search therefore goes `tag.stand` -> `tag.npc_generic` -> work slots, and logs
  which stage produced the slot.
- **`tag.service` and `tag.control` are WORK slots**: an actor placed on one plays the work animation (that was the
  console interaction the tester saw). `tag.npc_generic` is vanilla's "put the mission NPC here" tag
  (`md/gm_transport_passengers.xml:2080`, `md/gmc_improve_station_defences.xml:1119`, ...).
- **A slot has no `.position`, and `slot.distanceto.<position>` is a property-lookup failure.** Distance is always
  measured from an **entity** to a slot: `$NPC.distanceto.{$Slot}` (`md/npc_management.xml:327`,
  `md/npc_state_machines.xml:2478`, `md/x4ep1_mentor_subscription.xml:4352`). Placement picks the nearest free slot to
  the player and skips slots already taken by one of our own actors; a walk test picks the **farthest** slot from the
  actor so the movement is guaranteed to cover distance.
- `start_actor_walk` always takes a **slot**: `target="$slot" roomslot="@$pathtable.$roomslot" speed=`
  (`md/npc_state_machines.xml:9923,10005,10027,10183,10432,10442`). There is no walk-to-position form.
- `<select race="player.entity.race" tags="tag.crew"/>` honours **neither race nor gender**: asked argon/male, got
  `character_yaki_female_cau_marine_01_macro`, `character_pirate_female_afr_01_macro`, `character_scavenger_male_asi_01_macro`.
  A curated race+preset look (variant B) therefore needs `ref=` character definitions
  (`md/npc_instantiation.xml:1375,2666,2748,2809`) or an explicit macro - to be verified before W3.
- A created cue actor trips `md.NPC_State_Machines.ENTER_init<inst:...>: Assertion failure: Unknown NPC state case: 0x...
  type: null role: null post null [<name>]` about 0.3 s after creation: one error line per actor per state entry. It did
  not stop S10.3/S10.4 (placed, walked, mirrored) but it may be why avatars only ever play the idle animation. Open item
  for W3: give the actor a state (usecase / `set_actor_state` equivalent) or prove it harmless.
- `File I/O: Failed to verify the file signature ... (error: 14)` is **benign** for every unsigned file in our
  extensions; it is not a load failure.
- Extension MD files are read **at save load**, not at game start: all `extensions\x4mp_spike\md\*.xml` lines carry the
  pre-load timestamp of the `load_game` call, and the post-load clock restarts at 0.
