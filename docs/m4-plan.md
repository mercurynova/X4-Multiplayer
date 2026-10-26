# M4 plan: one universe, many screens

Status: plan v1, 2026-10-26 (after M3b/M3c code-complete). Owner: lead. Implementers: one agent per task, in a worktree
([execution-plan.md](execution-plan.md) §5, §6.1). In-game tester: the user (two PCs, `docs/in-game-session-6.md` is still
the open M3 run and is a prerequisite for the M4 acceptance sitting).

Read with: [mod-design.md](mod-design.md) §9 "M4", §3.5-3.7, §4.4-4.7, [protocol.md](protocol.md) §8.5, §9.1-9.3, §10.2,
§11, [roadmap.md](roadmap.md) M4 row, [x4-api-notes.md](x4-api-notes.md) §2.3-2.4, [decisions.md](decisions.md) ADR-009,
ADR-011, ADR-023, ADR-036, and [research/on-foot-presence.md](research/on-foot-presence.md) for the container-key lessons
that M4's manifest replaces.

---

## Open questions for the user (answer these first)

The user is away ("продолжай делать без меня"), so every wave below proceeds with the recommended answer. Changing an answer
later costs a wave, not a milestone, unless the row says otherwise.

| # | Question | Recommended answer |
|---|---|---|
| Q1 | **Who enumerates the sector?** Native has no "objects of a sector" call (2051 exported functions checked, `x4_game_func_list.inc`); MD has `find_object class="class.ship" space="$Sector" multiple="true"`. | **MD builds the index, native owns it.** MD pushes compact `id;macro;owner;...` batches over the existing bridge; native dedupes, indexes by sector, schedules rates and reads every pose itself (`GetObjectPositionInSector`). MD never pushes positions at frame rate. |
| Q2 | **Full-galaxy sweep or interest-only?** `GetAllFactionShips` over a mature save is ~84k ships and returns duplicates for hidden factions. | **Interest-only.** MD scans the sectors of the current `CaptureSet` (Sector + Adjacent + Linger, plus the authority's own sector). A full-galaxy sweep exists only as a diagnostic command, never on a timer. |
| Q3 | **How are stations matched to the manifest?** M3 matched containers by a client-computed FNV hash of the sector macro plus a 50 m bucket (`RelayModule.MatchedContainerBit`). | **Manifest ids replace the hash in M4** (ADR-009): the checkpoint manifest is the single source of static net ids; the client matches station (sector, macro, rounded position) to a manifest row at `on_universe_ready` and uses that row's net id. No wire change: the id was always opaque to the relay. |
| Q4 | **What happens to a client's local NPCs in an interest sector?** M3 left them alone, which is why client NPC worlds diverge. | **Suppress them** (sub-item 4): hide/remove non-player ships the authority streams, keep them for sectors outside the CaptureSet, and never touch the player's own ship, the player's subordinates or mission-critical objects. Sweep late spawns on a timer, not every frame. |
| Q5 | **UDP realtime lane.** The roadmap puts it in M4; M3 shipped TCP only. | **Yes, in W6**, with the M3 automatic TCP fallback kept: the lane upgrades when a UDP handshake succeeds within 3 s, and silently stays on TCP otherwise. |
| Q6 | **Record/replay scope.** | **Authority-side record of the emitted stream** (all lanes, timestamped) plus a replay driver that feeds a FakeNode or a second server. Not a client-side demo recorder. |
| Q7 | **Attention policy default.** mod-design sub-item 8 asks for `native` vs `activate` measurement. | **Measure first, default to `native`** (do not touch NPC attention for streamed ships) unless the measurement shows a client-side FPS cost that outweighs the behavioural risk. |
| Q8 | **What counts as "no pop"?** The AC says zero `spawn-near-player` events on a highway transit. | A ghost spawned inside the client camera's 5 km bubble **while the client is moving** counts as a pop. A ghost spawned behind the player or during a loading screen does not. The counter is `[SYNC] spawn-near-player`, already reserved by mod-design. |

---

## 1. Goal, scope and non-goals

**Goal.** A client no longer lives in its own approximation of the universe: in the sectors the server says it is
interested in, it sees **the authority's** ships — the same net ids, the same positions, the same owners — instead of
whatever its own simulation invented. Stations are the same stations on both sides because both sides use the manifest.
The cost is bounded: a busy sector of 200 ships still holds 45 FPS, the authority spends under 3 ms per frame, and a client
pulls under 200 kB/s.

**In scope**

| Area | What M4 delivers |
|---|---|
| Capture | `CaptureSet` parsed and honoured by the authority: tiers, focus spheres, epoch, rates |
| Index | Incremental universe index: sector → net id, dedupe, sized buffers, per-sector completeness |
| Stream | `EntitySpawn` / `WorldUpdate` / `EntityDespawn` / `EntityChange` / `EntityStatusBatch` / `SectorComplete` for NPC ships, dead-reckoned against the last sent sample |
| Statics | Station matching against the checkpoint manifest at `on_universe_ready`; ≥ 99% match in interest sectors |
| Client world | Local NPC suppression in interest sectors, budgeted ghost spawns, leave grace, late-spawn sweep |
| Hygiene | Manifest pruning, frozen ghosts during a disconnect, no despawn/respawn churn of a net id that stays in interest |
| Transport | UDP realtime lane active, TCP fallback intact |
| Tooling | Record/replay of the authority stream, mirror-test mode against a second server |
| Measurement | Attention policy A/B, FPS and bandwidth numbers written into the exit report |

**Non-goals (deliberately later)**

- No NPC AI, combat, orders or economy replication. Streamed ships are **visual and positional**; their local brains stay
  suppressed, and damage/kills remain M5.
- No dynamic station creation or destruction on a client (M5/M6).
- No cross-sector pathfinding or traffic simulation on the client.
- No change to the on-foot pipeline: avatars keep using the M3b/M3c container rule; M4 only removes the client-matched
  container hash in favour of manifest ids (Q3).
- No save-format change: the manifest is a sidecar produced at checkpoint time, as in M3.

---

## 2. Carry-overs from M3

| Item | State | What M4 does with it |
|---|---|---|
| Two-client acceptance run (`docs/in-game-session-6.md`) | **Open, needs a human** | Blocks the M4 exit sitting, not the code. Run it before M4 acceptance so a regression is attributable. |
| `dotnet test X4MP.sln` | Blocked: `X4MP.Server.csproj:62` runs `npm run build`, `server/web` has no `node_modules` | Ask before `npm ci`; until then the .NET suites run per project, as in M3. |
| Container key by client hash | M3c | Replaced by manifest ids in W4; the relay keeps accepting top-bit ids without a mirror lookup until then. |
| `ManifestReport` count-only | M2 | W4 turns it into a real match report with per-sector mismatch keys. |
| Realtime over TCP | M3 (Q4 answered "M3" then slipped) | W6 adds UDP with the fallback. |
| `spawn-near-player` counter | Reserved in mod-design | W5 makes it real and asserts 0 on a highway transit. |

---

## 3. Waves

Each wave is a worktree, a build, the full ctest and Lua suite, and a local commit (no push). Numbers in **AC** are what the
exit report has to quote.

### W1 — `CaptureSet` and the universe index (M4-01..M4-05)

| Task | Work | AC |
|---|---|---|
| M4-01 | `AuthorityFlow` handles `CaptureSet`: epoch, `sectors:[(sector, rate_hz)]`, `focus:[(sector, center, radius_m, rate_hz)]`. Store as a small fixed-capacity table (sector u16 → rate, focus list), reject a stale epoch, log a one-line diff on change. | A `CaptureSet` with 3 sectors + 1 focus is parsed in a unit test; a stale epoch is ignored; a malformed message is counted, not fatal |
| M4-02 | `UniverseIndex`: `net_id → Entry{sector, macro_ref, owner_ref, flags, last_scan_us, last_sent}`, plus `sector → vector<net_id>`. Sized from a high-water mark, never reallocating per frame; insert is idempotent (dedupe by id, as `GetAllFactionShips` requires). | 10 000 inserts with 30% duplicates yield 7 000 entries; a full index pass over 10 000 entries stays under 1 ms in a micro-benchmark test |
| M4-03 | MD sector scan: a new cue pair in `x4mp_galaxy.xml` (or a new `x4mp_capture.xml`) that runs `find_object class="class.ship" space="$Sector" multiple="true"` for one requested sector and pushes chunked `S;<sector>;<net>|<macro>|<owner>;...` + `E;<count>` over the bridge. Requested from native by a new verb `x4mp.capture_scan`. | The cue runs for a sector, native receives a complete batch, and a second scan of the same sector adds no new ids |
| M4-04 | Sector index reuse: the wire sector is the u16 from `galaxy_map.h`; the MD side sends the sector macro id and native resolves it with `index_of_macro`. Unknown macro → the sector is skipped and counted (`no_sector`), never guessed. | A scan of a sector whose macro is not in the map is skipped with a counter; a known macro maps to the same index as the self-ship path |
| M4-05 | Per-sector completeness: `SectorComplete{sector, epoch, count}` is emitted only after a full scan pass of that sector within the epoch, and never for a partial batch. | A test drives scan-partial → scan-complete and sees exactly one `SectorComplete` with the index count |

**AC (wave).** With 3 interest sectors the authority's per-frame mod cost stays ≤ 2 ms at p95 (mod-design sub-item 1),
measured by the existing frame-time counter and logged every 30 s.

### W2 — The stream (M4-06..M4-11)

| Task | Work | AC |
|---|---|---|
| M4-06 | `CaptureScheduler`: per sector, a rate from the CaptureSet (5/1 Hz tiers, 20 Hz inside a focus sphere); an entity is due when its sector is due. Round-robin within a sector so a 200-ship sector spreads over ticks. | 200 ships at 1 Hz emit across ≥ 20 ticks, never one burst; a 5 Hz sector emits 5× as often |
| M4-07 | Dead reckoning: keep `(pos, vel, rot, t)` of the last sent sample, predict forward, and send when the position error exceeds ε(d) (0.5 m within 2 km → 20 m at 50 km), rotation > 1° (0.5° Near), a flag/sector/dock change, or the keepalive (1 s Near, 5 s otherwise, `Keyframe`). Velocity is the finite difference of the last two reads. | A ship flying straight at 300 m/s emits far fewer updates than its poll rate; a ship turning emits on the rotation threshold; a stationary ship emits a keyframe at the keepalive interval |
| M4-08 | `WorldUpdate` batches: extend `step_world_clock` (already 20 Hz with no states) to carry the due entities, with absolute values and the existing mask, ≤ N per message, respecting `MaxFrameBytes`. | A batch of 50 moving ships encodes and decodes byte-exactly through `ReplicationDecoder`; an over-size batch is split, not dropped |
| M4-09 | `EntitySpawn` for every entity of a newly added sector, chunked, then `SectorComplete`. `SpawnKind`/`SpawnOrigin` set from the index; statics come from the manifest (W4). | A client that adds a sector receives every ship of the index exactly once before `SectorComplete` |
| M4-10 | `EntityDespawn` with the right reason: `OutOfInterest` when a sector leaves the CaptureSet (after the 60 s evict grace), `Destroyed`/`Removed` when the index pass no longer finds the ship, killer id when known. | A sector leaving interest despawns after the grace, not at once; a ship that disappears from the index despawns once, not every frame |
| M4-11 | `EntityChange` on owner/team/player/name/parent/macro change; `EntityCargo` unchanged from M3; `EntityStatusBatch` ≤ 1 Hz per entity with the 10 s keepalive. | An owner change emits one `EntityChange`; a hull change of 1/255 emits one status entry |

**AC (wave).** In a synthetic 200-ship sector the stream holds the protocol's bandwidth target (< 200 kB/s per client at the
CaptureSet rates) and never emits the same net id twice in one tick.

