#include <catch2/catch_test_macros.hpp>

#include "host/build_check.h"

using namespace x4mp::host;

namespace {
BuildInfo info(const char* version, std::optional<std::string> suffix, std::optional<x4mp::game::GameVersionPod> pod = std::nullopt,
               int types = 900) {
  BuildInfo b;
  b.game_version = version;
  b.build_suffix = std::move(suffix);
  b.version = pod;
  b.game_types_build = types;
  return b;
}
}  // namespace

TEST_CASE("parse_version_code and extract_build_number", "[host][build]") {
  CHECK(parse_version_code("9.00") == 900);
  CHECK(parse_version_code("X4 9.00 (611726)") == 900);
  CHECK(parse_version_code("7.50") == 750);
  CHECK(parse_version_code("garbage") == std::nullopt);
  CHECK(parse_version_code("") == std::nullopt);

  CHECK(extract_build_number("611726") == "611726");
  CHECK(extract_build_number("(611726)") == "611726");
  CHECK(extract_build_number("9.00 (611726)") == "611726");
  CHECK(extract_build_number("9.00") == std::nullopt);
  CHECK(extract_build_number("") == std::nullopt);
  CHECK(extract_build_number("a 1234 b 56789012 c") == "56789012");
}

TEST_CASE("the pinned build is supported", "[host][build]") {
  const auto r = check_build(info("9.00", "611726"), "900-611726");
  CHECK(r.status == BuildStatus::Supported);
  CHECK(r.detected == "900-611726");

  // The game's own version struct wins over the string.
  const auto r2 = check_build(info("", "(611726)", x4mp::game::GameVersionPod{9, 0}), "900-611726");
  CHECK(r2.status == BuildStatus::Supported);
}

TEST_CASE("a different build number is refused with a reason", "[host][build]") {
  const auto r = check_build(info("9.00", "611727"), "900-611726");
  CHECK(r.status == BuildStatus::Unsupported);
  CHECK(r.detected == "900-611727");
  CHECK(r.reason.find("Unsupported X4 build") != std::string::npos);
  CHECK(r.reason.find("900-611726") != std::string::npos);  // names what is supported
}

TEST_CASE("a different game version is refused even without a build number", "[host][build]") {
  const auto r = check_build(info("9.10", std::nullopt), "900-611726");
  CHECK(r.status == BuildStatus::Unsupported);
  CHECK(r.reason.find("version 910") != std::string::npos);

  const auto r2 = check_build(info("", std::nullopt, x4mp::game::GameVersionPod{8, 0}, 800), "900-611726");
  CHECK(r2.status == BuildStatus::Unsupported);
}

TEST_CASE("matching version with an unreadable build number is unverified, not refused", "[host][build]") {
  const auto r = check_build(info("9.00", std::nullopt), "900-611726");
  CHECK(r.status == BuildStatus::Unverified);
  CHECK(r.detected == "900-?");
  const auto r2 = check_build(info("9.00", ""), "900-611726");
  CHECK(r2.status == BuildStatus::Unverified);
}

TEST_CASE("an undeterminable version or a broken pin is refused", "[host][build]") {
  const auto r = check_build(info("", std::nullopt, std::nullopt, 0), "900-611726");
  CHECK(r.status == BuildStatus::Unsupported);
  CHECK(r.detected == "?-?");
  CHECK_FALSE(r.reason.empty());

  const auto r2 = check_build(info("9.00", "611726"), "nonsense");
  CHECK(r2.status == BuildStatus::Unsupported);
}

TEST_CASE("the version falls back to the SDK types build", "[host][build]") {
  const auto r = check_build(info("", "611726", std::nullopt, 900), "900-611726");
  CHECK(r.status == BuildStatus::Supported);
}
