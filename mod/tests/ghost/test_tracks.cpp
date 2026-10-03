// Path-error and display-latency over analytic tracks through the REAL Replication wire codec, a jittery link and 5-10 % loss.
// Bounds are the user's Q3 metric (docs/m3-plan.md): path error at the same server time p95 < 50 m below 500 m/s and < 10 m in
// steady flight below 300 m/s; display latency p95 <= 200 ms on a LAN. Numbers are printed so they can be quoted.
#include <cstdio>

#include <catch2/catch_test_macros.hpp>

#include "sim.h"

using namespace x4mp::test;

namespace {
void print(const char* name, const NetModel& net, const SimResult& r) {
  std::printf("[ghost-track] %-20s loss=%2.0f%% jit=%3.0fms | TRUE err p50/p95/max=%.2f/%.2f/%.2f m (steady p95=%.2f, fast p95=%.2f, n=%zu) | "
              "[sync] err p95=%.2f steady=%.2f fast=%.2f | lat p95=%.0f ms | vmax=%.0f snaps=%llu extrap=%llu held=%llu | delay %lld..%lld ms\n",
              name, net.loss * 100, net.jitter_ms, r.true_p50, r.true_p95, r.true_max, r.true_steady_p95, r.true_fast_p95, r.n_true,
              r.report.err_p95, r.report.err_steady_p95, r.report.err_fast_p95, r.report.lat_p95_ms, r.report.speed_max,
              static_cast<unsigned long long>(r.jumps_snapped), static_cast<unsigned long long>(r.report.extrapolated_frames),
              static_cast<unsigned long long>(r.report.held_frames), static_cast<long long>(r.delay_min_us / 1000),
              static_cast<long long>(r.delay_max_us / 1000));
  std::fflush(stdout);
}
}  // namespace

TEST_CASE("clean link: error is quantisation-sized", "[ghost][tracks]") {
  NetModel net;
  net.loss = 0;
  net.jitter_ms = 0;
  const SimResult r = run_track(line_track(250), net);
  print("line250 clean", net, r);
  CHECK(r.n_true > 2000);
  CHECK(r.true_max < 0.5);
  CHECK(r.report.lat_p95_ms <= 200);
  CHECK(r.jumps_snapped == 0);
}

TEST_CASE("line 250 m/s with jitter and loss", "[ghost][tracks]") {
  for (double loss : {0.05, 0.10}) {
    NetModel net;
    net.loss = loss;
    net.jitter_ms = 25;
    net.seed = 11;
    const SimResult r = run_track(line_track(250), net);
    print("line250", net, r);
    CHECK(r.n_true > 2000);
    CHECK(r.true_steady_p95 < 10.0);
    CHECK(r.report.err_steady_p95 < 10.0);
    CHECK(r.true_p95 < 50.0);
    CHECK(r.report.err_p95 < 50.0);
    CHECK(r.report.lat_p95_ms <= 200.0);
    CHECK(r.jumps_snapped == 0);
  }
}

TEST_CASE("circle (turning flight) with jitter and loss", "[ghost][tracks]") {
  struct Case { const char* name; double radius, speed; };
  for (const Case c : {Case{"circle r1500 v250", 1500, 250}, Case{"circle r500 v100", 500, 100}, Case{"circle r3000 v450", 3000, 450}}) {
    for (double loss : {0.05, 0.10}) {
      NetModel net;
      net.loss = loss;
      net.jitter_ms = 25;
      net.seed = 23;
      const SimResult r = run_track(circle_track(c.radius, c.speed), net);
      print(c.name, net, r);
      CHECK(r.n_true > 2000);
      if (c.speed < 300) CHECK(r.true_steady_p95 < 10.0);
    CHECK(r.report.err_steady_p95 < 10.0);
      CHECK(r.true_p95 < 50.0);
    CHECK(r.report.err_p95 < 50.0);
      CHECK(r.report.lat_p95_ms <= 200.0);
      CHECK(r.jumps_snapped == 0);
    }
  }
}

TEST_CASE("worst case: instant 90 degree heading changes every 3 s", "[ghost][tracks]") {
  for (double loss : {0.05, 0.10}) {
    NetModel net;
    net.loss = loss;
    net.jitter_ms = 25;
    net.seed = 61;
    const SimResult r = run_track(jink_track(200, 3.0), net);
    print("jink 200 m/s", net, r);
    CHECK(r.n_true > 2000);
    CHECK(r.true_steady_p95 < 10.0);
    CHECK(r.true_p95 < 50.0);
    CHECK(r.report.lat_p95_ms <= 200.0);
    CHECK(r.jumps_snapped == 0);
  }
}

