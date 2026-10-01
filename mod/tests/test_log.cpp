#include <catch2/catch_test_macros.hpp>

#include "core/log/log.h"

using namespace x4mp::log;

TEST_CASE("log level names and parsing", "[log]") {
  CHECK(std::string_view(level_name(Level::Warn)) == "WARN");
  CHECK(parse_level("info") == Level::Info);
  CHECK(parse_level("ERROR") == Level::Error);
  CHECK_FALSE(parse_level("verbose").has_value());
  CHECK_FALSE(parse_level("").has_value());
}

TEST_CASE("log filtering and line format", "[log]") {
  CHECK(should_log(Level::Info, Level::Warn));
  CHECK(should_log(Level::Info, Level::Info));
  CHECK_FALSE(should_log(Level::Info, Level::Debug));
  CHECK(format_line(Level::Info, "hi") == "[INFO] hi");
}