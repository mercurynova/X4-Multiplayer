// x4mp_probe: see probe.h / ../README.md. Throwaway: clarity and logging over polish.
//
// Threading: every X4Native callback is treated as "maybe another thread". Callbacks only count/log and set flags;
// game and Lua calls run from the on_frame_update handler (the Lua onUpdate thread). Logging goes through one mutex.

#include "probe.h"

#include <x4_md_events.h>
#include <x4n_core.h>
#include <x4n_events.h>
#include <x4n_hooks.h>
#include <x4n_log.h>
#include <x4n_settings.h>
#include <x4n_stash.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <format>
#include <optional>
#include <span>
#include <charconv>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <fstream>
#include <memory>
#include <mutex>
#include <nlohmann/json.hpp>
#include <sstream>
#include <thread>
#include <vector>

#include "core/crypto/crypto.h"
#include "core/log/log.h"
#include "core/session/session.h"
#include "probe_config.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace x4mp_probe {
namespace {

namespace sess = x4mp::session;
using Clock = std::chrono::steady_clock;

// ------------------------------------------------------------------------------------------------------------------
// Time + logging
// ------------------------------------------------------------------------------------------------------------------
std::int64_t qpc() {
  LARGE_INTEGER v;
  QueryPerformanceCounter(&v);
  return v.QuadPart;
}
std::int64_t qpc_freq() {
  static const std::int64_t f = [] {
    LARGE_INTEGER v;
    QueryPerformanceFrequency(&v);
    return v.QuadPart;
  }();
  return f;
}
double ms_between(std::int64_t a, std::int64_t b) { return 1000.0 * static_cast<double>(b - a) / static_cast<double>(qpc_freq()); }

std::atomic<bool> g_alive{false};
std::atomic<std::int64_t> g_base_qpc{0};  // first init of this X4 process (kept in the stash), so t= is comparable across reloads
std::mutex g_log_mu;

enum Lvl { L_INFO = 1, L_WARN = 2, L_ERR = 3 };

void emit(int lvl, std::string_view tag, const std::string& body) {
  if (!g_alive.load()) return;
  char head[96];
  std::snprintf(head, sizeof(head), "[X4MP-PROBE] t=%.3f tid=%lu ", ms_between(g_base_qpc.load(), qpc()),
                static_cast<unsigned long>(GetCurrentThreadId()));
  std::string line = head;
  line.append(tag);
  line.push_back(' ');
  line += body;
  std::lock_guard lk(g_log_mu);
  if (!g_alive.load()) return;
  switch (lvl) {
    case L_ERR: x4n::log::error(std::string_view(line)); break;
    case L_WARN: x4n::log::warn(std::string_view(line)); break;
    default: x4n::log::info(std::string_view(line)); break;
  }
}

template <class... A>
void plog(std::string_view tag, std::format_string<A...> f, A&&... a) {
  try { emit(L_INFO, tag, std::format(f, std::forward<A>(a)...)); } catch (...) {}
}
template <class... A>
void pwarn(std::string_view tag, std::format_string<A...> f, A&&... a) {
  try { emit(L_WARN, tag, std::format(f, std::forward<A>(a)...)); } catch (...) {}
}

// Core logger sink -> our log (so a leak through core/ would show in the password test too).
class CoreSink final : public x4mp::log::Sink {
 public:
  void write(x4mp::log::Level level, std::int64_t, std::string_view text) override {
    try {
      emit(level >= x4mp::log::Level::Warn ? L_WARN : L_INFO, "core", std::string(x4mp::log::level_name(level)) + " " + std::string(text));
    } catch (...) {}
  }
};

// ------------------------------------------------------------------------------------------------------------------
// SEH guard around game calls: a bad native call is logged instead of taking X4 down.
// ------------------------------------------------------------------------------------------------------------------
bool seh_run(void (*fn)(void*), void* ctx, unsigned long* code) {
  __try {
    fn(ctx);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    *code = GetExceptionCode();
    return false;
  }
}
template <class F>
bool guarded(const char* what, F&& f) {
  struct Ctx { F* f; } c{&f};
  unsigned long code = 0;
  const bool ok = seh_run([](void* p) { (*static_cast<Ctx*>(p)->f)(); }, &c, &code);
  if (!ok) pwarn("seh", "call={} exception_code=0x{:08x}", what, code);
  return ok;
}

// ------------------------------------------------------------------------------------------------------------------
// Stash (x4n::stash behind IStash; mutex because Session may call it from its own threads)
// ------------------------------------------------------------------------------------------------------------------
class X4Stash final : public sess::IStash {
 public:
  void put(std::string_view key, std::string_view value) override {
    std::lock_guard lk(m_);
    const std::string k(key);
    x4n::stash::set(k.c_str(), value.data(), static_cast<std::uint32_t>(value.size()));
  }
  [[nodiscard]] std::optional<std::string> get(std::string_view key) const override {
    std::lock_guard lk(m_);
    const std::string k(key);
    std::uint32_t n = 0;
    const void* p = x4n::stash::get(k.c_str(), &n);
    if (!p) return std::nullopt;
    return std::string(static_cast<const char*>(p), n);
  }
  void erase(std::string_view key) override {
    std::lock_guard lk(m_);
    const std::string k(key);
    x4n::stash::remove(k.c_str());
  }

