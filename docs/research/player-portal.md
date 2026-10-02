# Player portal (ADR-050)

Status: **requirement accepted, parked post-M5** (user, 2026-10-02). Design sketch only; refine
before the first task is written.

## 1. What the user asked for

A tab on the server's web GUI that **players** (not admins) sign in to, to:

1. See **trade offers** their team currently knows about and the **stations they have live trade
   info for**.
2. Look up **general knowledge** for planning trades and builds (wares, recipes, modules, ships).
3. See **their own assets**, including **fleets and groups of ships**.
4. **Organize their empire over time:** define **naming conventions** and keep **notes** about the
   empire, to pick up where they left off in later play sessions.

User decisions (2026-10-02):
- **Visibility: strict by default** (only what the team could see in game), with an admin setting.
- **Access: LAN/VPN** like the rest of the GUI (no public exposure needed).
- **Timing: parked until after M5** (needs the in-game economy and per-team progression).

## 2. Sections

### 2.1 Knowledge (static)
- Wares (price range, volume, transport class), production recipes, modules (inputs, workforce,
  outputs), ships (class, cargo, role), cross-linked. A production-chain view ("what does N× this
  module need per hour, transitively").
- **Source:** the host's own game install at runtime (the authority, or the server reading a local
  X4 install if configured). **X4MP never ships Egosoft game data.** Cached per game build
  (`GalaxyMetadata`-style, keyed by build + enabled DLC/extension hash, so mods/DLC are reflected).
- Team-specific overlay: the team's **blueprints, research and licences** (ADR-048 TeamProgression)
  mark what the team can actually build.

### 2.2 Market (trade offers)
- The authority sends periodic **market snapshots** (station → buy/sell offers: ware, price,
  amount) on the Bulk lane, round-robin over stations so the cost stays flat (≈ 950 stations).
  Exact cadence and API (Lua trade queries) to verify in a spike.
- The server keeps the latest offer per station/ware plus a short history (for trends).
- **Visibility filter per team** (setting `PlayerPortal.MarketVisibility`):
  - `Strict` (default): a station's offers are visible to team T only while T has coverage
    (a ship, satellite or station in range, or docked) — mirroring X4's own rule — and otherwise
    show the **last seen** values with their age, like the in-game map. Exact coverage rule to be
    confirmed against X4 behaviour in a spike.
  - `KnownStations`: any station the team has discovered, always live.
  - `Everything`: the whole market (co-op / casual).
- Because the engine only computes trade knowledge for the `player` faction, the **server**
  computes per-team coverage from the world mirror (team-owned assets + positions). Ties into fog
  of war (ADR-038): one shared per-team visibility service.
- Later (P3): a trade-route finder that only uses offers visible to the team.

### 2.3 My empire (assets, fleets, organization)
- **Assets:** the player's/team's ships and stations (owner per ADR-016/M1-T4 mirror), location,
  hull, cargo, current order.
- **Fleets and groups:** X4's commander → subordinate hierarchy (and subordinate groups/wings).
  The authority must report commander relationships (field on `EntitySpawn/EntityChange` or a
  separate message; schema change). Shown as a tree per fleet, filterable by sector/role.
- **Naming conventions:** the player/team defines patterns (e.g. `{sector}-{role}-{n}`), sees
  which assets don't match, and can **apply a suggested rename** — sent as the normal
  `AssetRename` intent, so the M1-T4 permission policy applies (own/team assets only).
- **Notes:** free-text notes on the empire (a team journal), on individual assets, stations and
  sectors, and on fleets. Notes are **team-shared or private** (choice per note). Stored in the
  server DB and **kept across sessions and save reloads**, keyed by stable identifiers
  (the X4 id code like `ABC-123` for ships/stations, sector macro for sectors), not by `net_id`,
  which changes between sessions.

## 3. Access and security
- **Separate player area** (`/portal/*`), never the admin GUI. A player only ever sees their own
  team's data (enforced server-side, not just hidden in the UI).
- **No passwords.** Sign-in by a **one-time link or code** issued in game (e.g. from the X4MP menu:
  "Open player portal"), bound to the player's identity (`player_key`), short-lived, single use; the
  resulting cookie session is revocable by the admin and expires.
- Same binding as the admin GUI (private-network allow-list, LAN/VPN). Admin can disable the
  portal entirely (`PlayerPortal.Enabled`).
- Read-mostly. The only write paths are notes, naming conventions, and renames that go through
  the existing permission-checked intents.

## 4. Phases (all post-M5)
- **P1:** sign-in, knowledge (static data + team blueprints/research/licences), my assets, fleets,
  notes, naming conventions. Mostly server + web; the mod needs commander relationships and the
  sign-in link.
- **P2:** market snapshots + per-team visibility (`Strict` / `KnownStations` / `Everything`).
- **P3:** planners: production-chain calculator, trade-route finder, station build planner.
  - **Trade-route finder reference (user idea 2026-10-02):** the TaterTrader mod's "DeadTater" auto-trade order
    (Nexus mod 2246, a 9.x fork; queue-aware per-faction round-robin so traders don't pile onto one station,
    fleet-aware so subordinates inherit the commander's order). Use it as a **reference for the scoring and
    distribution logic only** (study, then write our own; check the mod's permissions first) and show the
    result as a dashboard: best routes per ship/fleet from the team's visible offers, with the reasoning
    (profit/jump, stock vs. demand, competing traders). Separately, players can simply run that mod in game
    if the session's mod policy allows it; AI orders run on the authority, so it would need to be installed
    there (an `AuthorityOnlyMD`-class mod, ADR-044 phase 3).

## 5. Dependencies and open questions
- Depends on: M5 (economy in game, ownership, `AssetRename` through the authority), ADR-048
  (TeamProgression), ADR-038 (shared per-team visibility service), M4 world streaming.
- Spike items (add to the in-game verification list when scheduled): Lua/MD API for station trade
  offers and its cost; X4's exact trade-visibility rule (coverage radius, satellites, docked);
  reading commander/subordinate groups; opening a URL from the game UI (same as V29).
- Static data source when the server runs on a machine without X4 (authority uploads a compact
  extract per build) — decide in P1.
