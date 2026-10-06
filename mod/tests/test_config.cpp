#include <algorithm>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/config/config.h"

using namespace x4mp::config;

namespace {
// A unique scratch directory, removed on destruction.
struct TempDir {
  std::filesystem::path path;
  TempDir() {
    static std::atomic<int> n{0};
    path = std::filesystem::temp_directory_path() /
           ("x4mp_cfg_test_" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + "_" +
            std::to_string(n++));
    std::filesystem::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    std::filesystem::remove_all(path, ec);
  }
  [[nodiscard]] std::filesystem::path write(const std::string& name, const std::string& text) const {
    const auto p = path / name;
    std::ofstream(p, std::ios::binary) << text;
    return p;
  }
};

bool has_diag(const LoadResult& r, x4mp::log::Level level, std::string_view needle) {
  return std::any_of(r.diagnostics.begin(), r.diagnostics.end(), [&](const Diagnostic& d) {
    return d.level == level && d.message.find(needle) != std::string::npos;
  });
}

class CaptureSink final : public x4mp::log::Sink {
 public:
  void write(x4mp::log::Level level, std::int64_t, std::string_view text) override {
    std::lock_guard lock(m);
    lines.push_back(std::string(x4mp::log::level_name(level)) + " " + std::string(text));
  }
  std::mutex m;
  std::vector<std::string> lines;
};
}  // namespace

TEST_CASE("config defaults", "[config]") {
  const Config c = defaults();
  CHECK(c.tcp_port == 47780);
  CHECK(c.log_level == x4mp::log::Level::Info);
  CHECK(c.log_rate_limit == 5);
  CHECK_FALSE(c.launch.active);
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

TEST_CASE("load: no files means defaults and no diagnostics", "[config]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.path / "missing.json";
  o.launch_file = d.path / "launch.json";
  const auto r = load(o);
  CHECK(r.diagnostics.empty());
  CHECK_FALSE(r.launch_consumed);
  CHECK(r.config.server_host == defaults().server_host);
  CHECK(load(LoadOptions{}).diagnostics.empty());  // empty paths are fine too
}

TEST_CASE("load precedence: defaults < user file < launch.json", "[config]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.write("x4mp.json",
                        R"({"server_host":"user.example","tcp_port":5000,"player_name":"FromUser","log_level":"warn",
                            "log_rate_limit":9})");
  o.launch_file = d.write("launch.json",
                          R"({"server":"launch.example:6000","name":"FromLaunch","save":"slot3","password_ref":"inline",
                              "password":"s3cret","expires":4102444800})");
  o.now_unix = 1'700'000'000;
  const auto r = load(o);
  CHECK(r.diagnostics.empty());
  CHECK(r.config.server_host == "launch.example");  // launch beats user
  CHECK(r.config.tcp_port == 6000);
  CHECK(r.config.player_name == "FromLaunch");
  CHECK(r.config.log_level == x4mp::log::Level::Warn);  // user beats default
  CHECK(r.config.log_rate_limit == 9);
  CHECK(r.config.outbox_byte_cap == defaults().outbox_byte_cap);  // untouched default
  CHECK(r.config.launch.active);
  CHECK(r.config.launch.save == "slot3");
  CHECK(r.config.launch.password_ref == "inline");
  CHECK(r.config.password == "s3cret");
}

TEST_CASE("launch.json without a port keeps the user/default port", "[config]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.write("x4mp.json", R"({"tcp_port":5000})");
  o.launch_file = d.write("launch.json", R"({"server":"just.host"})");
  const auto r = load(o);
  CHECK(r.config.server_host == "just.host");
  CHECK(r.config.tcp_port == 5000);
}