 private:
  mutable std::mutex m_;
};

// ------------------------------------------------------------------------------------------------------------------
// Callback thread statistics (B7)
// ------------------------------------------------------------------------------------------------------------------
enum Cb {
  CB_EXTENSION, CB_SHUTDOWN, CB_FRAME, CB_NATIVE_FRAME, CB_GAME_LOADED, CB_GAME_STARTED, CB_UNIVERSE_READY, CB_GAME_SAVE,
  CB_UI_RELOAD, CB_BEFORE_RELOAD, CB_MD_ZONE, CB_MD_STATE, CB_MD_MONEY, CB_HOOK_TIME, CB_HOOK_AUTOSAVE, CB_SETTING, CB_REPLY,
  CB_COUNT
};
constexpr const char* kCbNames[CB_COUNT] = {"x4native_init", "x4native_shutdown", "on_frame_update", "on_native_frame_update",
                                            "on_game_loaded", "on_game_started", "on_universe_ready", "on_game_save", "on_ui_reload",
                                            "on_before_reload", "md_changed_zone", "md_changed_state", "md_money_updated",
                                            "hook_after_GetCurrentGameTime", "hook_before_TriggerAutosave", "on_setting_changed",
                                            "lua_reply"};
struct CbStat {
  std::atomic<std::uint64_t> n{0};
  std::atomic<std::uint32_t> tids[4]{};
};
CbStat g_cb[CB_COUNT];
std::atomic<std::uint32_t> g_init_tid{0};
std::atomic<std::uint32_t> g_frame_tid{0};

void note(Cb cb, const char* extra = "") {
  CbStat& s = g_cb[cb];
  const std::uint64_t n = s.n.fetch_add(1) + 1;
  const std::uint32_t tid = GetCurrentThreadId();
  bool fresh = false;
  for (auto& slot : s.tids) {
    std::uint32_t cur = slot.load();
    if (cur == tid) break;
    if (cur == 0 && slot.compare_exchange_strong(cur, tid)) { fresh = true; break; }
    if (cur == tid) break;
  }
  if (n <= 5 || fresh) {
    plog("cb", "name={} n={} tid={} init_tid={} frame_tid={} new_thread={} {}", kCbNames[cb], n, tid, g_init_tid.load(), g_frame_tid.load(),
         fresh, extra);
  }
}

// ------------------------------------------------------------------------------------------------------------------
// Global state
// ------------------------------------------------------------------------------------------------------------------
std::filesystem::path g_dir_override;
std::atomic<int> g_image_inits{0};  // inits seen by this DLL image (> 1 means the image survived a reload)
std::atomic<bool> g_skip_autosave{false};
std::atomic<bool> g_hooks_installed{false};
std::atomic<std::int64_t> g_pending_save_qpc{0};  // set when a Lua SaveGame is issued / announced
std::atomic<std::uint64_t> g_time_calls{0};
std::atomic<std::uint64_t> g_autosave_calls{0};

struct MoneyTest {
  enum class Step { Idle, WaitBefore, WaitPlus, WaitAfter } step = Step::Idle;
  std::int64_t before = 0, plus = 0, after = 0, t_start = 0;
};
struct AutoLoad {
  enum class Step { Idle, ReloadList, WaitList, Loading, Done, Failed } step = Step::Idle;
  std::int64_t t0 = 0, t_reload = 0, t_issued = 0;
  std::string name;
  bool fallback_done = false;
  bool stall_warned = false;
};

struct State {
  // config (worker thread publishes, UI side applies)
  std::filesystem::path cfg_path, key_path;
  std::mutex cfg_mu;
  Config cfg_pub;
  std::atomic<std::uint64_t> cfg_version{0};
  std::uint64_t cfg_applied = 0;
  Config cfg;
  bool cfg_first = true;
  std::thread worker;
  std::mutex wmu;
  std::condition_variable wcv;
  bool wstop = false;

  // events
  std::vector<int> subs;
  std::vector<int> hooks;

  // session
  X4Stash stash;
  std::shared_ptr<x4mp::log::Logger> core_logger;
  std::unique_ptr<sess::Session> session;
  std::mutex poll_mu;
  std::vector<double> poll_ms;
  std::int64_t perf_last = 0, threads_last = 0, counts_last = 0;
  sess::SaveInfo save_info;
  bool have_save_info = false;
  int last_pct = -1;
  bool session_start_tried = false;

  // frame sources
  std::atomic<std::int64_t> last_ui_frame{0};
  bool ui_seen = false, native_seen = false;

  // flags raised from callbacks, consumed on the UI tick
  std::atomic<bool> universe_ready_flag{false};
  std::atomic<bool> button_flag{false};

  // state machines
  AutoLoad al;
  MoneyTest money;
  std::atomic<bool> reply_ready{false};
  std::atomic<std::int64_t> reply_value{0};
  std::string reply_tag;
  bool pause_active = false, pause_last = false, unpaused_check = false;
  std::int64_t pause_t0 = 0, pause_last_log = 0;
  std::int64_t reload_due = 0;
  std::string last_block_token, last_save_test;
  bool last_money = false;
  std::int64_t save_issue_qpc = 0;
  std::int64_t init_qpc = 0;
  bool pinned = false;
};
std::atomic<State*> gp{nullptr};

// ------------------------------------------------------------------------------------------------------------------
// Small helpers
// ------------------------------------------------------------------------------------------------------------------
std::string stash_get(State& s, const char* key) { return s.stash.get(key).value_or(std::string()); }
void stash_put(State& s, const char* key, std::string_view v) { s.stash.put(key, v); }

std::string wall_utc() {
  const auto t = std::time(nullptr);
  std::tm tm{};
  gmtime_s(&tm, &t);
  char b[40];
  std::strftime(b, sizeof(b), "%Y-%m-%dT%H:%M:%SZ", &tm);
  return b;
}

bool pin_self(State& s, const char* reason) {
  if (s.pinned) return true;
  HMODULE h = nullptr;
  const BOOL ok = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                                     reinterpret_cast<LPCWSTR>(&pin_self), &h);
  s.pinned = ok != 0;
  plog("pin", "reason={} ok={} module=0x{:x} last_error={}", reason, ok != 0, reinterpret_cast<std::uintptr_t>(h), ok ? 0UL : GetLastError());
  return s.pinned;
}

