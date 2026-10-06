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
**M3-01 -> wave 2 (protocol deltas + server gaps, as built):**
- **Schema (append-only, protocol stays 0.1).** `StateFlags.Hidden` (bit 10, `0x0400`, D1); `ManifestEntry.origin:EntityOrigin` and
  `ManifestEntry.controller_player:ushort` (D2); one more append the plan did not list: `PlayerInfo.online:bool = true`. The C++ FlatBuffers
  code is **not committed** (flatc generates it at build time), so nothing to regenerate there; the golden vectors were regenerated
  (`0x0101_RosterUpdate`, `0x0300_PlayerState`, `dgram_mixed`, `index.json`) and `protocol/cpp/build.ps1` passes.
- **Settings (D4).** `Avatars.StarterShipMacro` (default `ship_arg_s_fighter_01_a_macro`) and `Avatars.SpawnOffsetMeters` (default 300, 50..5000)
  are Live, `PushToNodes`, so they reach every node as `ServerSettingsUpdate` entries (text values), right after `Welcome` and on every
  change. They are **not** fields of the `SessionSettings` table. `AvatarOptions.ResolveStarterShipMacro(teamId, race)` is the single
  server-side place the ship macro comes from (Q5: later per faction/race); the mod should keep one equivalent function that reads the pushed value.
  **`Avatars.StarterLoadout`** (user note: early-game equipment only, never the `SpawnObjectAtPos2` default Mk2/Mk3 parts; vanilla loadout id, empty = the mod picks a basic
  early-game loadout) is carried the same way and resolved by `AvatarOptions.ResolveStarterLoadout(teamId, race)`, next to the macro.
  The authority spreads avatar slots between the offset and twice the offset (the server only carries the number).
- **Parked avatars (Q6).** The server never removes an avatar on leave. The authority sends `EntityChange{fields=Controller, controller_player=0}`;
  the mirror keeps the entity, `IsPlayerShip` stays true because **`origin` must be `PlayerShip`**, so it stays replicated galaxy-wide to everybody
  except its owner. Authority requirements: avatars carry `origin=PlayerShip`, `owner_player=<player>` and `name` = the player's name
  (the roster entry is removed when the player leaves, so offline ghosts must be labelled from `EntityRecord.name`, not from the roster).
  The node of a leaving player is gone, the roster shows `removed`; the client derives "(offline)" from `controller_player == 0`.
  On rejoin answer `PlayerShip` with a refreshing `EntitySpawn` (same net_id) or with `EntityChange{Controller=<player>}`: both bind
  `PlayerInfo.ship_net_id` again. `WorldMirror.ParkedAvatars` / `AvatarCount` list them (GUI map shows them dimmed, "(parked, offline)").
- **Own ship never in `Replication`.** Rule (`ReplicationMath.IsOwnShip`): `controller_player == me`, or `controller_player == 0 && origin == PlayerShip
  && owner_player == me` (the rejoin window before the authority re-sets the controller). The client still gets the `EntitySpawn` of its own avatar
  (that is how it learns its id), just no state entries. Tests: `ReplicationModuleTests` (parked, piloted, foreign).
- **Held `PlayerShip` requests.** Real bug fixed: a *resumed* authority never got the held requests because `ResumeSlot` restores the InGame phase before the
  node is attached, so `Authority` was still null at the phase callback. `RelayModule.OnNodeAttached` now forwards them (also those that may have been lost
  with the old socket). Requests are re-sent after every authority resume until the avatar spawn arrives, so **the authority must be idempotent**:
  same `player_id` returns the existing avatar. Tests: `RelayAvatarTests`.
- **Roster `online`.** `PlayerInfo.online=false` is broadcast when a node loses its socket and waits in the resume grace, and `true` again on resume.
  A planned `ClientReload` stays invisible (every join does one). `online` is not about avatars: a left player is `removed`.
- **Admin removal on kick/ban.** `POST /players/{id}/kick` takes `removeAvatar`, `POST /bans` takes `removeAvatar` (GUI: checkbox in both dialogs; kick also
  works for an offline player when the box is set). `IAvatarControl.RemoveAvatarsAsync(playerId)` drops every `PlayerShip` entity owned or piloted by the player
  from the mirror (nodes holding it get `EntityDespawn{Removed}` from the interest manager) and sends the **authority** `EntityDespawn{entries=[{net_id, reason=Removed}]}`
  for each one. This is a new direction for an existing message: **M3-11 must handle an inbound `EntityDespawn` on the authority** by removing the avatar
  from its `AvatarRegistry` through `SafeRemove` (never the player's own ship). In-game `AdminCommand` Kick/Ban are not implemented on the server at all
  (M5/M6), so `KickCmd`/`BanCmd` did not get the field.
- **GUI.** `PlayerLiveDto` gained `sectorId`, `sectorName` (galaxy metadata, null until it arrived), `position` (metres in the sector) and `shipNetId`
  (Players list column "Sector", detail page "Sector"); `GalaxyPlayerDto.online` (false for a detached node and for parked avatars, which are now listed on the map).
  `NodeSnapshot` and `SessionNode` carry the pose of the last `PlayerState` (`PosX/Y/Z` in 1/64 m).
- **For FakeNode (M3-05).** Answer `PlayerShip` with `origin=PlayerShip`, `owner_player`, `owner_team`, `controller_player` and `name`; on leave send the
  `Controller=0` change; handle the server's `EntityDespawn` order. `Avatars.*` arrive in `ServerSettingsUpdate`.


### M3-03 UDP realtime lane (mod `core/net`, `mod/native/core/net/udp_{types,lane}.h`, tests `mod/tests/test_udp_lane*.cpp`, ctest `core.udp*` and `udp.*`)

What it does (Q4): after Welcome the session calls `HookContext::start_udp(conn_id, udp_port, udp_token)`; the net thread opens a connected,
non-blocking UDP socket to the TCP peer's address and runs `UdpLane`: `UdpHello` every 250 ms until `UdpHelloAck` (Binding), then Realtime frames
go out as datagrams (batched, <= 1200 bytes, one message never spans two) with the ack/ack_bits of the server's datagrams (that is what drives
the server's replication baselines); bare-header ack after 50 ms of silence with unacked inbound; a `UdpHello` keepalive after 1 s of silence.
Fallback to TCP (state `Fallback`, no disconnect) when there is no ack within 3 s of binding, or when something we sent that expects an answer
is unacknowledged and the server's ack field has not advanced for 3 s (so a path that dies is noticed within 3 s whatever the traffic pattern;
measured 2.97 s live). Re-probe every 30 s (3 s of hellos). Inbound sub-messages are checked with `validate_frame` (the Verifier) and go through
the same `handle_frame` path as TCP frames (frame hook, Ping/Pong, inbox). Capability `UdpRealtime` (bit 0) is OR-ed into `client_caps` by
`Session::start` unless `NetOptions::udp.mode == Off`.

