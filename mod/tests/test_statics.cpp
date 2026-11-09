// M4 W4: the station scan text, the checkpoint manifest index and the matching hub (M4-17..M4-21).
//
// The manifest is built with the generated FlatBuffers writers, exactly the shape the authority uploads, so the reader is tested against
// the real file format rather than a hand-rolled blob.
#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include <flatbuffers/flatbuffers.h>

#include "common_generated.h"
#include "manifest_generated.h"
#include "session_generated.h"

#include "core/authority/checkpoint_messages.h"
#include "features/authority/capture_ingest.h"
#include "features/onfoot/onfoot_types.h"
#include "features/statics/manifest_index.h"
#include "features/statics/station_scan.h"
#include "features/statics/statics_hub.h"

namespace S = x4mp::features::statics;
namespace O = x4mp::features::onfoot;
namespace A = x4mp::features::auth;
namespace P = X4MP::Proto;

namespace {

struct RowSpec {
  std::uint32_t net_id = 0;
  const char* sector_macro = "";
  const char* macro = "";
  float x = 0, y = 0, z = 0;
  const char* idcode = "";
  std::uint64_t component = 0;
};

// A minimal but valid X4MF: two reference tables and the entries that point into them (protocol/schema/manifest.fbs).
std::vector<std::uint8_t> build_manifest(const std::vector<RowSpec>& rows) {
  flatbuffers::FlatBufferBuilder fbb(4096);
  std::vector<std::string> sectors;
  std::vector<std::string> macros;
  for (const RowSpec& r : rows) {
    if (std::find(sectors.begin(), sectors.end(), r.sector_macro) == sectors.end()) sectors.emplace_back(r.sector_macro);
    if (std::find(macros.begin(), macros.end(), r.macro) == macros.end()) macros.emplace_back(r.macro);
  }
  const auto index_of = [](const std::vector<std::string>& list, const char* value) {
    return static_cast<std::uint32_t>(std::distance(list.begin(), std::find(list.begin(), list.end(), value)) + 1);
  };

  std::vector<flatbuffers::Offset<P::StringEntry>> strings;
  strings.reserve(macros.size());
  for (std::size_t i = 0; i < macros.size(); ++i) {
    strings.push_back(P::CreateStringEntryDirect(fbb, static_cast<std::uint32_t>(i + 1), P::StringKind::Generic, macros[i].c_str()));
  }
  std::vector<flatbuffers::Offset<P::SectorInfo>> sector_rows;
  sector_rows.reserve(sectors.size());
  for (std::size_t i = 0; i < sectors.size(); ++i) {
    sector_rows.push_back(P::CreateSectorInfoDirect(fbb, static_cast<std::uint16_t>(i + 1), sectors[i].c_str()));
  }
  std::vector<flatbuffers::Offset<P::ManifestEntry>> entries;
  entries.reserve(rows.size());
  for (const RowSpec& r : rows) {
    const P::Vec3f pos(r.x, r.y, r.z);
    entries.push_back(P::CreateManifestEntryDirect(fbb, r.net_id, P::EntityKind::Station, r.component,
                                                   index_of(macros, r.macro), 0, 0, 0,
                                                   static_cast<std::uint16_t>(index_of(sectors, r.sector_macro)), r.idcode, &pos));
  }
  const P::Id128 checkpoint(0x1122334455667788ull, 0x99aabbccddeeff00ull);  // lo, hi
  P::FinishManifestBuffer(fbb, P::CreateManifestDirect(fbb, &checkpoint, 0.0, 100, &strings, &sector_rows, &entries));
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

S::ManifestIndex loaded_index(const std::vector<RowSpec>& rows) {
  S::ManifestIndex index;
  const auto bytes = build_manifest(rows);
  REQUIRE(index.load(bytes));
  return index;
}

}  // namespace

// ------------------------------------------------------------------------------------------------ scan text

TEST_CASE("statics: the scan text becomes station rows", "[statics][m418]") {
  const auto batch = S::parse_station_batch(
      "S;sec_a_macro;0x18ead5|station_a_macro|1200.4|3400.2|-50.1;0x18ead6|station_b_macro|0|0|0|TELADI-01");
  REQUIRE(batch.has_value());
  CHECK(batch->sector_macro == "sec_a_macro");
  CHECK_FALSE(batch->end);
  REQUIRE(batch->rows.size() == 2);
  CHECK(batch->rows[0].component == 0x18ead5u);  // MD prints hex, the capture scan already taught us that
  CHECK(batch->rows[0].macro == "station_a_macro");
  CHECK(batch->rows[0].x == 1200.4);
  CHECK(batch->rows[0].y == 3400.2);
  CHECK(batch->rows[0].z == -50.1);
  CHECK(batch->rows[0].idcode.empty());
  CHECK(batch->rows[1].idcode == "TELADI-01");  // the optional M4-18 tie-break field

  const auto end = S::parse_station_batch("E;sec_a_macro;2");
  REQUIRE(end.has_value());
  CHECK(end->end);
  CHECK(end->count == 2);
  CHECK(end->rows.empty());
}

TEST_CASE("statics: text that is not a scan is refused, not half-read", "[statics][m418]") {
  CHECK_FALSE(S::parse_station_batch("").has_value());
  CHECK_FALSE(S::parse_station_batch("X;sec_a_macro;1").has_value());
  CHECK_FALSE(S::parse_station_batch("S;;1|m|0|0|0").has_value());   // no sector macro
  CHECK_FALSE(S::parse_station_batch("E;;3").has_value());            // no sector macro
  // A row with component 0 is not an object: dropped, but the batch itself is still a valid scan answer.
  const auto zero = S::parse_station_batch("S;sec;0|m|0|0|0");
  REQUIRE(zero.has_value());
  CHECK(zero->rows.empty());

  // One bad row is dropped, its neighbours stay.
  const auto batch = S::parse_station_batch("S;sec;1|m|0|0|0;bad|row;2|m|1|2|3");
  REQUIRE(batch.has_value());
  REQUIRE(batch->rows.size() == 2);
  CHECK(batch->rows[0].component == 1u);
  CHECK(batch->rows[1].component == 2u);
}

TEST_CASE("statics: the bridge envelope is unwrapped before the scan is parsed", "[statics][m418][bridge]") {
  // The platform event carries the bridge envelope ({"v":1,"data":"S;..."}), not the bare batch: the capture scans lost every reply to
  // this mismatch before M4-13 fixed them, and the statics scans were doing the same (no sector ever closed, so the census stayed empty).
  const std::string chunk = R"({"v":1,"data":"S;sec_a_macro;42|station_one_macro|10|20|30||argon;43|station_two_macro|1|2|3"})";
  const auto batch = S::parse_station_batch(A::scan_payload(chunk));
  REQUIRE(batch.has_value());
  CHECK_FALSE(batch->end);
  CHECK(batch->sector_macro == "sec_a_macro");
  REQUIRE(batch->rows.size() == 2);
  CHECK(batch->rows[0].component == 42u);
  CHECK(batch->rows[0].macro == "station_one_macro");
  CHECK(batch->rows[1].component == 43u);

  const std::string end = R"({"v":1,"data":"E;sec_a_macro;2"})";
  const auto closed = S::parse_station_batch(A::scan_payload(end));
  REQUIRE(closed.has_value());
  CHECK(closed->end);
  CHECK(closed->count == 2u);
  CHECK(closed->rows.empty());

  // A bare batch (no envelope) is passed through untouched.
  CHECK(A::scan_payload("S;sec;1|m|0|0|0") == "S;sec;1|m|0|0|0");
  CHECK(A::scan_payload("E;sec;3") == "E;sec;3");
}

// ------------------------------------------------------------------------------------------------ manifest reader

TEST_CASE("M4-17: a manifest loads, indexes and answers by match key", "[statics][m417]") {
  S::ManifestIndex index = loaded_index({{7, "sec_a_macro", "station_a_macro", 1200, 3400, -50, "AAA-01", 0x111},
                                         {8, "sec_a_macro", "station_b_macro", 0, 0, 0, "BBB-02", 0x222}});
  CHECK(index.loaded());
  CHECK(index.size() == 2);
  CHECK(index.stats().entries == 2);
  CHECK(index.stats().indexed == 2);
  CHECK(index.stats().no_macro == 0);
  CHECK(index.stats().no_sector == 0);
  CHECK(index.stats().collisions == 0);
  CHECK(index.checkpoint() == "99aabbccddeeff001122334455667788");

  const std::string key = O::station_match_key("sec_a_macro", "station_a_macro", 1200.4, 3400.2, -50.1);
  const S::StaticRow* row = index.lookup(key);
  REQUIRE(row != nullptr);
  CHECK(row->net_id == 7);
  CHECK(row->kind == static_cast<std::uint16_t>(P::EntityKind::Station));
  CHECK(row->component_id == 0x111);
  CHECK(row->sector_macro == "sec_a_macro");
  CHECK(row->macro == "station_a_macro");
  CHECK(row->idcode == "AAA-01");
  CHECK_FALSE(index.ambiguous(key));
  CHECK(index.lookup("sec_a_macro|nope|0,0,0") == nullptr);
  CHECK(index.lookup("") == nullptr);
}

TEST_CASE("M4-18: the interest sectors select their manifest rows", "[statics][m418]") {
  const S::ManifestIndex index = loaded_index({{7, "sec_a_macro", "station_a_macro", 0, 0, 0, "AAA-01", 1},
                                               {8, "sec_a_macro", "station_b_macro", 0, 0, 0, "BBB-02", 2},
                                               {9, "sec_b_macro", "station_c_macro", 0, 0, 0, "CCC-03", 3}});
  const std::uint16_t sector_a[] = {1};
  const std::uint16_t sector_b[] = {2};
  const std::uint16_t none[] = {9};
  CHECK(index.in_sectors(sector_a).size() == 2);
  CHECK(index.in_sectors(sector_b).size() == 1);
  CHECK(index.in_sectors(none).empty());
  CHECK(index.in_sectors(std::span<const std::uint16_t>{}).empty());
  CHECK(index.in_sectors_filtered(sector_a, static_cast<std::uint16_t>(P::EntityKind::Station)).size() == 2);
  CHECK(index.in_sectors_filtered(sector_a, static_cast<std::uint16_t>(P::EntityKind::ShipL)).empty());
}

TEST_CASE("M4-18: a duplicate key is ambiguous and the id code breaks the tie", "[statics][m418]") {
  // Two rows, one key: 0,0,0 and 10,10,10 fall into the same 50 m bucket.
  const S::ManifestIndex index = loaded_index({{11, "sec_a_macro", "station_a_macro", 0, 0, 0, "AAA-01", 1},
                                               {12, "sec_a_macro", "station_a_macro", 10, 10, 10, "AAA-02", 2}});
  const std::string key = O::station_match_key("sec_a_macro", "station_a_macro", 0, 0, 0);
  CHECK(index.stats().collisions == 1);
  CHECK(index.size() == 2);  // both stay: the tie-break needs both candidates
  CHECK(index.ambiguous(key));
  REQUIRE(index.lookup(key) != nullptr);
  CHECK(index.lookup(key)->net_id == 11);  // without an id code the first row wins
  REQUIRE(index.lookup(key, "AAA-02") != nullptr);
  CHECK(index.lookup(key, "AAA-02")->net_id == 12);
  REQUIRE(index.lookup(key, "AAA-01") != nullptr);
  CHECK(index.lookup(key, "AAA-01")->net_id == 11);
  CHECK(index.lookup(key, "ZZZ-99")->net_id == 11);  // an unknown id code falls back to the first row
}

TEST_CASE("M4-17: 5 000 rows load inside the budget", "[statics][m417]") {
  std::vector<RowSpec> rows;
  rows.reserve(5000);
  for (std::uint32_t i = 0; i < 5000; ++i) {
    // 100 sectors, 50 stations each, all macros from a fixed table so the string table stays realistic.
    static const char* kMacros[] = {"station_a_macro", "station_b_macro", "station_c_macro", "station_d_macro", "station_e_macro"};
    static const char* kSectors[] = {"sec_01_macro", "sec_02_macro", "sec_03_macro", "sec_04_macro", "sec_05_macro"};
    rows.push_back(RowSpec{i + 1, kSectors[i % 5], kMacros[(i / 5) % 5], static_cast<float>(i * 1000), static_cast<float>(i),
                           -static_cast<float>(i), "", i});
  }
  const auto bytes = build_manifest(rows);
  S::ManifestIndex index;
  const auto t0 = std::chrono::steady_clock::now();
  REQUIRE(index.load(bytes));
  const auto ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
  CHECK(index.size() == 5000);
  CHECK(index.stats().indexed == 5000);
  CHECK(ms < 50.0);  // M4-17 AC
  CHECK(index.lookup(O::station_match_key("sec_01_macro", "station_a_macro", 0, 0, 0)) != nullptr);
}

TEST_CASE("M4-17: a broken or foreign buffer is refused and leaves the index empty", "[statics][m417]") {
  S::ManifestIndex index = loaded_index({{7, "sec_a_macro", "station_a_macro", 0, 0, 0, "AAA-01", 1}});
  REQUIRE(index.loaded());

  const std::vector<std::uint8_t> garbage{1, 2, 3, 4, 5, 6, 7, 8, 9, 10};
  CHECK_FALSE(index.load(garbage));
  CHECK_FALSE(index.loaded());
  CHECK(index.size() == 0);
  CHECK(index.stats().entries == 0);
  CHECK(index.checkpoint().empty());

  // A manifest whose entry points at a string index that is not in the table: kept out of the index, counted.
  flatbuffers::FlatBufferBuilder fbb(256);
  const P::Vec3f pos(0, 0, 0);
  std::vector<flatbuffers::Offset<P::ManifestEntry>> entries;
  entries.push_back(P::CreateManifestEntryDirect(fbb, 5, P::EntityKind::Station, 1, 99 /* missing macro */, 0, 0, 0, 1, "", &pos));
  const P::Id128 checkpoint(1, 2);
  P::FinishManifestBuffer(fbb, P::CreateManifestDirect(fbb, &checkpoint, 0.0, 100, nullptr, nullptr, &entries));
  S::ManifestIndex sparse;
  REQUIRE(sparse.load(std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize())));
  CHECK(sparse.loaded());
  CHECK(sparse.stats().entries == 1);
  CHECK(sparse.stats().indexed == 0);
  CHECK(sparse.stats().no_macro == 1);
  CHECK(sparse.size() == 0);
}

