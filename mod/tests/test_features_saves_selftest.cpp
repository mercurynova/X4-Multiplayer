// M2-10: saves feature (block flag driven from native state, game_saved warning), self-test (janitor tests: test_janitor.cpp).
// Driven through a real ModHost + FakePlatform (bridge recorded), no SDK.

#include <algorithm>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "features/diag/diag_hub.h"
#include "features/saves/saves_feature.h"
#include "features/selftest/selftest_feature.h"
#include "game/main_thread.h"
#include "game/safe_remove.h"
#include "host/mod_host.h"
#include "host_fakes.h"

using namespace x4mp;
using namespace x4mp::features;
using x4mp::game::GameVersionPod;
using x4mp::game::UniverseId;

namespace {
GameVersionPod fk_version() { return {9, 0}; }
const char* fk_suffix() { return "611726"; }
double g_time = 100.0;
double fk_time() { return g_time; }
bool fk_paused() { return false; }


struct Fixture {
  test::TempDir dir;
  test::FakePlatform platform{dir.path};
  std::vector<std::pair<log::Level, std::string>> forwarded;
  explicit Fixture(const std::string& config_json = "") {
    platform.functions["GetGameVersion"] = reinterpret_cast<void*>(&fk_version);
    platform.functions["GetBuildVersionSuffix"] = reinterpret_cast<void*>(&fk_suffix);
    platform.functions["GetCurrentGameTime"] = reinterpret_cast<void*>(&fk_time);
    platform.functions["IsGamePaused"] = reinterpret_cast<void*>(&fk_paused);
    if (!config_json.empty()) dir.write("x4mp.json", config_json);
    game::main_thread().reset();
    game::set_player_guard({});
    diag_hub().reset();
    diag_hub().set_log_sender([this](log::Level l, std::string_view t) {
      forwarded.emplace_back(l, std::string(t));
      return true;
    });
    g_time = 100.0;
  }
  ~Fixture() {
    diag_hub().reset();
    game::main_thread().reset();
    game::set_player_guard({});
    game::set_remove_backend(nullptr);
  }
  [[nodiscard]] bool forwarded_contains(const std::string& s) const {
    return std::ranges::any_of(forwarded, [&](const auto& p) { return p.second.find(s) != std::string::npos; });
  }
};

host::HostOptions with(std::function<void(host::FeatureRegistry&)> reg) {
  host::HostOptions o;
  o.register_features = std::move(reg);
  return o;
}
}  // namespace

TEST_CASE("saves: block flag follows native state and is re-pushed on ui_ready, load and change", "[saves][m210]") {
  Fixture f;
  host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SavesFeature>()); }));
  REQUIRE(h.init() == host::InitResult::Started);
  CHECK(f.platform.subscribers.contains("x4mp.ui_ready"));
  CHECK(f.platform.subscribers.contains("x4mp.game_saved"));

  h.on_frame();  // initial push (a fresh state is always announced)
  auto pushes = f.platform.raised_named("x4mp.saves");
  REQUIRE(pushes.size() == 1);
  CHECK(pushes[0] == R"({"v":1,"block":false})");

  h.on_frame();
  CHECK(f.platform.raised_named("x4mp.saves").size() == 1);  // nothing changed

  diag_hub().set_connection(NodeRole::Client, true);
  h.on_frame();
  pushes = f.platform.raised_named("x4mp.saves");
  REQUIRE(pushes.size() == 2);
  CHECK(pushes[1] == R"({"v":1,"block":true})");

  // a save load wipes Lua's in-memory flag: ui_ready (new Lua state) and the load gates re-push the same value
  f.platform.fire("x4mp.ui_ready", R"({"v":1,"startmenu":false})");
  h.on_frame();
  CHECK(f.platform.raised_named("x4mp.saves").size() == 3);
  h.on_game_loaded();
  h.on_frame();
  CHECK(f.platform.raised_named("x4mp.saves").size() == 4);
  h.on_universe_ready();
  h.on_frame();
  CHECK(f.platform.raised_named("x4mp.saves").size() == 5);

  diag_hub().set_connection(NodeRole::Authority, true);  // an authority is not blocked by this feature
  h.on_frame();
  pushes = f.platform.raised_named("x4mp.saves");
  CHECK(pushes.back() == R"({"v":1,"block":false})");
  h.shutdown();
}

