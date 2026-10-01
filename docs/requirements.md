# X4MP (new) — Requirements & Pitfalls derived from the reference audit

> See docs/architecture.md — it is authoritative where this doc differs.

Source: full read of `reference/` (clone of <previous-multiplayer-mod-repo>):
README.md, STATE.md (all 1172 lines), FOLLOWUP.md, from_LLM_to_LLM.md, FEATURES.csv,
install.sh, scripts/*.sh, x4mp_linux/*, x4mp_windows/* (READ ME FIRST.txt, README.txt,
TESTING.md, all .bat, tools/*.ps1, tools/fake_client.py, patches/0001-*.patch), every
extension content.xml / ui.xml / x4native.json / md / libraries / t / ui Lua file, the
x4native `version_db/*.json`, plus a `strings` pass over every shipped `.dll`/`.so`
(x4mp, x4mp_stream, x4native_core, x4native_64).

**Clean-room note.** The reference has no license and ships binaries only. This document
records *behaviour, facts about the game, and lessons*. It contains no reference code.
Nothing here should be transcribed into our sources; protocol details are listed so we
understand what was needed, not so we stay compatible (we will not be wire-compatible).

Notation: "old" = reference mod. "authority" = the X4 instance that simulates the
universe (the old "host"). "node" = any X4 instance running our mod. "server" = our
standalone .NET 8 process.

---

## 0. Executive summary of what the old mod was

- Three extensions: `x4native` (eg3r's open-source native loader, Linux port by Neresco),
  `x4mp` (networking, host logic, client control, station/act handling), `x4mp_stream`
  (client-side reconciliation: binding, pinning, ghosts, glide, prune, MD-event capture).
- Host = an X4 instance listening on TCP 7778 (or UDP 7777; legacy split ports existed).
  Clients connect directly. No server process, no auth, no GUI. Config via env vars set
  by a bash/bat launcher. Save files moved by `scp`.
- Everyone loads the *same save*. Host simulates; it keeps a "high-sim set" (host player's
  sector + every client's current sector) active by `ActivateObject` and by planting a
  player-owned satellite in each such sector.
- Host streams the ships of each client's current sector (optionally a radius region).
  Client either binds streamed ships to local copies and pins them (pin+glide), or
  removes its local ships and draws everything as ghosts (`X4MP_GHOSTS=1`, the final
  recommended mode).
- Players see each other as ghost ships spawned under *real* NPC factions.
- Events: kills, player death, cargo, station trades, captures, station builds — detected
  on the acting client (MD events / polling), sent as `ACT ...` to the host, applied
  authoritatively, re-broadcast.
- Verified: Linux 2-machine co-op flight, stations both ways. Windows: host only, single
  machine; the Windows client path has **never run**. Combat/trade/boarding: deployed,
  never validated in-game.

---

## 1. Feature inventory → our requirements

Status legend (old): OK = verified working; T = implemented but untested in-game;
NO = not implemented; WIN? = never run on Windows.
Priority: **must** (M1–M6 scope, needed for a usable co-op session), **should**
(planned, after core), **later** (stretch / needs RE).

### 1.1 Networking & session

| Old feature | Old status | Our requirement | Pri |
|---|---|---|---|
| Host listens TCP 7778, client connects by IP | OK (Linux), host-only on Windows | **REQ-001** Standalone server accepts node connections on one configurable TCP port (default **47780**; UDP 47781 optional, HTTP 47790 admin — ADR-006); all nodes (authority included) are outbound clients of the server. | must |
| UDP transport option, legacy split ports | OK-ish | **REQ-002** One TCP connection per node carries Control and Bulk (and Realtime as fallback). An optional UDP Realtime lane is negotiated as a capability (`UdpRealtime`), never a second "mode" the user picks (ADR-006). | should |
| JOIN/WELCOME handshake, no auth, no versioning | OK | **REQ-003** Versioned handshake: protocol version, mod build id, game build (`version.dat` + build number), DLC/extension list hash, save checksum, player key, display name, optional session password. Server rejects mismatches with a human-readable reason shown in game and in GUI. | must |
| Persistent client identity (`x4mp_client.key`, `key=`/`name=`) | OK | **REQ-004** Persistent player identity (GUID + display name) stored in a per-user config file; server keeps a registry (SQLite) so returning players map to the same slot/faction/assets. Names may contain spaces (old protocol forbade them). | must |
| PING/PONG every ~5 s, host prunes after 30 s, client reconnects after 15 s | OK | **REQ-005** Heartbeat both directions with configurable timeouts; server-side liveness; client auto-reconnect with backoff; reconnect within a grace window resumes the same session slot (no duplicate player). | must |
| Accept-dedupe by peer IP (fix for 156 stale entries) | OK | **REQ-006** Server identifies sessions by player key + resume token, not IP; a new connection for an existing key atomically replaces the old one. | must |
| Rate limit 15 Hz (`X4MP_UPDATE_HZ`), delta threshold (`X4MP_DELTA_M`) | OK, later reverted to full snapshot every tick for correctness | **REQ-007** State streaming at configurable rate (default 10–20 Hz) using binary, length-prefixed frames with sequence numbers; delta encoding against acknowledged baselines with periodic keyframes. | must |
| Multi-thread streams (`X4MP_STREAMS`) | experimental, off | **REQ-008** Network I/O off the game main thread in the native mod, exchanging data with the main thread via bounded lock-free queues; never block the frame. | must |
| Hardcoded default IP 192.168.1.16 | bug | **REQ-009** Server address entered in game (Join dialog) or in mod config; remember last servers; optional LAN discovery broadcast. | must |

### 1.2 World sync

| Old feature | Old status | Our requirement | Pri |
|---|---|---|---|
| Same-save model; host simulates; high-sim set per client sector (ActivateObject + satellite) | OK | **REQ-101** Authority keeps every connected player's interest area fully simulated. Mechanism must not leave persistent objects in the save (see PIT-034). | must |
| Stream ships of client's current sector; FULL snapshot on entry | OK | **REQ-102** Interest management on the server: each node subscribes to a region (current sector + neighbours / radius); server fans out authority snapshots by subscription. | must |
| Region streaming (`X4MP_REGION`, 120 km radius) | experimental | **REQ-103** Region subscription with hysteresis and pre-subscription of the destination sector (gate/highway/jump) to avoid sector-entry churn. | must |
| Ghost rendering (client removes local NPC ships, draws authority world) | OK, recommended | **REQ-104** Default client render mode = authority-driven: in the interest area, client local NPC ships are suppressed and replaced by authority-driven proxies keyed by authority network IDs. No macro+nearest guessing. | must |
| Pin + glide / hybrid binding | OK but flickers | **REQ-105** (Optional) "bind" mode that reuses a local object only when it can be identified exactly (same persistent ID from the save); otherwise proxy. | later |
| Interpolation (`X4MP_SMOOTH_TAU`, `X4MP_MAX_LAG_M`), glide 1500 m/s up to 20 km | OK | **REQ-106** Client-side interpolation buffer (time-based, ~100–200 ms) with extrapolation cap and smooth correction; snaps only when offscreen/far. | must |
| Prune missing ships after 30 s grace | OK | **REQ-107** Explicit despawn messages from authority (destroyed / left interest / docked) instead of absence-based pruning; absence timeout only as a safety net. | must |
| Players see each other as ghost ships under real factions | OK | **REQ-108** Every remote player is rendered as a proxy ship with the correct macro; proxy respawned on ship swap; identity label (player name) visible. | must |
| Camera snap (host teleports its view to client on join; `SNAP`) | OK | **REQ-109** No host camera manipulation. Optional "go to player" admin action from GUI. | later |
| Host-player teleport into client sector (`X4MP_TELEPORT`) | legacy hack | Dropped. | — |
| Full-universe OBJ broadcast (`X4MP_STREAMSHIPS`) | crashed client (ID map full) | Dropped; replaced by budgeted interest (REQ-110). | — |
| Zone-limited spawning to protect ID map | OK | **REQ-110** Hard budget on proxies per node (count and spawn-per-frame), configurable, logged when hit. | must |
| Stations not streamed (client keeps own) | design choice | **REQ-111** Static save content (stations, gates) is assumed identical from the shared save; only *changes* (new, destroyed, owner change, build progress) are replicated. | must |
| Sector mapping by sector **macro** (runtime IDs differ) | OK | **REQ-112** All cross-instance references use stable keys (sector/cluster macro, authority network ID, or save-persistent ID verified at join), never raw runtime UniverseIDs. | must |

### 1.3 Gameplay events

| Old feature | Old status | Our requirement | Pri |
|---|---|---|---|
| Client station build → host (`ACT BUILD`) with baseline + dedupe by seq | T (host side verified) | **REQ-201** Client-built stations replicate to authority and all nodes; idempotent by build ID. | should |
| Host-built stations → clients (`STA`, baseline at universe ready) | OK | **REQ-202** Authority-built stations replicate to all nodes. | should |
| Host persists client stations per key (`clientstate.txt`) | in binary | **REQ-203** Server persists player-owned assets per player key so they survive reconnects/restarts. | should |
| Kills via MD `Killed` → `ACT KILL` → host `RemoveComponent` → `KILL` | T | **REQ-204** Destruction events replicate authoritatively (authority decides; clients only report). | should |
| Player death `ACT PLAYERDIED` → `PLAYERDIED` | T | **REQ-205** Player death/respawn replicates; never remove a player's own ship remotely. | should |
| Cargo diff every 5 s (`ACT CARGO`/`CARGO`) | T | **REQ-206** Player ship cargo replicates (event-driven where possible, poll fallback). | should |
| Station trade relay (`ACT TRADE`/`TRADE`): buys via `DropCargo` bulk, sells via single-unit `AddTradeWare` | T | **REQ-207** Player trades replicate to the authority's station state. | should |
| Credits sync | NO (no API) | **REQ-208** Player money handled by server ledger (server is the bank of record per player) — feasibility depends on API research. | later |
| Boarding: inert exemption on `BoardingOperationStarted/Removed`; capture via `EntityChangedOwner` → `CAPTURE` | T | **REQ-209** Ownership changes (captures) replicate; local boarding works in authority-driven mode. | should |
| Boarding operation (marines/phases) on peers | NO | **REQ-210** Boarding operation replication. | later |
| Shots / damage / projectiles | NO (no targeted orders on Linux) | **REQ-211** Combat state replication (hull/shield, attack orders). On Windows the internal RVA DB has `CreateOrderInternal`/`SetOrderParamInternal` — research it. | later |
| Station own economy | NO | **REQ-212** Station production/stock is authority-owned; nodes get periodic stock snapshots for stations in interest. | later |
| Chat | none | **REQ-213** Text chat (in-game + GUI broadcast). | should |

### 1.4 Save, launch, install, ops

| Old feature | Old status | Our requirement | Pri |
|---|---|---|---|
| Save transfer by scp (passwordless SSH) | OK on Linux; Windows needs sshd | **REQ-301** Server distributes the session save with SHA-256, in-band over the Bulk lane by default and over HTTP as a fallback (ADR-007); node downloads automatically into the correct per-account save folder before loading. | must |
| `X4MP_SAVE` base name (no `.xml.gz`) | OK | **REQ-302** Save identity = checksum + generated name (e.g. `x4mp_<session>_<n>`), never a user-typed name. | must |
| "Cannot join a NEW game" | limitation | **REQ-303** Starting a session from a new game: authority creates the universe, saves, uploads; clients then download and load. | must |
| Auto-start vs in-game menu | both | **REQ-304** In-game Host/Join UI is primary; no env-var auto-start. Optional command-line/config "auto-join" for testing. | must |
| Launcher writes `steam_appid.txt` | OK | **REQ-305** Config is read from a file (not env); `steam_appid.txt` not required (see §7). | must |
| Install.bat/ps1, Check Install, Collect Logs, Uninstall, Make Zip | OK | **REQ-306** Installer, health check, log collector, uninstaller (mod side); server ships as a single self-contained exe/folder. | must |
| Debug logging (`X4MP_DEBUG`), log files under x4native folder | OK (`X4MP_LOG` ignored on Windows) | **REQ-307** Structured logs on node and server; nodes stream warnings/errors and periodic stats to the server; GUI log viewer. | must |
| Perf log (`role fps net_update_avg_us clients`) | in binary | **REQ-308** Nodes report FPS, frame time, net time, queue depths, proxies count; GUI dashboard. | must |
| Diagnostics `[FRM]/[FLK]/[FLKV]/[CONVERGE]` | OK | **REQ-309** Built-in sync diagnostics (drift, spawn/despawn rates, budget hits) exposed as metrics, toggleable at runtime from GUI. | should |
| Fake client harness | OK | **REQ-310** Fake node + fake authority simulators (see §8). | must |
| Admin: none | — | **REQ-311** Web GUI: players, ping, bandwidth, FPS, kick/ban, session settings, save management, log viewer, galaxy map. | must |

---

## 2. Wire protocol as observed (old, text, newline-delimited)

Reconstructed from fake_client.py, STATE/FOLLOWUP, launcher docs and format strings in
the binaries. Fields are space-separated; floats printed `%.3f` (positions in metres,
sector-relative), rotations yaw/pitch/roll in degrees (rad in some paths — unclear).
Direction: C→H client to host, H→C host to client.

### 2.1 Handshake & liveness
| Verb | Dir | Fields | Notes |
|---|---|---|---|
| `JOIN x4mp <key> <name>` | C→H | key = persistent GUID/hex, name = single token | Host replies WELCOME; re-JOIN refreshes liveness. Early legacy: `JOIN` alone. |
| `WELCOME <id>` | H→C | id = host-assigned small int | id also selects the ghost faction index. |
| `LOADING`, `STREAM`, `STREAM <n>` | C→H (inferred) | — | Present in binary; legacy split-port handshake (client loading / data-stream port). Semantics not documented. |
| `PING <n>` | H→C | counter | ~every 5 s. |
| `PONG` | C→H | — | |
| `INPUT tick=<u>` | C→H | client tick | Legacy uplink placeholder, rate-limited. |

### 2.2 State stream
| Verb | Dir | Fields | Notes |
|---|---|---|---|
| `PLAYER <x> <y> <z> <yaw> <pitch> <roll> <ship_macro> [<sector_macro>]` | C→H | | Client's own ship. **No id field** — differs from the H→C form (caused PIT-026). |
| `PLAYER <cid> <x> <y> <z> <yaw> <pitch> <roll> <macro> <faction> [<sector_macro>]` | H→C | cid 0 = host's ship, N = client N | Relay of every player's ship; 10th field optional for old hosts. |
| `SNAP <playerid> <zoneid> <x> <y> <z> <yaw> <pitch> <roll> <hosttick>` | H→C | | Host player state / camera snap (early form had no zone). |
| `FULL 1` | H→C | | Marks that the OBJ lines following form a complete snapshot of the client's sector/region. Sent every tick in the final build. Bug: header sent without `\n` (PIT-024). |
| `OBJ <id> <sector_macro> <x> <y> <z> <yaw> <pitch> <roll> <faction> <macro>` | H→C | | One ship. Early form: `OBJ <id> <zone> <sector> x y z ... faction macro`. |
| `STA <id> <sector_macro> <x> <y> <z> <yaw> <pitch> <roll> <faction> <macro>` | H→C | faction usually `player` | Host-built station; re-sent every ~10 s so client stale-drop never removes it. |

### 2.3 Actions (client reports) and authoritative broadcasts
| Verb | Dir | Fields | Notes |
|---|---|---|---|
| `ACT test n=<tick>` | C→H | | Transport test (`X4MP_TEST_ACTION`). |
| `ACT BUILD <seq> <macro> <x> <y> <z> <yaw> <pitch> <roll> <sector_macro>` | C→H | seq = client-local build counter | Re-sent every scan (~10 s); host dedupes by seq; spawns under client ghost faction. |
| `ACT KILL <host_id>` | C→H | host object id | Host `RemoveComponent` + broadcast. |
| `KILL <host_id>` | H→C | | Clients remove proxy/bound ship. |
| `ACT PLAYERDIED` | C→H | | |
| `PLAYERDIED <key>` | H→C | key = sender's relay key (hash) | Sent to the *other* clients. |
| `ACT CARGO <count> <ware> <amt> ...` | C→H | | Only sent if non-empty (avoids flaky-empty clears). |
| `CARGO <key> <count> <ware> <amt> ...` | H→C | | Applied to that player's ghost. |
| `ACT TRADE <station_id> <x> <y> <z> <ware list>` | C→H | station UniverseID + position fallback | |
| `TRADE <station_id> <x> <y> <z> <ware list>` | H→C | | |
| `ACT CAPTURE <host_id>` | C→H | | Host `SetComponentOwner` to client's ghost faction. |
| `CAPTURE <host_id> <faction>` | H→C | | |

### 2.4 Internal buses (in-process, not wire)
- x4mp → x4mp_stream: event `x4mp_stream.data` carries raw received lines.
- x4mp_stream → x4mp: event `x4mp.send_act` carries `ACT ...` lines to send.
- Lua → native: `x4mp_host_request`, `x4mp_join_request(<ip>)`; Lua bridges `x4mp.host`/`x4mp.join`.
- Native → Lua: `x4mp.pause`, `x4mp.unpause`, `x4mp.debug_api`, game `loadSave(<name>)`.

### 2.5 Host persistence file (observed format strings)
`clientstate.txt` under `~/.config/x4mp/` (Windows build references `USERPROFILE`):
header `# x4mp client state (host-side persistence)`, lines `client <key> <name> <faction>`
and `station <key> <macro> <x> <y> <z> <yaw> <pitch> <roll> <sector_macro>`.

### 2.6 Observed volumes
- ~84–92k ships in a mid-game universe; ~139 sectors.
- Text OBJ ≈ 100 B; 382 objects × 15 Hz ≈ 570 KB/s per client.
- fake_client against Windows host, 25 s: 101k OBJ, 1.9k FULL, 1.9k SNAP/PLAYER, 59 distinct ids (FULL every tick ⇒ heavy redundancy).
- UDP batches capped at 60000 B (near 65507 limit).

### 2.7 What we change (protocol requirements)
- **REQ-401** Binary, length-prefixed framing; schema (IDL) generating C++ and C# codecs; no hand-written string parsing.
- **REQ-402** Distinct message types per direction (no shared verb with different field layouts).
- **REQ-403** Every message carries/derives a sender identity from the session, never from payload (no "key" spoofing).
- **REQ-404** Snapshots carry: sequence, authority tick, region id, `complete` flag, and an explicit list of despawns. Absence never implies deletion unless `complete` and authority index for that region is fresh (PIT-029).
- **REQ-405** Network IDs assigned by the authority (stable for object lifetime), with a per-node map net-id → local proxy id.
- **REQ-406** Actions carry a client-generated idempotency ID; server/authority ack with result (accepted/rejected/applied) so clients can roll back previews.
- **REQ-407** Capability flags + protocol version negotiated at handshake.

---

## 3. Config knobs (env vars) and our disposition

All were read from the process environment by the native DLLs (Lua cannot read env).
"Keep" = concept survives as a server-side session setting (S), node-side mod setting
(N), or debug setting (D). Defaults are from the launchers / docs.

| Env var | Default | Read by | Meaning | Ours |
|---|---|---|---|---|
| `X4MP_AUTO` | unset | x4mp | `host`/`client` auto-start on load | Drop; replaced by in-game UI + optional `autojoin` in node config (D). |
| `X4MP_SERVER_IP` | 192.168.1.16 | x4mp | Host IP | Keep (N): server address, remembered list. |
| `X4MP_PORT` | 7777 | x4mp | UDP/legacy control port | Keep (S): listen ports 47780/47781/47790 (server boot config). |
| `X4MP_STREAM_PORT` | 7778 | x4mp, stream | data port | Merge into single port. |
| `X4MP_TRANSPORT` | tcp | both | tcp/udp | Drop as user choice; capability-negotiated later. |
| `X4MP_LEGACY_NET` | 0 | both | split vs consolidated ports | Drop. |
| `X4MP_SAVE` | unset | x4mp | save base name to load | Replace by server-distributed save (S). |
| `X4MP_SAVE_DIR` | auto | launcher | save folder override | Keep (N): override for per-account save folder. |
| `X4MP_MODULE` | x4ep1_gamestart_boron1 | x4mp | gamestart for new game | Keep (S): new-session gamestart. |
| `X4MP_DIFFICULTY` | easy | x4mp | new-game difficulty | Keep (S). |
| `X4MP_CLIENT_KEY` / `X4MP_CLIENT_NAME` | generated | x4mp | identity | Keep (N): identity file. |
| `X4MP_UPDATE_HZ` | 15 | x4mp | stream rate 1–60 | Keep (S). |
| `X4MP_DELTA_M` | 0.5 | x4mp | min movement before resend | Keep (S) as delta quantisation threshold. |
| `X4MP_HOST_TIMEOUT` | 30 s | x4mp | prune silent clients | Keep (S). |
| `X4MP_CLIENT_TIMEOUT` | 15 s | x4mp | client reconnect trigger | Keep (N/S negotiated). |
| `X4MP_UNIVERSE_TIMEOUT` | 180 s | x4mp | fallback "ready" if universe_ready never fires (0 = strict) | Keep (N). |
| `X4MP_OBJMODE` | cache | x4mp | cache vs full enumeration | Drop (implementation detail). |
| `X4MP_STREAMSHIPS` | 0 | x4mp | full-universe OBJ broadcast | Drop (crashes ID map). |
| `X4MP_FULLSIM` | 0 | x4mp | activate ALL ships (CPU heavy) | Drop or D. |
| `X4MP_TELEPORT` | 0 | x4mp | move host player into client sector | Drop. |
| `X4MP_CLEANUP` | 0 | x4mp | client removes own ships | Subsumed by REQ-104. |
| `X4MP_PAUSE` | 0 | x4mp | client calls `Pause()` | Drop (pauses everything). |
| `X4MP_STREAMS` | 0 | x4mp | # off-main-thread stream threads | Drop; our net thread is always off-main (REQ-008). |
| `X4MP_RELEVANCE_M` | 20000 | x4mp | relevance radius | Keep (S): interest radius. |
| `X4MP_REGION` / `X4MP_REGION_M` | 0 / 120000 | both | region streaming radius (must match both sides) | Keep (S) — server-owned, so it can never mismatch. |
| `X4MP_INERT` | 1 | stream | deactivate local AI of streamed ships | Keep (N, internal) as part of authority-driven mode. |
| `X4MP_GHOSTS` | 0 (launcher picks 1) | stream | render authority world as ghosts | Default behaviour (REQ-104). |
| `X4MP_PIN_INTERVAL` | 1 | stream | frames between pins | Keep (D). |
| `X4MP_RENDER_INTERVAL` | 3 | stream | frames between render passes | Keep (D). |
| `X4MP_SMOOTH_TAU` | 0.12 | stream | interpolation smoothing constant | Keep (N) as interpolation delay. |
| `X4MP_MAX_LAG_M` | 300 | stream | max interpolation lag (m) | Keep (N). |
| `X4MP_BIND_RADIUS` | 1000 | stream | re-match radius | Only for later bind mode. |
| `X4MP_CONVERGE_RADIUS` | 20000 | stream | entry convergence radius (non-greedy) | Drop. |
| `X4MP_CONVERGE_GREEDY` | 1 | stream | unlimited-distance entry binding | Drop (caused mis-binds). |
| `X4MP_SYNC_M` | 100 | stream (older) | drift threshold before correction | Keep concept (N): correction threshold. |
| `X4MP_GLIDE_SPEED` / `X4MP_GLIDE_MAX` | 1500 m/s / 20000 m | stream | glide instead of teleport | Keep (N). |
| `X4MP_HYBRID_GHOST` / `_M` | 1 / 3000 | stream | in hybrid, ghost ships diverged > M | Drop with hybrid. |
| `X4MP_SOFT_PIN_SPEED` / `_MIN` | 3000 / 50 | stream | soft correction speed / min drift | Keep concept (N) in correction. |
| `X4MP_DRIFT_ALERT_M` / `_VERBOSE_M` | 5 / 1000 | stream | drift diagnostics thresholds | Keep (D). |
| `X4MP_FRAME_DIAG_S` | 5 | stream | frame diagnostic period | Keep (D). |
| `X4MP_AUTOFLY` / `_INTERVAL` | 0 / 10 | stream | test hook: teleport through sectors | Keep (D) as a test driver (note: fails while docked). |
| `X4MP_TEST_ACTION` | 0 | x4mp | periodic test ACT | Keep (D) as protocol self-test. |
| `X4MP_TEST_MENU` | unset | x4mp | simulate menu click | Keep (D). |
| `X4MP_DEBUG` | 0 | both | verbose logs | Keep (N/D): log level, runtime-switchable from GUI. |
| `X4MP_LOG` | 1 | launcher only | on-disk log (ignored on Windows!) | Always log; level configurable. |
| `X4MP_PERF_LOG` | unset | x4mp | perf log path (hardcoded `/tmp/...` even on Windows) | Replace by metrics to server (REQ-308). |
| `X4MP_RESUME_ROLE` / `_IP` / `_MODULE` | set at runtime | x4mp | survive extension restart via process env | Keep concept: reload-surviving state (PIT-007). |
| `X4MP_GAME_DIR` | auto | scripts | game folder | Installer auto-detect + override. |
| `X4MP_FORCE_RADV` | auto | Linux launcher | force AMD Vulkan ICD | Linux-only, later. |
| `X4MP_GAME_ARGS` | — | Linux run | extra X4 flags (`-showfps`, `-nocputhrottle`) | Keep in launcher (N). |

**Concepts we keep (summary):** server address/port, identity, session password (new),
update rate, delta threshold, interest radius/region, timeouts (host, client, universe
ready), proxy budget (new), interpolation/glide/correction tunables, log level,
diagnostics toggles, test drivers. **Rule: session-wide values live on the server and are
pushed to nodes at handshake** so they cannot mismatch (old README: "make sure region
streaming is set the SAME on both sides").

---

## 4. Game functions, Lua APIs and MD events the old mod used

Function names are not present as strings in the mod binaries (called through
x4native's function table); names below come from the docs and were confirmed present
in `x4native_core.dll`'s table (2065/2065 resolved on 9.00 build 611726).

### 4.1 Exported C functions (via x4native function table)
| Function | Used for | Notes / gotchas |
|---|---|---|
| `GetPlayerObjectID` | player entity | Returns the **character**, not the ship — spacesuit macro when walking/docked (PIT-011). |
| `GetPlayerOccupiedShipID`, `GetPlayerControlledShipID`, `GetPlayerShipID` | player ship | `GetPlayerControlledShipID` = 0 when docked. Union of all sources needed (PIT-010). |
| `GetPlayerZoneID`, `GetContextByClass(id,"sector")` | current sector | |
| `GetPlayerID`, `IsPlayerValid` | "game loaded" polling (Lua FFI) | `GetPlayerID ~= 0` more reliable than `IsPlayerValid`. |
| `GetObjectPositionInSector` | positions | Logs error flood for objects without sector (dying/docked) → guard (PIT-019). |
| `SetObjectSectorPos` | pin proxies/bound ships | |
| `MovePlayerToSectorPos` | early thin client / autofly | Does not change zone while docked. |
| `SpawnObjectAtPos2(macro, sector, pos, owner)` | ghosts/proxies, stations, satellites | owner must be an existing faction id string; `nullptr` ⇒ returns 0 silently (PIT-013). |
| `RemoveComponent` | prune, ghost mode suppression, kills | Never on player ship (PIT-010). |
| `IsValidComponent` | stale-ID guard | Dying objects can be valid but sectorless. |
| `ActivateObject(id, bool)` | high-sim on authority; inert (AI off) on clients | Inert blocks local boarding (needs exemption). |
| `GetNumAllFactionShips/GetAllFactionShips`, `GetNumAllFactionStations/GetAllFactionStations` | enumeration | Size buffers from `GetNum*` (PIT-015); `includehidden` returns duplicates (PIT-016). |
| `GetNumAllFactions/GetAllFactions` | real faction list for ghost owners | |
| `GetOwnerDetails2` | owner faction (capture detection) | |
| `SetComponentOwner` | apply capture | |
| `GetComponentName` | — | Returns **display names** ("Argon Prime"), not macros (PIT-018). |
| `GetNumCargo/GetCargo(container, tags="")` | cargo diff | `tags` must be `""`, not nullptr. |
| `AddTradeWare(container, ware)` | station/ghost gaining wares | **One unit per call**. |
| `DropCargo(container, ware, amount)` | station/ghost losing wares | Bulk. |
| `AddPlayerMoney` | — | Affects local player only; no `GetPlayerMoney` export found. |
| `CreateOrder3` | considered for combat | No target parameter. |
| `CreateBoardingOperation`, `AddAttackerToBoardingOperation` (+ `AbortBoardingOperation`, `GetAllBoardingPhases`, …) | future boarding replication | Exported. |
| `NewMultiplayerGame`, `ConnectToMultiplayerGame` | built-in SLNet/RakNet MP | **Unusable without Steam/EgoNet** ("Failed to initialize the network engine"); calling `NewMultiplayerGame` synchronously from a menu handler crashed (needed 0.1 s deferral). The old host path still called it vestigially. We must not. |
| `NewGame`, `ContinueGameStart` | new game / continue | |
| `IsSaveListLoadingComplete` | readiness | Returns true immediately at init — **not** a "menu ready" signal. |
| `IsSaveValid(name)` | validate save | Rejects names with `.xml.gz`. |

### 4.2 Internal (non-exported) functions via `version_db/internal_functions.json`
Resolved at runtime from RVAs keyed by build (`900-606138`, `900-607242`, `900-607977`,
`900-611726`; Windows X4.exe only): `X4_FrameTick` (native frame hook),
`EventQueue_InsertOrDispatch` (MD event hook), `RadarVisibilityChanged_BuildEvent`,
`IsRadarVisible_ReadByte`, `SetObjectRadarVisible_Action`, `CreateOrderInternal`,
`SetOrderParamInternal`, `SetCommander`, `SetAssignment`, `CreateDynamicInterior`,
`MacroRegistry_Lookup`, `ComponentRegistry_Find`, `Component_GetCombinedSeed`,
`ConstructionDB_CreatePlanDirect/AddPlan`, `PlanEntry_Construct`, `GameAlloc`,
`GetFactionBuildMethod`, `FactionRelation_LookupReasonID/GetFloat`,
`Screenshot_ArmSingleShot` (611726 only).
**Implication for us (Windows-first):** targeted orders and radar visibility *are*
reachable on Windows — the old "blocked" combat features were blocked by Linux only.
These are per-build and break on every game patch (PIT-045).

### 4.3 Lua (UI environment)
`RegisterEvent`/`UnregisterEvent`, `SetScript("onUpdate", fn)` (per-frame tick),
`Helper.addDelayedOneTimeCallbackOnUpdate` + `getElapsedTime` (deferral),
`GetUISafeModeOption()` (protected UI check), `package.loadlib` (DLL load — requires
protected UI off), `require("ffi")`, `require("debug")` (global `debug` is nil; needed to
read `OptionsMenu.displayOptions` upvalue `config` for menu injection), `Menus` table /
`OptionsMenu`, `menu.submenuHandler`, `config.optionDefinitions["main"]`,
`ScheduleReloadUI`, `DebugError`, `ReadText`, `Pause`/`Unpause`, `LoadGame(name)`,
game Lua event `loadSave`, `GetComponentData(id, "macro")` (correct macro lookup).
Lua has **no `os.getenv`**.

### 4.4 Mission Director
Cues in `x4native/md/x4native_main.xml` raise Lua events:
- `event_game_loaded` → `x4native.game_loaded` → native `on_game_loaded` (1st pass; IDs valid, universe NOT built).
- `event_game_started` → `on_game_started` (**new game only**).
- `event_universe_generated` → `on_universe_ready` (both new & loaded; the real "world ready").
- `event_game_saved` → `on_game_save` (after the save — too late to clean ghosts).

Typed MD event hook (x4native `md_subscribe_before`, IDs from SDK `x4_md_events.h`):
`Killed` = 237 (source = killed, raw+0x18 = killer), `EntityChangedOwner` = 175,
`BoardingOperationStarted` = 41, `BoardingOperationRemoved` = 40,
`BoardingPhaseChanged` = 42, `AttackStarted` = 32, `AttackStopped` = 33.
Callbacks may run on worker threads.

### 4.5 x4native framework surface (open source upstream — usable by us)
Exports `x4native_api_version`, `x4native_init`, `x4native_shutdown`; manifest
`x4native.json` (`library`, `priority`, `min_api_version`, `autoreload`, optional
`settings` array → rows injected into Settings → Extensions → "..." as toggle/slider/
dropdown, persisted per profile, `on_setting_changed`). Events: `on_game_loaded`,
`on_game_started`, `on_universe_ready`, `on_game_save`, `on_frame_update` (Lua onUpdate),
`on_native_frame_update` (hooked), `on_ui_reload`, `on_before_reload`,
`on_radar_changed`. API: `subscribe`, `raise_event`, `raise_lua_event`,
`register_lua_bridge`, `log` (not variadic), `set_log_file`, hook manager
(MinHook detours, before/after hooks with crash isolation). Logs:
`%USERPROFILE%\Documents\Egosoft\X4\<id>\x4native\x4native.log` + per-extension logs.
Proxy (`x4native_64.dll`) copies `x4native_core.dll` → `x4native_core_live.dll` for
hot-reload (source of the Linux race, PIT-040).

---

## 5. Pitfalls & hard-won lessons (PIT-xxx) with required mitigation

### 5.1 Platform / loading
- **PIT-001 Steam relaunch drops the environment (Windows).** X4 launched outside Steam re-launches via `SteamAPI_RestartAppIfNecessary`; the child keeps the command line but inherits steam.exe's env. *Mitigation:* no configuration via env vars at all; node config in a file under the user profile/extension folder; session config pushed by the server.
- **PIT-002 Lua has no `os.getenv`.** The old Join button always used the hardcoded `192.168.1.16`. *Mitigation:* Join dialog with editable address; native side owns config and exposes it to Lua via bridge.
- **PIT-003 Protected UI mode blocks `package.loadlib`.** Mod looks installed and enabled but nothing happens. Stored as `<uisafemode>` in `<profile>\config.xml`. *Mitigation:* installer/health check reads it; Lua shows a visible warning if the native layer failed to load.
- **PIT-004 VC++ 2015–2022 x64 runtime** required by x4native DLLs; missing ⇒ silent no-load. *Mitigation:* build our native DLL with static CRT; installer checks MSVCP140/VCRUNTIME140/VCRUNTIME140_1 and offers the redist link.
- **PIT-005 Mark-of-the-Web** on DLLs from a downloaded zip can block loading. *Mitigation:* installer `Unblock-File`s everything; health check detects `Zone.Identifier`.
- **PIT-006 Missing `version_db` ⇒ MD/frame/radar hooks silently disabled** (packaging bug that disabled kills/captures/boarding). *Mitigation:* mod self-test at startup reports hook resolution status to the server; GUI shows per-node capability; features that depend on unresolved hooks are disabled with a clear warning; packaging test asserts the folder exists.
- **PIT-007 Extension restart on Lua-state reload.** Loading a game reloads the UI; x4native "re-discovers" and **shuts down/re-inits** native extensions, destroying globals and closing sockets (`autoreload:false` does not prevent it). *Mitigation:* treat init/shutdown as routine; persist intent (role, server, session token) in a reload-surviving store (process env or temp file keyed by PID); net client reconnects and resumes via token; server holds the slot through a grace period.
- **PIT-008 Two-pass save load.** `on_game_loaded` fires after pass 1; touching objects then raced the "Movement worker" → heap corruption SIGSEGV. *Mitigation:* do no world mutation before `on_universe_ready`; timeout fallback only after `game_loaded`; state machine with explicit phases reported to server.
- **PIT-009 MD event callbacks run on worker threads.** *Mitigation:* callbacks only copy POD data into a queue; all game API calls on main thread (frame update).
- **PIT-040 x4native proxy hot-reload copy race** (Linux: concurrent copies to the same `_live` file corrupted it; crashed on `NewMultiplayerGame`/resolution change). *Mitigation:* ship with autoreload off; prefer an x4native version without the copy race; never call MP engine functions.
- **PIT-041 Readiness signals lie.** `IsSaveListLoadingComplete` true at init; calling network functions at init failed; game reaches menu ~14 s, extension init ~20 s, save load up to 180 s (26 s typical). *Mitigation:* explicit lifecycle state machine; generous, configurable timeouts; progress reported to server/GUI.
- **PIT-057 Menu injection is fragile.** Entries injected by pulling the `config` upvalue of `OptionsMenu.displayOptions` via `require("debug")`, appended to `optionDefinitions["main"]` (appear at the very bottom, under "Exit to Desktop"), with host action deferred 0.1 s. *Mitigation:* isolate UI injection in one Lua module with defensive checks, version-tested; log success marker (`menu installed`) that the health check looks for.

### 5.2 Game API semantics
- **PIT-010 Never `RemoveComponent` the player's ship** ⇒ `Game Over (killmethod=removed)` ⇒ menu ⇒ reload loop. Docked player ⇒ `GetPlayerControlledShipID()==0`, so a single source misses it. *Mitigation:* `PlayerShipSet` = union of occupied/controlled/ship/object + its ship context, re-evaluated live before every removal; central "safe remove" wrapper used everywhere.
- **PIT-011 `GetPlayerObjectID` is the character** (spacesuit macro when walking/docked). *Mitigation:* prefer occupied → controlled → ship IDs for anything ship-related.
- **PIT-012 Proxy model is fixed at spawn.** Ship swap left everyone seeing the old model. *Mitigation:* proxies keyed by (net id, macro); macro change ⇒ despawn + respawn.
- **PIT-013 `SpawnObjectAtPos2` needs an existing faction**; custom factions added via `libraries/factions.xml` diff (`x4mp_host`, `x4mp_client_1..8`) were **never registered** in the loaded save ("Failed to retrieve owner faction"); invalid owner returns 0 silently and the old code retried every frame (~2.4 M errors/hour). *Mitigation:* validate owner faction exists before spawning; spawn failures back off exponentially and are counted/reported; research whether library-added factions require a new game or an MD `create_faction`-style action.
- **PIT-014 Ghost faction choice.** Deterministic pick from sorted real factions, excluding `player` and the faction the player currently pilots (campaign starts put the player in a foreign "alliance" ship); computed lazily after the universe exists (JOIN arrives before the authority's universe). *Mitigation:* server assigns per-player display faction from the authority's validated list after universe-ready; proxies are additionally tagged so they are never confused with local assets.
- **PIT-015 Fixed enumeration buffers** (2048 per faction) silently truncated ~9 call sites. *Mitigation:* always size from `GetNum*`; assert counts.
- **PIT-016 `includehidden` enumeration returns the same ship under multiple factions** (index 158 was ~10 unique). *Mitigation:* dedupe by ID at every enumeration.
- **PIT-017 Runtime IDs differ between instances** even with the same save (sector IDs observed different). The old trade path assumed station UniverseIDs match with a position fallback — unverified. *Mitigation:* REQ-112/405: stable keys only; at join, build and verify a macro/ID map and report mismatches.
- **PIT-018 `GetComponentName` returns display names.** Sector map had 2 entries instead of 139. *Mitigation:* use `GetComponentData(id,"macro")` (Lua) or an equivalent macro getter.
- **PIT-019 Position reads on sectorless objects flood the log** (~590× per dying/docked object). *Mitigation:* check validity and sector context before reads; rate-limit identical game-API error logs.
- **PIT-020 Stale IDs** cause `Failed to retrieve component` floods. *Mitigation:* `IsValidComponent` before use; drop from caches on first failure.
- **PIT-042 Docked player:** `MovePlayerToSectorPos` doesn't change zone; auto-fly tests fail while docked. *Mitigation:* test drivers undock first or start in space.
- **PIT-043 Unexplained client player-sector drift after load** (1145→1559→1327 over 18 min, no input). *Mitigation:* log player sector transitions with cause; investigate early in M3.
- **PIT-044 API gaps:** no credit read/set (`AddPlayerMoney` local only), `AddTradeWare` single unit (sells converge slowly), `CreateOrder3` has no target. *Mitigation:* server-side ledger for money (REQ-208); batch single-unit adds across frames with budget; use internal-function DB on Windows for targeted orders (REQ-211).
- **PIT-045 Internal RVAs and MD event type IDs are per game build.** *Mitigation:* handshake reports game build; mod refuses event features on unknown builds; CI/doc step to update DB on game patches; health check compares `version.dat`.
- **PIT-047 Inert ships (AI off) block local boarding.** *Mitigation:* activation exemptions for ships in boarding operations (driven by MD boarding events) — and generally a per-object "locally owned interaction" exemption list.
- **PIT-048 Campaign saves where the player pilots a foreign faction:** player-faction fleet heuristics break (old "fleet reassign" had to skip). *Mitigation:* ownership logic uses player identity, not faction string equality.

### 5.3 Networking / protocol
- **PIT-021 Blocking send dropped the game to 5 FPS.** *Mitigation:* REQ-008 (net thread, non-blocking, bounded queues, drop/coalesce policy on backpressure).
- **PIT-022 Windows non-blocking `connect()` reports `WSAEWOULDBLOCK`, not `WSAEINPROGRESS`** — treating it as failure meant *no Windows client could ever connect*. *Mitigation:* platform layer tests; Windows CI test that connects a real socket.
- **PIT-023 Error-string helper returned a pointer to a local buffer (UB).** *Mitigation:* return `std::string`; static analysis/ASan in CI.
- **PIT-024 Text framing bug** (`FULL 1\n` copied without the newline) glued the first object to the header and dropped it every tick — likely cause of the "one ship flickers despite drift=0" mystery. *Mitigation:* REQ-401 generated binary codecs; golden-byte and fuzz tests; fake node asserts per-message integrity.
- **PIT-025 Missing role gate** (UDP path) fed the host its own clients' data as if it were a client: 2.4 M spawn errors/hour, garbage IDs. *Mitigation:* server-centric relay + per-role message whitelist validated by the server and by the node.
- **PIT-026 Same verb, different field layouts per direction** (`PLAYER` with/without cid) + tolerant `sscanf` ⇒ silent field shift. *Mitigation:* REQ-402; strict decode with rejection counters.
- **PIT-027 Reconnects leaked host entries** (g_clients grew to 156; EBADF every ~10 s; net time 1→8 ms/frame). *Mitigation:* REQ-006; connection objects owned by one component; every close logged with site/reason.
- **PIT-028 Silent fd closer never identified.** *Mitigation:* single socket owner, RAII, close-reason logging, connection lifecycle metrics in GUI.
- **PIT-029 Empty snapshot for a not-yet-indexed sector would prune the client's whole sector.** *Mitigation:* REQ-404 — `complete` only when the authority's index for the region is fresh; despawns explicit.
- **PIT-046 Field-delimited names:** names had to be single tokens. *Mitigation:* length-prefixed UTF-8 strings.
- **PIT-062 Bandwidth:** full text snapshot every tick ~0.5–1 MB/s/client. *Mitigation:* binary + delta + quantisation; measure with the fake authority; budget per node in GUI.
- **PIT-063 UDP datagram size** (60000 B cap, fragmentation). *Mitigation:* if UDP is added later, MTU-sized packets (~1200 B) with app-level fragmentation only for reliable channel.

### 5.4 Sync model
- **PIT-030 Population divergence ⇒ mis-binding.** Client universe frozen near save positions while the authority simulated; macro+nearest bound a fighter beside the player to one 117 km away (218 of 268 outliers). Coordinate frames were proven identical — the problem is *which* object, not *where*. *Mitigation:* REQ-104/405: authority IDs, proxies, no guessing.
- **PIT-031 One-sector streaming ⇒ churn at every transition** (spawn 100–700 / zone drops 100–1400 per 15 s on highways; glides up to 447 km). *Mitigation:* REQ-103 region + pre-subscription + hysteresis.
- **PIT-032 Spawning too much exhausts the game ID map** (`AutoIDMap::Insert(): ID map is full`, fatal: 83k ships × ~30 sub-objects). Also VRAM spikes on mass spawns (SIGFPE in `operator delete` on a worker thread). *Mitigation:* REQ-110 proxy budget + per-frame spawn rate limit.
- **PIT-033 Binding leaks** (`g_bound_locals` not released on zone drop ⇒ frozen stale ships). *Mitigation:* single proxy registry with lifecycle state machine and invariant checks (counts reported as metrics).
- **PIT-034 Authority autosave bakes ghosts and high-sim satellites into the save** (a sim satellite from an old session is now in save_009). `on_game_save` fires after saving. *Mitigation:* tag every spawned object; disable or control autosave on the authority (server-triggered saves only) and remove/hide proxies + helper objects *before* save; save-pollution check tool (scan save XML for tagged objects); clients never save session state.
- **PIT-035 "Load-bearing" helper satellite** must not be removed by ghost-mode suppression (old code special-cased it). *Mitigation:* explicit whitelist category for mod-owned helper objects.
- **PIT-036 Absence-based pruning needs grace periods** (10 s → 30 s to stop prune/respawn churn for ships docking/crossing). *Mitigation:* explicit despawns (REQ-107).
- **PIT-037 Station duplicates** — client may already have the station locally (STA dedupe). *Mitigation:* station replication keyed by build ID; on apply, detect an identical local station and adopt instead of spawning.
- **PIT-038 Authority performance** ~25–30 FPS with 1–2 clients; cost is high-sim of extra sectors, not networking (TCP vs UDP A/B: 31.8 vs 28.7 FPS; net_update ~3 ms of 35 ms). *Mitigation:* report authority FPS in GUI; cap high-sim sectors; advise dedicated authority machine.
- **PIT-039 Pause() as thin-client** stopped the player too; early client also still rendered the whole universe (82–145% CPU). *Mitigation:* don't pause; suppress only within interest area.

### 5.5 Process / packaging / ops
- **PIT-049 Logs:** `g_api->log` not variadic; X4 log timestamps ~2 h off wall clock; debug counters were per-load statics that never printed on short cycles. *Mitigation:* our own logging wrapper with UTC timestamps and node clock-offset estimation (server time sync in heartbeat).
- **PIT-050 Package drift:** three copies of extensions (root, `x4mp_linux`, `x4mp_windows`) with different builds; Windows DLL shipped unstripped (3 MB); Windows pkg lacked `x4native_settings.json`, Linux lacked `version_db`; two launcher copies differed (one lacked the identity block). *Mitigation:* single build pipeline producing versioned artifacts; build id in handshake; packaging tests.
- **PIT-051 One X4 instance per machine** ⇒ multiplayer can't be tested on one PC. *Mitigation:* fake node / fake authority (REQ-310) are first-class deliverables.
- **PIT-052 Hardcoded paths/IPs** (`/tmp/x4mp_perf.log` in the Windows DLL, `192.168.1.16`, `save_010` defaults). *Mitigation:* no literals; config + known-folder APIs.
- **PIT-053 Untested client path.** Every Windows result was host-only. *Mitigation:* M2/M3 exit criteria require a real 2-machine Windows session and a recorded checklist.
- **PIT-054 Mods mismatch.** A save only loads identically with the same extensions/DLCs. *Mitigation:* handshake compares extension/DLC list; guide for a vanilla second install.
- **PIT-055 Firewall "block" answer is remembered** ⇒ client just times out. *Mitigation:* only the **server** listens (nodes are outbound); installer adds a firewall rule for the server exe (Private profile); health check tests reachability from the GUI.
- **PIT-056 Linux process name `Main()`** breaks `pkill -x`; Linux Vulkan ICD forcing broke NVIDIA; glibc symbol bumps (`sqrtf@GLIBC_2.43`). *Mitigation:* noted for a later Linux port.

---

## 6. Known bugs / limitations in the reference and how our design avoids them

| # | Reference bug / limitation | Our avoidance |
|---|---|---|
| B1 | In-game Join always connects to hardcoded 192.168.1.16 (Lua `os.getenv` is nil) | Join dialog + config file (REQ-009). |
| B2 | Menu "Host" lost its listener after universe load (extension restart); fixed by stashing role in process env | Nodes don't listen; reconnect+resume token survives restarts (PIT-007). |
| B3 | Env config discarded on Windows unless `steam_appid.txt` exists | No env config (PIT-001). |
| B4 | MD/frame hooks dead because `version_db` missing | Self-test + capability report (PIT-006). |
| B5 | `FULL 1` header missing newline → first object dropped each tick (probable flicker cause) | Generated binary codecs (REQ-401). |
| B6 | Host fed client-format PLAYER to its own stream parser (UDP role gate) | Server relay + per-role whitelist (PIT-025). |
| B7 | Reconnect loop / 156 stale clients / EBADF | Key-based sessions (REQ-006). |
| B8 | Save-reload loop: prune removed docked player ship → Game Over | Explicit despawns + safe-remove wrapper (PIT-010). |
| B9 | `FULL` lines dropped by router; link_alive never refreshed in consolidated mode | One message dispatcher, typed handlers, link state from transport. |
| B10 | Ghost factions never existed (`x4mp_client_N`) | Validated faction assignment (PIT-013/014). |
| B11 | 2048-ship enumeration cap | PIT-015. |
| B12 | Binding leak + index duplicates | Proxy registry (PIT-033), dedupe (PIT-016). |
| B13 | Empty FULL for unindexed sector | `complete` flag semantics (REQ-404). |
| B14 | Windows `connect()` EWOULDBLOCK treated as failure ⇒ Windows clients could never connect | PIT-022. |
| B15 | `strerror` helper returned local buffer | PIT-023. |
| B16 | Players broadcast spacesuit macro; ghost model not updated on ship swap | PIT-011/012. |
| B17 | Residual single-ship flicker despite drift=0 (open) | Codec fix + ID-based proxies. |
| B18 | Client player sector drifts after load (open) | Investigate (PIT-043). |
| B19 | Client reconnect creates a 2nd NetClient until 30 s timeout | REQ-006. |
| B20 | `X4MP_LOG` ignored on Windows; perf log to `/tmp` | REQ-307/308. |
| B21 | Autosave pollution with ghosts/satellites | PIT-034. |
| B22 | Cannot join a NEW game; save must be copied manually or via scp (Windows lacks sshd) | Server save distribution (REQ-301/303). |
| B23 | Combat/trade/boarding never validated; credits & station economy not synced; boarding ops not replicated; no shot-level combat | Explicit test checklist per event (§8); Windows internal functions research (REQ-211); server ledger (REQ-208). |
| B24 | Sells converge slowly (single-unit `AddTradeWare`) | Budgeted batched application across frames. |
| B25 | Host player teleport / camera snap hacks intrude on the host player | Dropped (REQ-109). |
| B26 | No auth, no versioning, no mismatch detection (wrong save = "world differs") | Handshake with build/save checksum (REQ-003). |
| B27 | Host FPS 25–30 with clients (high-sim cost) | Accepted physics of the design; surface in GUI, cap sectors (PIT-038). |
| B28 | Cosmetic misleading log after resume; debug summaries only after 300 ticks | Structured lifecycle logging. |
| B29 | Region streaming must be set identically on both sides | Server-owned session settings (§3 rule). |
| B30 | Linux launcher exports `SteamAppId=294140` while Windows uses appid 392160 | Not applicable (Windows-first); verify appid if a Linux port happens. |

---

## 7. Install / launch / save-sync UX lessons for Windows

1. **Steam relaunch.** Launching `X4.exe` directly triggers a Steam restart that discards the parent's environment. `steam_appid.txt` (`392160`) beside `X4.exe` suppresses it (old installer created it; uninstaller removed it). Our approach: nothing depends on env or on how X4 was launched — the player can start X4 normally from Steam. Do **not** require `steam_appid.txt`; if a launcher ever passes command-line flags, note that the command line survives the relaunch.
2. **Protected UI mode must be OFF** (Settings → Game Options). Detect via `<uisafemode>` in `Documents\Egosoft\X4\<id>\config.xml`; tell the user exactly where to click; the game warns about online features — expected.
3. **Enable extensions** (Settings → Extensions) and fully restart X4. Detect disabled state via `<extension id="..." enabled="false">` in `<id>\content.xml`.
4. **VC++ redist** for x4native (MSVCP140, VCRUNTIME140, VCRUNTIME140_1 in System32). Our own DLL: static CRT.
5. **Mark-of-the-Web**: unblock files after unzip; SmartScreen "More info → Run anyway" on .bat/.exe; consider signing later.
6. **Finding the game:** registry `HKCU\Software\Valve\Steam\SteamPath` (+ HKLM WOW6432Node), `steamapps\libraryfolders.vdf` library paths, `steamapps\common\X4 Foundations\X4.exe`; dedupe case-insensitively; warn if several installs found ("install into the one you play"); fallback prompt.
7. **Per-account folders:** saves at `%USERPROFILE%\Documents\Egosoft\X4\<numeric account id>\save\*.xml.gz`; logs at `...\<id>\x4native\`. Multiple numeric folders possible — pick newest/ask. (Old launcher bug: assumed `Documents\EgoSoft\X4\save`.)
8. **Game build check:** `version.dat` in the game folder (e.g. `900`) plus build number from the PE; package targets a specific build; mismatch warning.
9. **Vanilla install recommended:** second Steam library, copy the game folder, keep only `ego_dlc_*` in `extensions\`, install our mod there; X4 runs from a copy while Steam runs. Saves are shared between the copies → name MP saves clearly.
10. **Save sync:** everyone must load the byte-identical save; host must start a new game, leave the intro, save, then distribute. Windows has OpenSSH client but not server ⇒ scp from a Windows host fails. **Our server distributes saves over HTTP with checksums**; the mod writes the file into the right account folder and loads it (base name without `.xml.gz`).
11. **Firewall:** prompt appears on first listen; a dismissed prompt is remembered as Block. In our topology only the server listens → add a rule for the server exe at install; nodes need no inbound rule.
12. **Start order & waiting:** server first, authority next, clients after; loads take 30 s–3 min — show progress states in game and GUI.
13. **Menu entries location:** bottom of the main menu, below "Exit to Desktop" — tell users.
14. **One-click tools that worked well:** `Install.bat`, `Check Install.bat` (plain-English verdict, ordered problems list, "send this window"), `Collect Logs.bat` (zips x4native logs + content.xml + config.xml to Desktop), `Uninstall.bat` (saves untouched), `Make Shareable Zip.bat` (excludes developer-only folders), Desktop shortcut. Keep this UX; add server-side "node health" view so the host can see a friend's problem without them sending logs.
15. **Honest status messaging** to users ("treat the first session as debugging") prevented false expectations — keep a status page in the GUI/README.
16. Batch-file pitfalls: use flat `goto` flow (variables set in `if` blocks aren't visible), typed flags were dropped by a buggy compare — prefer PowerShell or the .NET app for any launcher logic.

---

## 8. Test plan ideas (from TESTING.md and fake_client.py)

### 8.1 Without X4 (CI, M1)
- **T-01 Fake node** (successor of fake_client.py): connects, handshakes with key/name, sends player state at a rate with optional synthetic motion (`--orbit`), answers heartbeats, records every received message by type with counts/bytes/avg size/sample, distinct object ids, total and peak bandwidth (KB/s, Mbit/s), exits non-zero on handshake or stream failure. Modes: observe-only, claim a sector, verbose.
- **T-02 Fake authority:** emits a synthetic universe (configurable sectors/ships, ~90k ships scale test), movement, despawns, kills, captures, station builds; responds to actions. Lets the server, interest management and GUI be developed end-to-end.
- **T-03 Codec tests:** golden-byte round trips for every message; fuzzing decoders; reject wrong-direction messages (PIT-024/025/026).
- **T-04 Session tests:** reconnect with same key replaces old connection; resume within grace; duplicate key; timeouts; 8+ fake nodes; kick/ban.
- **T-05 Interest tests:** sector change, region boundaries with hysteresis, empty/unindexed region never produces mass despawn, budget enforcement.
- **T-06 Bandwidth regression:** fixed synthetic scenario must stay under a KB/s budget per node.
- **T-07 Save distribution:** upload, checksum, resume, corrupted download rejected.
- **T-08 Windows socket test:** non-blocking connect path on real Winsock (PIT-022).

### 8.2 With one X4 (M2)
- **T-10 Startup self-test lines** (equivalent of the old checklist): x4native resolved N/N functions; internal functions resolved; frame hook, MD event hook installed; our mod "menu installed"; reported to server and visible in GUI.
- **T-11 Health check script:** game found, build, files present, `version_db` present, MotW clean, VC++ present, extensions enabled, Protected UI off, log folder exists, menu installed, unresolved-hook warning.
- **T-12 Authority against fake nodes:** authority X4 + server + N fake nodes; verify streaming starts only after universe ready, counts sane (~tens of thousands indexed), authority FPS reported.
- **T-13 Lifecycle:** host/join via in-game UI survives the extension restart on load; repeated load/reload; `/reloadui`; game over and back to menu; universe-ready timeout fallback.
- **T-14 Save hygiene:** run a session, trigger saves/autosaves, scan the save for tagged proxies/helpers ⇒ must be zero.
- **T-15 Player-ship safety:** docked, walking (spacesuit), ship swap, campaign start piloting a foreign-faction ship — no removal, correct macro reported.

### 8.3 Two+ machines (M3–M5)
- Per-machine checklist: same X4 build, same mod build, vanilla + DLCs only, extensions enabled, one instance per machine.
- Order: server → authority → client. Verify: server shows both nodes connected; authority simulating; client in universe; each sees the other's ship with name label; ship models update on swap.
- **Sync checks:** busy sector compare; highway flight across many sectors (old worst case) with drift/spawn/despawn metrics (old `[FLK] drift=0` equivalent); dock/undock; jump gates.
- **Event checks (each once, with log evidence):** kill an NPC; get killed; buy and sell at a station (stock converges on all nodes); board and capture a ship (works with AI-suppressed proxies); build a station on client and on authority; reconnect mid-session and verify assets.
- **Stress:** 3–4 clients in different sectors (authority FPS), mass-spawn sector (ID map / VRAM budget), 60-minute stability run (old target: 28+ min stable).
- **Diagnostics to keep:** frame-alignment check (nearest local vs nearest streamed object), per-15 s flicker counters (stale, zone, spawn, despawn, pin, drift>threshold, max drift), convergence report on sector entry, proxy-registry invariants.

### 8.4 Process lessons for testing
- Evidence before theory: check logs for scale and frequency before diagnosing (the human's rule in from_LLM_to_LLM.md).
- Machines under heavy load (shared with an LLM server, saturated disk) stalled loads for 10+ min — use dedicated, idle test machines.
- An AI developer cannot play: every gameplay feature needs a scripted, human-runnable checklist with expected log lines, and ideally a test driver that triggers the event without a human.
