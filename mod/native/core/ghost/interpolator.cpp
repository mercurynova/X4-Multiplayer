#include "core/ghost/interpolator.h"

#include <algorithm>

namespace x4mp::ghost {

// ---------------------------------------------------------------------------------------------
// DelayController
// ---------------------------------------------------------------------------------------------

void DelayController::on_sample(std::int64_t arrival_us, std::int64_t sample_t_us, std::int64_t sample_dt_us) noexcept {
  d_[head_] = arrival_us - sample_t_us;
  head_ = (head_ + 1) % kWin;
  if (n_ < kWin) ++n_;
  if (sample_dt_us > 0) {
    const double dt = static_cast<double>(std::clamp<std::int64_t>(sample_dt_us, 10'000, 1'000'000));
    interval_us_ += (dt - interval_us_) * 0.1;
  }
  if (!cfg_.adaptive_delay || n_ < 16) {
    target_us_ = cfg_.default_delay_us;
    return;
  }
  std::array<std::int64_t, kWin> tmp;
  std::copy_n(d_.begin(), n_, tmp.begin());
  const std::int64_t lo = *std::min_element(tmp.begin(), tmp.begin() + static_cast<std::ptrdiff_t>(n_));
  for (std::size_t i = 0; i < n_; ++i) tmp[i] -= lo;
  const std::size_t rank = static_cast<std::size_t>(0.95 * static_cast<double>(n_ - 1) + 0.5);
  std::nth_element(tmp.begin(), tmp.begin() + static_cast<std::ptrdiff_t>(rank), tmp.begin() + static_cast<std::ptrdiff_t>(n_));
  jitter_p95_us_ = static_cast<double>(tmp[rank]);
  const double want = 1.5 * interval_us_ + 2.0 * jitter_p95_us_;
  target_us_ = std::clamp<std::int64_t>(static_cast<std::int64_t>(want), cfg_.min_delay_us, cfg_.max_delay_us);
}

void DelayController::step(std::int64_t dt_us) noexcept {
  if (dt_us <= 0) return;
  if (current_us_ < target_us_) {
    current_us_ = std::min(target_us_, current_us_ + dt_us / 10);  // up to 100 ms per second
  } else if (current_us_ > target_us_) {
    current_us_ = std::max(target_us_, current_us_ - dt_us / 100);  // down to 10 ms per second
  }
  current_us_ = std::clamp(current_us_, cfg_.min_delay_us, cfg_.max_delay_us);
}

// ---------------------------------------------------------------------------------------------
// Interpolator
// ---------------------------------------------------------------------------------------------

bool Interpolator::discontinuity(const Sample& a, const Sample& b) noexcept {
  return (b.flags & kTeleport) || a.sector != b.sector || ((a.flags | b.flags) & kHidden);
}

void Interpolator::push(const Sample& s, std::int64_t arrival_us) noexcept {
  if (arrival_us != 0) {
    const std::int64_t dt = (have_push_ && s.t_us > last_push_t_us_) ? s.t_us - last_push_t_us_ : 0;
    delay_.on_sample(arrival_us, s.t_us, dt);
  }
  // Sorted insert; equal time replaces; older than a full ring is dropped.
  std::size_t i = n_;
  while (i > 0 && ring_[i - 1].s.t_us > s.t_us) --i;
  if (i > 0 && ring_[i - 1].s.t_us == s.t_us) {
    ring_[i - 1] = {s, euler_to_quat(s.rot)};
  } else {
    if (n_ == kRing) {
      if (i == 0) return;
      for (std::size_t k = 1; k < n_; ++k) ring_[k - 1] = ring_[k];
      --i;
      --n_;
    }
    for (std::size_t k = n_; k > i; --k) ring_[k] = ring_[k - 1];
    ring_[i] = {s, euler_to_quat(s.rot)};
    ++n_;
  }
  if (!have_push_ || s.t_us > last_push_t_us_) last_push_t_us_ = s.t_us;
  have_push_ = true;
  stats_.on_source_sample(s.t_us, s.sector, s.flags, s.pos);
}

void Interpolator::apply_state(std::int64_t t_us, std::uint16_t flags, std::uint8_t hull, std::uint8_t shield) noexcept {
  for (std::size_t k = n_; k > 0; --k) {
    Sample& s = ring_[k - 1].s;
    if (s.t_us <= t_us) {
      s.flags = flags;
      s.hull = hull;
      s.shield = shield;
      return;
    }
  }
  if (n_ > 0) {  // the state change predates everything we hold: apply to the oldest
    Sample& s = ring_[0].s;
    s.flags = flags;
    s.hull = hull;
    s.shield = shield;
  }
}

Interpolator::Raw Interpolator::evaluate(std::int64_t t) const noexcept {
  Raw r;
  if (n_ == 0) return r;

  auto fill_static = [&](const Node& nd, PoseState st) {
    r.state = st;
    r.sector = nd.s.sector;
    r.flags = nd.s.flags;
    r.hull = nd.s.hull;
    r.shield = nd.s.shield;
    r.pos = nd.s.pos;
    r.rot = nd.s.rot;
    r.vel = {};
    r.speed = length(nd.s.vel);
    r.governing_t_us = nd.s.t_us;
    r.governing_flags = nd.s.flags;
    r.represented_t_us = nd.s.t_us;
  };

  if (t < ring_[0].s.t_us) {
    fill_static(ring_[0], PoseState::Early);
    return r;
  }
  std::size_t i = n_ - 1;
  while (i > 0 && ring_[i].s.t_us > t) --i;  // last sample with t_us <= t
  const Node& a = ring_[i];

  // The pose of sample `i` carried on past its own time: linear extrapolation for at most extrap_cap, then held, stale after stale_us.
  auto extrapolate = [&](std::size_t idx) {
    const Node& n0 = ring_[idx];
    const std::int64_t age = t - n0.s.t_us;
    fill_static(n0, PoseState::Interpolating);
    r.represented_t_us = t;
    if (age <= 0) return;
    const std::int64_t capped = std::min(age, cfg_.extrap_cap_us);
    const double cap_s = static_cast<double>(capped) / 1e6;
    Vec3 vel = n0.s.vel;
    const bool have_prev_seg = idx >= 1 && !discontinuity(ring_[idx - 1].s, n0.s) && n0.s.t_us > ring_[idx - 1].s.t_us;
    const double prev_dt_s = have_prev_seg ? static_cast<double>(n0.s.t_us - ring_[idx - 1].s.t_us) / 1e6 : 0.0;
    // Velocity unknown (the sender gave none on two samples in a row): fall back to the finite difference. A sender that reports
    // velocity and says it has stopped is believed.
    if (length(vel) < 1e-9 && have_prev_seg && length(ring_[idx - 1].s.vel) < 1e-9) vel = (n0.s.pos - ring_[idx - 1].s.pos) * (1.0 / prev_dt_s);
    r.pos = n0.s.pos + vel * cap_s;
    r.speed = length(vel);
    if (have_prev_seg) r.rot = quat_to_euler(slerp(ring_[idx - 1].q, n0.q, 1.0 + cap_s / prev_dt_s));
    if (age > cfg_.stale_us) {
      r.state = PoseState::Stale;
      r.vel = {};
    } else if (age > cfg_.extrap_cap_us) {
      r.state = PoseState::Held;
      r.vel = {};
    } else {
      r.state = PoseState::Extrapolating;
      r.vel = vel;
    }
    r.represented_t_us = n0.s.t_us + capped;
  };

  if (i + 1 < n_) {
    const Node& b = ring_[i + 1];
    if (discontinuity(a.s, b.s)) {
      // M3-31: hold the earlier sample's pose until the new one is reached, then snap. The hold is the SAME pose the render showed before this
      // sample arrived (the capped extrapolation, or hidden once stale): going back to the raw earlier sample made a silent gap (a superhighway)
      // end with a jump back to the old pose and sector before the move to the new one.
      extrapolate(i);
      if (r.state == PoseState::Extrapolating) r.state = PoseState::Interpolating;
      r.vel = {};
      return r;
    }
    const double seg_s = static_cast<double>(b.s.t_us - a.s.t_us) / 1e6;
    const double u = static_cast<double>(t - a.s.t_us) / static_cast<double>(b.s.t_us - a.s.t_us);
    fill_static(a, PoseState::Interpolating);
    const bool no_vel = length(a.s.vel) < 1e-6 && length(b.s.vel) < 1e-6;
    r.pos = no_vel ? a.s.pos + (b.s.pos - a.s.pos) * u : hermite(a.s.pos, a.s.vel, b.s.pos, b.s.vel, seg_s, u);
    r.rot = quat_to_euler(slerp(a.q, b.q, u));
    r.vel = a.s.vel + (b.s.vel - a.s.vel) * u;
    r.speed = length(r.vel);
    if (no_vel) {
      r.vel = (b.s.pos - a.s.pos) * (1.0 / seg_s);
      r.speed = length(r.vel);
    }
    r.flags = a.s.flags;
    r.represented_t_us = t;
    return r;
  }

  // Past (or exactly at) the newest sample.
  extrapolate(n_ - 1);
  return r;
}

RenderPose Interpolator::render(std::int64_t now_us) noexcept {
  RenderPose out;
  std::int64_t dt_real = 0;
  if (last_now_us_ != 0 && now_us > last_now_us_) dt_real = std::min<std::int64_t>(now_us - last_now_us_, 1'000'000);
  last_now_us_ = now_us;
  delay_.step(dt_real);

  const std::int64_t t = now_us - delay_.current_us();
  out.render_t_us = t;
  const Raw r = evaluate(t);
  if (r.state == PoseState::Empty) return out;

  const bool hidden = (r.flags & kHidden) != 0 || r.state == PoseState::Stale;
  bool snap = false;
  if (!have_prev_) {
    snap = true;
  } else {
    if (r.sector != prev_.sector) snap = true;
    if (r.governing_t_us != prev_.governing_t_us && (r.governing_flags & kTeleport)) snap = true;
    if (hidden != prev_hidden_) snap = true;
    if (!snap) {
      double dt_t = static_cast<double>(t - prev_render_t_us_) / 1e6;
      if (dt_t < 0) dt_t = 0;
      const Vec3 err = r.pos - (prev_.pos + prev_.vel * dt_t);
      const double e = length(err);
      const double limit = std::max(cfg_.snap_min_m, std::max(r.speed, prev_.speed) * cfg_.snap_speed_s);
      if (e > limit) snap = true;
      else if (e > cfg_.blend_deadband_m) offset_ = offset_ - err;
    }
  }
  if (snap) {
    offset_ = {};
  } else if (dt_real > 0) {
    const double k = 1.0 - static_cast<double>(dt_real) / static_cast<double>(cfg_.blend_us);
    offset_ = offset_ * (k > 0 ? k : 0.0);
  }

  out.state = r.state;
  out.sector = r.sector;
  out.pos = r.pos + offset_;
  out.rot = r.rot;
  out.flags = r.flags;
  out.hull = r.hull;
  out.shield = r.shield;
  out.snapped = snap;
  out.hidden = hidden;
  out.speed_mps = r.speed;
  out.represented_t_us = r.represented_t_us;

  if (!hidden) {
    stats_.on_render(now_us, t, r.represented_t_us, r.sector, out.pos, r.speed);
    stats_.count_frame(snap && have_prev_, r.state == PoseState::Extrapolating, r.state == PoseState::Held);
  }
  prev_ = r;
  prev_render_t_us_ = t;
  prev_hidden_ = hidden;
  have_prev_ = true;
  return out;
}

}  // namespace x4mp::ghost