// ------------------------------------------------------------------------------------------------ hub + matching

TEST_CASE("M4-19: the on-foot container id is the manifest net id", "[statics][m419]") {
  S::StaticsHub& hub = S::statics_hub();
  hub.clear();
  hub.set_index(loaded_index({{21, "sec_a_macro", "station_a_macro", 1200, 3400, -50, "AAA-01", 0x111}}));
  REQUIRE(hub.ready());

  const std::string key = O::station_match_key("sec_a_macro", "station_a_macro", 1200.4, 3400.2, -50.1);
  CHECK(hub.net_id_for(key) == 21);

  const std::string kv = "mode=1;skey=" + key + ";rmacro=room_macro;ax=0;ay=0;az=0";
  const auto ctx = O::parse_room_context(kv);
  REQUIRE(ctx.has_value());
  CHECK(ctx->container_net_id == 21);  // not the client-side hash

  // The three-part form MD sends when it has no composed key takes the same route.
  const auto parts = O::parse_room_context("mode=1;sector=sec_a_macro;smacro=station_a_macro;sx=1200.4;sy=3400.2;sz=-50.1;"
                                           "rmacro=room_macro;ax=0;ay=0;az=0");
  REQUIRE(parts.has_value());
  CHECK(parts->container_net_id == 21);

  // Without a manifest (or for a station the manifest does not name) the M3 hash is the fallback.
  hub.clear();
  const auto fallback = O::parse_room_context(kv);
  REQUIRE(fallback.has_value());
  CHECK(fallback->container_net_id == O::matched_container_id(key));
  CHECK(fallback->container_net_id != 0);
}

