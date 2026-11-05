#include "features/onfoot/onfoot_tracker.h"

#include <cmath>

namespace x4mp::features::onfoot {

OnFootTracker::Step OnFootTracker::update(const Sample& s, std::int64_t now_us) noexcept {
  Step step;

  // A room / container / mode change is sent at once, and the remote side must snap.
  const bool changed = !have_last_ || !(s.ctx == last_ctx_);
  const bool room_changed = changed && have_last_ && last_ctx_.valid && s.ctx.valid &&
                            (s.ctx.room_macro_ref != last_ctx_.room_macro_ref || s.ctx.anchor_x != last_ctx_.anchor_x ||
                             s.ctx.anchor_y != last_ctx_.anchor_y || s.ctx.anchor_z != last_ctx_.anchor_z);

  // Everything below is measured from the LAST SEND, not from the previous frame: "moved more than 5 cm since the last sample"
  // (docs/research/on-foot-presence.md 3.1) is what decides the rate, and a per-frame delta would make a normal walk (2.3 cm per frame
  // at 60 fps) look idle. The same distance over the time since the last send is the speed the animation hint uses.
  float dx = 0;
  float dy = 0;
  float dz = 0;
  if (have_send_) {
    dx = s.cx - last_x_;
    dy = s.cy - last_y_;
    dz = s.cz - last_z_;
  }
  const float moved = std::sqrt(dx * dx + dy * dy + dz * dz);

  float dyaw = 0;
  if (have_send_) {
    dyaw = std::fmod(std::fabs(s.yaw - last_yaw_), 6.28318530717958647692f);
    if (dyaw > 3.14159265358979323846f) dyaw = 6.28318530717958647692f - dyaw;
  }
  const bool moving = moved >= cfg_.moving_metres || dyaw >= cfg_.moving_yaw_radians;

  // Per-CALL delta. `moving` above is measured from the last send and stays true right after one, so a stop has to be read from
  // two consecutive calls: the first call that brings no movement at all after a moving one is sent at once.
  float step_moved = 0;
  if (have_step_) {
    const float sx = s.cx - step_x_;
    const float sy = s.cy - step_y_;
    const float sz = s.cz - step_z_;
    step_moved = std::sqrt(sx * sx + sy * sy + sz * sz);
  }
  const bool still = step_moved < cfg_.still_metres;

  if (have_send_ && now_us > last_send_us_ && s.ctx.container == last_ctx_.container) {
    const double dt = static_cast<double>(now_us - last_send_us_) / 1'000'000.0;
    if (dt > 0.001) speed_mps_ = static_cast<float>(static_cast<double>(moved) / dt);
  } else {
    speed_mps_ = 0;
  }

  const std::int64_t interval = !s.ctx.valid || s.ctx.mode == Mode::None
                                    ? cfg_.hidden_interval_us
                                    : (moving ? cfg_.moving_interval_us : cfg_.idle_interval_us);
  const bool due = now_us - last_send_us_ >= interval;

  step.anim = s.ctx.mode == Mode::Seated ? Anim::Sit
              : s.ctx.mode == Mode::Conversation ? Anim::Talk
              : speed_mps_ > 4.5f ? Anim::Run
              : speed_mps_ > 0.15f ? Anim::Walk
                                   : Anim::Idle;

  // The leave state is the exception: mode None has its own 5 s repeat, so a stop must not turn it into a 1 Hz one.
  const bool stopped = was_moving_ && still && s.ctx.valid && s.ctx.mode != Mode::None;
  if (force_ || changed || due || stopped) {
    step.send = true;
    step.keyframe = force_ || changed;
    step.teleport = room_changed || (changed && s.ctx.valid);
    last_send_us_ = now_us;
    last_x_ = s.cx;
    last_y_ = s.cy;
    last_z_ = s.cz;
    last_yaw_ = s.yaw;
    have_send_ = true;
    force_ = false;
  }

  last_ctx_ = s.ctx;
  have_last_ = true;
  step_x_ = s.cx;
  step_y_ = s.cy;
  step_z_ = s.cz;
  have_step_ = true;
  was_moving_ = !still;
  return step;
}

}  // namespace x4mp::features::onfoot
