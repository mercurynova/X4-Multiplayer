// M4 wave 1: the capture set, the universe index, the sector-scan text and the wire codec.
// See docs/m4-plan.md W1 (M4-01..M4-05) and docs/mod-design.md §9 "M4" sub-item 1.

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include <flatbuffers/flatbuffers.h>

#include "features/authority/capture_ingest.h"
#include "features/authority/capture_set.h"
#include "features/authority/capture_set_wire.h"
#include "features/authority/universe_index.h"
#include "world_generated.h"

using namespace x4mp::features::auth;
namespace P = X4MP::Proto;

TEST_CASE("capture: a set is taken once per epoch, and a resend is not a change", "[authority][capture]") {
  CaptureSet set;
  CHECK(set.empty());
  CHECK(set.applied() == false);
  CHECK_FALSE(set.contains(12));

  // Two rows for the same sector (the server unions several clients): the higher rate wins, one row survives.
  REQUIRE(set.apply(7, {{12, 5}, {44, 1}, {12, 20}}, {}));
  CHECK(set.epoch() == 7);
  CHECK(set.applied());
  CHECK(set.sectors().size() == 2);
  CHECK(set.rate_of(12) == 20);
  CHECK(set.rate_of(44) == 1);
  CHECK(set.rate_of(9) == 0);
  CHECK(set.contains(44));
  CHECK_FALSE(set.contains(0));  // "no sector" is never captured

  // A resend of the same epoch, and an older one, must not restart capture: that would re-spawn every entity of every sector.
  CHECK_FALSE(set.apply(7, {{12, 5}}, {}));
  CHECK_FALSE(set.apply(6, {{12, 5}}, {}));
  CHECK(set.rate_of(44) == 1);

  REQUIRE(set.apply(8, {}, {}));
  CHECK(set.empty());
  CHECK(set.epoch() == 8);
}

TEST_CASE("capture: a focus sphere raises the rate of the entities inside it", "[authority][capture]") {
  CaptureSet set;
  REQUIRE(set.apply(1, {{44, 1}}, {{44, 100.0f, 200.0f, 0.0f, 50.0f, 20}}));
  CHECK(set.rate_of(44) == 1);                     // the sector row: 1 Hz
  CHECK(set.rate_at(44, 0.0f, 0.0f, 0.0f) == 1);   // far outside the sphere: the sector rate
  CHECK(set.rate_at(44, 120.0f, 200.0f, 0.0f) == 20);
  CHECK(set.rate_at(44, 100.0f, 250.0f, 0.0f) == 20);  // exactly on the radius
  CHECK(set.rate_at(45, 120.0f, 200.0f, 0.0f) == 0);

  // A sector that only a focus names is captured inside the sphere and nowhere else.
  REQUIRE(set.apply(2, {}, {{44, 100.0f, 200.0f, 0.0f, 50.0f, 20}}));
  CHECK(set.contains(44));
  CHECK(set.rate_of(44) == 0);
  CHECK(set.rate_at(44, 120.0f, 200.0f, 0.0f) == 20);
  CHECK(set.rate_at(44, 0.0f, 0.0f, 0.0f) == 0);

  // Without a focus row the sector rate applies everywhere.
  REQUIRE(set.apply(3, {{44, 5}}, {}));
  CHECK(set.rate_at(44, 0.0f, 0.0f, 0.0f) == 5);
  CHECK(set.rate_at(44, 5000.0f, 0.0f, 0.0f) == 5);
}

TEST_CASE("capture: a focus row is dropped when it cannot name a sphere", "[authority][capture]") {
  CaptureSet set;
  // radius 0 / negative / NaN, and a sector of 0: none of them can be honoured, and keeping them would capture nothing forever.
  REQUIRE(set.apply(1, {}, {{44, 0, 0, 0, 0.0f, 20}, {44, 0, 0, 0, -1.0f, 20}, {0, 0, 0, 0, 10.0f, 20}}));
  CHECK(set.focus().empty());
  CHECK(set.empty());
}

TEST_CASE("capture: the change line names what moved", "[authority][capture]") {
  CaptureSet first;
  REQUIRE(first.apply(1, {{12, 5}, {44, 1}}, {}));
  CHECK(first.describe() == "2 sectors [12@5Hz 44@1Hz], 0 focus []");

  CaptureSet second;
  REQUIRE(second.apply(2, {{12, 20}, {45, 1}}, {}));
  const auto change = second.describe_change(first);
  CHECK(change.find("rate 12@20Hz (was 5Hz)") != std::string::npos);
  CHECK(change.find("added 45@1Hz") != std::string::npos);
  CHECK(change.find("dropped 44@1Hz") != std::string::npos);
  CHECK(second.describe_change(second).empty());
}

