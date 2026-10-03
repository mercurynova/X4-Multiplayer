#pragma once
// host/mod_host: the mod host (M2-04). Owns config, log, game adapter, gates, frame loop and the feature registry.
// SDK-free: the DLL entry (main.cpp) wires X4Native events to the on_* methods below, and tests/hostsim drive the very
// same methods through a fake IPlatform / a fake X4NativeAPI.
//
// ---- DLL lifecycle contract (what a fake X4Native host, e.g. M2-01 hostsim, must do) -----------------------------
//  exports (from X4N_EXTENSION / X4N_SHUTDOWN in main.cpp):
//     int  x4native_api_version(void)          -> X4NATIVE_API_VERSION (1)
//     int  x4native_init(X4NativeAPI*)         -> always X4NATIVE_OK, never throws (a refused build stays inert)
//     void x4native_shutdown(void)             -> never throws; idempotent
//  x4native_init uses from the API: extension_path, get_game_version, get_x4native_version, game_types_build,
//     get_game_function, log (+ _ext_log_fn when present, else log), stash_set/get/remove, _ext_id (stash namespace),
//     subscribe/unsubscribe. Anything the fake leaves nullptr must be tolerated by it; our side tolerates a null
//     get_game_function result per export (null-safe adapter) but needs a non-null `subscribe`, `log`, `stash_*`.
//  init sequence: reset main-thread capture -> read stash "host.state" -> resolve paths (portable if the extension
//     folder holds x4mp.json or x4mp.portable) -> load x4mp.json (+ one-shot launch.json) -> open the file log ->
//     banner -> supported-build check (refused: log reason, return OK, subscribe to NOTHING) -> resolve game exports ->
//     install SafeRemove backend -> register + init features -> subscribe to the events below.
//  events delivered to the DLL (all on one thread by default = the "main" thread, B7), by name through subscribe():
//     "on_frame_update"   every UI frame, also in the start menu; data == nullptr (the host measures time itself)
//     "on_game_loaded"    save data ready; clears the universe_ready gate
//     "on_universe_ready" the universe exists; sets the gate, refreshes the player guard, features' on_universe_ready
//     "on_ui_reload"      /reloadui: x4mp.json is re-read, features get on_config_changed
//  reload (save load, hot reload) = x4native_shutdown() then x4native_init() again with the SAME stash. shutdown
//     runs feature on_shutdown in reverse order, writes stash "host.state" {reload_count+1, gates at shutdown},
//     flushes and closes the log. The next init sees it in HostContext::previous (gates are NOT restored: M2-07).
// ---------------------------------------------------------------------------------------------------------------

#include <atomic>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>

#include "core/config/config.h"
#include "game/game_api.h"
#include "host/build_check.h"
#include "host/feature.h"
#include "host/frame_budget.h"
#include "host/host_log.h"
#include "host/paths.h"
#include "host/platform.h"

namespace x4mp::host {

// Defined in feature_list.cpp: the one place that lists the shipped features.
void register_builtin_features(FeatureRegistry& registry);

struct HostOptions {
  std::function<void(FeatureRegistry&)> register_features;  // default: register_builtin_features
  ClockNs clock = nullptr;                                  // frame clock; default QPC (tests inject)
  std::filesystem::path documents_override;                 // tests: pretend Documents is here
  std::int64_t perf_log_interval_ms = 5000;
};

enum class InitResult { Started, Refused };

struct HostStateBlob {  // stash "host.state" (trivially copyable)
  std::uint32_t magic = 0x53483458;  // "X4HS"
  std::uint32_t version = 1;
  std::uint32_t reload_count = 0;
  std::uint8_t game_loaded = 0;
  std::uint8_t universe_ready = 0;
  std::uint8_t pad[2] = {0, 0};
};

class ModHost {
 public:
  explicit ModHost(IPlatform& platform, HostOptions options = {});
  ~ModHost();
  ModHost(const ModHost&) = delete;
  ModHost& operator=(const ModHost&) = delete;

  // Never throws.
  InitResult init() noexcept;
  void on_frame() noexcept;
  void on_game_loaded() noexcept;
  void on_universe_ready() noexcept;
  void on_ui_reload() noexcept;
  void shutdown() noexcept;

  [[nodiscard]] bool running() const noexcept { return running_; }
  [[nodiscard]] bool refused() const noexcept { return refused_; }
  [[nodiscard]] const std::string& refusal_reason() const noexcept { return refusal_reason_; }
  [[nodiscard]] const BuildCheck& build_check() const noexcept { return build_; }

  [[nodiscard]] const Gates& gates() const noexcept { return gates_; }
  [[nodiscard]] const config::Config& config() const noexcept { return config_; }
  [[nodiscard]] const ConfigPaths& paths() const noexcept { return paths_; }
  [[nodiscard]] FeatureRegistry& features() noexcept { return registry_; }
  [[nodiscard]] const FrameBudget& budget() const noexcept { return *budget_; }
  [[nodiscard]] game::GameApi& game() noexcept { return game_; }
  [[nodiscard]] HostLog* log() noexcept { return log_.get(); }
  [[nodiscard]] std::uint64_t frame_count() const noexcept { return frame_index_; }
  [[nodiscard]] std::size_t guard_id_count() const noexcept { return guard_ids_; }

 private:
  void log_header();
  void install_refused_responder() noexcept;
  void drain_pending_events() noexcept;
  void handle_game_loaded() noexcept;
  void handle_universe_ready() noexcept;
  void maybe_log_perf(std::int64_t now_ns);
  void write_stash_state();
  void teardown_log() noexcept;

  IPlatform& platform_;
  HostOptions options_;
  ClockNs clock_;

  config::Config config_;
  struct LaunchOverlay {  // which keys the (consumed, one-shot) launch.json overrode at init
    bool active = false;
    bool server = false, name = false, password = false;
    config::Config snapshot;
  } launch_overlay_;
  ConfigPaths paths_;
  std::unique_ptr<HostLog> log_;
  game::GameApi game_;
  std::unique_ptr<FrameBudget> budget_;
  Gates gates_;
  PreviousRun previous_;
  FeatureRegistry registry_;
  std::unique_ptr<HostContext> ctx_;
  BuildCheck build_;

  bool initialised_ = false;
  bool finished_ = false;
  bool running_ = false;
  bool refused_ = false;
  std::string refusal_reason_;
  std::string extension_path_;

  std::atomic<bool> pending_game_loaded_{false};      // delivered off the frame thread: replayed by the next on_frame
  std::atomic<bool> pending_universe_ready_{false};
  std::uint64_t frame_index_ = 0;
  std::int64_t last_frame_ns_ = 0;
  std::int64_t last_perf_ns_ = 0;
  std::size_t guard_ids_ = 0;
};

}  // namespace x4mp::host
