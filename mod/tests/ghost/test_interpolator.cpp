#include <cmath>

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
  for (int ms = 1110; ms <= 1175; ms += 5) {  // t 1010..1075: the last part is between 1050 and the teleport sample -> holds 1050 (M3-31: the capped extrapolation of it)
    p = in.render(ms * kMs);
    CHECK_FALSE(p.snapped);
  }
  CHECK(p.pos.x == Approx(7.5).margin(0.5));  // 5 + 100 m/s x 25 ms, the same pose the render showed before the sample arrived
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

// M3-31 (Finding 18): a long silent gap (a superhighway) followed by a teleport sample in another sector. The render goes stale (hidden) in the gap
// and must NOT show the old raw sample again before the new one is reached; the move is one snap into the new sector.
TEST_CASE("a long gap before a sector change stays hidden until the new sample, then snaps once (M3-31)", "[ghost][interp]") {
  Interpolator in;
  in.push(mk(1000, 0, 100), 0);
  in.push(mk(1050, 5, 100), 0);
  RenderPose p;
  int snaps = 0;
  std::uint16_t last_sector = 0;
  int sector_changes = 0;
  for (int ms = 1100; ms < 20000; ms += 16) {  // 19 s of silence
    p = in.render(ms * kMs);
    if (!p.hidden) {
      CHECK(p.sector == 1);
      last_sector = p.sector;
    }
  }
  CHECK(p.hidden);
  CHECK(p.state == PoseState::Stale);
  in.push(mk(20000, 900, 100, 2, kTeleport), 0);
  in.push(mk(20050, 905, 100, 2, 0), 0);
  for (int ms = 20060; ms < 20400; ms += 16) {  // the new sample arrives while the render is still ~100 ms behind it
    p = in.render(ms * kMs);
    if (ms < 20100) {
      CHECK(p.hidden);  // not the old sample, not the old sector
      CHECK(p.state == PoseState::Stale);
    }
    if (!p.hidden) {
      snaps += p.snapped ? 1 : 0;
      CHECK(p.sector == 2);
      if (last_sector != 0 && p.sector != last_sector) ++sector_changes;
      last_sector = p.sector;
    }
  }
  CHECK(snaps == 1);
  CHECK(sector_changes == 1);
}

// M3-32 (Finding 21), the client's view of Alice's superhighway transit: samples at 20 Hz up to 4.8 km/s, then the Hidden state, then (3.9 s of silence
// later) the exit in another sector at 2 Hz (a player ship outside the followed sectors). The Hidden state hides the ghost one display delay after its
// own time (not seconds later), and the reappearance snaps to the first new sample and follows the slow 2 Hz stream without the old motion.
TEST_CASE("a Hidden sample hides the ghost at once; the exit snaps to the new sector and does not extrapolate stale speed (M3-32)", "[ghost][interp][m332]") {
  Interpolator in;
  double x = 0;
  std::int64_t t = 1000;
  for (int i = 0; i < 100; ++i) {  // 5 s: 3 -> 4.8 km/s
    const double v = 3000 + 360 * (i / 20.0);
    in.push(mk(t, x, v), t * kMs + 40 * kMs);
    x += v * 0.05;
    t += 50;
  }
  RenderPose p;
  std::int64_t now_ms = 1000 + 300;
  for (; now_ms < t + 300; now_ms += 16) p = in.render(now_ms * kMs);
  CHECK(!p.hidden);
  // the Hidden state (sample time t), at the last pose
  const std::int64_t hidden_t = t;
  in.push(mk(hidden_t, x, 4800, 1, kHidden | kInHighway), hidden_t * kMs + 40 * kMs);
  std::int64_t hidden_seen = 0;
  for (; now_ms < hidden_t + 1500; now_ms += 16) {
    p = in.render(now_ms * kMs);
    if (p.hidden && hidden_seen == 0) hidden_seen = now_ms;
  }
  REQUIRE(hidden_seen != 0);
  CHECK(hidden_seen - hidden_t <= 350);  // the display delay, not seconds
  CHECK(p.hidden);
  // 3.9 s later: the teleport state in sector 2, then 2 Hz samples at a slowing speed
  const std::int64_t exit_t = hidden_t + 3900;
  double ex = 500;
  in.push(mk(exit_t, ex, 0, 2, kTeleport), exit_t * kMs + 250 * kMs);
  double v = 1500;
  std::int64_t st = exit_t;
  int shown_frames = 0;
  int snaps = 0;
  double worst = 0;
  for (; now_ms < exit_t + 6000; now_ms += 16) {
    while (st + 500 <= now_ms - 250) {  // a sample is "received" 250 ms after its time
      st += 500;
      ex += v * 0.5;
      v = std::max(200.0, v - 500);
      in.push(mk(st, ex, v, 2, 0), (st + 250) * kMs);
    }
    p = in.render(now_ms * kMs);
    if (p.hidden) continue;
    ++shown_frames;
    CHECK(p.sector == 2);
    if (p.snapped) ++snaps;
    worst = std::max(worst, std::fabs(p.pos.x - ex));
  }
  CHECK(shown_frames > 100);
  CHECK(snaps <= 2);  // the reappearance, plus at most one correction of the decelerating 2 Hz stream (no flashing)
  CHECK(worst < 1000.0);
}
