#include "features/map/map_hub.h"

#include <iterator>
#include <set>
#include <utility>

#include <flatbuffers/verifier.h>
#include <nlohmann/json.hpp>

#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::features::map {

namespace P = X4MP::Proto;

namespace {

// A real universe has a few hundred sectors. These caps only keep a broken or hostile server from growing this hub without bound.
constexpr std::size_t kMaxSectors = 4096;
constexpr std::size_t kMaxLinks = 16384;

std::string text(const flatbuffers::String* value) { return value != nullptr ? value->str() : std::string(); }

std::pair<std::uint16_t, std::uint16_t> link_key(std::uint16_t from, std::uint16_t to) {
  return from < to ? std::pair{from, to} : std::pair{to, from};
}

bool valid_sector(const MapSectorRow& sector) { return sector.index != 0; }

// Builds the map a snapshot describes. False when the payload is not a usable map (the previous one is kept).
bool read_snapshot(const P::MapSnapshot& snapshot, std::map<std::uint16_t, MapSectorRow>& sectors,
                   std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow>& links) {
  const auto* rows = snapshot.sectors();
  if (rows == nullptr || rows->size() == 0 || rows->size() > kMaxSectors) return false;
  for (const auto* row : *rows) {
    if (row == nullptr) return false;
    MapSectorRow sector;
    sector.index = row->index();
    sector.macro = text(row->macro());
    sector.cluster = text(row->cluster_macro());
    sector.name = text(row->name());
    sector.owner = text(row->owner());
    if (const auto* pos = row->galaxy_pos()) {
      sector.x = pos->x();
      sector.y = pos->y();
      sector.z = pos->z();
    }
    if (!valid_sector(sector) || !sectors.emplace(sector.index, std::move(sector)).second) return false;
  }

  const auto* links_in = snapshot.links();
  if (links_in != nullptr) {
    if (links_in->size() > kMaxLinks) return false;
    for (const auto* row : *links_in) {
      if (row == nullptr) continue;
      const auto key = link_key(row->from(), row->to());
      if (key.first == key.second || !sectors.contains(key.first) || !sectors.contains(key.second)) return false;
      if (!links.emplace(key, MapLinkRow{key.first, key.second, static_cast<std::uint8_t>(row->kind())}).second) return false;
    }
  }
  return true;
}

// GalaxyMetadata is the checkpoint's map: SectorInfo carries owner_ref (a string-table ref), not a faction id, so owners stay empty.
bool read_galaxy(const P::GalaxyMetadata& galaxy, std::map<std::uint16_t, MapSectorRow>& sectors,
                 std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow>& links) {
  const auto* rows = galaxy.sectors();
  if (rows == nullptr || rows->size() == 0 || rows->size() > kMaxSectors) return false;
  for (const auto* row : *rows) {
    if (row == nullptr) return false;
    MapSectorRow sector;
    sector.index = row->index();
    sector.macro = text(row->macro());
    sector.cluster = text(row->cluster_macro());
    sector.name = text(row->name());
    if (const auto* pos = row->galaxy_pos()) {
      sector.x = pos->x();
      sector.y = pos->y();
      sector.z = pos->z();
    }
    if (!valid_sector(sector) || !sectors.emplace(sector.index, std::move(sector)).second) return false;
  }

  const auto* links_in = galaxy.links();
  if (links_in != nullptr) {
    if (links_in->size() > kMaxLinks) return false;
    for (const auto* row : *links_in) {
      if (row == nullptr) continue;
      const auto key = link_key(row->from(), row->to());
      if (key.first == key.second || !sectors.contains(key.first) || !sectors.contains(key.second)) return false;
      if (!links.emplace(key, MapLinkRow{key.first, key.second, static_cast<std::uint8_t>(row->kind())}).second) return false;
    }
  }
  return true;
}

}  // namespace

const char* source_name(MapSource source) noexcept {
  switch (source) {
    case MapSource::None: return "none";
    case MapSource::Checkpoint: return "checkpoint";
    case MapSource::Live: return "live";
  }
  return "none";
}

