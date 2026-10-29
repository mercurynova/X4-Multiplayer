#include "features/statics/statics_feature.h"

#include <chrono>
#include <filesystem>
#include <format>
#include <fstream>
#include <iterator>
#include <mutex>
#include <unordered_set>
#include <utility>

#include "features/avatars/avatar_hub.h"
#include "features/diag/diag_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "features/onfoot/onfoot_types.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "features/statics/manifest_index.h"
#include "features/statics/statics_hub.h"
#include "nlohmann/json.hpp"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
// A sector whose pass never closed is asked again: MD may have dropped the request (no UI, a reload in between).
constexpr std::int64_t kAskRetryUs = 10'000'000;
// A manifest that does not read (still downloading, or not an X4MF) is not re-read every frame.
constexpr std::int64_t kLoadRetryUs = 5'000'000;
// A real save has a handful of misses; a broken key would flood the log, so the per-sector list is capped.
constexpr std::uint32_t kMissesLoggedPerSector = 12;
// M4-21: the report carries up to this many mismatch keys (protocol.md 8.4: "up to N"), and the log gets the same ones.
constexpr std::size_t kReportKeys = 16;
// The scan answer is a text message of a few hundred bytes per chunk; anything near this is not one of ours.
constexpr std::size_t kMaxScanBytes = 512 * 1024;

std::int64_t steady_us() {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// M5-16: sectors the authority asked for between two frames. A handful is plenty; a flood is capped so a bad caller cannot grow it.
std::mutex g_request_mutex;
std::unordered_set<std::uint16_t> g_requested;

std::vector<std::uint16_t> take_requested() {
  const std::lock_guard lock(g_request_mutex);
  std::vector<std::uint16_t> out(g_requested.begin(), g_requested.end());
  g_requested.clear();
  return out;
}
}  // namespace

void request_station_scan(std::uint16_t sector) {
  if (sector == 0) return;
  const std::lock_guard lock(g_request_mutex);
  if (g_requested.size() < 64) g_requested.insert(sector);
}

void StaticsInbox::push(std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= 64) items.erase(items.begin());  // a stuck frame thread must not grow this without bound
  items.push_back(std::move(text));
}

std::vector<std::string> StaticsInbox::take() {
  const std::lock_guard lock(mutex);
  return std::exchange(items, {});
}

void StaticsFeature::on_init(host::HostContext& ctx) {
  auto get = [&ctx](const char* name) { return ctx.platform.get_game_function(name); };
  api_ = std::make_unique<game::GhostsApi>(ctx.game, get);
  inbox_ = std::make_shared<StaticsInbox>();
  auto inbox = inbox_;
  const bool ok = ctx.platform.subscribe_event("x4mp.statics_scan", [inbox](std::string_view text) {
    if (text.size() <= kMaxScanBytes) inbox->push(std::string(text));
  });
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: init (bridge verb x4mp.statics_scan {})", ok ? "subscribed" : "not available");
}

void StaticsFeature::on_game_loaded(host::HostContext& ctx) {
  // A new universe: every local component id is gone. The manifest file and the index are per checkpoint, so they stay until the file
  // itself changes (load_manifest compares the path).
  sectors_.clear();
  wanted_.clear();
  report_pending_ = false;
  first_ask_us_ = 0;
  statics::statics_hub().clear_bindings();
  statics::statics_hub().clear_report();
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: a new universe was loaded: station bindings dropped ({} manifest rows kept)",
            statics::statics_hub().index().size());
}

void StaticsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  if (!inbox_) return;
  on_authority_ = diag_hub().role() == NodeRole::Authority;
  if (!info.universe_ready) return;

  const std::int64_t now = steady_us();
  // The authority has no manifest file (it writes one, it never reads one): its station ids are the shared match-key hash. It still needs
  // the scan, because a trade names a station by net id and only the scan turns that back into a local component (M5-16).
  if (!on_authority_) load_manifest(ctx, now);
  pump(ctx, now);
}

