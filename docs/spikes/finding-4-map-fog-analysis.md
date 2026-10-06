# Findings 4 and 18: the map flips to fog (M3-27 investigation, 2026-10-06)

Investigation only: no product code changed. Sources: `x4-unpacked/` (X4 9.00 build 611726), read-only strings of the installed `X4.exe` (to name engine concepts, nothing copied), the sitting 1-3 logs and the sitting 3 authority checkpoints in `out/session4/` (local, git-ignored), our mod code. Everything marked **(unverified)** is a hypothesis, not an observation.

## 0. Summary

- The map's explored-area / fog / radar-bubble drawing is **native engine code**. `menu_map.lua` only configures the holomap through C calls; no Lua computes fog. The Lua-visible inputs are per-object flags (`isknown`, `isradarvisible`, `isinliveview`, `isgravidarplayeraccessible`) and per-sector discovered hexes (a "gravidar" system). So the cause cannot be read out of the unpacked data; it has to be found by experiment.
- **The fog data is not what flips.** The authority's checkpoint taken at 12:06 local, inside the glitch window, still holds the player's discovered hexes for the glitch sector, and they had just grown. The known sector/station counts are constant too (6 sectors, 12 stations, sitting 3 saves and the client probe). The flip is at the map's render / live-view layer, driven by something that changes for the sector the avatar is in.
- **Top hypotheses** (ranked in section 3): (H1) the player <-> own-team relation is exactly **+1.0, which is the engine's `self` relation range**, so the engine may treat team ships as the player's own for live view / gravidar access; (H2) the **native re-own** (`SetComponentOwner`) of a live, forced-visible avatar on a team move leaves stale ownership-derived state (Finding 18 was first seen right after the move; sitting 2 had no move and was fine); (H3) our one **periodic write on a parked avatar**, `ActivateObject(false)` every 5 s, toggles a gravidar/active state on a non-player ship.
- **Experiment (section 5):** no-code first (real authority + FakeNode bots, fly to a non-start sector, then team moves and relation changes through the GUI, `/x4mp knowledge` while it flips), then a build with five diag switches and a periodic probe that logs the live-view state per team ship every 2 s, so the user only has to fly and note times.
- **Fix candidates (section 6):** own-team relation 0.99 instead of 1.0; respawn instead of re-own on team moves (or MD `set_owner`); re-assert `ActivateObject(false)` only when the read-back says the ship became active. Each is a one-line config/logic change, but only an in-game run can confirm it.
- Finding 4 (sitting 1) and Finding 18 may be **two different bugs**: sitting 1's "Unknown Sector" is probably vanilla (section 4), and sitting 3's client did not reproduce.

## 1. How the map decides explored / fog / radar bubble

### 1.1 What is in the unpacked data