TEST_CASE("launch.json is consumed exactly once", "[config]") {
  TempDir d;
  LoadOptions o;
  o.launch_file = d.write("launch.json", R"({"server":"once.example:7000"})");
  const auto first = load(o);
  CHECK(first.launch_consumed);
  CHECK(first.config.server_host == "once.example");
  CHECK(first.config.launch.active);
  CHECK_FALSE(std::filesystem::exists(o.launch_file));  // gone from its original name
  const auto second = load(o);
  CHECK_FALSE(second.launch_consumed);
  CHECK(second.config.server_host == defaults().server_host);  // not applied again
  CHECK_FALSE(second.config.launch.active);
  CHECK(second.diagnostics.empty());
}

TEST_CASE("expired or malformed launch.json is consumed but ignored", "[config]") {
  TempDir d;
  LoadOptions o;
  o.now_unix = 2000;
  o.launch_file = d.write("launch.json", R"({"server":"old.example","expires":1000})");
  auto r = load(o);
  CHECK(r.launch_consumed);
  CHECK(r.config.server_host == defaults().server_host);
  CHECK_FALSE(r.config.launch.active);
  CHECK(has_diag(r, x4mp::log::Level::Warn, "expired"));

  o.launch_file = d.write("launch.json", "{ not json");
  r = load(o);
  CHECK(r.launch_consumed);
  CHECK(has_diag(r, x4mp::log::Level::Error, "launch file"));
  CHECK_FALSE(std::filesystem::exists(o.launch_file));
}

TEST_CASE("invalid values fall back to the default and the diagnostic names the key", "[config]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.write("x4mp.json",
                        R"({"tcp_port":"not-a-number","log_level":"shout","server_host":"","log_rate_limit":0,
                            "outbox_byte_cap":12,"mystery":true,"player_name":"Okay"})");
  const auto r = load(o);
  const Config def = defaults();
  CHECK(r.config.tcp_port == def.tcp_port);
  CHECK(r.config.log_level == def.log_level);
  CHECK(r.config.server_host == def.server_host);
  CHECK(r.config.log_rate_limit == def.log_rate_limit);
  CHECK(r.config.outbox_byte_cap == def.outbox_byte_cap);
  CHECK(r.config.player_name == "Okay");  // valid keys in the same file still apply
  for (const char* key : {"tcp_port", "log_level", "server_host", "log_rate_limit", "outbox_byte_cap"}) {
    INFO(key);
    CHECK(has_diag(r, x4mp::log::Level::Error, std::string("key '") + key + "'"));
  }
  CHECK(has_diag(r, x4mp::log::Level::Warn, "unknown key 'mystery'"));
  CHECK_FALSE(has_diag(r, x4mp::log::Level::Error, "not-a-number"));  // values are never echoed
}

TEST_CASE("invalid user file JSON yields an error and defaults", "[config]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.write("x4mp.json", "[1,2,3]");
  const auto r = load(o);
  CHECK(has_diag(r, x4mp::log::Level::Error, "user file"));
  CHECK(r.config.tcp_port == defaults().tcp_port);
}

TEST_CASE("invalid config values are reported as error log lines naming the key", "[config][log]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.write("x4mp.json", R"({"tcp_port":0})");
  const auto r = load(o);
  auto sink = std::make_shared<CaptureSink>();
  {
    x4mp::log::Logger logger(x4mp::log::Logger::Options{}, {sink});
    report(r, logger);
    logger.flush();
  }
  std::lock_guard lock(sink->m);
  REQUIRE(sink->lines.size() == 1);
  CHECK(sink->lines[0].starts_with("ERROR "));
  CHECK(sink->lines[0].find("tcp_port") != std::string::npos);
}

