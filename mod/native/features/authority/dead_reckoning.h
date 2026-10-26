#pragma once
// features/authority: dead reckoning for the captured world (M4-07; docs/m4-plan.md W2, protocol.md 11).
//
// The authority polls an entity (M4-06) and must decide whether the polled pose is worth a WorldUpdate entry. Sending
// every poll would be a full sample stream; the reference protocol sends a sample only when the client's prediction
// would be visibly wrong:
//
//   * position: the client extrapolates the last sample with its velocity. Send when |now - predicted| exceeds
//     epsilon(distance to the observer): 0.5 m within 2 km, rising linearly to 20 m at 50 km (a far ship is a dot).
//   * rotation: send when any Euler angle moved more than 0.5 deg near, 1 deg far (no angular velocity is predicted).
//   * a sector or state-flag change is a discontinuity: always sent.
//   * a keepalive (Keyframe) every 1 s near / 5 s far, so a stationary ship still refreshes and a lost entry heals.
//
// Velocity is the finite difference of the last two polls (X4 exports no velocity getter). The wire stores it as 0.25 m/s
// units (4 m/s when VelCoarse), so the prediction the client runs uses the QUANTISED velocity; the streamer feeds the
// quantised value back into the track after a send. This file is pure arithmetic: no SDK, no flatbuffers.

#include <cmath>
#include <cstdint>
#include <optional>

namespace x4mp::features::auth {

struct TrackPose {
  double x = 0.0, y = 0.0, z = 0.0;  // sector-local metres
  double yaw = 0.0, pitch = 0.0, roll = 0.0;  // radians
};

struct TrackVelocity {
  double x = 0.0, y = 0.0, z = 0.0;  // m/s
};

struct DeadReckoningConfig {
  double near_distance_m = 2000.0;  // "Near" tier boundary
  double far_distance_m = 50000.0;  // the far end of the epsilon ramp
  double epsilon_near_m = 0.5;
  double epsilon_far_m = 20.0;
  double rotation_near_rad = 0.5 * 3.14159265358979323846 / 180.0;
  double rotation_far_rad = 1.0 * 3.14159265358979323846 / 180.0;
  double keepalive_near_s = 1.0;
  double keepalive_far_s = 5.0;
  double max_velocity_gap_s = 2.0;  // a longer gap between two polls makes the finite difference meaningless
};

// Why an entity is on the wire this tick. None = the prediction is still good enough, nothing is sent.
enum class UpdateReason : std::uint8_t {
  None = 0,
  Threshold,   // position error above epsilon(d)
  Rotation,    // an Euler angle moved above the threshold
  Flags,       // StateFlags changed (docked, travel drive, ...)
  Sector,      // the entity changed sector
  Keepalive,   // the periodic Keyframe
};

[[nodiscard]] const char* to_string(UpdateReason reason) noexcept;

// 0.5 m up to `near_distance_m`, linear in distance to `epsilon_far_m` at `far_distance_m`, capped beyond.
[[nodiscard]] double position_epsilon(double distance_m, const DeadReckoningConfig& cfg = {}) noexcept;

// The shortest signed difference a - b on the circle, in [-pi, pi].
[[nodiscard]] double angle_delta(double a, double b) noexcept;
// The largest absolute Euler difference of two poses (yaw, pitch, roll), in radians.
[[nodiscard]] double rotation_error(const TrackPose& a, const TrackPose& b) noexcept;

// The pose the client would predict at `now_s` from the last sent sample and its velocity.
[[nodiscard]] TrackPose predict(const TrackPose& sent, const TrackVelocity& velocity, double dt_s) noexcept;

// (b - a) / dt. nullopt when the gap is not positive, is longer than cfg.max_velocity_gap_s, or the input is not finite.
[[nodiscard]] std::optional<TrackVelocity> finite_difference(const TrackPose& a, double a_s, const TrackPose& b, double b_s,
                                                             const DeadReckoningConfig& cfg = {}) noexcept;

struct DrInput {
  TrackPose sent;        // the last pose put on the wire
  TrackPose now;         // the pose just read from the game
  TrackVelocity velocity;  // the last derived velocity (what the client predicts with)
  double sent_at_s = 0.0;
  double now_s = 0.0;
  std::uint16_t sent_flags = 0;
  std::uint16_t now_flags = 0;
  std::uint16_t sent_sector = 0;
  std::uint16_t now_sector = 0;
  double observer_distance_m = 0.0;  // from the authority's own ship / the focus centre; far when unknown
  bool force_keyframe = false;       // the first sample after a spawn
};

// The reason to send, or None. Sector and flag changes win over the thresholds; the keepalive is the fallback.
[[nodiscard]] UpdateReason classify(const DrInput& in, const DeadReckoningConfig& cfg = {}) noexcept;

// True when the pose is usable (every component finite).
[[nodiscard]] bool is_finite(const TrackPose& pose) noexcept;
[[nodiscard]] bool is_finite(const TrackVelocity& velocity) noexcept;

}  // namespace x4mp::features::auth
