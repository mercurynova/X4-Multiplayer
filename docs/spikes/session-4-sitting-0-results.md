# In-game session 4, sitting 0: spike results S13 (2026-10-03)

Script: [in-game-session-4.md](../in-game-session-4.md) Sitting 0. Kit: `x4mp_probe` (M3-001) + `x4mp_spike` (M3-002), product mod parked.
X4 9.00 build 611726, all DLCs, UIX and SirNukes enabled, scratch slot `save_007` (copy of the M2 test save). Log zip (local, git-ignored):
`out/session4/logs-s0-*.zip`; galaxy dump `out/session4/galaxy-dump.json` (152 sectors).

| Spike | Verdict | Evidence / numbers | Decision for wave 2 |
|---|---|---|---|
| S13.5 seat | PASS | `GetPlayerOccupiedShipID` 0 -> ship at sit-down (16.5 s), ship -> 0 at stand-up (31.6 s); while standing `GetPlayerContainerID` / `GetPlayerObjectID` = the ship; walking out of the cockpit changes nothing | M3-09: the own ship is known from the container while standing; self-spawn on the sit-down edge (no 20 s retry window) |
| S13.6 takeover (standing) | PASS | `CanTeleportPlayerTo` = `"granted"`; `TeleportPlayerTo(force)` seated after 11 ms, guard 10 frames after 90 ms; vacated original `SafeRemove`d, no Game Over; flyable in ~3 s | M3-12 path as planned |
| S13.6 takeover (docked) | PASS with a finding | seated 107 ms, guard 623 ms, original removed; **the new ship spawned 300 m "ahead" inside the station** (clipping until the player flew out) | spawn positions need a clearance check (MD `get_safe_pos`, or the station's undock point when docked): M3-11 / M3-12 |
| S13.7 teams | PASS | team 1 allied (+0.75) shows in its colour (orange), team 2 hostile (-1) shows **red** (hostility overrides the faction colour); names with spaces; `player` not locked; min hull held at exactly 50 % under fire; nothing attacked the player | M3-08 as planned |
| S13.1 ghost spawn | PASS | native `SpawnObjectAtPos2` + `ActivateObject(false)`: no orders, drift 0.000 m in 60 s; MD dress: name, owner `x4mp_team_2`, radar visible, min hull | M3-10 as planned. `SpawnObjectAtPos2` default equipment is high-end (user note): avatars need a ship macro + early-game loadout |
| S13.2 motion | **mode c** | ratings: a (per-frame set, interpolated) 4 "slight jitter, fine"; b (20 Hz raw) 2.5 "jumpy, bad experience"; **c (a + MD velocity at 5 Hz) 4.5 "very good, good experience even in the F3 camera"**. Without the velocity hint the target info shows **0 m/s** on straight flight; with c it shows the right speed. Set cost tens of us | M3-10: per-frame interpolated set + velocity hint 5 Hz (mode c) |
| S13.2 collision | noted | at 11 m/s into a parked ghost: both bounce back a little, no hull damage, player stopped | M3-10: a driven ghost re-asserts its path each frame; a pushed parked ghost must be snapped back |
| S13.3 cross-sector | PASS (context) / FAIL (position) | `SetObjectSectorPos` into another sector: the context is the new sector after 1 frame; **positions were wrong** (placed ~20 000 km off, return 40 942 km off): the probe's target-position math, not the game | M3-10: sector-local positions only (S13.4 shows sector-local values in the tens of km); add a test that a cross-sector move lands within 1 m |
| S13.10 pause | PASS | while paused `on_frame_update` ticks (~110/s), `SetObjectSectorPos` reads back within 2 mm, but the ghost is **not redrawn** until unpause | ghosts look frozen while the local game is paused (cosmetic, accepted) |
| S13.4 sample | PASS | 3 runs (~6 300 samples, ~17-18 Hz); **angles are radians**; positions sector-local; highway context readable (local and super highway); sector valid in every sample incl. superhighways (two sector changes); docked and on-foot detected; gate jump = ~3.5 s frame gap; pose read 1-2 us | M3-09: own-ship sampler as planned; Hidden flag for highway/docked/on-foot |
| S13.9 SETA | not tested | the user has no SETA item | **User decision: SETA unavailable in multiplayer, always** (block at the source, switch-off as safety net); test later |
| S13.8 persistence | PASS | team ship saved and reloaded: same position (0.000 m), name, sector, still inert; **component ids change on load** (136136571 -> 169452728), found again by **idcode**; the MD table keyed by component survives the load | M3-11 / M3-13: bind avatars and ghosts by idcode (or the MD table) after every load, never by stored ids |
| S13.11 galaxy dump | PASS | 152 sectors written/logged | FakeNode `--galaxy-file` for sittings 1-2 |
| S13.12 chat | PASS | injected lines shown in the vanilla chat window outside Ventures, custom author names and colours (Alice orange; Bob default = the player's colour); typed lines captured; `/x4mpspike list` worked | M3-06 approach confirmed |
| cleanup | finding | removed only the ship `persist_check` had re-bound; the two ghosts survived because cleanup used pre-load ids | same rule as S13.8: find our objects by idcode / owner / name after a load (janitor, M3-13) |

Kit lessons: the probe defaults `pause_on_ready=1` and `connect=1` were still active (fixed live by editing `x4mp_probe.json`; `write-probe-config.ps1`
must write both false); native blocks show no on-screen message (only Lua blocks do); the scratch-slot check reads `GetLastSaveInfo`, i.e. the last
save **written** (an autosave defeats it: save to the scratch slot right before a destructive block).