| Fact | Evidence |
|---|---|
| The map is a native holomap; Lua sets options and calls `ShowUniverseMap2` / `SetMapState` | `ui/addons/ego_detailmonitor/menu_map.lua:998` (decl), `:2601`, `:3368`; render switches `:950-962` |
| The only per-object gates Lua applies: ship/station must be `isknown` and `isradarvisible` to be listed | `menu_map.lua:7551-7563` (`menu.isObjectValid`) |
| "Unknown location" legend entry uses the fog hex icon | `menu_map.lua:1536`; colour `holomap_fogofwar_periphery` in `libraries/colors.xml:906`; `holomap_component_undiscovered` `:835` |
| Satellite radar range is a map option (default on) | `menu_map.lua:1437-1440`, `:5941-5943` (`SetMapRenderSatelliteRadarRange`) |
| "Discovered" is a per-sector hex concept; MD can clear fog or search the nearest undiscovered position | `libraries/common.xsd:40250` (`clear_fog_of_war`, "Does not reveal any objects"), `:27045` (`find_closest_undiscovered_position`, "position in a sector that the player hasn't uncovered yet"); users `md/setup_gamestarts.xml:1660`, `aiscripts/order.move.recon.xml:725` |
| Live view = "visible on the player's gravidar or by any player-owned object"; enter / leave events | `libraries/scriptproperties.xml:193` (`isinliveview`), `libraries/common.xsd:5727`, `:14296`, `:14308` |
| Radar visibility, knowledge, gravidar access are separate flags | `scriptproperties.xml:222` (`isradarvisible`), `:89` (`isknown`: "known to the player"), `:289` (`isgravidarplayeraccessible`: "whether the player has access to this object's gravidar") |
| Gravidar access of a NON-player object is an explicit grant (diplomacy only) | `libraries/common.xsd:40533` (`add_player_gravidar_access_request`); the only users are the diplomacy agent actions `md/diplomacy.xml:3775`, `:4037`, `:5896`, `:6157` (and the matching removes) |
| Relation ranges: **`self` is exactly 1.0**, `ally` 0.5-1.0, `member` 0.1-1.0 | `libraries/factions.xml:6-8` (header comment); our team relation is +1.0 (`mod/extension/x4mp/md/x4mp_teams.xml:8`) |
| `ActivateObject` is the "isactive" switch of an object (vanilla only uses it on deployables / satellites) | `ui/addons/ego_interactmenu/menu_interactmenu.lua:1394-1397`, `libraries/scriptproperties.xml:144` |

### 1.2 What the engine binary names (strings only)

Class and message names in `X4.exe`: `AI::Gravidar` (with `GetClosestUndiscoveredHex`, `InitializeDiscoveredSectorResourceHexes`), `ActiveGravidarUnit`, `GravidarListener`, `UpdateGravidarAccessEvent`, `PlayerGravidarAccessRequestAction`, `FogOfWarChangedEvent`, `MappedBoundsChangedEvent`, `ObjectEnteredLiveViewEvent` / `ObjectLeftLiveViewEvent` / `ObjectChangedStateInLiveViewEvent`, `SetObjectForcedRadarVisibleAction`. One log message: "Discovered area has exceeded valid bounds - resetting it to prevent the map from freezing" (not seen in any of our X4 logs, 0 hits in all six sitting logs).

### 1.3 What the save shows (the discovered data)

