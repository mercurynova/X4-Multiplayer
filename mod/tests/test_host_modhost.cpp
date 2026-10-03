#include <memory>
#include <stdexcept>
#include <string>

#include <catch2/catch_test_macros.hpp>

#include "game/main_thread.h"
#include "game/safe_remove.h"
#include "host/mod_host.h"
#include "host_fakes.h"

using namespace x4mp::host;
using x4mp::game::GameVersionPod;
using x4mp::game::UniverseId;

namespace {
// ---- fake game exports (the ModHost resolves them through FakePlatform::functions) ----
const char* g_suffix = "611726";
GameVersionPod g_version{9, 0};
UniverseId g_obj = 0;
bool g_paused = false;
GameVersionPod fk_version() { return g_version; }
const char* fk_suffix() { return g_suffix; }
UniverseId fk_object() { return g_obj; }
double fk_time() { return 42.0; }
bool fk_paused() { return g_paused; }

void install_game(x4mp::test::FakePlatform& p) {
  p.functions["GetGameVersion"] = reinterpret_cast<void*>(&fk_version);
  p.functions["GetBuildVersionSuffix"] = reinterpret_cast<void*>(&fk_suffix);
  p.functions["GetPlayerObjectID"] = reinterpret_cast<void*>(&fk_object);
  p.functions["GetCurrentGameTime"] = reinterpret_cast<void*>(&fk_time);
  p.functions["IsGamePaused"] = reinterpret_cast<void*>(&fk_paused);
}

struct Probe {
  int init = 0, frames = 0, universe = 0, loaded = 0, shutdown = 0, config = 0;
  bool saw_universe_in_frame = false;
  bool last_universe_ready = false;
  std::optional<double> last_game_time;
  bool last_paused = false;
  bool throw_in_frame = false;
};

class ProbeFeature final : public IFeature {
 public:
  explicit ProbeFeature(Probe& p) : p_(p) {}
  std::string_view name() const noexcept override { return "probe"; }
  void on_init(HostContext&) override { ++p_.init; }
  void on_frame(HostContext& ctx, const FrameInfo& fi) override {
    ++p_.frames;
    p_.last_universe_ready = fi.universe_ready;
    p_.last_game_time = fi.game_time;
    p_.last_paused = fi.game_paused;
    if (ctx.gates.universe_ready) p_.saw_universe_in_frame = true;
    if (p_.throw_in_frame) throw std::runtime_error("boom");
  }
  void on_universe_ready(HostContext&) override { ++p_.universe; }
  void on_game_loaded(HostContext&) override { ++p_.loaded; }
  void on_config_changed(HostContext&) override { ++p_.config; }
  void on_shutdown(HostContext&) override { ++p_.shutdown; }

 private:
  Probe& p_;
};

HostOptions with_probe(Probe& p) {
  HostOptions o;
  o.register_features = [&p](FeatureRegistry& r) { r.add(std::make_unique<ProbeFeature>(p)); };
  return o;
}

struct Fixture {
  x4mp::test::TempDir dir;
  x4mp::test::FakePlatform platform{dir.path};
  Fixture() {
    install_game(platform);
    g_suffix = "611726";
    g_version = {9, 0};
    g_obj = 0;
    g_paused = false;
    x4mp::game::main_thread().reset();
    x4mp::game::set_player_guard({});
  }
  ~Fixture() {
    x4mp::game::main_thread().reset();
    x4mp::game::set_player_guard({});
    x4mp::game::set_remove_backend(nullptr);
  }
};
}  // namespace

TEST_CASE("host starts on the pinned build and drives the gates", "[host][modhost]") {
  Fixture f;
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);
  CHECK(host.running());
  CHECK(host.build_check().status == BuildStatus::Supported);
  CHECK(probe.init == 1);
  CHECK(f.platform.native_lines.size() >= 1);  // banner reached the X4Native log

  // Start menu frames: no universe, no game calls.
  host.on_frame();
  host.on_frame();
  CHECK(probe.frames == 2);
  CHECK_FALSE(probe.last_universe_ready);
  CHECK_FALSE(probe.last_game_time);
  CHECK_FALSE(host.gates().game_loaded);

  host.on_game_loaded();
  CHECK(probe.loaded == 1);
  CHECK(host.gates().game_loaded);
  CHECK_FALSE(host.gates().universe_ready);
  g_paused = true;
  host.on_frame();
  CHECK(probe.last_game_time == 42.0);
  CHECK(probe.last_paused);
  CHECK_FALSE(probe.last_universe_ready);

  host.on_universe_ready();
  CHECK(probe.universe == 1);
  CHECK(host.gates().universe_ready);
  CHECK(host.gates().universe_epoch == 1);
  host.on_frame();
  CHECK(probe.last_universe_ready);
  CHECK(probe.saw_universe_in_frame);

  // A new load clears the universe gate again.
  host.on_game_loaded();
  CHECK_FALSE(host.gates().universe_ready);
  host.on_universe_ready();
  CHECK(host.gates().universe_epoch == 2);

  host.shutdown();
  CHECK(probe.shutdown == 1);
  CHECK_FALSE(host.running());
  host.on_frame();  // inert after shutdown
  CHECK(probe.frames == 4);
}