TEST_CASE("M4-20: pruning drops only the bindings the pass no longer lists", "[statics][m420]") {
  S::StaticsHub& hub = S::statics_hub();
  hub.clear();
  hub.set_index(loaded_index({{7, "sec_a_macro", "station_a_macro", 0, 0, 0, "AAA-01", 0x111},
                              {8, "sec_a_macro", "station_b_macro", 100, 100, 100, "BBB-02", 0x222}}));
  hub.bind(1, 7, 0x111);
  hub.bind(1, 8, 0x222);
  CHECK(hub.bound() == 2);
  CHECK(hub.component_of(7) == 0x111);

  // Station 8 is gone from the world, station 7 is still there.
  CHECK(hub.prune_sector(1, {0x111}) == 1);
  CHECK(hub.bound() == 1);
  CHECK(hub.component_of(7) == 0x111);
  CHECK(hub.component_of(8) == 0);

  // The survivor must stay in the sector index: a second pass over the same sector prunes nothing.
  CHECK(hub.prune_sector(1, {0x111}) == 0);
  CHECK(hub.bound() == 1);

  // A sector with no bindings is not an error.
  CHECK(hub.prune_sector(9, {}) == 0);
}

TEST_CASE("M4-21: the hub aggregates the per-sector match report", "[statics][m421]") {
  S::StaticsHub& hub = S::statics_hub();
  hub.clear();
  hub.set_index(loaded_index({{7, "sec_a_macro", "station_a_macro", 0, 0, 0, "AAA-01", 0x111}}));
  REQUIRE_FALSE(hub.matching_done());

  hub.sector(1).stations = 10;
  hub.sector(1).matched = 9;
  hub.sector(1).missed = 1;
  hub.sector(1).ambiguous = 1;
  hub.sector(1).tiebreak_idcode = 1;
  hub.sector(2).stations = 4;
  hub.sector(2).matched = 4;
  CHECK(hub.total_matched() == 13);
  CHECK(hub.total_missed() == 1);

  S::MatchReport report;
  report.checkpoint = std::string(hub.index().checkpoint());
  report.manifest_rows = hub.index().size();
  report.sectors = 2;
  report.stations = 14;
  report.matched = hub.total_matched();
  report.unmatched = hub.total_missed();
  report.ambiguous = 1;
  report.tiebreak_idcode = 1;
  report.keys.push_back("sec_a_macro|gone_macro|0,0,0");
  hub.set_report(std::move(report));

  CHECK(hub.matching_done());
  CHECK(hub.report().stations == 14);
  CHECK(hub.report().matched == 13);
  CHECK(hub.report().unmatched == 1);
  CHECK(hub.report().keys.size() == 1);
  CHECK(hub.report().checkpoint == "99aabbccddeeff001122334455667788");

  // A new checkpoint invalidates the report together with the bindings.
  hub.set_index(loaded_index({{7, "sec_a_macro", "station_a_macro", 0, 0, 0, "AAA-01", 0x111}}));
  CHECK_FALSE(hub.matching_done());
  CHECK(hub.report().keys.empty());
  hub.clear();
  CHECK_FALSE(hub.ready());
  CHECK_FALSE(hub.matching_done());
}

