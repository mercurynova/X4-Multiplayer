// M2-12: launch.json parsing / expiry / redaction / consumption, the launch feature through a real ModHost, and the NodeStats math.
// SDK-free (x4mp_host_tests).

#include <cmath>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include "admin_generated.h"
#include "message_ids_generated.h"
#include "features/join/join_json.h"
#include "features/join/join_requests.h"
#include "features/launch/launch_feature.h"
#include "features/launch/launch_json.h"
#include "features/stats/budget_line.h"
#include "features/stats/frame_timing.h"
#include "features/stats/stats_collector.h"
#include "features/stats/stats_message.h"
#include "game/main_thread.h"
#include "game/safe_remove.h"
#include "host/mod_host.h"
#include "host_fakes.h"

using namespace x4mp;
using namespace x4mp::features;

namespace {
// 2026-10-03T12:00:00Z
constexpr std::int64_t kNow = 1791028800;

const char* kValid =
    R"({"server":"10.0.0.5:4000","name":"Pilot","password":"hunter2-secret","role":"authority","admin_password":"admin-secret-pw","expires_utc":"2026-10-03T12:05:00Z"})";
}  // namespace

TEST_CASE("launch: timestamp parsing", "[launch]") {
  std::int64_t t = 0;
  REQUIRE(launch::parse_utc_timestamp("1970-01-01T00:00:00Z", t));
  CHECK(t == 0);
  REQUIRE(launch::parse_utc_timestamp("2026-10-03T12:00:00Z", t));
  CHECK(t == kNow);
  REQUIRE(launch::parse_utc_timestamp("2026-10-03T12:00:00.250Z", t));
  CHECK(t == kNow);
  REQUIRE(launch::parse_utc_timestamp("2024-02-29T23:59:59Z", t));  // leap day
  CHECK(t == 1709251199);
  CHECK_FALSE(launch::parse_utc_timestamp("2025-02-29T00:00:00Z", t));
  CHECK_FALSE(launch::parse_utc_timestamp("2026-10-03 12:00:00Z", t));
  CHECK_FALSE(launch::parse_utc_timestamp("2026-10-03T12:00:00", t));        // no Z: local time is not accepted
  CHECK_FALSE(launch::parse_utc_timestamp("2026-10-03T12:00:00+02:00", t));
  CHECK_FALSE(launch::parse_utc_timestamp("2026-13-03T12:00:00Z", t));
  CHECK_FALSE(launch::parse_utc_timestamp("", t));
}

TEST_CASE("launch: a valid request parses and becomes the UI's x4mp.join payload", "[launch]") {
  const auto p = launch::parse_launch(kValid, kNow);
  REQUIRE(p.status == launch::LaunchStatus::Ok);
  CHECK(p.request.server == "10.0.0.5:4000");
  CHECK(p.request.name == "Pilot");
  CHECK(p.request.want_authority);
  REQUIRE(p.request.expires_unix.has_value());
  CHECK(*p.request.expires_unix == kNow + 300);

  // The payload goes through the very parser the Lua verb uses.
  std::string error;
  const auto join = join::parse_join(launch::to_join_payload(p.request), &error);
  REQUIRE(join.has_value());
  CHECK(join->host == "10.0.0.5");
  CHECK(join->port == 4000);
  CHECK(join->name == "Pilot");
  CHECK(join->password == "hunter2-secret");
  CHECK(join->admin_password == "admin-secret-pw");
  CHECK(join->want_authority);
}

TEST_CASE("launch: minimal request and client role", "[launch]") {
  const auto p = launch::parse_launch(R"({"server":"example.org","name":"A"})", kNow);
  REQUIRE(p.status == launch::LaunchStatus::Ok);
  CHECK_FALSE(p.request.want_authority);
  CHECK_FALSE(p.request.expires_unix.has_value());
  const auto join = join::parse_join(launch::to_join_payload(p.request));
  REQUIRE(join.has_value());
  CHECK(join->port == 47780);
  CHECK(join->password.empty());
  const auto c = launch::parse_launch(R"({"server":"example.org","name":"A","role":"Client"})", kNow);
  CHECK(c.status == launch::LaunchStatus::Ok);
}

