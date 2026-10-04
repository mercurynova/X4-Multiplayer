#include "features/ghosts/ghost_feature.h"

#include <algorithm>
#include <format>
#include <utility>

#include "features/diag/diag_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "features/join/platform_stash.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
constexpr std::int64_t kSaveIntervalUs = 1'000'000;
constexpr std::int64_t kReportIntervalUs = 5'000'000;

std::int64_t steady_us() {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

Level to_level(int l) {
  switch (l) {
    case 0: return Level::Debug;
    case 2: return Level::Warn;
    case 3: return Level::Error;
    default: return Level::Info;
  }
}
}  // namespace

namespace ghosts {

void GhostHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_player_id) {
  if (core_) ++core_->counters.frames_seen[type];
  if (!core_ || !GhostCore::wants(type)) return;
  if (diag_hub().role() == NodeRole::Authority) return;  // the authority's players' ships are its avatars (M3-11), not ghosts
  core_->on_frame_message(type, payload, self_player_id, steady_us() + core_->clock_offset_us);
}

void GhostHub::session_ended() {
  if (core_) core_->driver.remove_all();
}

bool GhostHub::is_ghost_local(std::uint64_t local_id) const noexcept {
  return core_ && local_id != 0 && core_->driver.registries().ghosts.contains_local(local_id);
}

GhostHub& ghost_hub() noexcept {
  static GhostHub hub;
  return hub;
}

}  // namespace ghosts

GhostFeature::GhostFeature() = default;

GhostFeature::~GhostFeature() { ghosts::ghost_hub().detach(); }

void GhostFeature::on_init(host::HostContext& ctx) {
  host::HostLog* log = &ctx.log;
  core_ = std::make_unique<ghosts::GhostCore>(ghosts::GhostConfig{}, [log](int level, const std::string& text) {
    log->raw(Cat::Ghost, to_level(level), text);
  });
  auto get = [&ctx](const char* name) { return ctx.platform.get_game_function(name); };
  api_ = std::make_unique<game::GhostsApi>(ctx.game, get);
  world_ = std::make_unique<ghosts::GameGhostWorld>(*api_, ctx.platform);
  core_->driver.bind_world(world_.get());
  stash_ = std::make_unique<join::PlatformStash>(ctx.platform, "");

  // What a reload must not lose: the string table (the server does not replay it on a resume) and the ghost records.
  if (const auto text = stash_->get("ghost.strings")) {
    if (!core_->strings.from_text(*text)) X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn, "ghosts: the stashed string table is unusable; waiting for the next one");
  }
  std::size_t restored = 0;
  if (const auto meta = stash_->get("ghost.meta")) {
    if (core_->driver.restore(*meta, 0)) restored = core_->driver.size();
    else X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn, "ghosts: the stashed ghost records are unusable; the registry blob is left to the janitor");
  }
  last_saved_strings_version_ = core_->strings.version();

  ghosts::ghost_hub().attach(core_.get());
  X4MP_CLOG(ctx.log, Cat::Ghost, Level::Info, "ghosts: init (restored {} ghost records and {} strings from the stash, spawn export {}, move export {})",
            restored, core_->strings.size(), ctx.game.fns().SpawnObjectAtPos2 ? "yes" : "NO", ctx.game.fns().SetObjectSectorPos ? "yes" : "NO");
}

void GhostFeature::on_game_loaded(host::HostContext& ctx) {
  if (!core_ || !universe_seen_) return;
  // A new universe INSIDE this DLL incarnation (X4Native normally re-inits the DLL on a load, then this is a no-op): ids are stale.
  universe_seen_ = false;
  core_->driver.forget_local_ids();
  X4MP_CLOG(ctx.log, Cat::Ghost, Level::Info, "ghosts: a new universe was loaded: local ids dropped");
}

void GhostFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  if (!core_) return;
  if (diag_hub().role() == NodeRole::Authority) return;
  if (!info.universe_ready) return;
  ghosts::GhostCore& c = *core_;
  universe_seen_ = true;
  const std::int64_t steady = steady_us();

  features::NetSample ns;
  if (diag_hub().sample_net(ns) && ns.clock_offset_us != 0) c.clock_offset_us = ns.clock_offset_us;

  if (c.clock_offset_us == 0) {  // no server clock yet: the interpolators cannot place anything
    if (!warned_no_clock_ && c.driver.size() > 0) {
      warned_no_clock_ = true;
      X4MP_CLOG(ctx.log, Cat::Ghost, Level::Info, "ghosts: waiting for the first clock sample from the server");
    }
    return;
  }
  // The stream clock (ghost_core.h StreamClock): the Replication time base is the authority's, not necessarily the server clock.
  const std::int64_t now_server = steady + c.clock_offset_us - c.stream_clock.bias_us();

  if (c.driver.has_pending_adoption()) c.driver.adopt(*stash_, now_server);

  const auto* map = selfship::selfship_hub().map();
  if ((map == nullptr || !map->ready()) && c.driver.size() > 0 && !warned_no_map_) {
    warned_no_map_ = true;
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Info, "ghosts: waiting for the sector map (selfship feature, MD collector)");
  }
  const auto t0 = std::chrono::steady_clock::now();
  const ghosts::FrameStats fs = c.driver.frame(now_server);
  const double cost_us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count();
  last_visible_ = fs.visible;
  if (fs.tracked > 0) {  // an idle node (no ghosts) must not dilute the percentile
    frame_cost_us_.push(cost_us);
    frame_cost_max_us_ = std::max(frame_cost_max_us_, cost_us);
  }

  if ((c.driver.dirty() || c.strings.version() != last_saved_strings_version_) && steady - last_save_us_ >= kSaveIntervalUs) {
    last_save_us_ = steady;
    save_state(ctx);
  }
  if (steady - last_report_us_ >= kReportIntervalUs) {
    if (last_report_us_ != 0) report(ctx, now_server);
    last_report_us_ = steady;
  }
}

void GhostFeature::save_state(host::HostContext&) {
  if (!core_ || !stash_) return;
  core_->driver.save(*stash_);
  stash_->put("ghost.strings", core_->strings.to_text());
  last_saved_strings_version_ = core_->strings.version();
  core_->driver.mark_clean();
}

void GhostFeature::report(host::HostContext& ctx, std::int64_t) {
  ghosts::GhostCore& c = *core_;
  for (const std::string& line : c.driver.take_sync_lines()) ctx.log.raw(Cat::Ghost, Level::Info, line);
  const ghosts::DriverCounters& k = c.driver.counters();
  if (c.driver.size() == 0 && c.counters.replication == 0) return;
  ctx.log.raw(Cat::Ghost, Level::Info,
              std::format("[sync] ghosts tracked={} visible={} spawned={} respawned={} shown_again={} hidden={} removed={} adopted={} places={} sector_changes={} hints={} "
                          "unmapped_frames={} spawn_failed={} frame_p95_us={:.0f} frame_max_us={:.0f} rep_msgs={} rep_entries={} rep_bad={} strings={} string_msgs={} spawn_msgs={} spawn_unresolved={}",
                          c.driver.size(), last_visible_, k.spawned, k.respawned, k.shown, k.hidden, k.removed, k.adopted, k.places, k.sector_changes, k.hints_sent,
                          k.unmapped_frames, k.spawn_failed, frame_cost_us_.percentile(0.95), frame_cost_max_us_, c.counters.replication,
                          c.counters.replication_entries, c.counters.replication_bad, c.strings.size(), c.counters.strings, c.counters.spawn_messages,
                          c.counters.spawn_unresolved));
  frame_cost_us_.clear();
  frame_cost_max_us_ = 0;
}

void GhostFeature::on_shutdown(host::HostContext& ctx) {
  ghosts::ghost_hub().detach();
  if (core_ && stash_) {
    std::string seen;
    for (const auto& [t, n] : core_->counters.frames_seen) seen += std::format("{:#06x}x{} ", t, n);
    save_state(ctx);  // no game calls: the stash must carry the ghosts across the reload
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Info, "ghosts: shutdown, {} ghost records and {} strings kept in the stash (string msgs {}, spawn msgs {}, bad frames {}, frame types seen {})", core_->driver.size(),
              core_->strings.size(), core_->counters.strings, core_->counters.spawn_messages, core_->counters.bad_frames, seen);
  }
}

}  // namespace x4mp::features
