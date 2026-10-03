#include <catch2/catch_test_macros.hpp>

#include "host/frame_budget.h"

using x4mp::host::FrameBudget;

namespace {
std::int64_t g_now = 0;
std::int64_t fake_clock() noexcept { return g_now; }

void run_frame(FrameBudget& b, std::int64_t us) {
  b.begin_frame();
  g_now += us * 1000;
  b.end_frame();
}
}  // namespace

TEST_CASE("FrameBudget counts frames over budget", "[host][budget]") {
  g_now = 1'000'000;
  FrameBudget b(1500, &fake_clock);  // 1.5 ms
  CHECK(b.budget_ns() == 1'500'000);

  run_frame(b, 400);
  run_frame(b, 1500);  // exactly the budget: not over
  run_frame(b, 1501);  // over
  run_frame(b, 9000);  // way over
  const auto s = b.stats();
  CHECK(s.frames == 4);
  CHECK(s.over_budget == 2);
  CHECK(s.last_ns == 9'000'000);
  CHECK(s.max_ns == 9'000'000);
}

TEST_CASE("FrameBudget remaining and elapsed follow the injected clock", "[host][budget]") {
  g_now = 5'000;
  FrameBudget b(2000, &fake_clock);
  CHECK(b.elapsed_ns() == 0);               // no open frame
  CHECK(b.remaining_ns() == 2'000'000);     // a closed frame offers the full budget
  b.begin_frame();
  g_now += 500'000;
  CHECK(b.elapsed_ns() == 500'000);
  CHECK(b.remaining_ns() == 1'500'000);
  CHECK_FALSE(b.over_budget());
  g_now += 2'000'000;
  CHECK(b.remaining_ns() == 0);             // never negative
  CHECK(b.over_budget());
  CHECK(b.end_frame() == 2'500'000);
  CHECK(b.end_frame() == 0);                // no frame open: no-op
  CHECK(b.stats().frames == 1);
}

TEST_CASE("FrameBudget percentiles over the rolling window", "[host][budget]") {
  g_now = 0;
  FrameBudget b(10'000, &fake_clock);
  for (int i = 1; i <= 100; ++i) run_frame(b, i * 10);  // 10 .. 1000 us
  auto s = b.stats();
  CHECK(s.frames == 100);
  CHECK(s.max_ns == 1'000'000);
  CHECK(s.p50_ns >= 490'000);
  CHECK(s.p50_ns <= 520'000);
  CHECK(s.p95_ns >= 940'000);
  CHECK(s.p95_ns <= 960'000);
  CHECK(s.avg_ns == 505'000);

  b.reset_window();
  s = b.stats();
  CHECK(s.frames == 100);   // lifetime counters survive
  CHECK(s.max_ns == 0);
  CHECK(s.p95_ns == 0);
}

TEST_CASE("FrameBudget window keeps only the last kWindow frames", "[host][budget]") {
  g_now = 0;
  FrameBudget b(100'000, &fake_clock);
  for (std::size_t i = 0; i < FrameBudget::kWindow; ++i) run_frame(b, 5000);  // old, slow frames
  for (std::size_t i = 0; i < FrameBudget::kWindow; ++i) run_frame(b, 100);   // fully replace them
  const auto s = b.stats();
  CHECK(s.p95_ns == 100'000);
  CHECK(s.p50_ns == 100'000);
  CHECK(s.max_ns == 5'000'000);  // max is since the last reset, not windowed
}

TEST_CASE("FrameBudget budget can change at runtime", "[host][budget]") {
  g_now = 0;
  FrameBudget b(1000, &fake_clock);
  run_frame(b, 1200);
  CHECK(b.stats().over_budget == 1);
  b.set_budget_us(2000);
  run_frame(b, 1200);
  CHECK(b.stats().over_budget == 1);
  CHECK(b.stats().budget_ns == 2'000'000);
}
