#include <catch2/catch_test_macros.hpp>

#include "core/net/net.h"

TEST_CASE("net exposes the shared protocol constants", "[net]") {
  CHECK(x4mp::net::protocol_version_string() == "0.1");
  CHECK(x4mp::net::default_tcp_port() == 47780);
}