TEST_CASE("M5-16: station components and net ids map both ways, per sector", "[statics][m516]") {
  S::StaticsHub& hub = S::statics_hub();
  hub.clear();
  CHECK(hub.stations_known() == 0);
  CHECK(hub.station_net_id(0x111) == 0);
  CHECK(hub.station_component(0x111) == 0);

  hub.note_station(1, 0x111, 0x111);  // the authority's no-manifest id is the component hash, here a plain value
  hub.note_station(1, 0x222, 0x222);
  hub.note_station(2, 0x333, 0x333);
  CHECK(hub.stations_known() == 3);
  CHECK(hub.station_net_id(0x111) == 0x111);
  CHECK(hub.station_component(0x222) == 0x222);

  // A re-note with the same id is not a change and does not grow the map.
  hub.note_station(1, 0x111, 0x111);
  CHECK(hub.stations_known() == 3);

  // A new id for the same component replaces the old one: one net id maps to one component.
  hub.note_station(1, 0x111, 0x444);
  CHECK(hub.stations_known() == 3);
  CHECK(hub.station_net_id(0x111) == 0x444);
  CHECK(hub.station_component(0x111) == 0);
  CHECK(hub.station_component(0x444) == 0x111);

  // A scan pass prunes the ids it no longer lists, sector by sector.
  CHECK(hub.prune_stations(1, {0x222}) == 1);
  CHECK(hub.stations_known() == 2);
  CHECK(hub.station_net_id(0x222) == 0x222);
  CHECK(hub.station_net_id(0x111) == 0);
  CHECK(hub.station_component(0x444) == 0);
  CHECK(hub.prune_stations(1, {0x222}) == 0);  // idempotent
  CHECK(hub.prune_stations(9, {}) == 0);       // a sector with no ids is not an error

  // Dropping the bindings (a new checkpoint, a shutdown) drops the ids with them.
  hub.bind(1, 0x222, 0x222);
  hub.clear_bindings();
  CHECK(hub.stations_known() == 0);
  CHECK(hub.station_component(0x222) == 0);
}

