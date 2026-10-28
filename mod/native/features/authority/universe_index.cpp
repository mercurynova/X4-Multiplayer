#include "features/authority/universe_index.h"

#include <algorithm>

namespace x4mp::features::auth {

void UniverseIndex::reserve(std::size_t expected) {
  if (expected == 0) return;
  if (entries_.bucket_count() < expected) entries_.reserve(expected);
  by_sector_.reserve(std::min<std::size_t>(expected, 1024));
}

UpsertResult UniverseIndex::upsert(std::uint32_t net_id, std::uint16_t sector, std::uint32_t macro_ref, std::uint32_t owner_ref,
                                   std::uint8_t kind, std::uint8_t flags, std::uint64_t component, std::int64_t now_us) {
  if (net_id == 0 || sector == 0) return {};  // a row without an id or a sector cannot be scheduled; counted by the caller
  const auto it = entries_.find(net_id);
  if (it != entries_.end()) {
    IndexEntry& e = it->second;
    if (e.sector != sector) {
      // The ship crossed a sector border: keep the net id (a client that already has the ghost must not see a respawn) and move the bucket.
      auto& old_bucket = by_sector_[e.sector];
      std::erase(old_bucket, net_id);
      by_sector_[sector].push_back(net_id);
      e.sector = sector;
      ++counters_.moved;
    }
    e.macro_ref = macro_ref;
    e.owner_ref = owner_ref;
    e.kind = kind;
    e.flags = flags;
    if (component != 0) e.component = component;
    e.last_seen_us = now_us;
    e.seen_in_pass = true;
    ++counters_.refreshed;
    return {&e, false};
  }
  IndexEntry e;
  e.net_id = net_id;
  e.sector = sector;
  e.macro_ref = macro_ref;
  e.owner_ref = owner_ref;
  e.kind = kind;
  e.flags = flags;
  e.component = component;
  e.last_seen_us = now_us;
  e.seen_in_pass = true;
  auto [slot, ok] = entries_.emplace(net_id, e);
  (void)ok;
  by_sector_[sector].push_back(net_id);
  ++counters_.inserted;
  return {&slot->second, true};
}

void UniverseIndex::begin_scan(std::uint16_t sector) {
  open_.insert(sector);
  const auto it = by_sector_.find(sector);
  if (it == by_sector_.end()) return;
  for (const auto id : it->second) {
    if (auto* e = find(id)) e->seen_in_pass = false;
  }
}

bool UniverseIndex::scanning(std::uint16_t sector) const {
  return open_.contains(sector);
}

std::size_t UniverseIndex::end_scan(std::uint16_t sector) { return end_scan(sector, nullptr); }

std::size_t UniverseIndex::end_scan(std::uint16_t sector, std::vector<RemovedRow>* removed) {
  open_.erase(sector);
  const auto it = by_sector_.find(sector);
  if (it == by_sector_.end()) {
    ++counters_.scans;
    return 0;
  }
  auto& ids = it->second;
  std::size_t removed_count = 0;
  std::size_t keep = 0;
  for (std::size_t i = 0; i < ids.size(); ++i) {
    const auto id = ids[i];
    const auto e = entries_.find(id);
    if (e != entries_.end() && e->second.seen_in_pass && e->second.sector == sector) {
      ids[keep++] = id;
      continue;
    }
    // Gone from the sector: either the scan did not see it (destroyed) or it moved and upsert already re-bucketed it. Only the first case is
    // a removal; a moved row is still in `entries_` under its new sector.
    if (e != entries_.end() && e->second.sector != sector) continue;
    if (e != entries_.end()) {
      if (removed != nullptr) removed->push_back(RemovedRow{id, e->second.component});
      entries_.erase(e);
    }
    ++removed_count;
  }
  ids.resize(keep);
  counters_.removed += static_cast<std::uint32_t>(removed_count);
  ++counters_.scans;
  return removed_count;
}

std::uint32_t UniverseIndex::net_id_of_component(std::uint64_t component) const {
  if (component == 0) return 0;
  const auto it = by_component_.find(component);
  return it == by_component_.end() ? 0 : it->second;
}

void UniverseIndex::bind_component(std::uint64_t component, std::uint32_t net_id) {
  if (component == 0 || net_id == 0) return;
  by_component_.emplace(component, net_id);
}

std::size_t UniverseIndex::drop_sector(std::uint16_t sector) {
  open_.erase(sector);
  const auto it = by_sector_.find(sector);
  if (it == by_sector_.end()) return 0;
  std::size_t dropped = 0;
  for (const auto id : it->second) {
    const auto e = entries_.find(id);
    if (e == entries_.end()) continue;
    if (e->second.component != 0) by_component_.erase(e->second.component);
    entries_.erase(e);
    ++dropped;
  }
  by_sector_.erase(it);
  return dropped;
}

bool UniverseIndex::erase(std::uint32_t net_id) {
  const auto it = entries_.find(net_id);
  if (it == entries_.end()) return false;
  if (it->second.component != 0) by_component_.erase(it->second.component);
  const auto bucket = by_sector_.find(it->second.sector);
  if (bucket != by_sector_.end()) std::erase(bucket->second, net_id);
  entries_.erase(it);
  ++counters_.removed;
  return true;
}

const IndexEntry* UniverseIndex::find(std::uint32_t net_id) const {
  const auto it = entries_.find(net_id);
  return it == entries_.end() ? nullptr : &it->second;
}

IndexEntry* UniverseIndex::find(std::uint32_t net_id) {
  const auto it = entries_.find(net_id);
  return it == entries_.end() ? nullptr : &it->second;
}

const std::vector<std::uint32_t>* UniverseIndex::ids_of(std::uint16_t sector) const {
  const auto it = by_sector_.find(sector);
  return it == by_sector_.end() ? nullptr : &it->second;
}

void UniverseIndex::clear() {
  entries_.clear();
  by_sector_.clear();
  by_component_.clear();
  open_.clear();
  counters_ = IndexCounters{};
}

}  // namespace x4mp::features::auth