TEST_CASE("an unsupported build is refused with a logged reason and stays inert", "[host][modhost]") {
  Fixture f;
  g_suffix = "611727";
  Probe probe;
  {
    ModHost host(f.platform, with_probe(probe));
    CHECK(host.init() == InitResult::Refused);
    CHECK(host.refused());
    CHECK_FALSE(host.running());
    CHECK(host.refusal_reason().find("Unsupported X4 build") != std::string::npos);
    host.on_frame();
    host.on_game_loaded();
    host.on_universe_ready();
    CHECK(probe.init == 0);
    CHECK(probe.frames == 0);
    CHECK(probe.universe == 0);
  }
  const std::string log = f.dir.read("logs/x4mp.log");
  CHECK(log.find("REFUSING TO START") != std::string::npos);
  CHECK(log.find("611727") != std::string::npos);
  // The reason also reached the X4Native log (ERROR mirror).
  bool mirrored = false;
  for (const auto& l : f.platform.native_lines) mirrored = mirrored || l.find("REFUSING TO START") != std::string::npos;
  CHECK(mirrored);
}

TEST_CASE("a different game version is refused too", "[host][modhost]") {
  Fixture f;
  g_version = {9, 10};
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  CHECK(host.init() == InitResult::Refused);
  CHECK(probe.init == 0);
}

TEST_CASE("an unreadable build number starts with a warning", "[host][modhost]") {
  Fixture f;
  f.platform.functions.erase("GetBuildVersionSuffix");
  Probe probe;
  {
    ModHost host(f.platform, with_probe(probe));
    CHECK(host.init() == InitResult::Started);
    CHECK(host.build_check().status == BuildStatus::Unverified);
  }
  const std::string log = f.dir.read("logs/x4mp.log");
  CHECK(log.find("build not verified") != std::string::npos);
}

TEST_CASE("a feature that throws is contained and disabled after 3 frames", "[host][modhost]") {
  Fixture f;
  Probe probe;
  probe.throw_in_frame = true;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);
  for (int i = 0; i < 10; ++i) host.on_frame();
  CHECK(probe.frames == 3);
  CHECK(host.features().is_disabled("probe"));
  CHECK(host.running());  // the host itself keeps going
}

TEST_CASE("config comes from x4mp.json; launch.json is left for the launch feature", "[host][modhost][config]") {
  Fixture f;
  f.dir.write("x4mp.json", R"({"server_host":"example.org","frame_budget_us":900,"log_level":"debug","log_categories":{"net":"warn"}})");
  f.dir.write("launch.json", R"({"server":"10.0.0.5:4000","name":"Pilot"})");
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);
  // M2-12: the host no longer reads launch.json (features/launch consumes it and starts the join); x4mp.json alone feeds the config.
  CHECK(host.config().server_host == "example.org");
  CHECK(host.config().player_name.empty());
  CHECK(host.config().frame_budget_us == 900);
  CHECK(host.budget().budget_ns() == 900'000);
  CHECK_FALSE(host.config().launch.active);
  CHECK(host.log()->category_level(Cat::Net) == Level::Warn);
  CHECK(host.log()->category_level(Cat::Sess) == Level::Debug);
  CHECK(std::filesystem::exists(f.dir.path / "launch.json"));  // untouched by the host
  CHECK(host.paths().portable);
}

TEST_CASE("on_ui_reload re-reads x4mp.json", "[host][modhost][config]") {
  Fixture f;
  f.dir.write("x4mp.json", R"({"frame_budget_us":1000})");
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);
  CHECK(host.budget().budget_ns() == 1'000'000);

  f.dir.write("x4mp.json", R"({"frame_budget_us":2500,"player_name":"Renamed"})");
  host.on_ui_reload();
  CHECK(probe.config == 1);
  CHECK(host.config().player_name == "Renamed");
  CHECK(host.budget().budget_ns() == 2'500'000);

  // A broken file keeps defaults for the bad key and does not take the host down.
  f.dir.write("x4mp.json", R"({"frame_budget_us":"fast"})");
  host.on_ui_reload();
  CHECK(host.running());
  CHECK(host.config().frame_budget_us == 1500);
}

TEST_CASE("the password is never logged, even in the config dump", "[host][modhost][redact]") {
  Fixture f;
  f.dir.write("x4mp.json", R"({"password":"correct-horse-battery","player_name":"Pilot","log_level":"debug"})");
  {
    Probe probe;
    ModHost host(f.platform, with_probe(probe));
    REQUIRE(host.init() == InitResult::Started);
    X4MP_CLOG(*host.log(), Cat::Auth, Level::Info, "debug: using {}", host.config().password);
    host.on_ui_reload();
    host.shutdown();
  }
  const std::string log = f.dir.read("logs/x4mp.log");
  REQUIRE_FALSE(log.empty());
  CHECK(log.find("correct-horse-battery") == std::string::npos);
  CHECK(log.find("password=<redacted>") != std::string::npos);
  CHECK(log.find("player_name=Pilot") != std::string::npos);
  for (const auto& l : f.platform.native_lines) CHECK(l.find("correct-horse-battery") == std::string::npos);
}

