#pragma once
// host/feature: the feature interface and the exception-containing registry (M2-04, m2-plan 5.2).
//
// A feature is a unit of mod behaviour (join flow, save control, resume, selftest ...) living in
// native/features/<name>/. The host drives it through four required hooks plus two optional ones. Everything runs on
// the main thread (the on_frame_update thread) unless a feature spawns its own (the net thread inside core/session).
//
// Containment: every hook call is wrapped in try/catch(...). A throw is logged (type/what, never a secret), counted,
// and after kMaxThrows (3) throws the feature is DISABLED for the rest of the process: no further hooks except a
// best-effort on_shutdown. A throwing on_init disables the feature immediately (it never became usable). The host and
// the other features keep running. This is the contract that makes "a bug in feature X cannot take X4 down".
//
// How a later task adds a feature: see feature_list.cpp (one #include + one registry line).

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/config/config.h"
#include "game/game_api.h"
#include "host/frame_budget.h"
#include "host/host_log.h"
#include "host/paths.h"
#include "host/platform.h"

namespace x4mp::host {

// Lifecycle gates (m2-plan 2.4): game_loaded is set by on_game_loaded (save data valid, gamestart MD not run),
// universe_ready by on_universe_ready (the only point where touching universe objects is safe). Both are cleared
// when a new load starts (on_game_loaded) and at init. Features must not touch universe objects while
// !universe_ready.
struct Gates {
  bool game_loaded = false;
  bool universe_ready = false;
  std::uint64_t universe_epoch = 0;  // incremented on every on_universe_ready (this process incarnation)
  // M3-13: true when the current universe-ready gate was opened by the host after /reloadui (the SAME universe, no save was loaded);
  // false after a real on_universe_ready (a save load or a fresh start). The janitor sweeps only after a save load.
  bool universe_ready_after_reload = false;
};

// Persisted by the host in the stash at shutdown (key "host.state") and readable after a reload; the host does NOT
// restore gates from it (a reload may land in a different universe: epoch rules are M2-07's).
struct PreviousRun {
  bool present = false;          // a stash entry from an earlier incarnation exists
  std::uint32_t reload_count = 0;  // how many shutdown+init cycles preceded this init
  bool game_loaded_at_shutdown = false;
  bool universe_ready_at_shutdown = false;
};

struct HostContext {
  const config::Config& config;   // current config (updated in place on reload; read it each time)
  game::GameApi& game;            // null-safe adapter; main thread only
  HostLog& log;
  IPlatform& platform;            // stash, native log, version info
  FrameBudget& budget;
  const Gates& gates;
  const PreviousRun& previous;
  std::string_view extension_path;
  const ConfigPaths* paths = nullptr;  // where x4mp.json / logs live (M2-06); set by ModHost, null in bare test contexts
};

struct FrameInfo {
  std::uint64_t frame_index = 0;
  double delta_s = 0.0;                    // wall-clock since the previous frame (host measured)
  std::optional<double> game_time;         // GetCurrentGameTime, only once game_loaded
  bool game_paused = false;                // only meaningful once game_loaded
  bool game_loaded = false;
  bool universe_ready = false;
};

class IFeature {
 public:
  virtual ~IFeature() = default;
  [[nodiscard]] virtual std::string_view name() const noexcept = 0;  // stable id used in logs / stats

  virtual void on_init(HostContext&) {}
  // Every frame, in the start menu too. Check ctx.gates / info.universe_ready before touching the universe. Use
  // ctx.budget.remaining_ns() to slice work.
  virtual void on_frame(HostContext&, const FrameInfo&) {}
  virtual void on_universe_ready(HostContext&) {}
  virtual void on_shutdown(HostContext&) {}
  // Optional extras.
  virtual void on_game_loaded(HostContext&) {}
  virtual void on_config_changed(HostContext&) {}  // after x4mp.json was re-read on on_ui_reload
};

enum class FeatureState { Active, Disabled };

struct FeatureStats {
  std::string name;
  FeatureState state = FeatureState::Active;
  int throws = 0;
  std::uint64_t frame_calls = 0;
  std::int64_t frame_total_ns = 0;
  std::int64_t frame_max_ns = 0;
  std::string last_error;
};

class FeatureRegistry {
 public:
  static constexpr int kMaxThrows = 3;

  // Add before init_all(). Registration order = hook order; shutdown runs in reverse.
  void add(std::unique_ptr<IFeature> feature);
  [[nodiscard]] std::size_t size() const noexcept { return entries_.size(); }

  void init_all(HostContext& ctx);
  void frame_all(HostContext& ctx, const FrameInfo& info);
  void game_loaded_all(HostContext& ctx);
  void universe_ready_all(HostContext& ctx);
  void config_changed_all(HostContext& ctx);
  void shutdown_all(HostContext& ctx);

  [[nodiscard]] std::vector<FeatureStats> stats() const;
  [[nodiscard]] bool is_disabled(std::string_view name) const;
  [[nodiscard]] std::size_t disabled_count() const noexcept;

 private:
  struct Entry {
    std::unique_ptr<IFeature> feature;
    FeatureStats stats;
  };
  template <class Fn>
  void guarded(HostContext& ctx, Entry& e, const char* hook, bool disable_now, bool timed, Fn&& fn);
  void record_throw(HostContext& ctx, Entry& e, const char* hook, std::string what, bool disable_now);

  std::vector<Entry> entries_;
};

}  // namespace x4mp::host
