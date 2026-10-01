# X4MP knowledge base

Everything needed to work on this project, on any machine. If you learn something
non-obvious (a game API quirk, a tool gotcha, a setup step), add it to the right doc
below. The repo is the only memory that follows you between computers.

## Getting set up
| Doc | Use it for |
|---|---|
| [dev-setup.md](dev-setup.md) | Per-machine requirements by role, install commands, game settings, paths, ports, rebuilding local-only folders |
| [`../tools/check-env.ps1`](../tools/check-env.ps1) | One command that checks this machine against dev-setup.md |
| [`../CLAUDE.md`](../CLAUDE.md) | Project context + working agreements for Claude sessions |

## Design (what we're building)
| Doc | Use it for |
|---|---|
| [architecture.md](architecture.md) | **Authoritative** system design; wins over the detailed docs |
| [decisions.md](decisions.md) | ADR log (why things are the way they are), user decisions, open questions, in-game verification list |
| [protocol.md](protocol.md) + [`../protocol/schema/`](../protocol/schema/) | Wire protocol, message catalog, FlatBuffers schema |
| [server-design.md](server-design.md) | C# server, admin API, GUI screens, FakeNode |
| [mod-design.md](mod-design.md) | C++/Lua/MD mod: threading, authority/client pipelines, teams, economy |
| [mod-management.md](mod-management.md) | Third-party mods: per-player extension reports, session mod list/policy, rejection messages with Nexus/Workshop links, launcher enable/disable sync, modded-game roadmap (ADR-044) |

## Research and reference
| Doc | Use it for |
|---|---|
| [x4-api-notes.md](x4-api-notes.md) | X4 9.00 + X4Native API facts: exported functions, Lua globals, MD events, gaps |
| [requirements.md](requirements.md) | Audit of the old mod: REQ list, PIT pitfalls, known bugs, Windows UX lessons |
| [research/library-mods.md](research/library-mods.md) | SirNukes Mod Support APIs and kuertee UI Extensions: licenses, internals, coexistence (ADR-043) |
| `../x4-unpacked/` (local only) | Extracted game Lua/MD/libraries; search for `ffi.cdef` signatures and MD usage |
| `../reference/` (local only) | Old mod clone: lessons only, never copy code |

## Doing the work
| Doc | Use it for |
|---|---|
| [roadmap.md](roadmap.md) | Milestones M0–M6, task ids + acceptance criteria, spikes S1–S9, backlog |
| [execution-plan.md](execution-plan.md) | Opus-plans/Sonnet-codes workflow, delegation waves, brief template, review checklist |

## Quick facts
- Game: X4 9.00 build 611726 (pinned). Protected UI mode OFF. Same DLC set for all players.
- Ports: 47780/TCP, 47781/UDP, 47790/HTTP.
- Saves: `Documents\Egosoft\X4\<steam-id>\save\` (may be under OneDrive); load by name without `.xml.gz`.
- Unpack game data: `python tools/x4cat_extract.py "<X4 dir>" x4-unpacked "<regex>"`.