// Native -> Lua command channel (the SDK exposes no lua_State*). Lua shim: ui/x4mp_probe.lua.
void lua_cmd(const char* verb, const std::string& arg = {}) {
  const std::string payload = std::string(verb) + ";" + arg;
  const int rc = x4n::raise_lua("x4mp_probe.cmd", payload.c_str());
  plog("lua", "cmd={} arg={} rc={}", verb, arg, rc);
}

// ------------------------------------------------------------------------------------------------------------------
// Config: read on init, re-read when mtime/size change (worker thread)
// ------------------------------------------------------------------------------------------------------------------
bool read_file(const std::filesystem::path& p, std::string& out) {
  std::ifstream in(p, std::ios::binary);
  if (!in) return false;
  std::ostringstream ss;
  ss << in.rdbuf();
  out = ss.str();
  return out.size() <= 256 * 1024;
}

void publish_config(State& s, const std::string& text, bool initial) {
  const ParseResult r = parse_config(text);
  if (!r.ok) {
    pwarn("cfg", "parse_error file_bytes={} (config not applied; values are never logged)", text.size());
    return;
  }
  {
    std::lock_guard lk(s.cfg_mu);
    s.cfg_pub = r.config;
  }
  s.cfg_version.fetch_add(1);
  plog("cfg", "loaded initial={} {}", initial, describe(r.config));
  for (const auto& n : r.notes) pwarn("cfg", "{}", n);
}

void worker_loop(State* s) {
  std::error_code ec;
  auto last_t = std::filesystem::last_write_time(s->cfg_path, ec);
  auto last_sz = ec ? 0 : std::filesystem::file_size(s->cfg_path, ec);
  bool had = !ec;
  std::unique_lock lk(s->wmu);
  while (!s->wstop) {
    s->wcv.wait_for(lk, std::chrono::milliseconds(1000), [&] { return s->wstop; });
    if (s->wstop) break;
    lk.unlock();
    std::error_code e1, e2;
    const auto t = std::filesystem::last_write_time(s->cfg_path, e1);
    const auto sz = e1 ? 0 : std::filesystem::file_size(s->cfg_path, e2);
    const bool exists = !e1;
    if (exists && (!had || t != last_t || sz != last_sz)) {
      std::string text;
      if (read_file(s->cfg_path, text)) {
        last_t = t;
        last_sz = sz;
        had = true;
        publish_config(*s, text, false);
      }
    } else if (!exists) {
      had = false;
    }
    lk.lock();
  }
}

// Rewrites one integer key in the config file (used to reset reloadui_after_s so it acts once).
void rewrite_config_int(State& s, const char* key, int value) {
  std::string text;
  if (!read_file(s.cfg_path, text)) return;
  nlohmann::json j = nlohmann::json::parse(text, nullptr, false);
  if (j.is_discarded() || !j.is_object()) return;
  j[key] = value;
  std::ofstream out(s.cfg_path, std::ios::binary | std::ios::trunc);
  out << j.dump(2);
  plog("cfg", "rewrote key={} value={}", key, value);
}

std::array<std::uint8_t, 32> load_or_create_key(State& s) {
  std::array<std::uint8_t, 32> key{};
  std::string hex;
  {
    std::ifstream in(s.key_path);
    if (in) std::getline(in, hex);
  }
  std::vector<std::uint8_t> b;
  if (!hex.empty() && x4mp::crypto::from_hex(hex, b) && b.size() == 32) {
    std::copy(b.begin(), b.end(), key.begin());
    return key;
  }
  (void)x4mp::crypto::random_bytes(key);
  std::error_code ec;
  std::filesystem::create_directories(s.key_path.parent_path(), ec);
  std::ofstream out(s.key_path);
  out << x4mp::crypto::to_hex(key) << "\n";
  plog("conn", "player_key created file={}", s.key_path.filename().string());
  return key;
}

// ------------------------------------------------------------------------------------------------------------------
// Session
// ------------------------------------------------------------------------------------------------------------------
std::string game_save_folder() {
  auto* g = x4n::game();
  if (!g || !g->GetSaveFolderPath) return {};
  std::string out;
  guarded("GetSaveFolderPath", [&] {
    const char* p = g->GetSaveFolderPath();
    if (p) out = p;
  });
  return out;
}

void maybe_start_session(State& s) {
  if (s.session || s.session_start_tried) return;
  const Config& c = s.cfg;
  if (!c.connect || c.server.empty()) return;
  const auto ep = split_endpoint(c.server);
  if (!ep) {
    pwarn("conn", "bad server value; not connecting");
    s.session_start_tried = true;
    return;
  }
  s.session_start_tried = true;
  sess::SessionOptions o;
  o.endpoint = x4mp::net::Endpoint{ep->first, ep->second};
  o.player_name = c.name;
  if (!c.password.empty()) o.password = c.password;  // goes to Session only; never logged
  o.identity.mod_build = "x4mp_probe";
  o.identity.mod_version = "0.0.0-probe";
  o.identity.game_version = x4n::game_version() ? x4n::game_version() : "";
  o.identity.x4native_version = x4n::version() ? x4n::version() : "";
  o.player_key = load_or_create_key(s);
  o.stash = &s.stash;
  o.net.backoff_first_ms = 500;
  const std::string folder = game_save_folder();
  o.save_dir = folder;
  const std::string loaded = stash_get(s, "probe.loaded_sha256");
  if (!loaded.empty()) o.loaded_save_sha256.assign(loaded.begin(), loaded.end());
  o.resume = sess::load_intent(s.stash);
  plog("conn", "start host={} port={} name={} password={} save_dir={} resume_intent={} loaded_sha_known={}", ep->first, ep->second, c.name,
       c.password.empty() ? "<unset>" : "<set>", folder.empty() ? "<none: GetSaveFolderPath unavailable>" : folder, o.resume.has_value(),
       !loaded.empty());
  s.session = std::make_unique<sess::Session>(std::move(o));
  if (!s.session->start()) {
    pwarn("conn", "Session::start failed");
    s.session.reset();
  }
}

