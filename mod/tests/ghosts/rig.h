#pragma once
// Test rig for the ghost driver: a FakeWorld, a GhostDriver, an in-memory stash and a 60 fps frame loop that feeds analytic tracks
// through the REAL Replication wire encoding (tests/ghost/sim.h tracks), so the whole path decode -> interpolate -> place is exercised.
#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "fake_world.h"
#include "sim.h"

namespace x4mp::test {

class MemStash final : public session::IStash {
 public:
  void put(std::string_view key, std::string_view value) override { m[std::string(key)] = std::string(value); }
  std::optional<std::string> get(std::string_view key) const override {
    const auto it = m.find(std::string(key));
    if (it == m.end()) return std::nullopt;
    return it->second;
  }
  void erase(std::string_view key) override { m.erase(std::string(key)); }
  std::map<std::string, std::string> m;
};

struct Rig {
  FakeWorld world;
  std::unique_ptr<GhostDriver> driver;
  MemStash stash;
  std::vector<std::string> logs;
  std::int64_t base_us = 1'000'000'000;  // server time of track t = 0
  std::int64_t now = 1'000'000'000;
  std::map<std::uint32_t, Track> tracks;
  std::map<std::uint32_t, double> next_sample_s;  // per net_id: track time of the next 20 Hz sample
  double sample_hz = 20.0;
  double net_delay_ms = 15.0;
  std::uint16_t self = 1;
  std::uint64_t frames = 0;

  explicit Rig(GhostConfig cfg = {}) { make_driver(cfg); }

  void make_driver(GhostConfig cfg = {}) {
    driver = std::make_unique<GhostDriver>(cfg, [this](int level, const std::string& s) {
      if (level >= 2) logs.push_back(s);
      all_logs.push_back(s);
    });
    driver->bind_world(&world);
    driver->set_self(self);
  }
  std::vector<std::string> all_logs;

  static SpawnInfo info(std::uint32_t net, std::uint16_t player, const std::string& name, std::uint16_t controller, const std::string& owner = "x4mp_team_1",
                        const std::string& macro = "ship_arg_s_fighter_01_a_macro", std::uint16_t team = 1) {
    SpawnInfo i;
    i.net_id = net;
    i.player_id = player;
    i.controller = controller;
    i.team = team;
    i.name = name;
    i.macro = macro;
    i.owner = owner;
    return i;
  }

  void add(std::uint32_t net, Track t, const SpawnInfo& i, std::optional<InitialState> st = std::nullopt) {
    tracks[net] = std::move(t);
    next_sample_s[net] = static_cast<double>(now - base_us) / 1e6;
    driver->on_spawn(i, now, st);
  }

  // Encodes one sample as the server would send it (a Replication entry) and ingests it.
  void ingest(std::uint32_t net, const TrackPoint& p, std::int64_t t_us, std::int64_t arrival_us, std::uint16_t extra_flags = 0) {
    wire::ReplicationEntry e;
    e.net_id = net;
    e.mask = wire::kRepSector | wire::kRepPos | wire::kRepRot | wire::kRepVel | wire::kRepFlags | wire::kRepTime;
    e.sector = p.sector;
    e.pos_x = *wire::quantize_position(p.pos.x);
    e.pos_y = *wire::quantize_position(p.pos.y);
    e.pos_z = *wire::quantize_position(p.pos.z);
    e.yaw = *wire::quantize_rotation(p.rot.yaw);
    e.pitch = *wire::quantize_rotation(p.rot.pitch);
    e.roll = *wire::quantize_rotation(p.rot.roll);
    e.vel_x = *wire::quantize_velocity(p.vel.x, false);
    e.vel_y = *wire::quantize_velocity(p.vel.y, false);
    e.vel_z = *wire::quantize_velocity(p.vel.z, false);
    e.state_flags = static_cast<std::uint16_t>(p.flags | extra_flags);
    const std::int64_t tick = t_us + 10'000;
    e.time_ms = *wire::quantize_time_offset_ms(t_us, tick);
    std::array<std::uint8_t, 64> buf{};
    const std::size_t len = *wire::write_replication_entry(wire::MutableByteSpan(buf.data(), buf.size()), e);
    (void)driver->streams().ingest(static_cast<std::uint64_t>(tick), wire::ByteSpan(buf.data(), len), 1, arrival_us);
  }

  // Samples that arrived by `now` (sample time + 10 ms tick + net delay).
  std::function<std::uint16_t(std::uint32_t, double)> flags_for = [](std::uint32_t, double) -> std::uint16_t { return 0; };
  std::function<bool(std::uint32_t, double)> drop_sample = [](std::uint32_t, double) { return false; };

  void feed_due() {
    for (auto& [net, tr] : tracks) {
      double& ns = next_sample_s[net];
      while (true) {
        const std::int64_t t_us = base_us + static_cast<std::int64_t>(ns * 1e6);
        const std::int64_t arrival = t_us + 10'000 + static_cast<std::int64_t>(net_delay_ms * 1000.0);
        if (arrival > now) break;
        if (!drop_sample(net, ns)) ingest(net, tr(ns), t_us, arrival, flags_for(net, ns));
        ns += 1.0 / sample_hz;
      }
    }
  }

  FrameStats step(std::int64_t frame_us = 16'667) {
    now += frame_us;
    feed_due();
    ++frames;
    return driver->frame(now);
  }
  void run(double seconds) {
    const std::int64_t end = now + static_cast<std::int64_t>(seconds * 1e6);
    while (now < end) (void)step();
  }

  [[nodiscard]] double track_time_s(std::int64_t represented_us) const { return static_cast<double>(represented_us - base_us) / 1e6; }
};

}  // namespace x4mp::test