void StaticsFeature::load_manifest(host::HostContext& ctx, std::int64_t now_us) {
  const std::string& path = avatars::avatar_hub().manifest_file();
  if (path.empty()) {
    if (!warned_no_manifest_) {
      warned_no_manifest_ = true;
      X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: waiting for the checkpoint manifest (the join feature sets its path from the save info)");
    }
    return;
  }
  if (path == loaded_path_) return;
  if (path == failed_path_ && now_us - failed_us_ < kLoadRetryUs) return;

  std::ifstream file(std::filesystem::path(path), std::ios::binary);
  if (!file) {
    failed_path_ = path;
    failed_us_ = now_us;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "statics: cannot open the manifest '{}' (still downloading?)", path);
    return;
  }
  const std::vector<std::uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
  statics::ManifestIndex index;
  if (!index.load(bytes)) {
    failed_path_ = path;
    failed_us_ = now_us;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "statics: '{}' is not a readable X4MF manifest ({} bytes); station matching stays off", path, bytes.size());
    return;
  }
  const statics::ManifestStats stats = index.stats();
  const std::string checkpoint = std::string(index.checkpoint());
  const std::size_t rows = index.size();
  statics::statics_hub().set_index(std::move(index));
  loaded_path_ = path;
  failed_path_.clear();
  // A new manifest means new net ids: the passes of the old one are not this one's, so every sector is asked again.
  sectors_.clear();
  wanted_.clear();
  report_pending_ = false;
  first_ask_us_ = 0;
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info,
            "statics: manifest {} loaded from '{}': {} rows indexed of {} entries ({} without a macro, {} without a sector, {} "
            "collisions); matching starts with the next station scan",
            checkpoint, path, rows, stats.entries, stats.no_macro, stats.no_sector, stats.collisions);
}

void StaticsFeature::pump(host::HostContext& ctx, std::int64_t now_us) {
  const selfship::GalaxyMap* map = selfship::selfship_hub().map();

  // M4-18: the scan answers. A chunk carries rows, the closing count carries the total of the pass.
  for (std::string& text : inbox_->take()) {
    const auto batch = statics::parse_station_batch(text);
    if (!batch) {
      ++bad_;
      continue;
    }
    const std::uint16_t sector = map != nullptr ? map->index_of_macro(batch->sector_macro) : std::uint16_t{0};
    if (sector == 0) {
      ++bad_;  // the galaxy map has no row for this macro yet; the sector is asked again
      continue;
    }
    on_batch(ctx, sector, *batch);
  }

  // Which sectors to scan: a client matches its interest (InterestUpdate, W3/W5); the authority scans what a trade asked for plus the
  // sector its own player is in. Until the server sends an interest update, the player's sector is the honest fallback, so a
  // single-sector session still matches its stations.
  const auto own_sector = [&]() -> std::uint16_t {
    if (map == nullptr || !api_) return 0;
    const auto ship = api_->local_ship();
    const auto universe = ship != 0 ? api_->sector_of(ship) : 0;
    return universe != 0 ? map->index_of(universe) : std::uint16_t{0};
  };

  std::vector<std::uint16_t> wanted;
  if (on_authority_) {
    wanted = take_requested();
    for (const std::uint16_t sector : wanted) {
      // An explicitly requested sector is scanned again, unless the last pass is still fresh (kAskRetryUs).
      SectorState& state = sectors_[sector];
      if (state.asked_us == 0 || now_us - state.asked_us >= kAskRetryUs) state.complete = false;
    }
    if (const std::uint16_t own = own_sector(); own != 0) wanted.push_back(own);
  } else {
    const ghosts::GhostCore* core = ghosts::ghost_hub().core();
    if (core != nullptr) wanted = core->interest_sectors();
    if (wanted.empty()) {
      const std::uint16_t sector = own_sector();
      if (sector != 0) {
        if (!warned_no_interest_) {
          warned_no_interest_ = true;
          X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: no InterestUpdate yet: matching the sector the player is in (index {})", sector);
        }
        wanted.push_back(sector);
      }
    }
  }
  wanted_ = wanted;

  for (const std::uint16_t sector : wanted) {
    if (sector == 0) continue;
    SectorState& state = sectors_[sector];
    if (state.complete) continue;
    if (state.asked_us != 0 && now_us - state.asked_us < kAskRetryUs) continue;
    const std::string_view macro = map != nullptr ? map->macro_of(sector) : std::string_view{};
    if (macro.empty()) continue;
    nlohmann::json j;
    j["v"] = 1;
    j["sector"] = std::string(macro);
    if (!ctx.platform.raise_lua("x4mp.statics_request", j.dump())) continue;
    // A new pass: its counts are its own, and so are the components it reports.
    statics::statics_hub().sector(sector) = statics::SectorMatch{};
    state.seen.clear();
    state.missed_keys.clear();
    state.misses_logged = 0;
    state.asked_us = now_us;
    ++asks_;
    if (!on_authority_ && !report_pending_) {
      report_pending_ = true;  // M4-21: this pass set owes a report; it is published once every sector of it closed
      first_ask_us_ = now_us;
    }
  }
}

