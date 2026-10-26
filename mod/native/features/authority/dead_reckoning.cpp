#include "features/authority/dead_reckoning.h"

#include <algorithm>

namespace x4mp::features::auth {

namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr double kTwoPi = 2.0 * kPi;
}  // namespace

const char* to_string(UpdateReason reason) noexcept {
  switch (reason) {
    case UpdateReason::None: return "none";
    case UpdateReason::Threshold: return "threshold";
    case UpdateReason::Rotation: return "rotation";
    case UpdateReason::Flags: return "flags";
    case UpdateReason::Sector: return "sector";
    case UpdateReason::Keepalive: return "keepalive";
  }
  return "?";
}

double position_epsilon(double distance_m, const DeadReckoningConfig& cfg) noexcept {
  if (!(distance_m > cfg.near_distance_m)) return cfg.epsilon_near_m;  // also catches NaN
  if (distance_m >= cfg.far_distance_m) return cfg.epsilon_far_m;
  const double t = (distance_m - cfg.near_distance_m) / (cfg.far_distance_m - cfg.near_distance_m);
  return cfg.epsilon_near_m + t * (cfg.epsilon_far_m - cfg.epsilon_near_m);
}

double angle_delta(double a, double b) noexcept {
  double d = std::fmod(a - b + kPi, kTwoPi);
  if (d < 0.0) d += kTwoPi;
  return d - kPi;
}

double rotation_error(const TrackPose& a, const TrackPose& b) noexcept {
  return std::max({std::fabs(angle_delta(a.yaw, b.yaw)), std::fabs(angle_delta(a.pitch, b.pitch)),
                   std::fabs(angle_delta(a.roll, b.roll))});
}

TrackPose predict(const TrackPose& sent, const TrackVelocity& velocity, double dt_s) noexcept {
  TrackPose p = sent;
  if (dt_s > 0.0 && std::isfinite(dt_s)) {
    p.x += velocity.x * dt_s;
    p.y += velocity.y * dt_s;
    p.z += velocity.z * dt_s;
  }
  return p;
}

std::optional<TrackVelocity> finite_difference(const TrackPose& a, double a_s, const TrackPose& b, double b_s,
                                               const DeadReckoningConfig& cfg) noexcept {
  const double dt = b_s - a_s;
  if (!(dt > 0.0) || dt > cfg.max_velocity_gap_s || !std::isfinite(dt)) return std::nullopt;
  if (!is_finite(a) || !is_finite(b)) return std::nullopt;
  TrackVelocity v{(b.x - a.x) / dt, (b.y - a.y) / dt, (b.z - a.z) / dt};
  if (!is_finite(v)) return std::nullopt;
  return v;
}

UpdateReason classify(const DrInput& in, const DeadReckoningConfig& cfg) noexcept {
  if (in.force_keyframe) return UpdateReason::Keepalive;
  if (!is_finite(in.now)) return UpdateReason::None;  // a broken read is never sent
  if (in.now_sector != in.sent_sector) return UpdateReason::Sector;
  if (in.now_flags != in.sent_flags) return UpdateReason::Flags;

  const double dt = in.now_s - in.sent_at_s;
  const bool near = in.observer_distance_m <= cfg.near_distance_m;
  const double rot_limit = near ? cfg.rotation_near_rad : cfg.rotation_far_rad;
  if (rotation_error(in.now, in.sent) > rot_limit) return UpdateReason::Rotation;

  const TrackPose predicted = predict(in.sent, in.velocity, dt);
  const double dx = in.now.x - predicted.x, dy = in.now.y - predicted.y, dz = in.now.z - predicted.z;
  const double error = std::sqrt(dx * dx + dy * dy + dz * dz);
  if (error > position_epsilon(in.observer_distance_m, cfg)) return UpdateReason::Threshold;

  const double keepalive = near ? cfg.keepalive_near_s : cfg.keepalive_far_s;
  if (dt >= keepalive) return UpdateReason::Keepalive;
  return UpdateReason::None;
}

bool is_finite(const TrackPose& pose) noexcept {
  return std::isfinite(pose.x) && std::isfinite(pose.y) && std::isfinite(pose.z) && std::isfinite(pose.yaw) &&
         std::isfinite(pose.pitch) && std::isfinite(pose.roll);
}

bool is_finite(const TrackVelocity& velocity) noexcept {
  return std::isfinite(velocity.x) && std::isfinite(velocity.y) && std::isfinite(velocity.z);
}

}  // namespace x4mp::features::auth
