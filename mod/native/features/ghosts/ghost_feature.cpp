#include "features/ghosts/ghost_feature.h"

#include <algorithm>
#include <format>
#include <utility>

#include "features/authority/capture_ingest.h"
#include "features/diag/diag_hub.h"
#include "features/diag/knowledge_feature.h"
#include "features/ghosts/ghost_hub.h"
#include "features/ghosts/local_suppression.h"
#include "features/join/platform_stash.h"
#include "features/selfship/galaxy_map.h"
#include "features/selfship/selfship_hub.h"
#include "game/safe_remove.h"
#include "nlohmann/json.hpp"

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

std::uint32_t GhostHub::net_id_of_local(std::uint64_t local_id) const noexcept {
  if (!core_ || local_id == 0) return 0;
  const auto* entry = core_->driver.registries().netmap.find_local(local_id);
  return entry != nullptr ? entry->net_id : 0;
}

GhostHub& ghost_hub() noexcept {
  static GhostHub hub;
  return hub;
}

}  // namespace ghosts

GhostFeature::GhostFeature() = default;

GhostFeature::~GhostFeature() { ghosts::ghost_hub().detach(); }

void ScanInbox::push(std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= 64) items.erase(items.begin());  // a stuck frame thread must not grow this without bound
  items.push_back(std::move(text));
}

std::vector<std::string> ScanInbox::take() {
  const std::lock_guard lock(mutex);
  return std::exchange(items, {});
}

void GhostFeature::on_init(host::HostContext& ctx) {
  host::HostLog* log = &ctx.log;
  ghosts::GhostConfig gcfg{};
  gcfg.inert_once = ctx.config.diag.avatars_inert_once;
  gcfg.activate_streamed = ctx.config.diag.ghost_attention_activate;  // M4-29 attention A/B (`activate`; default `native`)
  gcfg.hints_off = ctx.config.diag.hints_off;                  // M3-34
  gcfg.hints_all_speeds = ctx.config.diag.hints_all_speeds;
  gcfg.engine_guard = !ctx.config.diag.engine_guard_off;
  core_ = std::make_unique<ghosts::GhostCore>(gcfg, [log](int level, const std::string& text) {
    log->raw(Cat::Ghost, to_level(level), text);
  });
  auto get = [&ctx](const char* name) { return ctx.platform.get_game_function(name); };
  api_ = std::make_unique<game::GhostsApi>(ctx.game, get);
  world_ = std::make_unique<ghosts::GameGhostWorld>(*api_, ctx.platform);
  world_->set_dress_skip_radar_known(ctx.config.diag.dress_no_radar_no_known);
  if (ctx.config.diag.avatars_inert_once) {
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn, "ghosts: diag.avatars_inert_once is set: ActivateObject(false) runs once per ghost spawn, never re-asserted (M3-28 experiment)");
  }
  if (ctx.config.diag.ghost_attention_activate) {
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn,
              "ghosts: diag.ghost_attention_activate is set: the attention policy is `activate` (ActivateObject(true) right after make_inert); "
              "the default is `native` (M4-29 A/B)");
  }
  if (ctx.config.diag.hints_off || ctx.config.diag.hints_all_speeds || ctx.config.diag.engine_guard_off) {
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn, "ghosts: diag switches set (M3-34 experiment): hints_off={} hints_all_speeds={} engine_guard_off={}", ctx.config.diag.hints_off,
              ctx.config.diag.hints_all_speeds, ctx.config.diag.engine_guard_off);
  }
  if (ctx.config.diag.dress_no_radar_no_known) {
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn, "ghosts: diag.dress_no_radar_no_known is set: the ghost dress skips forced radar visibility and set_known (M3-28 experiment)");
  }
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

  // M4-13: the suppression pass. It asks MD for a sector's local ships over the same scan pair the authority uses (x4mp.capture_request ->
  // x4mp.capture_scan); the answers arrive as platform events and are drained on the frame thread.
  suppression_ = std::make_unique<ghosts::LocalSuppression>();
  scan_inbox_ = std::make_shared<ScanInbox>();
  {
    auto inbox = scan_inbox_;
    (void)ctx.platform.subscribe_event("x4mp.capture_scan", [inbox](std::string_view text) {
      if (text.size() <= 256 * 1024) inbox->push(std::string(text));
    });
  }
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

  if (ctx.config.diag.ghosts_off) {  // M3-23 diagnostic: the client spawns no ghosts
    if (!warned_diag_off_) {
      warned_diag_off_ = true;
      X4MP_CLOG(ctx.log, Cat::Ghost, Level::Warn, "ghosts: diag.ghosts_off is set: no ghosts are spawned on this node");
    }
    return;
  }
  if (ghosts::ghost_hub().spawn_hold()) return;  // M3-12: the client takeover is not Done yet

  if (c.driver.has_pending_adoption()) c.driver.adopt(*stash_, now_server);

  const auto* map = selfship::selfship_hub().map();
  if ((map == nullptr || !map->ready()) && c.driver.size() > 0 && !warned_no_map_) {
    warned_no_map_ = true;
    X4MP_CLOG(ctx.log, Cat::Ghost, Level::Info, "ghosts: waiting for the sector map (selfship feature, MD collector)");
  }
  const auto t0 = std::chrono::steady_clock::now();
  // M4-16: the link is gone (a reconnect clears the own-ship link): keep every ghost exactly where it is, unmoving, and resume without a
  // respawn when the link comes back.
  c.driver.set_frozen(!selfship::selfship_hub().linked());
  // M4-17: tell the driver which hull is already ours, so a passenger sitting in another player's ship (which own_ship_tracker
  // reports as the passenger's own ship) does not become a second copy of that ship on the other side.
  c.driver.set_own_ship_net_id(selfship::selfship_hub().own_net_id());
  const ghosts::FrameStats fs = c.driver.frame(now_server);
  // M4-13/M4-15: local NPC suppression for the sectors the server has closed for us, and the 1 Hz late-spawn sweep.
  pump_suppression(ctx, c, now_server);
  const double cost_us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count();
  last_visible_ = fs.visible;
  if (!first_spawn_probed_ && c.driver.counters().spawned > 0) {  // M3-23: the knowledge probe after the first ghost spawn
    first_spawn_probed_ = true;
    knowledge_probe("after first ghost spawn");
  }
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

