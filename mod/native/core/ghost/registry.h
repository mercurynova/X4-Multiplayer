#pragma once
// core/ghost: NetMap (net_id <-> local object id), GhostRegistry (ghost objects this node created) and AvatarRegistry (avatars the
// authority drives), with stash (de)serialisation and the adoption rules after a DLL re-init (M3-02; docs/m3-plan.md 4.2, 4.8).
//
// The DLL is unloaded and re-initialised on every save load and /reloadui; the X4Native stash survives (session-2 results). Same
// universe (the M2 epoch rule says so): registries come back from the stash and every local id is checked with the game's
// IsValidComponent; valid ids are ADOPTED (no respawn), invalid ones are dropped and the ghost is respawned from the next sample.
// New universe: the caller does not adopt, it clears (and the janitor removes `[MP] ` objects by name).
//
// The three registries are saved as ONE text blob (key kStashKey) so they can never disagree after a partial write. Format v1:
//   x4gr 1 <epoch>
//   N <net_id> <local_id> <kind> <player>      (one per NetMap entry)
//   G <net_id> <player> <local_id> <team>      (one per ghost)
//   A <player> <net_id> <local_id>             (one per avatar)
//   end <number of entries above>
// A blob that does not parse completely is rejected as a whole (nothing adopted; the janitor cleans up by name).
//
// Pure C++, no game calls; local ids are opaque 64-bit values (X4 UniverseID). Not thread-safe (frame thread only).

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/session/session.h"  // IStash

namespace x4mp::ghost {

using LocalId = std::uint64_t;  // UniverseID; 0 = none

enum class NetKind : std::uint8_t {
  Self = 0,    // this node's own (avatar) ship
  Ghost = 1,   // a remote player's ghost on this node
  Avatar = 2,  // an avatar driven by the authority
  Other = 3
};

struct NetEntry {
  std::uint32_t net_id = 0;
  LocalId local_id = 0;
  NetKind kind = NetKind::Other;
  std::uint16_t player_id = 0;
  bool operator==(const NetEntry&) const = default;
};

struct GhostEntry {
  std::uint32_t net_id = 0;
  std::uint16_t player_id = 0;
  LocalId local_id = 0;
  std::uint16_t team = 0;
  bool operator==(const GhostEntry&) const = default;
};

struct AvatarEntry {
  std::uint16_t player_id = 0;
  std::uint32_t net_id = 0;
  LocalId local_id = 0;
  bool operator==(const AvatarEntry&) const = default;
};

// Bidirectional map. One net_id maps to one local id and vice versa; bind() replaces conflicting entries.
class NetMap {
 public:
  void bind(std::uint32_t net_id, LocalId local_id, NetKind kind, std::uint16_t player_id = 0);
  bool unbind_net(std::uint32_t net_id);
  bool unbind_local(LocalId local_id);
  [[nodiscard]] const NetEntry* find_net(std::uint32_t net_id) const noexcept;
  [[nodiscard]] const NetEntry* find_local(LocalId local_id) const noexcept;
  [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }
  [[nodiscard]] const std::vector<NetEntry>& entries() const noexcept { return entries_; }
  void clear() { entries_.clear(); }

 private:
  std::vector<NetEntry> entries_;
};

class GhostRegistry {
 public:
  void add(const GhostEntry& e);  // replaces an entry with the same net_id
  bool remove(std::uint32_t net_id);
  [[nodiscard]] const GhostEntry* find(std::uint32_t net_id) const noexcept;
  [[nodiscard]] const GhostEntry* find_player(std::uint16_t player_id) const noexcept;
  [[nodiscard]] bool contains_local(LocalId local_id) const noexcept;
  [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }
  [[nodiscard]] const std::vector<GhostEntry>& entries() const noexcept { return entries_; }
  void clear() { entries_.clear(); }

 private:
  std::vector<GhostEntry> entries_;
};

class AvatarRegistry {
 public:
  void add(const AvatarEntry& e);  // replaces an entry with the same player_id
  bool remove_player(std::uint16_t player_id);
  [[nodiscard]] const AvatarEntry* find_player(std::uint16_t player_id) const noexcept;
  [[nodiscard]] const AvatarEntry* find_net(std::uint32_t net_id) const noexcept;
  [[nodiscard]] bool contains_local(LocalId local_id) const noexcept;
  [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }
  [[nodiscard]] const std::vector<AvatarEntry>& entries() const noexcept { return entries_; }
  void clear() { entries_.clear(); }

 private:
  std::vector<AvatarEntry> entries_;
};

inline constexpr std::string_view kStashKey = "ghost.registries";

struct AdoptReport {
  bool found = false;           // a blob was in the stash
  bool parse_error = false;     // it did not parse completely: nothing adopted, stash blob erased
  bool epoch_mismatch = false;  // saved by another universe: nothing adopted, stash blob erased
  std::size_t kept_net = 0, dropped_net = 0;
  std::size_t kept_ghosts = 0, dropped_ghosts = 0;
  std::size_t kept_avatars = 0, dropped_avatars = 0;
  std::size_t rebuilt = 0;      // counterpart entries re-created so the three registries agree
  std::size_t conflicts = 0;    // entries dropped because their local id or net id was already taken
  std::vector<LocalId> dropped_local_ids;  // ids that failed IsValidComponent (nothing to remove: they are gone)
};

// The registries of one node together. They are always saved and adopted as a unit.
class Registries {
 public:
  NetMap netmap;
  GhostRegistry ghosts;
  AvatarRegistry avatars;

  void clear() {
    netmap.clear();
    ghosts.clear();
    avatars.clear();
  }
  [[nodiscard]] bool empty() const noexcept { return netmap.size() == 0 && ghosts.size() == 0 && avatars.size() == 0; }

  [[nodiscard]] std::string to_blob(std::uint64_t epoch) const;
  [[nodiscard]] static std::optional<Registries> from_blob(std::string_view blob, std::uint64_t& epoch_out);

  void save(session::IStash& stash, std::uint64_t epoch) const;  // writes kStashKey
  // Replaces the content with what the stash holds for `epoch`, keeping only entries whose local id passes `is_valid`
  // (IsValidComponent in the mod). The registries are empty afterwards if nothing was adopted. The blob is left in the stash
  // when adoption succeeded (the next save overwrites it) and erased when it was unusable.
  AdoptReport adopt(session::IStash& stash, std::uint64_t epoch, const std::function<bool(LocalId)>& is_valid);
};

}  // namespace x4mp::ghost
