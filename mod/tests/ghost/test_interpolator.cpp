#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include "core/ghost/interpolator.h"

using namespace x4mp::ghost;
using Catch::Approx;

namespace {
Sample mk(std::int64_t t_ms, double x, double vx = 100.0, std::uint16_t sector = 1, std::uint16_t flags = 0) {
  Sample s;
  s.t_us = t_ms * 1000;
  s.sector = sector;
  s.flags = flags;
  s.pos = {x, 0, 0};
  s.vel = {vx, 0, 0};
  return s;
}
constexpr std::int64_t kMs = 1000;
}  // namespace

TEST_CASE("empty interpolator shows nothing", "[ghost][interp]") {
  Interpolator in;
  const RenderPose p = in.render(1'000'000);
  CHECK(p.state == PoseState::Empty);
  CHECK(p.hidden);
}

TEST_CASE("samples inserted out of order are sorted; too old for a full ring are dropped", "[ghost][interp]") {
  Interpolator in;
  in.push(mk(1000, 0), 0);
  in.push(mk(1100, 10), 0);
  in.push(mk(1050, 5), 0);  // out of order
  CHECK(in.sample_count() == 3);
  in.push(mk(1050, 5), 0);  // duplicate time replaces
  CHECK(in.sample_count() == 3);
  for (int i = 0; i < 10; ++i) in.push(mk(2000 + i * 50, 100.0 + i), 0);
  CHECK(in.sample_count() == Interpolator::kRing);
  in.push(mk(500, 0), 0);  // older than everything in a full ring
  CHECK(in.sample_count() == Interpolator::kRing);
  CHECK(in.newest_sample_us() == 2450 * kMs);
}

TEST_CASE("interpolates between samples (hermite on a straight line is linear)", "[ghost][interp]") {
  Interpolator in;
  in.push(mk(1000, 0, 100), 0);
  in.push(mk(1050, 5, 100), 0);
  in.push(mk(1100, 10, 100), 0);
  // render time = now - 100 ms default delay: now 1175 -> t 1075
  const RenderPose p = in.render(1175 * kMs);
  CHECK(p.state == PoseState::Interpolating);
  CHECK(p.pos.x == Approx(7.5).margin(1e-9));
  CHECK(p.render_t_us == 1075 * kMs);
  CHECK_FALSE(p.hidden);
}

TEST_CASE("extrapolates 500 ms then holds, hides when stale", "[ghost][interp]") {
  Interpolator in;
  in.push(mk(1000, 0, 100), 0);
  in.push(mk(1050, 5, 100), 0);
  // newest sample t=1050 ms. render time = now - 100 ms.
  RenderPose p = in.render(1250 * kMs);  // t = 1150: 100 ms past newest
  CHECK(p.state == PoseState::Extrapolating);
  CHECK(p.pos.x == Approx(15.0).margin(1e-6));
  p = in.render(1350 * kMs);  // 1250: 200 ms past
  CHECK(p.pos.x == Approx(25.0).margin(1e-6));
  p = in.render(1700 * kMs);  // 1600: 550 ms past -> held at 500 ms
  CHECK(p.state == PoseState::Held);
  CHECK(p.pos.x == Approx(5.0 + 50.0).margin(1e-6));
  CHECK(p.represented_t_us == (1050 + 500) * kMs);
  p = in.render(3000 * kMs);
  CHECK(p.state == PoseState::Held);
  CHECK(p.pos.x == Approx(55.0).margin(1e-6));
  CHECK_FALSE(p.hidden);
  p = in.render(6300 * kMs);  // 5.15 s past the newest
  CHECK(p.state == PoseState::Stale);
  CHECK(p.hidden);
  // data comes back: shown again, with a snap
  in.push(mk(6300, 400, 100), 0);
  in.push(mk(6350, 405, 100), 0);
  p = in.render(6450 * kMs);
  CHECK_FALSE(p.hidden);
  CHECK(p.snapped);
}