void GhostFeature::pump_suppression(host::HostContext& ctx, ghosts::GhostCore& c, std::int64_t now_us) {
  if (!suppression_ || !scan_inbox_) return;
  const auto* map = selfship::selfship_hub().map();

  // M4-13: the server closed these sectors for this client: every entity of them has been sent as EntitySpawn, so our own copies of their
  // ships may go. The marker is what keeps us from suppressing a ship the authority has not streamed yet (it would vanish for a moment).
  for (const auto& closed : c.take_sector_closed()) suppression_->on_sector_closed(closed.sector);
  // M4-15: only sectors still in interest are swept; the ids we removed are remembered, so a re-entry removes nothing twice.
  suppression_->on_interest(c.interest_sectors());

  self_component_ = api_ ? api_->local_ship() : 0;
  const auto skip = [this](std::uint64_t component) {
    if (component == 0) return true;
    if (self_component_ != 0 && component == self_component_) return true;  // the player's own ship is never suppressed
    return ghosts::ghost_hub().is_ghost_local(component);                   // our ghosts are the authority's ships, not local ones
  };

  for (const auto& text : scan_inbox_->take()) {
    // The platform event carries the bridge envelope ({"v":1,"data":"S;..."}), not the bare batch the parser reads (M4-13: every scan
    // used to be counted as bad here, so suppression never ran).
    const auto batch = auth::parse_scan_batch(auth::scan_payload(text));
    if (!batch) {
      ++scan_bad_;
      continue;
    }
    const std::uint16_t sector = map != nullptr ? map->index_of_macro(batch->sector_macro) : 0;
    if (sector == 0) {
      ++scan_bad_;  // the galaxy map does not know the macro yet; the next sweep asks again
      continue;
    }
    suppression_->on_scan(sector, batch->rows, skip);
  }

  suppression_->frame(now_us, [&ctx, map](std::uint16_t sector) {
    if (map == nullptr) return;
    const std::string_view macro = map->macro_of(sector);
    if (macro.empty()) return;
    nlohmann::json j;
    j["v"] = 1;
    j["sector"] = std::string(macro);
    (void)ctx.platform.raise_lua("x4mp.capture_request", j.dump());
  }, [](std::uint64_t component) { (void)game::safe_remove(component); });
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
  // M4-30: the 30 s budget line quotes this; publish it even when the ghost line below is skipped.
  ghosts::ghost_hub().set_suppressed(suppression_ ? suppression_->total_suppressed() : 0);
  if (c.driver.size() == 0 && c.counters.replication == 0) return;
  // Two lines: a message is cut at log::kMaxLineChars (240, core/log/log.h), and this used to be one line that ended mid-word at
  // spawn_failed, so unmapped/unresolved/spawn_near were never readable live (measured 232 chars, 272 with the prefix). The first
  // line keeps the field order tools/session4/sync-report.ps1 parses; the second carries the health counters, spawn_near included
  // (M4-23, criterion 6: it must stay 0 across a highway transit).
  ctx.log.raw(Cat::Ghost, Level::Info,
              std::format("[sync] ghosts tracked={} visible={} spawned={} respawned={} hidden={} removed={} adopted={} hints={} rep_msgs={} rep_entries={} frame_p95_us={:.0f} "
                          "frame_max_us={:.0f} clk_ms={} bias_ms={}",
                          c.driver.size(), last_visible_, k.spawned, k.respawned, k.hidden, k.removed, k.adopted, k.hints_sent, c.counters.replication,
                          c.counters.replication_entries, frame_cost_us_.percentile(0.95), frame_cost_max_us_, c.clock_offset_us / 1000, c.stream_clock.bias_us() / 1000));
  ctx.log.raw(Cat::Ghost, Level::Info,
              std::format("[sync] ghosts health: rep_bad={} spawn_failed={} unmapped={} unresolved={} spawn_near={}", c.counters.replication_bad, k.spawn_failed,
                          k.unmapped_frames, c.counters.spawn_unresolved, k.spawn_near));
  // M4-13: the suppression pass, one line, only when it has done something.
  if (suppression_) {
    const ghosts::LocalSuppression::Stats& s = suppression_->stats();
    if (s.requests != 0 || s.removed != 0) {
      ctx.log.raw(Cat::Ghost, Level::Info,
                  std::format("[sync] suppression: suppressed={} requests={} scans={} rows={} queued={} removed={} skipped_player={} skipped_mission={} skipped_known={} "
                              "skipped_dup={} bad={}",
                              suppression_->total_suppressed(), s.requests, s.scans, s.rows, s.queued, s.removed, s.skipped_player, s.skipped_mission,
                              s.skipped_known, s.skipped_dup, scan_bad_));
    }
  }
  frame_cost_us_.clear();
  frame_cost_max_us_ = 0;
  // M3-34: the engine guard's totals, one line, only when the totals changed (the engine touched one of our objects)
  if (k.engine_moved + k.engine_sector + k.engine_highway + k.guard_zone_skip != last_guard_hits_) {
    last_guard_hits_ = k.engine_moved + k.engine_sector + k.engine_highway + k.guard_zone_skip;
    ctx.log.raw(Cat::Ghost, Level::Info,
                std::format("[sync] engine guard: ghosts moved by the game={} into another sector={} in a highway context={} (reads {}), skipped outside your zone={}",
                            k.engine_moved, k.engine_sector, k.engine_highway, k.guard_reads, k.guard_zone_skip));
  }
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
