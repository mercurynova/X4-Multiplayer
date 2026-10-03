#include "host/mod_host.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <sstream>

#include "core/version/version.h"
#include "game/main_thread.h"
#include "game/player_guard.h"
#include "game/safe_remove.h"
#include "host/status_json.h"

namespace x4mp::host {

namespace {
std::atomic<HostLog*> g_violation_log{nullptr};

void on_main_thread_violation(const char* where) noexcept {
  if (HostLog* l = g_violation_log.load()) {
    X4MP_CLOG(*l, Cat::Host, Level::Error, "game call '{}' refused: not on the main thread", where ? where : "?");
  }
}

std::string_view build_status_name(BuildStatus s) {
  switch (s) {
    case BuildStatus::Supported: return "supported";
    case BuildStatus::Unsupported: return "UNSUPPORTED";
    case BuildStatus::Unverified: return "unverified";
  }
  return "?";
}
}  // namespace

ModHost::ModHost(IPlatform& platform, HostOptions options)
    : platform_(platform), options_(std::move(options)), clock_(options_.clock ? options_.clock : &qpc_now_ns) {
  if (!options_.register_features) options_.register_features = &register_builtin_features;
}

ModHost::~ModHost() { shutdown(); }

InitResult ModHost::init() noexcept {
  try {
    if (initialised_ || finished_) return running_ ? InitResult::Started : InitResult::Refused;
    initialised_ = true;

    auto& mt = game::main_thread();
    mt.reset();
    mt.capture_init();
    mt.set_violation_handler(&on_main_thread_violation);

    // Stash from an earlier incarnation (reload).
    {
      std::uint32_t size = 0;
      const void* p = platform_.stash_get("host.state", &size);
      if (p && size == sizeof(HostStateBlob)) {
        HostStateBlob b;
        std::memcpy(&b, p, sizeof b);
        if (b.magic == HostStateBlob{}.magic && b.version == 1) {
          previous_.present = true;
          previous_.reload_count = b.reload_count;
          previous_.game_loaded_at_shutdown = b.game_loaded != 0;
          previous_.universe_ready_at_shutdown = b.universe_ready != 0;
        }
      }
    }

    extension_path_ = platform_.extension_path();
    paths_ = resolve_config_paths(extension_path_, options_.documents_override);
    config::LoadOptions lo;
    lo.user_file = paths_.user_file;
    lo.launch_file = paths_.launch_file;
    auto loaded = config::load(lo);
    config_ = loaded.config;
    if (config_.launch.active) {
      // launch.json was consumed by load(): remember which keys it set so a /reloadui (which no longer sees the
      // file) can re-apply them over a re-read x4mp.json.
      config::LoadOptions user_only;
      user_only.user_file = paths_.user_file;
      const config::Config base = config::load(user_only).config;
      launch_overlay_.active = true;
      launch_overlay_.server = config_.server_host != base.server_host || config_.tcp_port != base.tcp_port;
      launch_overlay_.name = config_.player_name != base.player_name;
      launch_overlay_.password = config_.password != base.password;
      launch_overlay_.snapshot = config_;
    }

    HostLog::Options ho;
    ho.file = config_.log_file.empty() ? paths_.default_log_file : std::filesystem::path(config_.log_file);
    ho.level = config_.log_level;
    ho.rate_limit = static_cast<std::uint32_t>(config_.log_rate_limit);
    ho.native_mirror = [this](log::Level level, std::string_view text) { platform_.native_log(level, text); };
    log_ = std::make_unique<HostLog>(std::move(ho));
    for (const auto& w : log_->apply_config(config_)) log_->raw(Cat::Host, Level::Warn, w);
    log::Logger::set_global(&log_->logger());
    g_violation_log.store(log_.get());
    config::report(loaded, log_->logger());

    // Game adapter + build check.
    game::GameInfo info;
    info.game_version = platform_.game_version();
    info.x4native_version = platform_.x4native_version();
    info.game_types_build = platform_.game_types_build();
    game_ = game::GameApi(game::resolve_game_fns([this](const char* n) { return platform_.get_game_function(n); }), std::move(info));

    BuildInfo bi;
    bi.game_version = game_.info().game_version;
    bi.version = game_.game_version_struct();
    bi.build_suffix = game_.build_version_suffix();
    bi.game_types_build = game_.info().game_types_build;
    bi.x4native_version = game_.info().x4native_version;
    build_ = check_build(bi, version::game_build_pin());

    budget_ = std::make_unique<FrameBudget>(config_.frame_budget_us, clock_);
    ctx_ = std::make_unique<HostContext>(HostContext{config_, game_, *log_, platform_, *budget_, gates_, previous_, extension_path_, &paths_});

    log_header();
    platform_.native_log(log::Level::Info, version::hello_line());

    if (build_.status == BuildStatus::Unsupported) {
      refused_ = true;
      refusal_reason_ = build_.reason;
      log_->raw(Cat::Host, Level::Error,
                "REFUSING TO START: " + build_.reason + " [detected " + build_.detected + ", mod pin " +
                    std::string(version::game_build_pin()) + "]; the mod stays inert (no events, no features)");
      log_->flush();
      install_refused_responder();
      return InitResult::Refused;
    }
    if (build_.status == BuildStatus::Unverified) {
      log_->raw(Cat::Host, Level::Warn,
                "build not verified: " + build_.reason + "; raw game_version='" + game_.info().game_version +
                    "' suffix='" + bi.build_suffix.value_or("<none>") + "'");
    }

    const auto missing = game::missing_exports(game_.fns());
    if (!missing.empty()) {
      std::string list;
      for (const auto& m : missing) list += (list.empty() ? "" : ", ") + m;
      log_->raw(Cat::Host, Level::Warn, "game exports missing (features must cope): " + list);
    }
    const bool removal = game::install_game_backend([this](const char* n) { return platform_.get_game_function(n); });
    log_->raw(Cat::Host, Level::Info, std::string("SafeRemove backend ") + (removal ? "installed" : "NOT available (removals are no-ops)"));

    options_.register_features(registry_);
    registry_.init_all(*ctx_);

    last_frame_ns_ = 0;
    last_perf_ns_ = clock_();
    running_ = true;
    X4MP_CLOG(*log_, Cat::Host, Level::Info, "host started: {} feature(s), frame budget {} us", registry_.size(), config_.frame_budget_us);
    log_->flush();
    return InitResult::Started;
  } catch (...) {
    // Init must never throw into X4. Stay inert.
    refused_ = true;
    running_ = false;
    refusal_reason_ = "host initialisation failed";
    try {
      platform_.native_log(log::Level::Error, "x4mp: host initialisation threw; the mod stays inert");
    } catch (...) {
    }
    return InitResult::Refused;
  }
}

// A refused host subscribes to no game event, but the Lua UI still has to show the player WHY (criterion 6): it answers the
// three verbs the join UI sends with a "rejected / build" status carrying the reason. Verb handlers run inside the Lua call
// (the UI thread), so raising the Lua event from them is legal.
void ModHost::install_refused_responder() noexcept {
  try {
    const std::string status = make_status_json(StatusFields{.state = "rejected", .detail = build_.reason, .reject = "build"});
    IPlatform* platform = &platform_;
    const auto answer = [platform, status](std::string_view) { (void)platform->raise_lua("x4mp.status", status); };
    for (const char* verb : {"x4mp.ui_ready", "x4mp.request_status", "x4mp.join"}) platform_.on_lua_verb(verb, answer);
  } catch (...) {
  }
}

void ModHost::log_header() {
  HostLog& l = *log_;
  l.raw(Cat::Host, Level::Info, version::hello_line());
  l.raw(Cat::Host, Level::Info,
        "game_version=" + game_.info().game_version + " build_suffix=" + game_.build_version_suffix().value_or("<unavailable>") +
            " types_build=" + std::to_string(game_.info().game_types_build) + " x4native=" + game_.info().x4native_version);
  l.raw(Cat::Host, Level::Info,
        std::string("build check: ") + std::string(build_status_name(build_.status)) + " (" + build_.reason + ")");
  l.raw(Cat::Host, Level::Info,
        std::string("config dir=") + (paths_.portable ? "<extension folder, portable>" : "<Documents>\\Egosoft\\X4\\x4mp") +
            " user_file_exists=" + (std::filesystem::exists(paths_.user_file) ? "yes" : "no"));
  if (previous_.present) {
    l.raw(Cat::Host, Level::Info,
          "previous run: reload_count=" + std::to_string(previous_.reload_count) +
              " game_loaded=" + (previous_.game_loaded_at_shutdown ? "1" : "0") +
              " universe_ready=" + (previous_.universe_ready_at_shutdown ? "1" : "0"));
  }
  std::istringstream desc(config::describe(config_));  // password is shown as <redacted>/<unset>
  for (std::string line; std::getline(desc, line);) l.raw(Cat::Host, Level::Info, "config " + line);
}

void ModHost::on_frame() noexcept {
  if (!running_) return;
  try {
    game::main_thread().capture_frame();
    drain_pending_events();
    budget_->begin_frame();
    const std::int64_t now = clock_();
    ++frame_index_;

    FrameInfo fi;
    fi.frame_index = frame_index_;
    fi.delta_s = last_frame_ns_ == 0 ? 0.0 : static_cast<double>(now - last_frame_ns_) / 1e9;
    last_frame_ns_ = now;
    fi.game_loaded = gates_.game_loaded;
    fi.universe_ready = gates_.universe_ready;
    if (gates_.game_loaded) {
      fi.game_time = game_.game_time();
      fi.game_paused = game_.game_paused();
    }

    if (gates_.universe_ready) guard_ids_ = game::refresh_player_guard(game_);
    registry_.frame_all(*ctx_, fi);

    const std::int64_t spent = budget_->end_frame();
    if (spent > budget_->budget_ns()) {
      X4MP_CLOG(*log_, Cat::Perf, Level::Debug, "frame {} used {} us of {} us", frame_index_, spent / 1000, budget_->budget_ns() / 1000);
    }
    maybe_log_perf(now);
  } catch (...) {
    // The registry already contains feature throws; this only catches the host's own bugs. Never propagate.
    if (log_) X4MP_CLOG(*log_, Cat::Host, Level::Error, "unexpected exception in on_frame");
  }
}

void ModHost::maybe_log_perf(std::int64_t now_ns) {
  if ((now_ns - last_perf_ns_) < options_.perf_log_interval_ms * 1'000'000) return;
  last_perf_ns_ = now_ns;
  const FrameStats s = budget_->stats();
  X4MP_CLOG(*log_, Cat::Perf, Level::Info,
            "frames={} p50={}us p95={}us avg={}us max={}us over_budget={} budget={}us features_disabled={} guard_ids={} "
            "remove_blocked_by_guard={} main_thread_violations={} log_dropped={}",
            s.frames, s.p50_ns / 1000, s.p95_ns / 1000, s.avg_ns / 1000, s.max_ns / 1000, s.over_budget, s.budget_ns / 1000,
            registry_.disabled_count(), guard_ids_, game::blocked_removal_count(), game::main_thread().violations(),
            log_->logger().dropped_lines());
  budget_->reset_window();
}

// Session-2 finding B7: on_game_loaded can be delivered on X4Native's native thread. The gates and the features' hooks must only
// run on the frame thread, so an off-thread delivery is parked in an atomic flag and replayed (in order) by the next on_frame.
void ModHost::on_game_loaded() noexcept {
  if (!running_) return;
  if (!game::main_thread().is_main()) {
    pending_game_loaded_.store(true);
    return;
  }
  handle_game_loaded();
}

void ModHost::drain_pending_events() noexcept {
  if (pending_game_loaded_.exchange(false)) handle_game_loaded();
  if (pending_universe_ready_.exchange(false)) handle_universe_ready();
}

void ModHost::handle_game_loaded() noexcept {
  try {
    gates_.game_loaded = true;
    gates_.universe_ready = false;
    X4MP_CLOG(*log_, Cat::Host, Level::Info, "game loaded (universe not ready yet)");
    registry_.game_loaded_all(*ctx_);
  } catch (...) {
  }
}

void ModHost::on_universe_ready() noexcept {
  if (!running_) return;
  if (!game::main_thread().is_main()) {
    pending_universe_ready_.store(true);
    return;
  }
  drain_pending_events();  // a parked on_game_loaded must come first
  handle_universe_ready();
}

void ModHost::handle_universe_ready() noexcept {
  try {
    gates_.game_loaded = true;
    gates_.universe_ready = true;
    ++gates_.universe_epoch;
    guard_ids_ = game::refresh_player_guard(game_);
    X4MP_CLOG(*log_, Cat::Host, Level::Info, "universe ready (epoch {}, {} guarded id(s))", gates_.universe_epoch, guard_ids_);
    registry_.universe_ready_all(*ctx_);
  } catch (...) {
  }
}

void ModHost::on_ui_reload() noexcept {
  if (!running_) return;
  try {
    config::LoadOptions lo;
    lo.user_file = paths_.user_file;
    lo.launch_file = paths_.launch_file;
    auto loaded = config::load(lo);
    // launch.json is one-shot: a reload that finds no new request keeps the one consumed at init.
    if (!loaded.config.launch.active && launch_overlay_.active) {
      const config::Config& s = launch_overlay_.snapshot;
      loaded.config.launch = s.launch;
      if (launch_overlay_.server) {
        loaded.config.server_host = s.server_host;
        loaded.config.tcp_port = s.tcp_port;
      }
      if (launch_overlay_.name) loaded.config.player_name = s.player_name;
      if (launch_overlay_.password) loaded.config.password = s.password;
    }
    config_ = loaded.config;
    for (const auto& w : log_->apply_config(config_)) log_->raw(Cat::Host, Level::Warn, w);
    config::report(loaded, log_->logger());
    budget_->set_budget_us(config_.frame_budget_us);
    X4MP_CLOG(*log_, Cat::Host, Level::Info, "config reloaded (ui reload)");
    registry_.config_changed_all(*ctx_);
  } catch (...) {
  }
}

void ModHost::write_stash_state() {
  HostStateBlob b;
  b.reload_count = previous_.reload_count + 1;
  b.game_loaded = gates_.game_loaded ? 1 : 0;
  b.universe_ready = gates_.universe_ready ? 1 : 0;
  platform_.stash_set("host.state", &b, sizeof b);
}

void ModHost::teardown_log() noexcept {
  g_violation_log.store(nullptr);
  game::main_thread().set_violation_handler(nullptr);
  log::Logger::set_global(nullptr);
  try {
    log_.reset();  // flushes and joins the writer
  } catch (...) {
  }
}

void ModHost::shutdown() noexcept {
  if (!initialised_ || finished_) return;
  try {
    if (running_) {
      running_ = false;
      registry_.shutdown_all(*ctx_);
      const FrameStats s = budget_->stats();
      log_->raw(Cat::Host, Level::Info,
                "shutdown after " + std::to_string(frame_index_) + " frame(s), over_budget=" + std::to_string(s.over_budget));
      write_stash_state();
    }
  } catch (...) {
  }
  teardown_log();
  finished_ = true;  // one init/shutdown cycle per ModHost; a reload creates a new object (the DLL does)
}

}  // namespace x4mp::host
