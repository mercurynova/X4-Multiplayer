// core/ghost HintGate (F32): the whole hint rule in one place: speed cap AND settle time since the last jump.
// Times are exact binary fractions so the settle comparisons are not at the mercy of floating point rounding.
#include <catch2/catch_test_macros.hpp>

#include "core/ghost/hint_gate.h"

using x4mp::ghost::HintGate;
using x4mp::ghost::HintGateConfig;

TEST_CASE("HintGate: nothing is allowed before any valid sample", "[ghost][hintgate]") {
  const HintGate g({1000.0, 0.5});
  CHECK_FALSE(g.allow(0.0, 10.0));
  CHECK_FALSE(g.allow(100.0, 10.0));
  CHECK(g.calm_s(5.0) == 0.0);
}

TEST_CASE("HintGate: allow() needs settle_s of calm samples AND a speed at or below the cap", "[ghost][hintgate]") {
  HintGate g({1000.0, 0.5});
  g.speed(10.0, 300.0);
  CHECK_FALSE(g.allow(10.0, 300.0));
  CHECK_FALSE(g.allow(10.25, 300.0));
  CHECK(g.allow(10.5, 300.0));
  CHECK(g.allow(10.5, 1000.0));        // the cap itself is allowed
  CHECK_FALSE(g.allow(10.5, 1000.1));  // the speed being hinted is above the cap: no hint, whatever the calm history
  CHECK(g.calm_s(10.5) == 0.5);
}

TEST_CASE("HintGate: a sample above the cap or a discontinuity restarts the calm period", "[ghost][hintgate]") {
  HintGate g({1000.0, 0.5});
  g.speed(0.0, 100.0);
  REQUIRE(g.allow(1.0, 100.0));
  g.speed(1.0, 5000.0);
  CHECK_FALSE(g.allow(1.25, 100.0));
  g.speed(1.25, 100.0);  // first calm sample after the burst starts a new period
  CHECK_FALSE(g.allow(1.5, 100.0));
  CHECK(g.allow(1.75, 100.0));
  g.discontinuity();
  CHECK_FALSE(g.allow(10.0, 100.0));
  CHECK(g.calm_s(10.0) == 0.0);
  g.speed(10.0, 100.0);
  CHECK_FALSE(g.allow(10.25, 100.0));
  CHECK(g.allow(10.5, 100.0));
}

TEST_CASE("HintGate: the configured settle time and cap are honoured", "[ghost][hintgate]") {
  HintGate g(HintGateConfig{50.0, 2.0});
  g.speed(0.0, 10.0);
  CHECK_FALSE(g.allow(1.75, 10.0));
  CHECK(g.allow(2.0, 10.0));
  CHECK_FALSE(g.allow(2.0, 60.0));
  CHECK(g.config().settle_s == 2.0);
}
