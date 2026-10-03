// x4mp_probe tests (M2-005): config parsing, percentile, and the password-never-logged proof, which drives the whole
// probe (init, frames, a real Session handshake against a loopback fake server, lifecycle events, config-watch
// actions, money test, shutdown) through a stub X4NativeAPI and then searches every log line, every Lua event and
// every file the probe wrote for the password.

#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <fstream>
#include <sstream>
#include <thread>

#include "../src/probe.h"
#include "../src/probe_config.h"
#include "fake_server.h"
#include "stub_host.h"
#include <x4n_core.h>

using namespace std::chrono_literals;
namespace fs = std::filesystem;
using namespace x4mp_probe;

namespace {
constexpr const char* kSecret = "Banana-Test-42-s3cret";

std::string slurp(const fs::path& p) {
  std::ifstream in(p, std::ios::binary);
  std::ostringstream ss;
  ss << in.rdbuf();
  return ss.str();
}
void spit(const fs::path& p, const std::string& s) {
  std::ofstream out(p, std::ios::binary | std::ios::trunc);
  out << s;
}
// A fresh temp dir per test (no env vars: std::filesystem::temp_directory_path is the OS API).
fs::path fresh_dir(const char* tag) {
  const auto d = fs::temp_directory_path() / (std::string("x4mp_probe_test_") + tag + "_" + std::to_string(GetCurrentProcessId()));
  fs::remove_all(d);
  fs::create_directories(d);
  return d;
}
}  // namespace

TEST_CASE("probe config: defaults, keys, and no value ever appears in diagnostics", "[probe][config]") {
  const auto r = parse_config(R"({"server":"10.0.0.5:4000","name":"Bob","password":"zzz","auto_load":false,"pin_module":true,
    "spike_block":"saves1","spike_block_seq":3,"reloadui_after_s":5,"bogus":1,"pause_seconds":"x"})");
  REQUIRE(r.ok);
  CHECK(r.config.server == "10.0.0.5:4000");
  CHECK(r.config.name == "Bob");
  CHECK(r.config.password == "zzz");
  CHECK_FALSE(r.config.auto_load);
  CHECK(r.config.pin_module);
  CHECK(r.config.spike_block_seq == 3);
  CHECK(r.config.reloadui_after_s == 5);
  CHECK(r.config.pause_seconds == 20);  // bad type keeps the default
  REQUIRE(r.notes.size() == 1);
  CHECK(r.notes[0] == "bad type for key pause_seconds");
  CHECK(describe(r.config).find("zzz") == std::string::npos);
  CHECK(describe(r.config).find("password=<set>") != std::string::npos);

  const Config d = parse_config("{}").config;
  CHECK(d.server == "127.0.0.1:47780");
  CHECK(d.name == "Tester");
  CHECK(d.password.empty());
  CHECK(d.auto_load);

  // Malformed text that contains a secret: rejected, and nothing is reported.
  const auto bad = parse_config(std::string("{\"password\":\"") + kSecret + "\" oops");
  CHECK_FALSE(bad.ok);
  CHECK(bad.notes.empty());
  CHECK_FALSE(parse_config("[1,2]").ok);
}

TEST_CASE("probe config: endpoint and percentile helpers", "[probe][config]") {
  CHECK(split_endpoint("127.0.0.1:47780")->second == 47780);
  CHECK(split_endpoint("myhost")->second == 47780);
  CHECK(split_endpoint("[::1]:9")->first == "::1");
  CHECK_FALSE(split_endpoint("").has_value());
  CHECK_FALSE(split_endpoint("h:0").has_value());
  CHECK_FALSE(split_endpoint("h:99999").has_value());
  CHECK(percentile({}, 50) == 0.0);
  CHECK(percentile({5, 1, 3, 2, 4}, 50) == 3.0);
  CHECK(percentile({5, 1, 3, 2, 4}, 95) == 5.0);
  CHECK(percentile({1.0}, 95) == 1.0);
  CHECK(action_token(parse_config(R"({"spike_block":"a","spike_block_seq":2})").config) == "2|a");
}

