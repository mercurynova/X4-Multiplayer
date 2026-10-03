// x4mp-hostsim: runs a real X4Native extension DLL (x4mp.dll) without X4. See docs/hostsim.md for the script language.
//
//   x4mp-hostsim --dll <path-to-x4mp.dll> --script <file> [options]
//     --var NAME=VALUE            (repeatable) ${NAME} in the script
//     --work-dir DIR              temp tree (saves, extension folder, hostsim.log); default: a fresh dir under %TEMP%
//     --ext-id ID                 X4Native extension id (stash namespace); default x4mp
//     --admin-url URL --admin-user U --admin-password P   server admin REST API for `expect-admin`
//     --server-exe PATH --server-arg A (repeatable)   what `start-server` launches (stdout/stderr -> --server-log)
//     --server-log FILE           --server-env NAME=VALUE (repeatable, set before launching the server)
//     --server-pid N              process `kill-server` terminates when the server was not started by hostsim
//     --stash-dump FILE           write the stash as JSON at exit (for password searches)
//     --timeout-scale F           multiplies every expect-* timeout (slow CI); default 1
//     --max-seconds N             hard stop for the whole run (default 300)
//   Exit codes: 0 pass, 1 a script command failed (the line is printed as "HOSTSIM FAIL line N: ..."), 2 bad arguments/script.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <stdexcept>
#include <map>
#include <optional>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <nlohmann/json.hpp>
#include <x4_manual_types.h>

#include "admin_client.h"
#include "host.h"

namespace fs = std::filesystem;
using json = nlohmann::json;
using namespace std::chrono_literals;
using Clock = std::chrono::steady_clock;

namespace {

struct ScriptFail : std::runtime_error {
  using std::runtime_error::runtime_error;
};
struct UsageError : std::runtime_error {
  using std::runtime_error::runtime_error;
};

struct Options {
  std::string dll, script, work_dir, ext_id = "x4mp";
  std::map<std::string, std::string> vars;
  std::string admin_url, admin_user = "admin", admin_password;
  std::string server_exe, server_log;
  std::vector<std::string> server_args;
  std::vector<std::string> server_env;
  unsigned long server_pid = 0;
  std::string stash_dump;
  double timeout_scale = 1.0;
  int max_seconds = 300;
};

// ---- string helpers ----------------------------------------------------------------------------------------------
std::string trim(const std::string& s) {
  const auto a = s.find_first_not_of(" \t\r\n");
  if (a == std::string::npos) return "";
  return s.substr(a, s.find_last_not_of(" \t\r\n") - a + 1);
}

// Splits a command line into whitespace separated tokens ("quoted text" is one token). `starts[i]` is the offset of token i.
std::vector<std::string> tokenize(const std::string& line, std::vector<std::size_t>* starts = nullptr) {
  std::vector<std::string> out;
  std::size_t i = 0;
  while (i < line.size()) {
    while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) ++i;
    if (i >= line.size()) break;
    if (starts) starts->push_back(i);
    std::string tok;
    if (line[i] == '"') {
      ++i;
      while (i < line.size() && line[i] != '"') tok += line[i++];
      if (i < line.size()) ++i;
    } else {
      while (i < line.size() && line[i] != ' ' && line[i] != '\t') tok += line[i++];
    }
    out.push_back(std::move(tok));
  }
  return out;
}

bool is_number(const std::string& s) {
  if (s.empty()) return false;
  char* end = nullptr;
  std::strtod(s.c_str(), &end);
  return end && *end == '\0';
}

// ---- json path ("jsonpath-ish") ------------------------------------------------------------------------------------
// $.a.b[0].c   a.b[name==Bob].state   list.length   Steps: .name | [index] | [key==value] | .length (array/object/string size).
std::optional<json> eval_path(const json& root, std::string path) {
  if (!path.empty() && path[0] == '$') path.erase(0, 1);
  json cur = root;
  std::size_t i = 0;
  while (i < path.size()) {
    if (path[i] == '.') {
      ++i;
      continue;
    }
    if (path[i] == '[') {
      const auto close = path.find(']', i);
      if (close == std::string::npos) return std::nullopt;
      const std::string inner = path.substr(i + 1, close - i - 1);
      i = close + 1;
      if (const auto eq = inner.find("=="); eq != std::string::npos) {
        if (!cur.is_array()) return std::nullopt;
        const std::string key = inner.substr(0, eq), want = inner.substr(eq + 2);
        bool found = false;
        for (const auto& el : cur) {
          if (!el.is_object() || !el.contains(key)) continue;
          const auto& v = el[key];
          const std::string got = v.is_string() ? v.get<std::string>() : v.dump();
          if (got == want) {
            cur = el;
            found = true;
            break;
          }
        }
        if (!found) return std::nullopt;
      } else {
        if (!cur.is_array() || !is_number(inner)) return std::nullopt;
        const auto idx = static_cast<std::size_t>(std::atoll(inner.c_str()));
        if (idx >= cur.size()) return std::nullopt;
        cur = cur[idx];
      }
      continue;
    }
    std::size_t j = i;
    while (j < path.size() && path[j] != '.' && path[j] != '[') ++j;
    const std::string name = path.substr(i, j - i);
    i = j;
    if (cur.is_object() && cur.contains(name)) {
      cur = cur[name];
    } else if (name == "length" && (cur.is_array() || cur.is_object() || cur.is_string())) {
      cur = cur.is_string() ? cur.get<std::string>().size() : cur.size();
    } else {
      return std::nullopt;
    }
  }
  return cur;
}