TEST_CASE("M5-17: the scan carries the station owner, and a silent pass does not erase it", "[statics][m517]") {
  // The 7th field is the owner faction id; the 6th stays the M4-18 id code, so MD sends an empty id code today.
  const auto batch = S::parse_station_batch(
      "S;sec_a_macro;0x18ead5|station_a_macro|1200.4|3400.2|-50.1||argon;0x18ead6|station_b_macro|0|0|0|TELADI-01|teladi");
  REQUIRE(batch.has_value());
  REQUIRE(batch->rows.size() == 2);
  CHECK(batch->rows[0].owner == "argon");
  CHECK(batch->rows[0].idcode.empty());
  CHECK(batch->rows[1].idcode == "TELADI-01");
  CHECK(batch->rows[1].owner == "teladi");

  // A row without the field at all is still a row: the owner is simply unknown.
  const auto bare = S::parse_station_batch("S;sec;0x10|m|1|2|3");
  REQUIRE(bare.has_value());
  REQUIRE(bare->rows.size() == 1);
  CHECK(bare->rows[0].owner.empty());

  // The hub keeps the owner next to the id, and a pass that could not read it keeps the one it already knew.
  auto& hub = S::statics_hub();
  hub.clear_stations();
  hub.note_station(4, 0x18ead5, 0x99, "sec_a_macro", false, "argon");
  CHECK(hub.owner_of(0x18ead5) == "argon");
  hub.note_station(4, 0x18ead5, 0x99, "sec_a_macro");  // the same station, owner unreadable this time
  CHECK(hub.owner_of(0x18ead5) == "argon");
  CHECK(hub.sector_macro_of(0x18ead5) == "sec_a_macro");

  // A changed owner is learned, and a pinned id keeps the owner it was pinned with.
  hub.note_station(4, 0x18ead5, 0x99, "sec_a_macro", false, "teladi");
  CHECK(hub.owner_of(0x18ead5) == "teladi");
  hub.pin_station(4, 0x18ead5, 0x4242, "sec_a_macro");
  CHECK(hub.station_net_id(0x18ead5) == 0x4242);
  CHECK(hub.owner_of(0x18ead5) == "teladi");  // pinning an id does not lose what the scan read
  // A captured pinned station keeps its id and changes hands: the scan updates the owner, never the id.
  hub.note_station(4, 0x18ead5, 0x99, "sec_a_macro", false, "argon");
  CHECK(hub.station_net_id(0x18ead5) == 0x4242);
  CHECK(hub.owner_of(0x18ead5) == "argon");
  hub.clear_stations();
  CHECK(hub.owner_of(0x18ead5).empty());
}

