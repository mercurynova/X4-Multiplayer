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

  // ---- M5-16: the local station id table ----
  // Every station the scan reported, matched to a manifest row or not, so this node can translate a trade line's local component into
  // the net id the wire carries and back. The net id is the manifest row's id when there is one; without a row it is the M3 container
  // hash of the very same match key (onfoot::matched_container_id), which every node computes identically - the fallback the on-foot
  // flow already uses (statics_hub.h:62). Unlike `bind`, this is not a matching claim: it carries no report counters.
  void note_station(std::uint16_t sector, std::uint64_t component, std::uint32_t net_id, std::string sector_macro = {},
                    bool pinned = false, std::string owner = {});
  // M5-19: the authority assigned this id (a built station's EntitySpawn/IntentResult). A pinned id survives later scans: the
  // hash fallback must not take it back.
  void pin_station(std::uint16_t sector, std::uint64_t component, std::uint32_t net_id, std::string sector_macro = {}) {
    note_station(sector, component, net_id, std::move(sector_macro), true);
  }
  [[nodiscard]] std::uint32_t station_net_id(std::uint64_t component) const;
  [[nodiscard]] std::uint64_t station_component(std::uint32_t net_id) const;
  // M5-18: the sector macro a station was scanned in (empty when this node never named it).
  [[nodiscard]] std::string sector_macro_of(std::uint64_t component) const;
  // M5-17: the faction id the scan read for this station (empty when MD could not read it, or the node never scanned it).
  [[nodiscard]] std::string owner_of(std::uint64_t component) const;
  [[nodiscard]] std::size_t stations_known() const noexcept { return station_by_component_.size(); }
  // M5-16: a station this pass no longer lists is gone from this universe.
  std::size_t prune_stations(std::uint16_t sector, const std::unordered_set<std::uint64_t>& present);
  void clear_stations();

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

  // ---- M4-21: the authority's station census (the manifest's static rows) ----
  // The authority scans every sector once and keeps the rows here; a checkpoint manifest is written from a snapshot of them
  // (authority_flow builds the rows just before encoding, so an interrupted pass still exports what it has).
  struct CensusRow {
    std::uint32_t net_id = 0;
    std::uint64_t component = 0;
    std::string sector_macro;
    std::string macro;
    std::string owner;
    double x = 0, y = 0, z = 0;
  };
  // Opens a new pass over these sector macros (a new generation). Rows committed by earlier passes stay: a sector whose scan never
  // answers keeps its last known rows instead of losing them.
  void start_census(std::vector<std::string> sector_macros);
  [[nodiscard]] bool census_active() const noexcept { return census_active_; }
  [[nodiscard]] std::uint32_t census_generation() const noexcept { return census_generation_; }
  [[nodiscard]] const std::unordered_set<std::string>& census_open() const noexcept { return census_open_; }
  [[nodiscard]] std::size_t census_open_count() const noexcept { return census_open_.size(); }
  // The scan of this sector starts: its staging rows are dropped (a retry must not double them).
  void census_begin_sector(const std::string& sector_macro);
  // One station of a sector scan. Into staging while the sector is open, straight into the committed set otherwise. A component seen
  // twice in one pass counts once.
  void census_add(const std::string& sector_macro, const CensusRow& row);
  // The end marker of a sector scan: staging replaces that sector's committed rows.
  void census_close_sector(const std::string& sector_macro);
  // The scan never answered: the staged rows are dropped and the sector's earlier rows stay.
  void census_abandon_sector(const std::string& sector_macro);
  [[nodiscard]] std::size_t census_size() const;
  [[nodiscard]] std::size_t census_sectors() const;
  [[nodiscard]] std::vector<CensusRow> census_rows() const;
  void clear_census();

 private:
  struct CensusSector {
    std::vector<CensusRow> rows;
    std::unordered_set<std::uint64_t> components;
  };
  ManifestIndex index_;
  std::unordered_map<std::uint32_t, std::uint64_t> components_;            // net_id -> local component
  std::unordered_map<std::uint16_t, std::unordered_set<std::uint32_t>> by_sector_;  // sector -> net ids bound there
  std::map<std::uint16_t, SectorMatch> sectors_;
  MatchReport report_;
  bool matching_done_ = false;

  // M5-16: every scanned station, matched or not (see note_station). Kept apart from the bindings above so the M4 report keeps its
  // meaning: a station without a manifest row is still "missed" in the report, yet its id is known here.
  struct LocalStation {
    std::uint32_t net_id = 0;
    std::uint16_t sector = 0;
    std::string sector_macro;  // M5-18: what the scan called the sector (the wire index is `sector`)
    bool pinned = false;       // M5-19: the authority assigned this id; a scan's hash id must not replace it
    std::string owner;         // M5-17: the station's owner faction as the scan read it; empty when unknown
  };
  std::unordered_map<std::uint64_t, LocalStation> station_by_component_;
  std::unordered_map<std::uint32_t, std::uint64_t> component_by_station_;
  std::unordered_map<std::uint16_t, std::unordered_set<std::uint64_t>> stations_by_sector_;

  // M4-21: the census. `census_` holds the rows of finished scans (what the manifest reads), `census_staging_` the rows of the scans
  // still in flight; closing a sector moves its staging set over.
  std::map<std::string, CensusSector> census_;
  std::map<std::string, CensusSector> census_staging_;
  std::unordered_set<std::string> census_open_;
  std::uint32_t census_generation_ = 0;
  bool census_active_ = false;
};

[[nodiscard]] StaticsHub& statics_hub();

}  // namespace x4mp::features::statics