TEST_CASE("probe: the password from the config is never written to any log, Lua event or file", "[probe][password]") {
  const auto dir = fresh_dir("pw");
  fake::Listener server;
  const std::string cfg_path = (dir / "x4mp_probe.json").string();
  auto cfg = [&](const std::string& extra) {
    return std::string("{\"server\":\"127.0.0.1:") + std::to_string(server.endpoint().port) + "\",\"name\":\"Tester\",\"password\":\"" + kSecret +
           "\",\"pause_seconds\":1" + extra + "}";
  };
  spit(cfg_path, cfg(""));

  stub::Host host;
  // Simulated Lua side: answers the money read-back like the shim would.
  host.on_lua = [&host](const std::string& name, const std::string& param) {
    if (name == "x4mp_probe.cmd" && param.starts_with("money_read;")) {
      const std::string tag = param.substr(11);
      host.fire_str("x4mp_probe.reply", "money;tag=" + tag + ";value=" + std::to_string(host.money));
    }
  };
  x4n::detail::g_api = &host.api;
  set_config_dir_override(dir);
  init();

  auto frame = [&](int n, std::chrono::milliseconds gap = 2ms) {
    for (int i = 0; i < n; ++i) {
      host.fire("on_frame_update");
      std::this_thread::sleep_for(gap);
    }
  };
  CHECK(host.log_contains("[X4MP-PROBE]"));
  CHECK(host.log_contains("x4mp_probe init"));
  CHECK(host.log_contains("cfg loaded initial=true"));
  CHECK(host.log_contains("password=<set>"));

  frame(3);  // first tick applies the config and starts the Session
  auto conn = server.accept();
  REQUIRE(conn.valid());
  const auto hello = fake::do_server_hello(conn, /*password=*/true);
  REQUIRE(hello.has_value());
  CHECK(hello->get()->auth_proof()->size() == 32);  // a real HMAC proof was sent (the password itself was not)
  conn.send(fake::welcome(7, 1, 2, false));
  const auto deadline = std::chrono::steady_clock::now() + 5s;
  while (!host.log_contains("Welcome player_id=7") && std::chrono::steady_clock::now() < deadline) frame(1, 5ms);
  CHECK(host.log_contains("Welcome player_id=7"));

  // Lifecycle events, the pause hold, a settings click, a save mark.
  host.fire("on_game_loaded");
  host.fire("on_universe_ready");
  frame(5);
  CHECK(host.log_contains("Pause()"));
  host.fire("on_game_save");
  host.fire("on_ui_reload");
  X4NativeSettingChanged sc{"x4mp_probe", "test_button", X4N_SETTING_TOGGLE, 1, 0, nullptr};
  host.fire("on_setting_changed:x4mp_probe", &sc);
  CHECK(host.log_contains("probe button clicked"));
  host.fire_str("x4mp_probe.save_begin", "x4mp_s2test_1");

  // Config watch: a new block + the money test (the worker re-reads on mtime/size change, ~1 s).
  spit(cfg_path, cfg(",\"spike_block\":\"money\",\"spike_block_seq\":1,\"reloadui_after_s\":0"));
  const auto dl2 = std::chrono::steady_clock::now() + 6s;
  while (!host.log_contains("money result") && std::chrono::steady_clock::now() < dl2) frame(1, 5ms);
  CHECK(host.log_contains("money result"));
  CHECK(host.log_contains("delta_plus=100 delta_minus=-100 restored=true"));
  {
    std::lock_guard lk(host.mu);
    bool raised = false;
    for (const auto& [n, p] : host.lua_events) raised = raised || (n == "x4mp_spike.run" && p == "money");
    CHECK(raised);
  }

  shutdown();
  CHECK(host.log_contains("unload_for_reload done"));
  CHECK(host.log_contains("x4native_shutdown end"));
  x4n::detail::g_api = nullptr;

  // The proof: the secret is nowhere.
  const auto logs = host.log_copy();
  CHECK(logs.size() > 20);
  for (const auto& l : logs) CHECK(l.find(kSecret) == std::string::npos);
  {
    std::lock_guard lk(host.mu);
    for (const auto& [n, p] : host.lua_events) {
      CHECK(n.find(kSecret) == std::string::npos);
      CHECK(p.find(kSecret) == std::string::npos);
    }
    for (const auto& [k, v] : host.stash) {
      CHECK(k.find(kSecret) == std::string::npos);
      CHECK(v.find(kSecret) == std::string::npos);
    }
  }
  for (const auto& e : fs::directory_iterator(dir)) {
    if (e.path().filename() == "x4mp_probe.json") continue;  // the user's own config holds it by definition
    CHECK(slurp(e.path()).find(kSecret) == std::string::npos);
  }
  fs::remove_all(dir);
}
