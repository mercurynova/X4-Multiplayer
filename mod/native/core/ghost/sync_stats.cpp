#include "core/ghost/sync_stats.h"

#include <cstdio>

#include "core/ghost/sample.h"

namespace x4mp::ghost {

void SyncStats::on_source_sample(std::int64_t t_us, std::uint16_t sector, std::uint16_t flags, Vec3 pos) noexcept {
  // Sorted insert (samples can arrive out of order); equal time replaces.
  std::size_t i = hist_n_;
  while (i > 0 && hist_[i - 1].t_us > t_us) --i;
  if (i > 0 && hist_[i - 1].t_us == t_us) {
    hist_[i - 1] = {t_us, sector, flags, pos};
  } else {
    if (hist_n_ == kHistory) {
      if (i == 0) return;  // older than everything we still hold
      for (std::size_t k = 1; k < hist_n_; ++k) hist_[k - 1] = hist_[k];
      --i;
      --hist_n_;
    }
    for (std::size_t k = hist_n_; k > i; --k) hist_[k] = hist_[k - 1];
    hist_[i] = {t_us, sector, flags, pos};
    ++hist_n_;
  }
  resolve();
}

void SyncStats::on_render(std::int64_t now_us, std::int64_t render_t_us, std::int64_t represented_t_us, std::uint16_t sector,
                          Vec3 displayed_pos, double speed_mps) noexcept {
  lat_.push(static_cast<double>(now_us - represented_t_us) / 1000.0);
  speed_max_ = std::max(speed_max_, speed_mps);
  // Drop pending poses that are too old to ever be scored.
  while (pend_n_ > 0 && render_t_us - pend_[pend_head_].t_us > kPendingMaxAgeUs) {
    pend_head_ = (pend_head_ + 1) % kPending;
    --pend_n_;
  }
  if (pend_n_ == kPending) {
    pend_head_ = (pend_head_ + 1) % kPending;
    --pend_n_;
  }
  pend_[(pend_head_ + pend_n_) % kPending] = {render_t_us, sector, displayed_pos, speed_mps};
  ++pend_n_;
  resolve();
}

void SyncStats::resolve() noexcept {
  while (pend_n_ > 0 && hist_n_ > 0) {
    const Pending& p = pend_[pend_head_];
    if (p.t_us > hist_[hist_n_ - 1].t_us) break;  // the truth for this pose has not arrived yet
    bool score = false;
    double err = 0;
    if (p.t_us >= hist_[0].t_us) {
      std::size_t i = hist_n_ - 1;
      while (i > 0 && hist_[i - 1].t_us >= p.t_us) --i;  // i = first sample with t >= p.t
      if (hist_[i].t_us == p.t_us) {
        if (hist_[i].sector == p.sector && !(hist_[i].flags & kHidden)) {
          score = true;
          err = distance(p.pos, hist_[i].pos);
        }
      } else if (i > 0) {
        const Src& a = hist_[i - 1];
        const Src& b = hist_[i];
        const bool broken = (b.flags & kTeleport) || a.sector != b.sector || p.sector != a.sector || ((a.flags | b.flags) & kHidden);
        if (!broken) {
          const double u = static_cast<double>(p.t_us - a.t_us) / static_cast<double>(b.t_us - a.t_us);
          err = distance(p.pos, a.pos + (b.pos - a.pos) * u);
          score = true;
        }
      }
    }
    if (score) {
      err_all_.push(err);
      if (p.speed < kSteadyMaxMps) err_steady_.push(err);
      else err_fast_.push(err);
    }
    pend_head_ = (pend_head_ + 1) % kPending;
    --pend_n_;
  }
}

void SyncStats::count_frame(bool snapped, bool extrapolating, bool held) noexcept {
  ++frames_;
  if (snapped) ++snaps_;
  if (extrapolating) ++extrap_;
  if (held) ++held_;
}

SyncReport SyncStats::report(bool reset) noexcept {
  SyncReport r;
  r.n_all = err_all_.size();
  r.n_steady = err_steady_.size();
  r.n_fast = err_fast_.size();
  r.n_lat = lat_.size();
  r.err_p50 = err_all_.percentile(0.50);
  r.err_p95 = err_all_.percentile(0.95);
  r.err_max = err_all_.max();
  r.err_steady_p95 = err_steady_.percentile(0.95);
  r.err_steady_max = err_steady_.max();
  r.err_fast_p95 = err_fast_.percentile(0.95);
  r.err_fast_max = err_fast_.max();
  r.lat_p50_ms = lat_.percentile(0.50);
  r.lat_p95_ms = lat_.percentile(0.95);
  r.lat_max_ms = lat_.max();
  r.speed_max = speed_max_;
  r.frames = frames_;
  r.snaps = snaps_;
  r.extrapolated_frames = extrap_;
  r.held_frames = held_;
  if (reset) {
    err_all_.clear();
    err_steady_.clear();
    err_fast_.clear();
    lat_.clear();
    speed_max_ = 0;
    frames_ = snaps_ = extrap_ = held_ = 0;
  }
  return r;
}

std::string format_sync_line(std::uint32_t net_id, const SyncReport& r) {
  char buf[320];
  std::snprintf(buf, sizeof buf,
                "[sync] net=%u frames=%llu err_p50/p95/max=%.2f/%.2f/%.2f m steady_p95=%.2f fast_p95=%.2f lat_p95=%.0f ms "
                "speed_max=%.0f snaps=%llu extrap=%llu held=%llu",
                net_id, static_cast<unsigned long long>(r.frames), r.err_p50, r.err_p95, r.err_max, r.err_steady_p95,
                r.err_fast_p95, r.lat_p95_ms, r.speed_max, static_cast<unsigned long long>(r.snaps),
                static_cast<unsigned long long>(r.extrapolated_frames), static_cast<unsigned long long>(r.held_frames));
  return buf;
}

}  // namespace x4mp::ghost
