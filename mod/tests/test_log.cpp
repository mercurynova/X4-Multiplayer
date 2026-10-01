#include <algorithm>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/log/log.h"

using namespace x4mp::log;

namespace {
class CaptureSink final : public Sink {
 public:
  explicit CaptureSink(Level min = Level::Debug) : min_(min) {}
  void write(Level level, std::int64_t, std::string_view text) override {
    std::lock_guard lock(m_);
    lines_.push_back(std::string(level_name(level)) + " " + std::string(text));
  }
  [[nodiscard]] Level min_level() const noexcept override { return min_; }
  [[nodiscard]] std::vector<std::string> lines() const {
    std::lock_guard lock(m_);
    return lines_;
  }
  [[nodiscard]] std::size_t count(std::string_view needle) const {
    std::lock_guard lock(m_);
    return static_cast<std::size_t>(std::count_if(lines_.begin(), lines_.end(), [&](const std::string& l) {
      return l.find(needle) != std::string::npos;
    }));
  }

 private:
  Level min_;
  mutable std::mutex m_;
  std::vector<std::string> lines_;
};

std::atomic<std::int64_t> g_fake_now{1'000'000'000};
std::int64_t fake_clock() noexcept { return g_fake_now.load(); }
void advance_ms(std::int64_t ms) { g_fake_now += ms * 1'000'000; }

Logger::Options fake_opts(std::uint32_t max_per_window) {
  Logger::Options o;
  o.min_level = Level::Debug;
  o.max_per_window = max_per_window;
  o.window_ms = 1000;
  o.clock = &fake_clock;
  return o;
}
}  // namespace

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
  CHECK(format_timestamped(Level::Warn, 1'000'000'000'456LL, "x") == "2001-09-09 01:46:40.456Z [WARN] x");
}

TEST_CASE("logger delivers formatted lines asynchronously and filters by level", "[log]") {
  auto sink = std::make_shared<CaptureSink>();
  CallSite site_a{"a.cpp", 1}, site_b{"b.cpp", 2};
  Logger::Options o = fake_opts(100);
  o.min_level = Level::Info;
  Logger logger(o, {sink});
  logger.log(site_a, Level::Info, "hello {} {}", 42, "world");
  logger.log(site_b, Level::Debug, "hidden");
  logger.log(site_b, Level::Error, "bad {:.1f}", 1.25);
  logger.flush();
  const auto lines = sink->lines();
  REQUIRE(lines.size() == 2);
  CHECK(lines[0] == "INFO hello 42 world");
  CHECK(lines[1] == "ERROR bad 1.2");
  logger.set_min_level(Level::Debug);
  logger.log(site_b, Level::Debug, "now visible");
  logger.flush();
  CHECK(sink->count("now visible") == 1);
}

TEST_CASE("per-sink minimum level (e.g. mirror only errors to X4Native)", "[log]") {
  auto all = std::make_shared<CaptureSink>(Level::Debug);
  auto errors = std::make_shared<CaptureSink>(Level::Error);
  CallSite site{"s.cpp", 1};
  Logger logger(fake_opts(100), {all, errors});
  logger.log(site, Level::Info, "info");
  logger.log(site, Level::Error, "err");
  logger.flush();
  CHECK(all->lines().size() == 2);
  REQUIRE(errors->lines().size() == 1);
  CHECK(errors->lines()[0] == "ERROR err");
}

TEST_CASE("long lines are truncated, not overflowed", "[log]") {
  auto sink = std::make_shared<CaptureSink>();
  CallSite site{"s.cpp", 1};
  Logger logger(fake_opts(100), {sink});
  const std::string big(1000, 'x');
  logger.log(site, Level::Info, "{}", big);
  logger.flush();
  REQUIRE(sink->lines().size() == 1);
  CHECK(sink->lines()[0].size() == std::string("INFO ").size() + kMaxLineChars);
}

