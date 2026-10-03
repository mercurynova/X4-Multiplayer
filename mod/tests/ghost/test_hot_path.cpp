// The per-frame path (decode + ingest + render + statistics for 7 remote players) must not allocate (docs/m3-plan.md 4.12).
// x4mp_ghost_tests replaces the global operator new (alloc_counter.cpp); the count is taken only while armed.
#include <array>
#include <chrono>
#include <cstdio>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "alloc_counter.h"
#include "sim.h"

using namespace x4mp;
using namespace x4mp::test;

TEST_CASE("no allocation per frame with 7 remote players", "[ghost][hot]") {
  constexpr int kPlayers = 7;
  constexpr double kSeconds = 12.0;
  struct Msg {
    std::int64_t arrival_us;
    std::uint64_t server_time_us;
    std::array<std::uint8_t, 64> bytes;
    std::size_t len;
  };
  std::vector<Msg> msgs;
  msgs.reserve(static_cast<std::size_t>(kPlayers * kSeconds * 20 + 16));
  const std::int64_t base = 1'000'000;
  for (double t = 0; t < kSeconds; t += 0.05) {
    for (int p = 0; p < kPlayers; ++p) {
      const TrackPoint tp = circle_track(1000.0 + 100.0 * p, 150.0 + 20.0 * p)(t);
      const std::int64_t t_us = base + static_cast<std::int64_t>(t * 1e6);
      wire::ReplicationEntry e;
      e.net_id = static_cast<std::uint32_t>(100 + p);
      e.mask = wire::kRepSector | wire::kRepPos | wire::kRepRot | wire::kRepVel | wire::kRepFlags | wire::kRepTime;
      e.sector = tp.sector;
      e.pos_x = *wire::quantize_position(tp.pos.x);
      e.pos_y = *wire::quantize_position(tp.pos.y);
      e.pos_z = *wire::quantize_position(tp.pos.z);
      e.yaw = *wire::quantize_rotation(tp.rot.yaw);
      e.pitch = *wire::quantize_rotation(tp.rot.pitch);
      e.roll = *wire::quantize_rotation(tp.rot.roll);
      e.vel_x = *wire::quantize_velocity(tp.vel.x, false);
      e.vel_y = *wire::quantize_velocity(tp.vel.y, false);
      e.vel_z = *wire::quantize_velocity(tp.vel.z, false);
      e.time_ms = 0;
      Msg m;
      m.len = *wire::write_replication_entry(wire::MutableByteSpan(m.bytes.data(), m.bytes.size()), e);
      m.server_time_us = static_cast<std::uint64_t>(t_us);
      m.arrival_us = t_us + 20'000 + (p * 3'000);
      msgs.push_back(m);
    }
  }

  StreamSet set;
  for (int p = 0; p < kPlayers; ++p) set.ensure(static_cast<std::uint32_t>(100 + p));  // spawn: allocates, not the frame path
  std::vector<Interpolator*> ins;
  for (int p = 0; p < kPlayers; ++p) ins.push_back(set.find(static_cast<std::uint32_t>(100 + p)));
  ins.reserve(16);

  std::size_t next = 0;
  std::uint64_t frames = 0;
  double checksum = 0;
  std::vector<std::int64_t> frame_ns;
  frame_ns.reserve(1000);

  x4mp::test::alloc_counter_arm();
  for (std::int64_t now = base; now < base + static_cast<std::int64_t>((kSeconds - 1) * 1e6); now += 16'667) {
    const auto c0 = std::chrono::steady_clock::now();
    while (next < msgs.size() && msgs[next].arrival_us <= now) {
      const Msg& m = msgs[next++];
      (void)set.ingest(m.server_time_us, x4mp::wire::ByteSpan(m.bytes.data(), m.len), 1, m.arrival_us);
    }
    for (Interpolator* in : ins) {
      const RenderPose p = in->render(now);
      checksum += p.pos.x;
    }
    if (++frames % 300 == 0) {
      for (Interpolator* in : ins) {
        const SyncReport r = in->stats().report(true);
        checksum += r.err_p95;
      }
    }
    if (frame_ns.size() < frame_ns.capacity())
      frame_ns.push_back(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - c0).count());
  }
  const std::uint64_t allocs = x4mp::test::alloc_counter_disarm();

  std::sort(frame_ns.begin(), frame_ns.end());
  const double p95_us = static_cast<double>(frame_ns[frame_ns.size() * 95 / 100]) / 1000.0;
  std::printf("[ghost-hot] frames=%llu allocations=%llu cpu p95=%.1f us per frame (7 players, decode+interp+stats)\n",
              static_cast<unsigned long long>(frames), static_cast<unsigned long long>(allocs), p95_us);
  std::fflush(stdout);
  CHECK(frames > 600);
  CHECK(allocs == 0);
  CHECK(checksum != 0.0);   // keep the work observable
  CHECK(p95_us < 200.0);    // the whole mod budget is 0.2 ms p95 per frame (4.12)
}
