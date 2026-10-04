// Budget of the ghost driver (docs/m3-plan.md 4.12): with 7 remote players the mod's main-thread work (Replication ingest + interpolation +
// driver frame) must stay far below 0.2 ms p95, and the steady frame path must not allocate. The game itself is a null world here: the
// cost of the game's own SetObjectSectorPos is the game's, not ours.
#include <algorithm>
#include <chrono>
#include <map>
#include <cstdio>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "alloc_counter.h"
#include "rig.h"

using namespace x4mp;
using namespace x4mp::test;

namespace {
// No-allocation world: every call is trivial. Names are stored as already-correct labels (the dress check compares them).
class NullWorld final : public IGhostWorld {
 public:
  std::uint64_t next = 100;
  std::map<std::uint64_t, std::string> names;
  std::uint64_t sector_id(std::uint16_t i) override { return 9000u + i; }
  std::uint64_t spawn(std::string_view, std::uint64_t, const Pose&, std::string_view) override { return next++; }
  void make_inert(std::uint64_t) override {}
  void place(std::uint64_t, std::uint64_t, const Pose&) override { ++places; }
  void dress(std::uint64_t id, std::string_view l, int) override { names[id].assign(l); }
  void hint_velocities(std::span<const VelocityHint> h) override { hints += h.size(); }
  void set_owner(std::uint64_t, std::string_view) override {}
  bool valid(std::uint64_t) override { return true; }
  bool wrecked(std::uint64_t) override { return false; }
  RemoveOutcome remove(std::uint64_t) override { return RemoveOutcome::Removed; }
  std::string id_code(std::uint64_t) override { return "ID"; }
  std::string name(std::uint64_t id) override { return names[id]; }
  std::optional<std::uint64_t> find_ghost_by_idcode(std::string_view) override { return std::nullopt; }
  FactionPresence faction(std::string_view) override { return FactionPresence::Present; }
  bool local_ship_within(std::uint64_t, const Vec3&, double) override { return false; }
  std::uint64_t places = 0, hints = 0;
};
}  // namespace

TEST_CASE("7 remote players: frame cost p95 under 0.2 ms and no allocation per frame", "[ghosts][perf]") {
  NullWorld world;
  GhostDriver driver({}, {});
  driver.bind_world(&world);
  driver.set_self(1);
  constexpr int kPlayers = 7;
  const std::int64_t base = 1'000'000'000;
  std::int64_t now = base;
  std::vector<Track> tracks;
  for (int p = 0; p < kPlayers; ++p) {
    tracks.push_back(circle_track(1000.0 + 100.0 * p, 150.0 + 20.0 * p));
    driver.on_spawn(Rig::info(static_cast<std::uint32_t>(100 + p), static_cast<std::uint16_t>(2 + p), "P" + std::to_string(p), static_cast<std::uint16_t>(2 + p)), now);
  }
  std::vector<double> next_s(kPlayers, 0.0);

  const auto feed = [&] {
    for (int p = 0; p < kPlayers; ++p) {
      while (true) {
        const std::int64_t t_us = base + static_cast<std::int64_t>(next_s[static_cast<std::size_t>(p)] * 1e6);
        const std::int64_t arrival = t_us + 25'000;
        if (arrival > now) break;
        const TrackPoint tp = tracks[static_cast<std::size_t>(p)](next_s[static_cast<std::size_t>(p)]);
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
        e.state_flags = 0;
        e.time_ms = *wire::quantize_time_offset_ms(t_us, t_us + 10'000);
        std::array<std::uint8_t, 64> buf{};
        const std::size_t len = *wire::write_replication_entry(wire::MutableByteSpan(buf.data(), buf.size()), e);
        (void)driver.streams().ingest(static_cast<std::uint64_t>(t_us + 10'000), wire::ByteSpan(buf.data(), len), 1, arrival);
        next_s[static_cast<std::size_t>(p)] += 0.05;
      }
    }
  };

  // warm-up: spawn everything, finish the dress checks (they allocate a name string once)
  for (int i = 0; i < 60 * 9; ++i) {
    now += 16'667;
    feed();
    (void)driver.frame(now);
  }
  REQUIRE(driver.counters().spawned == kPlayers);

  std::vector<double> cost_us;
  cost_us.reserve(60 * 12);
  x4mp::test::alloc_counter_arm();
  for (int i = 0; i < 60 * 12; ++i) {
    now += 16'667;
    const auto t0 = std::chrono::steady_clock::now();
    feed();
    (void)driver.frame(now);
    cost_us.push_back(std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count());
  }
  const std::uint64_t allocs = x4mp::test::alloc_counter_disarm();
  std::sort(cost_us.begin(), cost_us.end());
  const double p50 = cost_us[cost_us.size() / 2], p95 = cost_us[cost_us.size() * 95 / 100], mx = cost_us.back();
  std::printf("[perf] ghosts: 7 players, %zu frames: p50 %.1f us p95 %.1f us max %.1f us, allocations %llu, places %llu, hints %llu\n", cost_us.size(), p50, p95, mx,
              static_cast<unsigned long long>(allocs), static_cast<unsigned long long>(world.places), static_cast<unsigned long long>(world.hints));
  CHECK(p95 < 200.0);  // the budget (m3-plan 4.12); the real number is a few microseconds
  CHECK(allocs == 0);
  CHECK(world.places > 7 * 60 * 10);
}
