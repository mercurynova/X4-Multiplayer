#pragma once
// features/selfship/galaxy_map: the node-local sector map (M3-09; docs/m3-plan.md 4.4, docs/protocol.md 8.2).
//
// The wire carries a sector as a u16 index (1-based, the rank of the sector macro in the ORDINALLY SORTED list of every sector macro, 0 =
// none). Every node maps  UniverseID (what the game calls the sector) <-> macro <-> index  from the SAME md/x4mp_galaxy.xml collection
// (control "map"), forwarded by ui/x4mp_authority.lua as the verb x4mp.sector_map:
//   {"v":1,"data":"S;<macro>|<universe id>;<macro>|<universe id>;..."}   a chunk of sectors (Lua sends at most 40 per message)
//   {"v":1,"data":"E;<count>"}                                           end marker: <count> sectors were sent in total
// The authority's GalaxyMetadata (features/authority build_plan) sorts the same way, so both sides agree without a table on the wire.
//
// Pure C++ (no SDK, no game calls). The lookups are allocation-free (they run every frame): a sorted vector and a one-entry cache.

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::features::selfship {

class GalaxyMap {
 public:
  // A collection message as above. false = not a valid message (nothing changed). A new "S;" after a completed map starts over.
  bool add_message(std::string_view data);
  void reset();

  // The end marker arrived and its count matches what was received: lookups work.
  [[nodiscard]] bool ready() const noexcept { return ready_; }
  [[nodiscard]] std::size_t size() const noexcept { return by_index_.size(); }
  [[nodiscard]] std::size_t dropped_records() const noexcept { return dropped_; }

  // 0 = unknown (or not ready).
  [[nodiscard]] std::uint16_t index_of(std::uint64_t universe_id) const noexcept;
  [[nodiscard]] std::uint64_t universe_id_of(std::uint16_t index) const noexcept;
  [[nodiscard]] std::string_view macro_of(std::uint16_t index) const noexcept;
  [[nodiscard]] std::uint16_t index_of_macro(std::string_view macro) const noexcept;

 private:
  struct Rec {
    std::string macro;
    std::uint64_t id = 0;
  };
  struct ById {
    std::uint64_t id = 0;
    std::uint16_t index = 0;
  };
  void finish();

  std::vector<Rec> pending_;  // as received
  std::size_t expected_ = 0;
  bool end_seen_ = false;
  bool ready_ = false;
  std::size_t dropped_ = 0;
  std::vector<Rec> by_index_;  // sorted by macro; index = position + 1
  std::vector<ById> by_id_;    // sorted by id
  mutable std::uint64_t cache_id_ = 0;
  mutable std::uint16_t cache_index_ = 0;
};

}  // namespace x4mp::features::selfship
