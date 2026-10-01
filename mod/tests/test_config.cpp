#include <catch2/catch_test_macros.hpp>

#include "core/config/config.h"

using namespace x4mp::config;

TEST_CASE("config defaults", "[config]") {
  const Config c = defaults();
  CHECK(c.tcp_port == 47780);
  CHECK(c.log_level == x4mp::log::Level::Info);
}

TEST_CASE("config parse overlays known keys", "[config]") {
  std::string errors;
  const auto c = parse_json(R"({"server_host":"example.org","tcp_port":5000,"log_level":"debug","extra":1})", &errors);
  REQUIRE(c.has_value());
  CHECK(c->server_host == "example.org");
  CHECK(c->tcp_port == 5000);
  CHECK(c->log_level == x4mp::log::Level::Debug);
  CHECK(errors.empty());
}

TEST_CASE("config invalid value falls back to the default with an error", "[config]") {
  std::string errors;
  const auto c = parse_json(R"({"tcp_port":70000,"log_level":"loud"})", &errors);
  REQUIRE(c.has_value());
  CHECK(c->tcp_port == 47780);
  CHECK(c->log_level == x4mp::log::Level::Info);
  CHECK_FALSE(errors.empty());
}

TEST_CASE("config rejects non-object JSON", "[config]") {
  CHECK_FALSE(parse_json("not json").has_value());
  CHECK_FALSE(parse_json("[1,2]").has_value());
}