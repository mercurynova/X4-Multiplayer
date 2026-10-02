# X4MP: X4: Foundations multiplayer (from-scratch rewrite)

A standalone **C# server with a web admin GUI** plus a **Windows C++ mod** (built on
X4Native) that lets several X4: Foundations players share one universe. One X4 instance
(the *authority*) simulates the universe. Every X4 instance, the authority included,
connects to the server, which owns sessions, routing, saves, teams, the economy and the GUI.

## Repo
Private GitHub: https://github.com/mercurynova/X4-Multiplayer (account `mercurynova`).
This clone uses gh for git credentials (repo-local `credential.helper = !gh auth git-credential`),
so pushes go through the **active** gh account. If push says "Repository not found",
run `gh auth switch -u mercurynova`.
**History was rewritten on 2026-10-02** (scrubbed personal data before going public): any clone made before that must be deleted and re-cloned, never pulled or merged.

## Start here
- `docs/README.md`: map of all project knowledge (read it first in a new session).
- `docs/dev-setup.md`: what a machine needs; run `tools/check-env.ps1`.
- `docs/architecture.md`: **authoritative design**; wins over the detailed docs.
- `docs/decisions.md`: ADR log, user decisions, in-game verification list.
- `docs/roadmap.md`: milestones, task ids (M0-xx, M1-xx), spikes S1–S9.
- `docs/execution-plan.md`: how work is delegated and reviewed.

## Working agreements (from the user)
- **Opus plans, Sonnet codes.** The main Opus session does planning, briefs and review.
  Implementation goes to subagents launched with `model: "sonnet"`, one task per agent, in a
  git worktree. Don't start big coding tasks in the Opus session. Planning is always fine.
- The user works from **multiple computers**. Claude's memory is per-machine, so record
  anything that must persist in this repo (this file, `docs/`), not only in memory.
- The user is the in-game tester. Claude can't play X4. Give them step-by-step test
  scripts and ask for logs.

## Hard rules
- The repo is **GPL-3.0** (`LICENSE`, 2026-10-02) and is being prepared to go public: never commit personal data (Steam ids, local user paths, emails), secrets, Egosoft game data, or the names/URLs of the owner's private reference repos.
- `reference/` is a previous X4 multiplayer mod (private clone; the owner has the URL): **unlicensed, binary-only.
  Read for lessons, never copy code.**
- Never modify the X4 install. `x4-unpacked/` is extracted game data, read-only reference.
- Pinned game build: **X4 9.00 build 611726**, X4Native **v9.0.0-611726**.
- Mod config comes from files, never env vars (Steam relaunch drops them).
- Never remove the player's own ship (instant game over). MD callbacks may run off the
  main thread: copy data only, and call game APIs on the frame update.

## Locked product decisions (details in docs/decisions.md)
- Web dashboard GUI; server-centric relay; Windows-first mod.
- Server stack: C#/.NET 10 (`net10.0`, SDK 10.0.4xx),
  ASP.NET Core + SignalR + SQLite; React/Vite/TS frontend embedded in the exe.
- Ports: TCP 47780 (control + bulk), UDP 47781 (realtime), HTTP 47790 (GUI + save fallback).
- Teams: up to 8, factions `x4mp_team_1..8`; players share a faction or are on separate
  ones; inter-team relations allied/neutral/hostile from the start.
- Credits: per-player wallets, teammate transfers + team pool, auto-shared if one team
  (CreditMode Auto|PerPlayer|Shared); donate/loan/escrowed trade gated
  Off/Teammates/Allied/Anyone. Starting credits are a GUI setting.
- Per-team HQ + research (ADR-048, all defaults); team origins (ADR-049): chosen HQ start sector + race-based starting blueprints from vanilla gamestarts.
- Team diplomacy (ADR-047): server-owned relations, treaties via our own screen.
- On-foot presence (ADR-046): M3b = HUD presence list + MP lounge room, M3c = any shared room, M5b = Talk-menu credits/team/trade.
- Avatar appearance (ADR-051): players choose the race + variants others see them as (default from team origin, free choice, others-only; NPC actor macros).
- Player portal (ADR-050, post-M5): player-only web tab (one-time in-game sign-in, LAN/VPN) for team market knowledge (strict in-game visibility by default, admin setting), assets/fleets, empire notes and naming conventions.
- Story/universe unlocks are session-global (no team gets extra sectors); story is
  played together per team (ADR-037). Fog of war later (ADR-038). Loan enforcement
  options later (ADR-040).
- Mods: server tracks each player's mods and a per-session mod list (Required/Allowed/Blocked);
  X4MP never hosts or installs mods, only links to Nexus/Workshop and toggles enable state
  (ADR-044, docs/mod-management.md). Library mods: SirNukes reference-only, kuertee UIX optional (ADR-043).

## Layout
`docs/` design + knowledge base · `protocol/schema/` FlatBuffers schema ·
`tools/` scripts (`x4cat_extract.py`, `check-env.ps1`) · `PLAN.md` original plan.
Server, web, mod and FakeNode folders get created in M0 (see architecture.md repo layout).
