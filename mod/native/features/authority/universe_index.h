#pragma once
// features/authority: the authority's universe index (M4-02; mod-design.md §9 "M4" sub-item 1).
//
// The authority streams only what the CaptureSet names, so it has to know which entities exist in a sector before it can schedule them. Native
// cannot enumerate a sector - there is no such exported function (docs/x4-api-notes.md §2.3) - so MD scans one sector at a time and pushes the
// ids over the bridge; this index is where those pushes land. The scheduler reads `ids_of(sector)`, the streamer reads `find(net_id)`.
//
// Rules that the tests pin:
//   - upsert is idempotent. A rescan returns every ship again, and the reference route (GetAllFactionShips) returns the same ship once per
//     hidden faction, so a known id only refreshes its row.
//   - storage is sized once from the scan's count (reserve) and never reallocates per frame.
//   - a scan is a pass: begin_scan(sector) clears the pass marks, every upsert marks its row, end_scan(sector) drops the rows of that sector
//     that were not seen (destroyed, or left the sector) and returns how many. SectorComplete may be sent only after end_scan.
//   - a ship that changes sector moves buckets; it keeps its net id, because a client that already has the ghost must not see a respawn.
//
// Pure logic: no SDK, no flatbuffers.

#include <cstddef>
#include <cstdint>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace x4mp::features::auth {

struct IndexEntry {
  std::uint32_t net_id = 0;
  std::uint16_t sector = 0;     // wire sector index, 0 = unknown
  std::uint32_t macro_ref = 0;  // string table ref of the ship macro
  std::uint32_t owner_ref = 0;  // string table ref of the owner faction
  std::uint8_t kind = 0;        // P::SpawnKind
  std::uint8_t flags = 0;       // P::SpawnFlags bits
  std::uint64_t component = 0;  // the X4 object id every pose read uses; 0 while the scan did not name one
  std::int64_t last_seen_us = 0;
  std::int64_t last_sent_us = 0;  // last WorldUpdate/EntitySpawn that carried this row
  bool seen_in_pass = false;
};

struct IndexCounters {
  std::uint32_t inserted = 0;    // new net ids
  std::uint32_t refreshed = 0;   // known net ids seen again
  std::uint32_t moved = 0;       // rows that changed sector bucket
  std::uint32_t removed = 0;     // rows dropped by end_scan
  std::uint32_t scans = 0;       // completed passes
};

struct UpsertResult {
  IndexEntry* entry = nullptr;
  bool inserted = false;
};

class UniverseIndex {
 public:
  // Size the table from the scan's count message. Never shrinks: a sector that lost ships keeps its capacity for the next pass.
  void reserve(std::size_t expected);

  // Add or refresh one row. `sector` 0 means the scan could not name a sector and the row is refused (counted, never guessed).
  UpsertResult upsert(std::uint32_t net_id, std::uint16_t sector, std::uint32_t macro_ref, std::uint32_t owner_ref, std::uint8_t kind,
                      std::uint8_t flags, std::uint64_t component, std::int64_t now_us);

  // Open a pass over one sector: every row of that sector is "not seen yet".
  void begin_scan(std::uint16_t sector);
  // Is a pass over this sector already open? A second batch of the same pass must not clear the marks of the first.
  [[nodiscard]] bool scanning(std::uint16_t sector) const;
  // Close the pass: rows of `sector` that were not seen are dropped. Returns how many.
  std::size_t end_scan(std::uint16_t sector);
  // Drop every row of one sector (it left the CaptureSet). Returns how many.
  std::size_t drop_sector(std::uint16_t sector);

  // The X4 component id -> net id binding: a scan names components, the wire names net ids. Bound on first sight and kept for the run, so a
  // rescan of the same ship refreshes its row instead of allocating a second id (which a client would see as a despawn + respawn).
  [[nodiscard]] std::uint32_t net_id_of_component(std::uint64_t component) const;
  void bind_component(std::uint64_t component, std::uint32_t net_id);

  [[nodiscard]] const IndexEntry* find(std::uint32_t net_id) const;
  [[nodiscard]] IndexEntry* find(std::uint32_t net_id);
  // The scheduler's view: the ids of one sector, stable between passes.
  [[nodiscard]] const std::vector<std::uint32_t>* ids_of(std::uint16_t sector) const;

  [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }
  [[nodiscard]] std::size_t sector_count() const noexcept { return by_sector_.size(); }
  [[nodiscard]] const IndexCounters& counters() const noexcept { return counters_; }
  [[nodiscard]] bool empty() const noexcept { return entries_.empty(); }

  // Drop everything (session end, or the server rolled the world back).
  void clear();

 private:
  std::unordered_map<std::uint32_t, IndexEntry> entries_;
  std::unordered_map<std::uint16_t, std::vector<std::uint32_t>> by_sector_;
  std::unordered_map<std::uint64_t, std::uint32_t> by_component_;
  std::unordered_set<std::uint16_t> open_;
  IndexCounters counters_;
};

}  // namespace x4mp::features::auth