void on_session_event(State& s, const sess::SessionEvent& e) {
  using K = sess::SessionEvent::Kind;
  switch (e.kind) {
    case K::StateChanged: plog("conn", "state {} -> {}", sess::to_string(e.prev), sess::to_string(e.state)); break;
    case K::ServerHello: plog("conn", "ServerHello server={} required_game_build={}", s.session->server().server_name, s.session->server().required_game_build); break;
    case K::Welcome: {
      const auto& w = s.session->welcome();
      const std::string prev = stash_get(s, "probe.player_id");
      plog("conn", "Welcome player_id={} resumed={} prev_player_id={} same_player_id={} team_id={}", w.player_id, w.resumed,
           prev.empty() ? "none" : prev, prev == std::to_string(w.player_id), w.team_id);
      stash_put(s, "probe.player_id", std::to_string(w.player_id));
      break;
    }
    case K::ServerDisconnect: pwarn("conn", "ServerDisconnect code={} text={} expected={}", e.code, e.text, e.expected); break;
    case K::ControlReplayed: plog("conn", "ControlReplayed count={}", e.count); break;
    case K::ControlDropped: plog("conn", "ControlDropped count={}", e.count); break;
    case K::NetDisconnected: plog("conn", "NetDisconnected {}", e.text); break;
    case K::SaveInfo:
      s.save_info = e.save;
      s.have_save_info = true;
      plog("save", "SaveInfo name={} size={} local_file={} manifest_size={}", e.save.display_name, e.save.size, e.save.local_file_name, e.save.manifest_size);
      break;
    case K::SaveProgress: {
      const int pct = static_cast<int>(e.progress.fraction() * 100.0) / 10 * 10;
      if (pct != s.last_pct) {
        s.last_pct = pct;
        plog("save", "download {}% ({}/{} bytes)", pct, e.progress.bytes_done, e.progress.size);
      }
      break;
    }
    case K::SaveFailed: pwarn("save", "download FAILED {}", e.text); break;
    case K::SaveReady: {
      plog("save", "download complete+verified; SaveReady sent");
      s.session->mark_in_session();
      if (s.cfg.auto_load && stash_get(s, "probe.autoload_done").empty() && s.al.step == AutoLoad::Step::Idle && s.have_save_info) {
        std::string name = std::filesystem::path(s.save_info.local_file_name).filename().string();
        if (name.empty()) name = "x4mp_" + x4mp::crypto::to_hex(std::span<const std::uint8_t>(s.save_info.sha256).first(6)) + ".xml.gz";
        for (const char* ext : {".xml.gz", ".xml"}) {
          const std::string x = ext;
          if (name.size() > x.size() && name.compare(name.size() - x.size(), x.size(), x) == 0) {
            name.resize(name.size() - x.size());
            break;
          }
        }
        s.al.name = name;
        s.al.t0 = qpc();
        s.al.step = AutoLoad::Step::ReloadList;
        plog("autoload", "start save_name={} load_mode={}", name, s.cfg.load_mode);
      } else if (s.cfg.auto_load) {
        plog("autoload", "skipped (already done in this X4 process, or no SaveInfo)");
      }
      break;
    }
    case K::Frame: break;
  }
}

void poll_session(State& s) {
  if (!s.session) return;
  std::unique_lock lk(s.poll_mu, std::try_to_lock);
  if (!lk.owns_lock()) return;
  std::vector<sess::SessionEvent> ev;
  const auto t0 = qpc();
  s.session->poll(ev);
  const auto t1 = qpc();
  if (s.poll_ms.size() < 100000) s.poll_ms.push_back(ms_between(t0, t1));
  for (const auto& e : ev) on_session_event(s, e);
}

// ------------------------------------------------------------------------------------------------------------------
// State machines (UI thread)
// ------------------------------------------------------------------------------------------------------------------
void step_autoload(State& s) {
  auto* g = x4n::game();
  auto& al = s.al;
  const auto now = qpc();
  switch (al.step) {
    case AutoLoad::Step::ReloadList:
      if (g && g->ReloadSaveList) {
        guarded("ReloadSaveList", [&] { g->ReloadSaveList(); });
        al.t_reload = now;
        plog("autoload", "ReloadSaveList called (native export)");
        al.step = AutoLoad::Step::WaitList;
      } else {
        pwarn("autoload", "ReloadSaveList not resolved");
        al.step = AutoLoad::Step::Failed;
      }
      break;
    case AutoLoad::Step::WaitList: {
      bool done = false;
      if (g && g->IsSaveListLoadingComplete) guarded("IsSaveListLoadingComplete", [&] { done = g->IsSaveListLoadingComplete(); });
      if (!done) {
        if (ms_between(al.t_reload, now) > 60000.0) {
          pwarn("autoload", "save list not complete after 60 s");
          al.step = AutoLoad::Step::Failed;
        }
        break;
      }
      plog("autoload", "save list complete wait_ms={:.1f}", ms_between(al.t_reload, now));
      bool valid_plain = false, valid_ext = false;
      if (g && g->IsSaveValid) {
        const std::string with_ext = al.name + ".xml.gz";
        guarded("IsSaveValid", [&] {
          valid_plain = g->IsSaveValid(al.name.c_str());
          valid_ext = g->IsSaveValid(with_ext.c_str());
        });
      }
      plog("autoload", "IsSaveValid name={} without_ext={} with_ext={}", al.name, valid_plain, valid_ext);
      // Intent first: after LoadGame the extension reloads and must not load again.
      stash_put(s, "probe.autoload_done", "1");
      stash_put(s, "probe.loaded_sha256", std::string(s.save_info.sha256.begin(), s.save_info.sha256.end()));
      al.t_issued = now;
      if (s.cfg.load_mode == "lua") {
        lua_cmd("loadgame", al.name);
        al.fallback_done = true;
      } else {
        const int rc = x4n::raise_lua("loadSave", al.name.c_str());
        plog("autoload", "raised Lua event loadSave rc={} name={}", rc, al.name);
      }
      plog("autoload", "load issued mode={} total_ms_since_savedownload={:.1f}", s.cfg.load_mode, ms_between(al.t0, now));
      al.step = AutoLoad::Step::Loading;
      break;
    }
    case AutoLoad::Step::Loading: {
      const double since = ms_between(al.t_issued, now);
      if (!al.fallback_done && since > 10000.0) {
        pwarn("autoload", "no reload within 10 s of the loadSave event; falling back to Lua LoadGame");
        lua_cmd("loadgame", al.name);
        al.fallback_done = true;
      }
      if (!al.stall_warned && since > 120000.0) {
        al.stall_warned = true;
        pwarn("autoload", "still no reload 120 s after load issued");
      }
      break;
    }
    default: break;
  }
}