std::string json_text(const json& v) { return v.is_string() ? v.get<std::string>() : v.dump(); }

// op: exists | absent | == | != | < | <= | > | >= | contains. Returns "" on success, else the reason.
std::string check_value(const std::optional<json>& v, const std::string& op, const std::string& expected) {
  if (op == "absent") return v ? "value is present: " + json_text(*v) : "";
  if (!v) return "path not found";
  if (op.empty() || op == "exists") return "";
  const std::string got = json_text(*v);
  if (op == "==") {
    if (v->is_number() && is_number(expected)) return std::fabs(v->get<double>() - std::atof(expected.c_str())) < 1e-9 ? "" : "got " + got + ", want " + expected;
    return got == expected ? "" : "got " + got + ", want " + expected;
  }
  if (op == "!=") return got != expected ? "" : "value equals " + expected;
  if (op == "contains") return got.find(expected) != std::string::npos ? "" : "got " + got + ", want it to contain " + expected;
  if (op == "<" || op == "<=" || op == ">" || op == ">=") {
    if (!v->is_number() || !is_number(expected)) return "need a number, got " + got;
    const double a = v->get<double>(), b = std::atof(expected.c_str());
    const bool ok = op == "<" ? a < b : op == "<=" ? a <= b : op == ">" ? a > b : a >= b;
    return ok ? "" : "got " + got + ", want " + op + " " + expected;
  }
  throw UsageError("unknown comparison '" + op + "'");
}

std::string check_number(double got, const std::string& op, const std::string& want_text) {
  if (!is_number(want_text)) throw UsageError("expected a number, got '" + want_text + "'");
  return check_value(json(got), op, want_text);
}

// ---- the runner ---------------------------------------------------------------------------------------------------
class Runner {
 public:
  explicit Runner(Options o) : o_(std::move(o)), host_(prepare_work_dir(), o_.ext_id) {
    host_.set_log_file(work_ / "hostsim.log");
    if (!o_.admin_url.empty()) admin_.configure(o_.admin_url, o_.admin_user, o_.admin_password);
    for (const auto& kv : o_.server_env) {
      const auto eq = kv.find('=');
      if (eq != std::string::npos) server_env_[kv.substr(0, eq)] = kv.substr(eq + 1);
    }
  }

  int run(const std::vector<std::string>& lines) {
    const auto t0 = Clock::now();
    int line_no = 0, commands = 0;
    bool has_init = false;
    for (const auto& l : lines) {
      const auto t = tokenize(trim(l));
      if (!t.empty() && (t[0] == "init")) has_init = true;
    }
    int rc = 0;
    try {
      if (!has_init) {
        line_no = 0;
        host_.note("[hostsim] (implicit) init");
        cmd_init();
      }
      for (const auto& raw : lines) {
        ++line_no;
        std::string line = trim(raw);
        if (line.empty() || line[0] == '#') continue;
        ++commands;
        line = expand(line);
        execute(line, line_no);
        if (host_.violations() > 0) throw ScriptFail("the mod broke an X4Native contract (see VIOLATION lines above)");
      }
      if (loaded_) cmd_shutdown();
      write_stash_dump();
      char buf[96];
      std::snprintf(buf, sizeof(buf), "HOSTSIM OK (%d commands, %.1f s)", commands, std::chrono::duration<double>(Clock::now() - t0).count());
      host_.note(buf);
    } catch (const ScriptFail& e) {
      host_.note(std::string("HOSTSIM FAIL line ") + std::to_string(line_no) + ": " + current_cmd_ + ": " + e.what());
      rc = 1;
    } catch (const UsageError& e) {
      host_.note(std::string("HOSTSIM USAGE line ") + std::to_string(line_no) + ": " + current_cmd_ + ": " + e.what());
      rc = 2;
    }
    if (rc != 0) {
      try {
        if (loaded_) cmd_shutdown();
      } catch (...) {
      }
      write_stash_dump();
    }
    stop_server();
    if (rc == 0 && o_.work_dir.empty()) {
      std::error_code ec;
      fs::remove_all(work_, ec);
    }
    return rc;
  }

