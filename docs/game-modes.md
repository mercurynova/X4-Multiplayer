# Game modes: cooperative and competitive multiplayer

Status: design note, 2026-11-06. **Only the cooperative mode is implemented.** The competitive mode is
recorded here so that the mechanics we build now do not have to be torn out later.

## 1. Cooperative (the mode we build today)

One shared empire: the players are crew of the same organisation, so everything that belongs to a player
belongs to the group.

- **Account / wallet**: one wallet. The authority's economy is the only economy; credits earned by any
  player are the group's credits.
- **Faction**: one player faction (`faction.x4mp_team_1` today). Reputation, licences and relations are
  shared, and every player's actions move the same numbers.
- **Ships and stations**: one fleet. Anyone may fly, dock, trade with or command any player-owned ship
  or station; ownership is "the group", not a person.
- **Universe**: one authoritative universe (the authority's game). Clients mirror it; the session save is
  the authority's save.
- **Interactions**: none to design — the players share everything, so there is no trade or combat between
  them. What matters is that two players can man the same ship (pilot + turrets) and the same station.

Consequences for the code we write now:

- every ownership question must go through one place (`own_ship`, `own_assets`, the capture owner check),
  never through `player.entity` directly, so a per-player owner can be swapped in later;
- the wire already carries `player_id` everywhere, so a future "who owns this" field has somewhere to go;
- the session config should grow a `mode` value even while it has a single legal value, so the handshake
  can refuse a mismatched client instead of behaving differently by accident.

## 2. Competitive multiplayer (future, not implemented)

Every player is their own empire, and the players interact through the game's normal systems.

- **Account / wallet**: one wallet per player, separate from the others.
- **Faction**: one player faction per player, with its own reputation, relations and licences. Two player
  factions must be able to be hostile, neutral or allied to each other and to the NPC factions.
- **Ships and stations**: per player. A ship belongs to the player who bought it; the other player cannot
  command it, and boarding/capturing it must be a real game action with the normal consequences.
- **Universe**: still one authoritative universe (otherwise there is nothing to interact in), but the
  authority is a referee rather than a player: it must not be one player's game if that player can be
  attacked. That is a server-side simulation question, not a mod question, and it is out of scope now.
- **Interactions between players**: trade (cargo and station trade offers), contracts, combat (including
  boarding), shared or contested sectors, and diplomacy between the two player factions.

What that will require that does not exist yet:

| Need | Where it lands |
|---|---|
| an owner per entity on the wire and in the save | `protocol/`, `server/src/X4MP.Core`, the capture/ghost registry |
| per-player wallet and reputation | economy and faction modules (currently authority-only) |
| player-vs-player damage and kill attribution | combat lane, which does not exist yet |
| a session `mode` and role model per mode | `SessionConfig`, the join handshake |
| a referee authority (not a player's game) | server-side simulation, a much larger change |

## 3. What to keep mode-agnostic now

1. Ownership is read through one accessor, never assumed to be "the local player".
2. Anything that is currently "shared" is documented as a cooperative decision, with the competitive
   shape written next to it (this file).
3. New wire fields carry an owner id even when the only legal value is the group.
4. The session handshake carries a `mode`; a client that does not speak it is refused rather than
   silently degraded.