void step_pause(State& s) {
  auto* g = x4n::game();
  const auto now = qpc();
  auto paused = [&]() {
    bool p = false;
    if (g && g->IsGamePaused) guarded("IsGamePaused", [&] { p = g->IsGamePaused(); });
    return p;
  };
  if (s.universe_ready_flag.exchange(false) && s.cfg.pause_on_ready) {
    plog("pause", "universe ready: IsGamePaused_before={} -> Pause()", paused());
    lua_cmd("pause");
    s.pause_active = true;
    s.pause_t0 = now;
    s.pause_last_log = now;
    s.pause_last = paused();
    plog("pause", "after Pause() IsGamePaused={}", s.pause_last);
    return;
  }
  if (!s.pause_active) return;
  const bool p = paused();
  if (p != s.pause_last) {
    plog("pause", "edge IsGamePaused={} elapsed_ms={:.0f}", p, ms_between(s.pause_t0, now));
    s.pause_last = p;
  }
  if (ms_between(s.pause_last_log, now) >= 1000.0) {
    s.pause_last_log = now;
    plog("pause", "poll elapsed_s={:.0f} IsGamePaused={}", ms_between(s.pause_t0, now) / 1000.0, p);
  }
  if (ms_between(s.pause_t0, now) >= s.cfg.pause_seconds * 1000.0) {
    plog("pause", "hold over: Unpause() IsGamePaused_before={}", p);
    lua_cmd("unpause");
    s.pause_active = false;
    s.unpaused_check = true;
    s.pause_t0 = now;
  }
}

void step_money(State& s) {
  auto* g = x4n::game();
  auto& m = s.money;
  const auto now = qpc();
  if (m.step == MoneyTest::Step::Idle) return;
  if (ms_between(m.t_start, now) > 8000.0) {
    pwarn("money", "timeout waiting for Lua read-back (step={})", static_cast<int>(m.step));
    m.step = MoneyTest::Step::Idle;
    return;
  }
  if (!s.reply_ready.load()) return;
  const std::int64_t v = s.reply_value.load();
  s.reply_ready = false;
  auto add = [&](std::int64_t amount) {
    if (g && g->AddPlayerMoney) {
      guarded("AddPlayerMoney", [&] { g->AddPlayerMoney(amount); });
      plog("money", "AddPlayerMoney({}) called", amount);
    } else {
      pwarn("money", "AddPlayerMoney not resolved");
    }
  };
  switch (m.step) {
    case MoneyTest::Step::WaitBefore:
      m.before = v;
      add(+100);
      lua_cmd("money_read", "plus");
      m.step = MoneyTest::Step::WaitPlus;
      break;
    case MoneyTest::Step::WaitPlus:
      m.plus = v;
      add(-100);
      lua_cmd("money_read", "after");
      m.step = MoneyTest::Step::WaitAfter;
      break;
    case MoneyTest::Step::WaitAfter:
      m.after = v;
      plog("money", "result before={} plus={} after={} delta_plus={} delta_minus={} restored={} (+100 native units; delta 100 => Lua unit equals native unit, delta 1 => native is cents of Lua credits)",
           m.before, m.plus, m.after, m.plus - m.before, m.after - m.plus, m.after == m.before);
      m.step = MoneyTest::Step::Idle;
      break;
    default: break;
  }
}

void start_money(State& s) {
  if (s.money.step != MoneyTest::Step::Idle) return;
  plog("money", "native money test starting");
  s.money.t_start = qpc();
  s.reply_ready = false;
  lua_cmd("money_read", "before");
  s.money.step = MoneyTest::Step::WaitBefore;
}

void start_save_test(State& s, const std::string& name) {
  s.save_issue_qpc = qpc();
  g_pending_save_qpc = s.save_issue_qpc;
  plog("savetest", "issuing SaveGame name={} via Lua (QPC taken just before the call)", name);
  lua_cmd("savegame", name);
}

void run_block(State& s, const std::string& block) {
  plog("spike", "block={} (config watch)", block);
  if (block == "reloadui") {
    s.reload_due = qpc() + static_cast<std::int64_t>(5.0 * static_cast<double>(qpc_freq()));
    plog("reloadui", "armed by run-block in 5 s");
    return;
  }
  if (block == "pin_on") {
    pin_self(s, "run-block pin_on");
    return;
  }
  if (block == "pin_off") {
    plog("pin", "pin_off requested; a pinned module cannot be unpinned in this process (restart X4)");
    return;
  }
  if (block == "money") start_money(s);
  const int rc = x4n::raise_lua("x4mp_spike.run", block.c_str());
  plog("spike", "raised Lua event x4mp_spike.run param={} rc={}", block, rc);
}