void StaticsFeature::on_batch(host::HostContext& ctx, std::uint16_t sector, const statics::StationBatch& batch) {
  SectorState& state = sectors_[sector];
  statics::SectorMatch& match = statics::statics_hub().sector(sector);
  const statics::ManifestIndex& index = statics::statics_hub().index();

  for (const statics::StationRow& row : batch.rows) {
    // The very key the on-foot flow uses (onfoot::station_match_key): sector macro, station macro, position rounded to 50 m.
    const std::string key = onfoot::station_match_key(batch.sector_macro, row.macro, row.x, row.y, row.z);
    const statics::StaticRow* hit = index.loaded() ? index.lookup(key, row.idcode) : nullptr;
    // M5-16: every station gets an id this node can name on the wire, matched or not: the manifest row's id, else the M3 hash of the
    // same key (the fallback the on-foot flow uses). Without a manifest (the authority) it is always the hash.
    statics::statics_hub().note_station(sector, row.component, hit != nullptr ? hit->net_id : onfoot::matched_container_id(key),
                                       batch.sector_macro, false, row.owner);
    state.seen.insert(row.component);
    if (!index.loaded()) continue;  // no manifest on this node: there is nothing to match, only ids to note
    ++match.stations;
    if (hit == nullptr) {
      ++match.missed;
      // M4-21: the keys of the report, bounded over the whole pass set, and the first few of them also logged per sector.
      if (state.missed_keys.size() < kReportKeys) state.missed_keys.push_back(key);
      if (state.misses_logged < kMissesLoggedPerSector) {
        ++state.misses_logged;
        X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "statics: no manifest row for station {} in {} (key '{}', component 0x{:x})", row.macro,
                  batch.sector_macro, key, row.component);
      }
      continue;
    }
    ++match.matched;
    // M4-18 tie-break accounting: an ambiguous key is matched (a row won) but reported as ambiguous, and an id code that picked the
    // row is counted separately. The owner tie-break cannot be decided from the scan (it carries no owner), so it stays 0.
    if (index.ambiguous(key)) {
      ++match.ambiguous;
      if (!row.idcode.empty() && row.idcode == hit->idcode) ++match.tiebreak_idcode;
    }
    if (hit->component_id != 0 && hit->component_id == row.component) ++match.universe_id_equal;
    statics::statics_hub().bind(sector, hit->net_id, row.component);
  }

  if (!batch.end) return;
  state.complete = true;
  state.count = batch.count;
  // M4-20: a station of this sector the pass no longer lists is gone from the world, so its binding goes too.
  pruned_ += statics::statics_hub().prune_sector(sector, state.seen);
  const std::size_t dropped_ids = statics::statics_hub().prune_stations(sector, state.seen);
  state.seen.clear();
  if (index.loaded()) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: {} closed: {} stations, {} matched, {} missed, {} bound in total", batch.sector_macro,
              match.stations, match.matched, match.missed, statics::statics_hub().bound());
  } else {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: {} closed: {} stations, {} ids known ({} dropped), no manifest on this node",
              batch.sector_macro, batch.count, statics::statics_hub().stations_known(), dropped_ids);
  }
  // M4-21: the report of a pass set goes out once every sector of it closed, so its counters are complete.
  if (index.loaded() && report_pending_ && all_complete()) {
    report_pending_ = false;
    publish_report(ctx);
  }
}