### W3 — Client: sector entry and suppression (M4-12..M4-16)

| Task | Work | AC |
|---|---|---|
| M4-12 | `SectorEntered` handling on the client: budget the spawns (≤ K ghosts per frame), queue the rest, and keep the queue bounded by dropping the farthest. | 200 spawns in one message produce ≤ K ghost creations per frame and no frame spike in the host-sim test |
| M4-13 | Local NPC suppression: for each interest sector, hide/remove non-player ships that are not the player's, not subordinates, and not mission objects. Keep a per-sector `suppressed` set so a re-entry does not double-count. | In a host-sim sector with 50 local NPCs and 50 streamed, exactly the streamed ones remain visible |
| M4-14 | Leave grace: on leaving a sector, keep its ghosts for the grace window, then despawn; a re-entry inside the window reuses the same ghosts (no churn). | Leaving and re-entering within 10 s produces zero despawn/respawn of the same net id (the `[FLK]` AC) |
| M4-15 | Late-spawn sweep: a timer (not per frame) removes local NPCs that appeared after the sector was suppressed. | A ship spawned by vanilla 30 s after entry is suppressed within one sweep |
| M4-16 | Frozen ghosts during a disconnect: on link loss, stop updating positions but keep the ghosts, mark them frozen, and unfreeze on reconnect; on session end, remove them. | A 10 s link loss leaves ghosts in place, unmoving, and they resume without a respawn |

