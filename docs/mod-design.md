# X4MP Mod Design (in-game side)

> See docs/architecture.md — it is authoritative where this doc differs.

Status: draft v0, 2026-10-01, consolidated (see `docs/decisions.md`). Scope: everything that runs inside `X4.exe`, which
means the native C++ module on X4Native, the Lua UI, the Mission Director (MD) XML
and the extension package. The server, protocol and GUI are covered elsewhere:

- `docs/protocol.md` owns message names, framing, encoding and versioning. This
  document was written with **logical** names (`EntityBatch`, `Despawn`, `Event.Kill`,
  `Wallet.Delta`, `Offer.*` …); read them through the mapping table in
  `docs/decisions.md` ADR-027 (e.g. `EntityBatch` = `WorldUpdate`, `Event.Kill` =
  `Intent{KillClaim}`, `OwnerChanged` = `EntityChange`, `StationStock`/`PlayerCargo` =
  `EntityCargo`, `Wallet.Delta`/`Wallet.Balance` = `CreditDelta`/`WalletUpdate`,
  `Settle` = `AssetTransferOrder`). Sectors travel as the `u16` index from
  `GalaxyMetadata`; `sectorKey` (macro) is the node-local key. Where the two documents
  disagree, protocol.md wins.
- `docs/x4-api-notes.md` owns exact game function signatures and the gaps
  analysis. This document follows it. The decisions taken from it are: gate on
  `on_universe_ready`; derive velocity from position deltas; move ghosts with
  `SetObjectSectorPos`; bulk cargo through MD `add_cargo exact=`; credits
  through the Lua `GetPlayerMoney` / `TransferPlayerMoneyTo` and MD
  `transfer_money`; targeted orders through the Lua `SetOrderParam`; no export
  lists all sectors; pin the build. Anything marked **[VERIFY]** has not been
  confirmed in game.
- `docs/requirements.md` owns the REQ/PIT catalogue. Appendix A maps each PIT
  that applies to the mod to the section of this document that mitigates it.
- Session-level **teams** and **credits** policy, decided by the user, are in
  sections 11 and 12.

Locked decisions from `PLAN.md`: Windows first, C++ on X4Native plus Lua and MD,
server-centric relay (every X4 instance is a client of the standalone .NET
server), one X4 instance is the **authority**, all the others are **clients**.

Terms used throughout:

| Term | Meaning |
|---|---|
| node | One X4 instance running our mod, in either role. |
| authority | The node whose universe is the truth. It simulates, captures and sends. |
| client | A node that renders the authority's world as ghosts and sends its own player's state and actions. |
| ghost | An object the mod spawns locally to represent a remote entity. It is inert, owned by the remote entity's (node-relative) faction, tracked in the ghost registry, and it never gets saved. |
| avatar | A client player's ship as it exists on the authority: a real, persistent, team-owned ship driven by that client (3.7). |
| NetId | A 32-bit id the authority assigns to each replicated entity. Clients never see authority UniverseIDs as identity. |
| match key | A stable key used to pair an authority entity with an object that already exists in the client's copy of the same save (see 4.2). |

---

## 0. What we keep from the reference, and what we change

Evidence comes from `reference/STATE.md`, `reference/from_LLM_to_LLM.md`,
`reference/FOLLOWUP.md`, `reference/FEATURES.csv` and the Windows smoke test of
2026-09-18.

| Reference behavior | Our design |
|---|---|
| Two DLLs (`x4mp`, `x4mp_stream`) talking through string events. Caused routing bugs: FULL lines dropped, an ungated UDP path fed client lines into the host's parser, and 2.4M errors per hour. | **One DLL**, one process-wide message router, and role gating enforced in types (see 2.2). |
| Text protocol with a full snapshot every tick, `memcpy` framing bug. | Binary, versioned protocol (protocol.md). Change-driven sends; server-side per-client deltas with `SectorComplete`, checksums and explicit despawns instead of full snapshots (ADR-011/012). |
| Config from env vars. Steam relaunch drops them. | Config file plus X4Native settings plus the Join dialog. No env vars at all. |
| Menu host request lost when x4native re-discovers extensions on universe load; fixed with `_putenv`. | Session intent kept in the **X4Native stash** (`x4n::stash`, which survives extension reload and `/reloadui`). The server also supports session resume. |
| Bind/pin of local ships, with ghost mode bolted on later; ghost mode won. | **Ghost mode is the only mode in v1.** Static objects (stations, gates) are matched, not ghosted. Bind/pin is not built. |
| MD hooks failed until `version_db/` shipped. | `version_db` is a hard packaging check. The installer and the mod self-test both verify it. MD-XML fallbacks exist for the player-scoped events we need most. |
| Host enumerated all ~85k ships every 5 s with per-faction duplicates. | Incremental, de-duplicated universe index that only covers **interest sectors**, and a frame-time budget. |
| Host autosave baked ghosts and satellites into saves. | Ghost registry, a pre-save strip, an MD patch that blocks autosave on clients, a save-load janitor, and no sim satellites. |
| Prune deleted the docked player's ship, causing Game Over and a reload loop. | `PlayerGuard` (see 6.1) is consulted by **every** removal through one choke point, `SafeRemove()`. |
| Borrowed real NPC factions for player ghosts. All clients flew the save's single player ship identity. | **Teams** map to mod factions `x4mp_team_1..8`, node-relative, with a session relation matrix. Every player gets a persistent **avatar** ship (section 11). |
| "No credits API": credits not synced. `AddTradeWare` misused as +1 cargo. | Server-authoritative ledger with per-player wallets or a shared wallet, transfers, loans and escrowed offers. Local money is reconciled through Lua `GetPlayerMoney` and `AddPlayerMoney`. Cargo moves through MD `add_cargo`/`remove_cargo exact=` (sections 5.3 and 12). |

---

## 1. Extension package layout

### 1.1 Folders shipped into `X4 Foundations/extensions/`

```
extensions/
├── x4native/                      vendored X4Native release, pinned (MIT)
│   ├── content.xml  ui.xml  md/  ui/  t/
│   └── native/
│       ├── x4native_64.dll  x4native_core.dll
│       └── version_db/            REQUIRED: internal_functions.json etc.
└── x4mp/                          our extension
    ├── content.xml
    ├── ui.xml
    ├── x4native.json
    ├── native/
    │   └── x4mp.dll               our single native module (x64, /MT)
    ├── config/
    │   └── x4mp.defaults.json     shipped defaults (read-only)
    ├── ui/
    │   ├── x4mp_bridge.lua        Lua<->native bridge, savedvariables, SaveGame wrap
    │   ├── x4mp_menu.lua          main/pause menu entry + Join/Status screens
    │   ├── x4mp_hud.lua           connection status HUD menu
    │   ├── x4mp_chat.lua          chat (vanilla chat window adapter + fallback)
    │   └── x4mp_players.lua       player list panel
    ├── md/
    │   ├── x4mp_main.xml          lifecycle, player-scoped fallback events, sector list
    │   ├── x4mp_actions.xml       "actions shim": event_ui_triggered → add/remove_cargo,
    │   │                          transfer_money, set_faction_relation(_locked),
    │   │                          set_faction_active, set_object_hull/shield, autosave flag
    │   └── notifications.xml      DIFF patch: autosave gate (see 6.2)
    ├── libraries/
    │   ├── factions.xml           DIFF: team factions x4mp_team_1..x4mp_team_8, active="0" (section 11)
    │   └── colors.xml             DIFF: colour mappings faction_x4mp_team_1..8
    └── t/
        └── 0001-l044.xml          text page 92xxx (English); other languages later
```

The extension id is `x4mp`. The reference used the same id, so the installer
detects a reference install (an `x4mp_stream/` folder, or `native/x4mp.so`) and
offers to remove it before installing. Two extensions with the same id must never
be present together.

### 1.2 `content.xml`

```xml
<content id="x4mp" name="X4 Multiplayer" version="010" save="0" enabled="1"
         author="X4MP project" date="2026-10-01"
         description="Server-based multiplayer for X4: Foundations (requires X4Native).">
  <dependency id="x4native" optional="false" version="900"/>
  <text language="44" name="X4 Multiplayer" description="..."/>
</content>
```

- `save="0"`: a save made **outside** a session must never depend on our
  extension. Team factions ship `active="0"` and own nothing until a session
  activates them, and the save hygiene in section 6 strips every ghost.
  **Authority session saves** (`x4mp_<session>_<n>`) are a deliberate exception:
  they contain team-owned assets under `x4mp_team_*` factions, so they need the
  mod to load. **[VERIFY]** what X4 does when it loads such a save without the
  mod: unknown-faction objects dropped, reassigned, or a load error. If the result
  is bad, switch to `save="1"` so the game warns when the mod is missing (section
  11.8).
- DLC dependencies are optional and not declared. The DLC set is compared at
  handshake time instead (see 6.4).

### 1.3 `ui.xml`

Same schema as `x4-unpacked/ui/core/addon.xsd`. Two patterns come from
`x4-unpacked/ui/addons/ego_chatwindow/ui.xml`:
`<savedvariable … storage="userdata">`, which persists in `uidata.xml` and
**not** in the save, and `<dependency name="ego_detailmonitorHelper"/>`, so that
`Helper` exists before our files run.

```xml
<addon name="x4mp" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
       xsi:noNamespaceSchemaLocation="../../ui/core/addon.xsd">
  <environment type="menus">
    <file name="ui/x4mp_bridge.lua"/>
    <file name="ui/x4mp_menu.lua"/>
    <file name="ui/x4mp_hud.lua"/>
    <file name="ui/x4mp_chat.lua"/>
    <file name="ui/x4mp_players.lua"/>
    <dependency name="ego_detailmonitorHelper"/>
    <dependency name="ego_gameoptions"/>          <!-- [VERIFY] ordering effect -->
    <savedvariable name="__X4MP_USER" storage="userdata"/>
  </environment>
</addon>
```

`__X4MP_USER` holds `{ version, lastAddress, lastName, hudPos, chatPos }`.
The **password is never persisted** in Lua.

### 1.4 `x4native.json`

```json
{
  "library": "native/x4mp.dll",
  "priority": 50,
  "min_api_version": 1,
  "autoreload": false,
  "settings": [
    { "id": "log_level",       "name": "Log level",       "type": "dropdown", "default": "info" },
    { "id": "hud_enabled",     "name": "Show MP HUD",     "type": "bool",     "default": true },
    { "id": "interp_delay_ms", "name": "Interp delay (ms)", "type": "number", "default": 120 }
  ]
}
```

> Corrected in M0-08: X4Native v9.0.0-611726's settings schema uses `id` + `name`, not
> `key`. Check `mod/third_party/x4native/v9.0.0-611726/sdk` for the exact field set.

The `settings` array is X4Native's injector for Settings > Extensions
(`extensions/x4native/ui/x4n_settings_menu.lua`). Read the values with
`get_setting_*`. Only user-facing toggles go here. Everything else goes in the
config file (see 2.6). **[VERIFY]** the exact settings schema against the pinned
X4Native release.

### 1.5 Vendoring X4Native

- Upstream is `github.com/eg3r/X4Native`, MIT licensed. Releases are tagged per
  game build: `v9.0.0-611726` matches X4 9.00 build 611726, which is the
  verified retail build.
- In the repo, keep it at `mod/third_party/x4native/<tag>/` with the release
  zip contents (runtime folder plus `sdk/`). A small `VERSION` file records the
  tag and SHA-256 of each binary. **Commit the SDK headers**, because we compile
  against them. Commit the runtime binaries too: they are small, it gives
  reproducible packaging, and MIT allows redistribution with the license file.
- Packaging copies `x4native/` into the release zip **with `LICENSE` and
  `native/version_db/`**. CI fails if `version_db/internal_functions.json` is
  missing or has no entry for every supported game build.
- **Pinned build, fail loudly.** Each mod release declares
  `supported_builds = ["900-611726"]` (game build plus X4Native tag). At init,
  `x4n::game_version()` and the build suffix are compared against that list. On a
  mismatch the mod logs an ERROR, the HUD and Join dialog show "Unsupported X4
  build x.y (supported: …)", and connecting is refused. There is no user override
  in release builds, and the server enforces the same list (ADR-004, PIT-045). The same check runs when the MD hook or frame tick is
  unresolved.
- Upgrade procedure: bump the tag, rebuild, run the in-game self-test (see
  8.5). The self-test logs resolved function counts and whether the hooks are
  installed (`MD event hook installed`, `Native frame hook installed`).
- We do **not** fork X4Native unless forced to. The reference's Linux proxy
  copy-race fix does not apply to the Windows proxy. Any X4Native bugs we hit
  go upstream as PRs.
- Runtime prerequisite: `x4native_core.dll` imports MSVCP140/VCRUNTIME140, so
  the installer checks for the VC++ 2015-2022 x64 redist. Our DLL links the CRT
  statically (`/MT`) so it adds no dependency of its own.
- Users must turn **Protected UI Mode** off (Settings > Extensions). The
  installer README says so, and `x4native.lua` logs it. We cannot detect it from
  our own code, because if it is on, our code never loads.

---

## 2. C++ module architecture

### 2.1 Layering

```
mod/native/
├── core/        PURE C++, no X4 headers. Unit-testable on any machine.
│   ├── net/          socket, framing, reconnect, TLS-less transport, codec glue
│   ├── queue/        SPSC/MPSC queues, latest-wins slots
│   ├── sched/        frame budget, job scheduler, rate tiers
│   ├── repl/         change detection, dead-reckoning, interpolation, NetId map
│   ├── session/      state machine (Disconnected→…→InSession), resume token
│   ├── config/       JSON config load/merge/validate
│   └── log/          logger, rate limiter, metrics
├── game/        THE ONLY CODE THAT TOUCHES X4. Thin adapters.
│   ├── IGame.h           interface: enumerate, read pos, spawn, move, remove, owner, cargo …
│   ├── X4Game.cpp        implementation over x4n::game() / game_fn()
│   ├── PlayerGuard.cpp   player-ship set, SafeRemove()
│   ├── GhostRegistry.cpp ghost bookkeeping (stash-backed)
│   └── MdCapture.cpp     x4n::md::on_*_after subscriptions → POD ring
├── roles/
│   ├── Authority.cpp     capture pipeline (section 3)
│   └── Client.cpp        apply pipeline (section 4)
├── features/    events: Kill, Death, Build, Trade, Capture, Chat (section 5)
├── bridge/      Lua bridge (raise_lua / event subscriptions), JSON in/out
└── main.cpp     X4N_EXTENSION / X4N_SHUTDOWN, wiring
```

Rule: `core/` must build and pass its tests with no X4 SDK on the include path.
`roles/` and `features/` talk only to `IGame`. That makes the pipelines testable
against `FakeGame`, a scripted in-memory universe (see 8.4).

### 2.2 Threads