TEST_CASE("acceleration 0 -> 450 m/s with jitter and loss", "[ghost][tracks]") {
  for (double loss : {0.05, 0.10}) {
    NetModel net;
    net.loss = loss;
    net.jitter_ms = 25;
    net.seed = 31;
    SimOptions opt;
    opt.duration_s = 45;
    opt.warmup_s = 0.5;  // keep the acceleration phase in the statistics
    const SimResult r = run_track(accel_track(20, 450), net, opt);
    print("accel 0-450", net, r);
    CHECK(r.n_true > 1500);
    CHECK(r.true_steady_p95 < 10.0);
    CHECK(r.report.err_steady_p95 < 10.0);
    CHECK(r.true_p95 < 50.0);
    CHECK(r.report.err_p95 < 50.0);
    CHECK(r.report.lat_p95_ms <= 200.0);
    CHECK(r.jumps_snapped == 0);
  }
}

TEST_CASE("gate jumps: one snap per jump, bounded error elsewhere", "[ghost][tracks]") {
  for (double loss : {0.05, 0.10}) {
    NetModel net;
    net.loss = loss;
    net.jitter_ms = 25;
    net.seed = 47;
    SimOptions opt;
    opt.duration_s = 61;  // jumps at 12, 24, 36, 48, 60 s; the warm-up ends at 3 s
    const SimResult r = run_track(gate_track(250, 12.0), net, opt);
    print("gate jumps", net, r);
    CHECK(r.jumps_snapped == 5);
    CHECK(r.true_steady_p95 < 10.0);
    CHECK(r.report.err_steady_p95 < 10.0);
    CHECK(r.true_p95 < 50.0);
    CHECK(r.report.err_p95 < 50.0);
    CHECK(r.report.lat_p95_ms <= 200.0);
  }
}

TEST_CASE("a 400 ms loss burst is bridged by extrapolation without a snap", "[ghost][tracks]") {
  NetModel net;
  net.loss = 0.05;
  net.jitter_ms = 20;
  net.burst_loss_at_s = 20.0;
  net.burst_len_s = 0.4;
  net.seed = 5;
  const SimResult r = run_track(circle_track(1500, 250), net);
  print("circle burst 400ms", net, r);
  CHECK(r.true_p95 < 10.0);
  CHECK(r.true_max < 50.0);
  CHECK(r.jumps_snapped == 0);
  CHECK(r.report.extrapolated_frames > 0);
}

TEST_CASE("heavy jitter (TCP-like, up to 120 ms) raises the delay, error stays bounded", "[ghost][tracks]") {
  NetModel net;
  net.loss = 0.05;
  net.jitter_ms = 120;
  net.seed = 9;
  const SimResult r = run_track(circle_track(1500, 250), net);
  print("circle jitter120", net, r);
  CHECK(r.delay_max_us > 130'000);
  CHECK(r.delay_max_us <= 250'000);
  CHECK(r.true_steady_p95 < 10.0);
    CHECK(r.report.err_steady_p95 < 10.0);
  CHECK(r.report.lat_p95_ms <= 300.0);  // jitter this large legitimately costs latency
}

TEST_CASE("SyncStats really measures: a ghost rendered 30 m off scores ~30 m", "[ghost][stats]") {
  SyncStats st;
  for (int i = 0; i < 40; ++i) st.on_source_sample(1'000'000 + i * 50'000, 1, 0, {i * 12.5, 0, 0});
  for (int f = 0; f < 100; ++f) {
    const std::int64_t t = 1'200'000 + f * 16'667;
    const double x = (static_cast<double>(t) - 1'000'000.0) / 50'000.0 * 12.5;
    st.on_render(t + 100'000, t, t, 1, {x + 30.0, 0, 0}, 250.0);
  }
  const SyncReport r = st.report();
  CHECK(r.n_all >= 90);
  CHECK(r.err_p95 > 29.9);
  CHECK(r.err_p95 < 30.1);
  CHECK(r.lat_p95_ms > 99.0);
  CHECK(r.lat_p95_ms < 101.0);
  CHECK(r.n_steady == r.n_all);
}