TEST_CASE("launch: expiry", "[launch]") {
  const std::string stale = R"({"server":"h","name":"A","password":"pw-secret","expires_utc":"2026-10-03T11:59:59Z"})";
  const auto p = launch::parse_launch(stale, kNow);
  CHECK(p.status == launch::LaunchStatus::Expired);
  CHECK(p.request.password.empty());  // an expired request keeps no secrets
  CHECK(launch::parse_launch(stale, kNow - 10).status == launch::LaunchStatus::Ok);
  // exactly at the expiry second is still valid
  CHECK(launch::parse_launch(R"({"server":"h","name":"A","expires_utc":"2026-10-03T12:00:00Z"})", kNow).status == launch::LaunchStatus::Ok);
}

TEST_CASE("launch: invalid requests", "[launch]") {
  const auto code = [](const char* text) { return launch::parse_launch(text, kNow).error; };
  CHECK(code("not json") == "bad_json");
  CHECK(code("[1,2]") == "bad_json");
  CHECK(code(R"({"name":"A"})") == "no_server");
  CHECK(code(R"({"server":"h"})") == "no_name");
  CHECK(code(R"({"server":"h","name":"A","role":"admin"})") == "bad_role");
  CHECK(code(R"({"server":"h","name":"A","expires_utc":"tomorrow"})") == "bad_expires");
  CHECK(code(R"({"server":5,"name":"A"})") == "bad_type");
  for (const char* t : {"not json", R"({"name":"A"})", R"({"server":"h","name":"A","expires_utc":"x"})"}) {
    CHECK(launch::parse_launch(t, kNow).status == launch::LaunchStatus::Invalid);
  }
}

TEST_CASE("launch: nothing that describes or reports a request contains a secret", "[launch][redact]") {
  const auto p = launch::parse_launch(kValid, kNow);
  const std::string line = launch::describe(p.request);
  CHECK(line.find("hunter2-secret") == std::string::npos);
  CHECK(line.find("admin-secret-pw") == std::string::npos);
  CHECK(line.find("<redacted>") != std::string::npos);
  CHECK(line.find("Pilot") != std::string::npos);
  // parse errors never echo a value either
  const auto bad = launch::parse_launch(R"({"server":"h","name":"A","password":"leaky-secret","expires_utc":"nope"})", kNow);
  CHECK(bad.error.find("leaky-secret") == std::string::npos);
  CHECK(bad.request.password.empty());
  auto copy = p.request;
  launch::wipe(copy);
  CHECK(copy.password.empty());
  CHECK(copy.admin_password.empty());
}

TEST_CASE("launch: consume_launch_file deletes the file in every case", "[launch]") {
  test::TempDir dir;
  const auto file = dir.path / "launch.json";
  const auto now = std::int64_t{kNow};

  CHECK_FALSE(launch::consume_launch_file(file, now).existed);

  dir.write("launch.json", kValid);
  auto ok = launch::consume_launch_file(file, now);
  CHECK(ok.existed);
  CHECK(ok.removed);
  CHECK(ok.parse.status == launch::LaunchStatus::Ok);
  CHECK_FALSE(std::filesystem::exists(file));

  dir.write("launch.json", R"({"server":"h","name":"A","expires_utc":"2020-01-01T00:00:00Z"})");
  auto expired = launch::consume_launch_file(file, now);
  CHECK(expired.parse.status == launch::LaunchStatus::Expired);
  CHECK_FALSE(std::filesystem::exists(file));

  dir.write("launch.json", "{ broken");
  auto invalid = launch::consume_launch_file(file, now);
  CHECK(invalid.parse.status == launch::LaunchStatus::Invalid);
  CHECK_FALSE(std::filesystem::exists(file));
  CHECK_FALSE(launch::consume_launch_file(file, now).existed);  // second read: gone
}