| Thread | Owner | Does | Must never |
|---|---|---|---|
| **UI/main** (X4's Lua thread; all x4n event callbacks) | X4 | All game API calls, job dispatch, Lua bridge | Block on I/O; sleep; wait on a lock held by net thread for > µs |
| **net** (ours, `std::jthread`) | us | Winsock connect/send/recv, framing, (de)compression, heartbeats, reconnect with backoff, clock-sync pings | Call any game function or `x4n::*` API |
| **MD worker(s)** (X4's) | X4 | Fire `md_subscribe_*` callbacks | We copy PODs only, no game calls, no allocation beyond a preallocated ring |

**Why a net thread rather than polling non-blocking sockets in
`on_frame_update`?** Frames stop during loading screens, during save/load and
while the game is in the background. The connection, heartbeat and resume
handshake have to keep running through those. The reference lost its listener
when the universe loaded, and a blocking send once cost it 25 FPS. With a
dedicated thread, the main thread's network cost is two queue swaps per frame.

Role gating: `Authority` and `Client` are separate classes. Only one is
instantiated, by `Session` after `Welcome` assigns the role. The message router
dispatches by `(role, msgType)` through a static table, and an unexpected
message for the current role is logged once and dropped. This removes the
reference's "host parses its client's PLAYER lines" class of bug.

### 2.3 Queues

| Queue | Producer → Consumer | Type | Overflow policy |
|---|---|---|---|
| `inbox` | net → main | SPSC ring of decoded `Message` (owned buffers) | Block-free. If full: drop **unreliable** (`Replication`), never drop reliable. Log `inbox_overflow` counter. |
| `outbox_reliable` | main → net | SPSC ring | Unbounded (vector, swap under mutex). Events, chat, acks. |
| `outbox_state` | main → net | **latest-wins slots** keyed by channel (e.g. one per `(tier, sector)` batch) | Overwrite stale unsent state. Bounded memory regardless of network stall. |
| `md_ring` | MD workers → main | MPSC, fixed-capacity array of POD `MdEvent{type, u64 a,b,c, f64 t}` with atomic write index + per-slot sequence | If full: increment `md_dropped`; never block a game worker. |

Implementation: in v1, `std::mutex` plus a double-buffered `std::vector` swap is
enough for `inbox` and `outbox_*`. Contention is two short lock holds per frame,
and messages are batched. `md_ring` is lock-free, because MD callbacks can fire
on game worker threads in tight loops (from `from_LLM_to_LLM.md`: callbacks "may
run on a worker thread" and must be pure memory reads). Everything sits behind
`core/queue` interfaces, so we can swap in lock-free versions if profiling
justifies it.

The net thread wakes on socket readiness or on an outbox signal. It uses
`WSAPoll` with a 5 ms timeout, plus a `WSAEventSelect`-signalled "outbox
non-empty" event. Sends are fully non-blocking. A partial send keeps its
remainder in a per-connection write buffer, and the connection drops if that
buffer exceeds a cap (8 MB) rather than growing without limit.

### 2.4 Main-thread dispatch (frame update)

Frame source: X4Native's `on_frame_update`, raised from Lua `SetScript("onUpdate")`
in `extensions/x4native/ui/x4native.lua`. It runs on the UI thread every UI frame,
both in the start menu and in game. If X4Native's native frame hook is installed
(`Native frame hook installed`, which needs `version_db`), it is available too,
but we do **not** depend on it.

Each frame:

```
on_frame_update():
  t0 = qpc()
  drain md_ring            → feature handlers (game calls allowed now)
  drain inbox (≤ budget)   → router → Session / Client / Authority handlers
  scheduler.run(budget_us - elapsed)   // time-sliced jobs, round-robin by priority
  flush outbound batches   → outbox_* (cheap swap)
  metrics.frame(qpc()-t0)
```

- **Budget.** The default is 2.0 ms on the authority and 1.5 ms on clients,
  configurable. The scheduler measures each job slice with `QueryPerformanceCounter`
  and resumes cursor-based jobs next frame. The reference measured network
  work at about 3 ms of a 35 ms frame. Its 25–30 FPS was the cost of
  high-attention simulation, not networking, so we keep our own work small and
  measurable and leave the attention policy as a tunable (see 3.6).
- **Jobs are resumable iterators** with cursors, such as "index next 500 ships"
  or "read positions for the next 300 entities in tier 2". There are no
  unbounded loops on the main thread.
- **Game-state gates.** Nothing that touches universe objects runs before
  `on_universe_ready`, which X4Native raises from MD
  `event_universe_generated` in `extensions/x4native/md/x4native_main.xml`. The
  reference found that `on_game_loaded` fires after the **first** load pass, and
  spawning then raced the "Movement worker" and segfaulted. Gates are cleared on
  `on_game_loaded` (a new universe is coming) and set again on
  `on_universe_ready`.

### 2.5 MD event capture

Primary path: X4Native typed MD subscriptions, for example
`x4n::md::on_killed_after(...)` as used in upstream `examples/event_test`, or the
raw `md_subscribe_after(type_id, cb, ud)`. Reference type ids for 9.00 are
`Killed`=237, `EntityChangedOwner`=175, `BoardingOperationStarted`=41,
`BoardingOperationRemoved`=40 and `BoardingPhaseChanged`=42. Take the ids
from the SDK's `x4_md_events.h` and never hardcode them.

```cpp
// worker-thread safe: only reads from the event struct, writes to a POD ring
static void on_killed(const x4n::md::KilledData& e) {
    md_ring.try_push(MdEvent{MdType::Killed, e.source_id, e.killer_id, 0, now_seconds()});
}
```

Rules:

1. No game function calls, no `x4n::log`, no heap allocation, and no lock
   that the main thread can hold for long.
2. Copy ids only. Resolve macro, owner, sector and so on later, on the main
   thread, and check `IsValidComponent` first, because the object may already
   be gone.
3. Use `*_after` subscriptions unless we need to veto something. We never veto
   in v1.

Fallback path, for when `version_db` has no entry for a new game build and the
MD hook is not installed: `md/x4mp_main.xml` listens to the **player-scoped**
events that exist in the vanilla scripts. The tally below counts uses in
`x4-unpacked/md/*.xml`.

| Need | Fallback MD event | Notes |
|---|---|---|
| Player killed something | `event_player_killed_object` (9 uses) | param = victim |
| Player ship destroyed | `event_object_destroyed object="player.occupiedship"` | `event_object_destroyed` requires `object` or `group` (common.xsd `objecteventsource`), so there is **no global destroyed listener** in MD |
| Player changed sector | `event_object_changed_sector object="player.entity"` | used by vanilla autosave in `md/notifications.xml` |
| Player trade | `event_player_trade_completed` | |
| Player build finished | `event_player_build_finished`, `event_player_build_finished_components` | |
| Owner change of a known object | `event_object_changed_owner group="$x4mpWatched"` | requires us to maintain the group |

Fallback cues forward to Lua through
`<raise_lua_event name="'x4mp.md'" param="[…]"/>`. The bridge passes them to
native as `x4mp.md_fallback`. They arrive on the main thread, so no ring is
needed. If the hook is missing, the mod logs `md_hook=fallback` and reports it in
`NodeStats` so the server GUI shows a degraded state.

### 2.6 Configuration (no environment variables)

Values are merged in this order, last wins:

1. Compiled-in defaults.
2. `extensions/x4mp/config/x4mp.defaults.json`, shipped and read-only.
3. `%USERPROFILE%\Documents\Egosoft\X4\x4mp\x4mp.json`, the user file
   (`SHGetKnownFolderPath(FOLDERID_Documents)`). The launcher and the installer
   write this file. It is created with comments-as-keys documentation on first
   run.
4. `…\x4mp\launch.json`, a **one-shot** auto-connect request written by the
   launcher: `{ "server": "...", "name": "...", "password_ref": "prompt|inline", "save": "...", "expires": <unix> }`.
   The mod consumes it, deletes it and ignores it once expired. This replaces
   `X4MP_AUTO`.
5. X4Native settings (`get_setting_*`), for the few user-facing toggles.
6. Join dialog values passed at connect time through the bridge.

Config is read once at `x4native_init` and again on `on_ui_reload`.
Unknown keys produce warnings. Validation errors fall back to the default for
that key and are logged loudly. Nothing reads `getenv`.

Session continuity across extension reloads, which happen on save load per
STATE.md 2026-09-18: `X4N_SHUTDOWN` stops the net thread (join with a 200 ms
timeout, then graceful close with `Disconnect{code=ClientReload}`) and writes the following
to `x4n::stash`:

```
session.intent   = {connect|none}, server, name, role, resume_token, session_id
ghost.registry   = [UniverseID…]          (see 6.2)
netmap.epoch     = universe epoch (see 4.2)
```

`X4N_EXTENSION` reads the stash and, if `intent=connect`, reconnects with
`ClientHello{resume_token}`. The server keeps a session slot for 60 s after an
unexpected disconnect, so the reload is invisible to the other players. The
stash is in-process memory and dies with the game, which is the semantics we want.

### 2.7 Logging

- File: `%USERPROFILE%\Documents\Egosoft\X4\<account>\x4native\x4mp\x4mp.log`
  matches the X4Native convention that the reference observed. The account
  folder is resolved by X4Native's logger. If the path is unavailable, fall
  back to `Documents\Egosoft\X4\x4mp\logs\`. Logs rotate at 20 MB with 5 kept,
  and each session gets a header with game version, build, X4Native version,
  mod build hash, role and config dump (secrets redacted).
- The logger is asynchronous. A lock-free ring is drained by a writer thread,
  so the main and net threads never `fwrite`.
- **Rate limiting per call site** (`LOG_RL(key, period)`). The reference
  produced "Failed to retrieve sector" floods of 590 per object and 2.4M spawn
  errors per hour. Repeats are collapsed into `(+N suppressed)`.
- Categories: `net`, `sess`, `auth`, `client`, `ghost`, `md`, `save`, `ui`,
  `perf`. Each has its own level.
- `perf` metrics are logged once every 5 s **and** sent to the server in
  `NodeStats` (every 2 s): FPS, main-thread ms (p50/p95), job backlog, entities tracked,
  ghosts alive, in/out kB/s, queue depths, `md_dropped`, `inbox_overflow`,
  `spawn_fail`, and `remove_blocked_by_guard`.
- Also mirror ERROR-level messages to X4Native's log (`x4n::log`), so a single
  file shows them next to load errors.

---

## 3. Authority pipeline

The authority captures its universe and sends **one** stream of entity state to
the server. The **server** does per-client interest filtering and fan-out, so the
authority does no per-client work. That is the main structural improvement over
the reference.

### 3.1 Interest set

- The server computes the union of interest regions across all clients, plus
  the authority's own sector, and sends `CaptureSet{epoch, sectors:[(sector, rate_hz)], focus:[(sector, center, radius_m, rate_hz)]}`
  whenever it changes (≤ 1 per 500 ms). The authority must capture each sector at its
  requested rate and each focus sphere (`NearRadius` 15 km) at 20 Hz; the tiers in 3.3
  are the internal schedule that meets those rates (ADR-011).
- `sectorKey` is the **sector macro name**, for example
  `cluster_113_sector001_macro`. Sector UniverseIDs differ between instances
  even with the same save (STATE.md 2026-08-11), so they are never used across
  the wire. Each node builds `SectorMap{macro ↔ local UniverseID}` at
  `on_universe_ready`. Macro lookup must use `GetComponentData(id,"macro")`,
  because `GetComponentName` returns display names (STATE.md 2026-08-15, PIT-018).
  **No export lists all sectors** (x4-api-notes 2.3), so the list comes from MD:
  `md/x4mp_main.xml` runs, on `event_universe_generated`,
  `<find_sector name="$s" space="player.galaxy" multiple="true"/>` **[VERIFY
  attribute set]**. It then emits one `raise_lua_event 'x4mp.sectors'` carrying
  the list. The Lua bridge turns each entry into `{id, macro, cluster macro}`
  with `GetComponentData` and passes the batch to native as JSON. A cross-check
  uses `GetSectorsByOwner` over all factions. Native asserts that the count
  matches the server's galaxy table for the session's DLC set.
- A client's region is its current sector **plus** a configurable lookahead:
  sectors linked by gates or highways within N jumps (`PrefetchDepth`, server-owned,
  default 1 from the start), plus a Linger tier for the previous sector. The reference's flicker at sector entry came from streaming one sector at
  a time. Prefetching the next sector's entities lets ghosts exist before the
  player arrives. Neighbours are learned from gate connections **[VERIFY API]**,
  or the server provides them from a static galaxy table extracted from the
  game's `maps/` files.

### 3.2 Universe index (enumeration)

- Goal: for each interest sector, a de-duplicated list of replicable
  objects (ships, stations, and later deployables), with class and macro.
- Source: `GetNumAllFactionShips` / `GetAllFactionShips` and the `…Stations`
  variants for each faction (signatures in the UI ffi blocks), with **buffers
  sized from `GetNum…`** (the reference was capped at 2048), then bucketed by
  `GetContextByClass(id,"sector",false)`. Dedupe by UniverseID, because
  `includehidden` aliases return the same ship under several factions.
  **[VERIFY]** whether a cheaper per-sector enumeration exists, such as a Lua
  `GetContainedShips`-style call or an X4Native helper. If it does, prefer it
  and keep this as the fallback.
- **Incremental.** A cursor job walks factions and their ships, indexing at most
  `index_per_frame` (default 2000) objects per frame. A full pass over about 90k
  ships therefore takes about 45 frames, or under 2 s at 30 FPS. Objects outside
  interest sectors are skipped after the cheap sector lookup, and we keep only
  their sector, so moves into an interest sector are caught next pass. Entities
  in interest sectors get their metadata cached: macro, owner, class and match
  key (see 4.2).
- Fast path: entities already tracked are re-verified on their own position
  schedule (3.3). The full index pass only exists to discover newcomers, and
  newcomers in interest sectors are also caught by an MD hook where possible
  (an `EntityCreated`-style MD type if the SDK exposes one **[VERIFY]**).
- **Sector-indexed flag.** A sector's manifest (3.5) is not sent until one full
  index pass has completed after it joined the interest set. This fixes the
  reference's latent "empty FULL prunes the client's whole sector" bug.

### 3.3 Capture rate scheduling (tiers)

Each tracked entity is placed in a tier, which is re-evaluated every 1 s.

| Tier | Members | Read rate | Notes |
|---|---|---|---|
| T0 | Player ships (authority's own player and the avatars of client players) | 20 Hz | Also includes anything the player is targeting or docked at. |
| T1 | Ships within `near_radius` (default 10 km) of any player | 10 Hz | |
| T2 | Other ships in interest sectors | 2 Hz | Movement is dead-reckoned in between. |
| T3 | Stations, gates and other static objects | On change (owner, build state, hull bucket) plus 1/30 Hz verify | Never position-streamed. |

Reads per tier are spread across frames (phase offset = `NetId % period`), so the
per-frame cost stays flat rather than spiking at 10 Hz boundaries.

Per-entity read: `GetObjectPositionInSector(id)` returns `UIPosRot` (position and
rotation). Guard every read with `IsValidComponent`, and with a sector check
(`GetContextByClass(id,"sector")` must be non-zero). Objects that are docked,
dying or in transit lose their sector, and the reference spammed errors on
exactly those. Velocity is **derived** by finite difference between the last two
reads, scaled by game time. No UI-exposed velocity getter was found in the 9.00
ffi blocks, and `GetComponentData(id,"velocity")` is **[VERIFY]**.

### 3.4 Change detection

Send-side dead reckoning: for each entity we keep the last **sent** state
`(pos, vel, rot, t)`. On each read, predict `pos' = pos + vel·(t_now − t)` and
compare it to the actual position. Send only if:

- position error > `ε_pos(d)`, where `d` is the distance to the nearest player:
  0.5 m inside 2 km, rising linearly to 20 m at 50 km;
- rotation error > 1° (0.5° in T0);
- the owner, sector, docked state or a flag changed; or
- time since the last send > `keepalive` (T0/T1: 1 s, T2: 5 s).

Stationary and cruising ships therefore cost nothing on the wire, and highway
ships at 15 km/s are cheap because their motion is linear. Clients extrapolate
with the same model, so error stays bounded by ε. This is the "delta" the
reference wanted but avoided because it worried about drift. Drift cannot build
up here, because the authority sends whenever the client's prediction would be
wrong.

### 3.5 Sending

- `WorldUpdate{authority_tick, capture_time_us, states:[EntityState]}` (protocol.md §9)
  goes out as unreliable state on the latest-wins `outbox_state` channel. That
  is TCP by default per protocol.md, and droppable in the queue. Batches hold at
  most about 64 KB before compression.
- `EntitySpawn{netId, sectorKey, class, macro, owner, matchKey, initial state}`
  is **reliable** and is sent the first time an entity is tracked in an interest
  sector. The server caches the latest spawn and state for each entity, so it
  can bring a newly joining client up to date without asking the authority.
- `Despawn{netId, reason: destroyed|left_interest|docked_inside|removed}` is
  reliable.
- `SectorComplete{sector, epoch, count}` is reliable and sent once per sector after the
  first full index pass that follows the sector joining the `CaptureSet`. (The earlier
  periodic `SectorManifest` is replaced by the server's `InterestChecksum` +
  `ResyncRequest`, ADR-012.)
- **NetId** is allocated monotonically by the authority and kept in the stash
  across reloads, together with `UniverseID ↔ NetId` maps, and persisted as
  `next_net_id` in every checkpoint. **Static objects keep their ids through the
  checkpoint manifest** (match key → net_id), not by hashing (ADR-009).

### 3.6 Simulation attention (performance knob)

X4 fully simulates only the player's surroundings. Everything else runs in
low-attention mode: ships move, but combat is abstracted. The reference forced
high attention in client sectors with `ActivateObject` plus a player-owned
satellite per client sector. That caused the FPS drop to 25–30 and **baked
satellites into saves**.

Policy for v1: `attention = "native"` (default), which means do nothing and
accept low-attention fidelity in sectors where only clients are present. It is
an option (`attention = "activate"`) to call `ActivateObject` on ships near
client players, without satellites. Satellites are never created. Measure the
FPS cost in M4 before deciding the default.

### 3.7 Client player ships on the authority ("avatars")

For NPCs on the authority to see, target and trade with client players, every
client's ship needs a representation in the authority universe. With teams
(section 11), that representation is a **real, persistent asset**, not a ghost.
Each client player's ship (the **avatar**) is owned by that player's team
faction on the authority. It is saved with the session and stays parked where
it was when its player went offline.

- While its player is online, the avatar is kinematic. It is inert (no local AI
  on the authority) and is moved by `SetObjectSectorPos` from the client's
  `PlayerState` at 20 Hz. The client is the truth for its own movement.
- While its player is offline, the avatar is an ordinary parked ship. It is
  re-inerted when the player reconnects.
- Avatars are tracked in `AvatarRegistry` (playerId ↔ NetId ↔ UniverseID). They
  are **not** in the ghost registry and are not stripped before saves.
- M5 adds an option for avatars to take damage from NPCs, with the authority
  sending `Event.Damage` to the owning client (see 5.1).
- Fallback, if team factions are unavailable (section 11.9): avatars become
  ghost mirrors under a borrowed faction, and they **are** stripped before every
  save, as in the reference design.

---

## 4. Client pipeline

### 4.1 Model

The client loads **the same save** as the authority, with a checksum enforced
at join (6.4). Its static world (sectors, gates, stations as of the save) is
correct at load time. Its dynamic world (NPC ships) diverges from the moment the
authority starts simulating. Therefore:

- **Dynamic objects** (ships in interest sectors) are **ghost-only**. Local NPC
  ships in those sectors are suppressed, and the authority's ships are rendered
  as ghosts. This is the mode the reference found to be flicker-free (`[FLK]
  drift=0`). Bind+pin was the reference's earlier mode. It mis-bound ships to
  same-macro ships up to 117 km away and fought the local AI. **We do not build
  bind+pin.** A capability flag is reserved in case it is ever needed.
- **Static objects** (stations, gates, accelerators) are **matched** to their
  local instances and updated in place: owner, later hull, stock deltas from
  trades. They are never removed and respawned.
- The **client's own player ship** is the client's truth. The client sends
  `PlayerState` at 20 Hz. It is never ghosted or suppressed locally.

### 4.2 Entity mapping

`NetMap` maps NetId to local UniverseID, and records kind: `ghost`, `matched` or
`self`.

**Match keys** are used only for static objects and the join-time snapshot:

1. **Primary for stations, gates and accelerators:**
   `(sectorKey, macro, round(pos, 50 m))`. These do not move, so the key is
   unambiguous in practice. Ties are logged and resolved by owner at save time.
2. **Tie-breaker only:** the ID code (`GetObjectIDCode`, "ABC-123").
   x4-api-notes 2.1 notes it is human-friendly but **not unique**, so it is
   never a key on its own.
3. Ships are **not matched** in ghost mode. Pre-existing ships in interest
   sectors are suppressed and replaced by ghosts (4.3). The one exception is the
   client's own player ship (4.9 and 11.4).
4. **UniverseID equality is never trusted** (PIT-017). README.md says "same
   save ⇒ static IDs match", while STATE.md shows that sector IDs differ. The ids
   may happen to coincide, but we do not rely on it. A debug check logs how
   often they coincide, to settle the question with data.

Match index: at `on_universe_ready` the client indexes its local stations and
gates by match key. That is a few thousand objects, done incrementally. Ships are
indexed per sector on entry, only to find and suppress them.

**Universe epoch:** a random 64-bit value created at `on_universe_ready` and kept
in the stash. If the client sees a different epoch after `on_ui_reload`, a new
universe was loaded, so `NetMap` and the ghost registry are cleared because those
UniverseIDs are gone. If the epoch is the same, it was a plain `/reloadui`, and
the maps are kept.

### 4.3 Sector entry (the critical moment)

When the client's player sector changes (MD hook, or the fallback
`event_object_changed_sector object="player.entity"`, or polling
`GetContextByClass(player ship,"sector")` every 250 ms as a belt-and-braces
check):

1. Send `PlayerState` immediately (it carries the new sector). The server owns
   interest: it promotes the already-prefetched Adjacent sector and sends
   `InterestUpdate`, spawns and `SectorComplete` for newly added sectors (ADR-011).
2. **Suppress local dynamic objects** in a sector only after its `SectorComplete`
   has arrived (normally while it was still Adjacent, out of view): every ship that is not
   in `PlayerGuard`, not player-owned (in v1, client-local player assets are
   out of scope, see 4.10), not matched, and not a ghost. Suppression is
   `SafeRemove()`. Alternative (**[VERIFY]**): hide by deactivation plus
   relocation if removal ever has side effects such as mission failures. The
   reference used removal successfully. Suppression is spread over frames at
   200 objects per frame. The sector change itself (gate flash or highway exit)
   hides the transition.
3. Spawn ghosts for every cached `EntitySpawn` in that sector, at most
   `spawn_per_frame` (default 40) per frame, ordered by distance to the player.
   Spawn bursts are a VRAM and stability risk: the reference saw a SIGFPE on a
   low-VRAM machine.
4. The previous sector stays in the server's Linger tier (20 s), so flying back
   through a gate causes no remove/respawn cycle. Ghosts are removed only on
   explicit `EntityDespawn{OutOfInterest}`.

Local NPCs that spawn later in a ghosted sector (from the local economy or jobs)
are caught by a 1 Hz sweep and suppressed. **[VERIFY]** whether we can stop the
client's local job and economy spawners outright. Options include an MD diff on
the job scripts, or keeping the client permanently paused for non-player
objects. Removing them as they appear is the baseline.

### 4.4 Ghosts

- Spawn: `SpawnObjectAtPos2(macro, localSectorId, UIPosRot, ownerFaction)`.
  The owner **must be an existing faction id**. `nullptr` returns 0 silently,
  and an unknown faction fails (STATE.md 2026-08-11 and 2026-08-19, PIT-013).
  The faction is validated before every spawn against the node's faction list
  (`GetAllFactions`). NPC ghosts use the entity's **real owner faction** from
  the authority, so relations, colours and targeting look right. Player and
  team ghosts use the node-relative team faction (section 11). Then call
  `SetObjectForcedRadarVisible(id, true)` for player ghosts.
- Spawned ghost ships come with whatever crew or AI the macro defaults to. We
  make them inert: `ActivateObject(id,false)`, or the equivalent the reference
  used for `X4MP_INERT`, re-asserted every 5 s. **[VERIFY]** that a deactivated
  ghost still renders engine effects. If it does not, accept that for v1.
- Every ghost is registered in `GhostRegistry` **before** the first frame it
  exists. The registry stores the UniverseID, NetId, kind and spawn time, and is
  mirrored to the stash.
- Spawn failure: if the result is 0, record it in `spawn_fail{macro,reason}`,
  back off for that NetId with exponential retry up to 30 s, then give up and
  report `GhostSpawnFailed` to the server. **Never retry every frame.** That was
  the reference's 2.4M-per-hour flood.
- Ghost invulnerability and collisions: a ghost that a local NPC or the local
  player rams or shoots could die locally and diverge. Policy: ghosts are not
  made invulnerable, because the player must be able to kill them (5.1). Local
  NPCs in a ghosted sector are suppressed, so in practice only the local player
  can hit them.

### 4.5 Interpolation and extrapolation

- Clock: the server stamps `authorityTime` on every batch. The client keeps
  `offset = serverTime − localTime` through NTP-style pings on the net thread
  (min-RTT filter over the last 16 samples).
- Render time = `now + offset − interp_delay`. The default delay is 120 ms, and
  it adapts between 80 and 250 ms based on observed batch jitter (p95
  inter-arrival).
- Each ghost keeps a ring of the last 4 states. For a render time between two
  samples, use **cubic Hermite** position (with velocities) and **slerp**
  rotation. Past the newest sample, **extrapolate** linearly with the last
  velocity for up to `max_extrap` (500 ms; 1 s for T2), then hold.
- Corrections: if a new sample shows an error over `snap_dist` (default 2 km,
  scaled up with speed), snap. Otherwise blend the error out over 200 ms. At
  highway speed (15 km/s), 120 ms of delay is 1.8 km of lag, which is
  consistent across all ghosts, so relative positions stay correct. Distance to
  the local player is the visible artifact. Player ghosts in T0 use a shorter
  delay of 80 ms.
- Apply step: per frame, `SetObjectSectorPos(ghost, localSector, UIPosRot)` for
  ghosts within the **apply budget**, prioritised by screen-relevance (distance
  to the camera). Far ghosts update at 10 Hz. **[VERIFY]** that it is the
  `SetObjectSectorPos` variant the reference used, and that it does not reset
  physics or velocity visibly. If ghosts stutter between sets, test
  setting velocity too, if an API exists.

### 4.6 Suppressing local AI

- Ghosts: inert (4.4).
- Matched stations: they keep running locally, because their production and
  trade offers are local. Their **stock** diverges from the authority (a
  reference limitation). v1 replicates player-trade deltas only (5.3), and
  resyncs stock for the station the player is docked at. While docked, the
  authority sends that station's full `StationStock` every 10 s, and the client
  applies it through the MD cargo shim (5.3).
- The client player's own ship: never touched.
- The local economy and jobs in non-interest sectors: they run, but nobody sees
  them. Their cost is the client's own FPS. An option `client_pause_oos=true`
  could reduce the local simulation **[VERIFY feasibility]**, but pausing the
  whole game (`Pause()`) is not an option, because the client's player has to
  fly.

### 4.7 Player ghosts and factions

Which faction owns a player's ghost is decided by the **team model** in section
11. The short version: each team maps to one faction, the mapping is relative to
each node, and on every node the local human's own team is the real `player`
faction.

- Player ghosts show the player's name (`SetComponentName` **[VERIFY]**) and are
  always in T0. They are **never pruned by manifest** (4.8) while the server
  lists the player as connected.
- Ship swaps: if a player changes ship macro, the ghost is despawned and
  respawned, because a ghost keeps its spawn model (reference patch 0001). The
  player's ship macro comes from the occupied or controlled ship, never
  `GetPlayerObjectID()`, which returns the spacesuit when the player is on
  foot.
- When a player is on foot or docked inside a station, their ghost is hidden
  (despawned with reason `docked_inside`) and reappears on undock.

### 4.8 Pruning

- A ghost is despawned **only** on an explicit `EntityDespawn{net_id}`. Desync is
  repaired by `InterestChecksum` → `ResyncRequest` (ADR-012); absence never implies
  deletion.
- On disconnect (when the link has been down for more than 5 s), ghosts are
  **frozen**, not pruned. If the session cannot be resumed within 60 s, all
  ghosts are despawned and the client returns to "offline local game" with a
  notification. It does not quit to the menu.
- Prune never touches anything that is not in `GhostRegistry`. Suppression
  (4.3) is the only path that removes local, non-ghost objects, and it goes
  through `PlayerGuard`.

### 4.9 Sector change for the player across the authority

The client sends `PlayerState{sector, pos, rot, flags, hull, shield, target}` (no
velocity; the server derives it) at 20 Hz, plus an immediate send on sector change. The authority moves the player's
avatar (3.7) into the mapped sector with
`SetObjectSectorPos`, or despawns and respawns it when the sector changes.

### 4.10 Out of scope in v1 (explicit)

Out of scope in v1: client-owned fleets and stations from the client's local
save that predate the session, and commanding team NPC ships from a client
(planned for M6, see 11.6). The **save's** pre-existing player assets belong to
the team that maps to the authority's `player` faction (section 11.3). Each
client player owns their avatar ship, plus anything they build, buy or capture
during the session. Those assets live in the authority's universe under their
team faction. Credits are covered by the wallet model (section 12).

---

## 5. Event pipelines

General shape:

1. **Detect** on the node where the event happens.
2. Report it to the server as a reliable `Event.*` with an idempotency `eventId`.
3. The server forwards it to the authority, which **validates and applies** it.
4. The authority broadcasts the **result**, which in most cases already arrives
   as state (`Despawn`, owner change).
5. Clients apply the result.

The originating client may show the effect **optimistically** (for example, the
ghost it killed is already gone locally) and reconciles when the result arrives.
Double application is prevented by `eventId` and by idempotent appliers. Applying
"remove NetId 42" twice is a no-op.

### 5.1 Kills

| Step | Client-originated (client player kills a ghost) | Authority-originated (NPC dies on authority) |
|---|---|---|
| Detect | MD `Killed` (hook) or `event_player_killed_object` (fallback). Victim UniverseID → `NetMap` → NetId. | MD `Killed` on authority for any tracked entity, or the entity becomes invalid during a read. |
| Send | `Event.Kill{eventId, victimNetId, killerNetId?, method}` | (none, it is state) |
| Authority | Validate that the NetId exists and is not a player. Destroy it with the **explosion** variant (see below). Emit `Despawn{reason=destroyed, killer}`. | Emit `Despawn{reason=destroyed}` |
| Clients | Despawn the ghost. If `reason=destroyed` and it is on screen, destroy it **with explosion** rather than removing it. | Same |

Destroy with explosion: `RemoveComponent` just makes the object vanish. The
export `SelfDestructComponent(id)` destroys it with effects (x4-api-notes 2.4).
It is called through `SafeDestroy(id)`, which applies the same `PlayerGuard`
checks as `SafeRemove`. Off-screen ghosts (more than 20 km from the camera) use
`SafeRemove`, because nobody sees the explosion. If `SelfDestructComponent`
turns out to leave a wreck or fire MD side effects we don't want **[VERIFY]**,
the fallback is MD `<destroy_object explosion="true"/>` through the actions shim.

Damage (M5 stretch): a client player's avatar on the authority is hit by NPCs.
The authority sends `Event.Damage{targetPlayer, hullPct, shieldPct}`, throttled
to 5 Hz, and the client applies it to its own ship with an MD `set_object_hull`
/ `set_object_shield` action **[VERIFY names]**. Without this, NPCs cannot hurt
client players. That is a known v1 gameplay gap, and it is the largest one.

### 5.2 Player death

- Client's own ship destroyed locally (rare in ghost mode): detect it with
  `Killed` where the victim is in `PlayerGuard`, or the fallback
  `event_object_destroyed object="player.occupiedship"`. Send
  `Event.PlayerDied{eventId, cause}`. The server relays it, and the authority
  and other clients despawn that player's ghost. The local game runs its normal
  death flow. Our mod never removes the player's ship to "help".
- Client player killed on the authority (only once 5.1 damage exists): the
  authority sends `Event.Damage{hull=0}`, and the client's local game kills
  its own ship through MD. Same path as above.
- After death the client reloads the session save, or vanilla behaviour
  applies. The session handles it as a rejoin: the server keeps the player slot
  and sends `Welcome` again with `resume`.

### 5.3 Cargo and trade

- Detection: `event_player_trade_completed` (MD fallback and also the
  primary, because it is player-scoped and main-thread). We also diff the player
  ship cargo every 2 s (`GetNumCargo`/`GetCargo` with tags `""`, **not**
  nullptr, per the reference). Only non-empty reads count, so one bad read
  cannot wipe state.
- Report:
  `Event.Trade{eventId, stationMatchKey|stationNetId, ware, amount(+buy/−sell), price}`
  and `PlayerCargo{wares[]}`. The latter is state, sent on change, at most 1 Hz.
- Authority applies the station stock change through the **MD actions shim**:
  `<add_cargo object="$station" ware="$w" exact="$n"/>` for a player sale and
  `<remove_cargo …exact="$n"/>` for a player purchase. These are bulk
  operations (x4-api-notes 2.6). The reference's `AddTradeWare` changes a
  station's *traded-wares list*, not cargo, so it is not used. `DropCargo` ejects
  crates into space, so it is not used either. Many changes are batched into one
  `AddUITriggeredEvent` per frame. Latency from Lua to MD is at least one frame.
  The authority then broadcasts `StationStock{netId, ware, newAmount}` for that
  ware.
- Clients apply the absolute `newAmount` to their matched station through the
  same shim: compute the delta against the local amount, then `add_cargo` or
  `remove_cargo`. They also apply `PlayerCargo` to the player ghost's cargo
  (cosmetic, for inspection).
- Money for the trade is handled by the wallet model (section 12). The client
  reports the money delta, and the authority credits or debits the station
  owner's faction account with MD `transfer_money` (or `faction.money` adjust)
  so the NPC economy sees the income.

### 5.4 Station builds

- v1 replicates **completed** stations and, where possible, construction state.
  Client builds are forwarded as a request to the authority.
- Detection on the client: `event_player_build_finished` /
  `event_player_build_finished_components`, plus X4Native typed
  `on_build_finished_after` (upstream `event_test` uses `BuildFinishedData`).
  As a safety net, a 10 s scan diffs player-faction stations against the
  baseline taken at `on_universe_ready`.
- Report: `Event.BuildRequest{eventId, sectorKey, pos, rot, stationMacro, plan:[{moduleMacro, offset, rot}…]}`.
  The construction plan is read on the client **[VERIFY]**. X4Native ships
  `x4n_plans.h`, and `version_db` lists `ConstructionDB_CreatePlanDirect` /
  `ConstructionDB_AddPlan`.
- Authority: create the station under the requesting player's **team faction**
  (node-relative, section 11). The export
  `SpawnStationAtPos(macro, sector, UIPosRot, constructionplanid, ownerid)`
  takes a construction-plan id, so the authority first registers the received
  plan (MD `create_construction_plan`-style action, or the X4Native plan
  helpers; **[VERIFY]**), then spawns the station with it, owned by the team
  faction. The fallback is to spawn the station macro and add modules. It emits `EntitySpawn` (static, T3), and other clients get
  it through normal replication. The requesting client **keeps its local
  station** and the authority's spawn is matched to it by match key (position
  plus macro), so it is not duplicated.
- Authority player builds replicate by the same mechanism, because they are
  ordinary new stations in interest sectors. Stations are static, so new ones
  are also sent globally as `EntitySpawn` regardless of interest (they are few),
  which means map views agree everywhere.

### 5.5 Boarding and capture

- Detection: `EntityChangedOwner` (MD hook) on the client. The new owner is
  the local `player` faction and the entity is a ghost (has a NetId). Boarding
  start and stop (`BoardingOperationStarted`/`Removed`) temporarily
  **re-activate** the target ghost, so the local boarding operation can run
  (reference approach), and re-inert it afterwards.
- Report: `Event.Capture{eventId, netId}`.
- Authority: `SetComponentOwner(entity, teamFaction(capturer's team))`, where
  the faction is node-relative (11.2), then broadcast
  `OwnerChanged{netId, ownerTeam}`. The wire carries a **team index**, not a
  faction string. Each client maps it locally (11.2) and applies it to its
  ghost.
- On the capturing client, the vanilla capture has already made the ghost
  `player`-owned locally, which is the correct local view for its own team. It
  stays a ghost in the registry, so it is never saved locally (clients don't
  save anyway). Controlling a captured ship ("fly it") is out of scope for v1.
- The boarding operation itself (marines, phases) is local only. Other nodes
  see only the result.

### 5.6 Chat

- Client UI → `api.raise_event("x4mp.chat_send", json{text, to?})` → native →
  `Chat{eventId, fromPlayerId, text, channel}` → server (rate limit, length
  limit, moderation) → broadcast to all nodes, the authority included. The
  server can also inject system messages (joins, leaves, kicks,
  announcements).
- Native → Lua: `raise_lua("x4mp.chat_recv", json)`. The JSON is escaped, and
  multiple messages are batched per frame.
- Slash commands are handled client-side: `/who`, `/ping`, `/me`, `/w <name>`.
  Unknown commands are **not** passed to `ExecuteDebugCommand`, which is what
  the vanilla chat window does for `/…` (`chatwindow.lua:465`).

### 5.7 Summary table

| Event | Detect (where/how) | Wire | Authority action | Client apply |
|---|---|---|---|---|
| Kill (by client) | client MD Killed / `event_player_killed_object` | `Event.Kill` | destroy + `Despawn(destroyed)` | explode/remove ghost |
| Kill (on authority) | authority MD Killed / invalid on read | `Despawn(destroyed)` | — | explode/remove ghost |
| Player death | client MD / `event_object_destroyed object=player.occupiedship` | `Event.PlayerDied` | destroy avatar (`SafeDestroy`) | remove that player's ghost |
| Damage to client (M5+) | authority avatar hull change | `Event.Damage` | — | MD set hull/shield on own ship |
| Trade | `event_player_trade_completed` + cargo diff | `Intent{TradeReport}`, `EntityCargo` (own ship), `CreditDelta` | MD `add_cargo`/`remove_cargo` + `StationStock`; owner faction money | stock change on matched station; local money reconcile (12) |
| Build | MD build finished + 10 s diff | `Event.BuildRequest` | create station + `EntitySpawn` | spawn or match station |
| Capture | MD EntityChangedOwner | `Event.Capture` | `SetComponentOwner` + `OwnerChanged` | set owner on ghost |
| Chat | Lua UI | `Chat` | display | display |

---

## 6. Safety

### 6.1 Never remove the player's ship

- `PlayerGuard::refresh()` runs every frame (cheap) and gathers:
  `GetPlayerOccupiedShipID`, `GetPlayerControlledShipID`, `GetPlayerShipID`,
  `GetPlayerObjectID`, the `ship` context of the player object, and the
  container the player is docked in. When the player is docked,
  `GetPlayerControlledShipID()` returns 0, which caused the reference's reload
  loop.
- `SafeRemove(id)` is **the only** function in the codebase that calls
  `RemoveComponent`. It refuses, logs `remove_blocked_by_guard` and counts the
  attempt when:
  - `id` is in `PlayerGuard`, **or**
  - `id` is the context (ship or station) of any id in `PlayerGuard`, **or**
  - the player owns `id` and it is not a ghost.
- A CI grep test fails the build if `RemoveComponent` appears anywhere outside
  `PlayerGuard.cpp`.

### 6.2 Save hygiene

Goal: no ghost or mod helper object ever lands in a save file. Team-owned
assets (avatars, built stations, bought or captured ships) are **real session
data**. They do go into authority session saves, by design (1.2, 11.8).

1. **Ghost registry.** Every spawned ghost is registered. The registry is
   mirrored to the stash so it survives extension reloads within a universe. On
   the authority, ghosts are rare: only the fallback mirrors of 3.7, and debug
   objects. On clients, ghosts make up almost everything we spawn.
2. **Client: no saving while connected.**
   - Autosave: `md/notifications.xml` is shipped as an MD **diff** that changes
     the `AutoSave_Attempt` branch `do_elseif value="player.autosave.available"`
     to `player.autosave.available and not global.$x4mp_noSave?`. The vanilla
     cue is at `x4-unpacked/md/notifications.xml` lines 904–930. Native sets the
     flag through Lua → `AddUITriggeredEvent` → an MD cue that sets
     `global.$x4mp_noSave`.
   - Manual and quick save: `x4mp_bridge.lua` wraps the global `IsSavingPossible`
     and `SaveGame` (used by `gameoptions.lua:1277, 9296, 9310`). While a client
     session is active, `IsSavingPossible` returns false, and the menu shows the
     mouse-over "Saving is disabled while connected as a client". **[VERIFY]**
     that the globals are writable from an addon and that quicksave goes
     through them. If quicksave bypasses Lua, add an X4Native `hook_before` on
     the exported save function as a fallback.
   - `C.TriggerAutosave(bool)` exists in the ffi list. We never call it on
     clients.
3. **Authority: controlled saves only.**
   - Vanilla autosave is blocked through the same flag. Saves happen only on server
     `RequestSave` (every `AutosaveMinutes`, default 15, admin "Save now", join or
     shutdown); the mod has no timer of its own (ADR-008).
   - Save sequence (`SaveJob`): freeze replication, despawn all ghost-registry
     objects (normally none on the authority), verify the registry is empty
     (`IsValidComponent` false for all), call `SaveGame(slot, name)` through Lua and
     send `SaveStarted` (journal marker) in the same frame, build the checkpoint
     **manifest** (net_ids, `AvatarRegistry`, `inherit_team` status; it replaces the
     sidecar and is uploaded with the save, ADR-008), wait for `on_game_save`
     (`event_game_saved` via `x4native_main.xml`), respawn any ghosts, then
     resume. Avatars stay in place. The pause is a few hundred ms of replication
     freeze, plus whatever time the game's own save takes.
   - A manual save by the authority's user goes through the same wrapped
     `SaveGame`, which runs `SaveJob` instead.
4. **Janitor on load (both roles).** At `on_universe_ready`, scan for objects
   carrying our ghost name prefix (`[MP] `). With the team model, faction
   ownership is **not** a ghost marker, because team factions own real assets.
   Also scan for the reference mod's leftovers: objects owned by `x4mp_host` or
   `x4mp_client_*`, and player-owned satellites in client sectors with no
   matching user record. Remove them with `SafeRemove` and log the count. This
   cleans saves polluted by the reference or by a crash mid-session. When
   loading **outside** a session, team-owned objects are left alone, and the
   mod warns if any `x4mp_team_*` faction is active.
5. Saves used for sessions are named `x4mp_<session>_<n>` and kept separate
   from the user's own slots.

### 6.3 Crash resilience

- All `x4n` callbacks are wrapped in `try { … } catch (...) { log; disable feature }`.
  No C++ exception may cross into X4. A feature that throws 3 times is disabled
  for the session, and the server and HUD are told.
- Before every use of a stored UniverseID, check `IsValidComponent`. Never cache
  pointers into game memory.
- No game calls from the net or MD threads (enforced by an
  `assert_main_thread()` in every `X4Game` method in debug builds).
- Spawn and remove rates are capped (4.3), and every per-frame loop has a
  budget.
- We do **not** install `SetUnhandledExceptionFilter`, because X4 has its own
  crash reporter. Instead, the logger flushes every 1 s and on ERROR, so the
  last lines before a crash are on disk. Optional: a `MiniDumpWriteDump` on our
  own detected fatal states (assert failures), written to the log folder.
- Network failure never affects the game: there are no blocking calls, the
  outbox is bounded, and the client degrades to frozen ghosts and then offline
  (4.8).
- Shutdown order in `X4N_SHUTDOWN`: unsubscribe events and MD subs, stop
  scheduler jobs, stop and join the net thread (200 ms cap), flush the log,
  write the stash. **Do not** remove ghosts on shutdown during an extension
  reload. The universe persists, and the registry in the stash keeps them
  tracked.

### 6.4 Version and compatibility checks (at `Hello`)

The node sends:

- protocol version and capability flags;
- mod build hash and semver;
- X4 version and build (`x4n::game_version()` plus the build number from
  `GetBuildVersionSuffix` / `GetVersionString` as used in
  `gameoptions.lua:1249`);
- X4Native version and `md_hook=installed|fallback`;
- enabled `ego_dlc_*` list (`C.IsExtensionEnabled`) and whether other
  non-Egosoft extensions are enabled (a warning, not an error);
- for clients, the **save identity**: filename, SHA-256 of the `.xml.gz`, and
  the in-save game id if readable.

The server rejects or warns according to session policy. The authority's values
define the session. Mismatched game build (outside the pinned list or different from the
authority's), protocol major, mod build, DLC/extension set, or the wrong save hash are
fatal (ADR-004).

The server sends `Welcome{player_id, roles, resume_token, team, faction_slot, TeamTable,
TeamRelations, SessionSettings, udp_token, …}`, then `SessionSaveInfo` (protocol.md §4.1, §6.4).

---

## 7. Lua UI

All UI code follows the vanilla patterns in
`x4-unpacked/ui/addons/ego_gameoptions/gameoptions.lua` and
`x4-unpacked/ui/addons/ego_chatwindow/chatwindow.lua`.

### 7.1 Lua ↔ native bridge (`x4mp_bridge.lua`)

- Handle: `_G.__X4NATIVE_API`, set by `extensions/x4native/ui/x4native.lua`.
  Our files may load first, so they use a retry-on-`gfx_ok`/`show` pattern, the
  same one used in `reference/.../x4mp_menu.lua`.
- **Lua → native:** `api.raise_event("x4mp.<verb>", jsonString)`. Each verb is
  subscribed natively with `x4n::on("x4mp.<verb>", cb(const char*))`. Verbs:
  `join`, `disconnect`, `chat_send`, `ui_ready`, `request_status`,
  `md_fallback`, `save_requested`.
- **Native → Lua:** `x4n::raise_lua("x4mp.<topic>", json)`. This must run on the
  UI thread, which is always true for us because we only call it from
  `on_frame_update` handlers. Lua listens with `RegisterEvent("x4mp.<topic>", fn)`.
  Topics: `status` (at most 2 Hz), `chat_recv`, `players`, `notify`,
  `load_save`, `md_action`, `error`.
- Payloads are a small JSON subset. Native uses a vendored single-header JSON
  library. Lua uses a ~150-line minimal JSON codec in `x4mp_bridge.lua`. X4's
  Lua has no `json` module, and we do not depend on another mod.
- **Lua → MD:** `AddUITriggeredEvent("x4mp", control, param)`, consumed in
  `md/x4mp_main.xml` by `event_ui_triggered`. **MD → Lua:**
  `raise_lua_event` (vanilla pattern, for example `md/*.xml`
  `raise_lua_event name="'info_updatePeople'"`).

### 7.2 Main-menu entry

Menu injection through an upvalue is fragile (PIT-057, x4-api-notes 3.2), so it
is isolated and guarded, and every feature stays reachable without it.

- **One adapter module**, `x4mp_optionsmenu_adapter`, is the only code that
  touches `OptionsMenu` internals. Before doing anything it **probes**:
  `Menus` contains `OptionsMenu`; `require("debug")` returns a table with
  `getupvalue`; the `config` upvalue exists and has
  `optionDefinitions.main` as an array; `menu.submenuHandler`,
  `menu.createOptionsFrame` and `Helper.clearDataForRefresh` are functions.
  The result goes into one log line, `X4MP ui: optionsmenu adapter OK|DEGRADED(<failed probe>)`,
  which is also forwarded to native and the server (health check).
- All of our screens (Multiplayer, Join, Economy, Players) are written against a
  **small rendering interface**: `beginScreen(title)`, `addEditRow`,
  `addButtonRow`, `addTextRow`, `endScreen`. There are two implementations:
  - (a) **Embedded:** draws inside the OptionsMenu frame. This is the primary
    path, with the vanilla look, and it works in the start menu.
  - (b) **Standalone:** our own registered menu, `X4MPMenu`
    (`table.insert(Menus, menu)` plus `Helper.registerMenu`, as in
    `chatwindow.lua:41-46`), with its own frame created by
    `Helper.createFrameHandle` (helper.lua:3767).
- If any probe fails, the adapter injects nothing, and the screens use
  implementation (b). Entry points for (b), any of which works:
  - the chat command `/mp` from the vanilla chat window adapter (7.6);
  - the HUD widget (click);
  - a row in **Settings → Extensions → X4 Multiplayer**, using X4Native's own
    maintained settings injector (`x4n_settings_menu.lua`) through a `button`
    setting **[VERIFY setting type support]**;
  - automatically on start, when `launch.json` requests a connect.
  **[VERIFY]** that a standalone menu can open over the start menu, which is
  itself `OptionsMenu`.
- kuertee's UI Extensions mod would give stable hooks, but we do **not** take it
  as a dependency (x4-api-notes 3.2).
- After each game patch, the `selftest` (8.5) re-runs the probes, and CI keeps
  a recorded hash of `gameoptions.lua` from the pinned build, so changes are
  noticed.

Embedded-mode details:

- Injection (as in X4Native's settings injector and the reference): find
  `Menus[i].name == "OptionsMenu"`, pull the `config` upvalue of
  `menu.displayOptions` with `require("debug").getupvalue`, and edit
  `config.optionDefinitions["main"]`. The vanilla table is at
  `gameoptions.lua:1246–1352`.
- Insert **one** row `{ id = "x4mp", name = ReadText(92000,1) --[["Multiplayer (X4MP)"]], submenu = "x4mp" }`
  right after the vanilla `timelines` entry (index 8), **before** the separator
  line. Do **not** reuse id `multiplayer`. Vanilla already has a hidden
  `multiplayer` entry (`display = C.IsNetworkEngineEnabled`, lines 1306–1311)
  backed by Egosoft's SLNet lobby (`displayLobby`, line 11912), which needs Steam
  or EgoNet and does not work for us (STATE.md 6d).
- The row is visible in both the start menu and the in-game pause menu
  (`menu.isStartmenu` false). Its label adds a status suffix, for example
  `Multiplayer — Connected (3)`.
- Wrap `menu.submenuHandler` (vanilla at line 5103). For our ids
  (`x4mp`, `x4mp_join`, `x4mp_status`) call our display functions. Otherwise
  delegate. Injection is idempotent and checks for already-injected rows,
  because `/reloadui` re-runs the files.

### 7.3 Multiplayer screen (`x4mp`)

Built like `menu.displayLobby` / `menu.displayOnlineLogin`
(gameoptions.lua 11912–12070): `Helper.clearDataForRefresh(menu, config.optionsLayer)`,
`frame = menu.createOptionsFrame()`, `ftable = frame:addTable(…)`, a header row
with a back-arrow button (`menu.onCloseElement("back")`), then rows. Note that
`config` here is the upvalue we captured.

- **Not connected:** rows `Join server…` → `x4mp_join`, `Recent servers` (from
  `__X4MP_USER`), and `Help` (opens a text page).
- **Connected:** status block (server, role, team, ping, players, sync health),
  plus `Player list`, `Teams` (11.7), `Credits / Economy` (12.6), `Disconnect`
  (with confirmation via the vanilla `menu.displayUserQuestion`, line 12716),
  and `Chat`.

### 7.4 Join dialog (`x4mp_join`)

The layout copies the online-login form (`displayOnlineLogin`, lines 11958–12070):

| Row | Widget | Notes |
|---|---|---|
| Server address | `createEditBox({ description = …, defaultText = "host:port" })`, `onTextChanged` → `state.address` | Default is `__X4MP_USER.lastAddress`. Validate `host[:port]`. Port default from config. |
| Player name | edit box, `onTextChanged` → `state.name` | 1–24 chars, sanitised. Default is the last name, else `OnlineGetUserName()` if non-empty. |
| Password | edit box with `textHidden = true` | **Do not** use `encrypted = true`. Vanilla encrypted boxes go through `C.ResetEncryptedDirectInputData()` and the text is not delivered to `onTextChanged` in clear **[VERIFY]**. The password is kept in a local only until it is sent, then cleared. |
| Team | dropdown, filled after a server pre-query (`x4mp.server_info`), shown only if the session allows choosing a team | "Auto" or one of the session's teams, with member counts. Locked teams are greyed out (11.5). |
| Connect | button, `active = function() return valid(state) and not state.busy end` | → `raise_event("x4mp.join", json{address,name,password,team})` |
| Status line | text, live from `x4mp.status` | "Connecting…", "Checking save…", "Wrong save: need X", "Rejected: version" … |

Edit boxes need `menu.noupdate = true` while active, as vanilla does at line 11842,
so that the periodic refresh does not steal focus.

**Join flow from the start menu.** Connect, `ServerHello` / `ClientHello` / `Welcome`, then
`SessionSaveInfo{sha256, local_file_name = x4mp_<sha12>.xml.gz}`. Native checks for a local save with that hash
in the save folder. The folder comes from the export `GetSaveFolderPath()` and
is never guessed (x4-api-notes 2.9, PIT-052).

- If it is found, native waits for `IsSaveListLoadingComplete()`, then raises
  `x4mp.load_save`. Lua raises the vanilla `loadSave` event, which is
  registered at gameoptions.lua:408 and handled by `menu.loadSaveCallback` at
  gameoptions.lua:2956. That handler validates with `C.IsSaveValid` and calls
  `LoadGame` after 0.1 s. If the vanilla handler is missing in some build, our
  bridge calls `LoadGame(name)` itself through the same delayed callback. Pass
  the filename **without** `.xml.gz` (STATE.md 5).
- If it is missing, download it **in-band** on the Bulk lane (`SaveDownloadRequest`,
  windowed `SaveChunk`s, resume by offset), verify SHA-256, then load (M2; HTTP is an
  optional fallback, ADR-007).

The session stays connected through the load, because the stash and resume
token handle the extension reload (2.6). After `on_universe_ready` the node
reports `Ready`, and the server starts streaming.

**Join flow in game** (already in a universe): allowed only if the loaded save's
hash matches. Otherwise the dialog offers "Load the session save" (save first
prompt).

### 7.5 Connection status HUD (`x4mp_hud.lua`)

- This is a separate menu registered like `chatwindow.lua` (lines 28–44):
  `Menus` insert plus `Helper.registerMenu(menu)`, with its own small frame on
  a high layer (`config.layer = 3`, the same layer the chat uses), anchored at
  the top-right. Position is persisted in `__X4MP_USER.hudPos`.
- Content: a coloured dot (green connected, amber degraded or resuming, red
  offline), role, ping in ms, player count, and while degraded a reason ("MD
  hook fallback", "high jitter", "desync: n ghosts failed").
- Updates come from `x4mp.status` (2 Hz). The HUD redraws only on change. It
  takes no input and must not take focus. **[VERIFY]** that a passive frame on
  layer 3 coexists with the cockpit HUD and closes cleanly when other menus
  open. If not, fall back to transient notifications only (next bullet).
- Transient events (player joined or left, disconnected, save in progress) use
  the vanilla notification path: MD `show_notification` through
  `AddUITriggeredEvent`, or `show_help` (used in `md/notifications.xml:915`).

### 7.6 Chat

- **Preferred:** reuse the vanilla chat window (`ego_chatwindow`). It already
  has a hotkey (`INPUT_ACTION_SHOW_CHAT_WINDOW`, `alwaysactive`), an input box,
  scroll-back and drag. It reads messages from the **global**
  `OnlineGetChatMessages()` (chatwindow.lua:262) and sends through the global
  `OnlineSendChatMessage(text, userid)` (line 443). While a session is active,
  `x4mp_chat.lua` wraps both globals:
  - Get: returns our ring of `{author, authorid, time, text}` merged with the
    vanilla messages.
  - Send: routes to `x4mp.chat_send`.
  - New-message refresh: find the `ChatWindow` menu in `Menus` and call its
    `onChatMessageReceived()`, because the vanilla trigger is an engine
    `registerForEvent` on `Scene.UIContract`.
  - Disconnect restores the originals.
  - **[VERIFY]** that the chat window works outside Ventures, and that author
    colouring through `C.GetChatAuthorColor2` accepts arbitrary names.
- **Fallback:** our own chat menu cloned from the chatwindow structure: a frame
  with a message table and an edit box, opened from the Multiplayer screen and
  by typing in the HUD. It has no custom hotkey in v1, because custom input
  actions need input-map work **[VERIFY]**.

### 7.7 Player list (`x4mp_players.lua`)

This is a table in the Multiplayer screen, and optionally its own small menu.
Columns: name (in faction colour), role, sector (localised from the sector
macro), ping, ship class, status. It is driven by `x4mp.players` (on change,
plus every 5 s). Rows offer "Locate on map" and "Whisper". "Locate on map"
opens the map focused on the ghost through `OpenMenu("MapMenu", …)`
**[VERIFY params]**.

### 7.8 Text

All strings live in `t/0001-l044.xml`, page **92000**. The reference used 90001,
and we avoid colliding with it and with other mods. `ReadText(92000, n)` is used
everywhere, and the Lua code contains no inline English.

---

## 8. Build, repo layout, testing, diagnostics

### 8.1 Repo layout (mod part)

```
mod/
├── CMakeLists.txt            top-level (options: X4_DIR, X4MP_DEPLOY, X4MP_TESTS)
├── CMakePresets.json         msvc-x64-debug / msvc-x64-release / msvc-x64-relwithdebinfo
├── vcpkg.json                manifest: catch2 (tests), zstd or lz4 (if protocol uses it), nlohmann-json or a single-header json
├── native/                   (layout in 2.1)
├── tests/                    Catch2 unit tests for core/, roles/ via FakeGame
├── extension/x4mp/           content.xml, ui.xml, x4native.json, ui/, md/, t/, libraries/, config/
├── third_party/x4native/<tag>/  vendored SDK + runtime + LICENSE + VERSION
└── tools/
    ├── deploy.ps1            copy dll + extension files into %X4_DIR%\extensions\x4mp
    ├── selftest.lua          (dev only) /x4mp_selftest command
    └── replay/               recorder/replayer of authority streams (8.4)
protocol/                     shared schema → generated C++ header consumed by mod/native/core/net
```

### 8.2 Build

- CMake ≥ 3.25, Visual Studio 2022 (MSVC v143), `x64` only, `/std:c++latest`.
  The X4Native SDK requires C++23. Flags: `/W4 /permissive- /EHsc /MT` (static
  CRT), `/guard:cf`, `/Zi` with `/DEBUG:FULL` in RelWithDebInfo. PDBs are kept
  out of the release zip but archived per build hash for crash analysis.
- Output `x4mp.dll` with no prefix (`PREFIX ""`, as in the upstream example
  `CMakeLists.txt`). Exports come only from the X4N macros.
- Link `ws2_32`. There are no other system dependencies.
- The protocol codegen step runs as a CMake custom command from `protocol/`, so
  the mod and the server always build from the same schema.
- Portability: Winsock is isolated in `core/net/socket_win.cpp`, and a POSIX
  file is stubbed so a later Linux port only replaces that file. Nothing else
  includes `<windows.h>` except `game/` and the logger.
- `cmake --build --preset msvc-x64-relwithdebinfo --target deploy` copies to
  `${X4_DIR}/extensions/x4mp/`. For iteration, X4Native's hot-reload
  (`/reloadui`) picks up a rebuilt DLL without restarting X4. Hot reload is
  dev-only. `autoreload: false` ships.
- CI (GitHub Actions `windows-latest`) builds, runs unit tests, runs the
  `RemoveComponent` grep guard, validates the XML against the game XSDs
  (`ui/core/addon.xsd`, `libraries/md.xsd` and `diff.xsd`, extracted with
  `tools/x4cat_extract.py`; the XSDs are not committed), runs `luacheck` on
  `ui/`, and packages the zip with an x4native `version_db` presence check.

### 8.3 Logging and diagnostics in practice

- **Session-save scan tool:** `tools/savescan.py <save.xml.gz>` streams the
  save XML and counts objects whose name starts with `[MP] `, objects owned by
  `x4mp_host`/`x4mp_client_*` (reference leftovers), and per-team asset counts.
  CI and the M3 acceptance test use it to prove that no ghosts leaked into a
  save (PIT-034).
- Log locations are covered in 2.7. A `Collect Logs` script in the installer
  zips `x4mp.log*`, `x4native.log`, the game's own `debug.log` (if `-debug`
  was used) and the config (redacted).
- In-game diagnostics:
  - The `x4mp.status` payload includes sync health.
  - A debug overlay toggle (`/x4mp_debug` in the chat) adds per-tier entity
    counts, interp delay, extrapolation share, spawn and remove rates, and the
    worst error in metres.
  - `[SYNC]` lines every 5 s: `ghosts`, `extrap%`, `snaps/s`, `maxerr`,
    `manifest_missing`.
- Server-side: the `NodeStats` metrics drive the dashboard, so the admin sees
  FPS and budgets for every node.

### 8.4 Testing without a second machine

The reference found that only one X4 instance can run per machine, so we rely on
these:

1. **Unit tests (no X4):** `core/` (codec round-trip, queues under contention,
   reconnect state machine, dead-reckoning thresholds, Hermite interpolation,
   clock sync) and `roles/` against **`FakeGame`**, an `IGame` implementation
   with a scripted universe (ships on paths, kills, sector changes). Example: run
   Authority + server mock + Client in one process over loopback with
   `FakeGame`s, and assert that the client ghost positions track the authority
   within ε.
2. **Real X4 as authority + fake clients:** the M1 `tools/fake-node` connects to
   the real server as N synthetic clients (moving player states, interest
   changes, kill and chat events). It checks the authority's capture, avatars
   (visible in the real game) and event application. This is the reference's
   `fake_client.py` idea, in the new architecture.
3. **Real X4 as client + fake authority (record/replay):** while running as
   authority, the mod can **record** its outgoing stream (`record_stream=true`
   writes `*.x4mprec`). Later, `tools/fake-node --replay file.x4mprec` acts as
   the authority, and a real X4 client loading the **same save** renders it. This
   tests the whole client pipeline (spawn, interpolation, sector entry,
   prune) on one machine.
4. **Mirror mode (single X4, loopback):** the server echoes the authority's own
   stream back to the same node as if it were a client. The node renders ghost
   copies with a fixed offset (for example +3 km on Y) and a debug faction.
   This gives a visual check of interpolation and change detection against the
   real ships side by side. It runs in one game, using the debug capability flag
   `mirror_test`.
5. **Second instance alternatives:** a VM with GPU passthrough, or a friend's PC
   or laptop on the LAN, or a cloud GPU box with Parsec. Needed only for M3/M4
   acceptance runs.
6. Network impairment: the server's test mode adds latency, jitter and loss
   on the relay (see server-design), for testing the interpolation and resume
   paths.

### 8.5 In-game self-test

Run `/x4mp_selftest` (chat), or `selftest=true` in the config, at
`on_universe_ready`. It runs read-only checks and logs a PASS/FAIL table:

- X4Native functions resolved and hooks installed;
- sector map size (expected 140 or more with all DLC);
- faction list, whether `x4mp_team_1` exists and can be activated (decides the
  team model or the 11.9 fallback);
- station match-key collisions in the interest sectors (4.2);
- `OptionsMenu` adapter probes (7.2);
- the supported-build check (1.5);
- spawn a test ghost 10 km from the player, move it, then remove it with
  `SafeRemove`, timing each step;
- `PlayerGuard` contents;
- save-wrap installed.

---

## 9. Milestones M2–M5: ordered tasks and acceptance criteria

> Milestone scope and ordering are owned by `docs/roadmap.md` (ADR-035: in-band save
> download and load are in M2; the net core is built headless in M1). The tasks below
> remain the detailed mod checklist.

These assume M0 (repo, CMake, protocol schema v0) and M1 (server plus fake
nodes) are done.

### M2: Mod loads, connects, join dialog, heartbeat

1. Vendor X4Native at tag `v9.0.0-611726`, set up the CMake skeleton, and get
   `x4mp.dll` loading with a "hello" log line. **AC:** the `x4native.log` shows
   x4mp loaded. `version_db` resolves 22 or more internal functions.
2. `core/log` (async, rate-limited) and `core/config` (file merge, no env).
   **AC:** unit tests pass. A bad config value falls back with an error line.
3. `core/net` thread: connect, non-blocking send/recv, framing, heartbeat,
   reconnect with backoff, clock-sync ping. **AC:** against the M1 server, the
   connection holds for 30 minutes, and killing and restarting the server leads
   to auto-reconnect within 5 s. Main-thread net cost is under 0.2 ms per frame
   (measured).
4. Session state machine plus `Hello`/`Welcome`, with version, DLC and
   save-hash checks. **AC:** a mismatched game build is rejected with a clear
   message in the UI. The admin GUI shows the node with its metrics.
5. Stash-based resume across the extension reload. **AC:** start-menu join,
   then load the save. The server shows **no** player leave and join, only a
   resume of the session.
6. Lua bridge, menu entry, Multiplayer screen, Join dialog (address, name,
   password) and `__X4MP_USER` persistence. **AC:** join from the start menu
   using only the UI. The last address and name are remembered after a restart.
   The password is not written anywhere (grep `uidata.xml` and the logs).
7. `launch.json` one-shot auto-connect. **AC:** the launcher-written file
   connects without UI, and the file is deleted after use.
8. HUD status widget (or the notification fallback). **AC:** the HUD shows
   ping and state, and survives opening the map and closing it again.
9. `PlayerGuard`, `SafeRemove`, the CI grep guard and the self-test skeleton.
   **AC:** the self-test PASS table is logged.

### M3: Player positions and player ghosts both ways

1. Team-faction experiment (11.10 items 1–3): activate `x4mp_team_1` in a save
   created before the mod was installed, spawn a ship for it, and set relations.
   Implement the team model (11.2–11.5), or the 11.9 fallback. **AC:** the
   self-test reports `team factions: OK`. A test ghost spawns under
   `x4mp_team_2` in a loaded campaign save with the right colour.
1b. Avatar provisioning and takeover (11.4). **AC:** two clients joining the
   same save each fly their **own** avatar. The save's original ship is the
   authority human's. Rejoin puts the player back in their avatar where it
   was parked.
2. `SectorMap` (macro ↔ id) and universe epoch. **AC:** both nodes report the
   same sector count, and the macros agree.
3. `PlayerState` capture (client and authority), 20 Hz, with change detection.
4. Ghost spawn, inert handling, interpolation (Hermite plus extrapolation),
   `GhostRegistry` and stash mirror. Player ghosts on clients, avatars on the
   authority. Ship-swap respawn. On-foot or docked hide.
5. Sector-change handling for player ghosts.
6. Chat, end to end, through the vanilla chat window adapter or the fallback.
7. Save hygiene v1: the client save block (MD autosave diff plus the Lua wrap)
   and the janitor. **AC:** a client cannot save while connected. An authority
   save made with ghosts present contains **no** ghost (the session-save scan in
   8.3 finds zero `[MP] ` objects), and avatars are present under their team
   factions after reload.

M3 overall acceptance (two machines, LAN): two players fly together for 30
minutes across 5 or more sectors, including a highway. Each sees the other's
ship with the correct model and a faction-coloured name. The error between
remote truth and the ghost is under 50 m at speeds below 500 m/s, as measured by
the `[SYNC] maxerr` comparison. There are no Game Overs, no log floods (fewer
than 10 lines per second sustained), and less than 1 FPS cost from the mod.

### M4: Authority world streaming, interest management, ghost-only clients

1. `CaptureSet` handling and the incremental universe index (dedupe, sized
   buffers, sector-indexed flag). **AC:** a full pass over all ships stays
   within budget. The authority spends 2 ms or less of mod time per frame at
   p95 with 3 interest sectors.
2. Tier scheduler meeting `CaptureSet` rates, dead-reckoning change detection,
   `EntitySpawn`, `WorldUpdate`, `EntityDespawn` and `SectorComplete`. Static
   net_ids come from the checkpoint manifest (ADR-009).
3. Client static matching (sector, macro and rounded position, with the ID code
   as tie-breaker) at
   `on_universe_ready`. **AC:** at least 99% of the stations in interest
   sectors match. Mismatches are logged with their keys.
4. Client sector entry: suppress local ships, budgeted ghost spawns, leave
   grace, late-NPC sweep.
5. Manifest-based pruning, plus frozen ghosts during a disconnect.
6. Record/replay tooling and mirror-test mode.
7. Region lookahead (neighbour prefetch). **AC:** on a highway transit, ghosts
   in the destination sector are already present when the player arrives.
   There are no visible pops within 5 km, verified by video and by the
   `[SYNC] spawn-near-player` counter being 0.
8. Attention-policy measurement (`native` versus `activate`). **AC:**
   documented FPS numbers, and a default chosen.

M4 overall acceptance: the client sees the same NPC ships as the authority in
its sector and the next sector. Position error for ships within 5 km is under
25 m (T1) at p95. A busy sector (200 or more ships) holds 45 FPS or more on the
client (excluding vanilla cost) and costs the authority less than 3 ms per
frame. Bandwidth to a client is under 200 kB/s in a busy sector. There is no
`[FLK]`-style flicker (no despawn and respawn of the same NetId within 10 s
while it stays in interest).

### M5: Events

1. MD capture ring plus fallbacks. The `md_hook` state is reported.
2. Kills (both origins) with explosion destruction. **AC:** a client kills an
   NPC ghost, and within 500 ms it is gone (exploded) on the authority and on a
   second client. An NPC killed by NPCs on the authority disappears for all.
3. Player death flow. **AC:** the client dies, its ghost disappears for others,
   and the client re-joins the session after reload with no duplicate ghost.
4. Trade and cargo with budgeted stock application. **AC:** a client sells 500
   units, and the authority station stock rises by 500 within 10 s with no
   frame spike over 2 ms. A buy reduces stock immediately on all nodes.
5. Station builds: plan extraction (stretch) or completed-station replication
   (baseline). **AC:** a client-built station appears for the authority and the
   other clients at the same position, with no duplicate on the builder.
6. Boarding capture, with the inert exemption during boarding. **AC:** a
   capture changes the owner on all nodes, and the captured ship survives an
   authority save/load cycle under the right faction.
7. Damage to client players (stretch). **AC:** NPCs on the authority can damage
   and kill a client player.
8. Chat moderation hooks, `/who`, `/ping`, `/w`, `/t`.
9. Team relation matrix (11.6), plus team switch (11.5). **AC:** a session with
   two hostile teams shows red targeting on both sides. Switching the matrix to
   allied takes effect on all nodes within 2 s. `admin_only` team switch moves
   the avatar's owner.
10. Wallets: reconciliation loop (12.2) and CreditMode Auto/PerPlayer/Shared.
   **AC:** a 1M Cr purchase on a client shows up in the ledger within 1 s.
   Two simultaneous purchases in Shared mode leave all members with the same
   final balance within 2 s. A forced local edit is corrected within 30 s.
11. Send credits, team pool (12.4) and the Economy screen (12.7), first cut.
12. Loans and escrowed trade offers (12.5–12.6). Stretch for M5, otherwise M6.
   **AC:** a ship-for-credits offer settles atomically (owner change plus
   balances). An injected failure after the first op rolls back to the exact
   prior state, and residuals are 0.

---

## 10. Risks and unknowns needing in-game verification

Ordered by impact:

1. **Team factions in loaded saves.** The DLC precedent suggests
   library-added factions do get instantiated for existing saves, but the
   reference failed with "Failed to retrieve owner faction". This is the
   foundation of the team model (11). If it fails, the 11.9 fallback (borrowed
   factions, all teams allied) is much weaker. *Test in M3 task 1.*
2. **NPCs cannot interact with client players in ghost mode.** Ghosts are
   inert, and the authority's NPCs only see an inert avatar. Without damage relay
   (5.1/M5.7), client players are effectively invulnerable to NPCs and NPCs
   ignore them. This is the largest gameplay gap. It depends on hull and
   shield setters, either through MD actions or functions.
3. **Extension reload during save load.** The reference saw the extension shut
   down and re-init. We rely on X4Native's stash surviving it (documented: it
   survives `/reloadui` and hot reload) and on X4N_SHUTDOWN letting us join the
   net thread cleanly. *Verify the stash survives the "Re-discovery" path, and
   whether the DLL is actually unloaded* (which affects whether the thread must
   be joined).
4. **Local-world suppression side effects.** Removing local NPC ships on the
   client may break missions, job spawners or faction logic, and the local
   economy keeps respawning ships. We may need MD diffs to quiet the client's
   job spawners.
5. **Station match keys.** Matching relies on sector, macro and rounded
   position, because ID codes are not unique (x4-api-notes 2.1). Collisions are
   possible for modular stations built very close together. They are counted by
   the self-test.
6. **`SetObjectSectorPos` per frame on many ghosts.** The visual smoothness
   (engine trails, physics resets) and CPU cost at 200 or more ghosts are
   unmeasured. There is also the cost of spawn bursts at sector entry (VRAM; the
   reference's SIGFPE).
7. **Per-sector enumeration cost.** If only the per-faction global enumeration
   exists, indexing about 90k ships continuously costs CPU on the authority.
   The budget is set, but the full-pass latency (newcomer discovery) is
   unknown.
8. **Save blocking and the save wrap.** Whether the Lua globals `SaveGame` and
   `IsSavingPossible` can be wrapped from an addon, whether quicksave bypasses
   Lua, and whether the MD autosave diff applies cleanly.
9. **Lua → MD actions** through `AddUITriggeredEvent` and
   `event_ui_triggered`: param plumbing for `destroy_object`, hull setting and
   notifications.
10. **Game updates.** Each X4 patch needs X4Native `version_db` entries.
    Without them we lose MD hooks (we degrade to fallbacks) and any internal
    functions (construction plans). Pin the supported builds and refuse
    unknown builds with a clear message.
11. **HUD frame coexistence** with cockpit and menus (layer and focus
    behaviour), and the vanilla chat window outside Ventures.
12. **Attention model.** Low-attention simulation of client-only sectors may
    look wrong (combat resolved abstractly). Forcing high attention cost the
    reference about 30% FPS.
13. **Station stock divergence.** Local station economies on clients drift.
    Only player trades are synced. Players may notice prices and stock that
    disagree.
14. **Bandwidth and latency over the internet (non-LAN).** The reference was
    LAN-only. Our dead reckoning helps, but interp-delay tuning for 100 ms or
    more RTT is unmeasured.

15. **Avatar takeover.** `TeleportPlayerTo` into a freshly spawned ship, and
    suppressing the save's original ship on clients, are both unproven (11.4).
    Without them, multiple players share one ship identity.
16. **Money semantics.** These are unverified: money units, negative
    `AddPlayerMoney`, negative balances, and the reliability of
    `event_player_money_updated`. Shared-wallet overdraft handling depends on
    them (12.3).
17. **Settlement side effects.** `SetComponentOwner` on ships with crew,
    commanders or orders, and MD cargo results, may need extra cleanup.
    Compensation must be tested with injected failures (12.6).
18. **Session saves need the mod.** Team-owned assets make session saves depend
    on `x4mp_team_*`. Loading one without the mod is unverified (1.2, 11.8).

### 10.1 Status of the parallel docs

- `docs/x4-api-notes.md` exists, and this document is aligned with it (see the
  header). Its open questions (section 7 there) are part of the risk list above
  and the self-test in 8.5.
- `docs/requirements.md` exists. Appendix A maps its pitfalls to sections here.
- `docs/protocol.md` provides the lanes, resume token, server timestamps and the
  canonical messages; the logical names used here map to them via
  `docs/decisions.md` ADR-027. The consolidated risk list is `decisions.md` Part 3
  and the spike plan is `docs/roadmap.md` (M2-spike).

---

## 11. Teams and factions (session setting)

User requirement: the session can (a) put several players in **one shared
faction**, (b) put players in **different factions**, and (c) start different
factions **allied, neutral or hostile**, with relations allowed to change
later. The model is **teams**: each team maps to exactly one faction, and a
**team relation matrix** is applied at session start and whenever it changes.

### 11.1 What X4 gives us (evidence)

- X4 has exactly one real player faction, `player`
  (`x4-unpacked/libraries/factions.xml:70`). It holds the account
  (`<account amount="0"/>`), licences and base relations. The local human can
  only pilot and command assets owned by `player`.
- Factions are defined in `libraries/factions.xml`. **DLCs add factions with a
  diff on the same file** (`extensions/ego_dlc_boron|pirate|split|terran|timelines/libraries/factions.xml`).
  DLCs can be enabled on existing saves, which is the strongest evidence that
  library-added factions are instantiated for loaded saves too. The reference's
  "Failed to retrieve owner faction x4mp_client_N" is unexplained: its diff
  (`reference/extensions/x4mp/libraries/factions.xml`) may simply never have
  been applied or tested.
- Factions can ship **inactive** and be switched on at runtime: `trinity` is
  defined with `active="0"` (factions.xml:647) and activated by MD
  `<set_faction_active faction="faction.trinity" active="true"/>`
  (`md/story_paranid.xml:19542`, `extensions/ego_dlc_mini_01/md/setup_dlc_mini_01.xml:277`).
  The action is in `libraries/common.xsd:20324`.
- Relations: `<set_faction_relation faction= otherfaction= value= reason=>`
  (common.xsd:35318, used in `md/diplomacy.xml:147,232,4591`),
  `<add_faction_relation>`, `<reset_faction_relation>`,
  `<set_faction_relation_locked>` (35358), and
  `<set_faction_diplomacy_exclusion>` (35429), which keeps a faction out of the
  diplomacy minigame. Value ranges are documented in the DLC factions.xml
  header: ally 0.5–1.0, friend 0.01–1.0, neutral ±0.01, enemy below −0.01,
  kill at −0.32 or lower, nemesis −1.0.
- Colours: `libraries/colors.xml` maps `faction_<id>` to a colour
  (colors.xml:977–1002). Our diff adds `faction_x4mp_team_1..8`.
- Ownership changes: the export `SetComponentOwner(id, factionid)`
  (x4-api-notes 2.5).

### 11.2 Faction slots and node-relative mapping

`libraries/factions.xml` diff adds eight factions, `x4mp_team_1` … `x4mp_team_8`:

```xml
<diff>
  <add sel="/factions">
    <faction id="x4mp_team_1" name="{92000,101}" shortname="{92000,102}" prefixname="{92000,103}"
             description="{92000,104}" behaviourset="default" active="0"
             tags="claimspace nodiplomacyselection">     <!-- [VERIFY tag effects] -->
      <color ref="faction_x4mp_team_1"/>
      <icon active="faction_player" inactive="faction_player"/>   <!-- reuse; custom icons later -->
      <account amount="0"/>
      <licences> <!-- same set as faction "player" (factions.xml:74-80) --> </licences>
      <relations> <!-- copy of player's base relations (factions.xml:81-89) --> </relations>
    </faction>
    …
  </add>
</diff>
```

- `active="0"` means the factions are inert in ordinary single-player games.
  A session activates only the teams it uses, through the actions shim
  (`set_faction_active … active="true"`).
- Team display names are **session data**. The text entries are placeholders,
  and the HUD, player list and chat prefixes use the server-provided team name.
  **[VERIFY]** whether `set_faction_identity` (common.xsd:20215) can rename a
  faction at runtime. If it can, apply the session's team names.
- Up to 8 teams. Each team holds 1 to N players.

**Wire and mapping rule.** The wire never carries faction strings for teams.
It carries `teamIndex` (1–8), or `npc:<factionid>` for NPC owners. Each node
maps them with one function:

```
localFaction(owner):
  npc:<f>              → f
  team k (default "symmetric" mode) → "x4mp_team_k"
  except: the local human's own avatar ship → "player"   (required to fly it)
```

**Recommended default: symmetric mode.** On **every** node, including the
authority, team k's assets are owned by `x4mp_team_k`. The real `player`
faction on each node owns only that node's human avatar (and, on the authority,
nothing else after session setup, see 11.3). Why:

1. **Fair and uniform.** The authority player has no special ownership powers.
2. **Clean money accounting.** Asset income lands in team faction accounts and
   is never mixed into a human's `player` account (section 12).
3. **Team switches and relation changes don't re-own the world.** A viewer's
   team only changes relations, never the owner of ghosts.

**Alternative: native-host mode** (`authority_team_native=true`). The
authority's team maps to `player` on the authority, so the authority human can
command team assets with the vanilla UI. The cost is that money attribution on
the authority becomes heuristic (income and the human's own spending share one
account), and it is asymmetric. It is offered for one-team co-op hosts and is
not the default.

**So does team 1 map to the real player faction?** No, not by default. "Team
1" is just `x4mp_team_1`. On every node, the real `player` faction means "me,
the local human" and owns only my avatar. In native-host mode, the authority's
team maps to `player` **on the authority only**.

### 11.3 Who owns what, where

| Asset | On the authority | On a client of the same team | On a client of another team |
|---|---|---|---|
| A player's avatar ship | `x4mp_team_k` (a real, persistent ship, kinematically driven while online, 3.7). The authority human's own avatar is `player`. | Own avatar: `player` (local "self" copy). A teammate's avatar: ghost under `x4mp_team_k`. | Ghost under `x4mp_team_k` |
| Team stations, ships, built or captured | `x4mp_team_k`, fully simulated | Ghost or matched static under `x4mp_team_k` | Same |
| NPC assets | Real NPC faction | Ghost or matched, real NPC faction | Same |
| The save's pre-existing `player` assets | At **session creation** (once), re-owned to `x4mp_team_<inherit_team>` (setting, default: the authority player's team, ADR-033) by a budgeted job. Money moves to that team's pool (12.1). Recorded in the checkpoint manifest so it never repeats. | Matched statics re-owned locally to the same faction from the authority's `StaticOwner` data. Ships are ghosts anyway. | Same |

Client-side "player-like" presentation of your own team. In v1, your team's
assets are owned by `x4mp_team_<mine>` locally, with a locked relation of +1.0
to `player`. They therefore show as allied and friendly, in the team colour,
and they are listed under **Teams → Assets** in our own UI (11.7). They do
**not** appear in the vanilla property list, because the vanilla UI would issue
local orders to inert ghosts. **Player-view mode** (M6): own-team assets are
mapped to `player` locally, so the vanilla property UI and map orders work.
Order creation on those ghosts is intercepted with X4Native `hook_before` on
`CreateOrder`/`CreateOrder3`/`EnableOrder`, plus a Lua wrap of the global
`SetOrderParam`, and forwarded as `Command{netId, order, params}` (11.6).
**[VERIFY]** the side effects of `player`-owned ghosts: notifications, salary,
the "player owned destroyed" MD events on despawn.

### 11.4 Avatar provisioning (where each player's ship comes from)

Everyone loads the same save, so every client initially sits in **the save's
player ship**. Without intervention, everyone would be flying the same ship.

- **First join:** the server tells the authority to create the player's avatar.
  The ship macro comes from the session setting `starter_ship` (or the team
  template), spawned at the team spawn point (team HQ station or a configured
  sector and position) with
  `SpawnObjectAtPos2(macro, sector, pos, "x4mp_team_k")`. The authority returns
  its NetId.
- **Client takeover:** the client spawns a local copy of the avatar at the same
  position, owned by `player` (NetMap kind `self`), then moves the human into it
  with `TeleportPlayerTo(ship, allowcontrolling=true, instant=true, force=true)`
  after checking `CanTeleportPlayerTo` (x4-api-notes 2.2) **[VERIFY]**. Once
  `PlayerGuard` shows the player in the new ship, the save's original player
  ship on this client is suppressed. It belongs to the authority human, and
  arrives as a ghost like any other.
- **Rejoin:** the avatar already exists on the authority (persisted). The client
  spawns its self copy at the avatar's current position and teleports in.
- **The authority human** keeps the save's player ship as their avatar
  (`player`-owned on the authority, reported as their team).
- **Death:** see 5.2. The avatar is destroyed on the authority. On respawn the
  server provisions a new starter ship. The policy setting is `respawn_ship`.

### 11.5 Joins, team selection, switches

- **Join:** `Welcome` carries `teamIndex`, the team table
  (`{index, name, colour, factionId, members}`), the relation matrix and
  `creditMode`. The team is chosen in the Join dialog when the session allows
  it (`team_select = player|admin|auto_balance`). Otherwise the server assigns
  it.
- **Node setup after `on_universe_ready`**, as one budgeted job:
  1. Activate the session's team factions (actions shim, `set_faction_active`).
  2. Apply relations (11.6).
  3. Re-own matched statics per `StaticOwner`.
  4. Provision or take over the avatar (11.4).
  5. Report the result in `LoadStatus` (detail/error) and in `NodeStats`
     (`team_setup_state`). The server shows failures in the GUI.
- **Team switch** is allowed only when the session setting `team_switch` is
  `free`, `docked_only` or `admin_only` (default `admin_only`), and never in
  combat:
  1. The server updates the team table and broadcasts `TeamMemberChanged{player_id, from_team, to_team}`.
  2. The authority re-owns the switching player's avatar:
     `SetComponentOwner(avatar, x4mp_team_to)`.
  3. Personal assets follow the setting `team_switch_assets = stay|follow`
     (default `stay`). With `follow`, the authority re-owns every asset whose
     `creatorPlayerId` is that player.
  4. The switching node re-applies its local `player` relations for the new
     team. In symmetric mode, no other ownership changes anywhere.
  5. The wallet follows the player. Shared-pool shares do not (12.1).
- **Leave or disconnect:** the avatar stays parked and inert. Team assets keep
  running on the authority.

### 11.6 Relations and commands

**Team matrix** `M[i][j] ∈ {allied, neutral, hostile}`, with values
`allied = +0.75` (ally range), `neutral = 0.0` and `hostile = −1.0`. Applied
through the actions shim:

- Authority:
  - for all i≠j: `set_faction_relation x4mp_team_i ↔ x4mp_team_j = M[i][j]`;
  - authority `player` ↔ `x4mp_team_j` = `M[authTeam][j]` (or +1.0 for its
    own team);
  - then `set_faction_relation_locked` on each pair, so that the game's own
    reputation logic does not drift them. When the matrix changes, it unlocks,
    sets and relocks.
- Client of team t:
  - `player` ↔ `x4mp_team_j` = `M[t][j]`, and +1.0 for j = t;
  - team↔team pairs are the same as on the authority, so ghosts behave
    consistently.
- `set_faction_diplomacy_exclusion` on every team faction keeps them out of the
  vanilla diplomacy system **[VERIFY]**.
- Changes (admin GUI or a team vote, per session policy) arrive as
  `TeamRelations{matrix, seq}` and are re-applied idempotently.

**PvP.** Hostile teams can shoot each other's ghosts. A kill of a player's
avatar ghost on client A is reported as `Event.Kill{victim=avatarNetId}`. The
server routes it to the victim's client, which runs the normal death flow
(5.2). This requires `pvp=true` in the session. Damage relay is M5+ (5.1).

**Team ↔ NPC reputation.** At session creation, each team faction copies the
save's `player` relations to all NPC factions, through an MD loop over
`faction.player.relationto.{$f}` **[VERIFY property]**. During the session,
reputation is shared per team: a client's `event_player_relation_changed`
deltas go to the server, the authority applies `add_faction_relation` to the
team faction, and the authority broadcasts team↔NPC relations every 60 s.
Clients apply them to their local `player`. This is M6. Until then, team↔NPC
relations stay at their session-start copy.

**Commands on shared team assets** (M6). Session setting
`team_command = all_members|creator_only|leaders`. Each asset records
`creatorPlayerId` (builder, buyer or capturer). Clients send
`Command{netId, orderId, params[]}`. The server checks permission. The
authority executes `C.CreateOrder(ship, orderId, false)` →
Lua `SetOrderParam(ship, idx, p, nil, value)` → `C.EnableOrder(ship, idx)`, on
the UI thread through the Lua shim (x4-api-notes 2.8). Param layouts come from
`aiscripts/order.*.xml` **[VERIFY]**. Until M6, team assets on the authority
run their own default AI, and only the authority's human (in native-host mode)
can order them natively.

### 11.7 UI

- **Teams panel** in the Multiplayer screen: lists teams with colour swatch,
  name, members (online state), relation to my team (allied, neutral or hostile
  icon) and an asset count. Shows **Assets** for my team: avatars, stations and
  ships, with sector and status. "Locate on map" opens the map focused on the
  asset.
- Player list (7.7) gains a team column, and chat gains a team channel
  (`/t <msg>`).
- An admin-only matrix editor lives in the **server GUI**, not in game.

### 11.8 Save hygiene with teams

- Team factions, their relations, their active state and team-owned assets are
  **persistent session state** in authority saves. Ghosts are never persisted
  (6.2).
- The checkpoint manifest (ADR-008; the team table itself is server-owned) records
  `inherit_team` conversion done or not, `AvatarRegistry`, creator ids, and the
  NetId map, so a restart restores the exact team layout.
- Clients never save while connected (6.2). A client's own local save files
  are untouched by sessions, because sessions always load the session save.
- Loading a session save **without** the mod is unsupported and **[VERIFY]**
  (1.2). Loading a session save **with** the mod but outside a session: the
  janitor warns that team factions are active, and offers to (a) leave it as
  is, or (b) export team-owned assets to `player` with
  `SetComponentOwner` (single-player continuation).

### 11.9 Fallback if library factions fail (Plan B)

If the self-test shows `x4mp_team_*` absent or not activatable in a loaded
save, teams map to **borrowed real factions**. These are picked
deterministically from minor factions present in the save, excluding `player`,
the faction the player currently pilots (campaign starts), and any faction with
`nodiplomacyselection`. In this mode the relation matrix cannot be applied
freely without disturbing NPC diplomacy, so it degrades to "all teams allied,
own team = `player`-like ghosts". Avatars become ghost mirrors that are
stripped before each save (3.7 fallback). The admin GUI shows "Team factions
unavailable: degraded".

### 11.10 Needs in-game verification

1. `x4mp_team_*` exists after loading a save made **before** the mod was
   installed, and `set_faction_active` works on it.
2. `SpawnObjectAtPos2(…, "x4mp_team_k")` succeeds, and the colour mapping and
   icon render.
3. `set_faction_relation` and `set_faction_relation_locked` between two mod
   factions, and between `player` and a mod faction, behave as expected:
   targeting colours, and NPC police reactions.
4. The effect of tags (`claimspace`, `nodiplomacyselection`), and
   `set_faction_diplomacy_exclusion`.
5. `TeleportPlayerTo` into a freshly spawned `player`-owned ship, and suppressing
   the original ship afterwards.
6. The re-own job for the save's `player` assets: cost, and side effects
   (managers, subordinates, HQ, research).
7. `set_faction_identity` for runtime team names.
8. What happens when a session save is loaded without the mod.
9. Licence and docking behaviour for team-owned ships at NPC stations.

---

## 12. Credits and economy

User policy:

- Default **per-player wallets**.
- Players can **send** credits to teammates, and there is an optional **team
  pool**.
- A session with **only one team** uses a **shared wallet** automatically.
- Session setting `CreditMode = Auto | PerPlayer | Shared`, where Auto resolves
  to Shared if there is one team and PerPlayer otherwise.
- Players can also **donate**, **loan** (amount, optional interest and due date,
  repayment) and **trade credits for wares or ships** through **server-escrowed
  offers**, possibly across teams. All of this is gated by session settings.

### 12.1 Accounts (server-authoritative ledger)

The **server** owns the ledger: SQLite, append-only journal, see
server-design. Account kinds:

| Account | Exists when | Spent by |
|---|---|---|
| `wallet:<player>` | PerPlayer mode (always kept, even in Shared, for transfers and loans) | that player's local purchases |
| `pool:<team>` | Shared mode (the spendable wallet of every member), or PerPlayer with `team_pool=true` | Shared: every member's purchases. PerPlayer: deposits, and withdrawals per `pool_withdraw = all|leaders` |
| `faction:<team>` | always (mirror of the `x4mp_team_k` account on the authority) | team NPC assets on the authority (station managers, traders) |
| `escrow:<offerId>` | while an offer or settlement is open | nobody; released or refunded only |

**Spendable balance of a human** is `wallet:<p>` in PerPlayer mode and
`pool:<team>` in Shared mode. The node keeps **local `player` money equal to the
spendable balance** of its human.

`faction:<team>` is fed by the authority. Team-owned NPC assets on the authority
earn and spend into the `x4mp_team_k` account (symmetric mode, 11.2). The
authority reports that account's deltas as `CreditDelta{team_id=k, source=StationIncome}`,
which the server books to the team's pool (PerPlayer) or shared wallet (Shared)
(ADR-019). `faction:<team>` is therefore a game-side account, not a ledger wallet.

Units: X4 money is int64. **[VERIFY]** whether `GetPlayerMoney()` returns
credits or cents, and whether MD `money` values are in cents. Wire amounts are
always **int64 whole credits** (ADR-019), and the node converts at the boundary,
keeping any sub-credit remainder locally.

### 12.2 Keeping local money in sync (reconciliation loop)

Reading: `x4mp_bridge.lua` polls the Lua global `GetPlayerMoney()` every frame.
It is cheap and runs on the UI thread. On change it sends `x4mp.money {value}`
to native. MD `event_player_money_updated` is used as a wake-up and
reason hint.

Writing: the export `AddPlayerMoney(int64)` adjusts by a delta (negative deltas
**[VERIFY]**; fallback MD `transfer_money` / `reward_player money=` through the
actions shim).

Algorithm (native, main thread):

```
state: serverBalance (last WalletUpdate), ackSeq,
       pending = [(seq, delta, reason)]         // reported, not yet acked
       expectedAdjust = 0                        // our own AddPlayerMoney not yet observed

on local money change (old → new):
    d = new - old
    if expectedAdjust != 0 and d == expectedAdjust:   // our own write echoing back
        expectedAdjust = 0; return
    seq++; pending.push(seq, d, reasonHint())          // reason: trade/ship/build/repair/crew/fee/unknown
    send CreditDelta{account=spendable, seq, d, reason, ref}

on WalletUpdate{account, balance, ackSeq}:
    serverBalance = balance; drop pending with seq <= ackSeq
    target = serverBalance + sum(pending.delta)
    local = GetPlayerMoney()
    if local != target:
        expectedAdjust = target - local
        AddPlayerMoney(expectedAdjust)                 // converge
```

- **Deltas are commutative**, so the server applies them in arrival order,
  with no lost updates. Each `(node, seq)` is applied once, which makes resends
  after a reconnect safe.
- **Reasons** come from events seen in the same frame:
  `event_player_trade_completed` (ware, amount, station NetId), a ship purchase
  or build completion, repair, crew. A reason is for history and the admin GUI
  only. Correctness depends on the amount alone.
- **Trades also move money in the NPC world.** For `reason=trade` with an NPC
  station, the authority applies the counter-flow to the station owner's faction
  with MD `transfer_money`, so the NPC economy does not leak (5.3).
- **On the authority**, the authority human is handled the same way: their
  `player` money equals their spendable balance. Team asset income goes to the
  `x4mp_team_k` account, not to `player` (symmetric mode).

### 12.3 Shared wallet: simultaneous spending

With one pool and several spenders, two members can spend the same money within
one round-trip. The local purchase UI checks **local** money, which is stale by
up to one RTT.

- The server is authoritative and accepts both deltas. Both purchases already
  happened in-game, and undoing a local purchase is not possible.
- The pool **may go negative**. The new balance is broadcast to every member
  immediately, with a target of under 100 ms. Every member's local money then
  converges to the (negative) pool balance. **[VERIFY]** whether X4 tolerates
  negative player money. If it does not, the node clamps local money at 0 and
  the server records `debt:<team>`, which is repaid automatically from the next
  income.
- Mitigation: `shared_spend_guard`. Native shows a HUD warning and a chat line
  when the pool drops below a threshold. Session option
  `shared_large_purchase_lock` (amounts > X) makes the Economy UI request a
  short server lock first, but this only covers purchases made through our UI,
  not vanilla menus.
- The ledger keeps per-member spend history, so teams can see who spent what.

### 12.4 Send credits (donate) and team pool

- `Credits.Transfer{to: player|pool:<team>, amount, memo}`. The server checks:
  the sender's spendable balance ≥ amount, and the session policy
  (`transfers = team_only|allied|any`). It then debits and credits atomically in
  one DB transaction, pushes `WalletUpdate` to both parties, and both nodes
  converge through 12.2.
- Pool withdrawal: `Credits.PoolWithdraw{amount}`, subject to `pool_withdraw`.
- Rejections (insufficient funds, policy, recipient unknown) come back as
  `Credits.Result{ok=false, reason}` and are shown inline. No money moves.

### 12.5 Loans

- `LoanOffer{borrower, principal, repay_total, due_in_s, offer_ttl_s,
  auto_repay_pct}` (ADR-021). The principal is **escrowed at offer**. The recipient
  sees a notification and accepts or declines. On accept, escrow goes to the
  borrower and outstanding = `repay_total`.
- Repayment: `LoanRepay{loan_id, amount}` from the borrower at any time. With
  `auto_repay_pct > 0`, that share of the borrower's positive game income repays the
  loan automatically. Past due the loan is marked **overdue** and shown to both
  parties and the admin. There is no seizure and no forced negative balance.
- `Loan.Forgive{loanId}` by the lender. Loans persist in the server DB across
  sessions.
- Gated by `loans = off|team_only|any` and `max_interest_pct`.

### 12.6 Trade offers (escrowed credits ↔ wares or ships)

**Offer:** `TradeProposal{counterparty, give[TradeItem], want[TradeItem], ttl_s}` with
`TradeCounter`/`TradeAccept` on versions (protocol.md §15.6, ADR-022). v1 has no "open
market" offers. Locks are server-side (no `AssetLock` message); the server rejects
orders and other trades on a locked asset.

- The server validates ownership: credits from the ledger, and assets against
  its cached authority state (owner team, `creatorPlayerId`, cargo). It
  escrows credit items when both parties accept (ADR-022).
  Assets are **locked** server-side while the trade is open; the server refuses
  competing offers and orders for them. Locks are visible in the UI. A locked
  ship can still fly, but cannot be sold or offered again.
- Rules: avatars and ships currently occupied by a player can never be offered.
  Ware delivery needs `deliverTo` to be a container the receiver's team owns.
  Session option `cargo_transfer_proximity` (default: both containers docked at
  the same station, or within 5 km) keeps it plausible. Cross-team offers are
  gated by `cross_team_trade = off|allied|any`.

**Settlement** (two-phase, server-coordinated, idempotent by `offerId`/`opId`):

1. **Reserve.** On accept, the server escrows the acceptor's credits too.
2. **Execute on the authority.** The server sends
   `AssetTransferOrder{trade_id, lines:[OwnerChange(asset, to_team, to_player) | WareMove(asset, dest_asset, ware, amount)]}`
   and answers once with `AssetTransferConfirm{trade_id, ok, failed_line, compensated}`.
   If the server gets no answer it sends `TradeQuery`; the journal below lets the
   authority answer after a restart.
   The authority runs one budgeted job:
   - **Precheck everything before mutating anything:** `IsValidComponent`,
     current owner team equals the expected team, not occupied by a player, cargo
     amounts ≥ qty, destination capacity, proximity rule.
   - **Write a journal entry** (sidecar) listing the ops, in state
     `applying`.
   - **Apply in order:**
     - `ShipOwner`: `RemoveAllOrders2(ship, false, false)`, detach from its
       commander (**[VERIFY]** export or `SetCommander` internal), then
       `SetComponentOwner(ship, localFaction(team toTeam))`.
     - `Cargo`: actions shim `<remove_cargo object=$from ware=$w exact=$n result=$r/>`,
       then `<add_cargo object=$to ware=$w exact=$n result=$r2/>`. The shim
       reports each result back through `raise_lua_event` → native.
   - Report `SettleResult{offerId, ok, applied:[opId…], failedOp?, reason?}`.
3. **Commit or roll back on the server.**
   - **ok:** release the escrows to the counterparties, push `WalletUpdate`
     to everyone affected, and broadcast `OwnerChanged`, `StationStock` and
     `PlayerCargo` as usual. Clients apply ownership with the node-relative
     mapping (11.2). If a client was flying the transferred ship it was
     rejected at precheck, so no client loses its seat.
   - **fail:** the authority has already **compensated** the applied ops in
     reverse order (re-own to the original team; `add_cargo` back to the source,
     or `remove_cargo` from the destination). The server refunds both escrows
     and both parties see "Trade failed: <reason>". If compensation itself fails,
     for example because the source container was destroyed, the journal entry
     goes to `needs_admin`, and the GUI shows it with the exact residual
     difference. Wares are never silently created or destroyed.
4. **Crash safety.** If the authority restarts mid-settlement, it reads the
   journal at `on_universe_ready` and reports its state. The server then either
   completes the remaining ops or compensates, based on observed asset state. If
   the server restarts, open escrows and offers persist in its DB. Offers expire
   with an automatic refund.

**Local money after settlement:** only the ledger changes. Each affected node
receives `WalletUpdate` and converges through 12.2. No node edits money on
its own account for offers, transfers or loans.

### 12.7 Economy UI ("Credits / Economy")

This is a screen in the Multiplayer menu, built with the rendering interface
from 7.2, so it works embedded or standalone. Sections:

- **Header:** "Wallet: 1,234,567 Cr", plus "Team pool: … Cr" (if enabled) and
  the mode badge (Per-player or Shared). Amounts are formatted with the
  vanilla money formatter (`ConvertMoneyString`, **[VERIFY]** name).
- **Send credits:** a recipient dropdown (eligible players and the team pool,
  per policy), an amount edit box (validated: digits, ≤ spendable), a memo, and
  **Send**. A confirmation goes through `displayUserQuestion` (embedded) or our
  own dialog.
- **Team pool** (when enabled): Deposit and Withdraw.
- **Loans:** a table of lender, borrower, principal, outstanding, interest, due
  and status, with row actions **Repay** (borrower), **Forgive** (lender) and
  **Details**. **New loan…** opens a form: recipient, principal, interest %,
  due in N hours, auto-collect checkbox.
- **Trade offers:** tabs **Incoming / Outgoing / Open market**. Each row shows
  give vs want, counterparty and expiry, with **Accept / Decline** (incoming)
  or **Cancel** (outgoing). **New offer…** opens a form with a counterparty
  picker and two columns, Give and Want. Each column has Credits, Wares (pick
  owned container → ware → qty) and Ships (pick from the server-provided list
  of my team's eligible NetIds). Validation errors from the server are shown
  inline.
- **History:** the last 50 ledger entries for my accounts (time, kind,
  counterparty, amount, balance after).
- **Notifications** for incoming offers, loan requests, received transfers, due
  or overdue loans and settlement results:
  - a HUD badge counter (7.5);
  - a transient notification (MD `show_help` / `show_notification` through
    the shim);
  - a system chat line with the short form ("Alice offers 3 Hull Parts for
    40,000 Cr. Open Multiplayer → Economy, or /mp eco").
  Accept and decline also work from the chat line through `/mp accept <id>`
  and `/mp decline <id>`.

### 12.8 Failure handling summary

| Failure | Handling |
|---|---|
| Delta lost (disconnect) | Pending deltas are re-sent on resume with their seq. The server dedupes. |
| Local money diverges (game-side refund, rounding) | The next `WalletUpdate` converges it. A periodic `WalletUpdate` refresh request every 30 s asks for the balance anyway. |
| Shared-pool overdraft | Negative pool, or a local clamp at 0 plus team debt (12.3). |
| Transfer, loan or pool operation rejected | No mutation, and an inline error. |
| Settlement precheck fails | Nothing applied, escrows refunded. |
| Settlement fails midway | Compensating ops, escrows refunded. Residuals go to `needs_admin`. |
| Authority or server crash mid-settlement | Journal replay and reconcile (12.6 step 4). |
| `AddPlayerMoney` negative unsupported | MD `transfer_money` fallback through the shim **[VERIFY]**. |

### 12.9 Needs in-game verification

1. `GetPlayerMoney` units, and the polling cost.
2. `AddPlayerMoney` with negative values, and negative balances.
3. MD `transfer_money` between `player`, `x4mp_team_k` and NPC factions, and
   `faction.money` read and write.
4. That `event_player_money_updated` fires for every change, and what
   parameters it carries for reason attribution.
5. `remove_cargo`/`add_cargo` `exact=` on stations and ships, and their
   `result` values.
6. `SetComponentOwner` on a ship with crew, orders, a commander or
   subordinates: cleanup needs.
7. `ConvertMoneyString` (or the equivalent) for UI formatting.

---

## Appendix A. Pitfall coverage (`docs/requirements.md` PIT-xxx → this document)

| PIT | Topic | Where mitigated |
|---|---|---|
| 001 / 002 | Env vars dropped; no `os.getenv` | 2.6 (config file, `launch.json`, dialog) |
| 003 / 004 / 005 | Protected UI, VC++ redist, Mark-of-the-Web | 1.5, installer (8.2/M6) |
| 006 | Missing `version_db` | 1.5 packaging check, 2.5 fallback, 8.5 self-test |
| 007 | Extension restart on load and `/reloadui` | 2.6 stash plus resume token, 4.2 universe epoch |
| 008 / 041 | Two-pass load; readiness signals lie | 2.4 gates on `on_universe_ready`, session state machine |
| 009 | MD callbacks off-thread | 2.3 `md_ring`, 2.5 rules |
| 010 / 011 | Player-ship removal; spacesuit macro | 6.1 `PlayerGuard`/`SafeRemove`, 4.7 |
| 012 | Model fixed at spawn | 4.7 ship-swap respawn |
| 013 / 014 / 048 | Faction must exist; ghost faction choice; foreign-faction campaign | 4.4 validation, 11 (teams), 11.9 fallback |
| 015 / 016 | Buffer caps; duplicate enumeration | 3.2 |
| 017 / 018 | IDs differ; display names | 3.1 sector macros, 4.2 match keys |
| 019 / 020 | Position-read floods; stale IDs | 3.3 guards, 2.7 rate limiting |
| 021 / 022 / 027 / 028 | Blocking send; WSAEWOULDBLOCK; leaked connections | 2.2–2.3 net thread, single socket owner, Windows connect test (8.2 CI) |
| 024 / 025 / 026 | Framing; role gate; field shift | 2.2 role-typed router, generated codec (protocol.md) |
| 029 / 036 | Empty snapshot prune; grace | 3.2 sector-indexed flag, 3.5 manifests, 4.8 |
| 030 / 031 | Divergence mis-binding; one-sector churn | 4.1 ghost-only, 3.1 region lookahead, 4.3 |
| 032 | ID map or VRAM exhaustion | 4.3 spawn rate, interest-only ghosts |
| 033 | Binding leaks | 4.4 single `GhostRegistry` |
| 034 / 035 | Saves polluted; helper satellite | 3.6 no satellites, 6.2, 11.8 |
| 037 | Station duplicates | 5.4 match-then-adopt |
| 038 | Authority FPS | 2.4 budgets, 3.6 attention policy, metrics |
| 039 | Pause as thin client | 4.6 (no global pause) |
| 044 | API gaps (credits, cargo, orders) | 5.3 MD cargo, 12 wallets, 11.6 orders |
| 045 | Per-build RVAs | 1.5 pinned build, fail loudly |
| 047 | Inert blocks boarding | 5.5 exemption |
| 049 | Logging | 2.7 |
| 050 / 052 | Package drift; hardcoded paths | 8.2 single pipeline, `GetSaveFolderPath`, known folders |
| 051 / 053 | One instance per PC; untested client | 8.4 replay and mirror modes; M3/M4 two-machine acceptance |
| 054 | Mod or DLC mismatch | 6.4 handshake |
| 055 | Firewall | nodes are outbound-only (server listens) |
| 057 | Fragile menu injection | 7.2 adapter, probes, standalone fallback |
