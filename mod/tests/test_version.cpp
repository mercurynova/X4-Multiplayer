#include <catch2/catch_test_macros.hpp>

#include "core/version/version.h"

TEST_CASE("hello line carries version, protocol and build pin", "[version]") {
  const std::string line = x4mp::version::hello_line();
  CHECK(line == "x4mp " + std::string(x4mp::version::mod_version()) +
                    " hello (protocol 0.1, game build pin 900-611726)");
  CHECK(x4mp::version::game_build_pin() == "900-611726");
}