TEST_CASE("the password never appears in diagnostics, dumps or log output", "[config][log]") {
  TempDir d;
  LoadOptions o;
  o.user_file = d.write("x4mp.json", R"({"password":"hunter2-SECRET","tcp_port":-1})");
  o.launch_file = d.write("launch.json", R"({"password":"launch-SECRET","server":":::bad"})");
  const auto r = load(o);
  CHECK(r.config.password == "launch-SECRET");  // it is loaded, just never printed
  auto sink = std::make_shared<CaptureSink>();
  {
    x4mp::log::Logger logger(x4mp::log::Logger::Options{}, {sink});
    report(r, logger);
    logger.log_raw(x4mp::log::Level::Info, describe(r.config));
    logger.flush();
  }
  std::string all;
  for (const auto& dg : r.diagnostics) all += dg.message + "\n";
  all += describe(r.config);
  {
    std::lock_guard lock(sink->m);
    for (const auto& l : sink->lines) all += l + "\n";
  }
  CHECK(all.find("SECRET") == std::string::npos);
  CHECK(all.find("password=<redacted>") != std::string::npos);
  CHECK(describe(defaults()).find("password=<unset>") != std::string::npos);
}

TEST_CASE("frame_budget_us and log_categories (M2-04)", "[config]") {
  std::string errors;
  auto cfg = parse_json(R"({"frame_budget_us":800,"log_categories":{"net":"debug","perf":"warn"}})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->frame_budget_us == 800);
  REQUIRE(cfg->log_categories.size() == 2);
  CHECK(cfg->log_categories[0].first == "net");
  CHECK(cfg->log_categories[0].second == x4mp::log::Level::Debug);
  CHECK(describe(*cfg).find("log_category.perf=") != std::string::npos);

  errors.clear();
  cfg = parse_json(R"({"frame_budget_us":5,"log_categories":{"net":"loud"}})", &errors);
  REQUIRE(cfg);
  CHECK(cfg->frame_budget_us == 1500);  // out of range: default kept
  CHECK(cfg->log_categories.empty());
  CHECK(errors.find("frame_budget_us") != std::string::npos);
  CHECK(errors.find("log_categories.net") != std::string::npos);
  CHECK(errors.find("loud") == std::string::npos);  // values are never echoed

  errors.clear();
  cfg = parse_json(R"({"log_categories":"net"})", &errors);
  REQUIRE(cfg);
  CHECK(errors.find("log_categories") != std::string::npos);
}

TEST_CASE("diag switches (M3-23): default off, nested object and flat keys, invalid values keep the default", "[config][diag]") {
  std::string errors;
  auto cfg = parse_json(R"({})", &errors);
  REQUIRE(cfg);
  CHECK_FALSE(cfg->diag.any());
  CHECK(describe(*cfg).find("diag.") == std::string::npos);  // nothing printed while every switch is off

  cfg = parse_json(R"({"diag":{"takeover_keep_original":true,"ghosts_off":true}})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.takeover_keep_original);
  CHECK(cfg->diag.ghosts_off);
  CHECK_FALSE(cfg->diag.takeover_off);
  CHECK_FALSE(cfg->diag.janitor_off);
  CHECK(describe(*cfg).find("diag.takeover_keep_original=true") != std::string::npos);
  CHECK(describe(*cfg).find("diag.janitor_off=false") != std::string::npos);

  cfg = parse_json(R"({"diag.takeover_off":true,"diag.janitor_off":true})", &errors);  // the flat spelling the docs use
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.takeover_off);
  CHECK(cfg->diag.janitor_off);
  CHECK_FALSE(cfg->diag.ghosts_off);

  cfg = parse_json(R"({"diag":{"takeover_off":true},"diag.takeover_off":false})", &errors);  // the flat key wins
  REQUIRE(cfg);
  CHECK_FALSE(cfg->diag.takeover_off);

  errors.clear();
  cfg = parse_json(R"({"diag":{"takeover_off":"yes","ghosts_off":1}})", &errors);
  REQUIRE(cfg);
  CHECK_FALSE(cfg->diag.any());
  CHECK(errors.find("diag.takeover_off") != std::string::npos);
  CHECK(errors.find("diag.ghosts_off") != std::string::npos);
  CHECK(errors.find("yes") == std::string::npos);  // values are never echoed

  errors.clear();
  cfg = parse_json(R"({"diag":true})", &errors);
  REQUIRE(cfg);
  CHECK(errors.find("'diag'") != std::string::npos);
}

TEST_CASE("diag switches (M3-28): the five map fog switches parse in both spellings, default off, are described, and unknown ones still warn", "[config][diag][m328]") {
  std::string errors;
  auto cfg = parse_json(R"({})", &errors);
  REQUIRE(cfg);
  CHECK_FALSE(cfg->diag.team_self_relation_100);
  CHECK_FALSE(cfg->diag.team_move_respawn);
  CHECK_FALSE(cfg->diag.avatars_inert_once);
  CHECK_FALSE(cfg->diag.dress_no_radar_no_known);
  CHECK_FALSE(cfg->diag.no_set_faction_known);

  cfg = parse_json(R"({"diag":{"team_self_relation_100":true,"avatars_inert_once":true}})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.any());
  CHECK(cfg->diag.team_self_relation_100);
  CHECK(cfg->diag.avatars_inert_once);
  CHECK_FALSE(cfg->diag.team_move_respawn);
  CHECK(describe(*cfg).find("diag.team_self_relation_100=true") != std::string::npos);
  CHECK(describe(*cfg).find("diag.no_set_faction_known=false") != std::string::npos);

  cfg = parse_json(R"({"diag.team_move_respawn":true,"diag.dress_no_radar_no_known":true,"diag.no_set_faction_known":true})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.team_move_respawn);
  CHECK(cfg->diag.dress_no_radar_no_known);
  CHECK(cfg->diag.no_set_faction_known);
  CHECK_FALSE(cfg->diag.team_self_relation_100);

  errors.clear();
  cfg = parse_json(R"({"diag":{"team_move_respawn":"yes"}})", &errors);
  REQUIRE(cfg);
  CHECK_FALSE(cfg->diag.any());
  CHECK(errors.find("diag.team_move_respawn") != std::string::npos);
}

TEST_CASE("diag switch (M3-30): takeover_mode_reown parses in both spellings, defaults off and is described", "[config][diag][m330]") {
  std::string errors;
  auto cfg = parse_json(R"({})", &errors);
  REQUIRE(cfg);
  CHECK_FALSE(cfg->diag.takeover_mode_reown);
  CHECK_FALSE(cfg->diag.any());
  cfg = parse_json(R"({"diag":{"takeover_mode_reown":true}})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.takeover_mode_reown);
  CHECK(cfg->diag.any());
  CHECK(describe(*cfg).find("diag.takeover_mode_reown=true") != std::string::npos);
  cfg = parse_json(R"({"diag.takeover_mode_reown":true})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.takeover_mode_reown);
  cfg = parse_json(R"({"diag":{"takeover_keep_own_ship":true}})", &errors);
  REQUIRE(cfg);
  CHECK(errors.empty());
  CHECK(cfg->diag.takeover_keep_own_ship);
  CHECK_FALSE(cfg->diag.takeover_mode_reown);
  CHECK(describe(*cfg).find("diag.takeover_keep_own_ship=true") != std::string::npos);
}

TEST_CASE("diag switches (M3-23): the user file layer reads them and an unknown inner key only warns", "[config][diag]") {
  TempDir dir;
  LoadOptions o;
  o.user_file = dir.write("x4mp.json", R"({"diag":{"janitor_off":true,"nonsense":true},"log_level":"debug"})");
  const auto r = load(o);
  CHECK(r.config.diag.janitor_off);
  CHECK_FALSE(r.config.diag.takeover_off);
  CHECK(r.config.log_level == x4mp::log::Level::Debug);
  CHECK(has_diag(r, x4mp::log::Level::Warn, "diag.nonsense"));
  CHECK_FALSE(has_diag(r, x4mp::log::Level::Warn, "unknown key 'diag'"));
}