 private:
  fs::path prepare_work_dir() {
    if (!o_.work_dir.empty()) {
      work_ = fs::absolute(o_.work_dir);
    } else {
      work_ = fs::temp_directory_path() / ("x4mp-hostsim-" + std::to_string(GetCurrentProcessId()));
    }
    fs::create_directories(work_);
    return work_;
  }

  std::string expand(const std::string& line) {
    std::string out;
    for (std::size_t i = 0; i < line.size();) {
      if (line[i] == '$' && i + 1 < line.size() && line[i + 1] == '{') {
        const auto close = line.find('}', i);
        if (close == std::string::npos) throw UsageError("unterminated ${");
        const std::string name = line.substr(i + 2, close - i - 2);
        if (name == "work") out += work_.string();
        else if (name == "save_dir") out += host_.save_dir.string();
        else if (const auto it = o_.vars.find(name); it != o_.vars.end()) out += it->second;
        else throw UsageError("unknown variable ${" + name + "} (pass --var " + name + "=...)");
        i = close + 1;
      } else {
        out += line[i++];
      }
    }
    return out;
  }

  std::chrono::milliseconds scaled(long long ms) const {
    return std::chrono::milliseconds(static_cast<long long>(static_cast<double>(ms) * o_.timeout_scale));
  }

  // ---- dispatch ----
  void execute(const std::string& line, int line_no) {
    std::vector<std::size_t> starts;
    const auto t = tokenize(line, &starts);
    const std::string& cmd = t[0];
    current_cmd_ = cmd;
    const auto rest_from = [&](std::size_t k) { return k < t.size() ? trim(line.substr(starts[k])) : std::string(); };
    // Echo (payloads of lua commands are not echoed: they can hold the test password).
    if (cmd == "lua" || cmd == "lua-raw") {
      host_.note("[hostsim] L" + std::to_string(line_no) + " " + cmd + " " + (t.size() > 1 ? t[1] : "") + " (" + std::to_string(rest_from(2).size()) + " bytes)");
    } else {
      host_.note("[hostsim] L" + std::to_string(line_no) + " " + line);
    }

    if (cmd == "init") cmd_init();
    else if (cmd == "shutdown") cmd_shutdown();
    else if (cmd == "reload") cmd_reload(false);
    else if (cmd == "restart") cmd_reload(true);
    else if (cmd == "ui_reload") { need_loaded(); fire("on_ui_reload"); }
    else if (cmd == "load_save") cmd_load_save(t.size() > 1 ? t[1] : "");
    else if (cmd == "save") { need_loaded(); fire("on_game_save"); }
    else if (cmd == "frame") cmd_frame(t);
    else if (cmd == "lua") cmd_lua(t, rest_from(2), true);
    else if (cmd == "lua-raw") cmd_lua(t, rest_from(2), false);
    else if (cmd == "fire") { need_loaded(); need(t, 2, "fire <event> [text]"); std::string d = rest_from(2); fire(t[1], t.size() > 2 ? static_cast<void*>(d.data()) : nullptr); }
    else if (cmd == "set") cmd_set(t, rest_from(2));
    else if (cmd == "print") host_.note("[hostsim] " + rest_from(1));
    else if (cmd == "settle") { need(t, 2, "settle <ms>"); std::this_thread::sleep_for(std::chrono::milliseconds(std::atoll(t[1].c_str()))); }
    else if (cmd == "drop-lua") host_.drop_lua(t.size() > 1 ? t[1] : "");
    else if (cmd == "stash-clear") clear_stash();
    else if (cmd == "kill-server") cmd_kill_server();
    else if (cmd == "start-server") cmd_start_server();
    else if (cmd == "expect-lua") cmd_expect_lua(t, rest_from(0));
    else if (cmd == "expect-no-lua") cmd_expect_no_lua(t);
    else if (cmd == "expect-log") cmd_expect_log(t, rest_from(1));
    else if (cmd == "expect-no-log") { need(t, 2, "expect-no-log <text>"); if (host_.log_contains(rest_from(1))) throw ScriptFail("the mod log contains '" + rest_from(1) + "'"); }
    else if (cmd == "expect-file") cmd_expect_file(t, rest_from(2));
    else if (cmd == "expect-admin") cmd_expect_admin(t);
    else if (cmd == "expect-stash") cmd_expect_stash(t, rest_from(3));
    else if (cmd == "expect-state") cmd_expect_state(t);
    else if (cmd == "expect-sub") { need(t, 2, "expect-sub <event> [min]"); const int min = t.size() > 2 ? std::atoi(t[2].c_str()) : 1; const int n = host_.subscriber_count(t[1]); if (n < min) throw ScriptFail("the mod has " + std::to_string(n) + " subscriber(s) of '" + t[1] + "', want >= " + std::to_string(min)); }
    else if (cmd == "expect-bridge") { need(t, 2, "expect-bridge <lua-event>"); if (!host_.has_bridge(t[1])) throw ScriptFail("the mod did not call register_lua_bridge('" + t[1] + "')"); }
    else if (cmd == "expect-no-secret") cmd_expect_no_secret(t);
    else throw UsageError("unknown command '" + cmd + "'");
  }

