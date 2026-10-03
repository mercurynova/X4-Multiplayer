# M3 plan: two players see each other

Status: plan v1, 2026-10-03 (after M2 complete), **awaiting the user's answers** to the open questions below. Owner: lead
(Opus). Implementers: Sonnet agents, one task each, in a worktree ([execution-plan.md](execution-plan.md) §5, §6.1). In-game
tester: the user.

Read with: [architecture.md](architecture.md) §3, §4.5 step 6, §5.1, §6, §7.2, §8 (authoritative), [decisions.md](decisions.md)
ADR-011..016, 023, 026, 042, 046, 051 and Part 2 Q4/Q5/Q6/Q8/Q9/Q12, [mod-design.md](mod-design.md) §3.7, §4, §6, §7.5-7.7, §9 "M3",
§11, [protocol.md](protocol.md) §10-13, the session results [spikes/session-1-results.md](spikes/session-1-results.md),
[spikes/session-2-results.md](spikes/session-2-results.md), [spikes/session-3-results.md](spikes/session-3-results.md), and the
user script outline [in-game-session-4.md](in-game-session-4.md).

---

## Open questions for the user (answer these first)

Each has the recommended answer that the briefs will use unless you say otherwise.

**User answers (2026-10-03):** all as recommended, except **Q5**: the avatar ship should eventually follow the player's faction/race
(team origin, ADR-049/051); for M3 testing the Argon Elite is fine. So M3 keeps the server setting `Avatars.StarterShipMacro` (default the
Argon Elite) and the provisioning code takes the ship macro from one place, so that a later milestone can pick it per faction/race without
touching the avatar logic. Approved: wave 0 (spike kit M3-001/002).

**User note (2026-10-03, sitting 0):** the S13.6 test fighter (Nova Vanguard, `SpawnObjectAtPos2` default equipment) came with Mk2 weapons,
Mk3 engines/shields and other high-end parts. Fine for testing, but real starter avatars must be **early-game ships with early-game
equipment**, taken per race/story from the vanilla game starts (`libraries/gamestarts.xml` player ship + its loadout from
`libraries/loadouts.xml`, per ADR-049 team origins). M3 provisioning therefore takes a **ship macro + loadout id** from one place
(server setting, default: an Argon Elite with a basic vanilla-start loadout), never the spawn default; the per-race/story table is
filled when team origins land (M5), and a research note on the vanilla starts goes with M3-11.

| # | Question | Recommended answer |
|---|---|---|
| Q1 | **A second X4 copy for the two-PC sitting.** Steam lets one account play one game on one PC at a time, and Steam Families does not let two people play the *same* game at once. The second PC also needs build 611726 and **the same DLCs** (a DLC difference refuses the join). Options: (a) a second Steam account with its own X4 + the same DLCs; (b) a friend's PC with their own copy; (c) a GOG copy (only if its build matches 611726: X4Native is pinned to the Steam build, check first); (d) Steam offline mode on one PC with the same account (works technically on some setups; whether it is allowed by the licence terms is your call). | **(a) or (b).** Do not plan on (c) unless the build string matches; (d) only at your own discretion. Until a second copy exists, M3 finishes everything except criterion 1 on one PC (sittings 1 and 2), and criterion 1 stays "pending the two-PC sitting". |
| Q2 | **Who flies the second PC?** The 30-minute flight needs both ships moving. | **You alone is fine:** the host PC's ship flies on vanilla autopilot (order a long trip via a highway and a gate) while you fly the client PC next to it, then swap. A second person is a bonus for the chat and relation checks. |
| Q3 | **How "ghost error" is measured.** End-to-end lag alone is about 150 ms on a LAN (sample + server tick + interpolation delay), which is 75 m at 500 m/s even when the ghost follows the exact path. | **Two numbers.** (1) *Path error*: the ghost's rendered position vs the sender's true position **at the same server time** (what interpolation gets wrong): p95 < 50 m below 500 m/s and < 10 m in steady flight below 300 m/s. (2) *Display latency*: p95 <= 200 ms on a LAN. Both are logged by the mod every 5 s (`[sync]`), so no log merging is needed. |
| Q4 | **UDP realtime lane in M3 or M4?** The roadmap puts it in M4. The server and FakeNode already speak UDP; the mod sends Realtime frames over TCP today. TCP is fine on a wired LAN but stutters on Wi-Fi/VPN (head-of-line blocking). | **M3** (wave 1, self-contained in `core/net`), with automatic TCP fallback when UDP does not get through within 3 s. The two-PC sitting measures both. |
| Q5 | **Where a new player's avatar appears and which ship it is** (ADR-049 team HQs and origin starter ships come in M5). | M3: an **Argon Elite** (`ship_arg_s_fighter_01_a_macro`, server setting `Avatars.StarterShipMacro`), spawned **next to the host's ship** at a fixed per-slot offset (300-600 m), so everyone starts together. Rejoin: where the avatar was parked. |
| Q6 | **Offline players' avatars.** ADR-015 keeps them as parked team ships. Should the others see them? | **Yes, as parked ships** (name `[MP] Alice (offline)`), because they really exist in the shared universe. The ghost is removed only if the avatar is destroyed (M5) or the player is kicked/banned with asset removal (admin). |
| Q7 | **Host interruptions by checkpoints.** Every checkpoint shows X4's own "saving" screen on the host for about 5 s (session-3 open item 9). Joins do not cause a checkpoint (default `JoinCheckpointPolicy=LatestPlusJournal`); the 15-minute autosave does. | **Keep 15 minutes** for M3 (data safety while the code is young); the GUI can raise it (`Saves.AutosaveMinutes`). Hiding or shortening the save screen is a later investigation, not M3. |
| Q8 | **Quicksave on a client** (it bypasses the Lua block). From M3 a client has ghosts, so a client quicksave contains `[MP] ` ships. | **Keep the hard block in M4** as agreed. M3 adds: the janitor removes `[MP] ` ships whenever any save is loaded with the mod, and the GUI warning stays. Client saves are local copies only. |
| Q9 | **M3 checkpoints will need the mod to load** (active team factions, team-owned avatar ships). V21 passed for M2 checkpoints, which had none. | **Accept**, and test what really happens in sitting 2 (V21b). If X4 refuses such a save without the mod, record it; a "export to single-player" tool is M6. |
| Q10 | **Teams in M3.** Default stays one co-op team (ADR-017). The roadmap asks for relation colours from the GUI matrix. | Fly on **one team**; then a **5-minute two-team check** (GUI: move the second player to team 2, set Hostile, then Allied, look at the targeting colour). No PvP damage (M5). |
| Q11 | **Remote-player ghosts are made indestructible locally** (minimum hull), because kills are not synced before M5. Otherwise a client's local NPC (or you) can destroy a ghost that keeps flying for everyone else. | **Yes** for M3 (also for the avatars on the host, which NPCs there could otherwise kill). M5 replaces this with kill claims and damage relay. |
| Q12 | **Your own ship on a client in M3 is not protected.** Client NPC worlds are not synced until M4, so local NPCs can attack and kill you; X4 then shows its death screen. | **Accept** (vanilla behaviour); fly the tests in quiet, friendly sectors. A dev-only protection switch is not planned. |
| Q13 | **SETA.** Q4 of 2026-10-01 says SETA is off for all nodes during a session. | M3 **detects SETA and switches it off within 1 s**, with a notification "SETA is disabled in multiplayer". |
| Q14 | **Chat.** Reuse the vanilla chat window (needs a key bound to *Toggle Chat Window*), channels All and Team (`/t text`), whisper `/w name text`. SirNukes' chat API stays working (session-2 R5). | **Yes.** If the vanilla window fails outside Ventures (V23 rest, spike S13.12), a small chat panel in the Multiplayer window is the fallback. |

---

## 1. Goal, scope and non-goals

**Goal.** Two real players see each other. Each flies **their own ship (avatar)**; every other player's ship is shown
as a **ghost** named `[MP] <player>` in its team colour, moving smoothly at realtime rates, across sectors, gates and
highways. The authority's universe holds every player's avatar as a real, persistent, team-owned ship. Chat works both
ways. Saves stay clean. Developed and tested first on **one PC** (real X4 + FakeNode bots), then on **two PCs**.