TEST_CASE("saves: failed raise is retried later, not every frame", "[saves][m210]") {
  Fixture f;
  f.platform.lua_ok = false;
  host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SavesFeature>()); }));
  REQUIRE(h.init() == host::InitResult::Started);
  for (int i = 0; i < 10; ++i) h.on_frame();
  CHECK(f.platform.raised.empty());
  f.platform.lua_ok = true;
  for (int i = 0; i < 130; ++i) h.on_frame();
  CHECK(f.platform.raised_named("x4mp.saves").size() == 1);
  h.shutdown();
}

TEST_CASE("saves: game_saved while a client warns once per interval and tells the player", "[saves][m210]") {
  Fixture f;
  host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SavesFeature>()); }));
  REQUIRE(h.init() == host::InitResult::Started);

  f.platform.fire("x4mp.game_saved", R"({"v":1,"success":1,"age":10})");
  h.on_frame();
  CHECK(f.forwarded.empty());  // not a client: nothing to report

  diag_hub().set_connection(NodeRole::Client, true);
  f.platform.fire("x4mp.game_saved", R"({"v":1,"success":1,"age":20})");
  f.platform.fire("x4mp.game_saved", R"({"v":1,"success":1,"age":21})");
  h.on_frame();
  REQUIRE(f.forwarded.size() == 1);  // rate limited
  CHECK(f.forwarded[0].first == log::Level::Warn);
  CHECK(f.forwarded_contains("while connected as a client"));
  const auto notes = f.platform.raised_named("x4mp.notify");
  REQUIRE(notes.size() == 1);
  CHECK(notes[0].find(R"("level":"warn")") != std::string::npos);
  h.shutdown();
}

TEST_CASE("saves: status report reaches the hub; malformed and oversized messages are ignored", "[saves][m210]") {
  Fixture f;
  host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SavesFeature>()); }));
  REQUIRE(h.init() == host::InitResult::Started);
  f.platform.fire("x4mp.saves_status", "not json");
  f.platform.fire("x4mp.saves_status", std::string(10000, 'x'));
  h.on_frame();
  CHECK(diag_hub().saves_status().received);  // "not json" still yields a (all false) report
  CHECK_FALSE(diag_hub().saves_status().save_game_wrapped);
  f.platform.fire("x4mp.saves_status",
                  R"({"v":1,"save_game":true,"is_saving_possible":true,"menu_row":true,"tooltip":true,"blocking":false,"detail":"uix"})");
  h.on_frame();
  const auto s = diag_hub().saves_status();
  CHECK(s.save_game_wrapped);
  CHECK(s.menu_row_patched);
  CHECK(s.detail == "uix");
  h.shutdown();
}

TEST_CASE("saves: the test seam needs selftest=true", "[saves][m210]") {
  {
    Fixture f;
    host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SavesFeature>()); }));
    REQUIRE(h.init() == host::InitResult::Started);
    f.platform.fire("x4mp.saves_debug", R"({"v":1,"role":"client","connected":true})");
    h.on_frame();
    CHECK_FALSE(diag_hub().is_client());
    h.shutdown();
  }
  {
    Fixture f(R"({"selftest":true})");
    host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SavesFeature>()); }));
    REQUIRE(h.init() == host::InitResult::Started);
    f.platform.fire("x4mp.saves_debug", R"({"v":1,"role":"client","connected":true})");
    h.on_frame();
    CHECK(diag_hub().is_client());
    h.shutdown();
  }
}