`<discovered>` appears **once** per save, under the **player entity** (the `class="player"` component in the cockpit of the player's ship): one `<sector id=...><quadtree depth= x= y=>` per sector (154), `<node state="1"/>` leaves = discovered cells. The map listens for `fogofwarchanged addmapblip mappedboundschanged` on that player component and for `objectentered` on a sector (listener ids in the 11:57 and 12:06 saves). There is no per-faction discovered store in the save.

So the model is: **the player's gravidar units (own ships, stations, satellites, plus any object the player was granted access to) uncover cells; objects in live view are drawn live; the rest of a sector is drawn from the discovered quadtree.** The "one small radar bubble around one ship" of state (b) is the live-view area without the remembered area. Which of the three inputs flips is the open question.

## 2. What our own artefacts say

| Observation | Source | Reading |
|---|---|---|
| Authority checkpoint **12:06 local** (video 12:06:01-12:06:30): discovered quadtree of the glitch sector (`cluster_22_sector001`, the sector holding the avatar and the player) = 284 nodes, depth 8; 11:57: 236 nodes, depth 7. The start sector 760 nodes depth 9; `cluster_04_sector002` 140 nodes. | `out/session4/data/saves/` (decoded offline, see section 7) | The remembered area was **intact and growing** while the map showed fog: the flip is not data loss. |
| `knownto="player"` sectors 4 (plain save), 6 (11:42, 11:57, 12:06); known stations 7, 12, 12 | same saves | Constant: matches the client probe (`sectors_known=6/152`). Known flags are not what flips. |
| The avatar is a normal ship: `owner="x4mp_team_1" knownto="player" known="1" forcedvisible="1"`, has a `<gravidar/>` element, `[MP] Bob` | 12:06 checkpoint | Forced-visible + known + gravidar unit, owned by the player's own team. |
| Timeline (UTC; local = UTC-4): 15:29 `teams: apply seq=1 universe_ready`; **16:01:06 avatar re-owned to team 2** (`SetComponentOwner`), 16:01:15 relations re-applied; 16:04:52 relations re-applied, **16:04:59 re-owned back to team 1**; checkpoint + video 16:06 | `x4mp-lines.txt` of logs-s3a | Glitch "first noticed right after a team move" = the re-own window, ending with the avatar back at relation 1.0. |
| Only one `repaired the parked avatar` line in the whole log (00:44 on Oct 5, an older session) | same | M3-20 holds: a parked avatar gets **no pose writes**. The only recurring write left is `ActivateObject(false)` every 5 s (`avatar_director.cpp:14` `kInertPeriodS`, `maintain()` :685-715), plus read-only validity checks every 1 s. |
| Sitting 2 (authority, 3-7 bot avatars, no team move): map fine, **but only the start sector was looked at**; the start sector's tree reached depth 9 there too (Oct 4 20:44 checkpoint), so tree growth is normal | sitting 2 doc + checkpoints | Sitting 2 does not clear avatars in general; it never had an avatar in a non-start sector or a re-own. |
| Sitting 3 client (takeover, one ghost): map fine, probe constant | sitting 3 doc | The ghosts path (also forced-visible + inert re-assert + team owner) did not reproduce, so a pure "forced-visible team ship" is not sufficient on the client. |

## 3. Hypotheses, ranked

Confidence is about how well each fits ALL observations, not about the mechanism being proven.

**H1 (top, ~30%): relation +1.0 = the engine's `self` range.** `libraries/factions.xml:6` defines `self: 1.0 to 1.0`. We set player <-> own team to exactly +1.0 (`x4mp_teams.xml:8`, `set_faction_relation`). If the engine treats a `self`-related faction's ships like the player's own for the live-view / gravidar-access test (`isinliveview` "by any player-owned object", `isgravidarplayeraccessible`), the avatar's radar is a player gravidar unit: the "small bubble around one ship" would be the avatar's (or the player's) coverage, and every relation/ownership update re-evaluates it (`UpdateGravidarAccessEvent`). Fits: only sectors with team ships; persists parked / focused / after `/reloadui` (state is derived, not UI); the team apply (relation unlock/set/lock) and re-own both touch exactly this. Against: sitting 2 and the sitting 3 client have the same +1.0 and were fine (but see the gaps in section 2). Test: relation 0.99 (ally range) or 0, section 5.

**H2 (~25%): the native re-own of a live avatar leaves stale state.** `SetComponentOwner` on a live, forced-visible, known ship (M3-18, `avatar_director.cpp:600`) changes the owner without MD's `set_owner`. Fits the timing (first noticed after the 16:01 move; sitting 2 had none) and "sectors where the avatar has been" if the stale entry stays registered per sector. Against: the second half of the story needs the highway sector glitch to appear without a re-own there (the avatar was re-owned in Pious Mists II; the highway sector was visited later and showed the same). Test: a fresh session, fly to a non-start sector with the avatar, **no** move; then one move.

**H3 (~15%): `ActivateObject(false)` every 5 s on a non-player ship.** `ActiveGravidarUnit` suggests that "active" is exactly what registers a unit's gravidar; vanilla uses `ActivateObject` to switch satellites on and off. Every parked avatar still gets one write per 5 s. The video was sampled every 2 s, so runs of 2-3 samples per state would match a 5 s period (unverified, the video was not re-timed). Against: ghosts get the same call on the client and did not glitch in sitting 3. Test: a switch that stops the re-assert after the first call.