bool StaticsFeature::all_complete() const {
  if (wanted_.empty()) return false;
  for (const std::uint16_t sector : wanted_) {
    const auto it = sectors_.find(sector);
    if (it == sectors_.end() || !it->second.complete) return false;
  }
  return true;
}

void StaticsFeature::publish_report(host::HostContext& ctx) {
  statics::StaticsHub& hub = statics::statics_hub();
  statics::MatchReport rep;
  rep.checkpoint = std::string(hub.index().checkpoint());
  rep.manifest_rows = hub.index().size();
  for (const auto& [sector, m] : hub.sectors()) {
    (void)sector;
    if (m.stations == 0) continue;  // a sector asked but never reported is not part of this pass set
    ++rep.sectors;
    rep.stations += m.stations;
    rep.matched += m.matched;
    rep.unmatched += m.missed;
    rep.ambiguous += m.ambiguous;
    rep.tiebreak_owner += m.tiebreak_owner;
    rep.tiebreak_idcode += m.tiebreak_idcode;
    rep.universe_id_equal += m.universe_id_equal;
  }
  for (const auto& [sector, state] : sectors_) {
    (void)sector;
    for (const std::string& key : state.missed_keys) {
      if (rep.keys.size() >= kReportKeys) break;
      rep.keys.push_back(key);
    }
    if (rep.keys.size() >= kReportKeys) break;
  }
  rep.duration_ms = first_ask_us_ != 0 ? static_cast<std::uint32_t>((steady_us() - first_ask_us_) / 1000) : 0;

  const std::string summary = std::format(
      "[statics] manifest report {}: {}/{} stations matched over {} sector(s) ({} unmatched, {} ambiguous, {} by id code, {} same "
      "UniverseID, {} manifest rows, {} pruned, {} ms)",
      rep.checkpoint, rep.matched, rep.stations, rep.sectors, rep.unmatched, rep.ambiguous, rep.tiebreak_idcode, rep.universe_id_equal,
      rep.manifest_rows, pruned_, rep.duration_ms);
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "{}", summary);
  diag_hub().forward_log(Level::Info, summary);  // M4-21 AC: the server log carries this client's match report

  // Per-sector detail and the mismatch keys: the report body has no room for them, so they travel as log lines (protocol.md 8.4).
  for (const auto& [sector, m] : hub.sectors()) {
    if (m.stations == 0) continue;
    const std::string line = std::format("[statics] sector {}: {} stations, {} matched, {} missed, {} ambiguous", sector, m.stations,
                                         m.matched, m.missed, m.ambiguous);
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "{}", line);
    diag_hub().forward_log(Level::Info, line);
  }
  for (const std::string& key : rep.keys) {
    const std::string line = "[statics] unmatched key '" + key + "'";
    X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "{}", line);
    diag_hub().forward_log(Level::Warn, line);  // reproducible from the log alone (M4-21)
  }
  hub.set_report(std::move(rep));
}

void StaticsFeature::on_shutdown(host::HostContext& ctx) {
  const statics::StaticsHub& hub = statics::statics_hub();
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "statics: shutdown (manifest {} rows, {} stations bound, {} ids known, {} matched, {} missed, {} asks, {} bad)",
            hub.index().size(), hub.bound(), hub.stations_known(), hub.total_matched(), hub.total_missed(), asks_, bad_);
}

}  // namespace x4mp::features
