#include <algorithm>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "host/host_log.h"
#include "host/redact.h"
#include "host_fakes.h"

using namespace x4mp::host;

namespace {
bool contains(const std::string& hay, std::string_view needle) { return hay.find(needle) != std::string::npos; }
}  // namespace

TEST_CASE("key patterns redact the value, keep the key", "[host][redact]") {
  CHECK(Redactor::redact_keys("login password=hunter2 ok") == "login password=<redacted> ok");
  CHECK(Redactor::redact_keys("Password: hunter2") == "Password: <redacted>");
  CHECK(Redactor::redact_keys(R"({"password":"hunter2","name":"bob"})") == R"({"password":"<redacted>","name":"bob"})");
  CHECK(Redactor::redact_keys("resume_token=ABCDEF0123 next") == "resume_token=<redacted> next");
  CHECK(Redactor::redact_keys("admin_password = s3cret!") == "admin_password = <redacted>");
  CHECK(Redactor::redact_keys("api_key=k1&x=2") == "api_key=<redacted>&x=2");
  CHECK(Redactor::redact_keys("player_key: 00ff00ff") == "player_key: <redacted>");
  CHECK(Redactor::redact_keys("Authorization: Bearer abc.def.ghi") == "Authorization: <redacted>");
  CHECK(Redactor::redact_keys("sent Bearer abc.def") == "sent Bearer <redacted>");
}

TEST_CASE("key patterns leave ordinary text alone", "[host][redact]") {
  CHECK(Redactor::redact_keys("tokens left: 5") == "tokens left: 5");
  CHECK(Redactor::redact_keys("password") == "password");
  CHECK(Redactor::redact_keys("password=") == "password=");
  CHECK(Redactor::redact_keys("the secretary arrived") == "the secretary arrived");
  CHECK(Redactor::redact_keys("connected to host 127.0.0.1:47780") == "connected to host 127.0.0.1:47780");
  CHECK(Redactor::redact_keys("") == "");
}

TEST_CASE("registered secrets are replaced everywhere", "[host][redact]") {
  Redactor r;
  r.add_secret("hunter2hunter2");
  r.add_secret("ab");  // too short: ignored
  CHECK(r.apply("user typed hunter2hunter2 twice: hunter2hunter2") == "user typed <redacted> twice: <redacted>");
  CHECK(r.apply("about ab") == "about ab");
  r.clear_secrets();
  CHECK(r.apply("hunter2hunter2") == "hunter2hunter2");
}

TEST_CASE("secrets never reach the log file", "[host][redact][log]") {
  x4mp::test::TempDir dir;
  const auto file = dir.path / "logs" / "x4mp.log";
  {
    HostLog::Options o;
    o.file = file;
    o.level = Level::Debug;
    HostLog log(std::move(o));
    REQUIRE(log.file_open());

    x4mp::config::Config cfg;
    cfg.password = "correct-horse-battery";
    log.apply_config(cfg);

    // 1. a registered secret in free text
    X4MP_CLOG(log, Cat::Auth, Level::Info, "joining with {}", cfg.password);
    // 2. key patterns
    X4MP_CLOG(log, Cat::Auth, Level::Info, "welcome resume_token={} player_key={}", "TOKENVALUE123", "KEYVALUE456");
    // 3. a JSON blob and the raw path
    log.raw(Cat::Net, Level::Info, R"({"password":"JSONSECRET99","server":"x"})");
    // 4. a safe line survives
    X4MP_CLOG(log, Cat::Net, Level::Info, "connected to {}", "127.0.0.1");
    log.flush();
  }
  const std::string text = dir.read(std::filesystem::path("logs") / "x4mp.log");
  REQUIRE_FALSE(text.empty());
  CHECK_FALSE(contains(text, "correct-horse-battery"));
  CHECK_FALSE(contains(text, "TOKENVALUE123"));
  CHECK_FALSE(contains(text, "KEYVALUE456"));
  CHECK_FALSE(contains(text, "JSONSECRET99"));
  CHECK(contains(text, "<redacted>"));
  CHECK(contains(text, "[auth]"));
  CHECK(contains(text, "connected to 127.0.0.1"));
}

TEST_CASE("native mirror receives ERROR lines, redacted", "[host][redact][log]") {
  std::mutex m;
  std::vector<std::string> mirrored;
  HostLog::Options o;
  o.level = Level::Debug;
  o.native_mirror = [&](Level level, std::string_view text) {
    const std::lock_guard lock(m);
    mirrored.push_back(std::string(x4mp::log::level_name(level)) + " " + std::string(text));
  };
  HostLog log(std::move(o));
  x4mp::config::Config cfg;
  cfg.password = "swordfish-1234";
  log.apply_config(cfg);

  X4MP_CLOG(log, Cat::Host, Level::Info, "info line {}", cfg.password);
  X4MP_CLOG(log, Cat::Host, Level::Error, "failed login with {}", cfg.password);
  log.flush();

  const std::lock_guard lock(m);
  REQUIRE(mirrored.size() == 1);  // only the error
  CHECK(contains(mirrored[0], "ERROR"));
  CHECK(contains(mirrored[0], "failed login with <redacted>"));
  CHECK_FALSE(contains(mirrored[0], "swordfish"));
}

TEST_CASE("categories have their own level", "[host][log]") {
  x4mp::test::TempDir dir;
  HostLog::Options o;
  o.file = dir.path / "x4mp.log";
  o.level = Level::Warn;
  HostLog log(std::move(o));

  x4mp::config::Config cfg;
  cfg.log_level = Level::Warn;
  cfg.log_categories = {{"net", Level::Debug}, {"nonsense", Level::Debug}};
  const auto warnings = log.apply_config(cfg);
  REQUIRE(warnings.size() == 1);
  CHECK(contains(warnings[0], "nonsense"));

  CHECK(log.category_level(Cat::Net) == Level::Debug);
  CHECK(log.category_level(Cat::Sess) == Level::Warn);
  CHECK(log.enabled(Cat::Net, Level::Debug));
  CHECK_FALSE(log.enabled(Cat::Sess, Level::Info));

  X4MP_CLOG(log, Cat::Net, Level::Debug, "net debug visible");
  X4MP_CLOG(log, Cat::Sess, Level::Info, "sess info hidden");
  X4MP_CLOG(log, Cat::Sess, Level::Warn, "sess warn visible");
  log.flush();
  const std::string text = dir.read("x4mp.log");
  CHECK(contains(text, "[net] net debug visible"));
  CHECK_FALSE(contains(text, "sess info hidden"));
  CHECK(contains(text, "[sess] sess warn visible"));
}

TEST_CASE("category names parse case-insensitively", "[host][log]") {
  CHECK(parse_cat("NET") == Cat::Net);
  CHECK(parse_cat("perf") == Cat::Perf);
  CHECK(parse_cat("bogus") == std::nullopt);
  CHECK(std::string(cat_name(Cat::Ghost)) == "ghost");
}