**H4 (~10%): forced radar visible + `set_known` on a ship that sits in a sector the player is not scanning.** Both are set once at dress (`x4mp_avatars.xml:75-76`, `x4mp_ghosts.xml:41-42`). A forced-visible, known object in a non-live sector forces that sector into "live" drawing. Fits "sector where the avatar is"; the same sitting 3 client ghost counter-argument applies (a ghost is always inside the player's radar there). Test: dress without radar/known.

**H5 (~10%): mapped-bounds / discovered-area overflow.** Engine message and `MappedBoundsChangedEvent` exist; the tree depth grew in the glitch sector (7 -> 8 at 12:06). Against: no log line, the checkpoint tree is valid, depth 9 appeared in sitting 2 without a glitch. Probe: grep the X4 log for "Discovered area" (zero hits so far); the nearest-undiscovered probe in section 5 shows whether MD sees the area as discovered.

**H6 (<5%): our HUD / the takeover.** The HUD cannot know about sectors; M3-16 already fixed the one real HUD-vs-map interaction (blur). The takeover (original ship removal) cannot explain the authority. For the client in sitting 1 it stays a candidate only for Finding 4, not for 18.

**Why sitting 1 (client) and sitting 3 (authority) differ:** not explained by the data. Sitting 1 is the least instrumented run (no probe, no checkpoints from the client, FakeNode authority with 2+ orbiting bots, the first-run relation/team test); sitting 3's client was clean. The honest options: (a) one cause that needs a team ship in the player's own sector plus a relation/ownership update (sitting 1: bot ghosts re-owned and relations re-applied early; sitting 3 authority: avatar re-owned), or (b) two bugs. Section 5 decides it: run the client experiment of sitting 1 again with the new probe.

## 4. Finding 4 specifics: "Unknown Sector" and "?" were probably not knowledge loss

The plain `save_004` knows **4 of 152 sectors and 7 of 1401 stations** (`knownto` count in the save), and the start sector `cluster_04_sector001` has no gates (galaxy dump) so every destination of the start area is a never-visited sector. "To: Unknown Sector" and "?" markers are what vanilla shows for those. The sitting 3 probe (`sectors_known=6/152 ... stations`, constant through every takeover stage, equal to the checkpoint's own counts) confirms nothing was lost. What sitting 1 did show is the fog / bubble picture, which is the same visual as Finding 18. **Action: when re-running, compare against single player at the same spot, with the same neighbour sector.**

## 5. Experiment for the user (one PC, about 30 min, FakeNode bots)

### 5.1 No new code (run first)

Setup: real X4 = **authority**, `tools\session4\start-fake-clients.ps1 -SaveName save_004 -Wingmen 2` (sitting 2 recipe, `docs/in-game-session-4.md`). Bots Wing01/02 join as team 1 and orbit the host.

1. Sit down, bots arrive. Look at the **start sector map** for 30 s (expected: fine, as sitting 2). Type `/x4mp knowledge` once.
2. Fly to a **different sector** (any gate or the highway), bots follow (if they do not, wait for them: the avatar must be in that sector). Open the map there, watch 60 s. Flips? Note the wall-clock time of each flip, or record the screen. While it flips, type `/x4mp knowledge` twice, 10 s apart.
3. GUI Players: move Wing01 to **team 2** (check the Teams page for the current team 1 <-> team 2 relation first and note it). Watch the map in that sector 60 s; `/x4mp knowledge`.
4. GUI Teams: set team 1 <-> team 2 **Allied**, then **Hostile**, then back **Neutral** (30 s each), same sector.
5. Move Wing01 **back to team 1**. 60 s. `/x4mp knowledge`.
6. GUI **Request save now**, quit, `collect-logs.ps1 -Label f18a`. Send the zip, the flip times and where the bubble was (around you, around a bot, around nothing).

| Result | Meaning |
|---|---|
| Flips already at step 2 (no move) | H2 dead. H1 / H3 / H4 stay. |
| Clean at 2, flips from step 3 (neutral team 2) | H2 (re-own) or H1 inverted (a relation change itself); compare 4 and 5 |
| Flips only while Wing01 is team 1 (steps 2, 5), clean on team 2 neutral | **H1** (relation +1.0 / `self`) |
| `knowledge:` counts change when the map flips | knowledge flips: new lead (some code writes `known`), rerun with the probe below |
| Counts constant, map flips | render / live-view layer (H1, H3, H4, H5) |
| Never flips | the cause needs a real player or the second PC: go to the two-PC kit with the new probe |

### 5.2 New diag switches and probes wanted (describe only)

Switches (`x4mp.json` `"diag"`, all default false, log a `diag.<key>` warning like M3-23; authority and client where it applies):

- `team_self_relation_099`: write 0.99 instead of 1.0 for player <-> own team (and team <-> same team). Config only (`x4mp_teams.xml` value from `build_calls()`). Kills / confirms H1.
- `avatars_inert_once`: `ActivateObject(false)` only at spawn / re-own, never re-asserted (also for ghosts). Confirms H3.
- `dress_no_radar_no_known`: MD dress cues skip `set_object_forced_radar_visible` and `set_known` (avatars and ghosts). Confirms H4.
- `team_move_respawn`: on a team move, remove and respawn the avatar under the new faction at the same pose instead of `SetComponentOwner`. Confirms H2 and is the fix candidate.
- `no_set_faction_known`: skip `set_faction_known` in the team apply (cheap, rules out the faction-known path).

Probe (extend `md/x4mp_diag.xml` / `ui/x4mp_diag.lua`, new tag `liveview`): on `/x4mp knowledge watch [secs]` and automatically every 2 s while any avatar or ghost is in `player.sector`, log **one line** with: `player.sector` id and macro; for each team ship in the player's sector and in every sector holding an avatar (max 8): `owner`, `relationto.{faction.player}`, `isradarvisible`, `isinliveview`, `isgravidarplayeraccessible`, `isactive`, `isknown`, `sector.isknown`; and the result of `find_closest_undiscovered_position` (sector = player.sector, position = player position, range 2 km): `null` or a position. That last one reads the **discovered data from MD** while the map flips: if its answer flips with the map, the data layer flips (then H5/known); if it stays, the flip is purely render / live view. The user does not need to watch the map then: they note the times, and the log shows which flag changed at those times. Also set the X4 debug filter for general errors as usual and grep "Discovered area".

Run 2 (new build, one PC): repeat steps 1-5 once with `team_self_relation_099`, once with `avatars_inert_once`, once with `team_move_respawn` (each a 10 min run: start, step 2, step 3, step 5). Stop at the first switch that removes the flips.

## 6. Proposed fixes and side effects

| Fix | For | Side effects | Test without the user |
|---|---|---|---|
| Own-team relation 0.99 (inside `ally` 0.5-1.0, outside `self`) | H1 | UI value 29 instead of 30 (cosmetic); "own faction" treatments that need exactly 1.0 (none known: no licences, no trade, factions are inert shells); `PlayerReputation` "Prized Investor" promotion events (Finding 19) are expected to remain (they key on thresholds, check the log); allied docking / help behaviour stays | Catch2 on the relation plan values (`team_hub`, `x4mp_teams.xml` read-back `mismatches=0` still checked by the 0.001 tolerance, expected 0.99); validate `tools/validate-x4-xml.py`; the in-game check is one flip test |
| Team move = remove + respawn under the new faction at the same pose and loadout | H2 | New component id (the director already rebinds by idcode: the avatar keeps `net_id`, the idcode changes, update the record and the manifest); 1-2 s with no ship while respawning, a checkpoint in that window must list it as parked; clients see an `EntityDespawn`/`EntitySpawn` pair or a changed id (ghosts already handle respawn); loses damage state (avatars are indestructible at 100 hull before M5) | Extend the avatar director Catch2 team-move section (`avatars.director`) and the `avatars.hostsim` team move scenario; real check in game |
| Alternative for H2: MD `set_owner` instead of the native call | H2 | MD action instead of native; name, min hull, radar flags must be re-applied (re-run the dress cue); thread-safe on the MD side | hostsim fake MD answer |
| Re-assert `ActivateObject(false)` only when a read-back shows the ship active, at most every 30 s, never for parked avatars | H3 | A pushed ship that the engine re-activates (it started AI?) is caught later; `isactive` read needs an MD / Lua read (not in `GameFns` today) | Catch2 with a fake env that reports active / inactive |
| Skip radar-forced + `set_known` for avatars in sectors the player is not in | H4 | The avatar vanishes from the map in far sectors until the player's radar sees it (acceptable: matches NPC behaviour); the HUD list is unaffected | Lua / MD validation only |
| HUD: guard `Helper.clearDataForRefresh` (see section 7) | noise only | none | a HUD Lua test with a stale frame id |

Do not pick a fix before section 5: every candidate is a guess about engine internals that the unpacked data cannot confirm. After the experiment, the winning switch becomes the default and the other four are deleted.

## 7. The `helper.xpl ... GetChildren(): invalid frame ID` errors

- **Where from:** the message is raised by `ui/addons/ego_detailmonitorhelper/helper.xpl` (compiled; Lua source `helper.lua`), "from presentation `widget_fullscreen.bgf`" = a Lua callback run by the fullscreen widget presentation's update. `GetChildren(frame)` is called at `helper.lua:1634` (`findFrameLayer`), `:2418` (`Helper.clearDataForRefresh(menu, layer)`) and `:4125` (`onFrameHandleViewCreated`). Our HUD redraw (`ui/x4mp_hud.lua:230-232` `menu.display()` starts with `Helper.clearDataForRefresh(menu, cfg.layer)`) is the likely caller: it runs on the self-rescheduling `Helper.addDelayedOneTimeCallbackOnUpdate` loop and `menu.frames[6]` can still hold a frame id the engine already destroyed when another menu (the map) closed our frame (the HUD's own comment, lines 6-8 and 287).
- **Evidence:** in the sitting 3 authority log 14 errors, each within about 6-15 s after our own `hud: blocked by MapMenu,MapMenu,MapMenu (live), not forcing` / `hud: view ...` lines (e.g. 1560.9 s -> 1577.0 s, 1769.6 -> 1775.8, 2934.1 -> 2940.2); the same error appears on the **client** too (sitting 1 client 38 + 4 + 6 + 1, sitting 3 client 1), so it is not authority-specific and not sector-specific.
- **Related to the flip?** Very unlikely: it is per-menu, the flip is per-sector, and the draw-over-map case is already prevented (M3-16). Treat as log noise caused by a stale frame id. Fix when convenient: in `menu.display()` check `IsValidWidgetElement(menu.frames[layer])` first and drop the id (`menu.frames[layer] = nil`) before `clearDataForRefresh`. Verify by the error count reaching 0 in the next log.

## 8. Offline analysis done for this note (reproducible, no game data committed)

The checkpoints in `out/session4/data/saves/*.xml.gz` were parsed with ad-hoc Python (not committed): the `<discovered>` block per sector (`<quadtree depth x y>` attributes and node counts), the `knownto` attributes of sectors and stations, the avatar's attributes and sector, and the `fogofwar` listener. A reusable version would be a small `tools/` script (`tools/savescan` already walks the XML): "per-sector discovered node count + depth, known counts, team ships per sector", run on every checkpoint of a session so a flip experiment can also be judged from saves (not enough for a flip that lasts seconds, enough for data loss).

## 9. Open questions

- Where the bubble is centred in state (b) (player, avatar, other): not visible in our data; the experiment note asks for it.
- Whether the flip period is 5 s (H3); the video should be re-timed against the 2 s samples if the original is still available.
- Whether the highway-entry sector glitch started before or after the 16:01 re-own (the log has no sector change line for the avatar with a time; `ghost` / `avatars` info lines would show it).
- Finding 4 sitting 1: no checkpoint of the client exists; a client-side `knowledge watch` run of the sitting 1 setup is the only way to link the two findings.