void apply_config(State& s) {
  Config c;
  {
    std::lock_guard lk(s.cfg_mu);
    c = s.cfg_pub;
  }
  const bool first = s.cfg_first;
  s.cfg_first = false;
  const Config old = s.cfg;
  s.cfg = c;
  g_skip_autosave = c.skip_autosave;

  if (c.pin_module && !s.pinned) pin_self(s, "config pin_module");

  const std::string block_token = action_token(c);
  if (first) {
    // Startup: adopt what is in the file as already handled (survives reload through the stash).
    const std::string stored = stash_get(s, "probe.block_token");
    s.last_block_token = stored.empty() ? block_token : stored;
    s.last_save_test = stash_get(s, "probe.save_test").empty() ? c.save_test : stash_get(s, "probe.save_test");
    s.last_money = c.money_test;
    if (c.reloadui_after_s > 0) {
      plog("reloadui", "stale reloadui_after_s={} at startup: ignored and reset to 0", c.reloadui_after_s);
      rewrite_config_int(s, "reloadui_after_s", 0);
    }
  }
  if (block_token != s.last_block_token) {
    s.last_block_token = block_token;
    stash_put(s, "probe.block_token", block_token);
    if (!c.spike_block.empty()) run_block(s, c.spike_block);
  }
  if (c.save_test != s.last_save_test) {
    s.last_save_test = c.save_test;
    stash_put(s, "probe.save_test", c.save_test);
    if (!c.save_test.empty()) start_save_test(s, c.save_test);
  }
  if (c.money_test && !s.last_money) start_money(s);
  s.last_money = c.money_test;
  if (!first && c.reloadui_after_s > 0) {
    s.reload_due = qpc() + static_cast<std::int64_t>(c.reloadui_after_s * static_cast<double>(qpc_freq()));
    plog("reloadui", "armed: ExecuteDebugCommand(reloadui) in {} s", c.reloadui_after_s);
    rewrite_config_int(s, "reloadui_after_s", 0);
  }
  (void)old;
}

void periodic(State& s) {
  const auto now = qpc();
  if (s.perf_last == 0) s.perf_last = s.threads_last = s.counts_last = now;
  if (ms_between(s.perf_last, now) >= 5000.0) {
    s.perf_last = now;
    if (!s.poll_ms.empty()) {
      plog("perf", "Session::poll main-thread cost n={} p50_ms={:.4f} p95_ms={:.4f} max_ms={:.4f}", s.poll_ms.size(), percentile(s.poll_ms, 50),
           percentile(s.poll_ms, 95), *std::max_element(s.poll_ms.begin(), s.poll_ms.end()));
      s.poll_ms.clear();
    }
  }
  if (ms_between(s.threads_last, now) >= 10000.0) {
    s.threads_last = now;
    std::string body;
    for (int i = 0; i < CB_COUNT; ++i) {
      const auto n = g_cb[i].n.load();
      if (n == 0) continue;
      body += std::string(kCbNames[i]) + "{n=" + std::to_string(n) + " tids=";
      for (const auto& t : g_cb[i].tids)
        if (t.load()) body += std::to_string(t.load()) + ",";
      body += "} ";
    }
    plog("threads", "init_tid={} frame_tid={} {} get_game_time_calls={} autosave_calls={}", g_init_tid.load(), g_frame_tid.load(), body,
         g_time_calls.load(), g_autosave_calls.load());
  }
  if (s.reload_due != 0 && now >= s.reload_due) {
    s.reload_due = 0;
    plog("reloadui", "firing now");
    lua_cmd("reloadui");
  }
  if (s.unpaused_check && ms_between(s.pause_t0, now) > 500.0) {
    s.unpaused_check = false;
    bool p = false;
    auto* g = x4n::game();
    if (g && g->IsGamePaused) guarded("IsGamePaused", [&] { p = g->IsGamePaused(); });
    plog("pause", "after Unpause() IsGamePaused={}", p);
  }
}

void tick(bool ui_thread_ok) {
  State* sp = gp.load();
  if (!sp) return;
  State& s = *sp;
  if (s.cfg_version.load() != s.cfg_applied && ui_thread_ok) {
    s.cfg_applied = s.cfg_version.load();
    apply_config(s);
  }
  if (s.cfg_applied == 0) {  // no config yet (file missing); nothing to do
  } else {
    maybe_start_session(s);
  }
  poll_session(s);
  if (!ui_thread_ok) return;
  step_autoload(s);
  step_pause(s);
  step_money(s);
  periodic(s);
}

// ------------------------------------------------------------------------------------------------------------------
// Event callbacks
// ------------------------------------------------------------------------------------------------------------------
void cb_frame() {
  State* s = gp.load();
  if (!s) return;
  note(CB_FRAME);
  std::uint32_t expected = 0;
  g_frame_tid.compare_exchange_strong(expected, GetCurrentThreadId());
  if (!s->ui_seen) {
    s->ui_seen = true;
    plog("frame", "first on_frame_update ms_since_init={:.1f} (B1: ticking this early, in the start menu, means the DLL runs there)", ms_between(s->init_qpc, qpc()));
  }
  s->last_ui_frame = qpc();
  tick(true);
}

