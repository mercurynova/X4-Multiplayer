# M2 exit report

Date: 2026-10-03. Criteria: [m2-plan.md](m2-plan.md) section 3. Game evidence: [spikes/session-3-results.md](spikes/session-3-results.md) (in-game session 3,
real X4 9.00 build 611726, all DLCs, UIX and SirNukes enabled). CI: main at 44fe099, GitHub Actions run 37147310226 green (ubuntu + windows).

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Start-menu join, UI only | PASS | session 3 B1 (Run 2 retest, slot 7): save not on disk, 34.8 MB in-band download + SHA-256, loadSave, NodeReady, Phase InGame. As built the mod does **not** pause at universe ready (session-2 finding: pausing fights Esc). CI: hostsim JoinFlow |
| 2 | No leave/join across the save-load reload | PASS | server: `detached: ClientReload` + `resumed`, same player_id, no `left:`. CI: hostsim ReloadSurvival |
| 3 | `/reloadui` resume | PASS | B2: resumed, HUD line and Multiplayer entry back. Follow-up: universe-ready state after the UI reload (open item 2) |
| 4 | Reconnect after a server restart | PASS (after live fix 6) | B4 retest: Connecting -> Connected after the restart, fresh Welcome -> rejoin without a load -> NodeReady; X4 kept running, vanilla HUD stayed. CI: hostsim `join_rejoin` |
| 5 | 30-minute connection | PASS | B5: no disconnect in 30 min; mod_p95 max 0.089 ms (< 0.2); RTT and FPS in the GUI |
| 6 | Build mismatch refused | PASS (CI) | hostsim `refuse_build` / `join_refused_build`; not run in game (needs a debug build override) |
| 7 | Mod mismatch refused, grouped, links | PASS | B7 (strict): Install / Disable groups, Workshop button opens steamcommunity.com, Nexus address box copies; GUI Recent rejections. Follow-up: raw diff text (open item 4) |
| 8 | Real extension list | PASS | B8: GUI Mods list = Settings > Extensions |
| 9 | Authority from an admin-uploaded save | PASS | C2/C3: real X4 hosted from the start menu, checkpoints stored, session Running, 3 FakeNode clients joined after verifying the checkpoint, "Request save now" made a second one |
| 10 | Upload job bound to its connection | PASS (CI) | e2e step UploadKill on every CI run |
| 11 | `EntitySpawn.game_time` filled | PASS | C4: server 383.332 = mod 383.332. Follow-up: the session-start checkpoint sends no self-spawn (open item 3) |
| 12 | Save control | PASS (fallback clause) | client: Save row greyed with tooltip, quicksave **detected and reported** in the GUI (hard block stays M4), no autosave in 30 min; authority: no vanilla autosave in 20 min, only checkpoints |
| 13 | Self-test | PASS | client 7 PASS / 1 WARN / 1 SKIP; authority 8 PASS / 1 WARN; GUI Diagnostics |
| 14 | Password never persisted | PASS | `find-password.ps1`: no hit in 83 files. CI: hostsim |
| 15 | Remembered fields | PASS | address and name pre-filled after restart. Follow-up: "Last server" line (open item 5) |
| 16 | `launch.json` | PASS | auto-join and delete; expired file ignored and deleted |
| 17 | No regressions | PASS | CI green on main after every merge of the day; `mod/build.ps1` 251/251, Lua 161 |
| 18 | Session-2 verdicts recorded | PASS | [spikes/session-2-results.md](spikes/session-2-results.md) |
| V21 | Checkpoint loads without the mod | PASS | Run 4: loads with no warning, no X4MP trace |

**Result:** all 18 criteria pass. Nine defects were found and fixed live during session 3 (session-3 results, fixes 1-9). M2 closes once the
open items 1-7 of the session-3 results are fixed and a short retest (HUD line, chat + Esc, `/reloadui` self-test, first-checkpoint self-spawn) passes.
