#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include "core/ghost/math.h"

using namespace x4mp::ghost;
using Catch::Approx;

TEST_CASE("euler <-> quaternion round trip", "[ghost][math]") {
  for (double yaw : {-3.0, -1.2, 0.0, 0.7, 3.0})
    for (double pitch : {-1.2, -0.3, 0.0, 0.5, 1.2})
      for (double roll : {-2.5, -0.4, 0.0, 1.1, 2.9}) {
        const Euler e = quat_to_euler(euler_to_quat({yaw, pitch, roll}));
        CHECK(e.yaw == Approx(yaw).margin(1e-9));
        CHECK(e.pitch == Approx(pitch).margin(1e-9));
        CHECK(e.roll == Approx(roll).margin(1e-9));
      }
}

TEST_CASE("slerp goes the short way, hits the ends and extrapolates the angular rate", "[ghost][math]") {
  const Quat a = euler_to_quat({0.1, 0, 0});
  const Quat b = euler_to_quat({0.3, 0, 0});
  CHECK(quat_to_euler(slerp(a, b, 0.0)).yaw == Approx(0.1).margin(1e-9));
  CHECK(quat_to_euler(slerp(a, b, 1.0)).yaw == Approx(0.3).margin(1e-9));
  CHECK(quat_to_euler(slerp(a, b, 0.5)).yaw == Approx(0.2).margin(1e-9));
  CHECK(quat_to_euler(slerp(a, b, 2.0)).yaw == Approx(0.5).margin(1e-9));
  // across the +-pi seam the short arc is used
  const Quat c = euler_to_quat({3.1, 0, 0});
  const Quat d = euler_to_quat({-3.1, 0, 0});
  const double mid = quat_to_euler(slerp(c, d, 0.5)).yaw;
  CHECK(std::fabs(std::fabs(mid) - 3.14159265) < 1e-6);
}

TEST_CASE("hermite reproduces a cubic exactly and the end points", "[ghost][math]") {
  // p(t) = t^3 on t in [0,2]: v = 3t^2
  const Vec3 p0{0, 0, 0}, p1{8, 0, 0}, v0{0, 0, 0}, v1{12, 0, 0};
  CHECK(hermite(p0, v0, p1, v1, 2.0, 0.0).x == Approx(0.0));
  CHECK(hermite(p0, v0, p1, v1, 2.0, 1.0).x == Approx(8.0));
  CHECK(hermite(p0, v0, p1, v1, 2.0, 0.5).x == Approx(1.0));  // t = 1 -> 1
}