void cb_native_frame(const X4NativeFrameUpdate*) {
  State* s = gp.load();
  if (!s) return;
  note(CB_NATIVE_FRAME);
  if (!s->native_seen) {
    s->native_seen = true;
    plog("frame", "first on_native_frame_update ms_since_init={:.1f}", ms_between(s->init_qpc, qpc()));
  }
  const bool ui_quiet = ms_between(s->last_ui_frame.load(), qpc()) > 1000.0;
  if (ui_quiet) tick(s->cfg.allow_native_thread_calls);  // session polling only unless explicitly allowed
}

void lifecycle(Cb cb, const char* what) {
  note(cb);
  State* s = gp.load();
  plog("life", "event={} ms_since_init={:.1f}", what, s ? ms_between(s->init_qpc, qpc()) : 0.0);
}

void cb_game_loaded() { lifecycle(CB_GAME_LOADED, "on_game_loaded"); }
void cb_game_started() { lifecycle(CB_GAME_STARTED, "on_game_started"); }
void cb_universe_ready() {
  lifecycle(CB_UNIVERSE_READY, "on_universe_ready");
  if (State* s = gp.load()) s->universe_ready_flag = true;
}
void cb_game_save() {
  lifecycle(CB_GAME_SAVE, "on_game_save");
  const auto issued = g_pending_save_qpc.exchange(0);
  if (issued != 0) plog("savetest", "on_game_save {:.1f} ms after the SaveGame issue QPC", ms_between(issued, qpc()));
}
void cb_ui_reload() { lifecycle(CB_UI_RELOAD, "on_ui_reload"); }
void cb_before_reload() { lifecycle(CB_BEFORE_RELOAD, "on_before_reload"); }

void cb_reply(const char* data) {
  note(CB_REPLY);
  State* s = gp.load();
  const std::string text = data ? data : "";
  plog("lua", "reply {}", text);
  if (!s) return;
  if (text.starts_with("money;")) {
    const auto p = text.find("value=");
    if (p != std::string::npos) {
      std::int64_t v = 0;
      const char* b = text.data() + p + 6;
      const auto r = std::from_chars(b, text.data() + text.size(), v);
      if (r.ec == std::errc()) {
        s->reply_value = v;
        s->reply_ready = true;
      }
    }
  }
}
void cb_save_begin(const char* data) {  // Lua (spike saves4 etc.) may call raise_event("x4mp_probe.save_begin", name) just before SaveGame
  g_pending_save_qpc = qpc();
  plog("savetest", "save_begin mark from Lua name={}", data ? data : "");
}
void cb_mark(const char* data) { plog("mark", "label={}", data ? data : ""); }

void cb_setting(const X4NativeSettingChanged& st) {
  note(CB_SETTING);
  plog("button", "setting changed key={} type={} b={}", st.key ? st.key : "", st.type, st.b);
  if (st.key && std::strcmp(st.key, "test_button") == 0) {
    const int rc = x4n::raise_lua("x4mp_probe.notify", "probe button clicked");
    plog("button", "probe button clicked -> notify rc={}", rc);
  }
}

}  // namespace

// ------------------------------------------------------------------------------------------------------------------
// Public
// ------------------------------------------------------------------------------------------------------------------
void set_config_dir_override(const std::filesystem::path& dir) { g_dir_override = dir; }

