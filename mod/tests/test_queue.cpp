#include <atomic>
#include <cstdint>
#include <thread>

#include <catch2/catch_test_macros.hpp>

#include "core/queue/queue.h"

using x4mp::queue::SpscRing;

TEST_CASE("spsc ring basic push/pop and capacity", "[queue]") {
  SpscRing<int, 4> q;
  CHECK(SpscRing<int, 4>::capacity() == 3);
  CHECK_FALSE(q.try_pop().has_value());
  CHECK(q.try_push(1));
  CHECK(q.try_push(2));
  CHECK(q.try_push(3));
  CHECK_FALSE(q.try_push(4));  // full
  CHECK(q.try_pop() == 1);
  CHECK(q.try_push(4));        // wrapped
  CHECK(q.try_pop() == 2);
  CHECK(q.try_pop() == 3);
  CHECK(q.try_pop() == 4);
  CHECK_FALSE(q.try_pop().has_value());
}

TEST_CASE("spsc ring preserves order across threads", "[queue]") {
  constexpr std::uint32_t kCount = 200000;
  SpscRing<std::uint32_t, 64> q;
  std::atomic<bool> in_order{true};
  std::thread consumer([&] {
    std::uint32_t expected = 0;
    while (expected < kCount) {
      if (auto v = q.try_pop()) {
        if (*v != expected) in_order = false;
        ++expected;
      }
    }
  });
  for (std::uint32_t i = 0; i < kCount;) {
    if (q.try_push(i)) ++i;
  }
  consumer.join();
  CHECK(in_order.load());
}