#include "features/statics/manifest_index.h"

#include <format>
#include <algorithm>

#include "common_generated.h"
#include "manifest_generated.h"
#include "session_generated.h"
#include "features/onfoot/onfoot_types.h"

namespace x4mp::features::statics {

namespace P = X4MP::Proto;

namespace {

std::string str_of(const flatbuffers::String* s) { return s != nullptr ? std::string(s->c_str(), s->size()) : std::string(); }

}  // namespace

bool ManifestIndex::load(std::span<const std::uint8_t> bytes) {
  clear();
  if (bytes.size() < 8) return false;
  flatbuffers::Verifier v(bytes.data(), bytes.size());
  if (!P::VerifyManifestBuffer(v)) return false;
  const auto* m = P::GetManifest(bytes.data());
  if (m == nullptr) return false;

  // The two reference tables the entries point into: string index -> text (macros, factions) and sector index -> sector macro.
  std::unordered_map<std::uint32_t, std::string> strings;
  if (const auto* list = m->strings()) {
    strings.reserve(list->size());
    for (const auto* e : *list) {
      if (e != nullptr && e->value() != nullptr) strings.emplace(e->index(), str_of(e->value()));
    }
  }
  std::unordered_map<std::uint16_t, std::string> sector_macros;
  if (const auto* list = m->sectors()) {
    sector_macros.reserve(list->size());
    for (const auto* s : *list) {
      if (s != nullptr) sector_macros.emplace(s->index(), str_of(s->macro()));
    }
  }

  if (const auto* cp = m->checkpoint_id()) checkpoint_ = std::format("{:016x}{:016x}", cp->hi(), cp->lo());

  if (const auto* entries = m->entries()) {
    rows_.reserve(entries->size());
    by_key_.reserve(entries->size());
    for (const auto* e : *entries) {
      if (e == nullptr) continue;
      ++stats_.entries;
      StaticRow row;
      row.net_id = e->net_id();
      row.kind = static_cast<std::uint16_t>(e->kind());
      row.component_id = e->component_id();
      row.sector = e->sector();
      row.origin = static_cast<std::uint16_t>(e->origin());
      row.owner_team = e->owner_team();
      row.owner_player = e->owner_player();
      row.idcode = str_of(e->idcode());
      if (const auto* p = e->position()) {
        row.x = p->x();
        row.y = p->y();
        row.z = p->z();
      }
      const auto macro = strings.find(e->macro_ref());
      if (macro == strings.end()) {
        ++stats_.no_macro;
        continue;
      }
      row.macro = macro->second;
      const auto sector = sector_macros.find(e->sector());
      if (sector == sector_macros.end()) {
        ++stats_.no_sector;
        continue;
      }
      row.sector_macro = sector->second;
      if (const auto owner = strings.find(e->owner_ref()); owner != strings.end()) row.owner = owner->second;

      // The same key the on-foot container flow uses (M4-19): one function, so the two sides cannot drift.
      row.key = onfoot::station_match_key(row.sector_macro, row.macro, row.x, row.y, row.z);
      ++stats_.indexed;
      // Duplicates stay in rows_: the id code tie-break (M4-18) needs both candidates. by_key_ keeps the first, ambiguous_ remembers
      // the key so the caller can count the match as ambiguous.
      const std::size_t at = rows_.size();
      rows_.push_back(std::move(row));
      const StaticRow& stored = rows_.back();
      const auto [it, inserted] = by_key_.emplace(stored.key, at);
      if (!inserted) {
        ++stats_.collisions;  // the first row keeps the key; the caller's tie-break (owner, then id code) is a refinement
        ambiguous_.insert(stored.key);
      }
      if (!stored.idcode.empty()) by_key_idcode_.emplace(stored.key + '\x1f' + stored.idcode, at);
    }
  }
  loaded_ = true;
  return true;
}

std::vector<const StaticRow*> ManifestIndex::in_sectors(std::span<const std::uint16_t> sectors) const {
  // M4-18: return all rows in the given sectors
  std::vector<const StaticRow*> out;
  if (sectors.empty() || !loaded_) return out;
  
  out.reserve(rows_.size()); // Pre-allocate for efficiency
  for (const StaticRow& row : rows_) {
    for (const auto sector : sectors) {
      if (row.sector == sector) {
        out.push_back(&row);
        break;
      }
    }
  }
  return out;
}

std::vector<const StaticRow*> ManifestIndex::in_sectors_filtered(std::span<const std::uint16_t> sectors, 
                                                                   std::uint16_t kind_filter) const {
  // M4-18: return all rows in the given sectors with the specified kind filter
  std::vector<const StaticRow*> out;
  if (sectors.empty() || !loaded_) return out;
  
  out.reserve(rows_.size()); // Pre-allocate for efficiency
  for (const StaticRow& row : rows_) {
    // Check if row matches sector and kind filter
    bool sector_match = false;
    for (const auto sector : sectors) {
      if (row.sector == sector) {
        sector_match = true;
        break;
      }
    }
    
    if (sector_match && (kind_filter == 0 || row.kind == kind_filter)) {
      out.push_back(&row);
    }
  }
  return out;
}

std::vector<std::string> ManifestIndex::get_sector_macros() const {
  // Get all unique sector macros present in the manifest
  std::unordered_set<std::string> sector_macros;
  for (const StaticRow& row : rows_) {
    sector_macros.insert(row.sector_macro);
  }
  
  std::vector<std::string> result(sector_macros.begin(), sector_macros.end());
  return result;
}

std::vector<std::string> ManifestIndex::get_macro_list() const {
  // Get all unique macros present in the manifest
  std::unordered_set<std::string> macros;
  for (const StaticRow& row : rows_) {
    macros.insert(row.macro);
  }
  
  std::vector<std::string> result(macros.begin(), macros.end());
  return result;
}

std::size_t ManifestIndex::get_sector_count() const {
  // Count unique sectors in the manifest
  std::unordered_set<std::uint16_t> sectors;
  for (const StaticRow& row : rows_) {
    sectors.insert(row.sector);
  }
  return sectors.size();
}

std::size_t ManifestIndex::get_station_count() const {
  // Count unique stations in the manifest
  std::unordered_set<std::string> stations;
  for (const StaticRow& row : rows_) {
    stations.insert(row.key);  // Using the key which uniquely identifies a station
  }
  return stations.size();
}

const StaticRow* ManifestIndex::lookup(std::string_view key, std::string_view idcode) const {
  if (key.empty()) return nullptr;
  // M4-18 tie-break: an exact id code wins over the first row of the key. Only collisions need this; a normal key has one row.
  if (!idcode.empty()) {
    std::string composed(key);
    composed.push_back('\x1f');
    composed.append(idcode);
    const auto exact = by_key_idcode_.find(composed);
    if (exact != by_key_idcode_.end()) return &rows_[exact->second];
  }
  const auto it = by_key_.find(std::string(key));
  return it == by_key_.end() ? nullptr : &rows_[it->second];
}

bool ManifestIndex::ambiguous(std::string_view key) const {
  return !key.empty() && ambiguous_.contains(std::string(key));
}

void ManifestIndex::clear() {
  loaded_ = false;
  checkpoint_.clear();
  rows_.clear();
  by_key_.clear();
  by_key_idcode_.clear();
  ambiguous_.clear();
  stats_ = ManifestStats{};
}

}  // namespace x4mp::features::statics