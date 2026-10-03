#include <algorithm>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "host/feature.h"
#include "host_fakes.h"

using namespace x4mp::host;

namespace {
class MemSink final : public x4mp::log::Sink {
 public:
  void write(x4mp::log::Level, std::int64_t, std::string_view text) override {
    const std::lock_guard lock(m_);
    lines_.emplace_back(text);
  }
  [[nodiscard]] bool has(std::string_view needle) const {
    const std::lock_guard lock(m_);
    return std::ranges::any_of(lines_, [&](const std::string& l) { return l.find(needle) != std::string::npos; });
  }

 private:
  mutable std::mutex m_;
  std::vector<std::string> lines_;
};

// Everything a HostContext needs, with an in-memory log.
struct Rig {
  x4mp::test::TempDir dir;
  x4mp::test::FakePlatform platform{dir.path};
  x4mp::config::Config config;
  x4mp::game::GameApi game;
  std::shared_ptr<MemSink> sink = std::make_shared<MemSink>();
  HostLog log;
  FrameBudget budget{1500};
  Gates gates;
  PreviousRun previous;
  HostContext ctx;

  Rig()
      : log([this] {
          HostLog::Options o;
          o.extra_sinks.push_back(sink);
          return o;
        }()),
        ctx{config, game, log, platform, budget, gates, previous, ""} {}
};

struct Counters {
  int init = 0, frame = 0, universe = 0, loaded = 0, shutdown = 0, config = 0;
};

class ScriptedFeature final : public IFeature {
 public:
  ScriptedFeature(std::string name, Counters& c) : name_(std::move(name)), c_(c) {}
  std::string_view name() const noexcept override { return name_; }
  void on_init(HostContext&) override {
    ++c_.init;
    if (throw_init) throw std::runtime_error("init boom");
  }
  void on_frame(HostContext&, const FrameInfo&) override {
    ++c_.frame;
    if (throw_frame) throw std::runtime_error("frame boom");
    if (throw_nonstd) throw 42;
  }
  void on_universe_ready(HostContext&) override {
    ++c_.universe;
    if (throw_universe) throw std::logic_error("universe boom");
  }
  void on_game_loaded(HostContext&) override { ++c_.loaded; }
  void on_config_changed(HostContext&) override { ++c_.config; }
  void on_shutdown(HostContext&) override {
    ++c_.shutdown;
    if (throw_shutdown) throw std::runtime_error("shutdown boom");
  }

  bool throw_init = false, throw_frame = false, throw_nonstd = false, throw_universe = false, throw_shutdown = false;

 private:
  std::string name_;
  Counters& c_;
};
}  // namespace

TEST_CASE("registry drives every hook in registration order", "[host][registry]") {
  Rig rig;
  Counters a, b;
  FeatureRegistry reg;
  reg.add(std::make_unique<ScriptedFeature>("a", a));
  reg.add(std::make_unique<ScriptedFeature>("b", b));
  REQUIRE(reg.size() == 2);

  reg.init_all(rig.ctx);
  reg.game_loaded_all(rig.ctx);
  reg.universe_ready_all(rig.ctx);
  reg.frame_all(rig.ctx, FrameInfo{});
  reg.frame_all(rig.ctx, FrameInfo{});
  reg.config_changed_all(rig.ctx);
  reg.shutdown_all(rig.ctx);

  for (const Counters* c : {&a, &b}) {
    CHECK(c->init == 1);
    CHECK(c->loaded == 1);
    CHECK(c->universe == 1);
    CHECK(c->frame == 2);
    CHECK(c->config == 1);
    CHECK(c->shutdown == 1);
  }
  CHECK(reg.disabled_count() == 0);
  const auto st = reg.stats();
  REQUIRE(st.size() == 2);
  CHECK(st[0].name == "a");
  CHECK(st[0].frame_calls == 2);
}

TEST_CASE("a feature is disabled after 3 throws; others keep running", "[host][registry]") {
  Rig rig;
  Counters bad_c, good_c;
  auto bad = std::make_unique<ScriptedFeature>("bad", bad_c);
  bad->throw_frame = true;
  FeatureRegistry reg;
  reg.add(std::move(bad));
  reg.add(std::make_unique<ScriptedFeature>("good", good_c));
  reg.init_all(rig.ctx);

  reg.frame_all(rig.ctx, FrameInfo{});
  reg.frame_all(rig.ctx, FrameInfo{});
  CHECK_FALSE(reg.is_disabled("bad"));  // 2 throws: still active
  reg.frame_all(rig.ctx, FrameInfo{});  // third throw
  CHECK(reg.is_disabled("bad"));
  CHECK(bad_c.frame == 3);

  for (int i = 0; i < 10; ++i) reg.frame_all(rig.ctx, FrameInfo{});
  CHECK(bad_c.frame == 3);    // never called again
  CHECK(good_c.frame == 13);  // unaffected
  CHECK(reg.disabled_count() == 1);

  rig.log.flush();
  CHECK(rig.sink->has("DISABLED"));
  CHECK(rig.sink->has("frame boom"));

  const auto st = reg.stats();
  CHECK(st[0].throws == 3);
  CHECK(st[0].state == FeatureState::Disabled);
  CHECK(st[1].throws == 0);

  // A disabled feature still gets a best-effort on_shutdown.
  reg.shutdown_all(rig.ctx);
  CHECK(bad_c.shutdown == 1);
}

TEST_CASE("throws are counted across hooks, including non-std exceptions", "[host][registry]") {
  Rig rig;
  Counters c;
  auto f = std::make_unique<ScriptedFeature>("mixed", c);
  f->throw_universe = true;
  f->throw_nonstd = true;
  FeatureRegistry reg;
  reg.add(std::move(f));
  reg.init_all(rig.ctx);

  reg.universe_ready_all(rig.ctx);      // throw 1 (std::logic_error)
  reg.frame_all(rig.ctx, FrameInfo{});  // throw 2 (int)
  CHECK_FALSE(reg.is_disabled("mixed"));
  reg.frame_all(rig.ctx, FrameInfo{});  // throw 3
  CHECK(reg.is_disabled("mixed"));
  CHECK(reg.stats()[0].throws == 3);
}

TEST_CASE("a throwing on_init disables the feature immediately", "[host][registry]") {
  Rig rig;
  Counters c;
  auto f = std::make_unique<ScriptedFeature>("broken", c);
  f->throw_init = true;
  FeatureRegistry reg;
  reg.add(std::move(f));
  reg.init_all(rig.ctx);
  CHECK(reg.is_disabled("broken"));
  reg.frame_all(rig.ctx, FrameInfo{});
  reg.universe_ready_all(rig.ctx);
  CHECK(c.init == 1);
  CHECK(c.frame == 0);
  CHECK(c.universe == 0);
}

TEST_CASE("a throwing on_shutdown does not stop the remaining shutdowns", "[host][registry]") {
  Rig rig;
  Counters a, b;
  auto fa = std::make_unique<ScriptedFeature>("a", a);
  auto fb = std::make_unique<ScriptedFeature>("b", b);
  fb->throw_shutdown = true;  // b shuts down first (reverse order)
  FeatureRegistry reg;
  reg.add(std::move(fa));
  reg.add(std::move(fb));
  reg.init_all(rig.ctx);
  reg.shutdown_all(rig.ctx);
  CHECK(a.shutdown == 1);
  CHECK(b.shutdown == 1);
}

TEST_CASE("registry ignores null features", "[host][registry]") {
  FeatureRegistry reg;
  reg.add(nullptr);
  CHECK(reg.size() == 0);
}