const char* link_kind_name(std::uint8_t kind) noexcept {
  switch (static_cast<P::LinkKind>(kind)) {
    case P::LinkKind::Gate: return "gate";
    case P::LinkKind::Highway: return "highway";
    case P::LinkKind::Accelerator: return "accelerator";
    case P::LinkKind::Other: return "other";
  }
  return "other";
}

MapHub& map_hub() {
  static MapHub hub;
  return hub;
}

bool MapHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload) {
  const auto kind = static_cast<P::MsgType>(type);
  switch (kind) {
    case P::MsgType::GalaxyMetadata: return take_galaxy(payload);
    case P::MsgType::MapSnapshot: return take_snapshot(payload);
    case P::MsgType::MapDelta: return take_delta(payload);
    default: return false;
  }
}

bool MapHub::take_snapshot(std::span<const std::uint8_t> payload) {
  if (payload.empty()) {
    ++stats_.bad;
    return true;
  }
  flatbuffers::Verifier verifier(payload.data(), payload.size());
  if (!verifier.VerifyBuffer<P::MapSnapshot>(nullptr)) {
    ++stats_.bad;
    return true;
  }
  const auto* snapshot = flatbuffers::GetRoot<P::MapSnapshot>(payload.data());
  if (snapshot->version() == 0) {
    ++stats_.bad;
    return true;
  }

  std::map<std::uint16_t, MapSectorRow> sectors;
  std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow> links;
  if (!read_snapshot(*snapshot, sectors, links)) {
    ++stats_.bad;
    return true;
  }

  sectors_ = std::move(sectors);
  links_ = std::move(links);
  version_ = snapshot->version();
  source_ = MapSource::Live;
  has_map_ = true;
  dirty_ = true;
  ++stats_.snapshots;
  return true;
}

bool MapHub::take_delta(std::span<const std::uint8_t> payload) {
  if (payload.empty()) {
    ++stats_.bad;
    return true;
  }
  flatbuffers::Verifier verifier(payload.data(), payload.size());
  if (!verifier.VerifyBuffer<P::MapDelta>(nullptr)) {
    ++stats_.bad;
    return true;
  }
  const auto* delta = flatbuffers::GetRoot<P::MapDelta>(payload.data());
  if (!has_map_ || source_ != MapSource::Live) {
    // The server always sends a snapshot first. A delta before it cannot be applied to anything.
    ++stats_.gaps;
    return true;
  }
  if (delta->version() <= version_) {
    ++stats_.stale;
    return true;
  }
  if (delta->version() != version_ + 1) {
    // Missing a version means this node would stay silently wrong. Drop the map and say so; only a snapshot can restore it.
    ++stats_.gaps;
    drop();
    dirty_ = true;
    return true;
  }

  std::map<std::uint16_t, MapSectorRow> sectors = sectors_;
  std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow> links = links_;
  std::set<std::uint16_t> seen_sectors;
  std::set<std::pair<std::uint16_t, std::uint16_t>> seen_links;
  std::uint64_t upserts = 0;
  std::uint64_t link_upserts = 0;
  std::uint64_t removed = 0;

  const auto* upsert_rows = delta->sectors();
  if (upsert_rows != nullptr) {
    if (upsert_rows->size() > kMaxSectors) {
      ++stats_.bad;
      return true;
    }
    for (const auto* row : *upsert_rows) {
      if (row == nullptr) continue;
      MapSectorRow sector;
      sector.index = row->index();
      sector.macro = text(row->macro());
      sector.cluster = text(row->cluster_macro());
      sector.name = text(row->name());
      sector.owner = text(row->owner());
      if (const auto* pos = row->galaxy_pos()) {
        sector.x = pos->x();
        sector.y = pos->y();
        sector.z = pos->z();
      }
      // An upsert may replace a sector that already exists (that is how ownership changes arrive); a repeated index in the
      // same delta is ambiguous and refused.
      if (!valid_sector(sector) || !seen_sectors.insert(sector.index).second) {
        ++stats_.bad;
        return true;
      }
      sectors.insert_or_assign(sector.index, std::move(sector));
      ++upserts;
    }
  }

  const auto* link_rows = delta->links();
  if (link_rows != nullptr) {
    if (link_rows->size() > kMaxLinks) {
      ++stats_.bad;
      return true;
    }
    for (const auto* row : *link_rows) {
      if (row == nullptr) continue;
      const auto key = link_key(row->from(), row->to());
      if (key.first == key.second || !seen_links.insert(key).second) {
        ++stats_.bad;
        return true;
      }
      links[key] = MapLinkRow{key.first, key.second, static_cast<std::uint8_t>(row->kind())};
      ++link_upserts;
    }
  }

  const auto* removed_rows = delta->removed_sectors();
  if (removed_rows != nullptr) {
    if (removed_rows->size() > kMaxSectors) {
      ++stats_.bad;
      return true;
    }
    for (const auto index : *removed_rows) {
      if (index == 0) {
        ++stats_.bad;
        return true;
      }
      if (sectors.erase(index) != 0) ++removed;
      for (auto it = links.begin(); it != links.end();) {
        it = (it->first.first == index || it->first.second == index) ? links.erase(it) : std::next(it);
      }
    }
  }

  // Every link must join two live sectors; a delta that leaves a dangling link is not applied at all.
  for (const auto& entry : links) {
    if (!sectors.contains(entry.first.first) || !sectors.contains(entry.first.second)) {
      ++stats_.bad;
      return true;
    }
  }

  sectors_ = std::move(sectors);
  links_ = std::move(links);
  version_ = delta->version();
  dirty_ = true;
  ++stats_.deltas;
  stats_.sector_upserts += upserts;
  stats_.link_upserts += link_upserts;
  stats_.sectors_removed += removed;
  return true;
}