namespace {
const char* g_suffix = "611726";
x4mp::game::GameVersionPod g_version{9, 0};
x4mp::game::GameVersionPod fk_version() { return g_version; }
const char* fk_suffix() { return g_suffix; }

struct HostFixture {
  test::TempDir dir;
  test::FakePlatform platform{dir.path};
  HostFixture() {
    platform.functions["GetGameVersion"] = reinterpret_cast<void*>(&fk_version);
    platform.functions["GetBuildVersionSuffix"] = reinterpret_cast<void*>(&fk_suffix);
    x4mp::game::main_thread().reset();
    join::clear_join_payloads();
  }
  ~HostFixture() {
    x4mp::game::main_thread().reset();
    x4mp::game::set_player_guard({});
    x4mp::game::set_remove_backend(nullptr);
    join::clear_join_payloads();
  }
  static host::HostOptions options() {
    host::HostOptions o;
    o.register_features = [](host::FeatureRegistry& r) { r.add(std::make_unique<LaunchFeature>()); };
    return o;
  }
};
}  // namespace

TEST_CASE("launch feature: a valid file is consumed at init and queued as a join, once", "[launch][host]") {
  HostFixture f;
  f.dir.write("launch.json", R"({"server":"127.0.0.1:47971","name":"Alice","password":"pw-secret-1"})");
  {
    host::ModHost h(f.platform, HostFixture::options());
    REQUIRE(h.init() == host::InitResult::Started);
    CHECK_FALSE(std::filesystem::exists(f.dir.path / "launch.json"));
    const auto queued = join::take_join_payloads();
    REQUIRE(queued.size() == 1);
    const auto join = join::parse_join(queued[0]);
    REQUIRE(join.has_value());
    CHECK(join->port == 47971);
    CHECK(join->name == "Alice");
    h.shutdown();
  }
  // The log never holds the password.
  std::string log;
  for (const auto& e : std::filesystem::recursive_directory_iterator(f.dir.path)) {
    if (e.path().extension() == ".log") {
      std::ifstream in(e.path());
      log.assign(std::istreambuf_iterator<char>(in), {});
    }
  }
  CHECK(log.find("launch.json consumed") != std::string::npos);
  CHECK(log.find("pw-secret-1") == std::string::npos);

  // A second init (a reload) finds no file: nothing is queued again.
  host::ModHost again(f.platform, HostFixture::options());
  REQUIRE(again.init() == host::InitResult::Started);
  CHECK(join::take_join_payloads().empty());
  again.shutdown();
}

TEST_CASE("launch feature: an expired or invalid file is ignored and deleted", "[launch][host]") {
  HostFixture f;
  f.dir.write("launch.json", R"({"server":"127.0.0.1:47971","name":"Alice","expires_utc":"2020-01-01T00:00:00Z"})");
  host::ModHost h(f.platform, HostFixture::options());
  REQUIRE(h.init() == host::InitResult::Started);
  CHECK(join::take_join_payloads().empty());
  CHECK_FALSE(std::filesystem::exists(f.dir.path / "launch.json"));
  h.shutdown();

  f.dir.write("launch.json", "{ nope");
  host::ModHost h2(f.platform, HostFixture::options());
  REQUIRE(h2.init() == host::InitResult::Started);
  CHECK(join::take_join_payloads().empty());
  CHECK_FALSE(std::filesystem::exists(f.dir.path / "launch.json"));
  h2.shutdown();
}

TEST_CASE("launch feature: a request seen during a reload is deleted but does not start a join", "[launch][host]") {
  HostFixture f;
  {
    host::ModHost first(f.platform, HostFixture::options());
    REQUIRE(first.init() == host::InitResult::Started);
    first.shutdown();  // writes the host stash: the next init is a reload
  }
  f.dir.write("launch.json", R"({"server":"127.0.0.1:47971","name":"Alice"})");
  host::ModHost second(f.platform, HostFixture::options());
  REQUIRE(second.init() == host::InitResult::Started);
  CHECK_FALSE(std::filesystem::exists(f.dir.path / "launch.json"));
  CHECK(join::take_join_payloads().empty());
  second.shutdown();
}

// ---------------------------------------------------------------------------------------------------------------------
// stats
// ---------------------------------------------------------------------------------------------------------------------
TEST_CASE("stats: nearest-rank percentiles", "[stats]") {
  using stats::percentile;
  CHECK(percentile({}, 0.95) == 0.0);
  CHECK(percentile({7.0}, 0.95) == 7.0);
  std::vector<double> v;
  for (int i = 1; i <= 100; ++i) v.push_back(static_cast<double>(101 - i));  // shuffled order: 100..1
  CHECK(percentile(v, 0.95) == 95.0);
  CHECK(percentile(v, 0.50) == 50.0);
  CHECK(percentile(v, 1.0) == 100.0);
  CHECK(percentile(v, 0.0) == 1.0);
  CHECK(percentile({1.0, 2.0, 3.0, 4.0}, 0.95) == 4.0);
  CHECK(percentile({10.0, 20.0}, 0.5) == 10.0);
}