TEST_CASE("selftest: chat verb runs the table and forwards every line", "[selftest][m210]") {
  Fixture f;
  host::ModHost h(f.platform, with([](host::FeatureRegistry& r) {
                    r.add(std::make_unique<SavesFeature>());
                    r.add(std::make_unique<SelfTestFeature>());
                  }));
  REQUIRE(h.init() == host::InitResult::Started);
  h.on_game_loaded();
  h.on_universe_ready();
  f.platform.fire("x4mp.saves_status",
                  R"({"v":1,"save_game":true,"is_saving_possible":true,"menu_row":true,"tooltip":true,"blocking":false})");
  f.platform.fire("x4mp.selftest", R"({"v":1})");
  for (int i = 0; i < SelfTestFeature::kSampleFrames + 2; ++i) {
    g_time += 0.5;
    h.on_frame();
  }
  const auto rows = diag_hub().last_selftest();
  REQUIRE_FALSE(rows.empty());
  const auto verdict = [&](const std::string& name) {
    for (const auto& r : rows) {
      if (r.name == name) return r.verdict;
    }
    return std::string("MISSING");
  };
  CHECK(verdict("build.supported") == "PASS");
  CHECK(verdict("saves.wrappers") == "PASS");
  CHECK(verdict("saves.block") == "PASS");
  CHECK(verdict("game.time") == "PASS");
  CHECK(verdict("main_thread") == "PASS");
  CHECK(verdict("x4native.hooks") == "PASS");
  CHECK(verdict("game.adapter") == "FAIL");  // the fake exports only a few
  CHECK(verdict("player.guard") == "WARN");  // universe ready, no player ids in the fake
  CHECK(f.forwarded_contains("SELFTEST PASS  build.supported"));
  CHECK(f.forwarded_contains("SELFTEST summary:"));
  CHECK(f.platform.raised_named("x4mp.notify").size() >= 1);

  // a second run does not happen by itself
  const auto before = f.forwarded.size();
  for (int i = 0; i < 10; ++i) h.on_frame();
  CHECK(f.forwarded.size() == before);
  h.shutdown();
}

TEST_CASE("selftest: missing Lua report and flag mismatch are FAIL; config trigger runs at universe ready", "[selftest][m210]") {
  Fixture f(R"({"selftest":true})");
  host::ModHost h(f.platform, with([](host::FeatureRegistry& r) { r.add(std::make_unique<SelfTestFeature>()); }));
  REQUIRE(h.init() == host::InitResult::Started);
  h.on_frame();
  CHECK(diag_hub().last_selftest().empty());  // nothing before the universe is ready
  h.on_game_loaded();
  h.on_universe_ready();
  for (int i = 0; i < 6; ++i) h.on_frame();
  const auto rows = diag_hub().last_selftest();
  REQUIRE_FALSE(rows.empty());
  const auto it = std::ranges::find_if(rows, [](const SelfTestRow& r) { return r.name == "saves.wrappers"; });
  REQUIRE(it != rows.end());
  CHECK(it->verdict == "FAIL");

  SavesStatus s;
  s.received = true;
  s.save_game_wrapped = s.is_saving_possible_wrapped = s.menu_row_patched = s.tooltip_patched = true;
  s.blocking = false;
  diag_hub().set_saves_status(s);
  diag_hub().set_connection(NodeRole::Client, true);  // client but Lua is not blocking: the check must say so
  f.platform.fire("x4mp.selftest", "{}");
  for (int i = 0; i < SelfTestFeature::kSampleFrames + 2; ++i) h.on_frame();
  const auto h2 = diag_hub().last_selftest();
  const auto b = std::ranges::find_if(h2, [](const SelfTestRow& r) { return r.name == "saves.block"; });
  REQUIRE(b != h2.end());
  CHECK(b->verdict == "FAIL");
  h.shutdown();
}
