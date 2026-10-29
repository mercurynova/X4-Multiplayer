#include "features/statics/statics_hub.h"

#include <iterator>
#include <string>
#include <utility>

namespace x4mp::features::statics {

void StaticsHub::set_index(ManifestIndex index) {
  index_ = std::move(index);
  // A new checkpoint means new net ids: the old bindings would point at ids this manifest no longer names, and the old match report
  // describes a manifest that is no longer loaded.
  clear_bindings();
  clear_report();
}

void StaticsHub::clear() {
  index_.clear();
  components_.clear();
  by_sector_.clear();
  sectors_.clear();
  clear_stations();
  clear_report();
}

std::uint32_t StaticsHub::net_id_for(std::string_view key, std::string_view idcode) const {
  if (key.empty() || !index_.loaded()) return 0;
  const StaticRow* row = index_.lookup(key, idcode);
  return row != nullptr ? row->net_id : 0;
}

void StaticsHub::bind(std::uint16_t sector, std::uint32_t net_id, std::uint64_t component) {
  if (net_id == 0 || component == 0) return;
  components_[net_id] = component;
  by_sector_[sector].insert(net_id);
}

void StaticsHub::unbind(std::uint32_t net_id) {
  if (components_.erase(net_id) == 0) return;
  for (auto it = by_sector_.begin(); it != by_sector_.end();) {
    it->second.erase(net_id);
    it = it->second.empty() ? by_sector_.erase(it) : std::next(it);
  }
}

void StaticsHub::clear_bindings() {
  components_.clear();
  by_sector_.clear();
  clear_stations();  // the components are gone with the universe, so their ids are too
}

std::uint64_t StaticsHub::component_of(std::uint32_t net_id) const {
  const auto it = components_.find(net_id);
  return it == components_.end() ? 0 : it->second;
}

std::size_t StaticsHub::prune_sector(std::uint16_t sector, const std::unordered_set<std::uint64_t>& present) {
  const auto it = by_sector_.find(sector);
  if (it == by_sector_.end()) return 0;
  std::size_t dropped = 0;
  // Drop only the bindings whose component this pass did not list; the ones it did list stay in the sector index (a later pass must
  // still be able to prune them).
  for (auto id = it->second.begin(); id != it->second.end();) {
    const auto comp = components_.find(*id);
    if (comp != components_.end() && present.contains(comp->second)) {
      ++id;
      continue;
    }
    components_.erase(*id);
    id = it->second.erase(id);
    ++dropped;
  }
  if (it->second.empty()) by_sector_.erase(it);
  return dropped;
}

void StaticsHub::note_station(std::uint16_t sector, std::uint64_t component, std::uint32_t net_id, std::string sector_macro,
                              bool pinned, std::string owner) {
  if (component == 0 || net_id == 0) return;
  // What this node already knows about the station: a pass that could not read the owner or the sector macro must not erase them.
  std::string known_macro;
  std::string known_owner;
  bool was_pinned = false;
  const auto old = station_by_component_.find(component);
  if (old != station_by_component_.end()) {
    if (old->second.pinned && !pinned) {
      // M5-19: the authority's id wins over the local hash for good. The scan is still the only source for the owner and the sector
      // macro, though: a captured station keeps its pinned id and changes hands, so learn them and leave the id alone.
      if (!sector_macro.empty()) old->second.sector_macro = std::move(sector_macro);
      if (!owner.empty()) old->second.owner = std::move(owner);
      return;
    }
    was_pinned = old->second.pinned;
    known_macro = old->second.sector_macro;
    known_owner = old->second.owner;
    if (old->second.net_id == net_id && old->second.sector == sector && !pinned) {
      // the same answer as last pass: only learn what this pass named and the last one did not
      if (!sector_macro.empty()) old->second.sector_macro = std::move(sector_macro);
      if (!owner.empty()) old->second.owner = std::move(owner);
      return;
    }
    component_by_station_.erase(old->second.net_id);
    if (old->second.sector != sector) {
      const auto prev = stations_by_sector_.find(old->second.sector);
      if (prev != stations_by_sector_.end()) {
        prev->second.erase(component);
        if (prev->second.empty()) stations_by_sector_.erase(prev);
      }
    }
  }
  if (sector_macro.empty()) sector_macro = std::move(known_macro);
  if (owner.empty()) owner = std::move(known_owner);
  // One net id names one station on this node: a second component claiming it takes it over (a station was rebuilt at the same spot).
  const auto other = component_by_station_.find(net_id);
  if (other != component_by_station_.end() && other->second != component) station_by_component_.erase(other->second);
  LocalStation row;
  row.net_id = net_id;
  row.sector = sector;
  row.sector_macro = std::move(sector_macro);
  row.pinned = pinned || was_pinned;
  row.owner = std::move(owner);
  station_by_component_[component] = std::move(row);
  component_by_station_[net_id] = component;
  stations_by_sector_[sector].insert(component);
}

std::uint32_t StaticsHub::station_net_id(std::uint64_t component) const {
  const auto it = station_by_component_.find(component);
  return it == station_by_component_.end() ? 0 : it->second.net_id;
}

std::uint64_t StaticsHub::station_component(std::uint32_t net_id) const {
  const auto it = component_by_station_.find(net_id);
  return it == component_by_station_.end() ? 0 : it->second;
}

std::string StaticsHub::sector_macro_of(std::uint64_t component) const {
  const auto it = station_by_component_.find(component);
  return it == station_by_component_.end() ? std::string() : it->second.sector_macro;
}

std::string StaticsHub::owner_of(std::uint64_t component) const {
  const auto it = station_by_component_.find(component);
  return it == station_by_component_.end() ? std::string() : it->second.owner;
}

std::size_t StaticsHub::prune_stations(std::uint16_t sector, const std::unordered_set<std::uint64_t>& present) {
  const auto it = stations_by_sector_.find(sector);
  if (it == stations_by_sector_.end()) return 0;
  std::size_t dropped = 0;
  for (auto id = it->second.begin(); id != it->second.end();) {
    if (present.contains(*id)) {
      ++id;
      continue;
    }
    const auto row = station_by_component_.find(*id);
    if (row != station_by_component_.end()) {
      component_by_station_.erase(row->second.net_id);
      station_by_component_.erase(row);
    }
    id = it->second.erase(id);
    ++dropped;
  }
  if (it->second.empty()) stations_by_sector_.erase(it);
  return dropped;
}

void StaticsHub::clear_stations() {
  station_by_component_.clear();
  component_by_station_.clear();
  stations_by_sector_.clear();
}

std::uint32_t StaticsHub::total_matched() const noexcept {
  std::uint32_t n = 0;
  for (const auto& [sector, m] : sectors_) {
    (void)sector;
    n += m.matched;
  }
  return n;
}

std::uint32_t StaticsHub::total_missed() const noexcept {
  std::uint32_t n = 0;
  for (const auto& [sector, m] : sectors_) {
    (void)sector;
    n += m.missed;
  }
  return n;
}

void StaticsHub::reset_stats() {
  for (auto& [sector, m] : sectors_) {
    (void)sector;
    m = SectorMatch{};
  }
}

StaticsHub& statics_hub() {
  static StaticsHub hub;
  return hub;
}

}  // namespace x4mp::features::statics
