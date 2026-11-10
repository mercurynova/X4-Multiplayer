#pragma once
// core/authority/map_messages: FlatBuffers encoders for the live strategic map (T1.6).
//
//   KnownSectors  A->S  the authority's live map (sectors + gate links), sent after welcome and on change
//   MapSnapshot   S->N  the full map right after Welcome (and after a reset)
//   MapDelta      S->N  sector/link upserts and removed sectors
//
// The live map is independent of GalaxyMetadata: it is polled from the running universe (MD cue `map_data`),
// so a sector's owner can change without a new save. `owner` is the game's faction id string, not a string-table
// ref, so a map screen never depends on StringTableAdd delivery.

#include <cstdint>
#include <span>
#include <string>
#include <vector>

#include "core/authority/checkpoint_messages.h"  // Payload

namespace x4mp::authority {

struct MapSectorDesc {
  std::uint16_t index = 0;  // 1-based, the deterministic rank of the sector macro (same as SectorInfo.index)
  std::string macro;
  std::string cluster_macro;
  std::string name;
  std::string owner;        // faction id ("argon", "xenon", ...), empty = unowned
  float x = 0, y = 0, z = 0;
};

struct MapLinkDesc {
  std::uint16_t from = 0;
  std::uint16_t to = 0;
  std::uint8_t kind = 0;  // LinkKind::Gate
};

struct MapPlan {
  std::vector<MapSectorDesc> sectors;
  std::vector<MapLinkDesc> links;
};

[[nodiscard]] Payload encode_known_sectors(std::uint64_t revision, const MapPlan& map);
[[nodiscard]] Payload encode_map_snapshot(std::uint64_t version, const MapPlan& map);
// `upserts` and `links` are only the changed entries; `removed` are sector indices deleted together with their links.
[[nodiscard]] Payload encode_map_delta(std::uint64_t version, const std::vector<MapSectorDesc>& upserts,
                                       const std::vector<MapLinkDesc>& links, std::span<const std::uint16_t> removed);

// Content hash used by the authority to send KnownSectors only when the map actually changed.
[[nodiscard]] std::uint64_t map_hash(const MapPlan& map) noexcept;

}  // namespace x4mp::authority
