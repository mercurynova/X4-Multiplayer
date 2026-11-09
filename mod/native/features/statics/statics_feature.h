#pragma once
// features/statics: the client's manifest matching (M4-17..M4-21; docs/m4-plan.md W4, docs/mod-design.md 4.1-4.2, ADR-009).
//
// A client loads the same save as the authority, so its stations, gates and accelerators are the same objects, only their UniverseIDs
// differ. The checkpoint manifest is the single source of static net ids: the client downloads the X4MF next to the save, this feature
// indexes it (ManifestIndex) and matches the stations of its interest sectors against it by the key the on-foot flow already uses
// (onfoot::station_match_key: sector macro + station macro + position rounded to 50 m).
//
//   manifest file   avatars::avatar_hub().manifest_file() -> ManifestIndex::load (once per checkpoint)
//   station list    native -> Lua x4mp.statics_request {"v":1,"sector":"<macro>"} -> MD md/x4mp_statics.xml
//                   MD -> Lua x4mp.statics_scan "S;<macro>;<component>|<station macro>|<x>|<y>|<z>;..." / "E;<macro>;<count>"
//                   -> StaticsInbox -> this feature -> StaticsHub::bind(net_id, component)
//   consumers       the on-foot flow (M4-19: a station container's net id is the manifest row's id) and the ManifestReport (M4-21)
//
// Nothing here touches the game: the scan is MD's, the manifest is a file, the index and the matching are pure C++. A client does nothing
// before the universe is ready. The authority has no manifest (it writes one), but it runs the same scan for one reason (M5-16): a trade
// names a station by net id, and the scan is what turns that id back into a local component.

#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_set>
#include <vector>

#include "features/statics/station_scan.h"
#include "game/ghosts_api.h"
#include "host/feature.h"

namespace x4mp::features {

// M5-16: one more sector to scan. The authority asks when a trade names a station in a sector it has no ids for; a client's interest
// already drives its own pass set. Any thread may ask; the frame thread drains the queue in pump().
void request_station_scan(std::uint16_t sector);

// M4-21: starts a fresh station census (the authority's manifest rows) over these sector macros, unless one is already running or the
// galaxy map is not ready. Called from the checkpoint flow before the save is written; returns true when a census actually started.
// It never waits for the scan: the manifest is written from the rows committed so far.
[[nodiscard]] bool ensure_station_census(const std::vector<std::string>& sector_macros, std::int64_t now_us);

// M4-18: the station scan answers (x4mp.statics_scan) arrive on the platform's event thread; the frame thread drains them.
struct StaticsInbox {
  std::mutex mutex;
  std::vector<std::string> items;
  void push(std::string text);
  [[nodiscard]] std::vector<std::string> take();
};

class StaticsFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "statics"; }

  void on_init(host::HostContext& ctx) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  struct SectorState {
    std::int64_t asked_us = 0;   // 0 = never asked; the ask is retried every kAskRetryUs until the pass closes
    bool complete = false;       // the closing count of a pass arrived
    std::uint32_t count = 0;     // what that pass reported
    std::uint32_t misses_logged = 0;
    std::unordered_set<std::uint64_t> seen;  // the components of the pass in progress (M4-20 pruning)
    std::vector<std::string> missed_keys;    // bounded by kReportKeys over the whole report: the M4-21 mismatch keys
  };

  void load_manifest(host::HostContext& ctx, std::int64_t now_us);
  void pump(host::HostContext& ctx, std::int64_t now_us);
  // M4-21: drives the authority's census (one sector request at a time, bounded retries, no wait for the answers).
  void drive_census(host::HostContext& ctx, std::int64_t now_us);
  void on_batch(host::HostContext& ctx, std::uint16_t sector, const statics::StationBatch& batch);
  [[nodiscard]] bool all_complete() const;
  void publish_report(host::HostContext& ctx);

  std::unique_ptr<game::GhostsApi> api_;
  std::shared_ptr<StaticsInbox> inbox_;
  std::map<std::uint16_t, SectorState> sectors_;
  std::vector<std::uint16_t> wanted_;  // the sectors of the pass set in progress (interest, or the player's sector as fallback)
  std::string loaded_path_;   // the manifest this node loaded ("": none yet)
  std::string failed_path_;   // the manifest that failed to load, with the time, so a bad file is not re-read every frame
  std::int64_t failed_us_ = 0;
  std::int64_t first_ask_us_ = 0;  // when the pass set in progress started (M4-21 duration_ms)
  std::uint64_t asks_ = 0;
  std::uint64_t bad_ = 0;
  std::uint64_t pruned_ = 0;
  bool report_pending_ = false;  // a new pass set started: its report is published when every sector of it closed
  bool warned_no_manifest_ = false;
  bool warned_no_interest_ = false;
  bool on_authority_ = false;

  // M4-21: the census driver. `census_asked_` holds the sectors whose scan request went out but whose end marker has not come back.
  struct CensusAsk {
    std::int64_t asked_us = 0;
    std::uint32_t attempts = 0;
  };
  std::map<std::string, CensusAsk> census_asked_;
  std::uint32_t census_gen_ = 0;      // the generation this driver's bookkeeping belongs to
  std::uint64_t census_failed_ = 0;   // sectors abandoned in this generation (no answer after kCensusMaxAsks)
  bool census_was_active_ = false;    // the last pass finished: the summary line is still owed
};

}  // namespace x4mp::features
