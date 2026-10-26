#pragma once
// features/ghosts: local NPC suppression (M4-13/M4-15; docs/m4-plan.md W3, docs/mod-design.md 4.3).
//
// A client in ghost mode does not simulate the world: the authority's ships arrive as ghosts, and the client's OWN copies of those ships
// must go, or every NPC would be there twice. Native cannot enumerate a sector (the same reason MD owns the authority's capture scan), so
// the client asks MD for the sector's ships over the existing scan pair (`md/x4mp_capture.xml` + `ui/x4mp_capture.lua`):
//
//   SectorComplete (S->N)  -> this module marks the sector closed and asks for a scan
//   x4mp.capture_scan      -> rows: component | macro | owner | mission
//   every 1 s              -> ask again (M4-15: a ship vanilla spawned after the entry is caught by the sweep)
//
// A row is suppressed when it is NOT the player's (`owner != "player"`), NOT a mission object, NOT in the caller's skip set (the player's own
// ship and every ghost this node spawned), and not already suppressed. Removal is the caller's guarded SafeRemove; this class only decides
// what to remove, spreads it over frames (`removals_per_frame`) and remembers what it removed, so a re-entry does not remove twice.
//
// Pure logic: no SDK, no game calls, no Lua.

#include <cstddef>
#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "features/authority/capture_ingest.h"  // auth::ScanRow: the one row format of the sector scan

namespace x4mp::features::ghosts {

struct SuppressionConfig {
  // mod-design 4.3: suppression is spread over frames at 200 objects per frame, so a sector entry never stalls a frame.
  std::size_t removals_per_frame = 200;
  // M4-15: the late-spawn sweep. One scan per second per closed sector in interest.
  std::int64_t rescan_interval_us = 1'000'000;
};

class LocalSuppression {
 public:
  struct Stats {
    std::uint64_t requests = 0;         // scan requests raised
    std::uint64_t scans = 0;            // scan answers received
    std::uint64_t rows = 0;             // rows seen in those answers
    std::uint64_t queued = 0;           // rows accepted for removal
    std::uint64_t removed = 0;          // removals handed to the caller
    std::uint64_t skipped_player = 0;   // owner == "player"
    std::uint64_t skipped_mission = 0;  // a mission object
    std::uint64_t skipped_known = 0;    // the caller's skip set: the player's ship or one of our ghosts
    std::uint64_t skipped_dup = 0;      // already suppressed
  };

  using SkipFn = std::function<bool(std::uint64_t component)>;
  using AskFn = std::function<void(std::uint16_t sector)>;      // ask MD for the sector's ships
  using RemoveFn = std::function<void(std::uint64_t component)>;  // SafeRemove, guarded by the player guard

  explicit LocalSuppression(SuppressionConfig cfg = {}) : cfg_(cfg) {}

  // The server closed `sector` for this client: it may be suppressed, and a scan is due now.
  void on_sector_closed(std::uint16_t sector);
  // M4-15: the client's interest set. Only a closed sector that is still in interest is swept; the suppressed ids are kept, so a re-entry
  // never removes the same object twice.
  void on_interest(std::span<const std::uint16_t> sectors);
  // One scan answer for `sector` (a chunk or the closing count; the closing count carries no rows).
  void on_scan(std::uint16_t sector, std::span<const auth::ScanRow> rows, const SkipFn& skip);
  // One frame: ask the sectors whose scan is due, then remove up to removals_per_frame queued ids.
  void frame(std::int64_t now_us, const AskFn& ask, const RemoveFn& remove);

  [[nodiscard]] const Stats& stats() const noexcept { return stats_; }
  [[nodiscard]] std::size_t suppressed_of(std::uint16_t sector) const;
  [[nodiscard]] std::size_t total_suppressed() const;
  [[nodiscard]] std::size_t pending_removals() const noexcept { return queue_.size() - queue_head_; }
  [[nodiscard]] bool closed(std::uint16_t sector) const;
  void clear();

 private:
  struct SectorState {
    bool closed = false;
    std::int64_t last_ask_us = -1;  // -1 = never asked
    std::unordered_set<std::uint64_t> suppressed;
  };

  SuppressionConfig cfg_;
  std::unordered_map<std::uint16_t, SectorState> sectors_;
  std::unordered_set<std::uint16_t> interest_;
  std::vector<std::uint64_t> queue_;
  std::size_t queue_head_ = 0;
  Stats stats_;
};

}  // namespace x4mp::features::ghosts
