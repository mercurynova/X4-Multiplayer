// features/ghosts/local_suppression (M4-13/M4-15, docs/m4-plan.md W3, docs/mod-design.md 4.3): the client's own NPC ships in a sector the
// authority has closed must go, or every streamed ship would be there twice. This is the pure state machine: which sector may be suppressed,
// which rows are suppressed (not the player's, not a mission object, not our own ghost), the 200-per-frame spread and the 1 Hz late-spawn
// sweep. The removal itself is the caller's SafeRemove, so a fake here counts what it was handed.
#include <cstdint>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "features/authority/capture_ingest.h"
#include "features/ghosts/local_suppression.h"

using namespace x4mp::features;
using ghosts::LocalSuppression;
using ghosts::SuppressionConfig;

namespace {

auth::ScanRow row(std::uint64_t component, std::string owner = "argon", bool mission = false) {
  auth::ScanRow r;
  r.component = component;
  r.macro = "ship_arg_s_fighter_01_a_macro";
  r.owner = std::move(owner);
  r.mission = mission;
  return r;
}

std::vector<std::uint16_t> interest(std::initializer_list<std::uint16_t> sectors) { return std::vector<std::uint16_t>(sectors); }

}  // namespace

TEST_CASE("M4-13: a closed sector in interest is asked at once, then once a second", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint16_t> asked;
  const auto ask = [&asked](std::uint16_t sector) { asked.push_back(sector); };
  const auto remove = [](std::uint64_t) {};

  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.frame(1'000'000, ask, remove);
  REQUIRE(asked.size() == 1);
  CHECK(asked[0] == 7);
  CHECK(s.closed(7));
  CHECK(s.stats().requests == 1);

  // the sweep interval has not passed yet
  s.frame(1'500'000, ask, remove);
  CHECK(asked.size() == 1);
  // ... and now it has
  s.frame(2'000'000, ask, remove);
  CHECK(asked.size() == 2);
  s.frame(3'000'000, ask, remove);
  CHECK(asked.size() == 3);
  CHECK(s.stats().requests == 3);
}

TEST_CASE("M4-13: only a closed sector that is still in interest is swept", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint16_t> asked;
  const auto ask = [&asked](std::uint16_t sector) { asked.push_back(sector); };
  const auto remove = [](std::uint64_t) {};

  s.on_sector_closed(7);  // no interest yet: nothing to do
  s.frame(1'000'000, ask, remove);
  CHECK(asked.empty());

  s.on_interest(interest({7, 9}));
  s.frame(2'000'000, ask, remove);
  REQUIRE(asked.size() == 1);

  s.on_interest(interest({9}));  // 7 left interest
  s.frame(4'000'000, ask, remove);
  CHECK(asked.size() == 1);  // no further sweep of 7
  CHECK(s.closed(7));        // but its state (and its suppressed ids) stays

  s.on_interest(interest({7}));  // a re-entry resumes the sweep
  s.frame(5'000'000, ask, remove);
  CHECK(asked.size() == 2);
}

TEST_CASE("M4-13: the player's ships, mission objects and our own ghosts are never suppressed", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint64_t> removed;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [](std::uint16_t) {};
  const auto skip = [](std::uint64_t component) { return component == 9001 || component == 9002; };  // self ship + one of our ghosts

  const std::vector<auth::ScanRow> rows{
      row(100, "argon"),        // local NPC: suppressed
      row(101, "player"),       // the player's own ship
      row(102, "argon", true),  // a mission object
      row(9001, "argon"),       // the player's ship by id
      row(9002, "argon"),       // one of our ghost objects
      row(103, "argon"),        // another local NPC
  };
  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, rows, skip);
  CHECK(s.stats().rows == 6);
  CHECK(s.stats().queued == 2);
  CHECK(s.stats().skipped_player == 1);
  CHECK(s.stats().skipped_mission == 1);
  CHECK(s.stats().skipped_known == 2);
  CHECK(s.pending_removals() == 2);

  s.frame(1'000'000, ask, remove);
  REQUIRE(removed.size() == 2);
  CHECK(removed[0] == 100);
  CHECK(removed[1] == 103);
  CHECK(s.suppressed_of(7) == 2);
}

TEST_CASE("M4-13: 50 local NPCs and 50 streamed ships leave exactly the 50 streamed ones", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint64_t> removed;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [](std::uint16_t) {};

  // 1..50 are the local ships, 1001..1050 are the ghosts the authority streamed into this sector (our own objects).
  std::vector<auth::ScanRow> rows;
  for (std::uint64_t id = 1; id <= 50; ++id) rows.push_back(row(id));
  for (std::uint64_t id = 1001; id <= 1050; ++id) rows.push_back(row(id));
  const auto skip = [](std::uint64_t component) { return component >= 1001; };

  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, rows, skip);
  CHECK(s.stats().queued == 50);
  CHECK(s.stats().skipped_known == 50);
  s.frame(1'000'000, ask, remove);
  REQUIRE(removed.size() == 50);
  CHECK(removed.front() == 1);
  CHECK(removed.back() == 50);
  CHECK(s.total_suppressed() == 50);
}