**In scope**

| Area | What M3 delivers |
|---|---|
| Teams in game | `libraries/factions.xml` + `colors.xml` diffs with `x4mp_team_1..8` (inactive by default), text names; node setup at universe ready: activate the session's team factions, apply the relation matrix (ADR-016, unlock-set-relock, never lock `player`), re-apply on every `TeamRelations`; `team_setup_state` in `NodeStats`; self-test line `team.factions` |
| Own ship capture | Native per-frame sampling of the player's ship (sector, position, rotation, flags), `PlayerState` 20 Hz moving / 5 Hz idle / immediately on sector change, on **every** node incl. the authority; sector index from `GalaxyMetadata` (clients build their own macro -> local sector id map); pilot-seat detection (carry-over); SETA detection + switch-off |
| Avatars (ADR-015) | Authority: provision on `PlayerShip` (spawn under `x4mp_team_k`, inert, minimum hull), drive online avatars kinematically from the relayed `PlayerState`, park them when their player leaves, re-bind them after a checkpoint load, list them in the checkpoint manifest. Client: **takeover** after the load (the client wakes up in the host's ship): own avatar (local copy from the checkpoint, or a fresh spawn) -> `TeleportPlayerTo(force)` -> guard confirms -> the local copy of the host ship and of other avatars is removed through `SafeRemove` |
| Ghosts (client side, players only) | `Replication` / `EntitySpawn` / `EntityDespawn` / `EntityChange` for player ships -> native ghost spawn (`SpawnObjectAtPos2`, inert, `[MP] ` name, team owner, forced radar, minimum hull), per-frame `SetObjectSectorPos` with interpolation, sector changes, hide/show (docked, superhighway, not in avatar), `GhostRegistry` in the stash, adoption after `/reloadui`, despawn rules |
| Transport | UDP realtime lane in the mod (Q4), TCP fallback |
| Chat + roster UI | Vanilla chat window adapter (`x4mp_chat.lua`, chain-safe with SirNukes), All/Team/Whisper, player list in the Multiplayer window (name, team, sector, ping), join/leave notifications |
| Save hygiene v1 | Janitor **removes** `[MP] ` objects at universe ready (M2 only counted them); `SaveJob` verifies the ghost registry is empty on the authority; manifest lists avatars; `tools/savescan` (scan a save for `[MP] ` objects, reference leftovers and team-owned ships) |
| Carry-overs | Authority self-spawn when the player sits in the pilot seat; Join fields remembered in `x4mp.json` (§2) |
| Harness | hostsim fake world (spawn/move/teleport/sample exports) and a **two-DLL** e2e run (authority hostsim + client hostsim + FakeNode bot) in CI; FakeNode avatar provisioning, **wingman bots** that fly near a real player in the real galaxy, `--galaxy-file`; session-4 kit |

**Non-goals (explicit)**

- **No NPC/world streaming** (M4). Each client's local universe (NPC ships, economy, missions) keeps running unsynced
  and diverges from the authority's; only players are synced. No `CaptureSet` capture, no `SectorComplete`, no
  suppression of local NPCs, no manifest matching of stations (M4). The client sends the M2 count-only `ManifestReport`.
- **No combat or events** (M5): no kill claims, damage relay, deaths/respawns, cargo, trade, station builds, ship
  purchases. A player who changes into another ship is shown as hidden (flag `Hidden`) until M5 handles ship changes.
- **No in-game economy** (M5), no team asset commands (M6), no team HQ/origins (M5, ADR-048/049).
- **On-foot presence is M3b/M3c** (ADR-046/051): HUD presence list, MP lounge, remote characters, chosen appearance.
  M3 only hides a player's ghost while that player is on foot or docked inside. **M3b** = Tier 0 HUD list + Tier 1 MP
  lounge + appearance; **M3c** = Tier 2 any shared room; both are gated on session-2 sitting 3 (S10), which has not run yet.
- No quicksave hard block (M4, Q8), no installer/launcher (M6), no fog of war (ADR-038).
- Diplomacy screen (ADR-047) and research (ADR-048) stay later milestones; their spikes S11/S12 are not M3 blockers.

---

## 2. Carry-overs from M2 (m2-exit-report, session-3 retest)

| Carry-over | Where it goes |
|---|---|
| Self-spawn when the player sits in the pilot seat (today: 6 MD asks in 20 s; after a load the player **stands** in the ship for longer than 20 s and MD's `player.occupiedship` is null) | **M3-09**: native `GetPlayerOccupiedShipID()` / `GetPlayerControlledShipID()` are already read every frame by `PlayerGuard`; the edge 0 -> ship triggers one MD ship query (macro, name) and the self-spawn. No timer, no retry limit. The same tracker feeds `PlayerState` |
| Join fields in `x4mp.json` instead of `uidata.xml` (a run without the mod made X4 rewrite `uidata.xml` without our entries) | **M3-07**: native writes `last_address`, `last_name`, `last_host_role` (never a password) atomically (temp + rename) on a new bridge verb `x4mp.remember`; Lua reads them from the status topic; one-time migration from `__X4MP_USER` |
| Session-3 open item 10: download progress invisible on loopback | Session-4 sitting 3 (two PCs) checks it; no task unless it is still invisible over a LAN |
| Session-3 open item 9: host interrupted by the save screen | Q7; no M3 task |

---

## 3. Exit criteria

Verified by: **CI** (GitHub Actions, no X4), **local** (lead runs a command), **game-1** (one PC, session 4 sittings
1-2), **game-2** (two PCs, session 4 sitting 3). "hostsim pair" = two hostsim processes with the real DLL (authority +
client) plus the server and a FakeNode bot.

| # | Criterion | Verified by |
|---|---|---|
| 1 | **Fly together.** Two X4s on two PCs, 30 minutes, >= 5 sectors including a gate jump and a highway, each in their own avatar, each sees the other with the right ship model, team colour and the name `[MP] <player>`. No Game Over caused by the mod. | game-2 |
| 2 | **Ghost quality.** `[sync]` lines: path error p95 < 50 m below 500 m/s and < 10 m in steady flight below 300 m/s; display latency p95 <= 200 ms on a LAN (Q3). No ghost pops (despawn + respawn of the same `net_id` within 10 s) while both are in one sector. | game-2, game-1 (FakeNode wingman); CI hostsim pair with tight bounds (scripted circle/line/gate paths, path error p95 < 2 m) |
| 3 | **Avatar takeover.** After joining, the client sits in its own avatar (not the host's ship), the local copy of the host's ship is gone and the host is shown as a ghost; `remove_blocked_by_guard` may be logged but nothing the player is in is ever removed. Rejoin (leave + join again, and after a newer checkpoint) puts the player back into their avatar where it was parked, without a duplicate. | game-1, game-2; CI (hostsim scenario + Catch2 takeover state machine) |
| 4 | **Avatars on the authority.** Each online client's avatar moves on the authority with the client (path error p95 < 50 m), is owned by `x4mp_team_k`, takes no damage, stays parked when its player leaves, and is in the next checkpoint with its current position. After the authority reloads that checkpoint, every avatar is bound again (no duplicates, same `net_id`). | game-1 (sitting 2), game-2; CI (hostsim authority + FakeNode bots, manifest round trip) |
| 5 | **Pilot-seat self-spawn** (carry-over): the authority's own ship appears for the others within 1 s of the host sitting down, also after the session-start checkpoint. | game-1; CI (hostsim) |
| 6 | **Ghost lifecycle.** Gate jump: the ghost snaps into the new sector (no streak across space). Superhighway: hidden during transit, back at the exit (or followed, per spike S13.4). Docked inside / on foot / not in the avatar: hidden, back within 1 s. Leave: per Q6 (parked, `(offline)`). Kick: removed. | game-1, game-2; CI (hostsim pair scripted flags) |
| 7 | **Reloads.** `/reloadui` on a client with ghosts: the ghosts are adopted from the stash (no second spawn, no leftovers). A save load (new universe): the janitor finds 0 `[MP] ` objects after the load and the ghosts are back within 2 s. 20 reloads in hostsim: 0 leaks, 0 duplicates. | CI (hostsim), game-1 |
| 8 | **Save hygiene v1.** An authority checkpoint taken while 2 players are online: `tools/savescan` finds **0** `[MP] ` objects and the avatars under `x4mp_team_k`. A client quicksave with ghosts loads with the mod and the janitor removes them (count logged). | game-1 (sitting 2 + quicksave check), game-2; CI (savescan on fixtures, SaveJob strip test) |
| 9 | **Teams in game.** On every node the session's team factions are active, colours right; a relation change in the GUI matrix (Allied <-> Hostile) shows as the targeting colour of the other player's ghost within 2 s, on both PCs. | game-2 (two-team check, Q10), game-1 (FakeNode bot on team 2); CI (hostsim records the MD calls) |
| 10 | **Chat.** All and Team messages both ways between two X4s and between X4 and a FakeNode bot; whisper; the server rate limit message shows; SirNukes `/command`s still work with SirNukes enabled. | game-2, game-1; CI (Lua tests, FakeNode chat) |
| 11 | **Performance.** With 7 remote players (FakeNode wingman bots): mod main-thread p95 **< 0.2 ms** per frame on a client and on the authority (`NodeStats`, `[perf]`), FPS cost < 1 FPS vs. disconnected in the same spot, < 10 log lines/s sustained, < 20 kB/s per client. | game-1; CI (bandwidth from FakeNode, log-rate from hostsim) |
| 12 | **SETA** is switched off within 1 s while connected, with a notification (Q13). | game-1 |
| 13 | **UDP lane** (Q4): the GUI shows UDP active for both nodes; pulling the UDP path (firewall rule in sitting 3, or `--udp-block` in CI) falls back to TCP within 3 s without a disconnect. | CI (FakeNode/hostsim with UDP blocked), game-2 |
| 14 | **Remembered fields** (carry-over) in `x4mp.json`; still there after a run of X4 without the mod. | CI (hostsim), game-1 |
| 15 | **No regressions.** CI green on both OSes: all M1/M2 suites, Catch2, Lua tests, hostsim e2e (incl. the new pair run); `tools/e2e.ps1` full run locally. | CI + local |
| 16 | **Spike S13 verdicts recorded** (`docs/spikes/session-4-results.md` or a section of it) and any ADR they change updated **before** wave-2 briefs. | lead |

Evidence goes into `docs/m3-exit-report.md` (M2 style).

---

## 4. Design decisions

### 4.1 Who simulates what in M3

| Node | Simulates | Players' ships |
|---|---|---|
| **Authority** | The whole universe (NPCs, economy, missions) as in single player | Host's own ship: the save's `player` ship, real physics, sends `PlayerState` like everyone. Each client's ship: a **real, persistent avatar** owned by `x4mp_team_k`, inert while its player is online and driven kinematically from the relayed `PlayerState` (same interpolator as ghosts); parked (inert, unchanged) while offline. **No ghosts** on the authority |
| **Client** | Its own local copy of the universe, loaded from the checkpoint and running unsynced (diverges from the authority; M4 replaces NPCs in interest with ghosts) | Own ship: a local `player`-owned copy of its avatar, client-authoritative, sends `PlayerState`. Everyone else (host included): **ghosts** `[MP] <name>` under their team faction, inert, driven from `Replication`. Local copies of avatars and of the host's ship that came with the checkpoint are removed at takeover |
| **Server** | Nothing in the game sense | Mirror entity per player ship (avatar or host ship) fed directly from `PlayerState` (already built: `WorldMirror.ApplyPlayerState`, `RelayModule` 20 Hz feed to the authority with derived velocity, interest "player ships galaxy-wide, >= 2 Hz, Near 20 Hz") |

The server path exists from M1; M3 is mostly mod work plus small server gaps (M3-01).

### 4.2 Ghost creation, movement and removal

| Step | Options | Recommendation |
|---|---|---|
| Spawn | (a) native `SpawnObjectAtPos2(macro, sector, UIPosRot, owner)` via X4Native `get_game_function` (in the SDK list; session 1 used it from Lua FFI: 850 spawned, 0 failures); (b) MD `<create_ship>` (more control: no crew, `commandeerable`) through the actions shim (1 frame latency, V14) | **(a)**, on the frame thread, <= 2 player ghosts per frame. Owner = `x4mp_team_k` of the player's team (validated against `GetAllFactions` before every spawn; a missing faction is an error, never `nullptr`). (b) is the fallback if S13.1 shows a crew/AI that fights us |
| Inert | `ActivateObject(id, false)` (session 1: ghosts stayed where commanded, drift 0.03-0.4 m) | Keep, re-assert every 5 s; S13.1 checks "no pilot / no orders" for S and M ships |
| Name, radar, damage | `SetComponentName` is a **Lua global, not an FFI export**; MD `<set_object_name>`; `SetObjectForcedRadarVisible` (export); MD `<set_object_min_hull>` / `<set_object_min_shield>` exist (`common.xsd`) | One MD "ghost dress" call per spawn: name `[MP] <player>`, min hull 100 % (Q11). Radar natively. The registry, not the name, is the primary identity (the name is for the janitor and the player) |
| Move | `SetObjectSectorPos(id, sector, UIPosRot)` every frame (teleport, no physics); MD `<set_object_velocity>` exists (linear + angular) and might give engine effects / smoother look between our sets | Per-frame `SetObjectSectorPos` with the interpolated pose. Velocity assist via MD only if S13.2 rates it clearly better, and then only for player ghosts at <= 5 Hz |
| Cross-sector | `SetObjectSectorPos` with another sector's id (unverified) vs. despawn + respawn | S13.3 decides; fallback = respawn on sector change (cheap for a handful of ghosts) |
| Remove | `SafeRemove` (the only `RemoveComponent` caller; session 1: clean, no wreck) | Unchanged. Ghosts are in `GhostRegistry` before their first frame; prune touches only registry entries |
| Never touch the player | `PlayerGuard` + `SafeRemove` (M2) | Unchanged. The takeover (4.3) removes the host-ship copy only after the guard shows the player in the new ship for 10 consecutive frames |

### 4.3 Avatars and the takeover

Everybody loads the same checkpoint, so **every client wakes up inside the host's ship**, standing in the cockpit (session
3). Flow on the client after `NodeReady`:

1. Send `PlayerShip` (request my avatar). The server forwards it to the authority (already built), which returns the
   existing avatar or spawns one (Q5), and answers with `EntitySpawn{origin=PlayerShip, controller_player}`.
2. Find the local object for that avatar: avatars are **manifest entries** (new fields, 4.13); the client binds by
   (sector macro, macro, position within 1 m, idcode), like M4 will for stations. If found: `SetComponentOwner(id,
   "player")` (keeps the save's loadout); if not (new player): spawn a `player`-owned copy at the avatar's position.
3. `CanTeleportPlayerTo`, then `TeleportPlayerTo(ship, true, true, true)` (force: V02). Works while standing? -> S13.6.
4. Wait until `GetPlayerOccupiedShipID()` or `GetPlayerControlledShipID()` equals the avatar for 10 frames (guard).
5. `SafeRemove` the local copy of the host's ship and of every other avatar the manifest names; their ghosts come
   through `Replication` like any player ship. NetMap kinds: own avatar `self`, others `ghost`.
6. Only then start sending `PlayerState` with the avatar's `net_id` (the server stamps it anyway).

Failure handling: teleport refused -> retry every 2 s (max 10), HUD "Sit in the pilot seat to take over your ship"
(fallback if S13.6 says teleport needs the seat); never remove anything before step 4. Host side: the authority human
keeps the save's `player` ship; the authority sends its own self-spawn when the host sits down (carry-over).

Authority side per avatar: spawn under `x4mp_team_k`, `ActivateObject(false)`, min hull, `AvatarRegistry{player,
net_id, UniverseID}` (stash-mirrored); online -> driven from relayed `PlayerState` through the interpolator; leave ->
`EntityChange{Controller=0}`, stays where it is (parked). After a checkpoint load the authority binds avatars from its
own manifest exactly like a client (one `AvatarBinder` for both roles). An MD table keyed by component (the V12
decision) is a later improvement once S13.8 shows it survives save/load.

### 4.4 Coordinate frames and transitions

- Wire: sector-relative position (i32, 1/64 m), rotation in **radians** (i16, protocol.md §11), sector as `u16` index
  from `GalaxyMetadata`. Each node maps index -> macro -> local sector id; clients get the map from the same MD
  `find_sector` collector the authority uses (M2 `x4mp_galaxy.xml`), at universe ready.
- Sampling: `GetObjectPositionInSector(ship)` + `GetContextByClass(ship, "sector")`. Units of `UIPosRot` angles
  (degrees or radians) and behaviour in highways/gates/docking are **S13.4**.
- **Gate / accelerator jump:** new sector -> immediate `PlayerState` with `Teleport` -> receivers snap into the new sector.
- **Local highways** (inside a sector): positions stay sector-relative; normal interpolation (high speed, so
  extrapolation is capped and snaps scale with speed).
- **Superhighways** (between sectors): if the sector context is missing in transit (S13.4), send flag `InHighway` +
  `Hidden` with the last sector; receivers hide the ghost and show it again at the exit. If positions stay valid,
  render normally.
- **Docked:** docked outside on a pad: render (stations do not move). Docked inside / on foot / spacesuit / in another
  ship: flag `Hidden`, ghost hidden (`DespawnReason.DockedInside` or a local hide), avatar on the authority stays at its
  last position (real docking of avatars is M5).
- **SETA:** blocked (Q13). **Local pause** (Esc menu): the render clock is real time, so ghosts keep moving while a
  client is paused; on the authority the NPC world pauses (accepted in M3; S13.10 checks that moving objects works while
  paused).

### 4.5 Rates and bandwidth

| Stream | Rate | Size | Per client |
|---|---|---|---|
| `PlayerState` C->S | 20 Hz moving, 5 Hz idle (< 1 m/s and no turn), immediately on sector change / teleport / hide; 1 Hz while hidden | ~60 B + header | ~1.5 kB/s up |
| `Replication` S->C (player ships only) | server tick 20 Hz; Near (same sector, 15 km) 20 Hz, rest of sector 5 Hz, other sectors >= 2 Hz (ADR-011, built) | ~40 B per entry | 7 remote players ~8 kB/s down incl. headers |
| `PlayerState` relay S->A | 20 Hz per online client, with derived velocity (built) | ~70 B | authority ~10 kB/s down for 7 |
| Keyframes | full mask every 5 s (built) | | |

Budget: **< 20 kB/s per client** in M3 (the 256 kB/s budget of ADR-026 is for M4 world streaming).

### 4.6 Interpolation and extrapolation (`core/ghost`, shared by client ghosts and authority avatars)

- Ring of 8 samples per entity (20 Hz x 250 ms max delay needs >= 6). Render time = `server_now - delay`.
- Delay: 100 ms default for player ships, adaptive 80-250 ms from the p95 inter-arrival jitter (TCP needs more than UDP).
- Position: cubic Hermite with velocities (server-derived, 0.25 m/s fine / 4 m/s coarse); rotation: Euler -> quaternion,
  slerp, back to `UIPosRot`.
- Past the newest sample: extrapolate linearly up to 500 ms, then hold and mark stale; stale > 5 s -> hide.
- Corrections: snap when the error exceeds `max(200 m, speed x 0.5 s)` or on `Teleport`; else blend over 200 ms.
- Error measurement (Q3): the renderer keeps (render time, rendered pose) for 2 s; when samples covering that time have
  arrived it computes path error against the sender's samples (linear between samples). Display latency =
  `server_now - sample_time` of the sample in use. `[sync] net=.. player=.. err_p50/p95/max lat_p95 speed_max` every 5 s.

### 4.7 Clocks

- **Network time = the server clock.** Every node estimates it with the existing `core/net` clock sync (min-RTT filter).
  `PlayerState.sample_time_us`, `Replication.server_time_us` and entry offsets are in server time. Interpolation never
  uses game time.
- **Game time** (authority `GetCurrentGameTime()` == MD `player.age`, session 2 C5) only stamps world messages
  (`EntitySpawn.game_time`, `SaveStarted`). It stops on pause and differs per node; nothing in M3 compares it across nodes.
- Paused nodes keep sending `PlayerState` (unchanged pose -> 5 Hz idle) and keep rendering ghosts.

### 4.8 Save load, `/reloadui`, DLL re-init

- The DLL is unloaded and re-initialised on every save load and `/reloadui` (session 2 B4/B6); the stash survives; the
  M2 epoch rule decides "same universe" vs "new universe".
- **Same universe** (`/reloadui`): `GhostRegistry`, `AvatarRegistry` and `NetMap` come back from the stash; each id is
  checked with `IsValidComponent`; valid ones are adopted (no respawn), invalid ones dropped and respawned from the next
  sample. The server resets baselines on resume, so a full keyframe arrives anyway.
- **New universe** (save load, rejoin with a changed checkpoint): registries cleared; the janitor (first frame after
  universe ready) removes every `[MP] ` object with `SafeRemove` and logs the count; takeover (4.3) runs again; ghosts
  respawn from replication.
- Authority checkpoint (`SaveJob`): registry must be empty (no ghosts on the authority; verified, logged); avatars are
  saved as real ships with their current pose; manifest gets the avatar entries.

### 4.9 Teams in M3

The factions **must exist in M3** (avatars are owned by `x4mp_team_k` on the authority, ghosts on clients). M3 ships the
product `libraries/factions.xml` (eight factions, `active="0"`, licences and base relations copied from `player`, tags
per mod-design §11.2), `libraries/colors.xml` (`faction_x4mp_team_1..8`) and text entries on page 92000 (placeholder
names "X4MP Team N"; runtime renames via `set_faction_identity` are optional, V25). Setup job at universe ready
(mod-design §11.5 steps 1, 2, 5), relations per ADR-016 with the 2026-10-01 correction (lock team factions only). The
inherit-team re-own job (ADR-033) and team HQs are **not** M3 (M5).

### 4.10 Save hygiene v1 (ADR-023)

Janitor removal on, the `SaveJob` empty-registry check, the avatar manifest entries, `tools/savescan` (C# console:
streams a `.xml.gz`, counts `[MP] ` names, reference leftovers `x4mp_host`/`x4mp_client_*`, team-owned objects per
faction, prints JSON), and the M2 client save block unchanged. Quicksave hard block stays M4 (Q8).

### 4.11 Chat and roster UI

`x4mp_chat.lua` per mod-design §7.6 (wrap `OnlineGetChatMessages` / `OnlineSendChatMessage` at wrap time, re-wrap if
replaced, unwrap only if ours, `/` passthrough), channels via `/t` and `/w`; player list rows from `RosterUpdate` (name in
team colour, team, sector name from the macro, ping, "online/offline"); notifications for join/leave through the M2
notify cue. "Locate on map" is a stretch.

### 4.12 Performance budget

- Client and authority: mod main-thread **p95 < 0.2 ms** per frame with 7 remote players (sampling 1 ship, <= 7
  `SetObjectSectorPos` per frame, decode, interpolation). No per-frame MD or Lua; MD only at spawn ("dress"), team setup,
  galaxy map and the self-spawn query.
- Spawns <= 2 per frame for player ghosts. Logs: one `[sync]` and one `[perf]` line per 5 s; spawn/despawn lines are
  events, not per frame.
- Ghost rendering cost is the game's (one ship each), not counted as mod cost.

### 4.13 Protocol and schema deltas (append-only, protocol stays 0.1)

| # | Change |
|---|---|
| D1 | `StateFlags`: append `Hidden` (ghost must not be shown: on foot, docked inside, superhighway transit, not in the avatar) |
| D2 | `ManifestEntry`: append `origin:EntityOrigin` and `controller_player:ushort` (avatars carry `origin=PlayerShip` + their player), so nodes can bind avatars |
| D3 | `Capability`: none new (`UdpRealtime`, `GhostRender` exist); the mod starts advertising `GhostRender` and `UdpRealtime` |
| D4 | Server settings (not schema): `Avatars.StarterShipMacro`, `Avatars.SpawnOffsetMeters`, sent in `SessionSettings` |

---

## 5. Spikes before coding (session 4, sitting 0) and what waits for them

Sitting 0 is **one PC, about 60 minutes**, mostly automatic blocks of the throwaway `x4mp_probe` (native) and
`x4mp_spike` (Lua/MD) extensions (M3-001/002), run on a **scratch save slot**. The user flies a few manoeuvres and gives
1-5 ratings. Script: [in-game-session-4.md](in-game-session-4.md) Sitting 0.

| ID | Experiment (what the user does) | Pass criterion | Blocks | If it fails |
|---|---|---|---|---|
| S13.1 | Native spawn of an S and an M ship under `x4mp_team_2`, 1 km ahead; `ActivateObject(false)`; dress via MD. *User:* look at it, target it, check name/colour/radar, wait 60 s | Spawned from native, no pilot/orders (logged), stays put (drift < 1 m in 60 s), name and colour shown | M3-10, M3-11 | MD `<create_ship>` without crew; or empty the order queue |
| S13.2 | Ghost motion near the player: circle at 100/300/600 m/s at 1 km, line at 3 km/s; modes (a) per-frame set, (b) 20 Hz raw, (c) per-frame + MD velocity 5 Hz. *User:* watch each 20 s, rate smoothness 1-5; fly slowly into a parked ghost | (a) or (c) rated >= 4; collision outcome noted (bounce/damage) | M3-10 | Larger delay; velocity assist; keep ghosts >= 50 m from the player if collisions hurt |
| S13.3 | Cross-sector `SetObjectSectorPos` (ghost moved into the adjacent sector) | Sector context changes, ghost visible there on the map | M3-10 | Respawn on sector change |
| S13.4 | Self-sampling at 20 Hz while the *user* flies: cruise, boost, travel drive, local highway, superhighway, gate jump, accelerator, dock on a pad, dock inside, stand up, SETA | Sector + pose valid everywhere except where documented; angle units known (deg/rad); call cost (QPC) < 5 us | M3-09 | Hide in the gaps (flag `Hidden`) |
| S13.5 | Pilot seat after a load: per-frame `GetPlayerOccupiedShipID` / `ControlledShipID` / `PlayerObjectID` / container while the *user* stands, sits, walks out | Edge "sits down" visible natively | M3-09 (carry-over) | Status-tick MD query while the session runs |
| S13.6 | Takeover from the post-load state: spawn a `player`-owned fighter 300 m away, `TeleportPlayerTo(force)` while **standing** in the cockpit, then while **docked inside** a station; confirm guard; `SafeRemove` the vacated original on the scratch slot | Teleport works in both states; old ship removed; no Game Over | M3-12 | Wait for the seat, HUD hint |
| S13.7 | Team factions from the **product** diffs (not the spike's): activate team 1-2, relations allied/hostile vs `player`, colour, `set_object_min_hull` on a ghost; *user* shoots it briefly | Colours right; hull never drops below the minimum | M3-08 | Re-assert hull every 1 s |
| S13.8 | Persistence: spawn a team-owned ship, save to the scratch slot, reload: position (exact?), idcode, active state; an MD table keyed by that component (V12 follow-up) | Position within 1 m and idcode equal (binding works); table result recorded | M3-11 | Bind by idcode + nearest within 50 m |
| S13.9 | SETA: `IsSetaActive` when the *user* enables SETA; MD `<set_timewarp_factor>` / `<toggle_timewarp>` turns it off | Detected and turned off | M3-09 | Notification only + server warning |
| S13.10 | Esc pause: does `on_frame_update` tick and does `SetObjectSectorPos` move a ghost while paused? | Recorded | M3-10 | Ghosts freeze while the local game is paused (cosmetic) |
| S13.11 | Galaxy dump: write the sorted sector macros + links to `Documents\Egosoft\X4\x4mp\galaxy-dump.json` (FakeNode `--galaxy-file` for sittings 1-2) | File written, >= 140 sectors | M3-05 | Export from the server's galaxy cache after an authority run |
| S13.12 | Chat window outside Ventures: wrapper shows an injected message with an arbitrary author name and colour (`GetChatAuthorColor2`); *user* types a line (must reach the spike, not Ventures) | Shows and sends | M3-06 | Chat panel in the Multiplayer window |
| (opt.) | Session-2 `diplo1`/`diplo2` (S11.1/S11.2, 10 min) if the user has time: lock semantics for M3-08 | — | nothing | ADR-016 as written |

Wave-2 briefs are written after the verdicts (criterion 16), naming the chosen path, as in M2.

---

## 6. Waves and tasks

Conventions as M2: `M3-001..` = throwaway/test kit, `M3-01..` = product. Sizes: S <= 1 day, M 2-3, L 4-6 developer
days; time-box = agent wall clock. **At most 4 agents at a time.** Every brief carries the execution-plan §6.1 rules
(no sleep-polling, foreground or background runs, state the cost of retry loops) and the hard rules (no `reference/`
code, no X4 install writes, config from files, never log passwords, no personal data).

**Shared-file rules for M3**

| Shared file / resource | Rule |
|---|---|
| `protocol/schema/*.fbs`, `MsgType`, golden vectors | Only M3-01 (wave 1) |
| `mod/CMakeLists.txt` | Append-only, one block per task |
| `mod/native/host/feature_list.cpp` | One include + one `registry.add` line per feature task |
| `mod/native/game/game_api.*` (new exports) | Each wave-2 task adds its exports in its own `game/<area>_api.*` file; `GameFns`/`kGameFnCount` edits only by M3-04 (hostsim fake world, wave 1) which adds **all** M3 exports at once |
| `mod/extension/x4mp/ui.xml`, `content.xml` | M3-06 adds `x4mp_chat.lua`/`x4mp_players.lua`; M3-08 adds `libraries/*`; nobody else |
| `ui/x4mp_bridge.lua` | Topics/verbs through each task's own Lua file; bridge edits only by M3-06 in wave 1 and M3-12 in wave 2 |
| `ui/x4mp_menu.lua` | M3-07 (wave 1), M3-06 player list in its own file |
| `md/x4mp_galaxy.xml` | M3-09 only |
| `server/src/X4MP.Core/Session/MessagePolicy.cs`, `Relay/**`, `World/**` | M3-01 only |
| `tools/X4MP.FakeNode*` | M3-05 only (wave 1), M3-14 later |
| `tools/e2e.ps1`, `.github/workflows/ci.yml` | M3-04 in wave 1, M3-14 in wave 3 |
| Ports | C# `TestPorts`; hostsim pair runs use **47940-47949** (new range); existing ranges unchanged; user sessions use the defaults |

### 6.1 Wave 0: sitting-0 spike kit

| ID | Title | Content | Files | Acceptance | Size / time-box | Deps |
|---|---|---|---|---|---|---|
| **M3-001** | Probe blocks S13 (native) | `x4mp_probe` blocks run from `run-block.ps1`: `ghost_spawn` (S13.1), `ghost_motion <mode>` (S13.2: circle/line paths, modes a/b/c, QPC per call), `ghost_xsector` (S13.3), `sample` (S13.4, 20 Hz to the log for 120 s, context + pose + flags + `IsSetaActive`), `seat` (S13.5), `takeover` / `takeover_docked` (S13.6, refuses unless the save name is the scratch slot from the config), `persist_spawn` / `persist_check` (S13.8), `seta` (S13.9), `pause_move` (S13.10). All removals through a copy of the guard logic; logs in the session-2 format | `mod/spikes/x4mp_probe/**`, `X4MP_SPIKES` CMake block | Builds with `-Spikes`, normal build unchanged; each block idempotent; a 20-line smoke with a stub API loads every block; never removes an id in the guard | L / 150 min | — |
| **M3-002** | Spike Lua/MD + kit scripts | `x4mp_spike` blocks: `dress` (MD name/min hull/forced radar for an id from the probe), `teams_product` (S13.7 against the **product** library diffs copied in by the kit), `velocity` (MD `set_object_velocity` helper for S13.2c), `galaxy_dump` (S13.11), `chat` (S13.12). `tools/session4/` sitting-0 scripts: `install-spike.ps1` (swap: disable product `x4mp`, install probe + spike; `-Restore` swaps back), `write-probe-config.ps1 -ScratchSlot`, `run-block.ps1`, `collect-logs.ps1 -Label` | `mod/spikes/x4mp_spike/**`, `tools/session4/**` (sitting-0 part) | luacheck clean; MD/diff validated locally against the unpacked XSDs; scripts pass `-WhatIf`; no user paths hard-coded | M / 120 min | — |

Then the user runs **sitting 0**; the lead records the S13 verdicts (criterion 16).

### 6.2 Wave 1: foundations (does not wait for sitting 0)

| ID | Title | Goal | Files / areas | Acceptance tests | Size / time-box | Deps |
|---|---|---|---|---|---|---|
| **M3-01** | Protocol deltas + server gaps | D1/D2 schema appends + golden vectors; server: `Avatars.*` settings in `SessionSettings`; on leave the authority's `EntityChange{Controller=0}` keeps the avatar replicated as parked (Q6), on kick/ban an admin option removes it; `[MP] … (offline)` naming is client-side, server provides `online` in the roster; audit + tests: a client never receives its own ship in `Replication`; `PlayerShip` held requests survive authority resume; player sector/position on the GUI Players page and Map | `protocol/schema/**`, `server/src/X4MP.Core/{Relay,World,Session,Settings}/**`, `server/web/src/pages/{players,map}/**`, tests | C# unit + live: own-ship exclusion, controller clear on leave, parked avatar still replicated, settings round trip; Playwright: player row shows the sector; all M1/M2 suites green | M / 120 min | — |
| **M3-02** | `core/ghost` | Pure C++: sample ring, Hermite + slerp, adaptive delay, extrapolation/hold/stale, snap/blend, `Teleport`/`Hidden` handling, path-error and latency stats (4.6); `GhostRegistry` + `AvatarRegistry` + `NetMap` with stash (de)serialisation and adoption rules (4.8); Replication-codec decode into per-entity sample streams (uses `protocol/cpp`) | `mod/native/core/ghost/**`, tests | Catch2: circle/line/accel/gate-jump tracks with jitter and loss -> path error bounds; adoption keeps valid ids, drops invalid; stash round trip; no allocation per frame in the hot path (counter) | M / 150 min | — |
| **M3-03** | UDP realtime lane (mod) | `core/net`: bind UDP on `Welcome{udp_port, udp_token}`, datagram header codec (PROTO), Realtime lane over UDP, acks, fallback to TCP after 3 s without a server ack, re-probe every 30 s; capability `UdpRealtime` | `mod/native/core/net/**`, tests, `x4mp-headless` flag | Catch2 + live test against the real server (headless): UDP active, 5 % loss tolerated, UDP blocked -> TCP within 3 s without disconnect | M / 120 min | — |
| **M3-04** | hostsim fake world + pair runner | All M3 exports added to `GameFns` with null-safe wrappers (`SpawnObjectAtPos2`, `ActivateObject`, `SetObjectSectorPos`, `GetObjectPositionInSector`, `TeleportPlayerTo`, `CanTeleportPlayerTo`, `SetComponentOwner`, `SetObjectForcedRadarVisible`, `IsSetaActive`, `GetObjectIDCode`, `GetComponentName`, `IsComponentWrecked`); hostsim keeps a tiny fake universe (objects, sectors, player ship, seat state) and new commands (`ship path circle|line|gate ...`, `seat on|off`, `dock`, `seta on`, `expect-object`, `expect-ghost <player> err_p95 < N`); `pair_run.ps1` starts server + FakeNode + two hostsim processes; CI step `HostSimPair` | `mod/tools/hostsim/**`, `mod/native/game/game_api.*`, `mod/tests/hostsim/**`, `tools/e2e.ps1`, `ci.yml` | A scripted DLL-free smoke (stub DLL) for each new command; `pair_run.ps1` runs green against today's DLL with a trivial scenario; CI step < 3 min | L / 150 min | — |
| **M3-05** | FakeNode for M3 | Authority: answer `PlayerShip` with an avatar `EntitySpawn` (real macro ref, `x4mp_team_k`, spawn offset from the host ship); the fake **host ship** is self-spawned where the first `PlayerShip` says the client stands (that is the save's player ship: same macro, sector and position), so a real client sees `[MP] Host` where its local copy was; keep avatars across resume, `EntityChange` on leave; `--galaxy-file galaxy-dump.json` (real sector macros + links) for `GalaxyMetadata`; clients: `--wingman <player name>` (formation/orbit around the target's replicated pose, follow gate jumps with `Teleport`, speeds `--wingman-speed`), `--chat-echo`; per-bot `[sync]`-style stats of what it receives | `tools/X4MP.FakeNode*/**`, `docs/fakenode.md` | Live tests: swarm with wingmen around a FakeNode "real" player -> replication rate >= 18 Hz in Near; avatar spawn round trip; galaxy file parsed; chat echo | M / 120 min | — |
| **M3-06** | Chat + player list (Lua) | `x4mp_chat.lua` (4.11), bridge verbs `chat_send`, topic `chat`; `x4mp_players.lua` table in the Multiplayer window; join/leave notifications; native `ChatSend`/`ChatMessage` routing in a small `features/chat` | `ui/x4mp_chat.lua`, `ui/x4mp_players.lua`, `ui.xml`, `x4mp_bridge.lua`, `mod/native/features/chat/**`, Lua tests | Lua tests with vanilla-shaped and SirNukes-shaped globals (chain, re-wrap, unwrap only if ours, `/` passthrough); hostsim: chat round trip with a FakeNode `--chat-echo` bot | M / 120 min | — |
| **M3-07** | Remembered fields in `x4mp.json` | Carry-over (§2) | `mod/native/core/config/**` (writer), `features/join` (verb), `ui/x4mp_menu.lua` | Catch2 atomic write; hostsim: fields survive restart, migration from `__X4MP_USER` once, never a password (`expect-no-secret`) | S / 60 min | — |

Run order inside the cap: M3-01, M3-02, M3-04, M3-05 first; M3-03, M3-06, M3-07 next.

### 6.3 Wave 2: in game (briefs after sitting 0)

| ID | Title | Goal | Files / areas | Acceptance tests | Size / time-box | Deps |
|---|---|---|---|---|---|---|
| **M3-08** | `features/teams` | Library diffs (4.9), setup job, relation apply + re-apply, `team_setup_state`, self-test `team.factions` | `extension/x4mp/libraries/**`, `t/0001-l044.xml` (team texts), `md/x4mp_teams.xml`, `mod/native/features/teams/**` | XSD/diff validation locally; hostsim: setup MD calls in order, matrix change re-applied within 1 frame of `TeamRelations`; Catch2 for the matrix -> call list | M / 120 min | S13.7 |
| **M3-09** | `features/selfship` | Own-ship tracker (seat edge, sample, flags, sector index via a client-side galaxy map from `x4mp_galaxy.xml`), `PlayerState` rates (4.5), authority self-spawn on the seat edge (replaces the 6-ask retry), SETA off (Q13) | `mod/native/features/selfship/**`, `md/x4mp_galaxy.xml` (client mode), `features/authority` (remove the retry) | hostsim: seat off -> no state; seat on -> self-spawn within 1 frame + 20 Hz states; idle 5 Hz; sector change immediate; SETA reset; Catch2 rate logic | M / 120 min | M3-04; S13.4, S13.5, S13.9 |
| **M3-10** | `features/ghosts` (client) | Message handling for player ships, spawn + dress + inert + radar + min hull, per-frame driver with `core/ghost`, hide/show, sector changes (S13.3 path), registry in stash + adoption, `[sync]` stats, budget | `mod/native/features/ghosts/**`, `md/x4mp_ghosts.xml` (dress cue) | hostsim pair: scripted circle/line/gate -> `expect-ghost err_p95 < 2`; hide/show on flags; `/reloadui` adoption; 20 reloads 0 leaks | L / 150 min | M3-02, M3-04; S13.1-3, S13.10 |
| **M3-11** | `features/avatars` (authority) | Provision on `PlayerShip` (Q5), drive online avatars (interpolator), park on leave, min hull, `AvatarRegistry`, manifest avatar entries, `AvatarBinder` after a checkpoint load | `mod/native/features/avatars/**` (authority half), `features/authority` (manifest writer hook) | hostsim authority + FakeNode bots: avatars spawned and moving, parked on leave, checkpoint manifest lists them, reload -> rebound without duplicates | L / 150 min | M3-01, M3-02, M3-04, M3-08; S13.1, S13.8 |
| **M3-12** | Client takeover | 4.3 steps 1-6 as a state machine (Catch2 with a fake game), `AvatarBinder` client use, HUD hint fallback, removal of host-ship and avatar copies | `mod/native/features/avatars/**` (client half, after M3-11 merges), `x4mp_bridge.lua` (hint topic) | Catch2 state machine incl. refusal/retry and guard; hostsim pair: client ends in its avatar, host copy removed only after the guard, rejoin finds the parked avatar | M / 120 min | M3-09, M3-11; S13.6 |

### 6.4 Wave 3: hygiene, integration, kit

| ID | Title | Goal | Files / areas | Acceptance tests | Size / time-box | Deps |
|---|---|---|---|---|---|---|
| **M3-13** | Save hygiene v1 | Janitor removal (both roles, `SafeRemove`, count to log + `LogForward`), `SaveJob` empty-registry check, `tools/savescan` (C#), fixtures (synthetic saves, no game data) | `features/janitor/**`, `features/authority` (check), `tools/X4MP.SaveScan/**` | Catch2/hostsim: janitor removes only `[MP] ` registry-or-named objects, never guarded ids; savescan JSON on fixtures; CI step | M / 90 min | M3-10, M3-11 |
| **M3-14** | Integration, CI and session-4 kit | Pair-run scenarios in CI (join + takeover + ghosts + chat + reload + checkpoint), perf/bandwidth/log-rate checks, `tools/session4/` (topology scripts: `start-fake-authority.ps1 -Wingmen N`, `start-fake-clients.ps1 -Wingmen N`, `start-server-lan.ps1` with firewall check, `make-client-kit.ps1` for PC 2, `sync-report.ps1` summarising `[sync]`/`[perf]` lines, `savescan.ps1`, `collect-logs.ps1`), **kit check against the final code** + dry run in hostsim, finalise `docs/in-game-session-4.md`, update mod-design §4/§9/§11 and protocol.md for D1/D2 | `tools/session4/**`, `mod/tests/hostsim/**`, `tools/e2e.ps1`, `ci.yml`, `docs/in-game-session-4.md`, docs | Lead dry-runs every script (`-WhatIf` / hostsim); CI green; the script names in the session doc exist | L / 150 min | all above |

**Critical path:** M3-001/002 -> sitting 0 -> M3-10 / M3-11 -> M3-12 -> M3-13 -> M3-14 -> session 4 sittings 1-3.
Wave 1 runs during the wait for sitting 0.

---

## 7. Testing strategy and risks

### 7.1 Tests

| Layer | What | Where |
|---|---|---|
| Unit | `core/ghost` tracks, registries, adoption; UDP lane; rate logic; takeover state machine; team matrix -> calls; server own-ship exclusion, parked avatars, settings | Catch2, xUnit (CI) |
| Lua | chat chain-safety, player list rendering logic, remembered fields | Lua runner (CI) |
| hostsim (one DLL) | selfship, teams, janitor, reload adoption, self-spawn on seat, SETA | CI `HostSim` step |
| **hostsim pair** | authority DLL + client DLL + server + FakeNode bot: takeover, ghosts both ways with path error, gate/hidden flags, chat, checkpoint with avatars + rebind, 20 reloads | CI `HostSimPair` step (new) |
| FakeNode | wingman bots, avatar provisioning, UDP loss/blocked, swarm 8 clients with player ships | CI live tests, local swarms |
| savescan | fixtures | CI |
| In game | sitting 0 (spikes), sitting 1 (one PC, real client + FakeNode authority + wingmen), sitting 2 (one PC, real authority + FakeNode wingmen), sitting 3 (two PCs) | [in-game-session-4.md](in-game-session-4.md) |

What only the game can show: how ghosts look and move, the takeover from the real post-load state, real highway and
dock behaviour, colours, the chat window, true frame cost, two real players over a real LAN.

### 7.2 Biggest risks

| Risk | Impact | Mitigation |
|---|---|---|
| Ghost motion looks jittery next to the player (per-frame teleport vs camera) | Core feature looks bad | S13.2 before coding; velocity assist; adaptive delay; fixed-step render clock |
| Takeover from the standing/docked post-load state fails | Clients stuck in the host's ship | S13.6; seat-hint fallback; nothing removed before the guard confirms |
| Superhighway / docking positions unreadable | Ghost jumps or vanishes | S13.4; `Hidden` flag path |
| No second X4 licence | Criterion 1 cannot run | Q1; everything else verified on one PC with FakeNode wingmen |
| Avatars on the authority attacked by its NPCs, or ghosts by local NPCs | Divergent deaths before M5 | Minimum hull on both (Q11) |
| Client-local NPCs differ from the authority's (no M4 yet) | Testers confused; local NPCs kill a client (Q12) | Documented in the script; quiet sectors |
| Collisions with ghosts push or damage the player | Annoying, maybe a death | S13.2 collision test; keep-out distance if needed |
| M3 checkpoints need the mod | V21 regresses | Q9; record in sitting 2 |
| Host interrupted by the 5 s save screen | Annoying with real players | Q7 |
| Scope creep into M4 (NPC streaming) | Delay | Non-goals list; player ships only |

## 8. Handoff notes from merged tasks

(Filled in as tasks merge, as in m2-plan §8.)

### M3-02 `core/ghost` (pure C++, `mod/native/core/ghost/**`, tests `mod/tests/ghost/*`, exe `x4mp_ghost_tests`, ctest prefix `ghost.`)

API (namespace `x4mp::ghost`; no game calls, no SDK):
- `Sample{t_us (server time), sector, flags, pos (m), vel (m/s), rot Euler (rad), hull, shield}`; flag constants `kTeleport`, `kDocked`,
  ... `kHidden` (= bit 10, schema delta D1; change it in `sample.h` only if the generated schema differs).
- `Interpolator` (one per remote ship, ring of 8, fixed storage, **no allocation**): `push(sample, arrival_server_us)`,
  `apply_state(t_us, flags, hull, shield)` (flags-only Replication entries, e.g. Hidden), `RenderPose render(now_server_us)`,
  `delay_us()`, `stats()`. `RenderPose{state (Empty/Early/Interpolating/Extrapolating/Held/Stale), sector, pos, rot, flags, snapped,
  hidden, speed_mps}`. Driver rule: `snapped` or sector change -> place the object (`SetObjectSectorPos` into the new sector);
  `hidden` -> hide; otherwise just set the pose. `hidden` is also true for Stale (> 5 s without data).
- Rules: Hermite position + slerp rotation; render time = now - delay; delay 100 ms default, adaptive 80..250 ms (1.5 x interval +
  2 x p95 arrival jitter; up 100 ms/s, down 10 ms/s); extrapolation <= 500 ms then Held; Teleport / sector change / Hidden neighbour =
  discontinuity (hold the earlier sample, snap on reaching the new one); corrections > 0.5 m blend over 200 ms, > max(200 m,
  speed x 0.5 s) snap. Rotation is not blended.
- `ReplicationDecoder::decode(server_time_us, entries, count, sink(EntityUpdate))` keeps the per-net_id baseline (omitted fields = baseline)
  and turns entries into `EntityUpdate{has_pose, sample}`. `StreamSet` = decoder + one `Interpolator` per net_id:
  `ensure(net_id)` (spawn: allocates), `find`, `erase` (EntityDespawn), `ingest(server_time_us, entries, count, arrival_us)`.
  Call `decoder().forget/reset` when the server resets baselines (resume).
- `SyncStats` (inside each Interpolator): path error vs the sender's samples at the same server time (buckets all / steady < 300 m/s /
  fast), display latency, speed max; `stats().report(true)` every 5 s -> `format_sync_line(net_id, report)` is the `[sync]` line.
- `NetMap` (net_id <-> local id, kinds Self/Ghost/Avatar/Other), `GhostRegistry`, `AvatarRegistry`, bundled in `Registries`:
  `save(IStash&, epoch)` (one text blob, key `ghost.registries`) and `adopt(IStash&, epoch, is_valid)` -> `AdoptReport`. Adoption: valid
  ids kept, invalid dropped (listed in `dropped_local_ids`, nothing to remove), wrong epoch or unparsable blob -> nothing adopted and the
  blob is erased (the janitor cleans by name), registries reconciled so the three always agree. Call `save` after every change that
  matters (spawn, despawn, bind) or at least before unload; `is_valid` = `IsValidComponent`.

Numbers (relwithdebinfo, 60 s tracks at 20 Hz through the real Replication codec, 15 ms + 0..25 ms jitter, 5 % and 10 % loss; "true" =
rendered pose vs the analytic track at the represented server time, p95 / max in metres):

| track | true p95 | true max | `[sync]` p95 (worst 5 s window) | latency p95 |
|---|---|---|---|---|
| line 250 m/s | 0.00 | 0.00 | 0.00 | 140-152 ms |
| circle r1500 v250 | 0.01 | 0.07-0.19 | 0.05-0.09 | 144-153 ms |
| circle r500 v100 | 0.01 | 0.03-0.08 | 0.02-0.05 | 144-153 ms |
| circle r3000 v450 | 0.01 | 0.14-0.31 | 0.08-0.15 | 144-153 ms |
| accel 0 -> 450 m/s (20 m/s^2) | 0.01 | 0.01-0.02 | 0.02-0.03 | 147-151 ms |
| gate jumps every 12 s | 0.00 | 8.8-9.0 | 0.00 | 136-147 ms, exactly 1 snap per jump |
| worst case: 90 degree heading steps every 3 s | 2.8-3.3 | 12.6 | 6.5-6.6 | 152-159 ms |
| circle + 400 ms loss burst | 0.01 | 3.7 | 2.6 | 145 ms, no snap |
| circle, jitter up to 120 ms | 0.01 | 0.01 | 0.05 | 250 ms (delay rose to the 250 ms cap; legit cost of that jitter) |

Hot path: 7 players, decode + ingest + render + stats, 660 frames: **0 allocations**, ~2 us per frame (budget 200 us).

What M3-10 / M3-11 need to know:
- The interpolator works in **server time**; feed `arrival_us` = your clock-sync estimate of server now (used only for the delay
  estimate), and call `render(server_now)` once per frame per entity. Create the `Interpolator` (`StreamSet::ensure`) in the spawn path,
  never in the frame path; entries for net_ids you did not `ensure` are dropped by default (`only_tracked`).
- Euler convention lives in `math.h` (`R = Ry(yaw) Rx(pitch) Rz(roll)`); S13.4 decides what X4's `UIPosRot` angles are (degrees or
  radians, axis order). Convert in the game adapter, or change `euler_to_quat`/`quat_to_euler` once, nowhere else.
- Hidden (D1) arrives in the FLAGS field like any bit and is already honoured; `kHidden = 1 << 10`. D2 (`ManifestEntry.origin /
  controller_player`) does not touch Replication. If the schema lands another bit, only `sample.h` changes.
- Authority avatars (M3-11): build `Sample`s from the relayed `PlayerState` (it carries derived velocity, not Replication) and call
  `Interpolator::push`; there is no PlayerState decoder here yet.
- Flags-only Replication entries (no POS) only update the newest sample's flags/status (`apply_state`); velocity-only entries are
  folded into the baseline for the next pose.
- Not done here (belongs to the feature layers): spawn/despawn, `SetObjectSectorPos`, sector-index -> local sector id, the `[sync]` log
  call, the stash save timing, hide/show in game, the epoch value.
### M3-04: hostsim fake world + pair runner

- **GameFns** (`mod/native/game/game_api.*`): `kGameFnCount` is now **28**. Added `SpawnObjectAtPos2`, `ActivateObject`, `SetObjectSectorPos`, `GetObjectPositionInSector`, `TeleportPlayerTo`, `CanTeleportPlayerTo`, `SetComponentOwner`, `SetObjectForcedRadarVisible`, `IsSetaActive`, `GetObjectIDCode`, `GetComponentName`, `IsComponentWrecked`, `IsPlayerOccupiedShipDocked` (the player-guard inputs and `GetContextByClass` were already there). SDK-free `PosRotPod` mirrors `UIPosRot` (hostsim static_asserts the layout). Null-safe `GameApi` wrappers, all main-thread checked: `spawn_object`, `activate_object`, `set_object_sector_pos`, `object_position`, `teleport_player_to`, `can_teleport_player_to` (nullopt = export missing, `""` = allowed), `set_component_owner`, `set_object_forced_radar_visible`, `seta_active`, `object_id_code`, `component_name`, `component_wrecked`, `player_ship_docked`. **Wave-2 tasks do not touch `GameFns`/`kGameFnCount`** (§6 shared-file rule); add feature wrappers on top of `GameApi` in your own `game/<area>_api.*`.
- **hostsim world** (`mod/tools/hostsim/world.*`): see docs/hostsim.md "Fake universe commands". Object ids start at 400001; the player ship is 200001 (`player`), the dock station 300001, sectors 100001 and 100002 exist. The real DLL now sees non-null player-guard exports (`GetPlayerOccupiedShipID` = 0 while `seat off`).
- **Assumptions baked into the fake that S13 must confirm** (change them in `world.cpp`/`host.cpp`, not in the mod): angles are written in degrees by paths; `GetPlayerControlledShipID` stays set while docked (`world controlled-when-docked off` flips it, S13.9); `TeleportPlayerTo(allowcontrolling=true)` makes the target the player ship and sits the player in it, `CanTeleportPlayerTo` returns `""` when allowed; `SpawnObjectAtPos2` accepts any owner string and refuses only an empty macro, an unknown sector or a pending `world spawn-fail`.
- **expect-ghost** is parsed and validated but only evaluates when samples exist (`ghost-sample <player> <m>`); without samples it prints `STUB ... not evaluated`. **M3-10 must add the measurement** (feed `World::ghost_errors` from the ghost feature's test hook, or compare against the other process's truth) and can then make the no-samples case a failure.
- **Pair runner**: `mod/tests/hostsim/pair_run.ps1` (ports 47940-47942, ~20 s) starts the server, a FakeNode authority with a dummy save and two hostsim processes (Pia, Pax) running `pair_scenario.hostsim`; the two processes synchronise through `write-file`/`expect-file` in a shared `${sync}` folder (the tail of the scenario) and through `expect-admin` on the other player. CI: step `HostSimPair` in `tools/e2e.ps1` and in the `e2e-headless` job. Copy `pair_scenario.hostsim` (pass `-Scenario`) for ghost, avatar and own-ship scenarios; the runner provides `tcp`, `name`, `other`, `sync`.
- Existing tests that counted exports (`test_game_api.cpp`: 15/14) were updated to 28/27.

**Sitting-0 live facts so far (2026-10-03, lead):** S13.5 seat: `GetPlayerOccupiedShipID` 0->ship at sit-down, ship->0 at stand-up; while standing,
`GetPlayerContainerID`/`GetPlayerObjectID` = the ship (so the ship is known before the player sits). S13.6: takeover from standing works
(seated 11 ms after `TeleportPlayerTo(force)`, guard 90 ms), docked works too (107 ms / 623 ms); vacated original removed, no Game Over.
**`CanTeleportPlayerTo` returns `"granted"` when allowed** (not `""`: fix the hostsim fake and any wrapper). **A spawn 300 m ahead of a docked ship
landed inside the station** (clipping until the player flew out): avatar/ghost spawn positions need a clearance check (MD `get_safe_pos` or
the station's undock point) - M3-11 brief. `SpawnObjectAtPos2` default equipment is high-end (see user note at the top).
