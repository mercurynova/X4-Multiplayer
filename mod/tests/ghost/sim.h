#pragma once
// Test harness for core/ghost: analytic tracks, a lossy jittery link, the real Replication wire encoding/decoding, and a 60 fps
// frame loop. Server time = the receiver's clock here (perfect clock sync): clock error is core/net's business, not the interpolator's.
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <functional>
#include <numbers>
#include <vector>

#include "core/ghost/replication.h"
#include "x4mp/wire.h"

namespace x4mp::test {

using namespace x4mp::ghost;

struct TrackPoint {
  Vec3 pos{};
  Vec3 vel{};
  Euler rot{};
  std::uint16_t sector = 1;
  std::uint16_t flags = 0;
};
using Track = std::function<TrackPoint(double t_s)>;  // t_s since the start of the track

inline Track line_track(double speed) {
  return [=](double t) {
    TrackPoint p;
    const Vec3 dir{0.6, 0.0, 0.8};
    p.pos = dir * (speed * t);
    p.vel = dir * speed;
    p.rot = {0.6435, 0.0, 0.0};
    return p;
  };
}
inline Track circle_track(double radius, double speed) {
  return [=](double t) {
    TrackPoint p;
    const double w = speed / radius, a = w * t;
    p.pos = {radius * std::cos(a), 20.0 * std::sin(a * 0.5), radius * std::sin(a)};
    p.vel = {-radius * w * std::sin(a), 10.0 * w * std::cos(a * 0.5), radius * w * std::cos(a)};
    p.rot = {std::atan2(p.vel.x, p.vel.z), 0.0, 0.3};
    return p;
  };
}
// Accelerates at `acc` m/s^2 until `vmax`, then cruises.
inline Track accel_track(double acc, double vmax) {
  return [=](double t) {
    TrackPoint p;
    const double t_a = vmax / acc;
    double s, v;
    if (t < t_a) {
      s = 0.5 * acc * t * t;
      v = acc * t;
    } else {
      s = 0.5 * acc * t_a * t_a + vmax * (t - t_a);
      v = vmax;
    }
    const Vec3 dir{0.6, 0.0, 0.8};
    p.pos = dir * s;
    p.vel = dir * v;
    p.rot = {0.6435, 0.0, 0.0};
    return p;
  };
}
// Straight flight at `speed`; every `period_s` the ship jumps through a gate: new sector, position reset (Teleport on the first sample).
inline Track gate_track(double speed, double period_s) {
  return [=](double t) {
    TrackPoint p;
    const int seg = static_cast<int>(std::floor(t / period_s));
    const double local = t - seg * period_s;
    const Vec3 dir{0.0, 0.0, 1.0};
    p.pos = Vec3{1000.0 * seg, 0.0, -1500.0} + dir * (speed * local);
    p.vel = dir * speed;
    p.sector = static_cast<std::uint16_t>(1 + seg);
    p.rot = {0.0, 0.0, 0.0};
    if (local < 0.05) p.flags |= kTeleport;  // the first sample of a segment (20 Hz) carries it
    return p;
  };
}

// Worst case: the heading changes by 90 degrees instantly every `period_s` (a velocity step, no turn radius). No real ship does this.
inline Track jink_track(double speed, double period_s) {
  return [=](double t) {
    TrackPoint p;
    const int seg = static_cast<int>(std::floor(t / period_s));
    Vec3 pos{};
    for (int k = 0; k < seg; ++k) {
      const double th = k * std::numbers::pi / 2;
      pos = pos + Vec3{std::cos(th), 0.0, std::sin(th)} * (speed * period_s);
    }
    const double th = seg * std::numbers::pi / 2;
    const Vec3 dir{std::cos(th), 0.0, std::sin(th)};
    p.pos = pos + dir * (speed * (t - seg * period_s));
    p.vel = dir * speed;
    p.rot = {th, 0.0, 0.0};
    return p;
  };
}

struct NetModel {
  double loss = 0.0;        // probability a sample is lost
  double base_ms = 15.0;    // one-way delay
  double jitter_ms = 0.0;   // uniform extra delay in [0, jitter_ms]
  double burst_loss_at_s = -1;   // all samples in [at, at+burst_len) are lost
  double burst_len_s = 0.0;
  std::uint32_t seed = 1;
};

// xorshift: deterministic across platforms.
struct Rng {
  std::uint64_t s;
  explicit Rng(std::uint32_t seed) : s(0x9E3779B97F4A7C15ull ^ seed) {}
  double next() {
    s ^= s << 13;
    s ^= s >> 7;
    s ^= s << 17;
    return static_cast<double>(s >> 11) / 9007199254740992.0;
  }
};

inline double pct(std::vector<double> v, double p) {
  if (v.empty()) return 0;
  std::sort(v.begin(), v.end());
  return v[static_cast<std::size_t>(p * static_cast<double>(v.size() - 1) + 0.5)];
}

struct SimResult {
  // What the mod itself measures ([sync] windows of 5 s, worst window / summed counts), against the RECEIVED samples.
  SyncReport report;
  // Against the analytic track (the real truth, lost samples included): rendered pose vs track at the represented server time.
  std::size_t n_true = 0;
  double true_p50 = 0, true_p95 = 0, true_max = 0;
  double true_steady_p95 = 0, true_steady_max = 0;   // speed < 300 m/s
  double true_fast_p95 = 0, true_fast_max = 0;       // speed >= 300 m/s
  std::uint64_t jumps_snapped = 0;   // frames flagged snapped after the warm-up
  std::int64_t delay_min_us = 0, delay_max_us = 0, delay_end_us = 0;
  std::size_t lost = 0, sent = 0;
  std::uint64_t hidden_frames = 0;
};

struct SimOptions {
  double duration_s = 60.0;
  double warmup_s = 3.0;
  double sample_hz = 20.0;
  double frame_hz = 60.0;
  InterpolatorConfig cfg{};
  std::uint64_t base_us = 1'000'000;  // server time of track t = 0
};

inline SimResult run_track(const Track& track, const NetModel& net, const SimOptions& opt = {}) {
  struct Msg {
    std::int64_t arrival_us;
    std::uint64_t server_time_us;
    std::array<std::uint8_t, 64> bytes;
    std::size_t len;
  };
  std::vector<Msg> msgs;
  Rng rng(net.seed);
  const double dt = 1.0 / opt.sample_hz;
  SimResult res;
  const std::uint32_t net_id = 7;
  for (double t = 0; t < opt.duration_s; t += dt) {
    ++res.sent;
    const bool in_burst = net.burst_loss_at_s >= 0 && t >= net.burst_loss_at_s && t < net.burst_loss_at_s + net.burst_len_s;
    const double r_loss = rng.next(), r_jit = rng.next();
    if (in_burst || r_loss < net.loss) {
      ++res.lost;
      continue;
    }
    const TrackPoint p = track(t);
    const std::int64_t t_us = static_cast<std::int64_t>(opt.base_us) + static_cast<std::int64_t>(std::llround(t * 1e6));
    const std::uint64_t tick_us = static_cast<std::uint64_t>(t_us) + 10'000;  // the server tick that carries it
    wire::ReplicationEntry e;
    e.net_id = net_id;
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
    e.state_flags = p.flags;
    e.time_ms = *wire::quantize_time_offset_ms(t_us, static_cast<std::int64_t>(tick_us));
    Msg m;
    m.len = *wire::write_replication_entry(wire::MutableByteSpan(m.bytes.data(), m.bytes.size()), e);
    m.server_time_us = tick_us;
    m.arrival_us = static_cast<std::int64_t>(tick_us) + static_cast<std::int64_t>((net.base_ms + r_jit * net.jitter_ms) * 1000.0);
    msgs.push_back(m);
  }
  std::stable_sort(msgs.begin(), msgs.end(), [](const Msg& a, const Msg& b) { return a.arrival_us < b.arrival_us; });

  StreamSet set(opt.cfg);
  set.ensure(net_id);
  std::size_t next_msg = 0;
  const std::int64_t frame_us = static_cast<std::int64_t>(1e6 / opt.frame_hz);
  const std::int64_t end_us = static_cast<std::int64_t>(opt.base_us) + static_cast<std::int64_t>(opt.duration_s * 1e6);
  const std::int64_t warm_us = static_cast<std::int64_t>(opt.base_us) + static_cast<std::int64_t>(opt.warmup_s * 1e6);
  bool warmed = false;
  res.delay_min_us = INT64_MAX;
  Interpolator* in = set.find(net_id);
  std::vector<double> e_all, e_steady, e_fast;
  std::int64_t next_report = warm_us;
  auto fold = [&](const SyncReport& r) {
    SyncReport& a = res.report;
    a.n_all += r.n_all;
    a.n_steady += r.n_steady;
    a.n_fast += r.n_fast;
    a.n_lat += r.n_lat;
    a.err_p50 = std::max(a.err_p50, r.err_p50);
    a.err_p95 = std::max(a.err_p95, r.err_p95);
    a.err_max = std::max(a.err_max, r.err_max);
    a.err_steady_p95 = std::max(a.err_steady_p95, r.err_steady_p95);
    a.err_steady_max = std::max(a.err_steady_max, r.err_steady_max);
    a.err_fast_p95 = std::max(a.err_fast_p95, r.err_fast_p95);
    a.err_fast_max = std::max(a.err_fast_max, r.err_fast_max);
    a.lat_p50_ms = std::max(a.lat_p50_ms, r.lat_p50_ms);
    a.lat_p95_ms = std::max(a.lat_p95_ms, r.lat_p95_ms);
    a.lat_max_ms = std::max(a.lat_max_ms, r.lat_max_ms);
    a.speed_max = std::max(a.speed_max, r.speed_max);
    a.frames += r.frames;
    a.snaps += r.snaps;
    a.extrapolated_frames += r.extrapolated_frames;
    a.held_frames += r.held_frames;
  };
  for (std::int64_t now = static_cast<std::int64_t>(opt.base_us); now < end_us; now += frame_us) {
    while (next_msg < msgs.size() && msgs[next_msg].arrival_us <= now) {
      const Msg& m = msgs[next_msg++];
      (void)set.ingest(m.server_time_us, wire::ByteSpan(m.bytes.data(), m.len), 1, m.arrival_us);
    }
    const RenderPose pose = in->render(now);
    if (now >= warm_us) {
      if (!warmed) {
        (void)in->stats().report(true);
        warmed = true;
      }
      if (now >= next_report + 5'000'000) {  // a [sync] window every 5 s, as in the mod
        fold(in->stats().report(true));
        next_report += 5'000'000;
      }
      if (pose.snapped) ++res.jumps_snapped;
      if (pose.hidden) ++res.hidden_frames;
      res.delay_min_us = std::min(res.delay_min_us, in->delay_us());
      res.delay_max_us = std::max(res.delay_max_us, in->delay_us());
      if (!pose.hidden && !pose.snapped) {
        const TrackPoint tp = track(static_cast<double>(pose.represented_t_us - static_cast<std::int64_t>(opt.base_us)) / 1e6);
        if (tp.sector == pose.sector) {
          const double err = distance(pose.pos, tp.pos);
          e_all.push_back(err);
          (length(tp.vel) < SyncStats::kSteadyMaxMps ? e_steady : e_fast).push_back(err);
        }
      }
    }
  }
  res.delay_end_us = in->delay_us();
  fold(in->stats().report(true));
  res.n_true = e_all.size();
  res.true_p50 = pct(e_all, 0.5);
  res.true_p95 = pct(e_all, 0.95);
  res.true_max = pct(e_all, 1.0);
  res.true_steady_p95 = pct(e_steady, 0.95);
  res.true_steady_max = pct(e_steady, 1.0);
  res.true_fast_p95 = pct(e_fast, 0.95);
  res.true_fast_max = pct(e_fast, 1.0);
  return res;
}

}  // namespace x4mp::test