TEST_CASE("M4-13: suppression is spread over frames at 200 objects per frame", "[suppression][m413]") {
  SuppressionConfig cfg;
  cfg.removals_per_frame = 200;
  LocalSuppression s(cfg);
  std::vector<std::uint64_t> removed;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [](std::uint16_t) {};
  const auto skip = [](std::uint64_t) { return false; };

  std::vector<auth::ScanRow> rows;
  for (std::uint64_t id = 1; id <= 450; ++id) rows.push_back(row(id));
  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, rows, skip);

  s.frame(1'000'000, ask, remove);
  CHECK(removed.size() == 200);
  CHECK(s.pending_removals() == 250);
  s.frame(1'016'667, ask, remove);
  CHECK(removed.size() == 400);
  s.frame(1'033'334, ask, remove);
  CHECK(removed.size() == 450);
  CHECK(s.pending_removals() == 0);
  CHECK(s.stats().removed == 450);
}

TEST_CASE("M4-13: a re-entry does not suppress the same object twice", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint64_t> removed;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [](std::uint16_t) {};
  const auto skip = [](std::uint64_t) { return false; };
  const std::vector<auth::ScanRow> rows{row(100), row(101)};

  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, rows, skip);
  s.frame(1'000'000, ask, remove);
  REQUIRE(removed.size() == 2);

  // leave and come back: the sector is closed again and scanned again, and the same two ships are still in the answer
  s.on_interest(interest({9}));
  s.frame(2'000'000, ask, remove);
  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, rows, skip);
  CHECK(s.stats().skipped_dup == 2);
  CHECK(s.stats().queued == 2);  // unchanged
  s.frame(3'000'000, ask, remove);
  CHECK(removed.size() == 2);
  CHECK(s.suppressed_of(7) == 2);
}

TEST_CASE("M4-15: the 1 Hz sweep catches a ship vanilla spawned after the entry", "[suppression][m415]") {
  LocalSuppression s;
  std::vector<std::uint64_t> removed;
  std::vector<std::uint16_t> asked;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [&asked](std::uint16_t sector) { asked.push_back(sector); };
  const auto skip = [](std::uint64_t) { return false; };

  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, std::vector<auth::ScanRow>{row(100)}, skip);
  s.frame(1'000'000, ask, remove);
  REQUIRE(removed.size() == 1);

  // 30 s later a job spawns a ship in the sector; the next sweep asks again and its answer carries the new id
  s.frame(31'000'000, ask, remove);
  REQUIRE(asked.size() == 2);
  s.on_scan(7, std::vector<auth::ScanRow>{row(100), row(101)}, skip);
  CHECK(s.stats().skipped_dup == 1);
  s.frame(31'016'667, ask, remove);
  REQUIRE(removed.size() == 2);
  CHECK(removed[1] == 101);
  CHECK(s.suppressed_of(7) == 2);
}

TEST_CASE("M4-13: a scan of a sector nobody closed is kept but never queued for removal", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint64_t> removed;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [](std::uint16_t) {};
  const auto skip = [](std::uint64_t) { return false; };

  s.on_scan(7, std::vector<auth::ScanRow>{row(100)}, skip);  // no on_sector_closed: the authority has not finished the sector
  CHECK_FALSE(s.closed(7));
  s.on_interest(interest({7}));
  s.frame(1'000'000, ask, remove);
  CHECK(removed.empty());
  CHECK(s.stats().queued == 0);
}

TEST_CASE("M4-13: clear() forgets every sector, id and counter", "[suppression][m413]") {
  LocalSuppression s;
  std::vector<std::uint64_t> removed;
  const auto remove = [&removed](std::uint64_t id) { removed.push_back(id); };
  const auto ask = [](std::uint16_t) {};
  const auto skip = [](std::uint64_t) { return false; };

  s.on_interest(interest({7}));
  s.on_sector_closed(7);
  s.on_scan(7, std::vector<auth::ScanRow>{row(100)}, skip);
  s.frame(1'000'000, ask, remove);
  REQUIRE(s.total_suppressed() == 1);

  s.clear();
  CHECK(s.total_suppressed() == 0);
  CHECK(s.pending_removals() == 0);
  CHECK_FALSE(s.closed(7));
  CHECK(s.stats().removed == 0);
}