void init() {
  auto* sp = new State();
  State& s = *sp;
  const bool first_ever = !s.stash.get("probe.base_qpc");
  std::int64_t base = qpc();
  if (!first_ever) {
    try { base = std::stoll(*s.stash.get("probe.base_qpc")); } catch (...) {}
  } else {
    s.stash.put("probe.base_qpc", std::to_string(base));
  }
  g_base_qpc = base;
  s.init_qpc = qpc();
  g_alive = true;
  g_init_tid = GetCurrentThreadId();
  const int image_inits = ++g_image_inits;

  // Core logger -> our log.
  x4mp::log::Logger::Options lo;
  lo.min_level = x4mp::log::Level::Info;
  s.core_logger = std::make_shared<x4mp::log::Logger>(lo, std::vector<std::shared_ptr<x4mp::log::Sink>>{std::make_shared<CoreSink>()});
  x4mp::log::Logger::set_global(s.core_logger.get());

  note(CB_EXTENSION);
  HMODULE self = nullptr;
  GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&init), &self);
  plog("init", "x4mp_probe init wall_utc={} game_version={} x4native_version={} ext_id={} module_base=0x{:x} dll_image_inits={} (>1 means this DLL image survived the reload)",
       wall_utc(), x4n::game_version() ? x4n::game_version() : "?", x4n::version() ? x4n::version() : "?",
       x4n::detail::g_api->_ext_id ? x4n::detail::g_api->_ext_id : "?", reinterpret_cast<std::uintptr_t>(self), image_inits);

  // Stash round trip + lifecycle timing across reloads.
  {
    const std::string cnt = s.stash.get("probe.init_count").value_or("0");
    int n = 0;
    try { n = std::stoi(cnt); } catch (...) {}
    ++n;
    s.stash.put("probe.init_count", std::to_string(n));
    const std::string stamp = std::to_string(n) + ":" + std::to_string(qpc());
    s.stash.put("probe.roundtrip", stamp);
    const bool rt = s.stash.get("probe.roundtrip").value_or("") == stamp;
    const auto prev_down = s.stash.get("probe.last_shutdown_qpc");
    double gap = -1.0;
    if (prev_down) { try { gap = ms_between(std::stoll(*prev_down), qpc()); } catch (...) {} }
    plog("stash", "roundtrip_ok={} init_count={} survived_previous_shutdown={} ms_since_previous_shutdown={:.1f} session_intent_present={}", rt, n,
         n > 1, gap, sess::load_intent(s.stash).has_value());
  }

  // Config.
  std::filesystem::path dir = g_dir_override.empty() ? default_config_dir() : g_dir_override;
  s.cfg_path = dir / "x4mp_probe.json";
  s.key_path = dir / "x4mp_probe_key.txt";
  plog("cfg", "file={} (Documents\\Egosoft\\X4\\x4mp resolved with SHGetKnownFolderPath)", s.cfg_path.filename().string());
  {
    std::string text;
    if (!dir.empty() && read_file(s.cfg_path, text)) publish_config(s, text, true);
    else plog("cfg", "no config file yet; waiting for it (re-read when it appears or its mtime changes)");
  }
  s.worker = std::thread(worker_loop, &s);

  gp = sp;

  // Events.
  auto sub = [&](const char* name, auto fn) {
    const int id = x4n::on(name, fn);
    s.subs.push_back(id);
    if (id <= 0) pwarn("init", "subscribe {} failed id={}", name, id);
  };
  sub("on_frame_update", +[]() { cb_frame(); });
  sub("on_native_frame_update", +[](const X4NativeFrameUpdate* f) { cb_native_frame(f); });
  sub("on_game_loaded", +[]() { cb_game_loaded(); });
  sub("on_game_started", +[]() { cb_game_started(); });
  sub("on_universe_ready", +[]() { cb_universe_ready(); });
  sub("on_game_save", +[]() { cb_game_save(); });
  sub("on_ui_reload", +[]() { cb_ui_reload(); });
  sub("on_before_reload", +[]() { cb_before_reload(); });
  sub("x4mp_probe.reply", +[](const char* d) { cb_reply(d); });
  sub("x4mp_probe.save_begin", +[](const char* d) { cb_save_begin(d); });
  sub("x4mp_probe.mark", +[](const char* d) { cb_mark(d); });
  s.subs.push_back(x4n::on_setting_changed(+[](const X4NativeSettingChanged& st) { cb_setting(st); }));

  // Two frequent typed MD events (+ the money event for C6).
  s.subs.push_back(x4n::md::on_changed_zone_after(+[](const x4n::md::ChangedZoneData&) { note(CB_MD_ZONE); }));
  s.subs.push_back(x4n::md::on_changed_state_after(+[](const x4n::md::ChangedStateData&) { note(CB_MD_STATE); }));
  s.subs.push_back(x4n::md::on_money_updated_after(+[](const x4n::md::MoneyUpdatedData& d) {
    note(CB_MD_MONEY);
    plog("money", "md money_updated old={} new={} source_id={}", d.oldamount, d.newamount, d.source_id);
  }));

  // Hooks (config.hooks, read at init only; changing it needs an X4 restart).
  const bool want_hooks = s.cfg_version.load() == 0 ? true : [&] {
    std::lock_guard lk(s.cfg_mu);
    return s.cfg_pub.hooks;
  }();
  if (want_hooks) {
    const int h1 = x4n::hook::after<&X4GameFunctions::GetCurrentGameTime>(+[](double&) {
      if (!g_alive.load()) return;
      const auto n = ++g_time_calls;
      if (n <= 5) note(CB_HOOK_TIME);
    });
    const int h2 = x4n::hook::before<&X4GameFunctions::TriggerAutosave>(+[](x4n::hook::HookControl& ctl, bool& checkenabled) {
      if (!g_alive.load()) return;
      ++g_autosave_calls;
      note(CB_HOOK_AUTOSAVE);
      const bool skip = g_skip_autosave.load();
      plog("autosave", "TriggerAutosave called checkenabled={} skip_original={}", checkenabled, skip);
      if (skip) ctl.skip_original = 1;
    });
    s.hooks = {h1, h2};
    g_hooks_installed = h1 > 0 || h2 > 0;
    plog("init", "hooks installed GetCurrentGameTime={} TriggerAutosave={}", h1, h2);
  } else {
    plog("init", "hooks disabled by config");
  }
  plog("init", "subscriptions={} lua_events: listens x4mp_probe.reply/save_begin/mark; raises x4mp_probe.cmd/notify and x4mp_spike.run", s.subs.size());
}

void shutdown() {
  State* sp = gp.load();
  if (!sp) {
    g_alive = false;
    return;
  }
  State& s = *sp;
  const auto t0 = qpc();
  note(CB_SHUTDOWN);
  plog("shutdown", "x4native_shutdown begin ms_since_init={:.1f} dll_image_inits={}", ms_between(s.init_qpc, t0), g_image_inits.load());
  gp = nullptr;  // callbacks stop doing work

  for (int id : s.subs) x4n::off(id);
  for (int id : s.hooks) x4n::hook::remove(id);
  {
    std::lock_guard lk(s.wmu);
    s.wstop = true;
  }
  s.wcv.notify_all();
  if (s.worker.joinable()) s.worker.join();

  {
    std::lock_guard lk(s.poll_mu);  // wait out a poll in flight
    if (s.session) {
      const auto u0 = qpc();
      s.session->unload_for_reload();
      plog("shutdown", "unload_for_reload done (Disconnect ClientReload sent, net thread joined) join_ms={:.1f} intent_in_stash={}", ms_between(u0, qpc()),
           sess::load_intent(s.stash).has_value());
      s.session.reset();
    }
  }
  s.stash.put("probe.last_shutdown_qpc", std::to_string(qpc()));
  if (g_hooks_installed.load() && !s.pinned) {
    // The framework may leave its detours (code in this image) installed. Unloading would then crash X4; stay resident.
    pin_self(s, "hooks installed: safety pin so a leftover detour never points into freed code (B4 'DLL unloaded?' is not observable with hooks on; set hooks=false for that)");
  }
  plog("shutdown", "x4native_shutdown end took_ms={:.1f} pinned={}", ms_between(t0, qpc()), s.pinned);

  x4mp::log::Logger::set_global(nullptr);
  s.core_logger.reset();  // joins the logger thread (its sink logs through emit(), still alive)
  g_alive = false;
  delete sp;
}

}  // namespace x4mp_probe