**AC (wave).** The client's world in an interest sector contains exactly the authority's ships plus the player's own.

### W4 — Statics and the manifest (M4-17..M4-21)

| Task | Work | AC |
|---|---|---|
| M4-17 | Manifest reader on the client: load the checkpoint manifest sidecar at `on_universe_ready`, index rows by (sector macro, macro, rounded position). | A manifest with 5 000 rows loads in < 50 ms and is queried by key |
| M4-18 | Station matching at `on_universe_ready`: for every station in the interest sectors, match (sector, macro, rounded position) and take the row's net id; an ambiguous match uses the ID code as tie-breaker; a miss is logged with the key. | ≥ 99% of stations in interest sectors match on a real save; every miss is logged with its key |
| M4-19 | Replace the client container hash: `RelayModule.MatchedContainerBit` keeps working, but the id now comes from the manifest row, so the relay needs no lookup change. | The on-foot room flow still works with manifest ids and no wire change |
| M4-20 | Manifest pruning: rows for stations no longer present are dropped at the next checkpoint; a station destroyed between checkpoints is despawned by `EntityDespawn{Removed}`. | A destroyed station leaves no ghost after the next checkpoint |
| M4-21 | `ManifestReport` becomes real: per-sector matched/missed counts and up to N mismatch keys, replacing the M2 count-only body. | The server logs a match report per client; a mismatch key is reproducible from the log alone |

