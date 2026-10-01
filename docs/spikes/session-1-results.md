# Spike session 1 results (2026-10-01)

Run by the user on X4 9.00 with all 7 DLCs, test save `save_008`, extension `x4mp_spike`.
Raw evidence: [session-1-spike-lines.log](session-1-spike-lines.log) (every `[X4MP-SPIKE]` line).
`[=ERROR=]` is just X4's label for Lua `DebugError` output, not an error.

## Verdicts

| Item | Verdict | Evidence / numbers | Design impact |
|---|---|---|---|
| **V01** mod factions in a pre-existing save | **PASS** | `x4mp_team_1..8` resolve, activate, show in `GetAllFactions`, own ships; still active with relations after save+reload | ADR-014 (8 team factions) **confirmed**. No fallback needed |
| **V08** relations + lock | **PASS** | team1 → player +0.8 (user saw orange, allied), team2 −0.5 (cyan, enemy). `set_faction_relation_locked` blocks later MD changes. `set_faction_identity` rename works | Relation matrix via MD confirmed. Lock relations so vanilla scripts can't drift them |
| **V02** move the player into a spawned ship | **PASS (with `force`)** | `TeleportPlayerTo(ship,true,true,true)` works and the user flew it normally. `CanTeleportPlayerTo` returns `"research"` without force and `granted` with force. Old ship is then unoccupied and removable. Teleport back works. Avatar ship persisted through save+reload | ADR-015 avatars **confirmed**. Always use `force=true`. A gameplay research gate exists that we bypass, so document it |
| **V03** ghost cost | **PASS, with a budget** | Frame time 10.6 / 15.4 / 25.2 ms at 100 / 250 / 500 inert ghosts (user: 75 / 62 / 40 FPS). Cost scales the same idle vs moving, so it's **rendering, not `SetObjectSectorPos`**. Drift vs commanded position 0.03–0.4 m | Default `max_ghosts = 250`. Far ghosts can update at a lower rate, but the saving is small because movement is cheap. LOD/visibility is what matters. A µs-resolution timer is needed (native QPC); Lua `GetCurRealTime` showed 0 ms move cost |
| **V16** destroy methods | **INFO** | MD `destroy_object` and `SelfDestructComponent` both leave **wrecks** (still valid, `IsComponentWrecked=true`). `RemoveComponent` (guarded) cleans everything: 850 spawned, 0 leftover | Kills: use explode + wreck for visuals. Ghost despawn: guarded remove |
| **V17** velocity readable | **NO** | `GetComponentData(id,"velocity"/"speed")` returns nil on ghosts | Finite differences (current design) confirmed |
| **V04** money API | **PASS, with caveats** | MD `player.money` is in **cents**; Lua `GetPlayerMoney()` is in **credits**; `AddPlayerMoney(n)` takes **cents**. Overdraw clamps at **0** (no negatives). `event_player_money_updated` fired for all 10 changes. Balance restored exactly | Wire format = whole credits (ADR). Mod converts ×100 at the MD/Lua edge. **Shared-wallet overdraft can't be represented in game**: the server must block it (ADR-019 already has an overdraft flag); the client clamps at 0 and the server holds any debt |
| **V09** sector list + graph | **PASS** | 152 sectors via MD `find_sector`, with owner, known flag, gates (active state) and highways | Server galaxy map + interest graph can come from the authority at session start |
| **V10** enumeration cost | **PASS** | `GetAllFactionShips` over 33 factions = 10,225 ships in under 1 ms (Lua timer). With `includehidden=true` it returns 207,562 entries for 10,262 unique ships (×20 duplicates) | Use `includehidden=false` or dedupe (PIT confirmed). Full index passes are affordable |
| **V13** bulk cargo + ownership | **PASS** | `add_cargo exact=37` → 37; `remove_cargo exact=12` → 12. Removing more than present removes what's there; adding over capacity caps at capacity (1,960). Station single-unit add/remove OK. MD `set_owner` and Lua `SetComponentOwner` both work | Trade/cargo settlement path confirmed. Check the returned amount, which can be less than requested |
| **V14** Lua↔MD round trip | **PASS** | `AddUITriggeredEvent` → MD → `raise_lua_event`: avg 8.5 ms (7.5–9.5), about 1 frame | Fine for events/economy; not for per-frame state (use native) |
| **S9** story/unlock state | **PARTIAL** | Inactive gates enumerable (e.g. Boron-region gates `cluster_48↔604`, `31↔601` inactive in this save); Boron sectors unknown. `set_known` on a sector works and **persists** through save/reload | ADR-037 feasible. Unlock = gate activation + known flags. **Next:** test activating a gate via MD (spike 2) |
| **V12** MD variable on objects persists | **INCONCLUSIVE (spike bug)** | `Failed to set component.$x4mp_netid` (MD error) on both ship and station, so it never got set. Likely wrong syntax/permission for object variables | Keep manifest matching (ADR-010). Retest in spike 2 with the correct object-variable syntax |
| **V20** `debug.getupvalue` for menu injection | **FAIL** | `debug` library is nil in X4's Lua even with Protected UI off | The reference mod's upvalue trick is **unavailable**. Main-menu entry must hook public menu tables (`Menus[...]` functions) or use our own menu opened another way. Update mod-design §7 |
| Save pollution | **PASS** | After save+reload: exactly 1 team-2 ship (the intended S1 ship); no ghosts leaked | Cleanup approach works |
| Extension signatures | expected | "Failed to verify the file signature" warnings for every mod file | Normal for unsigned mods; document it in troubleshooting |

## Follow-ups
1. ADR updates: ADR-014/015 confirmed; V04 money units and the no-negative clamp; `max_ghosts=250`; V20 fallback for the main menu. Done in `decisions.md` alongside this file.
2. Spike session 2 (needs the native DLL from M2 work): S5 reload survival, S6 save control, V07 thread of MD callbacks, plus retests of V12 (object variables) and S9 gate activation.
3. MD `transfer_money` **works both ways between the player and `x4mp_team_1`**: team factions have their own money account (`hasownaccount=1`), and the round trip is exact. That makes **team pools usable as real in-game faction accounts** if we want them. Transfer to an NPC faction (argon) also works.