TEST_CASE("M4-21: the census keeps committed rows across a pass and commits a sector on its end marker", "[statics][m421][census]") {
  auto& hub = S::statics_hub();
  hub.clear_census();
  CHECK_FALSE(hub.census_active());
  hub.start_census({"sec_a_macro", "sec_b_macro"});
  CHECK(hub.census_generation() == 1);
  CHECK(hub.census_active());
  CHECK(hub.census_open_count() == 2);

  hub.census_begin_sector("sec_a_macro");
  S::StaticsHub::CensusRow a;
  a.net_id = 0x80000011;
  a.component = 0x111;
  a.sector_macro = "sec_a_macro";
  a.macro = "station_a_macro";
  a.owner = "argon";
  a.x = 10;
  hub.census_add("sec_a_macro", a);
  hub.census_add("sec_a_macro", a);  // the same component twice in one pass counts once
  CHECK(hub.census_size() == 0);     // still staging: the manifest must not read a half-scanned sector
  hub.census_close_sector("sec_a_macro");
  CHECK(hub.census_size() == 1);
  CHECK(hub.census_sectors() == 1);
  CHECK(hub.census_open_count() == 1);

  // A scan of a sector no census pass opened (a trade asked for it) lands straight in the committed set.
  S::StaticsHub::CensusRow c;
  c.net_id = 0x80000033;
  c.component = 0x333;
  c.sector_macro = "sec_c_macro";
  c.macro = "station_c_macro";
  hub.census_add("sec_c_macro", c);
  CHECK(hub.census_size() == 2);

  // A second closing count of the same sector (an extra scan) must not replace or wipe it.
  hub.census_close_sector("sec_a_macro");
  CHECK(hub.census_size() == 2);

  // A new pass replaces the rows of the sectors it closes; a sector that never answers keeps the previous pass's rows.
  hub.start_census({"sec_a_macro", "sec_d_macro"});
  CHECK(hub.census_generation() == 2);
  hub.census_begin_sector("sec_a_macro");
  S::StaticsHub::CensusRow a2 = a;
  a2.net_id = 0x80000012;
  a2.component = 0x112;
  hub.census_add("sec_a_macro", a2);
  hub.census_close_sector("sec_a_macro");
  CHECK(hub.census_size() == 2);  // sec_c, plus the replaced sec_a
  hub.census_begin_sector("sec_d_macro");
  hub.census_abandon_sector("sec_d_macro");
  CHECK_FALSE(hub.census_active());
  CHECK(hub.census_size() == 2);

  const auto rows = hub.census_rows();
  REQUIRE(rows.size() == 2);
  CHECK(rows[0].component == 0x112);  // sec_a first: the committed map is ordered by macro
  CHECK(rows[0].net_id == 0x80000012);
  CHECK(rows[1].sector_macro == "sec_c_macro");

  hub.clear_census();
  CHECK(hub.census_size() == 0);
  CHECK(hub.census_generation() == 0);
  CHECK_FALSE(hub.census_active());
}