TEST_CASE("PlayerGuard is refreshed each frame once the universe is ready and SafeRemove honours it", "[host][modhost][guard]") {
  Fixture f;
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);

  g_obj = 5001;
  host.on_frame();  // universe not ready: no guard yet
  CHECK_FALSE(x4mp::game::is_player_guarded(5001));

  host.on_game_loaded();
  host.on_universe_ready();
  CHECK(x4mp::game::is_player_guarded(5001));
  CHECK(x4mp::game::safe_remove(5001) == x4mp::game::RemoveResult::BlockedPlayerGuard);
  CHECK(host.guard_id_count() >= 1);

  g_obj = 5002;
  host.on_frame();  // refreshed every frame
  CHECK(x4mp::game::is_player_guarded(5002));
  CHECK_FALSE(x4mp::game::is_player_guarded(5001));
}

TEST_CASE("missing game exports do not stop the host", "[host][modhost]") {
  Fixture f;
  f.platform.functions.clear();
  f.platform.functions["GetGameVersion"] = reinterpret_cast<void*>(&fk_version);
  f.platform.functions["GetBuildVersionSuffix"] = reinterpret_cast<void*>(&fk_suffix);
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);
  host.on_game_loaded();
  host.on_universe_ready();
  host.on_frame();
  CHECK(probe.frames == 1);
  CHECK_FALSE(probe.last_game_time);
}

TEST_CASE("reload = shutdown + init again keeps the stash and reports the previous run", "[host][modhost][reload]") {
  Fixture f;
  Probe p1, p2;
  {
    ModHost host(f.platform, with_probe(p1));
    REQUIRE(host.init() == InitResult::Started);
    host.on_game_loaded();
    host.on_universe_ready();
    host.on_frame();
    host.shutdown();
  }
  REQUIRE(f.platform.stash.count("host.state") == 1);

  struct Seen {
    bool present = false;
    std::uint32_t reloads = 99;
    bool universe = false;
  } seen;
  HostOptions o;
  o.register_features = [&](FeatureRegistry& r) {
    struct Peek final : IFeature {
      Seen& s;
      explicit Peek(Seen& s_) : s(s_) {}
      std::string_view name() const noexcept override { return "peek"; }
      void on_init(HostContext& c) override {
        s.present = c.previous.present;
        s.reloads = c.previous.reload_count;
        s.universe = c.previous.universe_ready_at_shutdown;
      }
    };
    r.add(std::make_unique<Peek>(seen));
  };
  {
    ModHost host(f.platform, o);
    REQUIRE(host.init() == InitResult::Started);
    CHECK(seen.present);
    CHECK(seen.reloads == 1);
    CHECK(seen.universe);
    // Gates are NOT restored by the host (M2-07 decides).
    CHECK_FALSE(host.gates().universe_ready);
    host.shutdown();
  }
  // Third incarnation counts 2.
  Seen seen3;
  HostOptions o3;
  o3.register_features = [&](FeatureRegistry& r) {
    struct Peek final : IFeature {
      Seen& s;
      explicit Peek(Seen& s_) : s(s_) {}
      std::string_view name() const noexcept override { return "peek3"; }
      void on_init(HostContext& c) override { s.reloads = c.previous.reload_count; }
    };
    r.add(std::make_unique<Peek>(seen3));
  };
  ModHost host3(f.platform, o3);
  REQUIRE(host3.init() == InitResult::Started);
  CHECK(seen3.reloads == 2);
}

TEST_CASE("a corrupt stash entry is ignored", "[host][modhost][reload]") {
  Fixture f;
  f.platform.stash["host.state"] = {1, 2, 3};
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  CHECK(host.init() == InitResult::Started);
}

TEST_CASE("frame loop measures the budget", "[host][modhost][budget]") {
  Fixture f;
  Probe probe;
  ModHost host(f.platform, with_probe(probe));
  REQUIRE(host.init() == InitResult::Started);
  for (int i = 0; i < 50; ++i) host.on_frame();
  CHECK(host.frame_count() == 50);
  const auto s = host.budget().stats();
  CHECK(s.frames == 50);
  CHECK(s.over_budget == 0);  // an empty probe feature is far below 1.5 ms
}

TEST_CASE("default registry is empty and valid", "[host][modhost]") {
  Fixture f;
  ModHost host(f.platform);  // register_builtin_features
  CHECK(host.init() == InitResult::Started);
  host.on_frame();
  CHECK(host.features().disabled_count() == 0);
}
