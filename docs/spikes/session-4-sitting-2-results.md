# In-game session 4, sitting 2: real X4 = authority, FakeNode wingmen (2026-10-04)

Script: [in-game-session-4.md](../in-game-session-4.md) Sitting 2. One PC, X4 9.00 build 611726, session save `save_004`, kit `start-fake-clients.ps1`.
Logs (local, git-ignored): `out/session4/logs-s2-*`, `logs-s2b-*`; recordings in `Video Recordings/Local Machine/` (timestamp names). Times are UTC unless noted (local = UTC-4).

## Results

| Step | Verdict | Evidence |
|---|---|---|
| 2.1 host + self-spawn (criteria 4, 5) | PASS | first checkpoint right after the load; X4 seated the player during that save (18:42:30), so "stand 30 s" could not be done; `MD now reports the player's ship (ship_bor_s_fighter_01_a_macro)`; 3 avatars spawned 1 s later, **clear of stations** (MD `get_safe_pos`, radius 150 m), **early-game loadout** (`MD dress ... (loadout:basic)`, confirmed in game), Wing02 under `x4mp_team_2` (teams persist in the server DB) |
| 2.1 map (Finding 4 comparison) | PASS | the host's map shows all explored areas: the knowledge loss is client-side only |
| 2.2 checkpoint with avatars (criteria 4, 8) | PASS | `janitor: checkpoint check: 0 stale ... 3 avatar(s) kept: ghosts_cleaned=true`, `manifest lists 3 avatar(s)`; `savescan -Role Checkpoint -Manifest`: 3 expected avatars, 0 unexpected, result OK: **SaveScan reads a real checkpoint correctly** |
| 2.2 player leaves -> avatar parks | PASS after fix | `player 5 left: avatar net_id=4 stays parked`; first run oscillated (Finding 10), after M3-20 `repairs=0` and the kicked / parked ships stay still (confirmed in game) |
| 2.3 authority reload (criterion 4) | PASS | `3 avatar record(s) restored`, `rebound player 2/3/5 ... (idcode ...)`, no second spawn, ships where they were saved. First attempt hung (Finding 11) and needed GUI Stop/Start; after M3-21 a quit + re-host (96 s) loaded the current checkpoint directly and the session went Running |
| 2.4 perf, 7 avatars (criterion 11) | partial | FPS **120 -> 85** (connected with 7 driven avatars vs disconnected); mod main-thread p95 median 0.31 ms; rx 12 kB/s, tx 3.9 kB/s; 0 repairs |
| 2.5 V21b (criterion 8 / Q9) | PASS with note | a checkpoint copied to `save_012` **loads without the mod, no warning**; the avatar ships stay in the universe as `[MP] Wing0N`, owner **"???"** (team factions undefined without the mod) |

## Findings (numbering continues sitting 1)

| # | Finding | Status |
|---|---|---|
| 10 | A parked avatar oscillated: the last MD velocity hint was never cleared, the drive snapped it back ~1/s | **fixed M3-20**, confirmed in game |
| 11 | An authority that quits and hosts again (fresh rejoin, AuthorityLost -> AuthorityLoading) was never sent a save to load: stuck at "checking the session save" | **fixed M3-21**, confirmed in game |
| 12 | The authority's avatar records are one global file: a new session from a plain save re-spawned all 7 avatars of the previous session at their old poses (bots got those far-away ships back) | fix running (M3-22: records scoped to the session lineage) |
| 13 | `AuthorityGraceSeconds` 120 s (ADR-026) ends the session before a real X4 restart is back (2+ min); an Ended session cannot be resumed | open: raise the default (e.g. 900 s), allow restarting an Ended session from its last checkpoint |
| 14 | A GUI setting change (authority grace 900) was not there after a server restart (overrides are meant to persist in SQLite) | open: check whether the first change was saved; if so, a bug |
| - | Kit: `start-fake-clients.ps1` failed with 409 when the previous session was still live in the persisted DB | fixed (upload-save stops a leftover session first) |
| - | Kit: the 2.2 manifest is not next to the save; it is `out\session4\data\saves\<sha256>.x4mf` | doc fixed |
| - | Kit: `sync-report.ps1` judges the whole mod log (all runs); it should judge one run | open |
| - | Design (user): wingman bots copy the leader's heading; they should face their direction of travel (real players' ghosts already show the real ship rotation) | open: FakeWingman change |
| - | The starter macro `ship_arg_s_fighter_01_a_macro` shows in game as **Nova Vanguard**; docs/research/starter-ships.md calls it the Argon Elite | open: check the macro <-> name mapping |
| - | A 2.5 checkpoint opened without the mod keeps the `[MP]` avatars with owner "???" | open: decide (document, or strip avatars from exported copies) |
