# X4 Multiplayer — New Project Plan

## Status (2026-10-01)

**Design phase: complete.** The five design docs were consolidated into one design.
Next step: M0 (repo skeleton, CI, protocol codegen) and, in parallel with M1, the M2-spike
in-game experiments.

| Doc | Purpose |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | **Authoritative** overview; wins over every other doc |
| [`docs/decisions.md`](docs/decisions.md) | ADR log, open questions for the user (with defaults), in-game verification list |
| [`docs/roadmap.md`](docs/roadmap.md) | Milestones M0–M6, M0/M1 task list, M2-spike experiments |
| [`docs/protocol.md`](docs/protocol.md) + `protocol/schema/*.fbs` | Wire protocol |
| [`docs/server-design.md`](docs/server-design.md) | Server, admin GUI, FakeNode |
| [`docs/mod-design.md`](docs/mod-design.md) | X4 mod (native, Lua, MD) |
| [`docs/x4-api-notes.md`](docs/x4-api-notes.md) | X4 9.00 / X4Native API facts |
| [`docs/requirements.md`](docs/requirements.md) | Reference audit: REQ/PIT catalogue |

Key consolidation outcomes: FlatBuffers everywhere except the Replication codec; ports
TCP 47780 / UDP 47781 / HTTP 47790; pinned X4 9.00 build 611726 + X4Native
v9.0.0-611726; symmetric team factions `x4mp_team_1..8`; persistent per-player avatars;
in-band save transfer; checkpoint manifest binding for static objects.
Section 2 and 3 below are the original draft and are superseded by the docs above.

Reference: `reference/` (clone of mercurynova/X4Multiplayer_Mod). Read-only. We use it
for what it *learned*, not for its code.

## 1. What the reference actually is

- **No source for the important parts.** The repo ships only compiled `.so`/`.dll`
  binaries for `x4native`, `x4mp`, `x4mp_stream`. The C++ lived in an unpublished
  local tree. What we can read: Lua menu glue, launcher scripts, docs
  (`README.md`, `STATE.md`, `from_LLM_to_LLM.md`, `FEATURES.csv`), and
  `x4mp_windows/tools/fake_client.py` (which shows the wire protocol).
- **No LICENSE file**, so we should not copy its code anyway. A clean rewrite is the
  right call legally as well as technically.
- **Architecture:** one X4 instance is the "host" and listens on TCP 7778; clients
  connect directly. No separate server, no GUI — config is env vars + a bash/bat
  launcher. Save files are moved with `scp`.
- **Protocol:** newline-delimited text (`JOIN`, `WELCOME`, `PLAYER`, `FULL`, `OBJ`,
  `STA`, `KILL`, `CARGO`, `CAPTURE`, `TRADE`, `PING/PONG`). Full state every tick, no
  deltas, no versioning, no auth.
- **Sync model:** everyone loads the same save. Host simulates the whole universe and
  streams every ship in each client's sector. Clients either bind streamed ships to
  their local copies and pin positions, or hide local ships and draw "ghosts".