TEST_CASE("M4-21: the manifest carries census stations next to the avatars, and the client index finds them", "[statics][m421][manifest]") {
  using namespace x4mp::authority;
  std::vector<StringDesc> strings = {
      {1, static_cast<std::uint8_t>(P::StringKind::Macro), "ship_a_macro"},
      {2, static_cast<std::uint8_t>(P::StringKind::Macro), "station_a_macro"},
      {3, static_cast<std::uint8_t>(P::StringKind::Faction), "argon"},
  };
  std::vector<SectorDesc> sectors(1);
  sectors[0].index = 1;
  sectors[0].macro = "sec_a_macro";
  ManifestAvatar av;
  av.net_id = 1;
  av.kind = static_cast<std::uint8_t>(P::EntityKind::ShipS);
  av.macro_ref = 1;
  av.sector = 1;
  av.x = 1;
  av.y = 2;
  av.z = 3;
  ManifestStatic st;
  st.net_id = 0x80001234;
  st.component_id = 0x18ead5;
  st.macro_ref = 2;
  st.owner_ref = 3;
  st.sector = 1;
  st.x = 1200.4f;
  st.y = 3400.2f;
  st.z = -50.1f;
  const auto bytes = encode_manifest({0x1122334455667788ull, 0x99aabbccddeeff00ull}, 5000.0, 100, strings, sectors, {av},
                                     {st});
  REQUIRE(bytes.has_value());
  flatbuffers::Verifier verifier(bytes->data(), bytes->size());
  REQUIRE(verifier.VerifyBuffer<P::Manifest>(nullptr));
  const auto* manifest = flatbuffers::GetRoot<P::Manifest>(bytes->data());
  REQUIRE(manifest->entries() != nullptr);
  REQUIRE(manifest->entries()->size() == 2);
  const auto* first = manifest->entries()->Get(0);
  CHECK(first->net_id() == 1);
  CHECK(first->origin() == P::EntityOrigin::PlayerShip);  // the entries are sorted by net id, the avatar first
  const auto* entry = manifest->entries()->Get(1);
  CHECK(entry->net_id() == 0x80001234);
  CHECK(entry->origin() == P::EntityOrigin::Manifest);
  CHECK(entry->kind() == P::EntityKind::Station);
  CHECK(entry->component_id() == 0x18ead5);
  CHECK(entry->macro_ref() == 2);
  CHECK(entry->owner_ref() == 3);
  CHECK(entry->sector() == 1);
  CHECK(entry->controller_player() == 0);
  REQUIRE(entry->position() != nullptr);
  CHECK(entry->position()->x() == 1200.4f);

  // The client reads its own manifest back: the row is found by the very key its scan produces.
  S::ManifestIndex index;
  REQUIRE(index.load(*bytes));
  CHECK(index.size() == 2);
  const std::string key = O::station_match_key("sec_a_macro", "station_a_macro", 1200.4, 3400.2, -50.1);
  const S::StaticRow* row = index.lookup(key);
  REQUIRE(row != nullptr);
  CHECK(row->net_id == 0x80001234);
  CHECK(row->component_id == 0x18ead5);
}