TEST_CASE("stats: a window reports fps, frame p95 and the mod cost p95, then restarts", "[stats]") {
  stats::StatsCollector c;
  // 99 frames of 10 ms and one 100 ms spike: fps over 1.09 s, frame p95 = the 10 ms rank (95th of 100)
  for (int i = 0; i < 99; ++i) c.on_frame(0.010, 0.2);
  c.on_frame(0.100, 5.0);
  CHECK(c.window_seconds() == Catch::Approx(1.09));
  const auto w = c.take_window();
  CHECK(w.frames == 100);
  CHECK(w.fps == Catch::Approx(100.0 / 1.09).epsilon(0.001));
  CHECK(w.frame_ms_p95 == Catch::Approx(10.0));
  CHECK(w.mod_ms_p95 == Catch::Approx(0.2));
  CHECK(w.mod_ms_max == Catch::Approx(5.0));
  CHECK(c.window_seconds() == 0.0);
  const auto empty = c.take_window();
  CHECK(empty.frames == 0);
  CHECK(empty.fps == 0.0f);
  c.on_frame(-1.0, 1.0);          // garbage is ignored
  c.on_frame(std::nan(""), 1.0);
  CHECK(c.take_window().frames == 0);
}

TEST_CASE("stats: byte rates", "[stats]") {
  CHECK(stats::rate_per_s(1000, 5000, 2.0) == 2000u);
  CHECK(stats::rate_per_s(5000, 1000, 2.0) == 0u);  // counter restarted (new connection)
  CHECK(stats::rate_per_s(0, 100, 0.0) == 0u);
  CHECK(stats::rate_per_s(0, 100, 3.0) == 33u);
}

TEST_CASE("stats: the NodeStats payload decodes with the appended field", "[stats]") {
  stats::NodeStatsSample s;
  s.fps = 58.5f;
  s.frame_ms_p95 = 21.0f;
  s.game_time = 1234.5;
  s.rtt_ms = 12.25f;
  s.rx_bytes_per_s = 4096;
  s.tx_bytes_per_s = 512;
  s.net_main_ms_p95 = 0.75f;
  s.tcp_send_queue_bytes = 99;
  const auto bytes = stats::encode_node_stats(s);
  flatbuffers::Verifier v(bytes.data(), bytes.size());
  REQUIRE(v.VerifyBuffer<X4MP::Proto::NodeStats>(nullptr));
  const auto* m = flatbuffers::GetRoot<X4MP::Proto::NodeStats>(bytes.data());
  CHECK(m->fps() == 58.5f);
  CHECK(m->frame_ms_p95() == 21.0f);
  CHECK(m->game_time() == 1234.5);
  CHECK(m->rtt_ms() == 12.25f);
  CHECK(m->rx_bytes_per_s() == 4096u);
  CHECK(m->tx_bytes_per_s() == 512u);
  CHECK(m->net_main_ms_p95() == 0.75f);
  CHECK(m->tcp_send_queue_bytes() == 99u);
  CHECK(stats::msg_node_stats() == static_cast<std::uint16_t>(X4MP::Proto::MsgType::NodeStats));
}

// ---------------------------------------------------------------------------------------------------------------------
// M4-30: the 30 s budget line
// ---------------------------------------------------------------------------------------------------------------------
TEST_CASE("M4-30: a timing slot reports p50/p95/max and starts a new window", "[stats][m430]") {
  stats::TimingHub hub;
  CHECK(hub.take("authority").samples == 0);  // a slot nobody wrote is empty, not a crash
  stats::FrameTiming& t = hub.slot("authority");
  for (int i = 1; i <= 100; ++i) t.add(static_cast<double>(i) / 100.0);  // 0.01 .. 1.00 ms
  CHECK(t.size() == 100);
  const auto s = hub.take("authority");
  CHECK(s.samples == 100);
  CHECK(s.ms_p50 == Catch::Approx(0.50));
  CHECK(s.ms_p95 == Catch::Approx(0.95));
  CHECK(s.ms_max == Catch::Approx(1.00));
  CHECK(t.size() == 0);                       // the take reset the window
  CHECK(hub.take("authority").samples == 0);
  t.add(-1.0);                                // garbage is ignored, not folded into the max
  t.add(std::nan(""));
  CHECK(hub.take("authority").samples == 0);
}