  static void need(const std::vector<std::string>& t, std::size_t n, const char* usage) {
    if (t.size() < n) throw UsageError(std::string("usage: ") + usage);
  }
  void need_loaded() const {
    if (!loaded_) throw ScriptFail("the mod DLL is not loaded (run `init` first)");
  }
  void fire(const std::string& name, void* data = nullptr) {
    if (!host_.fire(name, data)) throw ScriptFail("a subscriber of '" + name + "' threw");
  }

  // ---- lifecycle ----
  void load_and_init() {
    dll_ = LoadLibraryA(o_.dll.c_str());
    if (!dll_) throw ScriptFail("LoadLibrary(" + o_.dll + ") failed, error " + std::to_string(GetLastError()));
    using VerFn = int (*)();
    using InitFn = int (*)(X4NativeAPI*);
    const auto ver = reinterpret_cast<VerFn>(GetProcAddress(dll_, "x4native_api_version"));
    init_ = reinterpret_cast<InitFn>(GetProcAddress(dll_, "x4native_init"));
    shutdown_ = reinterpret_cast<void (*)()>(GetProcAddress(dll_, "x4native_shutdown"));
    if (!ver || !init_) throw ScriptFail("the DLL does not export x4native_api_version / x4native_init");
    if (ver() != X4NATIVE_API_VERSION) throw ScriptFail("x4native_api_version() = " + std::to_string(ver()) + ", host speaks " + std::to_string(X4NATIVE_API_VERSION));
    host_.prepare_init();
    const auto t0 = Clock::now();
    const int rc = init_(&host_.api);
    const double ms = std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
    loaded_ = true;
    char buf[80];
    std::snprintf(buf, sizeof(buf), "[hostsim] x4native_init returned %d in %.1f ms", rc, ms);
    host_.note(buf);
    if (rc != X4NATIVE_OK) throw ScriptFail("x4native_init returned " + std::to_string(rc));
  }

  void unload() {
    double ms = 0;
    if (shutdown_) {
      const auto t0 = Clock::now();
      shutdown_();
      ms = std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
    }
    host_.clear_registrations();  // X4Native unsubscribes everything the extension left behind
    FreeLibrary(dll_);
    dll_ = nullptr;
    loaded_ = false;
    last_shutdown_ms_ = ms;
    char buf[80];
    std::snprintf(buf, sizeof(buf), "[hostsim] x4native_shutdown took %.1f ms, DLL unloaded", ms);
    host_.note(buf);
  }

  void cmd_init() {
    if (loaded_) throw ScriptFail("already initialised (use `reload`)");
    load_and_init();
  }
  void cmd_shutdown() {
    need_loaded();
    unload();
  }
  void cmd_reload(bool clear_stash_too) {
    need_loaded();
    unload();
    if (clear_stash_too) clear_stash();
    host_.reloads++;
    load_and_init();
  }
  void clear_stash() {
    // Host owns the stash map; the public surface is the API function itself.
    for (const auto& kv : host_.stash_copy()) {
      const auto slash = kv.first.find('/');
      host_.api.stash_remove(kv.first.substr(0, slash).c_str(), kv.first.substr(slash + 1).c_str());
    }
  }
  void cmd_load_save(const std::string& name) {
    need_loaded();
    if (!name.empty()) host_.note("[hostsim] load_save " + name);
    fire("on_game_loaded");
    fire("on_universe_ready");
  }