TEST_CASE("Teleport and sector change snap, never interpolate", "[ghost][interp]") {
  Interpolator in;
  in.push(mk(1000, 0, 100), 0);
  in.push(mk(1050, 5, 100), 0);
  in.push(mk(1100, 100000, 100, 1, kTeleport), 0);  // same sector teleport, far away
  in.push(mk(1150, 100005, 100, 1, 0), 0);
  (void)in.render(1100 * kMs);           // t 1000: first frame (always a snap)
  RenderPose p;
  for (int ms = 1110; ms <= 1175; ms += 5) {  // t 1010..1075: the last part is between 1050 and the teleport sample -> holds 1050
    p = in.render(ms * kMs);
    CHECK_FALSE(p.snapped);
  }
  CHECK(p.pos.x == Approx(5.0).margin(0.5));
  p = in.render(1210 * kMs);  // t 1110: governed by the teleport sample
  CHECK(p.snapped);
  CHECK(p.pos.x == Approx(100000.0 + 10 * 0.1).margin(1e-6));
  p = in.render(1226 * kMs);
  CHECK_FALSE(p.snapped);

  Interpolator in2;
  in2.push(mk(1000, 0, 100, 1), 0);
  in2.push(mk(1050, 5, 100, 1), 0);
  in2.push(mk(1100, 0, 100, 2), 0);  // sector change without Teleport flag (the flag packet was lost)
  in2.push(mk(1150, 5, 100, 2), 0);
  (void)in2.render(1100 * kMs);
  (void)in2.render(1175 * kMs);
  p = in2.render(1210 * kMs);
  CHECK(p.snapped);
  CHECK(p.sector == 2);
}

TEST_CASE("Hidden flag hides, via sample or apply_state, and shows again with a snap", "[ghost][interp]") {
  Interpolator in;
  in.push(mk(1000, 0, 100), 0);
  in.push(mk(1050, 5, 100), 0);
  in.push(mk(1100, 10, 100), 0);
  (void)in.render(1175 * kMs);
  in.apply_state(1100 * kMs, kHidden, 255, 255);  // flags-only entry
  RenderPose p = in.render(1250 * kMs);
  CHECK(p.hidden);
  in.apply_state(1100 * kMs, 0, 255, 255);
  p = in.render(1266 * kMs);
  CHECK_FALSE(p.hidden);
  CHECK(p.snapped);
}

TEST_CASE("a late correction blends over 200 ms; a huge one snaps", "[ghost][interp]") {
  // Two samples, then silence -> extrapolation at 100 m/s. A new sample 20 m behind the extrapolation arrives.
  Interpolator in;
  in.push(mk(1000, 0, 100), 0);
  in.push(mk(1050, 5, 100), 0);
  RenderPose p = in.render(1350 * kMs);  // t=1250: extrapolated x = 25
  CHECK(p.pos.x == Approx(25.0).margin(1e-6));
  in.push(mk(1100, 0, 100), 0);  // truth: only x = 0 at t 1100 (ship braked hard) -> true pose at 1250 is 15 if it kept speed
  in.push(mk(1300, 10, 0), 0);   // and it stopped: x = 10 at 1300
  p = in.render(1366 * kMs);     // t=1266: the raw pose jumped back; the displayed pose must not
  CHECK_FALSE(p.snapped);
  CHECK(p.pos.x > 20.0);         // still near where we were (25 + motion), the blend is pulling it back
  RenderPose last = p;
  for (int i = 0; i < 20; ++i) last = in.render((1366 + 17 * (i + 1)) * kMs);
  CHECK(last.pos.x < 15.0);      // after > 300 ms the offset has decayed

  // Huge error snaps.
  Interpolator in2;
  in2.push(mk(1000, 0, 100), 0);
  in2.push(mk(1050, 5, 100), 0);
  (void)in2.render(1350 * kMs);
  in2.push(mk(1300, 5000, 100), 0);
  in2.push(mk(1350, 5005, 100), 0);
  const RenderPose q = in2.render(1366 * kMs);
  CHECK(q.snapped);
}

TEST_CASE("delay controller: default 100 ms, adapts to jitter within 80..250 ms, slews slowly", "[ghost][interp][delay]") {
  InterpolatorConfig cfg;
  DelayController d(cfg);
  CHECK(d.current_us() == 100'000);
  // very steady link: target falls to the floor
  for (int i = 0; i < 40; ++i) d.on_sample(1'000'000 + i * 50'000 + 15'000, 1'000'000 + i * 50'000, 50'000);
  CHECK(d.target_us() == 80'000);
  d.step(1'000'000);
  CHECK(d.current_us() == 90'000);  // 10 ms per second down
  // very jittery link
  DelayController j(cfg);
  for (int i = 0; i < 64; ++i) j.on_sample(1'000'000 + i * 50'000 + 15'000 + (i % 4) * 40'000, 1'000'000 + i * 50'000, 50'000);
  CHECK(j.target_us() > 150'000);
  CHECK(j.target_us() <= 250'000);
  j.step(1'000'000);
  CHECK(j.current_us() == 200'000);  // 100 ms per second up
  // absurd jitter is clamped
  DelayController k(cfg);
  for (int i = 0; i < 64; ++i) k.on_sample(1'000'000 + i * 50'000 + (i % 2) * 900'000, 1'000'000 + i * 50'000, 50'000);
  CHECK(k.target_us() == 250'000);
}