- **Game bridge:** built on **X4Native** (eg3r's C++ extension loader, open source).
  X4's Lua has no sockets and no `os.getenv`, so a native DLL is required for
  networking.

### Lessons worth keeping (from their notes)
- X4's built-in SLNet multiplayer needs Steam/EgoNet; custom netcode is required.
- MD event callbacks can run on worker threads: only copy data there, call game APIs
  on the main thread (frame update).
- Never `RemoveComponent` the player's own ship (instant game over).
- Sending must be non-blocking (a blocking send dropped them to 5 FPS).
- Streaming one sector at a time causes divergence and flicker at sector entry. Plan
  for region streaming / interest management from day one.
- Host autosaves can bake ghost objects into the save. Ghosts must be tagged and
  cleaned before save.
- Windows: X4 relaunches through Steam and drops env vars. Pass config through a
  file, not environment variables.
- Known API gaps: no credit read/write, single-unit `AddTradeWare`, targeted orders
  need internal-function signatures.

## Decisions (locked)
- **GUI:** web dashboard served by the server (browser, LAN-accessible).
- **Server stack:** C# / .NET 10 (switched from .NET 8 on 2026-10-01; ASP.NET Core + SignalR for live GUI, SQLite).
- **Platform:** Windows first for the mod; keep the native code portable.
- **Topology:** server-centric relay; every X4 instance (authority included) is a
  client of the standalone server.
- **Teams/factions (added 2026-10-01):** per session, players can share one faction
  (co-op team) or be on different factions. Inter-team relations (allied / neutral /
  hostile) are set as a session setting from the start, configurable in the GUI.
- **Credits:** per-player wallets by default, with transfers to teammates plus an
  optional team pool. If the session has only one team, all players share a single
  wallet automatically. Admin override: CreditMode = Auto | PerPlayer | Shared.
- **Player economy:** donate credits, loan credits (interest / due date, ledger), and
  trade credits for wares or ships (server-escrowed, atomic, with rollback). Each is
  gated per session: Off / Teammates / Allied teams / Anyone.

## 2. Proposed architecture (new)

```
                 ┌───────────────────────────────────────────┐
                 │            X4MP SERVER (new)              │
                 │  standalone process, no copy of X4 needed │
                 │  • session/lobby, auth, player registry   │
                 │  • relay + interest management (fan-out)  │
                 │  • save-file distribution (HTTP)          │
                 │  • event log, metrics, persistence        │
                 │  • ADMIN GUI (players, map, logs, config) │
                 └───────▲───────────────▲───────────────▲───┘
                         │ TCP/UDP       │               │
            ┌────────────┴──┐   ┌────────┴──────┐  ┌─────┴─────────┐
            │ X4 "Authority"│   │ X4 Client     │  │ X4 Client     │
            │ (simulates    │   │ (renders,     │  │               │
            │  universe)    │   │  sends input) │  │               │
            └───────────────┘   └───────────────┘  └───────────────┘
              each = X4 + our mod (C++ ext on X4Native + Lua UI)
```

X4 can't run headless, so one game instance still has to simulate the universe.
The difference from the reference is that **every game instance, including the
authority, is a client of the standalone server.** The server owns the session,
the GUI, routing, saves, and admin. The authority can run on the same PC as the
server. This also lets us hand authority to another player later.

### Components
1. **`server/`**: standalone server with GUI
   - Network core: accepts game nodes, handshake/auth, versioned binary protocol,
     heartbeat, reconnects.
   - Session manager: one session = save + authority + clients + settings.
   - Relay with interest management: route the authority's world snapshots to each
     client based on sector/region subscription, with delta compression.
   - Save service: authority uploads its save, clients download it automatically
     with a checksum (replaces scp/SSH).
   - Admin API (REST + WebSocket push) and **GUI**: dashboard (players, ping,
     bandwidth, FPS reported by nodes), live galaxy/sector map, chat/broadcast,
     kick/ban, session settings, save management, log viewer.
   - Persistence (SQLite): players, bans, session history, config.
2. **`mod/`**: X4 extension
   - `native/`: C++ DLL on X4Native. Net client, state capture (authority),
     state apply (client: ghosts/binding/interp), event hooks (kill, trade,
     build, capture), main-thread job queue.
   - `ui/`: Lua main-menu entries plus a **Join dialog** (address/name/password
     typed in game; the reference hardcoded the IP), in-game status HUD, chat.
   - `md/`: Mission Director cues for game events.
3. **`protocol/`**: single schema (e.g. FlatBuffers or MessagePack + IDL) generating
   C++ and server-language bindings. Versioned, with capability flags.
4. **`tools/`**: fake-client / fake-authority simulators (let us build and test the
   server and GUI without running X4), installer, launcher, log collector.
5. **`docs/`**: architecture, protocol spec, X4 API notes, testing guide.

## 3. Milestones
| # | Milestone | Testable without X4? |
|---|---|---|
| M0 | Repo skeleton, build system, CI, protocol schema v0 | yes |
| M1 | Server: handshake, sessions, relay, admin API + GUI, with fake nodes | **yes** |
| M2 | Mod: loads via X4Native, connects to server, join dialog, heartbeat | needs X4 |
| M3 | Player positions + ghost ships both ways | needs X4 |
| M4 | Authority world streaming, interest mgmt, interpolation, ghost-only render mode | needs X4 |
| M5 | Events: kills, station builds, cargo/trade, captures, chat | needs X4 |
| M6 | Save distribution, installer, launcher, packaging | partly |

## 4. Design subagents (next step, run in parallel)
1. **X4 platform research**: X4Native upstream API (events, function table, MD hooks,
   Windows build), the exported game function list, what we can read and write.
   Output: `docs/x4-api-notes.md`.
2. **Protocol design**: messages, framing, transport (TCP + optional UDP), deltas,
   versioning, auth. Output: `docs/protocol.md` + schema draft.
3. **Server + GUI design**: module layout, threading, relay/interest management,
   admin API, GUI screens and wireframes. Output: `docs/server-design.md`.
4. **Mod design**: C++ module layout, main-thread queue, capture/apply pipelines,
   ghost lifecycle and save hygiene, Lua UI. Output: `docs/mod-design.md`.
5. **Reference audit**: mine every reference doc and script for behaviors, edge cases
   and bugs, and produce a requirements and pitfalls checklist. Output:
   `docs/requirements.md`.

Then a final consolidation pass that reconciles these into one design and an M0/M1
task list.