TEST_CASE("M4-30: a full timing window overwrites instead of growing without bound", "[stats][m430]") {
  stats::FrameTiming t;
  for (std::size_t i = 0; i < stats::FrameTiming::kMaxSamples + 500; ++i) t.add(0.5);
  CHECK(t.size() == stats::FrameTiming::kMaxSamples);
  const auto s = t.take();
  CHECK(s.samples == stats::FrameTiming::kMaxSamples);
  CHECK(s.ms_p95 == Catch::Approx(0.5));
  CHECK(s.ms_max == Catch::Approx(0.5));
}

TEST_CASE("M4-30: the collector reports the p50 the budget line quotes", "[stats][m430]") {
  stats::StatsCollector c;
  for (int i = 0; i < 10; ++i) c.on_frame(0.010, 0.1 * (i + 1));  // mod cost 0.1 .. 1.0 ms, frame 10 ms
  const auto w = c.take_window();
  CHECK(w.frame_ms_p50 == Catch::Approx(10.0));
  CHECK(w.mod_ms_p50 == Catch::Approx(0.5));
  CHECK(w.mod_ms_p95 == Catch::Approx(1.0));
}

TEST_CASE("M4-30: the budget line names every acceptance field in a fixed order", "[stats][m430]") {
  stats::BudgetSample s;
  s.fps = 47.5f;
  s.frame_ms_p50 = 18.0f;
  s.frame_ms_p95 = 26.5f;
  s.mod_ms_p50 = 0.4f;
  s.mod_ms_p95 = 1.1f;
  s.mod_ms_max = 2.5f;
  s.authority_ms_p50 = 1.2;
  s.authority_ms_p95 = 2.4;
  s.authority_ms_max = 2.9;
  s.clients = 3;
  s.ghosts = 42;
  s.suppressed = 17;
  s.rx_bytes_per_s = 150000;
  s.tx_bytes_per_s = 450000;
  s.per_client_bytes_per_s = stats::per_client_rate(s.tx_bytes_per_s, s.clients);
  s.udp_state = "active";
  const std::string line = stats::format_budget(s);
  CHECK(line.starts_with("budget: fps=47.5 "));
  CHECK(line.find("frame_ms=18.00/26.50 ") != std::string::npos);
  CHECK(line.find("mod_ms=0.400/1.100/2.500 ") != std::string::npos);
  CHECK(line.find("authority_ms=1.200/2.400/2.900 ") != std::string::npos);
  CHECK(line.find("clients=3 ghosts=42 suppressed=17 ") != std::string::npos);
  CHECK(line.find("rx=150000B/s tx=450000B/s per_client=150000B/s udp=active") != std::string::npos);
  // a node with nobody else in the session has no per-client cost, and an unset lane is "off", never an empty field
  stats::BudgetSample idle;
  CHECK(stats::per_client_rate(450000, 0) == 0u);
  CHECK(stats::format_budget(idle).find("per_client=0B/s udp=off") != std::string::npos);
  idle.udp_state = nullptr;  // defensive: a null literal must not produce "udp="
  CHECK(stats::format_budget(idle).find("udp=off") != std::string::npos);
  // M5-03: the `hooks=` block is the last field, and an unset one still says the MD half is dead rather than printing nothing
  CHECK(line.find("udp=active hooks=") != std::string::npos);
  CHECK(stats::format_budget(idle).find("hooks=dead md=0 fired=0 age=none fb=0 dup=0 queued=0") != std::string::npos);
  idle.hooks = "hooks=alive md=9 fired=41 age=1.2s fb=2 dup=0 queued=0";
  CHECK(stats::format_budget(idle).find("hooks=alive md=9 fired=41") != std::string::npos);
}
