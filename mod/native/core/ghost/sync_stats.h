#pragma once
// core/ghost: path-error and display-latency statistics (M3-02; docs/m3-plan.md 4.6 and Q3). Fixed-size storage only: nothing here
// allocates after construction, so it can run on the frame thread.
//
//   Path error     = |rendered position - sender's true position at the SAME server time|. The truth is the sender's own samples,
//                    linear between neighbours; pairs whose neighbours straddle a teleport / sector change / hidden sample are not
//                    scored (there is no defined truth in between). A rendered pose is held for up to 2 s and scored as soon as
//                    samples covering its render time have arrived.
//   Display latency = server_now - (server time the rendered pose represents).
//
// Buckets (Q3): every fresh error goes to `err_all`; speed < 300 m/s also to `err_steady`; speed >= 300 m/s to `err_fast`.
// M4 criterion 3: a fresh error whose rendered pose was within 5 km of the observer also goes to `err_near`.
// A pose whose represented time is more than kFreshMaxUs behind its render time, or whose newest source sample is older than
// kFreshMaxUs, is a data gap (the ghost was held or extrapolated across a transport gap), not a path error: it goes to
// `err_stale` instead, so a 31 s transport gap cannot masquerade as 2 km of interpolation error.

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <string>

#include "core/ghost/math.h"

namespace x4mp::ghost {

// Fixed ring of the last N values with percentile queries. percentile() copies the window onto the stack (N doubles).
template <std::size_t N>
class PercentileWindow {
 public:
  void push(double v) noexcept {
    buf_[head_] = v;
    head_ = (head_ + 1) % N;
    if (count_ < N) ++count_;
    ++total_;
  }
  [[nodiscard]] std::size_t size() const noexcept { return count_; }
  [[nodiscard]] std::uint64_t total() const noexcept { return total_; }
  void clear() noexcept {
    head_ = 0;
    count_ = 0;
  }
  // p in [0,1]; 0 when empty. Nearest-rank on the sorted window.
  [[nodiscard]] double percentile(double p) const noexcept {
    if (count_ == 0) return 0;
    std::array<double, N> tmp;
    std::copy_n(buf_.begin(), count_, tmp.begin());  // the first count_ slots always hold the live values
    const std::size_t rank = static_cast<std::size_t>(p * static_cast<double>(count_ - 1) + 0.5);
    std::nth_element(tmp.begin(), tmp.begin() + static_cast<std::ptrdiff_t>(rank),
                     tmp.begin() + static_cast<std::ptrdiff_t>(count_));
    return tmp[rank];
  }
  [[nodiscard]] double max() const noexcept {
    double m = 0;
    for (std::size_t i = 0; i < count_; ++i) m = std::max(m, buf_[i]);
    return m;
  }

 private:
  std::array<double, N> buf_{};
  std::size_t head_ = 0, count_ = 0;
  std::uint64_t total_ = 0;
};

struct SyncReport {
  std::size_t n_all = 0, n_steady = 0, n_fast = 0, n_lat = 0;
  double err_p50 = 0, err_p95 = 0, err_max = 0;            // all speeds
  double err_steady_p95 = 0, err_steady_max = 0;           // speed < 300 m/s (Q3: < 10 m)
  double err_fast_p95 = 0, err_fast_max = 0;               // speed >= 300 m/s (Q3: < 50 m below 500 m/s)
  double err_extrap_p95 = 0, err_extrap_max = 0;           // only poses rendered past the newest sample (extrapolated)
  std::size_t n_near = 0;                                   // fresh errors scored while the ghost was <= 5 km from the observer
  double err_near_p50 = 0, err_near_p95 = 0, err_near_max = 0;  // M4 criterion 3: < 25 m p95 inside the attention band
  std::size_t n_stale = 0;                                  // held poses (represented time too far behind render time)
  double err_stale_p50 = 0, err_stale_p95 = 0, err_stale_max = 0;  // data-gap error, reported separately from path error
  double lat_p50_ms = 0, lat_p95_ms = 0, lat_max_ms = 0;   // Q3: p95 <= 200 ms on a LAN
  std::size_t n_extrap = 0;
  double speed_max = 0;
  std::uint64_t frames = 0, snaps = 0, extrapolated_frames = 0, held_frames = 0;
};

class SyncStats {
 public:
  static constexpr std::size_t kWindow = 512;       // ~8 s of frames at 60 fps, one 5 s report window
  static constexpr std::size_t kHistory = 64;       // sender samples kept for the "truth"
  static constexpr std::size_t kPending = 256;      // rendered poses waiting for the truth (2 s at 120 fps)
  static constexpr std::int64_t kPendingMaxAgeUs = 2'000'000;
  static constexpr std::int64_t kFreshMaxUs = 1'000'000;  // represented time behind render time: still a path sample, not a gap
  static constexpr double kSteadyMaxMps = 300.0;
  static constexpr double kNearMaxM = 5000.0;  // M4 criterion 3: the attention band around the observer

