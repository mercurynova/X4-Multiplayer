#pragma once
// features/map/map_hub: what this node knows about the shared strategic map (T1.6).
//
// The map is not the tactical CaptureSet: a client must not have to visit a sector to know who owns it. The authority polls its
// running universe (MD cue `map_data`), sends KnownSectors to the server, and the server fans out:
//
//   GalaxyMetadata  S->N  the checkpoint's map, forwarded on attach and on every new galaxy. It has no faction id (only a
//                         string-table ref), so this hub uses it only as a fallback while no live map has arrived: owners are empty.
//   MapSnapshot     S->N  the full live map (attach, first map, a link removed between two live sectors)
//   MapDelta        S->N  sector/link upserts and removed sectors; version is the previous version + 1
//
// The live map wins over the checkpoint: once a snapshot has arrived, GalaxyMetadata is ignored. A delta without a map or with a
// version gap would leave the node silently divergent, so it is refused and the map is dropped (the UI says so); the next attach
// sends a fresh snapshot.
//
// Pure logic: no SDK, no game calls, no IO. Main thread only (the join feature drains the session events in on_frame).

#include <cstddef>
#include <cstdint>
#include <map>
#include <span>
#include <string>
#include <utility>

namespace x4mp::features::map {

enum class MapSource : std::uint8_t {
  None,        // no map at all
  Checkpoint,  // GalaxyMetadata only: owners are empty
  Live,        // MapSnapshot / MapDelta
};

[[nodiscard]] const char* source_name(MapSource source) noexcept;
// "gate", "highway", "accelerator", "other" (LinkKind as the wire carries it).
[[nodiscard]] const char* link_kind_name(std::uint8_t kind) noexcept;

struct MapSectorRow {
  std::uint16_t index = 0;  // 1-based, the deterministic rank of the sector macro
  std::string macro;
  std::string cluster;
  std::string name;
  std::string owner;  // faction id ("argon", "xenon", ...), empty = unowned
  float x = 0, y = 0, z = 0;
};

struct MapLinkRow {
  std::uint16_t from = 0;  // normalized: from < to
  std::uint16_t to = 0;
  std::uint8_t kind = 0;
};

struct MapStats {
  std::uint64_t snapshots = 0;      // MapSnapshot taken
  std::uint64_t deltas = 0;         // MapDelta applied
  std::uint64_t galaxy = 0;         // GalaxyMetadata taken as the checkpoint fallback
  std::uint64_t galaxy_ignored = 0; // GalaxyMetadata seen while a live map exists
  std::uint64_t stale = 0;          // a delta at or below the current version
  std::uint64_t gaps = 0;           // a delta that could not follow the current map (map dropped)
  std::uint64_t bad = 0;            // a payload that was not the message it claimed to be
  std::uint64_t sector_upserts = 0;
  std::uint64_t link_upserts = 0;
  std::uint64_t sectors_removed = 0;
};

class MapHub {
 public:
  // One session frame. Returns true when `type` is a map message (even one that was refused: it is still ours).
  bool on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload);

  [[nodiscard]] bool has_map() const noexcept { return has_map_; }
  [[nodiscard]] std::uint64_t version() const noexcept { return version_; }
  [[nodiscard]] MapSource source() const noexcept { return source_; }
  [[nodiscard]] std::size_t sector_count() const noexcept { return sectors_.size(); }
  [[nodiscard]] std::size_t link_count() const noexcept { return links_.size(); }
  [[nodiscard]] const std::map<std::uint16_t, MapSectorRow>& sectors() const noexcept { return sectors_; }
  [[nodiscard]] const std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow>& links() const noexcept { return links_; }
  [[nodiscard]] const MapStats& stats() const noexcept { return stats_; }

  // The topic payload for ui/x4mp_map.lua:
  //   {"v":1,"version":N,"source":"live"|"checkpoint"|"none","sectors":[{index,macro,cluster,name,owner,x,y,z}],
  //    "links":[{from,to,kind}]}
  // Sectors and links are ordered by index/pair, so the same map always renders in the same order.
  [[nodiscard]] std::string json() const;

  // The map changed since the last call (the join feature raises x4mp.map once per changed frame).
  bool take_dirty() noexcept;

  void reset();

 private:
  bool take_snapshot(std::span<const std::uint8_t> payload);
  bool take_delta(std::span<const std::uint8_t> payload);
  bool take_galaxy(std::span<const std::uint8_t> payload);
  void drop();

  bool has_map_ = false;
  std::uint64_t version_ = 0;
  MapSource source_ = MapSource::None;
  std::map<std::uint16_t, MapSectorRow> sectors_;
  std::map<std::pair<std::uint16_t, std::uint16_t>, MapLinkRow> links_;
  bool dirty_ = false;
  MapStats stats_;
};

[[nodiscard]] MapHub& map_hub();

}  // namespace x4mp::features::map