**AC (wave).** ≥ 99% station matches in the interest sectors, and the on-foot flow is unchanged from the player's point of view.

### W5 — Pops, interest and the highway (M4-22..M4-24)

| Task | Work | AC |
|---|---|---|
| M4-22 | Region lookahead: request the destination sector's index and spawns before the client arrives (neighbour prefetch on a highway or gate transit). | On a highway transit the destination sector's ghosts are already present on arrival |
| M4-23 | `[SYNC] spawn-near-player` counter: a spawn inside the client camera's 5 km bubble while the client moves. Logged with sector, net id and distance. | **0** on a highway transit; a non-zero value names the sector and the id |
| M4-24 | Interest hysteresis: a sector at the edge of the radius does not flip in and out; the 60 s evict grace plus a radius dead-band. | A client loitering at the radius boundary produces no spawn/despawn churn over 60 s |

**AC (wave).** Zero `spawn-near-player` pops on the highway transit; no `[FLK]` flicker over the whole run.

### W6 — Transport and tooling (M4-25..M4-28)

| Task | Work | AC |
|---|---|---|
| M4-25 | UDP realtime lane: handshake within 3 s, automatic TCP fallback, per-lane counters. | A session runs `WorldUpdate`/`EntityStatusBatch` over UDP with TCP as fallback, visible in the 30 s line |
| M4-26 | Record: the authority writes every emitted message with a monotonic timestamp to a compact file, rotated by size. | A 10-minute session records and replays without a decode error |
| M4-27 | Replay: a driver feeds a recorded stream into a FakeNode or a second server, at real time or as fast as possible. | Replaying a recording reproduces the same `SectorComplete` counts and net ids |
| M4-28 | Mirror-test mode: a second server instance consumes the authority's stream and its manifest, and reports divergence counts. | Divergence 0 on a 10-minute recording |

**AC (wave).** A recorded session replays byte-identically (same message sequence, same counts).

### W7 — Measurement and exit (M4-29..M4-31)

| Task | Work | AC |
|---|---|---|
| M4-29 | Attention-policy A/B: `native` vs `activate` for streamed ships, measuring client FPS in a 200-ship sector. | Both numbers in the exit report; the default chosen and justified |
| M4-30 | Budget instrumentation: authority mod time per frame (p50/p95/max), bytes per client per second, ghost count, suppression count — all in the 30 s line. | A busy sector reports ≥ 45 FPS, < 3 ms/frame authority, < 200 kB/s per client |
| M4-31 | `docs/m4-exit-report.md` + roadmap status + `in-game-session-7.md` (the two-client M4 run). | The report quotes every AC number with the log line it came from |

**AC (wave).** Every roadmap M4 acceptance number is quoted from a log, not asserted.

---

## 4. Verification per wave

| Gate | Command |
|---|---|
| Build | `powershell -NoProfile -ExecutionPolicy Bypass -File mod\build.ps1 -Config relwithdebinfo -NoTest` |
| Host tests | `mod\build\msvc-x64-relwithdebinfo\x4mp_host_tests.exe "<filter>"` |
| Full ctest | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe --output-on-failure` from `mod\build\msvc-x64-relwithdebinfo` |
| Lua | `python mod\tests\lua\run_lupa.py` |
| .NET | per project, because `X4MP.Server.csproj` runs `npm run build` and `server/web` has no `node_modules` (carry-over) |
| In game | `game_deploy` + `game_reload`, then the X4Mcp endpoint `POST http://127.0.0.1:7779/mcp`; the mod log is `%LOCALAPPDATA%\X4MP\logs\x4mp.log` |

A wave is done when the build is clean, ctest and Lua are green, the new tests cover the AC, and the commit is local.

## 5. Risks

| Risk | Mitigation |
|---|---|
| MD `find_object` on a huge sector is slow | Scan on request only, one sector per request, chunked pushes; measure per-sector scan time in W1 and cap the sector size |
| The bridge becomes the bottleneck | Only ids and static attributes cross it; positions are read natively |
| Suppression removes something the player needs | Allow-list (player ship, subordinates, mission objects) and a sweep rather than a per-frame pass |
| Ghost churn at sector edges | 60 s evict grace + radius dead-band (W5) |
| Client and authority disagree about a station | Manifest match is keyed and logged; a miss is F3-like (no ghost), never a guess |
| UDP lane breaks under VPN/Wi-Fi | 3 s handshake timeout, automatic TCP fallback, per-lane counters |