TEST_CASE("index: a rescan refreshes a row instead of inserting a second one", "[authority][index]") {
  UniverseIndex index;
  index.reserve(16);
  auto r = index.upsert(1001, 12, 5, 6, 19, 0, 0xABC, 1'000);
  REQUIRE(r.entry != nullptr);
  CHECK(r.inserted);
  CHECK(r.entry->sector == 12);
  CHECK(r.entry->component == 0xABC);
  CHECK(r.entry->last_seen_us == 1'000);

  r = index.upsert(1001, 12, 5, 6, 19, 0, 0xABC, 2'000);
  CHECK_FALSE(r.inserted);
  CHECK(index.size() == 1);
  CHECK(index.counters().inserted == 1);
  CHECK(index.counters().refreshed == 1);
  CHECK(r.entry->last_seen_us == 2'000);

  // A row without a net id or a sector cannot be scheduled and is refused, never stored under a guessed key.
  CHECK_FALSE(index.upsert(0, 12, 5, 6, 19, 0, 0xABC, 3'000).inserted);
  CHECK_FALSE(index.upsert(1002, 0, 5, 6, 19, 0, 0xDEF, 3'000).inserted);
  CHECK(index.size() == 1);
}

TEST_CASE("index: a pass drops the rows of a sector that the scan no longer sees", "[authority][index]") {
  UniverseIndex index;
  index.upsert(1001, 12, 5, 6, 19, 0, 0xABC, 1'000);
  index.upsert(1002, 12, 5, 6, 19, 0, 0xABD, 1'000);
  index.upsert(2001, 44, 7, 8, 19, 0, 0xAAA, 1'000);
  REQUIRE(index.size() == 3);

  index.begin_scan(12);
  CHECK(index.scanning(12));
  index.upsert(1001, 12, 5, 6, 19, 0, 0xABC, 2'000);  // seen again
  CHECK(index.end_scan(12) == 1);                      // 1002 is gone
  CHECK_FALSE(index.scanning(12));
  CHECK(index.size() == 2);
  CHECK(index.find(1002) == nullptr);
  CHECK(index.find(1001) != nullptr);
  CHECK(index.find(2001) != nullptr);  // another sector's rows are untouched by this pass
  CHECK(index.counters().removed == 1);
  CHECK(index.counters().scans == 1);
}

TEST_CASE("index: a ship that changes sector keeps its net id and moves bucket", "[authority][index]") {
  UniverseIndex index;
  index.upsert(1001, 12, 5, 6, 19, 0, 0xABC, 1'000);
  index.begin_scan(12);
  index.upsert(1001, 44, 5, 6, 19, 0, 0xABC, 2'000);
  CHECK(index.counters().moved == 1);
  CHECK(index.find(1001)->sector == 44);
  REQUIRE(index.ids_of(12) != nullptr);
  CHECK(index.ids_of(12)->empty());
  REQUIRE(index.ids_of(44) != nullptr);
  CHECK(index.ids_of(44)->size() == 1);
  // Closing the old pass must not delete the row that moved: it is still alive, just elsewhere.
  CHECK(index.end_scan(12) == 0);
  CHECK(index.find(1001) != nullptr);
}

TEST_CASE("index: a sector that leaves the capture set is dropped whole", "[authority][index]") {
  UniverseIndex index;
  index.upsert(1001, 12, 5, 6, 19, 0, 0xABC, 1'000);
  index.upsert(1002, 12, 5, 6, 19, 0, 0xABD, 1'000);
  index.upsert(2001, 44, 7, 8, 19, 0, 0xAAA, 1'000);
  CHECK(index.drop_sector(12) == 2);
  CHECK(index.size() == 1);
  CHECK(index.ids_of(12) == nullptr);
  CHECK(index.drop_sector(12) == 0);

  index.clear();
  CHECK(index.empty());
  CHECK(index.counters().inserted == 0);
}

TEST_CASE("index: the component binding survives a rescan and never allocates twice", "[authority][index]") {
  UniverseIndex index;
  CHECK(index.net_id_of_component(0xABC) == 0);
  index.bind_component(0xABC, 1001);
  CHECK(index.net_id_of_component(0xABC) == 1001);
  index.bind_component(0xABC, 9999);  // first binding wins: a second id would look like a respawn to a client
  CHECK(index.net_id_of_component(0xABC) == 1001);
  CHECK(index.net_id_of_component(0) == 0);
}

TEST_CASE("scan: a batch parses into rows, and a broken row is skipped, not fatal", "[authority][scan]") {
  const auto batch = parse_scan_batch("S;cluster_01_sector001_macro;0x1b665b|ship_arg_s_fighter_01_a_macro|argon;"
                                      "1632981|ship_arg_m_freighter_01_a_macro|argon;;junk;0x1b6661|ship_arg_l_destroyer_01_a_macro|"
                                      "xenon");
  REQUIRE(batch.has_value());
  CHECK_FALSE(batch->end);
  CHECK(batch->sector_macro == "cluster_01_sector001_macro");
  REQUIRE(batch->rows.size() == 3);
  CHECK(batch->rows[0].component == 0x1b665bull);  // MD prints a component in hex, so both bases are accepted
  CHECK(batch->rows[0].macro == "ship_arg_s_fighter_01_a_macro");
  CHECK(batch->rows[0].owner == "argon");
  CHECK(batch->rows[1].component == 1632981ull);  // the same id in decimal
  CHECK(batch->rows[2].owner == "xenon");

  const auto end = parse_scan_batch("E;cluster_01_sector001_macro;3");
  REQUIRE(end.has_value());
  CHECK(end->end);
  CHECK(end->count == 3);
  CHECK(end->rows.empty());

  CHECK_FALSE(parse_scan_batch("").has_value());
  CHECK_FALSE(parse_scan_batch("X;cluster_01_sector001_macro;1").has_value());
  CHECK_FALSE(parse_scan_batch("S;").has_value());
  CHECK_FALSE(parse_scan_batch("E;cluster_01_sector001_macro;").has_value());
  CHECK_FALSE(parse_scan_batch("E;cluster_01_sector001_macro;many").has_value());
}

TEST_CASE("scan: a row with a broken component or no macro is dropped", "[authority][scan]") {
  const auto batch = parse_scan_batch("S;sec;0|ship_a|argon;zz|ship_b|argon;12||argon;13|ship_c|");
  REQUIRE(batch.has_value());
  REQUIRE(batch->rows.size() == 1);
  CHECK(batch->rows[0].component == 13u);
  CHECK(batch->rows[0].macro == "ship_c");
  CHECK(batch->rows[0].owner.empty());  // an ownerless ship is still indexable; the owner is refreshed on the next pass
}

TEST_CASE("capture wire: a CaptureSet decodes into the model", "[authority][capture][wire]") {
  flatbuffers::FlatBufferBuilder fbb(256);
  const std::vector<P::CaptureSector> sectors{{12, 5}, {44, 1}};
  const auto sector_vector = fbb.CreateVectorOfStructs(sectors);
  const P::Vec3f center(100.0f, 200.0f, 0.0f);
  std::vector<flatbuffers::Offset<P::CaptureFocus>> focus;
  focus.push_back(P::CreateCaptureFocus(fbb, 44, &center, 50.0f, 20));
  const auto focus_vector = fbb.CreateVector(focus);
  fbb.Finish(P::CreateCaptureSet(fbb, 3, sector_vector, focus_vector));

  const std::vector<std::uint8_t> bytes(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
  std::string why;
  const auto data = decode_capture_set(bytes, &why);
  REQUIRE(data.has_value());
  CHECK(data->epoch == 3);
  REQUIRE(data->sectors.size() == 2);
  CHECK(data->sectors[0].sector == 12);
  CHECK(data->sectors[0].rate_hz == 5);
  CHECK(data->sectors[1].sector == 44);
  REQUIRE(data->focus.size() == 1);
  CHECK(data->focus[0].sector == 44);
  CHECK(data->focus[0].cx == 100.0f);
  CHECK(data->focus[0].cy == 200.0f);
  CHECK(data->focus[0].radius_m == 50.0f);
  CHECK(data->focus[0].rate_hz == 20);

  // And the decoded shape is what the model then takes.
  CaptureSet set;
  REQUIRE(set.apply(data->epoch, data->sectors, data->focus));
  CHECK(set.rate_of(12) == 5);
  CHECK(set.rate_at(44, 100.0f, 200.0f, 0.0f) == 20);

  CHECK_FALSE(decode_capture_set({}, &why).has_value());
  CHECK(why == "empty payload");
  const std::vector<std::uint8_t> junk(16, 0x7f);
  CHECK_FALSE(decode_capture_set(junk, &why).has_value());
}

TEST_CASE("capture wire: SectorComplete round trips", "[authority][capture][wire]") {
  const auto bytes = encode_sector_complete(12, 3, 7);
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  REQUIRE(verifier.VerifyBuffer<P::SectorComplete>(nullptr));
  const auto* root = flatbuffers::GetRoot<P::SectorComplete>(bytes.data());
  REQUIRE(root != nullptr);
  CHECK(root->sector() == 12);
  CHECK(root->epoch() == 3);
  CHECK(root->entity_count() == 7);
}

// M4-02: the index is fed by scans that repeat the same objects, so insert has to be idempotent and a pass over a full sector has to stay
// cheap. The timing half of the AC is measured in game (the frame counter, W7); here the shape of the table is pinned.
TEST_CASE("index: ten thousand inserts with duplicates keep seven thousand rows", "[authority][index]") {
  UniverseIndex idx;
  idx.reserve(10'000);
  std::int64_t now = 1'000'000;
  idx.begin_scan(12);
  for (std::uint32_t i = 0; i < 10'000; ++i) {
    // 30% of the inserts name an object the scan already saw, which is what a re-scan of a live sector looks like.
    const std::uint32_t net = 1 + (i < 7'000 ? i : i - 7'000);
    idx.upsert(net, 12, 100 + net, 7, 0, 0, 0x1000'0000ull + net, now);
    if (i % 1'000 == 999) now += 1'000;
  }
  const auto gone = idx.end_scan(12);
  CHECK(gone == 0);
  CHECK(idx.size() == 7'000);
  CHECK(idx.counters().inserted == 7'000);
  CHECK(idx.counters().refreshed == 3'000);
  const auto* ids = idx.ids_of(12);
  REQUIRE(ids != nullptr);
  CHECK(ids->size() == 7'000);
}
