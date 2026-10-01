# In-game spike session 1 (Lua + MD only, no native DLL)

Goal: settle the riskiest open questions ([decisions.md](../decisions.md) Part 3) before
we build on them. Claude can't play X4, so the user runs this and sends back logs.
Expected time: **60–90 minutes**.

## What's being tested

| Step | Spike | Verifies | Why it matters |
|---|---|---|---|
| 1 | S7 | V09, V10: list every sector with macros + gate graph; cost of a full ship enumeration | Server map + interest manager need the sector graph |
| 2 | S4 | V04: money API units, negative adds, negative balance, money-changed event, MD `transfer_money` | Whole economy design |
| 3 | S1 | V01, V08: mod factions `x4mp_team_1..8` exist in a save made **before** the extension was installed; activate; spawn a ship for a team faction; set relations; targeting colours | Whole teams design (top risk) |
| 4 | S8 | V13, V14: bulk `add_cargo`/`remove_cargo`; `SetComponentOwner` on a ship; Lua→MD round-trip latency | Trades, cargo, captures |
| 5 | S2 | V02: spawn a ship and move the player into it with `TeleportPlayerTo`; safely remove the old ship | Player avatars |
| 6 | S3 | V03, V16, V17: spawn 100/250/500 inert ships, move them every frame, measure frame time; destroy methods; velocity readable? | Ghost rendering budget |
| 7 | S9 | ADR-037: read plot/unlock state; try to apply a sector/gate unlock through MD | Shared story/unlocks |
| 8 | misc | V12 (`$x4mp_netid` MD variable survives save/load), V20 (`debug.getupvalue` works for menu injection) | Entity matching, main-menu entry |

Not in this session: S5 (connection survives save load) and S6 (save blocking) need the
native DLL and come in session 2.

## Before you start (one-time, ~10 min)

1. **Back up your saves folder**:
   `...\Documents\Egosoft\X4\<steam-id>\save\` (on this PC it's under OneDrive). Pause
   OneDrive sync for the session.
2. **Make a test save copy** from your normal game: load any save you're fine
   experimenting with, save to a **new slot**, and quit. Note its file name (e.g. `save_007`).
   It must have been created **without** the spike extension. That's the point of S1.
3. **Steam launch options** for X4 (Library → X4 → Properties → Launch options):
   `-debug all -logfile x4mp_spike.log`
   The log is written to `...\Documents\Egosoft\X4\<steam-id>\x4mp_spike.log`.
4. Settings → Extensions: **Protected UI mode OFF** (restart X4 if it asks).
5. Install the spike extension: run `mod\spikes\install-spike.ps1` (copies
   `mod\spikes\x4mp_spike\` into `<X4>\extensions\x4mp_spike\`). Built by a Sonnet task;
   ready before your session.

## Running it

1. Start X4 and load the **test save**.
2. The spike runs by itself. On-screen notifications show
   `X4MP spike: step N/8 <name> ...`. Don't pause the game or open big menus while it runs.
   Fly normally, or just sit in space (not docked) somewhere quiet.
3. Steps that need your eyes say so in the notification. For example, at step 3:
   **target the spawned team ship and tell me its colour and whether it's shown as
   enemy/friendly.** Write down what you see.
4. At step 5 (teleport): note whether you end up in control of the new ship, whether the
   HUD/camera look right, and whether anything strange happens (game over, black screen).
5. Step 6 is the heavy one. Watch the FPS counter (Settings → or `-showfps`) at 100/250/500
   ghosts and note it. If the game becomes unplayable, quit. That's a result too.
6. After the final "spike complete" notification:
   - **Save to another new slot** (tests V12 and pollution), quit to the main menu, and
     **load that save again**. The spike logs the persistence check on load.
7. Quit X4.

## What to send back

- `x4mp_spike.log` (or every line containing `[X4MP-SPIKE]`).
- Your notes: step 3 colours/relations, step 5 control/HUD, step 6 FPS at each count,
  anything weird.
- Then **uninstall**: delete `<X4>\extensions\x4mp_spike\` and throw away both test saves
  (they contain spike objects).

## Pass criteria and fallbacks

See [roadmap.md](../roadmap.md) §4 (S1–S9). Claude records each verdict in
`decisions.md` Part 3 and updates any ADR a failed spike changes.
