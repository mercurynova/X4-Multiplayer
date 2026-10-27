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
