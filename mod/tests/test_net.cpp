// Pure-logic tests for core/net: backoff schedule and the clock-sync estimator. Socket tests are in
// test_net_socket.cpp.

#include <catch2/catch_test_macros.hpp>

#include "core/net/backoff.h"
#include "core/net/clock_sync.h"
#include "core/net/net.h"

using namespace x4mp::net;

TEST_CASE("net exposes the shared protocol constants", "[net]") {
  CHECK(protocol_version_string() == "0.1");
  CHECK(default_tcp_port() == 47780);
}

TEST_CASE("backoff is 1/2/4/8/10 s, capped, and resettable", "[net][backoff]") {
  Backoff b;
  CHECK(b.peek_delay_ms() == 1000);
  CHECK(b.next_delay_ms() == 1000);
  CHECK(b.next_delay_ms() == 2000);
  CHECK(b.next_delay_ms() == 4000);
  CHECK(b.next_delay_ms() == 8000);
  CHECK(b.next_delay_ms() == 10000);
  for (int i = 0; i < 50; ++i) CHECK(b.next_delay_ms() == 10000);  // stays capped, never overflows
  b.reset();
  CHECK(b.next_delay_ms() == 1000);
  CHECK(b.attempts() == 1);
}

TEST_CASE("disconnect codes that must not trigger a reconnect", "[net]") {
  CHECK(is_no_retry_code(3));    // Kicked
  CHECK(is_no_retry_code(4));    // Banned
  CHECK(is_no_retry_code(5));    // Superseded
  CHECK(is_no_retry_code(12));   // GameVersionMismatch
  CHECK_FALSE(is_no_retry_code(2));   // ServerShutdown (retry_after)
  CHECK_FALSE(is_no_retry_code(30));  // HeartbeatTimeout
  CHECK_FALSE(is_no_retry_code(33));  // SlowConsumer
}

TEST_CASE("clock sync: rtt and offset math (symmetric path)", "[net][clock]") {
  ClockSync c;
  // Server clock is 5 s ahead. Local send at 1,000,000. One-way 20 ms each way, server holds the ping 100 us.
  const std::int64_t send = 1'000'000;
  const std::int64_t recv_srv = send + 20'000 + 5'000'000;
  const std::int64_t reply_srv = recv_srv + 100;
  const std::int64_t now = send + 20'000 + 100 + 20'000;
  const auto s = c.add_pong(send, recv_srv, reply_srv, now);
  REQUIRE(s.has_value());
  CHECK(s->rtt_us == 40'000);          // hold time removed
  CHECK(s->offset_us == 5'000'000 - 0);  // recv - (send + rtt/2) = 5,000,000
  CHECK(c.smoothed_rtt_us() == 40'000);
  CHECK(c.last_rtt_us() == 40'000);
}

TEST_CASE("clock sync: uses the lowest-rtt of the last 16 samples", "[net][clock]") {
  ClockSync c;
  // 15 samples with rtt 50 ms and offset 1000, then one with rtt 10 ms and offset 400 (the "true" one).
  for (int i = 0; i < 15; ++i) {
    const std::int64_t send = i * 1'000'000;
    // offset 1000 (server ahead), one way 25 ms, no hold
    (void)c.add_pong(send, send + 25'000 + 1000, send + 25'000 + 1000, send + 50'000);
  }
  const std::int64_t send = 100'000'000;
  (void)c.add_pong(send, send + 5'000 + 400, send + 5'000 + 400, send + 10'000);
  const auto best = c.best();
  REQUIRE(best.has_value());
  CHECK(best->rtt_us == 10'000);
  CHECK(best->offset_us == 400);
  CHECK(c.sample_count() == 16);

  // 16 more bad samples push the good one out of the window: the best becomes the 50 ms ones again.
  for (int i = 0; i < 16; ++i) {
    const std::int64_t s2 = 200'000'000 + i * 1'000'000;
    (void)c.add_pong(s2, s2 + 25'000 + 1000, s2 + 25'000 + 1000, s2 + 50'000);
  }
  CHECK(c.best()->rtt_us == 50'000);
  CHECK(c.sample_count() == 16);
}

TEST_CASE("clock sync: rejects impossible samples", "[net][clock]") {
  ClockSync c;
  CHECK_FALSE(c.add_pong(1000, 0, 0, 500).has_value());      // now before send: negative rtt
  CHECK_FALSE(c.add_pong(0, 100, 50, 1000).has_value());     // reply before recv: negative hold
  CHECK_FALSE(c.has_samples());
}

TEST_CASE("clock sync: offset steps first, slews 1 ms/s afterwards, steps over 50 ms", "[net][clock]") {
  ClockSync c;
  // First sample: offset 10,000 us, rtt 0.
  (void)c.add_pong(0, 10'000, 10'000, 0);
  CHECK(c.update(0) == 10'000);  // first sample steps
  CHECK(c.has_offset());
  // A better sample (lower rtt can't go below 0, use a tie: newest wins) with offset 20,000 -> error 10 ms: slew.
  (void)c.add_pong(1'000'000, 1'020'000, 1'020'000, 1'000'000);
  CHECK(c.update(1'000'000) == 11'000);  // 1 s elapsed: +1 ms
  CHECK(c.update(1'500'000) == 11'500);  // 0.5 s: +0.5 ms
  CHECK(c.update(60'000'000) == 20'000);  // plenty of time: converges, no overshoot
  // A new best sample 200 ms away: error > 50 ms steps immediately.
  (void)c.add_pong(61'000'000, 61'220'000, 61'220'000, 61'000'000);
  CHECK(c.update(61'000'000) == 220'000);
  c.reset();
  CHECK_FALSE(c.has_samples());
  CHECK_FALSE(c.has_offset());
}
