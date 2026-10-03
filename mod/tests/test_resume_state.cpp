#include <limits>

#include <catch2/catch_test_macros.hpp>

#include "features/resume/resume_state.h"

using namespace x4mp::features::resume;

namespace {
State in_game(double t, std::uint64_t pid = 1000042) {
  State s;
  s.stage = "ingame";
  s.epoch = 0x1234567890abcdefULL;
  s.fingerprint = Fingerprint{true, pid, t};
  return s;
}
Fingerprint at(double t, std::uint64_t pid = 1000042) { return Fingerprint{true, pid, t}; }
}  // namespace

TEST_CASE("decide_universe: /reloadui keeps the universe, a save load does not", "[resume]") {
  const State prev = in_game(1000.0);
  CHECK(decide_universe(prev, at(1000.0)).verdict == Verdict::SameUniverse);
  CHECK(decide_universe(prev, at(1003.5)).verdict == Verdict::SameUniverse);   // UI rebuild time
  CHECK(decide_universe(prev, at(999.7)).verdict == Verdict::SameUniverse);    // sampling jitter
  CHECK(decide_universe(prev, at(1000.0 + kClockForwardWindowS)).verdict == Verdict::SameUniverse);

  CHECK(decide_universe(prev, at(1000.0 + kClockForwardWindowS + 1.0)).verdict == Verdict::NewUniverse);  // newer checkpoint
  CHECK(decide_universe(prev, at(400.0)).verdict == Verdict::NewUniverse);                                // older checkpoint
  CHECK(decide_universe(prev, at(1000.0 - kClockBackSlackS - 0.1)).verdict == Verdict::NewUniverse);
  CHECK(decide_universe(prev, at(1000.0, 77)).verdict == Verdict::NewUniverse);                           // other player object
}

TEST_CASE("decide_universe: anything uncertain is a new universe", "[resume]") {
  State prev = in_game(50.0);
  CHECK(decide_universe(prev, Fingerprint{}).verdict == Verdict::NewUniverse);  // nothing sampled now
  prev.fingerprint.valid = false;
  CHECK(decide_universe(prev, at(50.0)).verdict == Verdict::NewUniverse);       // nothing sampled before
  for (const char* stage : {"loading", "preparing", "downloading", "joining", ""}) {
    State s = in_game(50.0);
    s.stage = stage;
    CHECK(decide_universe(s, at(50.0)).verdict == Verdict::NewUniverse);        // only an in-game node can be "the same universe"
  }
  CHECK(decide_universe(in_game(50.0), at(std::numeric_limits<double>::quiet_NaN())).verdict == Verdict::NewUniverse);
}

TEST_CASE("State round-trips through the stash JSON, including the 64-bit epoch", "[resume]") {
  State s = in_game(321.25);
  s.save_name = "x4mp_abcdef012345";
  s.save_sha = {1, 2, 3, 0xfe, 0xff};
  s.has_manifest = true;
  s.checkpoint = x4mp::session::Id128{0xdeadbeefcafef00dULL, 42};
  const auto back = parse(to_json(s));
  REQUIRE(back);
  CHECK(back->stage == "ingame");
  CHECK(back->save_name == s.save_name);
  CHECK(back->save_sha == s.save_sha);
  CHECK(back->has_manifest);
  CHECK(back->checkpoint.lo == 0xdeadbeefcafef00dULL);
  CHECK(back->checkpoint.hi == 42);
  CHECK(back->epoch == 0x1234567890abcdefULL);
  CHECK(back->fingerprint.valid);
  CHECK(back->fingerprint.player_id == 1000042);
  CHECK(back->fingerprint.game_time == 321.25);
}

TEST_CASE("parse refuses corrupt text and tolerates missing fields", "[resume]") {
  CHECK_FALSE(parse(""));
  CHECK_FALSE(parse("not json"));
  CHECK_FALSE(parse("[1,2,3]"));
  CHECK_FALSE(parse("{\"stage\":"));  // truncated
  CHECK_FALSE(parse("{\"stage\":5}"));  // wrong type

  const auto minimal = parse("{\"stage\":\"loading\"}");
  REQUIRE(minimal);
  CHECK(minimal->stage == "loading");
  CHECK(minimal->epoch == 0);
  CHECK_FALSE(minimal->fingerprint.valid);

  const auto bad_epoch = parse("{\"stage\":\"ingame\",\"epoch\":\"zz\"}");
  REQUIRE(bad_epoch);
  CHECK(bad_epoch->epoch == 0);
}

TEST_CASE("is_resumable_stage knows exactly the continuable stages", "[resume]") {
  for (const char* s : {"joining", "downloading", "preparing", "loading", "ingame", "rejoining"}) CHECK(is_resumable_stage(s));
  for (const char* s : {"", "other", "idle", "failed", "rejected"}) CHECK_FALSE(is_resumable_stage(s));
}
