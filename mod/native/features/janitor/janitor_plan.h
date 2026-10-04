#pragma once
// features/janitor: the janitor's decision rule (M3-13, docs/m3-plan.md 4.10), pure and SDK-free so Catch2 tests it without a game.
//
// An object is a LEFTOVER when it is ours and nothing keeps it alive:
//   ours      its name starts with "[MP] " (every ghost and avatar the mod dresses, plus the reference mod's), or - on a CLIENT only - it is
//             owned by a team faction x4mp_team_* (a client has no legitimate team-owned ship: the checkpoint's avatars and the old ghosts are
//             all leftovers once the takeover is done). The authority never removes a team-owned ship that merely lacks the name prefix.
//   alive     (never removed, and counted by the reason)
//               ghost     a ghost this node spawned (ghosts::ghost_hub().is_ghost_local)
//               avatar    an avatar of the authority: a local id the binder bound, or an idcode of an avatar record (ids change on every
//                         load, idcodes do not)
//               own       the takeover's own copy (the ship the player sits in or will sit in) and, until it is removed, the host copy
//               guarded   the player's guarded ids (player ship, controlled ship, container ...): SafeRemove would refuse them anyway
// Everything else that is ours is a leftover and goes through game::safe_remove (which re-checks the guard).

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_set>

namespace x4mp::features {

inline constexpr std::string_view kGhostNamePrefix = "[MP] ";

enum class JanitorVerdict : std::uint8_t { NotOurs, KeepGhost, KeepAvatar, KeepOwn, KeepGuarded, Remove };

struct JanitorProtection {
  std::function<bool(std::uint64_t)> is_ghost;    // ghost-local ids (null = none)
  std::function<bool(std::uint64_t)> is_guarded;  // player guard (null = none)
  std::unordered_set<std::uint64_t> avatar_ids;   // bound avatar ids (authority)
  std::unordered_set<std::string> avatar_idcodes; // avatar record idcodes (authority)
  std::unordered_set<std::uint64_t> own_ids;      // the takeover's avatar copy and host copy (client)
  bool remove_team_owned = false;                 // client role: team-owned objects are leftovers even without the name prefix
};

[[nodiscard]] inline bool janitor_is_ours(std::string_view name, bool team_owned, const JanitorProtection& p) {
  return name.starts_with(kGhostNamePrefix) || (team_owned && p.remove_team_owned);
}

// `idcode` is only read for objects that are ours (the caller may fetch it lazily).
[[nodiscard]] inline JanitorVerdict janitor_decide(std::uint64_t id, std::string_view name, bool team_owned, const std::function<std::string()>& idcode,
                                                   const JanitorProtection& p) {
  if (!janitor_is_ours(name, team_owned, p)) return JanitorVerdict::NotOurs;
  if (p.is_ghost && p.is_ghost(id)) return JanitorVerdict::KeepGhost;
  if (p.own_ids.contains(id)) return JanitorVerdict::KeepOwn;
  if (p.avatar_ids.contains(id)) return JanitorVerdict::KeepAvatar;
  if (!p.avatar_idcodes.empty() && idcode) {
    const std::string code = idcode();
    if (!code.empty() && p.avatar_idcodes.contains(code)) return JanitorVerdict::KeepAvatar;
  }
  if (p.is_guarded && p.is_guarded(id)) return JanitorVerdict::KeepGuarded;
  return JanitorVerdict::Remove;
}

}  // namespace x4mp::features