  // A sender sample, as accepted by the interpolator.
  void on_source_sample(std::int64_t t_us, std::uint16_t sector, std::uint16_t flags, Vec3 pos) noexcept;
  // One rendered pose (displayed position) at server time render_t_us, representing `represented_t_us`.
  // extrapolated: the pose was rendered past the newest sample (the interpolator's Extrapolating state), so its error is the
  // extrapolation error alone rather than interpolation between two known samples.
  // observer_distance_m: distance from the observer's ship to the rendered pose in metres, or <= 0 when unknown (the observer is
  // in another sector or has no ship). Poses inside the 5 km attention band are additionally scored in the `near` bucket.
  // source_fresh: the newest source sample is at most kFreshMaxUs old. False marks a pose extrapolated across a transport
  // gap as stale even when the extrapolation cap made its represented time catch up with the render clock.
  void on_render(std::int64_t now_us, std::int64_t render_t_us, std::int64_t represented_t_us, std::uint16_t sector,
                 Vec3 displayed_pos, double speed_mps, bool extrapolated = false, double observer_distance_m = -1.0,
                 bool source_fresh = true) noexcept;
  void count_frame(bool snapped, bool extrapolating, bool held) noexcept;

  // Snapshot of the current window. reset=true starts a fresh window (call every 5 s for the [sync] line).
  [[nodiscard]] SyncReport report(bool reset = false) noexcept;
  [[nodiscard]] const PercentileWindow<kWindow>& err_all() const noexcept { return err_all_; }
  void clear() noexcept { *this = SyncStats{}; }

 private:
  struct Src {
    std::int64_t t_us = 0;
    std::uint16_t sector = 0, flags = 0;
    Vec3 pos{};
  };
  struct Pending {
    std::int64_t t_us = 0;
    std::uint16_t sector = 0;
    Vec3 pos{};
    double speed = 0;
    bool extrap = false;
    bool fresh = true;       // represented time is close enough to the render time to be a path sample
    double observer_m = -1;  // <= 0: unknown; otherwise the observer distance at render time
  };
  void resolve() noexcept;

  std::array<Src, kHistory> hist_{};
  std::size_t hist_n_ = 0;
  std::array<Pending, kPending> pend_{};
  std::size_t pend_head_ = 0, pend_n_ = 0;

  PercentileWindow<kWindow> err_all_, err_steady_, err_fast_, err_extrap_, err_near_, err_stale_, lat_;
  double speed_max_ = 0;
  std::uint64_t frames_ = 0, snaps_ = 0, extrap_ = 0, held_ = 0;
};

// "[sync] net=<id> frames=.. err_p50/p95/max=.. steady_p95=.. fast_p95=.. ex_p95=.. ex_n=.. lat_p95=..ms speed_max=.." (reporting path, allocates).
[[nodiscard]] std::string format_sync_line(std::uint32_t net_id, const SyncReport& r);

// "[sync] net=<id> near n=.. p50/p95/max=.. m" for the M4 criterion-3 attention band; only meaningful when n_near > 0.
[[nodiscard]] std::string format_near_line(std::uint32_t net_id, const SyncReport& r);

// "[sync] net=<id> stale n=.. p50/p95/max=.. m" for held poses (data gaps), kept out of the path-error buckets.
[[nodiscard]] std::string format_stale_line(std::uint32_t net_id, const SyncReport& r);

}  // namespace x4mp::ghost
