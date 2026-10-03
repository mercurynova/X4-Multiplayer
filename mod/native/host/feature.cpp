#include "host/feature.h"

#include <algorithm>
#include <exception>
#include <typeinfo>

namespace x4mp::host {

void FeatureRegistry::add(std::unique_ptr<IFeature> feature) {
  if (!feature) return;
  Entry e;
  e.stats.name = std::string(feature->name());
  e.feature = std::move(feature);
  entries_.push_back(std::move(e));
}

void FeatureRegistry::record_throw(HostContext& ctx, Entry& e, const char* hook, std::string what, bool disable_now) {
  ++e.stats.throws;
  e.stats.last_error = what;
  const bool disable = e.stats.state == FeatureState::Active && (disable_now || e.stats.throws >= kMaxThrows);
  // Logged with the rate limiter's help: a feature that throws every frame is disabled after 3 anyway.
  X4MP_CLOG(ctx.log, Cat::Host, Level::Error, "feature '{}' threw in {} ({}): {}", e.stats.name, hook, e.stats.throws, what);
  if (disable) {
    e.stats.state = FeatureState::Disabled;
    ctx.log.raw(Cat::Host, Level::Error,
                "feature '" + e.stats.name + "' DISABLED after " + std::to_string(e.stats.throws) +
                    (disable_now ? " throw (failed to initialise)" : " throws") + "; last error: " + what);
  }
}

template <class Fn>
void FeatureRegistry::guarded(HostContext& ctx, Entry& e, const char* hook, bool disable_now, bool timed, Fn&& fn) {
  const std::int64_t t0 = timed ? qpc_now_ns() : 0;
  try {
    fn(*e.feature);
  } catch (const std::exception& ex) {
    record_throw(ctx, e, hook, std::string(typeid(ex).name()) + ": " + ex.what(), disable_now);
  } catch (...) {
    record_throw(ctx, e, hook, "non-standard exception", disable_now);
  }
  if (timed) {
    const std::int64_t dt = qpc_now_ns() - t0;
    ++e.stats.frame_calls;
    e.stats.frame_total_ns += dt;
    e.stats.frame_max_ns = std::max(e.stats.frame_max_ns, dt);
  }
}

void FeatureRegistry::init_all(HostContext& ctx) {
  for (auto& e : entries_) {
    if (e.stats.state == FeatureState::Disabled) continue;
    guarded(ctx, e, "on_init", /*disable_now=*/true, false, [&](IFeature& f) { f.on_init(ctx); });
  }
}

void FeatureRegistry::frame_all(HostContext& ctx, const FrameInfo& info) {
  for (auto& e : entries_) {
    if (e.stats.state == FeatureState::Disabled) continue;
    guarded(ctx, e, "on_frame", false, true, [&](IFeature& f) { f.on_frame(ctx, info); });
  }
}

void FeatureRegistry::game_loaded_all(HostContext& ctx) {
  for (auto& e : entries_) {
    if (e.stats.state == FeatureState::Disabled) continue;
    guarded(ctx, e, "on_game_loaded", false, false, [&](IFeature& f) { f.on_game_loaded(ctx); });
  }
}

void FeatureRegistry::universe_ready_all(HostContext& ctx) {
  for (auto& e : entries_) {
    if (e.stats.state == FeatureState::Disabled) continue;
    guarded(ctx, e, "on_universe_ready", false, false, [&](IFeature& f) { f.on_universe_ready(ctx); });
  }
}

void FeatureRegistry::config_changed_all(HostContext& ctx) {
  for (auto& e : entries_) {
    if (e.stats.state == FeatureState::Disabled) continue;
    guarded(ctx, e, "on_config_changed", false, false, [&](IFeature& f) { f.on_config_changed(ctx); });
  }
}

void FeatureRegistry::shutdown_all(HostContext& ctx) {
  // Reverse order. A disabled feature still gets a best-effort on_shutdown (to release threads/sockets); a throw there
  // is logged but cannot change its state any further.
  for (auto it = entries_.rbegin(); it != entries_.rend(); ++it) {
    Entry& e = *it;
    try {
      e.feature->on_shutdown(ctx);
    } catch (const std::exception& ex) {
      ++e.stats.throws;
      e.stats.last_error = ex.what();
      X4MP_CLOG(ctx.log, Cat::Host, Level::Error, "feature '{}' threw in on_shutdown: {}", e.stats.name, ex.what());
    } catch (...) {
      ++e.stats.throws;
      X4MP_CLOG(ctx.log, Cat::Host, Level::Error, "feature '{}' threw a non-standard exception in on_shutdown", e.stats.name);
    }
  }
}

std::vector<FeatureStats> FeatureRegistry::stats() const {
  std::vector<FeatureStats> out;
  out.reserve(entries_.size());
  for (const auto& e : entries_) out.push_back(e.stats);
  return out;
}

bool FeatureRegistry::is_disabled(std::string_view name) const {
  return std::ranges::any_of(entries_, [&](const Entry& e) { return e.stats.name == name && e.stats.state == FeatureState::Disabled; });
}

std::size_t FeatureRegistry::disabled_count() const noexcept {
  return static_cast<std::size_t>(std::ranges::count_if(entries_, [](const Entry& e) { return e.stats.state == FeatureState::Disabled; }));
}

}  // namespace x4mp::host
