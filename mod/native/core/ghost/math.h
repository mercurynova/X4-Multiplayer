#pragma once
// core/ghost: small vector / quaternion maths for the ghost interpolator (M3-02). Pure C++, header only, no allocation.
//
// Euler convention (the ONE place to change once S13.4 pins down what UIPosRot angles mean): angles are radians as on the wire
// (yaw, pitch, roll); R = Ry(yaw) * Rx(pitch) * Rz(roll) (yaw about +Y, pitch about +X, roll about +Z). euler_to_quat and
// quat_to_euler are exact inverses of each other (away from pitch = +-90 degrees); nothing else in core/ghost looks at angles.

#include <cmath>

namespace x4mp::ghost {

struct Vec3 {
  double x = 0, y = 0, z = 0;
};
[[nodiscard]] constexpr Vec3 operator+(Vec3 a, Vec3 b) noexcept { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
[[nodiscard]] constexpr Vec3 operator-(Vec3 a, Vec3 b) noexcept { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
[[nodiscard]] constexpr Vec3 operator*(Vec3 a, double s) noexcept { return {a.x * s, a.y * s, a.z * s}; }
[[nodiscard]] inline double length(Vec3 a) noexcept { return std::sqrt(a.x * a.x + a.y * a.y + a.z * a.z); }
[[nodiscard]] inline double distance(Vec3 a, Vec3 b) noexcept { return length(a - b); }

struct Euler {
  double yaw = 0, pitch = 0, roll = 0;
};

struct Quat {
  double w = 1, x = 0, y = 0, z = 0;
};
[[nodiscard]] constexpr Quat operator*(Quat a, Quat b) noexcept {
  return {a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z, a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
          a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x, a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w};
}
[[nodiscard]] inline Quat normalized(Quat q) noexcept {
  const double n = std::sqrt(q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z);
  if (n < 1e-12) return Quat{};
  return {q.w / n, q.x / n, q.y / n, q.z / n};
}
[[nodiscard]] constexpr double dot(Quat a, Quat b) noexcept { return a.w * b.w + a.x * b.x + a.y * b.y + a.z * b.z; }

[[nodiscard]] inline Quat euler_to_quat(Euler e) noexcept {
  const Quat qy{std::cos(e.yaw * 0.5), 0, std::sin(e.yaw * 0.5), 0};
  const Quat qx{std::cos(e.pitch * 0.5), std::sin(e.pitch * 0.5), 0, 0};
  const Quat qz{std::cos(e.roll * 0.5), 0, 0, std::sin(e.roll * 0.5)};
  return normalized(qy * qx * qz);
}

[[nodiscard]] inline Euler quat_to_euler(Quat q) noexcept {
  q = normalized(q);
  const double r02 = 2 * (q.x * q.z + q.w * q.y);
  const double r22 = 1 - 2 * (q.x * q.x + q.y * q.y);
  const double r10 = 2 * (q.x * q.y + q.w * q.z);
  const double r11 = 1 - 2 * (q.x * q.x + q.z * q.z);
  const double r12 = 2 * (q.y * q.z - q.w * q.x);
  Euler e;
  double s = -r12;
  if (s > 1) s = 1;
  if (s < -1) s = -1;
  e.pitch = std::asin(s);
  if (std::fabs(s) < 0.999999) {
    e.yaw = std::atan2(r02, r22);
    e.roll = std::atan2(r10, r11);
  } else {  // gimbal lock: fold the roll into the yaw
    const double r20 = 2 * (q.x * q.z - q.w * q.y);
    const double r00 = 1 - 2 * (q.y * q.y + q.z * q.z);
    e.yaw = std::atan2(-r20, r00);
    e.roll = 0;
  }
  return e;
}

// Spherical interpolation along the shortest arc. t outside [0,1] extrapolates along the same arc (angular-rate extrapolation).
[[nodiscard]] inline Quat slerp(Quat a, Quat b, double t) noexcept {
  double d = dot(a, b);
  if (d < 0) {
    b = {-b.w, -b.x, -b.y, -b.z};
    d = -d;
  }
  if (d > 0.9995) {  // nearly parallel: nlerp (also keeps small extrapolations exact enough)
    return normalized({a.w + (b.w - a.w) * t, a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t});
  }
  const double theta = std::acos(d > 1 ? 1 : d);
  const double s = std::sin(theta);
  const double wa = std::sin((1 - t) * theta) / s;
  const double wb = std::sin(t * theta) / s;
  return normalized({a.w * wa + b.w * wb, a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb});
}

// Cubic Hermite on a segment of length dt_s seconds: p0/p1 positions, v0/v1 velocities (m/s), u in [0,1].
[[nodiscard]] inline Vec3 hermite(Vec3 p0, Vec3 v0, Vec3 p1, Vec3 v1, double dt_s, double u) noexcept {
  const double u2 = u * u, u3 = u2 * u;
  const double h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
  return p0 * h00 + v0 * (h10 * dt_s) + p1 * h01 + v1 * (h11 * dt_s);
}

}  // namespace x4mp::ghost