TEST_CASE("rate limit: excess lines at one call site are suppressed and summarised", "[log][ratelimit]") {
  auto sink = std::make_shared<CaptureSink>();
  CallSite flood{"flood.cpp", 10}, other{"other.cpp", 20};
  Logger logger(fake_opts(3), {sink});

  for (int i = 0; i < 10; ++i) logger.log(flood, Level::Info, "flood {}", i);
  logger.log(other, Level::Info, "other line");  // independent site, unaffected
  logger.flush();
  CHECK(sink->count("flood ") == 3);       // only the first 3 got through
  CHECK(sink->count("other line") == 1);
  CHECK(sink->count("suppressed") == 0);   // window still open: no summary yet

  advance_ms(1500);  // the window rolls over on the next call
  logger.log(flood, Level::Info, "flood again");
  logger.flush();
  CHECK(sink->count("7 lines suppressed at flood.cpp:10") == 1);
  CHECK(sink->count("flood again") == 1);
}

TEST_CASE("rate limit: a site that goes quiet still gets its summary on flush", "[log][ratelimit]") {
  auto sink = std::make_shared<CaptureSink>();
  CallSite site{"quiet.cpp", 5};
  Logger logger(fake_opts(2), {sink});
  for (int i = 0; i < 6; ++i) logger.log(site, Level::Warn, "w{}", i);
  logger.flush();
  CHECK(sink->count("suppressed") == 0);
  advance_ms(2000);
  logger.flush();  // no further log call: the sweep reports the 4 swallowed lines
  CHECK(sink->count("4 lines suppressed at quiet.cpp:5") == 1);
  logger.flush();
  CHECK(sink->count("suppressed") == 1);  // reported exactly once
}

TEST_CASE("rate limit: macro call sites are keyed by file:line", "[log][ratelimit]") {
  auto sink = std::make_shared<CaptureSink>();
  Logger logger(fake_opts(2), {sink});
  for (int i = 0; i < 5; ++i) X4MP_LOG_TO(logger, Level::Info, "loop {}", i);  // one site
  X4MP_LOG_TO(logger, Level::Info, "separate site");                            // another site
  logger.flush();
  CHECK(sink->count("loop ") == 2);
  CHECK(sink->count("separate site") == 1);
}

TEST_CASE("global logger macros are no-ops without a logger and route when set", "[log]") {
  X4MP_LOGI("nobody listening {}", 1);  // must not crash
  auto sink = std::make_shared<CaptureSink>();
  {
    Logger logger(fake_opts(100), {sink});
    Logger::set_global(&logger);
    X4MP_LOGW("via global {}", 7);
    logger.flush();
  }  // destructor clears the global
  CHECK(Logger::global() == nullptr);
  CHECK(sink->count("WARN via global 7") == 1);
}

TEST_CASE("many threads logging: every line is delivered or counted as dropped", "[log][stress]") {
  auto sink = std::make_shared<CaptureSink>();
  constexpr int kThreads = 4, kPer = 3000;
  CallSite site{"mt.cpp", 1};
  Logger::Options o = fake_opts(1u << 30);
  o.queue_capacity = 512;  // small: force overflow handling
  Logger logger(o, {sink});
  std::vector<std::thread> ts;
  for (int t = 0; t < kThreads; ++t) {
    ts.emplace_back([&, t] {
      for (int i = 0; i < kPer; ++i) logger.log(site, Level::Info, "mt {} {}", t, i);
    });
  }
  for (auto& t : ts) t.join();
  logger.flush();
  const std::uint64_t delivered = sink->count("INFO mt ");
  CHECK(delivered + logger.dropped_lines() == static_cast<std::uint64_t>(kThreads) * kPer);
  CHECK(delivered > 0);
}

TEST_CASE("file sink appends timestamped lines and rotates", "[log]") {
  const auto dir = std::filesystem::temp_directory_path() /
                   ("x4mp_log_test_" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
  const auto path = dir / "sub" / "x4mp.log";
  {
    FileSink::Options fo;
    fo.max_bytes = 200;
    fo.keep = 3;
    auto sink = std::make_shared<FileSink>(path, fo);
    REQUIRE(sink->is_open());
    CallSite site{"f.cpp", 1};
    Logger logger(fake_opts(1000), {sink});
    for (int i = 0; i < 20; ++i) logger.log(site, Level::Info, "line number {}", i);
    logger.flush();
  }
  CHECK(std::filesystem::exists(path));
  CHECK(std::filesystem::exists(dir / "sub" / "x4mp.log.1"));
  std::ifstream in(path);
  std::string first;
  std::getline(in, first);
  CHECK(first.find("[INFO] line number") != std::string::npos);
  CHECK(first.find('Z') != std::string::npos);
  std::error_code ec;
  std::filesystem::remove_all(dir, ec);
}
