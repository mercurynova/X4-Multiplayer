#include <catch2/catch_test_macros.hpp>

#include <nlohmann/json.hpp>

#include "host/status_json.h"

using namespace x4mp::host;

TEST_CASE("status json: only state is required, the rest is omitted when empty", "[host][status]") {
  const auto j = nlohmann::json::parse(make_status_json(StatusFields{.state = "connecting"}));
  CHECK(j["v"] == 1);
  CHECK(j["state"] == "connecting");
  CHECK_FALSE(j.contains("detail"));
  CHECK_FALSE(j.contains("reject"));
  CHECK_FALSE(j.contains("progress"));
}

TEST_CASE("status json: every contract field round-trips and strings are escaped", "[host][status]") {
  StatusFields f;
  f.state = "rejected";
  f.detail = "line1\n\"quoted\" \\ back";
  f.reject = "build";
  f.server = "[::1]:47780";
  f.role = "client";
  f.team = 2;
  f.team_name = "Team 2";
  f.ping_ms = 12;
  f.players = 3;
  f.progress = 1.7;  // clamped
  const auto j = nlohmann::json::parse(make_status_json(f));
  CHECK(j["state"] == "rejected");
  CHECK(j["detail"] == "line1\n\"quoted\" \\ back");
  CHECK(j["reject"] == "build");
  CHECK(j["server"] == "[::1]:47780");
  CHECK(j["role"] == "client");
  CHECK(j["team"] == 2);
  CHECK(j["team_name"] == "Team 2");
  CHECK(j["ping_ms"] == 12);
  CHECK(j["players"] == 3);
  CHECK(j["progress"] == 1.0);
}

TEST_CASE("notify, error and load_save payloads", "[host][status]") {
  CHECK(nlohmann::json::parse(make_notify_json("hi \"x\"", "warn"))["text"] == "hi \"x\"");
  CHECK(nlohmann::json::parse(make_error_json("bad_join", "why"))["code"] == "bad_join");
  const auto plain = nlohmann::json::parse(make_load_save_json("x4mp_abc", false));
  CHECK(plain["name"] == "x4mp_abc");
  CHECK_FALSE(plain.contains("fallback"));
  CHECK(nlohmann::json::parse(make_load_save_json("x4mp_abc", true))["fallback"] == true);
}
