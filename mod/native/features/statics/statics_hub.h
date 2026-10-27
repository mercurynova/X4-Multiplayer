#pragma once
// features/statics: the process-wide hand-over of the checkpoint manifest (M4-17..M4-21).
//
// The manifest is the single source of static net ids (ADR-009): the client downloads the X4MF next to the save, indexes it with
// ManifestIndex, and matches the stations of its interest sectors against it. This hub is where the result lives, because two features
// need it: StaticsFeature (the matching pass) and the on-foot flow (M4-19: a station container's net id is the manifest row's id, not the
// client-side hash). Nothing here touches the game or the SDK.
//
// Frame thread only: the manifest is loaded and matched from StaticsFeature::on_frame, and the on-foot context is built from
// OnFootFeature::on_frame (both on the host's main thread), so no lock is needed.

#include <cstdint>
#include <map>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "features/statics/manifest_index.h"

namespace x4mp::features::statics {

// Per-sector matching outcome (M4-21: the ManifestReport body).
struct SectorMatch {
  std::uint32_t stations = 0;          // station rows the scan reported for this sector
  std::uint32_t matched = 0;           // ... that found a manifest row
  std::uint32_t missed = 0;            // ... that did not
  std::uint32_t ambiguous = 0;         // ... whose key names more than one manifest row (first row wins)
  std::uint32_t tiebreak_owner = 0;    // ... resolved by the owner after an ambiguous key
  std::uint32_t tiebreak_idcode = 0;   // ... resolved by the id code after an ambiguous key
  std::uint32_t universe_id_equal = 0; // diagnostic: the local component equals the manifest row's authority component_id
};

// M4-21: what the matching pass found, in the shape of protocol/schema/session.fbs ManifestReport plus the keys the report is about.
// The wire message has no per-sector list, so the per-sector part and the keys travel as log lines (LogForward) while the message
// carries the counters; both are produced from this one snapshot so they cannot disagree.
struct MatchReport {
  std::string checkpoint;        // hex of the manifest's Id128
  std::uint64_t manifest_rows = 0;
  std::uint32_t sectors = 0;     // sectors whose pass closed
  std::uint32_t stations = 0;    // station rows all those passes reported
  std::uint32_t matched = 0;
  std::uint32_t unmatched = 0;
  std::uint32_t ambiguous = 0;
  std::uint32_t tiebreak_owner = 0;
  std::uint32_t tiebreak_idcode = 0;
  std::uint32_t universe_id_equal = 0;
  std::uint32_t duration_ms = 0;
  std::vector<std::string> keys;  // up to kReportKeys unmatched match keys, in pass order
};

class StaticsHub {
 public:
  // Replaces the index (a new checkpoint) and drops every binding: the net ids of the old manifest are not this manifest's.
  void set_index(ManifestIndex index);
  void clear();

  [[nodiscard]] bool ready() const noexcept { return index_.loaded(); }
  [[nodiscard]] const ManifestIndex& index() const noexcept { return index_; }

  // M4-19: the manifest net id for a station match key (onfoot::station_match_key). 0 = no row, the caller keeps its hash fallback.
  // `idcode` (may be empty) breaks a tie between two rows with one key; without it the first row wins.
  [[nodiscard]] std::uint32_t net_id_for(std::string_view key, std::string_view idcode = {}) const;

  // M4-18: the station the scan found is bound to its manifest row.
  void bind(std::uint16_t sector, std::uint32_t net_id, std::uint64_t component);
  void unbind(std::uint32_t net_id);
  // A new universe (save load): the local components are gone, the manifest rows are not.
  void clear_bindings();
  [[nodiscard]] std::uint64_t component_of(std::uint32_t net_id) const;
  [[nodiscard]] std::size_t bound() const noexcept { return components_.size(); }
  // M4-20: after a complete pass, a station of `sector` that the pass no longer lists loses its binding (it is gone from the world).
  std::size_t prune_sector(std::uint16_t sector, const std::unordered_set<std::uint64_t>& present);

  SectorMatch& sector(std::uint16_t sector) { return sectors_[sector]; }
  [[nodiscard]] const std::map<std::uint16_t, SectorMatch>& sectors() const noexcept { return sectors_; }
  [[nodiscard]] std::uint32_t total_matched() const noexcept;
  [[nodiscard]] std::uint32_t total_missed() const noexcept;
  void reset_stats();

  // M4-21: the report of the pass set that just closed (set once per pass set by StaticsFeature).
  void set_report(MatchReport report) { report_ = std::move(report); matching_done_ = true; }
  [[nodiscard]] const MatchReport& report() const noexcept { return report_; }
  // True once a full pass set closed for the manifest that is loaded: the counters are real, not a count-only stub.
  [[nodiscard]] bool matching_done() const noexcept { return matching_done_; }
  // A new checkpoint or a new universe invalidates the report with the bindings.
  void clear_report() { report_ = MatchReport{}; matching_done_ = false; }

 private:
  ManifestIndex index_;
  std::unordered_map<std::uint32_t, std::uint64_t> components_;            // net_id -> local component
  std::unordered_map<std::uint16_t, std::unordered_set<std::uint32_t>> by_sector_;  // sector -> net ids bound there
  std::map<std::uint16_t, SectorMatch> sectors_;
  MatchReport report_;
  bool matching_done_ = false;
};

[[nodiscard]] StaticsHub& statics_hub();

}  // namespace x4mp::features::statics
