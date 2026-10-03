#include "core/ghost/registry.h"

#include <algorithm>
#include <charconv>

namespace x4mp::ghost {

// ---------------------------------------------------------------------------------------------
// NetMap / GhostRegistry / AvatarRegistry
// ---------------------------------------------------------------------------------------------

void NetMap::bind(std::uint32_t net_id, LocalId local_id, NetKind kind, std::uint16_t player_id) {
  std::erase_if(entries_, [&](const NetEntry& e) { return e.net_id == net_id || (local_id != 0 && e.local_id == local_id); });
  entries_.push_back({net_id, local_id, kind, player_id});
}
bool NetMap::unbind_net(std::uint32_t net_id) {
  return std::erase_if(entries_, [&](const NetEntry& e) { return e.net_id == net_id; }) > 0;
}
bool NetMap::unbind_local(LocalId local_id) {
  return std::erase_if(entries_, [&](const NetEntry& e) { return e.local_id == local_id; }) > 0;
}
const NetEntry* NetMap::find_net(std::uint32_t net_id) const noexcept {
  for (const auto& e : entries_) if (e.net_id == net_id) return &e;
  return nullptr;
}
const NetEntry* NetMap::find_local(LocalId local_id) const noexcept {
  if (local_id == 0) return nullptr;
  for (const auto& e : entries_) if (e.local_id == local_id) return &e;
  return nullptr;
}

void GhostRegistry::add(const GhostEntry& e) {
  std::erase_if(entries_, [&](const GhostEntry& x) { return x.net_id == e.net_id; });
  entries_.push_back(e);
}
bool GhostRegistry::remove(std::uint32_t net_id) {
  return std::erase_if(entries_, [&](const GhostEntry& x) { return x.net_id == net_id; }) > 0;
}
const GhostEntry* GhostRegistry::find(std::uint32_t net_id) const noexcept {
  for (const auto& e : entries_) if (e.net_id == net_id) return &e;
  return nullptr;
}
const GhostEntry* GhostRegistry::find_player(std::uint16_t player_id) const noexcept {
  for (const auto& e : entries_) if (e.player_id == player_id) return &e;
  return nullptr;
}
bool GhostRegistry::contains_local(LocalId local_id) const noexcept {
  for (const auto& e : entries_) if (e.local_id == local_id) return true;
  return false;
}

void AvatarRegistry::add(const AvatarEntry& e) {
  std::erase_if(entries_, [&](const AvatarEntry& x) { return x.player_id == e.player_id; });
  entries_.push_back(e);
}
bool AvatarRegistry::remove_player(std::uint16_t player_id) {
  return std::erase_if(entries_, [&](const AvatarEntry& x) { return x.player_id == player_id; }) > 0;
}
const AvatarEntry* AvatarRegistry::find_player(std::uint16_t player_id) const noexcept {
  for (const auto& e : entries_) if (e.player_id == player_id) return &e;
  return nullptr;
}
const AvatarEntry* AvatarRegistry::find_net(std::uint32_t net_id) const noexcept {
  for (const auto& e : entries_) if (e.net_id == net_id) return &e;
  return nullptr;
}
bool AvatarRegistry::contains_local(LocalId local_id) const noexcept {
  for (const auto& e : entries_) if (e.local_id == local_id) return true;
  return false;
}

// ---------------------------------------------------------------------------------------------
// Stash blob
// ---------------------------------------------------------------------------------------------

std::string Registries::to_blob(std::uint64_t epoch) const {
  std::string out = "x4gr 1 " + std::to_string(epoch) + "\n";
  for (const auto& e : netmap.entries())
    out += "N " + std::to_string(e.net_id) + " " + std::to_string(e.local_id) + " " + std::to_string(static_cast<unsigned>(e.kind)) +
           " " + std::to_string(e.player_id) + "\n";
  for (const auto& e : ghosts.entries())
    out += "G " + std::to_string(e.net_id) + " " + std::to_string(e.player_id) + " " + std::to_string(e.local_id) + " " +
           std::to_string(e.team) + "\n";
  for (const auto& e : avatars.entries())
    out += "A " + std::to_string(e.player_id) + " " + std::to_string(e.net_id) + " " + std::to_string(e.local_id) + "\n";
  out += "end " + std::to_string(netmap.size() + ghosts.size() + avatars.size()) + "\n";
  return out;
}

namespace {

// Whitespace-separated tokens of one line.
struct Tokens {
  std::string_view s;
  bool next(std::string_view& tok) {
    while (!s.empty() && s.front() == ' ') s.remove_prefix(1);
    if (s.empty()) return false;
    const std::size_t n = s.find(' ');
    tok = s.substr(0, n);
    s.remove_prefix(n == std::string_view::npos ? s.size() : n);
    return true;
  }
  template <class T>
  bool num(T& out) {
    std::string_view t;
    if (!next(t)) return false;
    const auto r = std::from_chars(t.data(), t.data() + t.size(), out);
    return r.ec == std::errc{} && r.ptr == t.data() + t.size();
  }
  bool done() {
    std::string_view t;
    return !next(t);
  }
};

}  // namespace

std::optional<Registries> Registries::from_blob(std::string_view blob, std::uint64_t& epoch_out) {
  Registries r;
  bool header = false, ended = false;
  std::size_t entries = 0, declared = 0;
  while (!blob.empty()) {
    const std::size_t nl = blob.find('\n');
    if (nl == std::string_view::npos) return std::nullopt;  // every line ends with \n
    Tokens t{blob.substr(0, nl)};
    blob.remove_prefix(nl + 1);
    std::string_view tag;
    if (!t.next(tag)) return std::nullopt;
    if (ended) return std::nullopt;  // nothing after "end"
    if (!header) {
      unsigned version = 0;
      if (tag != "x4gr" || !t.num(version) || version != 1 || !t.num(epoch_out) || !t.done()) return std::nullopt;
      header = true;
    } else if (tag == "N") {
      NetEntry e;
      unsigned kind = 0;
      if (!t.num(e.net_id) || !t.num(e.local_id) || !t.num(kind) || !t.num(e.player_id) || !t.done() || kind > 3) return std::nullopt;
      e.kind = static_cast<NetKind>(kind);
      r.netmap.bind(e.net_id, e.local_id, e.kind, e.player_id);
      ++entries;
    } else if (tag == "G") {
      GhostEntry e;
      if (!t.num(e.net_id) || !t.num(e.player_id) || !t.num(e.local_id) || !t.num(e.team) || !t.done()) return std::nullopt;
      r.ghosts.add(e);
      ++entries;
    } else if (tag == "A") {
      AvatarEntry e;
      if (!t.num(e.player_id) || !t.num(e.net_id) || !t.num(e.local_id) || !t.done()) return std::nullopt;
      r.avatars.add(e);
      ++entries;
    } else if (tag == "end") {
      if (!t.num(declared) || !t.done()) return std::nullopt;
      ended = true;
    } else {
      return std::nullopt;
    }
  }
  if (!header || !ended || declared != entries) return std::nullopt;
  return r;
}

void Registries::save(session::IStash& stash, std::uint64_t epoch) const { stash.put(kStashKey, to_blob(epoch)); }

AdoptReport Registries::adopt(session::IStash& stash, std::uint64_t epoch, const std::function<bool(LocalId)>& is_valid) {
  AdoptReport rep;
  clear();
  const auto blob = stash.get(kStashKey);
  if (!blob) return rep;
  rep.found = true;
  std::uint64_t saved_epoch = 0;
  auto parsed = from_blob(*blob, saved_epoch);
  if (!parsed) {
    rep.parse_error = true;
    stash.erase(kStashKey);
    return rep;
  }
  if (saved_epoch != epoch) {
    rep.epoch_mismatch = true;
    stash.erase(kStashKey);
    return rep;
  }

  auto valid = [&](LocalId id) {
    if (id == 0) return false;
    if (is_valid(id)) return true;
    rep.dropped_local_ids.push_back(id);
    return false;
  };

  for (const auto& e : parsed->netmap.entries()) {
    if (!valid(e.local_id)) { ++rep.dropped_net; continue; }
    if (netmap.find_net(e.net_id) || netmap.find_local(e.local_id)) { ++rep.conflicts; continue; }
    netmap.bind(e.net_id, e.local_id, e.kind, e.player_id);
    ++rep.kept_net;
  }
  for (const auto& e : parsed->ghosts.entries()) {
    if (!valid(e.local_id)) { ++rep.dropped_ghosts; continue; }
    if (ghosts.find(e.net_id) || ghosts.contains_local(e.local_id) || avatars.contains_local(e.local_id)) { ++rep.conflicts; continue; }
    ghosts.add(e);
    ++rep.kept_ghosts;
  }
  for (const auto& e : parsed->avatars.entries()) {
    if (!valid(e.local_id)) { ++rep.dropped_avatars; continue; }
    if (avatars.find_player(e.player_id) || avatars.contains_local(e.local_id) || ghosts.contains_local(e.local_id)) { ++rep.conflicts; continue; }
    avatars.add(e);
    ++rep.kept_avatars;
  }

  // Reconcile: the three registries must describe the same objects. A ghost/avatar the NetMap does not know gets a NetMap entry; a
  // NetMap entry of kind Ghost/Avatar without a counterpart gets one; a registry entry that contradicts the NetMap is dropped.
  const auto ghost_copy = ghosts.entries();
  for (const auto& g : ghost_copy) {
    const NetEntry* n = netmap.find_net(g.net_id);
    if (!n) {
      if (netmap.find_local(g.local_id)) { ghosts.remove(g.net_id); ++rep.conflicts; --rep.kept_ghosts; continue; }
      netmap.bind(g.net_id, g.local_id, NetKind::Ghost, g.player_id);
      ++rep.rebuilt;
    } else if (n->local_id != g.local_id) {
      ghosts.remove(g.net_id);
      ++rep.conflicts;
      --rep.kept_ghosts;
    }
  }
  const auto avatar_copy = avatars.entries();
  for (const auto& a : avatar_copy) {
    const NetEntry* n = a.net_id ? netmap.find_net(a.net_id) : nullptr;
    if (a.net_id == 0) continue;  // avatar not yet announced by the server: nothing to reconcile
    if (!n) {
      if (netmap.find_local(a.local_id)) { avatars.remove_player(a.player_id); ++rep.conflicts; --rep.kept_avatars; continue; }
      netmap.bind(a.net_id, a.local_id, NetKind::Avatar, a.player_id);
      ++rep.rebuilt;
    } else if (n->local_id != a.local_id) {
      avatars.remove_player(a.player_id);
      ++rep.conflicts;
      --rep.kept_avatars;
    }
  }
  const auto net_copy = netmap.entries();
  for (const auto& n : net_copy) {
    if (n.kind == NetKind::Ghost && !ghosts.find(n.net_id)) {
      ghosts.add({n.net_id, n.player_id, n.local_id, 0});
      ++rep.rebuilt;
    } else if (n.kind == NetKind::Avatar && !avatars.find_net(n.net_id) && !avatars.find_player(n.player_id)) {
      avatars.add({n.player_id, n.net_id, n.local_id});
      ++rep.rebuilt;
    }
  }
  return rep;
}

}  // namespace x4mp::ghost