API for the next tasks:
- `NetOptions::udp` (`UdpOptions`: `mode` Auto|Force|Off, `loss_pct`, `block`, `seed`, timers); `NetClient::set_udp_block(bool)`,
  `Session::set_udp_block(bool)`; `NetStatus::udp` (`UdpStats`: state, datagram/frame/byte counters, simulated drops, malformed, duplicates, binds,
  fallbacks, reprobes, `acked_seq`, `rx_loss_pct` = what the GUI's `NodeStats.udp_rx_loss_pct` wants). **M3-09/M3-05 wiring:** the session/host feature that
  fills `NodeStats` can set `udp_active = status.udp.state == UdpState::Active` and `udp_rx_loss_pct = status.udp.rx_loss_pct` (not done here:
  the host layer is not mine).
- Senders keep calling `Session::send(Lane::Realtime, type, payload)`; the lane takes the frame when Active and the type is allowed on UDP
  (Ping, Pong, UdpHello, UdpHelloAck, Replication, WorldUpdate, EntityStatusBatch, PlayerState) and it fits a datagram, else it goes over TCP
  exactly as before. `Force` mode (tests only) drops instead of using TCP.
- The datagram codec is the existing `x4mp/wire.h` (golden-tested against the C# `DatagramCodec`); the new part is the C++ `UdpReceiveWindow` (port
  of the server's `DatagramReceiveWindow`).
- Server-side limits worth knowing: the server keeps sending Realtime frames over UDP while it considers the node bound, even after the mod fell back
  (the mod keeps accepting and acking them); the server's `UdpHelloAck` header acks the datagram *before* the hello, which the lane's
  "ack field advances" rule is built around.

Failure injection / flags (`x4mp-headless`): `--udp auto|force|off`, `--udp-loss PCT`, `--udp-block`, `--udp-block-after SEC`, `--udp-keepalive-ms N`
(steady probe stream), `--udp-seed N`, `--expect-udp active|fallback|off` (also requires the TCP connection never dropped; with
`--udp-block-after` the fall-back time must be <= 3.5 s). ctest `smoke.headless_udp_real_server` (skipped without `X4MP_TEST_SERVER`).

Tests: Catch2 `core.udp*` (window incl. wrap, header/sub-message layout, batching and the 1200-byte limit, refused datagrams, bind/hello timing,
3 s fallback and 30 s re-probe, path dying while bound, idle keepalive, 5 % loss both ways for 60 s of simulated 20 Hz traffic, Force/Off,
block switch) and `udp.no allocation per frame once bound` (own exe `x4mp_udp_alloc_tests`, reuses `tests/ghost/alloc_counter.cpp`).
Live: `mod/tests/hostsim/udp_lane_run.ps1` (needs `moduild.ps1` + `tools\e2e.ps1 -Steps Publish`; ports **47920-47922**, 43 s): udp_force,
udp_loss5 (rx loss 4.7-5.4 %, no fallback), udp_blocked (Fallback within 3 s, connection up), udp_block_after (Fallback 2966 ms after the block,
connection up), udp_off. Verified: `mod/build.ps1` 305 ctest green; `e2e.ps1 -Steps Publish,HostSim,JoinFlow,ReloadSurvival` green.

**CI (not done here, M3-04 owns `tools/e2e.ps1` and `ci.yml`):** add an e2e step `UdpLane` that runs `mod/tests/hostsim/udp_lane_run.ps1` next to
HostSim (same pattern as `Invoke-HostSimScript`, ~1 min, Windows only). The Catch2 tests need nothing (they are in `x4mp_core_tests` / `x4mp_udp_alloc_tests`).
### M3-05 FakeNode for M3 (2026-10-03, branch worktree-agent-ad3bc793b2ba07f02)

Done as briefed; user guide in [fakenode.md](fakenode.md) ("Avatars, wingmen, chat echo and the galaxy file"). What the others need to know:

- **Wire behaviour the mod / M3-01 / M3-09 can rely on.** The fake authority answers every forwarded `PlayerShip` with `StringTableAdd` (new macro and `x4mp_team_k` faction)
  then `EntitySpawn{origin=PlayerShip, controller_player=<requester>, owner_team, owner_player, macro_ref=real macro, name="[MP] <player>"}`, 300 to 600 m from the host
  ship, same sector. The first request also self-spawns the **host ship** (same macro/sector/position as the request, `controller_player` = the authority's player id, name
  `[MP] Host`). Same player asks again -> same avatar and net_id (controller restored, last relayed `PlayerState` pose). Leave (`RosterUpdate.removed`, or absent from a full
  roster) -> `EntityChange{Controller=0}`. The fake authority has the Authority role only, so it sends no `PlayerState` for the host ship.
- **Where a request comes from in FakeNode.** Opt-in: `--avatars` / `--wingman` (otherwise bots behave as in M1/M2: no `PlayerShip`, net_id 0). Every bot says it stands at the same
  "host stand" (`FakeAvatarFlow.HostStand`: first sector with a gate, (1500, 0, -1200) m) in the macro of `--host-ship-macro`.
- **`--galaxy-file`** reads exactly the sitting-0 JSON (`format`, `sectors[{macro, cluster, gates[]}]`; tolerant: missing `cluster`/`gates`, unknown gate targets ignored). It has no
  gate positions, so links are plain gates with the generated deterministic positions (about 18 km out); sector names/owners/map positions are synthetic. Index = ordinal rank of the
  macro, as the mod does. Fixture: `server/tests/X4MP.FakeNode.Tests/Fixtures/galaxy-dump-small.json` (synthetic, 7 sectors, one isolated).
- **Schema D1/D2 and `Avatars.*` settings are not used.** The code is written against the schema as it is today. Rebase touch points when M3-01 lands: (1) `FakeAvatars` could read
  `Avatars.StarterShipMacro` / `SpawnOffsetMeters` from `SessionSettings` instead of `--avatar-macro`/`--avatar-offset` (one place: `FakeAuthorityOptions.Avatars` built in
  `LiveRunner.Session.cs` `RunAuthorityAsync`); (2) a `Hidden` flag could be sent by a bot through `PlayerStateT.Flags` (nothing sends it today); (3) the manifest entries for avatars
  (D2) are not produced (`FakeAuthoritySaves` untouched).
- **Own ship and the interest set (M3-01 audit).** Today the server still puts a node's own avatar into its interest set (the `InterestChecksum` counts it) but sends **no
  Replication entries for it**. `FakeClientSession` copes with both worlds: the own avatar is a ghost exempt from the stale check, and when a checksum only matches without it the
  session drops it for good (`OwnShipExcludedByServer`). If M3-01 stops sending the avatar's `EntitySpawn` to its owner altogether, `--avatars` bots will time out after 10 s
  ("no avatar within 10 s of the PlayerShip request"): the answer spawn to the requester is part of the takeover flow (4.3 step 1), keep it, or tell me and I switch the bots to
  learn the net_id from `RosterUpdate.ship_net_id` instead.
- **Measured (loopback, this machine, run next to other builds):** 3 wingmen + 1 "real" bot, over about 15 s: Near rate of every player stream 19.1 Hz (p50 gap 47 ms, p95 77 ms), a
  120 s explore run through the 4-sector fixture with gate jumps: 19.8 Hz, `follow-jumps=2`, `position-errors=0`. Under load single streams dipped to 16.7 Hz, so
  `AvatarLiveTests.WingmenFly...` asserts median >= 18 Hz and each >= 15 Hz.
- **Chat.** `FakeClientHandle.SendChatAsync` for tests; `--chat-echo` bots answer All/Team/Whisper (whisper back to the sender only), never an `echo: ` message.
- **Tests added.** FakeNode.Tests: `AvatarTests` (see the files), `GalaxyDumpTests`, `WingmanChatSyncTests`; Server.Tests `Net/AvatarLiveTests` (6 live: wingmen + Near rate, avatar
  round trip with host ship, resume without duplicates, leave -> parked and still replicated, chat echo incl. whisper privacy, galaxy file swarm with `--verify`).
  `tools/e2e.ps1 -Steps Publish,Swarm,AuthorityFlow` green; `packages.lock.json` untouched.
- **Not done / deliberately small:** no UDP-specific avatar behaviour (the bots use whatever lane `--udp` gives); wingmen do not avoid collisions with each other; the avatar flow is not
  re-run after a client resume (the server keeps the binding; `FakeClientSession` keeps `OwnAvatar` across `ResetForResume`).

### M3-07 remembered Join fields in `x4mp.json` (`core/config/remembered.{h,cpp}`, `join_feature.cpp` verb block, `x4mp_menu.lua`, tests `test_remembered.cpp`, `hostsim.remembered_fields`, Lua `test_screens.lua`)

- **Storage**: `last_address`, `last_name` in the user file (`ctx.paths->user_file`). `config::update_string_keys` is the atomic writer (temp `<file>.tmp` +
  rename, ordered_json so unknown/user keys and their order survive, no BOM, refuses `pass|pwd|secret|token` keys, does not touch a file that is not a
  JSON object). `apply_remember` (migrate mode fills only missing fields), `read_remembered`, `parse_remember` (ignores any password field, strips control
  chars, caps address 255 / name 64), `make_remembered_json`. `last_*` are known keys in `config.cpp` (no unknown-key warning).
- **Bridge** (own marked blocks in `x4mp_bridge.lua`): verb `x4mp.remember` `{"v":1,"address","name","migrate"?}` (Lua -> native, sent by `S.submitJoin` before
  `x4mp.join`, only for a valid form); topic `x4mp.remembered` `{"v":1,"address","name"}` ("" = none), raised after each remember and with every
  `ui_ready`/`request_status` answer. `B.remembered` holds the last payload. I used a separate topic instead of adding fields to `x4mp.status` (status is
  throttled and rebuilt from session state; the remembered fields are independent of it).
- **Lua** (`x4mp_menu.lua`): form pre-fill and the "Last server" line read `B.remembered` first (log source `config`), then the legacy `__X4MP_USER`
  (source `saved`). A topic arriving after the form was built fills only empty fields and redraws. Migration: once per Lua state, if native has no value for a
  field the legacy value exists for, send `remember` with `migrate:true`; a field native has makes Lua drop its legacy copy (other saved-variable keys such as
  `hudMode` stay). Nothing new is ever written to `__X4MP_USER`.
- **Not done**: `last_host_role` (the plan row mentions it). The host-as-authority toggle is not remembered today (`S.onClosed` resets it, and it needs an
  admin password each time), so remembering it would change behaviour; add the key in `remembered.*` if wanted.
- Tests: Catch2 `core.remembered:*` (9 cases), hostsim `remembered_fields` (migration once, override, bad input, restart survival, `expect-no-secret` for the
  password / admin password / the second legacy address), Lua `test_screens.lua` (6 new/changed). Lua tests were run with Python lupa (Lua 5.5): one
  pre-existing failure there (`unpack` in the options-menu adapter test) is a Lua 5.5 vs 5.1 difference, not this task.


### M3-06 chat + player list (`ui/x4mp_chat.lua`, `ui/x4mp_players.lua`, `mod/native/features/chat/**`)

What is built (contract rows in docs/mod-design.md section 7.1):
- **Native `features/chat`** (`chat_json.*` SDK-free, `chat_feature.*`): verb `x4mp.chat_send` -> `ChatSend` (Control lane, through `diag_hub().send_control`, so it only
  works while welcomed; otherwise a local "not connected" system line). Inbound `ChatMessage` / `RosterUpdate` reach the feature through **one hook in
  `join_feature.cpp`** (`chat::chat_hub().on_frame_message(...)` in the `K::Frame` case, `session_ended()` in `stop_session`); the chat feature, registered after
  join, raises `x4mp.chat` (one batch per frame) and `x4mp.players` (changes at most 2 Hz, a join/leave at once). `ui_ready` re-sends the last 50 messages (`replay:true`) and the
  table, so `/reloadui` loses nothing. `RosterTracker` produces join/leave events (not for the own id, not for the baseline roster, `online` flips count). Other M3 tasks that
  want roster data (ghosts, avatars) can read `chat::chat_hub().roster()` instead of parsing `RosterUpdate` again.
- **Lua `x4mp_chat.lua`**: wraps `OnlineGetChatMessages` (previous result + our ring, merged by time), `OnlineSendChatMessage` (plain line, userid 0/nil -> session; a Ventures
  private tab, userid > 0, passes through) and `ExecuteDebugCommand` (`/t text` = team, `/w name text` = whisper; **every other command passes untouched**). The vanilla window sends
  `/cmd rest` as `ExecuteDebugCommand("cmd", "rest")`, which is why `/t` and `/w` live there. Wrappers are only in place while the status is `ingame`/`save_changed`, are re-checked on
  `gfx_ok`, `show` and every status/chat/players topic (a replaced or re-wrapped global gets a new wrapper on top; a re-entrancy guard keeps a double wrap from duplicating lines or
  sends), and `uninstall` restores the previous function only where ours is on top (otherwise the wrapper stays, inert). Author = team-colour escape + name; own lines carry the
  `OnlineGetUserName` user id; `isprivate` is always false (the window's private tabs expect Ventures groups). Chat refresh = `ChatWindow.onChatMessageReceived()` when the menu is shown;
  with the window closed an incoming line from another player raises one HUD toast (`X4MPChat.toast = false` turns it off).
- **Lua `x4mp_players.lua`**: the table (`X4MPPlayers.rows(rows)`, one line per player: team-colour name, team, online/offline/joining, ping, sector) is added to the Multiplayer
  screen by **one line in `x4mp_menu.lua` `buildMain`** (while connected) and redrawn on a change when the main screen is open (never the join form). Join/leave = HUD notify cue + a system
  line in the chat window. Team colour: `Color.faction_x4mp_team_<n>` when M3-08 provides it, else a fixed palette. Sector: `X4MPPlayers.sectorResolver(index)` hook (M3-09/10 can set it
  once `GalaxyMetadata` maps sector index -> macro/name); until then "sector <index>". Texts: page 92000 ids 500-529 (inserted after id 60 in `t/0001-l044.xml`).
- Tests: Catch2 `host.chat:*` (7), Lua `test_chat.lua` (23) and `test_players.lua` (10) with vanilla-shaped and SirNukes-shaped globals (replace-wholesale, wrap-on-top, unwrap only if ours,
  `/` passthrough, replay after /reloadui), hostsim `join_flow.hostsim` (player table, chat all/team/whisper-to-nobody round trip through the real server, ui_ready replay).
  No local Lua here: `python mod/tests/lua/run_lupa.py` (pip install lupa) runs the same suite on Lua 5.1; CI still uses `lua5.1`.

Things the next tasks / the in-game test should know:
- **Not verified in game** (V23 rest): that the vanilla window shows our lines outside Ventures, that the embedded colour escape in the author really colours the name (the spike S13.12 result is
  not recorded in this repo; `x4mp_chat.lua` builds on the spike's approach), and that `menu.shown` is true while the window is faded. Fallback if it fails: a chat panel in the Multiplayer window.
- Clicking another player's name in the chat window opens the vanilla context menu (contact / report) that targets Ventures users; our `authorid` is negative so it never matches a real user. Untested live.
- `ChatMessage.from_player` has no team; the sender's team comes from the roster (0 until the first roster arrives). The server echoes a sent line to the sender, so there is no local echo; a server
  refusal (mute, rate limit, "not on a team", "not online") arrives as a `system` line from "server".
- Chat text is user content: Info log lines carry channel and byte count only (`chat: sent channel=all bytes=N`), Debug a 40 character cut. FakeNode `--chat-echo` (M3-05) was not on main
  when this was built: the round trip is covered by the sender receiving its own line from the real server.
- `x4mp.players.sector` is the raw `PlayerInfo.sector` index (ushort); `ship` is `ship_net_id`.

**User design decision (2026-10-03, sitting 0): SETA is unavailable in multiplayer, always.** No per-server option. One player in SETA makes
the session run badly and causes problems later (clock, replication, economy). M3-09: block it at the source where the game allows (disable/hide
the SETA activation while connected), and keep the detect-and-switch-off within 1 s as the safety net (S13.9 not tested in sitting 0: the user
has no SETA item; test later). Sitting-0 S13.4 facts: angles are radians; positions sector-local; the highway context is readable (local and
super highway), the sector stays valid through superhighways; docked and on-foot detected; a gate jump is a ~3.5 s frame gap; pose read ~1-2 us.

### M3-08 team factions (`features/teams`, `libraries/*`, `md/x4mp_teams.xml`, `ui/x4mp_teams.lua`, tests `test_teams.cpp`, `test_teams.lua`, `join_flow.hostsim`)

What is built:
- **Libraries** (`extension/x4mp/libraries/`): `factions.xml` diff adds `x4mp_team_1..8` (`active="0"`, `tags="nodiplomacyselection"`, the spike S13.7 entries; plus a small `<relations>` block: criminal -0.5, holyorderfanatic/khaak/xenon -1, so team ships are not free targets; **no licences**, team factions do not trade or build before M5) and `colors.xml` (`x4mp_team_<n>_glow` + `faction_x4mp_team_<n>` mappings: orange, cyan, yellow, magenta, white, purple, green, pink). Names on text page 92000 ids **811..884** (= 800 + team*10 + 1 name, 2 description, 3 short, 4 prefix; placeholders "X4MP Team N", the UI shows the server's team name). `Color.faction_x4mp_team_<n>` now exists for M3-06's player table / chat author. Both diffs and the MD script pass `tools/validate-x4-xml.py --check-properties`.
- **Native** (`features/teams`): `team_plan.*` (SDK-free) keeps the team model from `Welcome` (full table + matrix + own team), `TeamTable`, `TeamRelations`, `TeamMemberChanged` and maps it to a **Plan** (slots + relation triples: team pairs from the matrix Allied +0.75 / Neutral 0 / Hostile -1.0 / default; `player` <-> own team +1.0, `player` <-> other team = matrix(own, other); **no own team = no player triples**) and to the ordered MD call list `build_calls()` (unlock all, activate + known, relations both directions, lock all; `player` is never locked). `team_hub.*` is the apply state machine (`team_hub()` singleton, fed by **three hook lines in `join_feature.cpp`**: `on_welcome`, `on_frame_message`, `session_ended`, next to M3-06's chat hooks). `TeamsFeature` (registered after join, before stats) polls the hub every frame and raises `x4mp.teams_apply` (JSON plan) -> `ui/x4mp_teams.lua` -> `AddUITriggeredEvent("X4MP_Teams","apply",<flat list>)` -> `md/x4mp_teams.xml`, which applies the plan, **reads it back** and answers `R;seq;active;mismatches;player_locked;slots;relations` (or `E;seq;reason`) -> Lua -> verb `x4mp.teams_md` -> hub. The plan is (re)sent on every new universe epoch (universe ready), on every Welcome (join / resume, always), and **in the same frame as a TeamRelations/TeamTable/TeamMemberChanged frame that changes the plan** (a change that does not alter the plan, e.g. a pair of unused teams, sends nothing). A stale report (old seq) is ignored; no MD report within 8 s of frame time = `Failed` ("no_md_report"), a late right report recovers; Failed raises one `x4mp.notify` error.
- **State**: `team_setup_state` in `NodeStats` (FeatureState: 0 unknown = no session / nothing to do, 2 starting, 3 ok, 4 failed; added to `stats_message.*` and `stats_feature.cpp`). Self-test check **`team.factions`**: SKIP without a session or before universe ready; FAIL when `GetAllFactions` lists no `x4mp_team_*` (library diff not loaded) or the setup failed; WARN while starting; PASS when ok.
- Tests: Catch2 `host.teams*` (15: model from welcome/deltas, matrix -> plan -> call order, own-team moves, garbage, hub: waits for the universe, applies once, same-poll re-apply on a TeamRelations delta, no-change = no send, new epoch, resume Welcome, reports ok/fail/stale/timeout/late, retry), Lua `test_teams.lua` (7, in `run_all.lua`), hostsim `join_flow.hostsim` (real DLL + real server: plan applied at universe ready, simulated MD report -> state ok, `SELFTEST PASS team.factions`). The two-team matrix change itself is covered by the hub tests (hostsim has no admin write call).

What M3-11 / M3-10 need:
- **A team's faction id**: `x4mp::features::teams::team_hub().faction_of_team(team_id)` returns `"x4mp_team_<slot>"` (empty when the team is unknown or has no slot). A player's team id comes from the roster (`chat::chat_hub().roster()`, `PlayerRow.team`) or `Welcome.team_id`. The ids are fixed by the slot, so `SpawnObjectAtPos2(macro, sector, pos, "x4mp_team_k")` / `SetComponentOwner(id, "x4mp_team_k")` can use the string directly.
- **When the factions are ready**: `team_hub().factions_ready()` (state Ok) = MD activated the plan's factions in **this universe** and the relations read back right. It goes back to `Starting` on every re-send (universe ready, Welcome, a matrix change) and normally returns to Ok within a frame or two. Spawn / own avatars only while `factions_ready()`; before that an owner string of an inactive faction falls back to "ownerless" (spike S13.1 note). `team_hub().applied_plan()` is the last plan sent (the slots of the session). A team created later by the admin arrives as a `TeamTable` delta and is activated by the re-send in the same frame as the delta.
- Team factions stay active when the session ends (nothing deactivates them). `player` is never touched except `set_faction_relation` player <-> team as the plan says.

Not verified in game (add to the sitting list): (1) Lua array -> MD list via `AddUITriggeredEvent` (vanilla does it with `{a, b}` tables and reads `event.param3.{1}`; the spike only passed scalars); (2) `typeof $P == datatype.list`; (3) the effect of the `<relations>` block (xenon -1 etc.) on team ships and of `tags="nodiplomacyselection"` (S13.7 ran without the block); (4) activating teams in a fresh universe and re-sending on a matrix change while locked (the spike proved unlock-set-relock once, not repeatedly); (5) **Q9**: a save written with active team factions loaded **without** the mod. Deferred: `set_faction_diplomacy_exclusion` (a per-pair call; the tag already keeps the factions out of the diplomacy target list; ADR-047 treaties are M6), team <-> NPC relation copy from `player` (M6 reputation sync), inherit-team re-own (M5), `set_faction_identity` rename (V25). Shared-file touches: `ui.xml` one line (`x4mp_teams.lua`), three hooks in `join_feature.cpp`, one line each in `feature_list.cpp`, the self-test check and the stats field.
### M3-09 own ship: `features/selfship`, `game/selfship_api.*`, `features/authority` seat wait, MD/Lua (2026-10-03)

What is built (all frame-thread; the per-frame path allocates nothing, Catch2 `selfship.*` in its own exe proves it with 20 000 frames):
- **`features/selfship/own_ship_tracker.*`** (pure): `Observation` in, `Tick{edge, seated, ship, StateOut, blocked}` out. Seat edges `SatDown / StoodUp / ShipChanged`
  from `GetPlayerOccupiedShipID` (S13.5); the ship is known while standing (container is a ship). **Rates:** 20 Hz moving, 5 Hz idle (< 1 m/s and < 0.05 rad/s over a 250 ms
  window), 1 Hz while Hidden; IMMEDIATE on a sector change, teleport, ship change, the first state after sit-down, a change of Hidden/Docked/InHighway. The schedule catches up
  (exact average rate at 20..144 fps) and never bursts after a gap. **Flags:** `PlayerControlled` always, `Docked`, `InHighway`, `Hidden` = Docked or InHighway, `Teleport` on
  sector change / ship change / sit-down / link up (`force_resend`) / a position jump > 2 km + 15 km/s x frame time. **Standing up sends ONE Hidden state and then nothing**
  (no OnFootState before M3b; the plan's "1 Hz while hidden" applies to docked / highway only). Angles go out exactly as the game gives them (reads are radians, S13.4; writes to the game are degrees, see M3-15); positions are
  sector-local metres; quantised with `x4mp::wire::quantize_*`.
- **Sector index** = the 1-based rank of the sector macro in the **ordinally sorted** macro list (protocol.md 8.2). `features/authority/build_plan` now sorts the same way (it used MD's
  enumeration order before; the server never depended on it), so authority and clients agree without a table on the wire. `features/selfship/galaxy_map.*`: macro <-> UniverseID <-> index
  for ANY node (authority included), built from md/x4mp_galaxy.xml control `map` (new cue `X4MP_Galaxy_Map`: per sector a tag event with the macro and an event with the sector
  component), converted by `ui/x4mp_authority.lua` (`ConvertStringTo64Bit`, chunks of 40, verb `x4mp.sector_map` `S;macro|id;...` / `E;n`). Asked at universe ready and every 10 s until
  complete. **M3-10 needs the reverse lookup: `GalaxyMap::universe_id_of(index)`.** The map lives inside `SelfShipFeature` today; if you need it, read it through the hub (add a
  `const GalaxyMap*` to `SelfShipStatus`/hub, or move the map to the hub: one line) rather than building a second one. Lua accepts a table param `{macro, sector}` too, in case MD lists
  arrive as tables. Not verified in game: that `ConvertStringTo64Bit(tostring(<sector component>))` works for sectors (it does for ships, S13.7); the hostsim feeds the map by script.
- **PlayerState** goes out through the join feature's link (`selfship_hub().set_link`, installed while the node is InGame): `Session::send(Realtime, ...)` = UDP when the lane is
  Active (M3-03), TCP otherwise. `sample_time_us` = steady clock + the applied clock offset (not sent while the clock has no sample). `net_id` = `selfship_hub().own_net_id()`
  (0 until M3-12 sets it; the server stamps it anyway); hull/shield 255; no target. State is sent only when the sector map is ready, the sector is in it and the pose is readable
  (otherwise a rate-limited `[selfship] no state is sent: ...` warning).
- **Authority self-spawn on the seat** (REPLACES the 6-ask retry): `AuthorityFlow::note_seat(seated, edges)` is fed every frame from the hub by the join feature. When the checkpoint
  is stored and MD had no ship, the flow waits; it asks MD once (`x4mp.auth_collect {"ship_only":true}`) when the player sits (immediately if they already sit), never while they stand,
  never on a timer. "MD says none although the game says seated" re-asks after a frame-counted back-off (20 frames, doubling to 600). The spawn still carries the game time of that moment.
  `authority_flow.hostsim` now stands for 900 frames, asserts no ask, sits, asserts exactly one ask, answers none, then the ship.
- **SETA (unavailable, always).** Source: native tells Lua/MD `x4mp.seta_block {on}` while connected (also after every universe/reload; MD flag `md.$X4MP_SetaBlock`, reset on game load);
  MD cue `X4MP_Seta_Guard` listens to the vanilla start signal (`player.entity`, `startactivity`, `activity.seta`, md/modes.xml Mode_SETA) and answers with `stopactivity` (+ `toggle_timewarp`
  200 ms later if still on) and reports `x4mp.md_seta` -> verb `x4mp.seta_blocked` -> notification. The input itself cannot be removed from Lua (the toggle is an engine action, the
  menu action calls the FFI `StartPlayerActivity`), so "at the source" = revert at the same MD event. Safety net: `SetaGuard` polls `IsSetaActive` every frame while connected, requests
  the switch-off at once (`x4mp.seta_off` -> MD cue `X4MP_Seta_Off`: `stopactivity`, 300 ms `toggle_timewarp`, 300 ms `set_timewarp_factor`, each only while `player.timewarp.active`),
  repeats every 500 ms, and shows `x4mp.notify` "SETA is disabled in multiplayer" (max one per 5 s). **Not tested in game (S13.9 still open: the user has no SETA item):** whether the
  `stopactivity` signal ends a started SETA (vanilla uses it for the tea egg) and whether `toggle_timewarp` is needed; the XSD/property check passes.
- **Topics for the UI/tests:** `x4mp.selfship` (<= 1 Hz, immediate on a seat edge) `{seated, ship, sector, sent, rate_hz, immediate, teleports, seat_edges, flags, hidden, docked, map_ready,
  seta_detections, seta_requests}`; hostsim `expect-lua x4mp.selfship json $.rate_hz > 17` etc. (see `tests/hostsim/pair_scenario.hostsim`, block "M3-09").
- **What M3-10/11/12 consume:** `selfship_hub().status()` (`seated`, `ship` = the own ship's local UniverseID, `sector` index, `seat_edges`, `sit_downs`, `map_ready`) and
  `selfship_hub().set_own_net_id(id)` (M3-12: the avatar's net id for PlayerState). A *sit-down* is the trigger M3-12 can use for its "I am in the avatar" check; the hub status is
  updated before the join feature runs in the same frame (SelfShipFeature is registered before JoinFeature). Highway: local and super highways both set `InHighway` AND `Hidden`
  (they cannot be told apart natively); docked inside vs on a pad likewise sets `Hidden`. M3-10 may choose to render `InHighway` ghosts if it finds a way to tell local ones.
- **hostsim changes:** fake paths now write **radians** into the fake world (reads are radians, S13.4; the game's *write* exports take degrees, M3-15), `highway on|off` command, `world_ship.hostsim` expectations updated.
  The e2e pair scenario got the M3-09 block (map, 20 Hz, idle 5 Hz, gate teleport, dock Hidden, highway Hidden, SETA switch-off + notification, stand-up silence).

### M3-11 avatars on the authority (`features/avatars/**`, `game/avatars_api.*`, `md/x4mp_avatars.xml`, `ui/x4mp_avatars.lua`, tests `test_avatars.cpp`, `test_avatars.lua`, `tests/hostsim/avatars*`)

What is built (design: the header comments of `avatar_director.h`, `avatar_plan.h`):
- **One state machine, three layers.** `AvatarDirector` (pure, over `IAvatarEnv`; Catch2 with a fake world) <- `AvatarsFeature` (the real env: GameApi, Lua bridge, stash, team hub, selfship hub) <- `avatar_hub()` (hand-over: the join feature hands decoded `PlayerShip`, `PlayerState`, `EntityDespawn`, `RosterUpdate`, `ServerSettingsUpdate` to it; **two hook lines in `join_feature.cpp`**, `on_frame_message` and `session_ended`; the authority flow registers the net-id / string-table / Control-lane services while it lives). Only the authority node acts; on a client the hub queues roster/settings but the feature returns early.
- **Provision** on `PlayerShip`: waits for `team_hub().factions_ready()`, the roster (name, team) and the sector map; spot = `place_near(host ship, Avatars.SpawnOffsetMeters, player_id)` (1x..2x the offset, golden angle per slot) in the host ship's sector; MD `get_safe_pos` (radius 150 m) moves it clear of stations (sitting-0 finding; 5 s timeout -> the wanted spot); native `SpawnObjectAtPos2` under `x4mp_team_<slot>` + `ActivateObject(false)`; MD dress: name `[MP] <player>`, min hull 100, forced radar, known, **early-game loadout**; then `EntitySpawn{origin=PlayerShip, owner_player, owner_team, controller_player, name, idcode}` (macro/faction strings appended to the server's table by the flow, `AuthorityFlow::string_ref`; net ids from `AuthorityFlow::allocate_net_id`, so the host self-spawn and the avatars share one counter). The same player asking again (rejoin, held requests) gets a refreshing `EntitySpawn` of the SAME ship and net_id.
- **Ship macro + loadout from one place:** `resolve_starter(settings, team, race)` (`avatar_plan.cpp`), fed by `Avatars.StarterShipMacro` (default Argon Elite) and `Avatars.StarterLoadout` (loadouts.xml id; empty = the basic loadout built into `md/x4mp_avatars.xml`, Elite only). Research: [research/starter-ships.md](research/starter-ships.md). The settings arrive as `ServerSettingsUpdate` entries (the mod had no reader before; the hub decodes them).
- **Drive:** `PlayerState` (net_id = avatar, stamped by the server) -> `ghost::Interpolator` per avatar (server time; the wire has no velocity, the director derives it from consecutive states) -> `SetObjectSectorPos` every frame; MD `set_object_velocity` hint every 200 ms (S13.2 mode c, `x4mp.avatars_vel`); Hidden/stale = the ship stays. **Samples whose time is more than 2 s from the server clock (FakeNode bots count ticks, M3-05) are re-stamped with their arrival time (one warning per avatar)**; a real client with a working clock sync is unaffected. One `[drive]` log line per 5 s (states in, set-pose calls, velocity hints).
- **Park:** roster `removed` (or a full roster without the player) -> `EntityChange{Controller=0}`, record `online=false`, the ship stays; every 1 s a parked ship that moved > 0.5 m (or changed sector) is snapped back; `online=false` in the roster (resume grace) only **suspends** driving. `(offline)` stays client-side.
- **Admin removal:** inbound `EntityDespawn{Removed}` on the authority -> `game::safe_remove` (never the player's ship; a refusal is counted and logged), the record is dropped.
- **Persistence and the binder (component ids change on every load, S13.8):** identity `Record`s (player, team, net_id, name, macro, idcode, owner, sector macro, pose, online) live in the stash (`avatars.records`) and in `<config dir>/avatar-records.txt` (survives a game restart), plus a `ghost::Registries` mirror (NetMap kind Avatar + `AvatarRegistry`, key `ghost.registries`, epoch 0) used as an id hint. At every init (reload / save load) the director **rebinds** (`bind_records`: idcode + name + owner among the ships of the existing `x4mp_team_*` factions, nearest wins; tiers idcode+name, idcode, name), respawns only records nothing matches (at their last pose, same net_id), logs strays (`[MP] ` team ships without a record: left alone), then re-announces every avatar (also on every authority Welcome: the server may have lost its mirror). If the ship list is unavailable nothing is respawned.
- **Manifest:** `encode_manifest(..., avatars)` writes `ManifestEntry{origin=PlayerShip, controller_player, owner_team, owner_player, sector, idcode, position, macro_ref/owner_ref}` into the manifest's own string table; the flow takes the avatar snapshot (current poses) at SaveGame time. `AuthorityFlow` also got `reserve_net_ids_above` (a fresh join restarts its counter at 1; avatar ids stay reserved).
- **Bridge:** `x4mp.avatars_safepos|dress|vel` (native -> Lua -> `AddUITriggeredEvent("X4MP_Avatars", ...)`), `x4mp.md_avatars` -> verb `x4mp.avatars_md` (`P;seq;ok;x;y;z`, `D;seq;ok;detail`). `ui.xml` got one line (`x4mp_avatars.lua`). The sector map is **M3-09's** (`selfship_hub().map()`, a new accessor); no second Lua map was built.
- **Tests:** Catch2 `host.avatars*` (10 cases: settings/starter, names/placement, records, binder tiers, provisioning incl. safepos timeout / spawn failure / refresh / two players, driving + velocity hints + gate jump + park + snap-back + suspend, `EntityDespawn` guard, rebind after renumber / hint adoption / lost respawn / listing unavailable, destroyed -> respawn + snapshot, gating), Lua `test_avatars.lua` (7), MD validated with `tools/validate-x4-xml.py --check-properties`, live `tests/hostsim/avatars_run.ps1` (see hostsim.md; real DLL + FakeNode bots + `world renumber`), and `tools/e2e.ps1 -Steps Publish,HostSim,HostSimPair,AuthorityFlow` stays green.

What M3-12 (client takeover) needs:
- A **client** reads the avatar's manifest entry (`origin=PlayerShip`, `controller_player`, `idcode`, `position`, `sector` index, macro via the manifest strings) and can reuse `bind_records` / `Candidate` from `avatar_plan.h` (the manifest has no name: only tier 1 = idcode + owner applies, or extend the manifest). The enumeration for candidates is `game::AvatarsApi::team_ships()` (team factions); a client's local copy of its avatar is **owned by the team faction in the checkpoint** until `SetComponentOwner(id, "player")`.
- `selfship_hub().map()` gives index <-> macro <-> id; `team_hub().faction_of_team()` / `factions_ready()` as M3-08 says. The dress / safe-position MD cues are generic (`X4MP_Avatars` screen); a client spawn can reuse `x4mp.avatars_safepos`, but **its** feature must handle the reply (the hub and feature are authority-only today).
- The spawn clearance (`get_safe_pos`) applies to the client's fresh spawn as well (S13.6 docked finding).
- `CanTeleportPlayerTo` returns `"granted"` when allowed (the hostsim fake still answers `""`, the `GameApi` wrapper treats `""` as allowed): fix both together in M3-12.

Things M3-13 / M3-10 / M3-14 must know:
- **The janitor must not remove avatars.** On the authority an avatar is a legitimate `[MP] <player>` ship owned by a team faction and it is in the save. Removal at universe ready must skip the ships the avatar binder bound (run the janitor after the binder and exclude the ids of `ghost.registries` / the idcodes of `avatars.records`). `tools/savescan` must count them as team-owned avatars, not ghosts; the "ghost registry must be empty" checkpoint check is about `GhostRegistry`, not avatars.
- The hub only routes `PlayerShip` / `PlayerState` / `EntityDespawn` when the authority services are registered; M3-10's client feature needs its own routing for the client's `EntityDespawn`.
- FakeNode `PlayerState.sample_time_us` counts ticks (not server time); the authority re-stamps such samples with their arrival time. M3-14's `sync-report` must not expect server-clock stamps from bots.
- Not verified in game (add to sitting 1/2): (1) `get_safe_pos` result and its number formatting in the MD string; (2) `apply_loadout` of the basic Elite loadout on a `SpawnObjectAtPos2` ship (does it replace the Mk2/Mk3 default parts, are the `../con_*` paths right); (3) `set_object_velocity` on an avatar at 5 Hz together with the per-frame set (S13.2 mode c said yes for the probe's ghosts); (4) `GetAllFactionShips` for a team faction after a save load finds the avatars; (5) `SetObjectSectorPos` with sector-local metres across a gate jump (S13.3 left this open); (6) the avatar's idcode and name survive the checkpoint (S13.8 proved position, name, idcode for one ship).
- Deliberately small: a docked / on-foot player is `Hidden` and the avatar stays at its last pose (real docking is M5); no respawn-on-destroy policy (a vanished ship is respawned at its last pose, logged); the per-team race table is the `resolve_starter` TODO.

### M3-12 client takeover (`features/avatars/avatar_takeover.*`, `avatars_client.*`, hub + join hooks, `x4mp.hint` / `x4mp.takeover`, tests `test_takeover.cpp`, `pair_scenario.hostsim`)

What is built:
- **`AvatarTakeover`** (pure, over `ITakeoverEnv`; Catch2 `[takeover]`, 24 cases with a fake game) <- **`ClientTakeover`** (real env, `avatars_client.cpp`, owned by `AvatarsFeature`, runs only while `!avatar_hub().authority()`) <- hub (`ClientLink`, avatar spawns, manifest path). Stages: Idle -> Requesting (`PlayerShip` every 10 s until the own avatar's `EntitySpawn`) -> Locating -> Teleporting (`SetComponentOwner(copy,"player")`, `ActivateObject`, `CanTeleportPlayerTo` must say **"granted"**, `TeleportPlayerTo(copy, true, true, true)`) -> Confirming (occupied or controlled ship == copy for **10 consecutive frames**, 10 s timeout = refusal) -> Removing -> Done. Log prefix `takeover:` (category ghost); Lua topic `x4mp.takeover` on every stage change.
- **Locate decision (evidence).** The checkpoint contains an avatar only when it was spawned before the checkpoint (a rejoin / returning player); a new player's avatar is not in the save. The client therefore searches the `x4mp_team_*` ships for the `EntitySpawn`'s idcode (+ name), same tiers as the authority's binder (`pick_avatar_copy`), and **spawns a `player`-owned copy at the EntitySpawn's sector/pose** otherwise (the authority already applied the `get_safe_pos` clearance; the macro is `resolve_starter(settings, team)`: the client has no string table). Never spawns while the ship enumeration is unavailable (retry; a duplicate is worse). The hostsim pair run exercises the spawn branch; the bind branch is Catch2 only (it needs a real save: sitting 1/2).
- **Hold.** `avatar_hub().set_client_link()` (installed by the join feature while InGame and not authority) sets `selfship_hub().set_state_hold(true)`: **no PlayerState goes out until the guard confirmed** (otherwise the host-ship pose would drag the avatar on the authority: the server stamps the avatar's net id on every state). The machine sets `own_net_id` and lifts the hold at the guard; `SelfShipFeature` forces one immediate full state after the hold. `clear_client_link()` (session end) lifts it.
- **Removal.** After the guard: the vacated host-ship copy plus every team ship named `[MP] ...` or whose idcode another `EntitySpawn` / the **checkpoint manifest** names as an avatar (the client reads `x4mp_<sha12>.x4mf` itself: `decode_manifest_avatars`; the join feature tells the hub the path on SaveReady, the avatars feature keeps it in the stash for /reloadui), max 4 `SafeRemove` per frame, a refused id is retried 30 frames and then left (logged). Nothing is removed while the player is not in the avatar (waits 30 s, then leaves the copies). A team ship that is not an avatar (no `[MP] ` name, idcode unknown) is never touched.
- **Refusal / retry / hint.** Back-off 2,2,2,3,3,4,4,5,5,5 s, then every 20 s forever (nothing is ever given up; no destructive path exists). From the 3rd refusal: `x4mp.hint {"id":"takeover","show":true,"text":"Sit in the pilot seat to take over your ship"}` (the bridge shows it as the HUD notify, native repeats it every 30 s, `show:false` is sent once at the guard); a pilot-seat **sit-down** (`selfship_hub().status().sit_downs`) retries at once. `x4mp_bridge.lua` got its own block (`B.hint`, `B.takeover`; Lua tests in `test_bridge.lua`).
- **Reload / save load.** The record (phase Progress / Seated / Done, avatar + host local ids with idcodes, net id, pending removals) is in the stash key `avatars.takeover`, written on every phase change. The new DLL instance trusts local ids only when the avatar still has the recorded idcode; Done + seated in it = Done at once (own net id restored, no new request), Progress = reuses the spawned copy (never a second spawn), Seated = continues the removal; anything else drops the record and starts over. `session_ended` resets the machine (a rejoin asks again; the authority answers with the same ship).
- **GameApi / hostsim fix (S13.6).** `can_teleport_player_to` returns the game's text; **"granted" = allowed** (`game::is_teleport_granted`), `""`/null is NOT. Hostsim answers "granted" and refuses otherwise, and now provides `RemoveComponent` (a vacated ship can be removed, the current player ship never) so `SafeRemove` works there. `test_game_api.cpp` and `world_objects.hostsim` assert both.
- **Tests / evidence.** Catch2 `[takeover]` (record, pick tiers, back-off, spawn and bind flows incl. the removal order and "nothing removed before the 10th guard frame", refusal + hint + sit-down retry, slow cadence, false teleport, flicker resets the guard, guard timeout, SafeRemove refusal, removal while not seated, missing enumeration, unknown sector, spawn failures, gating, grant by player id, reload resume in Done / Progress+seated / Progress+unseated / foreign universe / Seated, session end, wire decoders against the real encoders). `pair_scenario.hostsim` (two real DLLs + FakeNode authority): teleport denied -> hint, host copy untouched, no state; `/reloadui` mid-takeover; allow -> teleport, guard, host copy `200001` removed, the player owns the spawned avatar; `/reloadui` after -> Done from the record, no new request. `./tools/e2e.ps1 -Steps Publish,HostSim,HostSimPair,JoinFlow,ReloadSurvival,AuthorityFlow` green (JoinFlow failed once with a 401 while another run held its ports, rerun green), `avatars_run.ps1` green, `mod/build.ps1` 396 tests green, Lua 226.

What M3-13 / M3-14 / M3-10 and the session-4 sittings need:
- **M3-13 janitor.** At universe ready on a client the janitor must NOT remove the takeover's objects: the avatar copy the machine will use (a team ship named `[MP] <me>`, later a `player`-owned ship; the guard protects it only once the player is in it) and, until Done, the host copy. Simplest: run the `[MP] ` sweep after the takeover reports Done (`x4mp.takeover` stage `done`, `ClientTakeover::done()`), or exclude the own avatar's idcode. After the takeover the other avatars' copies are already gone, so a sweep then should find none (log it). The local avatar copy keeps the name `[MP] <me>` (no rename on the client: it needs MD); `tools/savescan` on a client save will see one `[MP] ` ship owned by `player`.
- **M3-10 ghosts (integrated after the merge).** Decision: **gate ghost spawning until the takeover is Done** (the safer option; relying on filtering alone would still let a ghost spawn on top of the host copy before it is removed, and duplicate it visually, and a race between the removal frame and a spawn frame cannot be excluded). Mechanism: `avatar_hub().set_client_link()` (join feature, InGame + not authority) sets `ghosts::ghost_hub().set_spawn_hold(true)`; `GhostFeature::on_frame` returns early while it is set (messages keep queueing in the core, so the ghosts appear at once on release); `AvatarTakeover` lifts it in `finish()` (after the removals; also on a Done record after `/reloadui`) through `ITakeoverEnv::hold_ghosts`; `clear_client_link()` (session end) lifts it too. If a takeover never completes (no sector map, teleport refused for good) the client sees no ghosts: acceptable, the hint tells the player what to do. Belt and braces: the real env drops every `ghosts::ghost_hub().is_ghost_local(id)` from the candidate list and `remove()` refuses them. **Precise removal rule:** vacated host copy + ships whose idcode the checkpoint manifest / an EntitySpawn names as an avatar; the `[MP] ` name rule is only the fallback while no manifest could be read. The host's ship is shown by M3-10 from Replication only after the host copy is gone (no duplicate). Evidence: `ghosts_scenario.hostsim` (HostSimGhosts) now also asserts: takeover done, host copy `200001` absent, exactly the 3 ghosts of the other players, none named after this player, 1 removal, 4 spawns (3 ghosts + the takeover copy), through 20 reloads. The hub still only keeps `origin=PlayerShip` EntitySpawn entries (`HubInputs::avatar_spawns`). hostsim: M3-10's `RemoveComponent` fake is the only one (mine was removed).
- **M3-14 / FakeNode.** Nothing changed server-side. A client without a sector map never takes over (`ready()` needs the map) and keeps holding its PlayerState: hostsim scripts must answer `x4mp.sector_map_collect` before expecting states or a takeover.
- **Session 4, sitting 1/2 (add to the script):** (1) join standing in the host's ship: does the teleport work without the hint, how long until the guard (S13.6: 90 ms standing, 623 ms docked), does the player sit in the avatar with the early-game loadout; (2) a returning player: is the avatar found in the save by idcode (`takeover: the avatar is in the loaded save: local ship ...`) and does it keep its loadout after `SetComponentOwner(player)`; (3) the own ship's name `[MP] <name>`: does it matter; (4) does `GetAllFactionShips("x4mp_team_k")` list the avatars on a client after loading a session save; (5) the refusal path: if S13.6 holds everywhere the hint is never seen; (6) a docked host: does the avatar appear clear of the station (authority-side clearance, M3-11 item 1).
- **Known gaps (deliberate):** the client's local copy of the avatar has no min hull (the authority's has); a Done record is trusted without asking again after a server restart (the authority keeps net ids through its manifest); the ship macro of a spawned copy comes from the `Avatars.StarterShipMacro` setting, not from the EntitySpawn (macro refs need the string table: M3-10 / M4 add a client-side table).

### M3-10 client ghosts (`features/ghosts/*`, `game/ghosts_api.*`, `md/x4mp_ghosts.xml`, `ui/x4mp_ghosts.lua`; tests `tests/ghosts/*` exe `x4mp_ghosts_tests` ctest prefix `ghosts.`, `tests/hostsim/ghosts_run.ps1`)

What it does: player ships (origin `PlayerShip`, not the own ship) of other players become ghosts on a **client** node (nothing on the authority). Per ghost: native
`SpawnObjectAtPos2` under the team faction (`team_hub().faction_of_team`, only while `factions_ready()`), `ActivateObject(false)` + forced radar, MD dress
(`[MP] <name>`, `(offline)` while `controller == 0`, min hull 100, known), `SetObjectSectorPos` every frame from `core/ghost` (`render(now)`), a 5 Hz velocity hint
(MD `set_object_velocity`, mode c), hide (= `SafeRemove`, record kept) / show on the Hidden flag and `EntityDespawn{DockedInside}`, sector changes as ONE place() with the
new sector's id and sector-local pose, removal of a record on `EntityDespawn` / session end, stash persistence and adoption, `[sync]` lines every 5 s.

Pieces and what M3-11/12 reuse:
- `GhostDriver` (`ghost_driver.h`): SDK-free, `IGhostWorld` is the whole game surface (spawn, make_inert, place, dress, hint_velocities, set_owner, valid, wrecked, remove, id_code, name,
  find_ghost_by_idcode, faction, local_ship_within, sector_id). The authority's avatar driver can reuse the driver with its own `IGhostWorld` (kinematic avatars = the same spawn / place /
  interpolate loop, no hide); `FakeWorld` (`tests/ghosts/fake_world.h`) + `Rig` (`rig.h`) are the test kit. `GhostCore` (`ghost_core.*`) = driver + string table + message decoding
  (StringTableAdd, EntitySpawn, EntityChange, EntityDespawn, Replication); `ghost_hub()` is the join feature's hand-over (one line in `K::Frame`, one in `stop_session`).
- `game/ghosts_api.*` (`GhostsApi`): spawn / make_inert / place / pose / valid / id_code / name / `faction_state` / `ships_of` (exports resolved BY NAME, GameFns untouched), and the ONE
  place for the pose convention: `to_pos_rot` with `kYawSign/kPitchSign/kRollSign` (angles radians in the mod, converted to degrees on write by `GameApi` (M3-15); axis order from `core/ghost/math.h`: **not verified in game**; if a ghost flies sideways or
  nose-down flip the signs there and nowhere else).
- Registries: `driver.registries()` (`ghost::Registries`) is mirrored on every spawn / hide; `driver.save(stash)` writes `ghost.registries` (+ my `ghost.meta`, one line per record incl. idcode); the
  string table is kept in `ghost.strings`. `ghost_hub().is_ghost_local(id)` tells the janitor / takeover which objects are ghosts: **M3-13 must skip them on a /reloadui**.
- Adoption (`GhostDriver::adopt`): fixed registry epoch (identity decides, not an epoch): a stashed id is adopted only if valid AND idcode equal AND name starts with `[MP] `; otherwise the
  ghost is searched by idcode among `x4mp_team_1..8` ships (ids change on every load); otherwise it respawns from its samples. Unrelated objects at a stale id are never touched.
- Stream clock (important): `Replication.server_time_us` is the AUTHORITY's capture clock, not the server clock (FakeNode's authority stamps ticks since its own start: 1.4 s off; a real
  authority is only as good as its estimate). The render clock is therefore `now_server - StreamClock.bias_us()` (min of arrival - reference over 128 messages), i.e. the stream's own "present".
  `lat_p95` in `[sync]` is measured against that clock. If M3-11's capture times are exact the bias is just the minimum network latency.
- Deviations from the plan: **stale hiding is off** (`stale_us` 1 h): the server sends nothing for a ship that does not change (a parked avatar was silent for 30+ s), so silence is not
  staleness; ghosts leave through `EntityDespawn` / session end. The sector map is **not** built here: `GameGhostWorld::sector_id` reads `selfship_hub().map()` (one accessor added to the hub,
  `set_map()` in `SelfShipFeature`). `ui.xml` got one line (`x4mp_ghosts.lua`).
- Spawn rules: <= 2 per frame; waits while the faction is not ready (silent) / missing (error every 2 s) / the sector index is unmapped / the local player's ship is within 40 m of the spawn point
  (never spawns onto the player; no timeout); failed spawns back off 2 s; > 3 respawns in 30 s back off 10 s (a deliberate hide + re-show is not a respawn); a lost dress is repeated (name check at +2 s, 5 tries).
  A parked ghost is re-asserted every 250 ms (puts a pushed ghost back); inert every 5 s.
- Spawn near stations: not knowable natively; only "never on the player's ship" is checked (sitting-0 note 8). M3-11/12 need `get_safe_pos` / the undock point for avatars.

Numbers: Catch2 (`ghosts.*`, 36 cases): path error vs the analytic track (circle 1500 m / 250 m/s, line 300, accel to 450) < 0.5 m; gate jumps land within 1 m in the new sector with exactly one place() per jump and
no respawn; 7 ghosts: **frame p50 0.9 us, p95 2.6 us, max ~10 us, 0 allocations** (Replication ingest + interpolation + driver, game calls excluded; budget 200 us). e2e (`ghosts_run.ps1`, real server,
FakeNode authority + 2 flying bots, one real DLL): `[sync]` err p50/p95/max 0.00/0.00/0.00 m, display latency p95 ~120-250 ms (adaptive delay 116-250 ms), frame p95 ~28 us with 3 ghosts, 20 reloads: 0 leaks, 0 duplicates (3 spawns total).

Found on the way:
- **Separate fix, keep it (`join_feature.cpp`, ManifestReport ordering):** the count-only `ManifestReport` was sent in the same frame as `LoadStatus(Matching)` and could overtake it: the server answers `PhaseDenied`, never runs the catch-up
  (string table + journal) and the node never learns the ship macros. It is now sent in `finish_ready()` right before `NodeReady` (after the roster shows Matching). Without it no ghost can be spawned.
- **Open item for M3-14 (server, not fixed here):** strings the authority adds while a node is still joining are not sent to it (`OnStringsAdded` only reaches CatchingUp/InGame nodes, the catch-up snapshot is taken earlier), and a resume
  does not re-send the table; a client joining while avatars are being provisioned can miss a macro. The ghost feature persists its table in the stash for reloads; the ghost e2e starts the bots first.
- FakeNode stamps `PlayerState.sample_time_us` and `WorldUpdate.capture_time_us` with ticks since its own start, not the server clock (see stream clock above).
- hostsim: `expect-ghost` is real (reads the `[sync]` lines), new `until`, `repeat`, `world md-emulate|md-sectors|factions`, `world object push`, `RemoveComponent` / faction / faction-ship exports, count filters
  `moving hinted minhull active radar` (docs/hostsim.md). `world_objects.hostsim` no longer relies on the old STUB. CI: step `HostSimGhosts` in `tools/e2e.ps1` and `ci.yml` (ports 47965-47967; the pair range and 47944-47952 are used by other runs).
- Not covered by e2e (FakeNode cannot send it): the Hidden flag (Catch2 covers hide / show) and more than one gate jump (Catch2 covers it exactly).
- Not verified in game: ghost model / name colour, orientation signs, that MD `set_object_velocity` accepts the batch list shape, that `ConvertStringToLuaID` takes the decimal id string exactly as in the spike.

### M3-13 save hygiene v1 (`features/janitor/**`, `features/authority` check, `tools/X4MP.SaveScan*`, `tools/session4/savescan.ps1`, tests `test_janitor.cpp`, `X4MP.SaveScan.Tests`)

What is built:
- **Rule (`janitor_plan.h`, pure).** A leftover is an object that is *ours* and not *alive*. Ours = name starts with `[MP] `, or (client in game only, `TakeoverStatus.active`) owned by an `x4mp_team_*` faction even without the prefix. Alive (never removed, counted by reason in the summary): ghost (`ghost_hub().is_ghost_local`), takeover own copy / vacated host copy (`TakeoverStatus.avatar_id/host_id`), authority avatar (a local id the director's views report, or an idcode of any avatar record: ids change on every load, idcodes do not), guarded id (`game::is_player_guarded`; `safe_remove` re-checks). Everything else goes through `game::safe_remove` only.
- **Load-time janitor (both roles).** `JanitorFeature` sweeps once per universe, only after a REAL `on_universe_ready` (new `Gates::universe_ready_after_reload`, set by `ModHost` when it synthesizes the gate after `/reloadui`: no sweep then; a wait that was still pending survives the reload through stash key `janitor.pending`). When: a client with the takeover link waits for `TakeoverStatus.done` (the takeover may want the returning player's avatar that sits in the save); the authority sweeps as soon as it is connected (its avatars are protected by their records); a node with a pending session (`diag_hub().session_pending()`, set by the join feature for every stage between Joining and InGame) waits; a node with no session sweeps after `kStandaloneGraceS` = 15 s of frame time (a launch.json auto-connect starts within frames, so a joining node never gets there). Up to 20 removals per frame, one pass per frame until a pass finds nothing new (an id acted on once is never acted on again: the game may keep listing a removed object). The summary line `janitor: swept: N leftover '[MP] ' object(s) removed, R refused, among S scanned (M carry the prefix); kept: ghosts g, avatars a, own copy o, guarded u; P pass(es)` goes to the log and to LogForward (`[janitor] ...`).
- **Checkpoint check (SaveJob).** `AuthorityFlow::request_save` calls `JanitorFeature::checkpoint_check(ctx)` right before the SaveGame request (same frame, synchronous). Definition: stale = a non-avatar, non-guarded, non-ghost `[MP] `-named ship/station of `player` or an existing `x4mp_team_*` faction (avatars are protected by idcode / bound id; the authority never removes a team ship for lacking the prefix); it removes what it finds (a removed id counts as gone even if the game keeps listing it), and `GhostRegistry` must be empty (`ghost_hub().ghost_count()`; the authority has none). `ghosts_cleaned = (nothing refused) && ghost registry empty`, logged (`janitor: checkpoint check: ...: ghosts_cleaned=true|false`, forwarded) and sent as `SaveUploadBegin.ghosts_cleaned` (was hard-coded true). If the list exports are missing the universe cannot be verified: warning, `ghosts_cleaned=true` (a game without the exports would otherwise never get a current checkpoint).
- **Hubs.** `AvatarHub`: `set_protect_fn` (AvatarsFeature publishes ids + idcodes from `AvatarDirector::views()`), `set_takeover_status` (published every frame on a client from `ClientTakeover::done()/avatar_id()/host_id()`, reset by `clear_client_link`). `GhostHub::ghost_count()`. `DiagHub::set_session_pending`.
- **`tools/X4MP.SaveScan`** (C# net10.0, in `X4MP.sln`, references only `X4MP.Protocol`): streams `.xml.gz` or plain `.xml`, lists objects with `[MP] ` names, `x4mp_team_*` owners and reference-mod traces (`x4mp_host`, `x4mp_client_*` as owner / name / faction) with idcode/macro/sector/class, the team factions (with `active` when the save has it), the reference factions and the MD cues named `x4mp*` with their `$variables`. JSON (snake_case, `--json <file|->`) + a human summary. Exit 0 = clean, 1 = unexpected leftovers or a problem, 2 = usage / unreadable save. Expectations: `--manifest <x4mf>` (its `PlayerShip` entries' idcodes), `--expect-avatar`, `--expect-avatars-file`, `--avatar-owner any|team|player`, `--allow-team-owned`, `--no-require-avatars`. `tools/session4/savescan.ps1 -Save <path|name> -Role Checkpoint|Client|Any [-Manifest ..] [-OwnAvatar idcode] [-Json ..]`. Tests: `tools/X4MP.SaveScan.Tests` (13 xunit cases on three hand-written fixtures in `fixtures/*.xml`, gzipped on the fly; no game data). CI: a step in the `dotnet` job runs the CLI on the gzipped fixtures (clean = 0, checkpoint with avatars = 0, client quicksave = exactly 1). The two new projects have their own `packages.lock.json` (`dotnet restore --locked-mode` passes).
- **Tests.** Catch2 `test_janitor.cpp` (`features.` prefix, `[janitor][m313]`): the pure rule, sweep after grace removes only `[MP] ` ships and stations, deferred listing, guarded id, live ghost, authority avatar by id and idcode, faction list respected, missing exports, `/reloadui` (pending wait carried, completed sweep not repeated, a later real load sweeps), pending session holds, client waits for Done and spares own + host copy (and removes team-owned leftovers), authority sweeps once connected, checkpoint check (clean / stale removed / refused -> `ghosts_cleaned=false` / non-empty registry / missing exports). The 4 M2-10 janitor tests moved there. HostSimGhosts asserts the janitor ran after the takeover and removed nothing live.

What M3-14 / session 4 need:
- The e2e scenarios need no changes. A hostsim client waits for `x4mp.takeover` stage `done` before the janitor sweeps; scripts that never complete a takeover never see a sweep line (by design). A hostsim node without a session sweeps after 15 s of frame time.
- Session 4 sitting 1/2, new checks: (1) client loads the session save: the log shows `janitor: waiting for the takeover to finish`, then `swept: 0 ...` or the count of other avatars' copies the takeover missed; (2) quicksave on a client with ghosts, load it with the mod: `janitor: swept: N` with N = ghosts + old copies; `savescan.ps1 -Role Client -OwnAvatar <idcode>` on that quicksave shows them, and an empty list after a re-save; (3) authority checkpoint with 2 players: `janitor: checkpoint check: 0 stale ... ghosts_cleaned=true` and `savescan.ps1 -Role Checkpoint -Manifest <x4mf>` exits 0.
- Not verified in game: the save XML shape SaveScan assumes (`<component class= macro= code= owner= name=>` under `<component class="sector" macro=>`, `<faction id= active=>`, `<cue name=>` with `$variable` descendants) is a guess from knowledge of X4 saves, not from a real file: check it against the first real checkpoint and adjust `SaveScanner` (one place: `HandleComponent`); whether `RemoveComponent` makes an object disappear from `GetAllFactionShips` in the same frame (the check assumes it may not); whether `GetComponentName` returns the user-set name for stations too.
- Known gaps: the sweep only looks at `player` and `x4mp_team_1..8` objects (not an `[MP] ` object another faction owns); a save load while a client waits drops the wait (the new universe waits again); leftovers that SafeRemove refuses stay in the save and are logged (`refused`).

### M3-14 integration, CI and the session-4 kit (2026-10-04)

**Three integration bugs the new two-DLL pair run found (all fixed):**
1. **The M3 authority never sent a `WorldUpdate`**, and the server replicates nothing before the first one: with the real authority no client would ever have seen a player move (the FakeNode authority masked it). `AuthorityFlow::step_world_clock` now sends a keepalive `WorldUpdate` (no states) at 20 Hz on the Realtime lane once a checkpoint exists (`authority: world clock: ...`).
2. **The host's own ship was a plain world entity** (`origin=AuthorityRuntime`, no controller, `PlayerState.net_id=0`): no ghost, no motion, `PlayerInfo.ship_net_id=0` for the host. The self-spawn is now `origin=PlayerShip` with the host as controller / owner, team, name `[MP] <host>`; `AuthorityState.host_net_id` (persisted, Catch2 round trip) goes into the host's `PlayerState`.
3. **`NodeStats.udp_active` / `udp_rx_loss_pct` / `ghosts` were never filled** and `rx/tx` counted TCP only (criterion 13 "GUI shows UDP active" and criterion 11 bandwidth were unmeasurable). Filled now; the admin `NodeStatsDto` (`udpActive`, `udpRxLossPct`, `ghosts`, `generated.ts` regenerated) and the player detail page show "Realtime lane" and "Ghosts shown".

**Server string-table gap (the open item of M3-10), fixed with tests:** the catch-up string snapshot is taken while the node is `Matching` and the move to `CatchingUp` is queued (`Fire` is async), so a string added in that window reached the node neither way; `OnStringsAdded` now includes `Matching`. A resume with `last_journal_seq == 0` in `InGame` got nothing (a missed string never arrived): it gets the whole table now; a resume in `CatchingUp` without a position restarts the catch-up. Tests: `StringsAddedWhileAClientIsMatchingAreSentToIt`, `AResumedNodeWithoutAJournalPositionGetsTheStringsItMissed`, `AStringAddedWhileAClientIsStillJoiningReachesIt`. `ghosts_run.ps1` starts the client right after the bots now (it used to wait for them).

**CI (`tools/e2e.ps1`, `ci.yml` job `e2e-headless`, timeout 50 min):** new steps `UdpLane` (the M3-03 request), `HostSimAvatars` (`avatars_run.ps1`, wait loop without sleeping), `HostSimM3` (`m3_pair_run.ps1`: two real DLLs + 6 bots, ports 47930-47932, ~100 s), `Session4Kit` (`session4_dry_run.ps1 -Part kit`, ~70 s); the Windows job also reruns when `tools/session[234]/` changes. `SendQueueTests.TrySendIsFast` measures the best of 300 rounds (like the ReplicationBench fix); the checked limit (200 ns) is unchanged.
**Perf numbers (hostsim, relwithdebinfo, 7 remote players):** mod main-thread p95 0.10 ms client / 0.12 ms authority (target 0.2), ghost driver 0.03 ms, client rx ~1 kB/s tx ~2 kB/s (UDP counted), authority rx ~10 kB/s, < 10 log lines/s, host-ghost path error 4-6 m p95 at 270 m/s (the server stamps `PlayerState`-driven entities with the latest `WorldUpdate` game time: a floor of ~speed x 25 ms; improving it = stamp with the state's own sample time, server side). CI limits in `m3_pair_run.ps1` are looser (0.5 ms, 12 lines/s): runners are noisy.
**Hostsim:** `world md-emulate` also plays the authority's MD half (safe position, dress); `expect-file ... timeout=0` and the rule "nothing inside `until` may block the frame loop" (a blocked authority stopped its clock: ghosts showed 5 s of latency); new `reloadui` command (= /reloadui incl. the `on_game_loaded` replay): **plain `reload` never exercised ghost adoption** (the universe never became ready again), so the old "20 reloads, 0 leaks" evidence was weaker than it looked; the pair run and `session4_client.hostsim` use `reloadui` and assert `ghost adoption: records=N adopted_by_id` (it adopts by idcode: `registry: found=false`, the `ghost.registries` blob is not found at adoption: harmless, worth a look in M4).
**FakeNode:** `--avatar-timeout S` (a real authority needs more than 10 s), `--host-stand-pos x,y,z`. **Kit (`tools/session4`):** `start-fake-authority.ps1 -Wingmen N -HostStand`, `start-fake-clients.ps1 -Wingmen N -Target` (waits for the host's ship before it starts the bots), `start-server-lan.ps1 [-Check]` (firewall read-only, prints the `New-NetFirewallRule` / `Disable-` / `Enable-` commands, never runs them), `make-client-kit.ps1` (self-contained zip for PC B, scans for personal paths), `sync-report.ps1` (median-of-windows judgement against the Q3 / criterion-11 targets, `-Strict` for CI), `install.ps1` / `uninstall.ps1` (product), `collect-logs.ps1` (product mode), `upload-save.ps1` (fixed: the PUT body must be `byte[]`), `topology.ps1`. Dry runs: `session4_dry_run.ps1 -Part kit|topology|all` (all three topologies passed). `savescan.ps1` exit code for a missing save fixed (2, not 1).
**Known gaps / for the lead:** (1) the client's local copy of its own avatar gets no early-game loadout and no minimum hull (only the authority's avatar is dressed): the user asked for early-game equipment, needs an MD apply on the client; (2) with wingmen the first request decides where the fake host ship (and you) appear: `-HostStand`; (3) `sync-report` does not judge the latency of a parked ghost; (4) the authority's bandwidth in M4 needs the 256 kB/s view, not the 20 kB/s client one.

### M3-16: HUD no longer draws over a live menu

`x4mp_hud.lua` H.tick used to force-draw after `blockedMax` (10 s) of being blocked, which killed the render target of any real open menu (live session 4: the map went blurry at ~11.6 s). Now `H.liveBlockers()` decides: a blocking View entry is live while `entry.frames` (filled by `View.updateMenu`, viewhelper.lua; the same ids Helper checks with `IsValidWidgetElement`) holds at least one valid frame. Live: never forced, `blockedSince` restarts, log "hud: blocked by X (live), not forcing" at most every 30 s. No valid frame for `blockedMax`: stale, forced as before (session-3 heal). Without `IsValidWidgetElement` / `entry.frames`, only `config.fullscreenMenus` (MapMenu) count as live. Tests in test_adapter_hud.lua. In-game check: map open > 30 s with the HUD on.

### M3-15 ghost rotation units (2026-10-04, live finding of session 4 sitting 1)

Every ghost (FakeNode wingmen, the parked `[MP] Host`) faced sector +Z for ever; positions were fine. Cause: **X4's `SetObjectSectorPos` / `SpawnObjectAtPos2` take angles in DEGREES, `GetObjectPositionInSector` returns RADIANS** (vendored `sdk/x4n_math.h`: "GetObjectPositionInSector returns radians, SetObjectSectorPos expects degrees"; S13.4 confirmed the read side; the sitting-0 probe wrote degrees). The mod wrote radians, so a heading of 1.5 rad was a 1.5 degree turn.
- **Conversion point:** `GameApi::spawn_object` and `GameApi::set_object_sector_pos` (`game/game_api.cpp`) apply `pos_rot_to_game_write()` (`game/game_api.h`, `kRadToDeg`) right before the export call. Inside the mod a `PosRotPod` / `GhostPose` / avatar `Pose` is always radians; `object_position` (reads) is not converted. Because it sits at the one chokepoint, every writer is covered: `GhostsApi::spawn/place`, avatar spawn and driving (`avatars_feature.cpp`), the takeover local copy (`avatars_client.cpp`). `kYawSign/kPitchSign/kRollSign` are unchanged (axis order and signs are still not verified in game).
- **hostsim** now models the game: `SpawnObjectAtPos2` / `SetObjectSectorPos` thunks take degrees and store radians, `GetObjectPositionInSector` returns radians; script-side values (`ship place yaw=`, `world object add yaw=`, paths) are radians as stored. `world_objects.hostsim` expects pi/4 and pi/2 for the stub's 45 / 90 degree writes.
- **Tests:** `test_game_api.cpp` "angles are written to the game in degrees and read back in radians" (yaw pi/2 reaches the export as 90, reads back as pi/2; spawn path and raw `GameApi` writers too); `m3_authority/m3_client.hostsim`: the host turns to 1.2 rad and the client's `[MP] Host` ghost must read yaw 1.2 +- 0.05 rad in the fake world; FakeNode `AFormationWingmanReportsTheLeadersYawInRadians`.
- **Still to look at in game:** that ghosts now face their heading (if one flies sideways or nose-down, flip the sign constants in `ghosts_api.h`), and pitch/roll axis order.

### M3-18 a moved player's avatar follows the new team (2026-10-04)

Live finding (session 4 sitting 1, criterion 9): after **Move to...** the `[MP] Wing02` ghost kept the team-1 colour. Nothing re-owned the avatar: `FakeAuthority.Reassign` only walks the galaxy entities, the real authority mod ignored teams after the spawn.

**Trigger (single, decided):** the **roster upsert** (`RosterUpdate`, `PlayerInfo.team_id`). The server already pushes it on every move (`TeamModule.Fanout.AnnounceRoster`, after the `TeamTable` delta, so the faction slot is known). `ReassignPlayerAssets` is NOT the trigger: it is only sent when `MoveAssetsWithPlayer != None` and speaks about assets, not avatars; the real authority mod does not handle it at all. A roster also covers a move while the authority was away (the full roster after a resume).

- **Real authority** (`features/avatars/avatar_director.*`, `avatar_wire.*`, `avatars_feature.cpp`): `on_roster` sees `team != record.team` -> `want_team`; in `step()` (Live avatars) `apply_team_move` calls `IAvatarEnv::set_owner` (native `SetComponentOwner`, no MD needed: the name, min hull, radar and the inert state stay, `activate(false)` is re-asserted), updates the record (team, owner; persisted), then sends ONE `EntityChange{Owner|OwnerTeam}` (`encode_owner_change`). Retried every 2 s until the game call and the send worked; waits for `factions_ready()`. An avatar not yet announced carries the new owner in its spawn. A (re)spawn of a lost avatar always uses the faction of the CURRENT roster team. The binder has a second pass: a lost avatar with a pending team move is looked for under the new faction (ship moved in game, record not written yet).
- **FakeNode** (`FakeAvatars.Reown`, driven by `NoteRoster`): same trigger, same change message (plus a `StringTableAdd` for a new faction); the host ship entry too. A rejoin answers with the new team.
- **Server:** no change needed: `WorldMirror.ApplyChange` already applies `Owner`/`OwnerTeam` and `InterestManager` forwards it to the clients that hold the entity; clients (`ghost_core` -> `owner_dirty`) were done.
- **Tests:** Catch2 `avatars.director: a player moving team re-owns the avatar (M3-18)` (7 sections: one reown + one change, retry, factions wait, both rebind cases, respawn under new team); xUnit `APlayerMovingTeamReownsItsAvatarWithOneEntityChange`; e2e `ghosts_scenario.hostsim` + `ghosts_run.ps1`: the runner creates team 2 and moves `Bot01` through the admin API when the scenario writes `move_request.txt`, the real client's ghost becomes `owner=x4mp_team_2` in place (spawns stay 4) and keeps it over the 20 reloads.
- **Not covered by an automated run:** the real x4mp.dll authority doing the reown with a real game (`SetComponentOwner` on a ship made by `SpawnObjectAtPos2`; in-game check = sitting 1 step 1.5).

### M3-20 a parked avatar no longer oscillates (2026-10-04)

- **Cause (confirmed):** `park()` reset the velocity estimator but the 5 Hz hint loop skipped every non-online avatar, so the last non-zero MD velocity hint was never cleared. The inert ship kept drifting, `maintain()` snapped it back (repairs ~1/s) and it drifted again. Same for suspended avatars.
- **Fix:** `AvatarDirector` owes one zero-velocity hint (`zero_pending`) when an avatar parks, is suspended, or has been snapped back; the hint loop sends it through the existing MD `velocity` path (no XML change, a zero list entry is already accepted; the hint has no angular part). The parked snap tolerance is 2 m (was 0.5) and a repair now logs `avatars: repaired the parked avatar ...`. Client ghosts (`ghost_driver.cpp`) already send zero hints once the held pose stops changing and are re-placed every 250 ms instead of repaired, hidden ghosts are removed: no change needed.
- **Tests:** Catch2 `avatars.director` section (one zero hint after the leave, no repair for 1 m settle, one repair + one more zero hint for a real push); `avatars.hostsim` waits for a zero hint, then 30 s with no `repaired the parked avatar` log (the hostsim world has no physics, so it guards the plumbing and the no-spurious-repair rule).

### M3-21 (an authority's fresh rejoin never got a save to load; session 4 sitting 2)

- **Root cause:** `SaveService` only ever sent the authority a `SessionSaveInfo` for the admin-chosen *start save* while the session had no checkpoint (`SendStartSaveInfo` returned when `_current` was set). An authority rejoining fresh after `AuthorityLost` (session `AuthorityLoading`, a current checkpoint exists) got nothing, stayed in `SyncingSave` / `checking_save`, never went InGame, so `TryRequestInitialSave` never asked for a checkpoint.
- **Fix** (`SaveService.StartSave.cs`): `EffectiveStart()` = the start save while there is no checkpoint, else (phase `AuthorityLoading`) the current checkpoint's save. `SendStartSaveInfo` / `IsStartSave` use it, so the rejoining authority is sent the current checkpoint (no manifest, no checkpoint id, as on the first start), reports `SaveReady`, loads, `NodeReady`, then `RequestSave{Migration}` makes a new checkpoint and the session goes Running. No mod change: `Session::download_save` already skips the download when the `x4mp_ckpt_*` file in the save folder matches size and hash, and the avatar director rebinds from the loaded save.
- **Tests:** xUnit `AuthorityRejoinTests`; e2e `authority_restart_a/b.hostsim` as scenario 3 of `authority_flow_run.ps1` (AuthorityFlow step): the real x4mp.dll authority stores a checkpoint and exits without a disconnect, a new process (same identity key, checkpoint file in its save folder) joins fresh, loads it, no second self-spawn, session Running.


### M3-22 which avatars exist in a loaded universe is decided by the loaded save (live finding, session 4 sitting 2)

- **Finding:** session A ended, a NEW session B was created from the plain `save_004`. The authority's `avatar-records.txt` (and the in-memory director) still held session A's 7 records, the binder found none in the save, the director respawned all 7 and B's first checkpoint listed them.
- **Key (decided): the loaded save's sha256, not a session id.** `ServerHello.session_id` is a per-process GUID (`GatewayState.SessionId = Guid.NewGuid()`, renewed only when a session returns to Idle): it changes on every server restart, so it cannot key anything that must survive one. `avatar-records.txt` / the stash text is `x4av 2`, `C|<checkpoint save sha256 hex>|<idcode>;<idcode>...` ledger lines (checkpoints this authority stored, newest last, max 8; the idcodes are exactly the avatars its manifest lists, added in `AuthorityFlow::on_checkpoint_stored`) and the `A|` records. `x4av 1` files have no ledger.
- **Rules (`AvatarDirector::on_loaded_save`, called from `JoinFeature::handle_save_ready` for every save THIS node loads):** a sha in the ledger keeps exactly its listed avatars (any session, any server process, then the binder rebinds by idcode, no second spawn); any other save (the plain start save) keeps none; one log line `avatars: loading ...: k of n avatar record(s) kept`. A kept universe (stash `/reloadui`, "this game already runs it", resumed welcome) keeps everything.
- **Manifest from the server:** not used. The M3-21 start-save path sends no manifest and no checkpoint id; adding the manifest there would make the authority send a `ManifestReport` with a zero checkpoint id (join_feature.cpp `has_manifest_`). The local ledger is the source. Consequence: an authority on another PC (no ledger entry) drops the records on a checkpoint load, and its binder respawns what it does not find. A proper fix = the server sends the manifest with the start-save info and the mod reads it (follow-up).
- **Known limit:** the no-load case (a new session while the game already runs a universe) keeps records.
- **Tests:** Catch2 `avatars.director: a loaded save keeps exactly the avatars its checkpoint listed ...` (server restart + newest checkpoint: kept, rebound, no spawn; plain save: none; old file; kept universe; plain-save session provisions only on PlayerShip; ledger cap) and `avatars.plan` ledger round trip. E2E in `avatars_run.ps1`: `avatars_restart.hostsim` (server process restarted on the same data dir, session started from the newest checkpoint: `2 of 2 avatar record(s) kept`, nothing dropped or spawned) and `avatars_session2.hostsim` (new server, plain save: `0 of 2 ... kept`, no rebind/spawn/manifest entries, then a PlayerShip request provisions).

### M3-23 diagnostic build for Finding 4 (client loses the player's map knowledge)

- **No behaviour change unless a switch is set.** `x4mp.json` key `diag` (object; the flat spelling `"diag.takeover_off"` is accepted too, flat wins): `takeover_keep_original` (`AvatarTakeover::begin_removal` does not queue the host ship copy; everything else as before; log `diag.takeover_keep_original: ...`), `takeover_off` (`AvatarTakeover::step`, after the ready/player checks: stage goes to Done at once so the janitor does not wait, `hold_states(true)` stays, `hold_ghosts(false)`, no request/spawn/teleport/removal, no Done record is persisted), `ghosts_off` (`GhostFeature::on_frame` returns before anything is spawned, adopted or moved), `janitor_off` (the load-time sweep of `JanitorFeature::on_frame` is skipped on any role; the authority's checkpoint check is untouched). `core/config` `DiagConfig`, unit-tested in `test_config.cpp`.
- **Probe mechanism.** `features/diag/knowledge_feature.*` (first feature in the registry) + `ui/x4mp_diag.lua` + `md/x4mp_diag.xml`. Native `knowledge_probe(tag)` (hub, main thread) -> Lua event `x4mp.knowledge_ask {"seq"}` -> `AddUITriggeredEvent("X4MP_Diag","probe",seq)` -> MD counts: `find_sector knownto="faction.player"` (the attribute `md/cinematiccamera.xml:183` uses), `find_station/find_gate space="player.galaxy" multiple known="true"` (`known` = "Known to player?", `libraries/common.xsd:5709`; galaxy-wide station search as `md/conversations.xml:495`), clusters and the three samples (`cluster_01/07/14_sector001`) with `.isknown` (`libraries/scriptproperties.xml:89`, used on a cluster/sector by `md/diplomacy.xml:7218`, `md/setup.xml:891`) -> `x4mp.md_knowledge` -> `x4mp.knowledge_md` -> one log line `knowledge: sectors_known=N/M stations_known=.. gates_known=.. clusters_known=.. samples=[..] at='<tag>' game_age=..s` (also LogForward `[knowledge]`). Tags: `universe ready` (feature `on_universe_ready`), `takeover: avatar spawned|avatar bound from the save|teleported|guard confirmed|original removed`, `takeover_off: nothing done`, `after janitor sweep`, `after first ghost spawn`, `chat command /x4mp knowledge` (chat wrapper in `x4mp_chat.lua` -> `X4MPDiag.requestKnowledge()` -> verb `x4mp.knowledge_cmd`). The MD answer arrives a frame or so after the request: order by `game_age`. The MD part has not run in the game yet (only `tools/validate-x4-xml.py`): if the first run shows no `knowledge:` lines, look for a script error of `X4MP_Diag` in the game log.
- **Files touched (other agents' areas):** `avatars/avatar_takeover.{h,cpp}` (diag member, `probe()` hook on `ITakeoverEnv` with a no-op default, the probe calls, two switches), `avatars/avatars_client.cpp` (env `probe()`, `set_diag` per frame). NOT touched: `avatar_director.*`, `avatars_feature.cpp` (M3-22). Also `janitor_feature.cpp`, `ghost_feature.{h,cpp}`, `host/feature_list.cpp`, `x4mp_chat.lua`, `ui.xml` (one file line), hostsim (`expect-file-order`, fake MD answer `x4mp.knowledge_ask`, state `knowledge_probes`).
- **Tests:** Catch2 `test_config.cpp` (diag), `test_knowledge.cpp` (decode/format, ordered tags, chat command, no Lua), `test_takeover.cpp` `[diag]` (probe order, keep_original, off), `test_janitor.cpp` (janitor_off); Lua `test_diag.lua`, `test_chat.lua`; `ghosts_scenario.hostsim` asserts the probe lines in order with fixed fake numbers. `ghosts_off` has config + code only (no Catch2 case for the feature).
- **Test script:** `docs/in-game-session-4.md` "Finding 4 experiments".

### M3-27 map flips to fog (Findings 4 and 18): investigation, no code (2026-10-06)

- Analysis in [spikes/finding-4-map-fog-analysis.md](spikes/finding-4-map-fog-analysis.md). The map's fog / explored / radar-bubble drawing is native; the authority's 12:06 checkpoint shows the discovered data intact and known counts constant during the flip, so the flip is at the render / live-view layer, not data loss.
- Ranked hypotheses: H1 player <-> own-team relation exactly +1.0 is the engine's `self` range (`libraries/factions.xml:6`); H2 native `SetComponentOwner` re-own of a live forced-visible avatar (M3-18); H3 `ActivateObject(false)` every 5 s on a parked avatar; H4 forced radar + `set_known` on far-sector ships. The `helper.xpl GetChildren` errors are our HUD redraw hitting a stale frame id (log noise, also on clients).
- Next task (to schedule): add the five diag switches and the `liveview` probe (`/x4mp knowledge watch`) of section 5.2, then run the one-PC experiment of section 5.1 (its first part needs no new code). Fixes are listed in section 6 but must not be applied before the experiment picks one.

### M3-24 machine-local state moves to %LocalAppData%\X4MP; a player.key never crosses PCs (finding 15, session 4 sitting 3)
- **Cause:** the mod's whole config dir was `Documents\Egosoft\X4\x4mp\`. With Documents on OneDrive both test PCs shared `player.key`, the server matched the key and treated Bob as Alice rejoining (`Rejoined`), dropping the authority; both PCs also wrote one `x4mp.log`.
- **Where things live now (`host/paths.{h,cpp}`):** normal mode `ConfigPaths.dir` = `FOLDERID_LocalAppData\X4MP` (known-folder API, no env vars); `legacy_dir` = the old Documents dir (read-only). Everything that used `paths->dir` follows automatically: `player.key`, `logs\x4mp.log`, `ext-hash-cache.json`, `avatar-records.txt`, `authority-saves.json`, `authority\`, `launch.json`, `x4mp.json`. Portable mode (`x4mp.portable` or `x4mp.json` in the extension folder) is unchanged: everything in the extension folder; hostsim/CI use it. Fallbacks: no LocalAppData -> old Documents dir -> extension folder. `ModHost::Options::local_app_data_override` is the test hook.
- **x4mp.json:** moved too (diag switches and last_address are per PC). `migrate_user_config` copies the Documents file once when the new one does not exist, keeps the old file, logs one line (`x4mp.json copied once ...`). `player.key` is never copied.
- **Identity rule (`host/player_key.{h,cpp}`):** `player.key` = line 1 the 64 hex key, line 2 `machine=<sha256 hex of HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid>` (`machine_guid()` in paths.cpp; neither the raw id nor the key is ever logged). Load: tagged with this PC -> kept; no tag (any pre-M3-24 file) -> NOT adopted, fresh key written and one log line; tagged with another PC -> NOT adopted, fresh key, one log line; no file -> new. If the registry value is unreadable no judgement is possible: a valid key is kept and new files are untagged. An untagged key is never adopted by any PC, so two PCs can never end up with the same key. Single-PC users get a new identity once (accepted, pre-release).
- **Server, a fresh key with an existing name (`NodeGateway` step 7 -> `IPlayerStore.BindAsync`):** refused with `NameTaken` ("name is bound to another player"): a name stays bound to the first key. So after this migration every existing player is locked out of their name until the admin releases it. No server change was needed: the GUI **Players > Release name** (API `PATCH /api/v1/players/{id}` `{"releaseName":true}`, audit `player.release-name`; the player must be offline, kick first) renames the old row to `released-<id>` (history and bans stay), after which the new key can bind the name (new player row: wallet/team start new). Two ONLINE players still can never share a name (unchanged). Documented in `docs/in-game-session-4.md` sitting 3 step 3.0.4 and its troubleshooting table.
- **Kit (`tools/session4`, `tools/session3/common.ps1`):** `Get-X4MPConfigDir` is now LocalAppData\X4MP (or the portable extension folder), `Get-X4MPLegacyConfigDir` the old Documents dir; test stand-in env `X4MP_LOCALAPPDATA_ROOT` (also switches portable detection off). `collect-logs.ps1` and `sync-report.ps1` read this PC's log; the old Documents log is used only when this PC has none, with a warning that it may be another PC's; `player.key` is never zipped. `Set-ExtensionEnabled` returns `unavailable` with a clear warning when content.xml throws "The cloud file provider is not running" (install.ps1 carries on, deploy already done). `topology.ps1 Get-FirewallStatus` now reports per port: rule with the exact name present (and whether its remote scope includes 100.64.0.0/10), present but disabled, "covered only by another rule (step 3.5 will not work)", or missing; `start-server-lan.ps1` prints the `New-NetFirewallRule` line for every port without a named rule. Still the fast COM path; the CIM fallback only knows enabled named rules.
- **Tests:** Catch2 `tests/test_host_paths.cpp` (paths, one-time copy, key tag rules: new/kept/untagged/foreign/no machine id); the session3/4 hostsim dry runs set `X4MP_LOCALAPPDATA_ROOT`.
- **Open:** the server cannot tell a re-keyed player from a new one, so the released row's wallet/team are not inherited (a "re-key" admin action that keeps the row would need a schema flag); the FakeNode/headless tools keep their own key files.

### M3-26 ghost turns late when the remote ship turns in place (Finding 17)

- **Root cause (client decode, not the sender or the server):** `ReplicationDecoder::merge` (`core/ghost/replication.cpp`) set `has_pose = (mask & kRepPos)`. The server's delta masks (`ReplicationMath.DeltaMask`) omit POS for a ship that has not moved, so a ship turning in place produces ROT+TIME-only entries; `StreamSet::ingest` sent those to `apply_state` (flags / hull only) and dropped the rotation. The ghost's heading changed only at the next keyframe (every 5 s for Near/Sector, the full entry with POS) as one quick snap. Flight was unaffected (POS changes every sample); the avatar direction (authority sees Bob) was fine because Bob's ship is never perfectly still. Checked and found correct: `OwnShipTracker` (idle 5 Hz still sends the new heading; rotation >= 0.05 rad/s counts as moving), `RelayModule.OnPlayerState` / `WorldMirror.ApplyState` (rotation change bumps the version and notifies), `ReplicationModule.Collect` (version change, 20 Hz for player ships, `Rot` mask), `GhostDriver::frame` (`pose_differs` includes rotation, parked re-assert every 250 ms only repeats the same pose).
- **Fix:** `has_pose = mask & (Pos | Rot | Vel)`: an omitted POS means "equal to the baseline" (the server compares against the client's acked baseline), so the baseline position is exactly right and a rotation- or velocity-only entry (ship turns in place / stops) is a true pose sample (it carries TIME like POS). Only flags / status-only entries stay state updates. No server change, no bandwidth change (the wire format is untouched; an idle ship that does not rotate still sends nothing but keyframes).
- **Tests:** Catch2 `test_replication.cpp` (`[m3-26]`: ROT-only and VEL-only entries are pose samples with the baseline position; StreamSet shows each new heading within the display delay after a long silent keyframe gap). E2E: `m3_client.hostsim` / `m3_authority.hostsim` wait for the 1.2 rad heading with `until 5000 expect-object ...` (replaces the fixed `frame 60Hz 300`, the flake of two failures with yaw -2.40 / 1.83 = the old circling heading) and add three stationary turns (-0.8, 2.0, 0.3 rad) the client's ghost must reach within 1.5 s each.
- **Seen, not changed (Finding 9):** extrapolation after a long gap continues the previous segment's angular rate (`Interpolator::evaluate`), harmless at the 5-20 Hz send rates; the adaptive display delay at the 250 ms cap on a 6 ms link is the jitter estimator's business (not touched).