bool MapHub::take_galaxy(std::span<const std::uint8_t> payload) {
  if (payload.empty()) {
    ++stats_.bad;
    return true;
  }
  flatbuffers::Verifier verifier(payload.data(), payload.size());
  if (!verifier.VerifyBuffer<P::GalaxyMetadata>(nullptr)) {
    ++stats_.bad;
    return true;
  }
  if (has_map_ && source_ == MapSource::Live) {
    // The checkpoint's map is the fallback; the polled live map is newer and carries faction ids.
    ++stats_.galaxy_ignored;
    return true;
  }

  const auto* galaxy = flatbuffers::GetRoot<P::GalaxyMetadata>(payload.data());
  std::map<std::uint16_t, MapSectorRow> sectors;
  std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow> links;
  if (!read_galaxy(*galaxy, sectors, links)) {
    ++stats_.bad;
    return true;
  }

  sectors_ = std::move(sectors);
  links_ = std::move(links);
  version_ = 0;
  source_ = MapSource::Checkpoint;
  has_map_ = true;
  dirty_ = true;
  ++stats_.galaxy;
  return true;
}

std::string MapHub::json() const {
  nlohmann::json out;
  out["v"] = 1;
  out["version"] = version_;
  out["source"] = source_name(source_);

  nlohmann::json sectors = nlohmann::json::array();
  for (const auto& [index, sector] : sectors_) {
    nlohmann::json row;
    row["index"] = index;
    row["macro"] = sector.macro;
    row["cluster"] = sector.cluster;
    row["name"] = sector.name;
    row["owner"] = sector.owner;
    row["x"] = sector.x;
    row["y"] = sector.y;
    row["z"] = sector.z;
    sectors.push_back(std::move(row));
  }
  out["sectors"] = std::move(sectors);

  nlohmann::json links = nlohmann::json::array();
  for (const auto& [key, link] : links_) {
    links.push_back(nlohmann::json{{"from", link.from}, {"to", link.to}, {"kind", link_kind_name(link.kind)}});
  }
  out["links"] = std::move(links);
  return out.dump();
}

bool MapHub::take_dirty() noexcept {
  const bool dirty = dirty_;
  dirty_ = false;
  return dirty;
}

void MapHub::drop() {
  sectors_.clear();
  links_.clear();
  has_map_ = false;
  version_ = 0;
  source_ = MapSource::None;
}

void MapHub::reset() {
  drop();
  dirty_ = false;
  stats_ = MapStats{};
}

}  // namespace x4mp::features::map