  void cmd_frame(const std::vector<std::string>& t) {
    need_loaded();
    need(t, 3, "frame <rate>Hz|fast <count>");
    const bool fast = t[1] == "fast";
    double hz = 60.0;
    if (!fast) {
      hz = std::atof(t[1].c_str());
      if (hz <= 0) throw UsageError("bad frame rate '" + t[1] + "' (use e.g. 60Hz or fast)");
    }
    const long long n = std::atoll(t[2].c_str());
    const double dt = fast ? 1.0 / 60.0 : 1.0 / hz;
    const auto period = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(dt));
    std::vector<double> costs;
    costs.reserve(static_cast<std::size_t>(n));
    const auto t0 = Clock::now();
    auto next = t0;
    double real_time = 0;
    for (long long i = 0; i < n; ++i) {
      const auto f0 = Clock::now();
      X4NativeFrameUpdate fu{};
      fu.delta = dt;
      fu.game_time = host_.game_time.load();
      fu.real_time = real_time;
      fu.fps = static_cast<float>(fast ? 60.0 : hz);
      fu.speed_multiplier = static_cast<float>(host_.speed);
      fu.game_paused = host_.paused.load();
      fu.frame_counter = static_cast<int>(host_.frames.load());
      fire("on_native_frame_update", &fu);
      fire("on_frame_update");
      costs.push_back(std::chrono::duration<double, std::milli>(Clock::now() - f0).count());
      if (!host_.paused.load()) host_.game_time.store(host_.game_time.load() + dt * host_.speed);
      real_time += dt;
      host_.frames++;
      if (host_.violations() > 0) throw ScriptFail("contract violation during frame " + std::to_string(i));
      if (!fast) {
        next += period;
        std::this_thread::sleep_until(next);
      }
    }
    std::sort(costs.begin(), costs.end());
    char buf[200];
    const auto pct = [&](double p) { return costs.empty() ? 0.0 : costs[std::min(costs.size() - 1, static_cast<std::size_t>(p * static_cast<double>(costs.size())))]; };
    std::snprintf(buf, sizeof(buf), "[hostsim] %lld frames in %.2f s; mod callback ms p50 %.3f p95 %.3f max %.3f; game_time %.2f", n,
                  std::chrono::duration<double>(Clock::now() - t0).count(), pct(0.5), pct(0.95), costs.empty() ? 0.0 : costs.back(), host_.game_time.load());
    host_.note(buf);
  }

  void cmd_lua(const std::vector<std::string>& t, const std::string& payload, bool require_bridge) {
    need_loaded();
    need(t, 2, "lua <event> [payload]");
    std::string cpp_event = t[1];
    if (!host_.has_bridge(t[1], &cpp_event)) {
      if (require_bridge) throw ScriptFail("the mod never called register_lua_bridge('" + t[1] + "'), so X4Native would drop this event (use lua-raw to bypass)");
    }
    std::string data = payload;
    fire(cpp_event, data.data());
  }

  void cmd_set(const std::vector<std::string>& t, const std::string& value) {
    need(t, 3, "set <key> <value>");
    const std::string& k = t[1];
    const auto flag = [&] { return value == "1" || value == "true" || value == "yes"; };
    if (k == "game_version") host_.game_version = value;
    else if (k == "x4native_version") host_.x4native_version = value;
    else if (k == "game_build") host_.game_types_build = std::atoi(value.c_str());
    else if (k == "build_suffix") host_.build_suffix = value;
    else if (k == "save_dir") host_.save_dir = value;
    else if (k == "paused") host_.paused = flag();
    else if (k == "save_list_complete") host_.save_list_complete = flag();
    else if (k == "save_valid") host_.save_valid = flag();
    else if (k == "game_time") host_.game_time = std::atof(value.c_str());
    else if (k == "speed") host_.speed = std::atof(value.c_str());
    else if (k == "ext_id") {
      if (loaded_) throw ScriptFail("ext_id can only be set before init");
      host_.set_ext_id(value);
    } else if (k.rfind("setting.", 0) == 0) host_.settings[k.substr(8)] = value;
    else throw UsageError("unknown setting '" + k + "'");
  }

  // ---- expectations ----
  void cmd_expect_lua(const std::vector<std::string>& t, const std::string&) {
    need(t, 2, "expect-lua <topic> [timeout_ms] [contains <text> | json <path> [op value]]");
    std::size_t i = 2;
    long long timeout = 10000;
    if (i < t.size() && is_number(t[i])) timeout = std::atoll(t[i++].c_str());
    std::function<bool(const hostsim::LuaEvent&)> accept;
    std::string desc;
    if (i < t.size() && t[i] == "contains") {
      std::string needle;
      for (std::size_t k = i + 1; k < t.size(); ++k) needle += (k > i + 1 ? " " : "") + t[k];
      accept = [needle](const hostsim::LuaEvent& e) { return e.param.find(needle) != std::string::npos; };
      desc = " containing '" + needle + "'";
    } else if (i < t.size() && t[i] == "json") {
      need(t, i + 2, "expect-lua <topic> json <path> [op value]");
      const std::string path = t[i + 1], op = i + 2 < t.size() ? t[i + 2] : "", val = i + 3 < t.size() ? t[i + 3] : "";
      accept = [path, op, val](const hostsim::LuaEvent& e) {
        const auto j = json::parse(e.param, nullptr, false);
        if (j.is_discarded()) return false;
        return check_value(eval_path(j, path), op, val).empty();
      };
      desc = " with json " + path + " " + op + " " + val;
    }
    hostsim::LuaEvent got;
    if (!host_.wait_lua(t[1], scaled(timeout), accept, &got)) {
      throw ScriptFail("no Lua event '" + t[1] + "'" + desc + " within " + std::to_string(scaled(timeout).count()) + " ms; pending: " + host_.lua_summary());
    }
    host_.note("[hostsim]   got " + t[1] + " " + got.param.substr(0, 160));
  }

  void cmd_expect_no_lua(const std::vector<std::string>& t) {
    need(t, 2, "expect-no-lua <topic> [window_ms]");
    const long long window = t.size() > 2 ? std::atoll(t[2].c_str()) : 1000;
    hostsim::LuaEvent got;
    if (host_.wait_lua(t[1], std::chrono::milliseconds(window), nullptr, &got)) throw ScriptFail("unexpected Lua event '" + t[1] + "': " + got.param.substr(0, 160));
  }

  void cmd_expect_log(const std::vector<std::string>& t, const std::string& rest) {
    need(t, 2, "expect-log <text> [timeout=<ms>]");
    long long timeout = 10000;
    std::string needle = rest;
    if (const auto p = needle.rfind(" timeout="); p != std::string::npos && is_number(needle.substr(p + 9))) {
      timeout = std::atoll(needle.c_str() + p + 9);
      needle.erase(p);
    }
    if (!host_.wait_log(needle, scaled(timeout), false)) throw ScriptFail("the mod log never contained '" + needle + "' within " + std::to_string(scaled(timeout).count()) + " ms");
  }

  // expect-file <path> <text>: the file (relative to the work dir; the mod's file log is extension/logs/x4mp.log) holds the text.
  void cmd_expect_file(const std::vector<std::string>& t, const std::string& text) {
    need(t, 3, "expect-file <path> <text>");
    fs::path p = t[1];
    if (p.is_relative()) p = work_ / p;
    const auto deadline = Clock::now() + scaled(3000);
    for (;;) {
      std::ifstream f(p, std::ios::binary);
      const std::string data((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
      if (data.find(text) != std::string::npos) return;
      if (Clock::now() >= deadline) throw ScriptFail(p.string() + (f ? " does not contain '" : " cannot be read; wanted '") + text + "'");
      std::this_thread::sleep_for(100ms);  // condition wait with timeout (the mod's log writer may lag)
    }
  }

  void cmd_expect_admin(const std::vector<std::string>& t) {
    need(t, 2, "expect-admin <path> [<jsonpath> [op value]] [timeout=<ms>]");
    long long timeout = 10000;
    std::vector<std::string> a(t.begin() + 1, t.end());
    if (!a.empty() && a.back().rfind("timeout=", 0) == 0) {
      timeout = std::atoll(a.back().c_str() + 8);
      a.pop_back();
    }
    const std::string path = a[0];
    const std::string jp = a.size() > 1 ? a[1] : "";
    const std::string op = a.size() > 2 ? a[2] : (jp.empty() ? "" : "exists");
    std::string expected;
    for (std::size_t k = 3; k < a.size(); ++k) expected += (k > 3 ? " " : "") + a[k];
    const auto deadline = Clock::now() + scaled(timeout);
    std::string last;
    for (;;) {
      const auto r = admin_.get(path);
      if (!r.error.empty()) last = r.error;
      else if (r.status < 200 || r.status >= 300) last = "HTTP " + std::to_string(r.status);
      else if (jp.empty()) return;
      else {
        const auto j = json::parse(r.body, nullptr, false);
        if (j.is_discarded()) last = "response is not JSON";
        else {
          last = check_value(eval_path(j, jp), op, expected);
          if (last.empty()) return;
        }
      }
      if (Clock::now() >= deadline) break;
      std::this_thread::sleep_for(200ms);  // between REST attempts of a condition wait with a timeout
    }
    throw ScriptFail("GET " + path + " " + jp + " " + op + " " + expected + ": " + last + " (after " + std::to_string(scaled(timeout).count()) + " ms)");
  }

  void cmd_expect_stash(const std::vector<std::string>& t, const std::string& text) {
    need(t, 3, "expect-stash <key> present|absent|== <text>");
    const std::string key = t[1].find('/') == std::string::npos ? o_.ext_id + "/" + t[1] : t[1];
    std::string val;
    const bool has = host_.stash_value(key, &val);
    if (t[2] == "present") {
      if (!has) throw ScriptFail("stash key " + key + " is missing");
    } else if (t[2] == "absent") {
      if (has) throw ScriptFail("stash key " + key + " is present");
    } else if (t[2] == "==") {
      if (!has) throw ScriptFail("stash key " + key + " is missing");
      while (!val.empty() && val.back() == '\0') val.pop_back();
      if (val != text) throw ScriptFail("stash " + key + " = '" + val.substr(0, 80) + "', want '" + text + "'");
    } else {
      throw UsageError("expected present, absent or ==");
    }
  }

  void cmd_expect_state(const std::vector<std::string>& t) {
    need(t, 4, "expect-state <name> <op> <value>");
    const std::string& n = t[1];
    double got = 0;
    if (n == "paused") got = host_.paused ? 1 : 0;
    else if (n == "game_time") got = host_.game_time;
    else if (n == "reload_save_list_calls") got = host_.reload_save_list_calls;
    else if (n == "money_delta") got = static_cast<double>(host_.money_delta.load());
    else if (n == "reloads") got = host_.reloads;
    else if (n == "frames") got = static_cast<double>(host_.frames.load());
    else if (n == "stash_count") got = static_cast<double>(host_.stash_copy().size());
    else if (n == "hooks") got = static_cast<double>(host_.hook_count());
    else if (n == "lua_pending") got = static_cast<double>(host_.lua_pending());
    else if (n == "last_shutdown_ms") got = last_shutdown_ms_;
    else if (n.rfind("subs.", 0) == 0) got = host_.subscriber_count(n.substr(5));
    else throw UsageError("unknown state '" + n + "'");
    const auto why = check_number(got, t[2], t[3]);
    if (!why.empty()) throw ScriptFail(n + ": " + why);
  }

  void cmd_expect_no_secret(const std::vector<std::string>& t) {
    need(t, 2, "expect-no-secret <text> [extra-dir...]");
    const std::string& s = t[1];
    if (s.empty()) throw UsageError("empty secret");
    for (const auto& l : host_.log_copy()) if (l.find(s) != std::string::npos) throw ScriptFail("secret found in the mod log");
    for (const auto& e : host_.lua_all()) if (e.topic.find(s) != std::string::npos || e.param.find(s) != std::string::npos) throw ScriptFail("secret found in Lua event '" + e.topic + "'");
    for (const auto& kv : host_.stash_copy()) if (kv.first.find(s) != std::string::npos || kv.second.find(s) != std::string::npos) throw ScriptFail("secret found in the stash key " + kv.first);
    std::vector<fs::path> roots{work_};
    for (std::size_t i = 2; i < t.size(); ++i) roots.emplace_back(t[i]);
    std::size_t files = 0;
    for (const auto& root : roots) {
      std::error_code ec;
      for (fs::recursive_directory_iterator it(root, fs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec)) {
        if (!it->is_regular_file(ec) || it->file_size(ec) > (64u << 20)) continue;
        std::ifstream f(it->path(), std::ios::binary);
        std::string data((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        ++files;
        if (data.find(s) != std::string::npos) throw ScriptFail("secret found in " + it->path().string());
      }
    }
    host_.note("[hostsim]   secret scan clean (log, Lua events, stash, " + std::to_string(files) + " files)");
  }

  // ---- server control ----
  void cmd_kill_server() {
    HANDLE h = server_handle_;
    bool own = h != nullptr;
    if (!h && o_.server_pid) {
      h = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, o_.server_pid);
      if (!h) throw ScriptFail("cannot open server pid " + std::to_string(o_.server_pid));
    }
    if (!h) throw ScriptFail("no server to kill (start it with start-server or pass --server-pid)");
    TerminateProcess(h, 1);
    WaitForSingleObject(h, 15000);
    CloseHandle(h);
    if (own) server_handle_ = nullptr;
    o_.server_pid = 0;
  }

  void cmd_start_server() {
    if (o_.server_exe.empty()) throw ScriptFail("start-server needs --server-exe");
    for (const auto& [k, v] : server_env_) SetEnvironmentVariableA(k.c_str(), v.c_str());
    SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE};
    HANDLE log = INVALID_HANDLE_VALUE;
    if (!o_.server_log.empty()) log = CreateFileA(o_.server_log.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    STARTUPINFOA si{};
    si.cb = sizeof(si);
    if (log != INVALID_HANDLE_VALUE) {
      si.dwFlags = STARTF_USESTDHANDLES;
      si.hStdOutput = log;
      si.hStdError = log;
      si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    }
    PROCESS_INFORMATION pi{};
    const auto quote = [](const std::string& s) { return s.find_first_of(" \t") == std::string::npos ? s : "\"" + s + "\""; };
    std::string cmdline = quote(o_.server_exe);
    for (const auto& a : o_.server_args) cmdline += " " + quote(a);
    if (!CreateProcessA(nullptr, cmdline.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
      throw ScriptFail("CreateProcess failed (" + std::to_string(GetLastError()) + "): " + cmdline);
    }
    if (log != INVALID_HANDLE_VALUE) CloseHandle(log);
    CloseHandle(pi.hThread);
    server_handle_ = pi.hProcess;
    const auto deadline = Clock::now() + scaled(40000);
    for (;;) {
      if (WaitForSingleObject(server_handle_, 0) == WAIT_OBJECT_0) throw ScriptFail("the server exited right after start (see --server-log)");
      if (admin_.configured()) {
        const auto r = admin_.request("GET", "/healthz", "", false);
        if (r.error.empty() && r.status == 200) break;
      } else {
        break;
      }
      if (Clock::now() >= deadline) throw ScriptFail("the server did not answer /healthz within 40 s");
      std::this_thread::sleep_for(200ms);  // condition wait with timeout
    }
  }

  void stop_server() {
    if (server_handle_) {
      TerminateProcess(server_handle_, 0);
      WaitForSingleObject(server_handle_, 5000);
      CloseHandle(server_handle_);
      server_handle_ = nullptr;
    }
  }

  void write_stash_dump() {
    if (o_.stash_dump.empty()) return;
    json j = json::object();
    for (const auto& kv : host_.stash_copy()) {
      // Strings (null terminated) as text, everything else hex: the dump exists for password searches.
      std::string v = kv.second;
      if (!v.empty() && v.back() == '\0') v.pop_back();
      j[kv.first] = v;
    }
    std::ofstream(o_.stash_dump) << j.dump(2, ' ', false, json::error_handler_t::replace);
  }

  Options o_;
  fs::path work_;
  hostsim::Host host_;
  hostsim::AdminClient admin_;
  std::string current_cmd_;
  HMODULE dll_ = nullptr;
  bool loaded_ = false;
  int (*init_)(X4NativeAPI*) = nullptr;
  void (*shutdown_)() = nullptr;
  double last_shutdown_ms_ = 0;
  HANDLE server_handle_ = nullptr;
  std::map<std::string, std::string> server_env_;
};

LONG WINAPI crash_filter(EXCEPTION_POINTERS* ep) {
  HMODULE m = nullptr;
  char name[MAX_PATH] = "?";
  if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, static_cast<LPCSTR>(ep->ExceptionRecord->ExceptionAddress), &m)) {
    GetModuleFileNameA(m, name, MAX_PATH);
  }
  std::fprintf(stdout, "HOSTSIM FAIL: crash 0x%08lX at %p in %s\n", ep->ExceptionRecord->ExceptionCode, ep->ExceptionRecord->ExceptionAddress, name);
  std::fflush(stdout);
  return EXCEPTION_EXECUTE_HANDLER;
}

Options parse_args(int argc, char** argv) {
  Options o;
  for (int i = 1; i < argc; ++i) {
    const std::string a = argv[i];
    const auto val = [&]() -> std::string {
      if (i + 1 >= argc) throw UsageError("missing value for " + a);
      return argv[++i];
    };
    if (a == "--dll") o.dll = val();
    else if (a == "--script") o.script = val();
    else if (a == "--work-dir") o.work_dir = val();
    else if (a == "--ext-id") o.ext_id = val();
    else if (a == "--var") {
      const auto kv = val();
      const auto eq = kv.find('=');
      if (eq == std::string::npos) throw UsageError("--var needs NAME=VALUE");
      o.vars[kv.substr(0, eq)] = kv.substr(eq + 1);
    } else if (a == "--admin-url") o.admin_url = val();
    else if (a == "--admin-user") o.admin_user = val();
    else if (a == "--admin-password") o.admin_password = val();
    else if (a == "--server-exe") o.server_exe = val();
    else if (a == "--server-arg") o.server_args.push_back(val());
    else if (a == "--server-log") o.server_log = val();
    else if (a == "--server-env") o.server_env.push_back(val());
    else if (a == "--server-pid") o.server_pid = std::strtoul(val().c_str(), nullptr, 10);
    else if (a == "--stash-dump") o.stash_dump = val();
    else if (a == "--timeout-scale") o.timeout_scale = std::atof(val().c_str());
    else if (a == "--max-seconds") o.max_seconds = std::atoi(val().c_str());
    else throw UsageError("unknown argument " + a);
  }
  if (o.dll.empty() || o.script.empty()) throw UsageError("usage: x4mp-hostsim --dll <x4mp.dll> --script <file> [options]");
  return o;
}

}  // namespace

int main(int argc, char** argv) {
  SetUnhandledExceptionFilter(crash_filter);
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
  try {
    const Options o = parse_args(argc, argv);
    std::ifstream f(o.script);
    if (!f) throw UsageError("cannot read script " + o.script);
    std::vector<std::string> lines;
    for (std::string l; std::getline(f, l);) lines.push_back(l);

    // Hard stop for a hung mod (a deadlock in shutdown would otherwise hang CI until its own timeout).
    const int max_s = o.max_seconds;
    std::thread([max_s] {
      std::this_thread::sleep_for(std::chrono::seconds(max_s));
      std::printf("HOSTSIM FAIL: exceeded --max-seconds %d (hung?)\n", max_s);
      std::fflush(stdout);
      TerminateProcess(GetCurrentProcess(), 1);
    }).detach();

    Runner runner(o);
    const int rc = runner.run(lines);
    std::fflush(stdout);
    // Do not run static destructors against a mod DLL that may have left threads behind.
    TerminateProcess(GetCurrentProcess(), static_cast<UINT>(rc));
    return rc;
  } catch (const UsageError& e) {
    std::fprintf(stderr, "x4mp-hostsim: %s\n", e.what());
    return 2;
  }
}
