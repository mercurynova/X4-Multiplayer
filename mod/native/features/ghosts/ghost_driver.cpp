#include "features/ghosts/ghost_driver.h"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <format>

namespace x4mp::features::ghosts {

namespace {
constexpr int kDebug = 0, kInfo = 1, kWarn = 2, kError = 3;
constexpr std::string_view kMetaKey = "ghost.meta";
constexpr std::string_view kMetaHeader = "x4gm 1";

ghost::InterpolatorConfig interp_config(const GhostConfig& c) {
  ghost::InterpolatorConfig i;
  i.stale_us = c.stale_us;
  return i;
}

std::string clean_field(std::string_view s) {  // the meta format is tab separated, one record per line
  std::string out(s);
  for (char& c : out) {
    if (c == '\t' || c == '\n' || c == '\r') c = ' ';
  }
  return out;
}

std::string dash(std::string_view s) { return s.empty() ? std::string("-") : clean_field(s); }
std::string undash(std::string_view s) { return s == "-" ? std::string() : std::string(s); }

std::vector<std::string_view> split_tabs(std::string_view line) {
  std::vector<std::string_view> out;
  std::size_t pos = 0;
  while (true) {
    const auto t = line.find('\t', pos);
    if (t == std::string_view::npos) {
      out.push_back(line.substr(pos));
      break;
    }
    out.push_back(line.substr(pos, t - pos));
    pos = t + 1;
  }
  return out;
}

template <class T>
bool parse_num(std::string_view s, T& out) {
  const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
  return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}

bool pose_differs(const Pose& a, const Pose& b, double eps_m, double eps_rad) {
  return std::fabs(a.pos.x - b.pos.x) > eps_m || std::fabs(a.pos.y - b.pos.y) > eps_m || std::fabs(a.pos.z - b.pos.z) > eps_m ||
         std::fabs(a.rot.yaw - b.rot.yaw) > eps_rad || std::fabs(a.rot.pitch - b.rot.pitch) > eps_rad ||
         std::fabs(a.rot.roll - b.rot.roll) > eps_rad;
}
}  // namespace

GhostDriver::GhostDriver(GhostConfig cfg, LogFn log) : cfg_(cfg), log_(std::move(log)), streams_(interp_config(cfg)) { ghosts_.reserve(16); hint_batch_.reserve(16); }

void GhostDriver::log(int level, const std::string& text) const {
  if (log_) log_(level, text);
}

std::string GhostDriver::make_label(std::string_view name, std::uint16_t controller, std::uint16_t player_id) {
  std::string base(name);
  const auto trim = [&] {
    while (!base.empty() && base.front() == ' ') base.erase(base.begin());
    while (!base.empty() && base.back() == ' ') base.pop_back();
  };
  trim();
  if (base.rfind(kNamePrefix, 0) == 0) base.erase(0, kNamePrefix.size());
  trim();
  if (base.size() >= kOfflineSuffix.size() && base.compare(base.size() - kOfflineSuffix.size(), kOfflineSuffix.size(), kOfflineSuffix) == 0) {
    base.erase(base.size() - kOfflineSuffix.size());
  }
  trim();
  if (base.empty()) base = "Player" + std::to_string(player_id);
  std::string label(kNamePrefix);
  label += base;
  if (controller == 0) label += kOfflineSuffix;
  return label;
}

GhostDriver::Ghost* GhostDriver::find(std::uint32_t net_id) noexcept {
  for (auto& g : ghosts_) {
    if (g.info.net_id == net_id) return &g;
  }
  return nullptr;
}
const GhostDriver::Ghost* GhostDriver::find(std::uint32_t net_id) const noexcept {
  for (const auto& g : ghosts_) {
    if (g.info.net_id == net_id) return &g;
  }
  return nullptr;
}

// ---------------------------------------------------------------------------------------------------------------------
// messages
// ---------------------------------------------------------------------------------------------------------------------
bool GhostDriver::on_spawn(const SpawnInfo& info, std::int64_t now_us, const std::optional<InitialState>& state) {
  if (info.net_id == 0) return false;
  const bool own = self_ != 0 && (info.controller == self_ || (info.controller == 0 && info.player_id == self_));
  if (own) {
    if (find(info.net_id)) erase_record(info.net_id);  // the server never replicates our own ship; a leftover ghost of it is wrong
    return false;
  }
  Ghost* g = find(info.net_id);
  const std::string label = make_label(info.name, info.controller, info.player_id);
  if (!g) {
    ghosts_.emplace_back();
    g = &ghosts_.back();
    g->first_seen_us = now_us;
    g->spawn_not_before_us = 0;
    streams_.ensure(info.net_id);
    g->label = label;
    g->info = info;
    g->hidden_by_server = false;
    dirty_ = true;
    log(kInfo, std::format("ghost record: net={} player={} controller={} team={} macro={} owner={} label='{}'", info.net_id, info.player_id, info.controller,
                           info.team, info.macro, info.owner, label));
  } else {
    // A refresh (the server re-sends every spawn after a resume). Keep the object; update what may have changed.
    const bool relabel = g->label != label;
    const bool reown = g->info.owner != info.owner && !info.owner.empty();
    g->info.player_id = info.player_id;
    g->info.controller = info.controller;
    g->info.team = info.team;
    if (!info.macro.empty()) g->info.macro = info.macro;
    if (!info.name.empty()) g->info.name = info.name;
    if (reown) {
      g->info.owner = info.owner;
      g->owner_dirty = true;
    }
    if (relabel) {
      g->label = label;
      g->label_dirty = true;
    }
    g->hidden_by_server = false;
    if (relabel || reown) dirty_ = true;
  }
  g->initial = state;
  if (state) g->initial_used = false;
  return true;
}

void GhostDriver::on_change(std::uint32_t net_id, std::optional<std::string> name, std::optional<std::uint16_t> controller, std::optional<std::string> owner,
                            std::optional<std::uint16_t> team) {
  Ghost* g = find(net_id);
  if (!g) return;
  if (name) g->info.name = *name;
  if (controller) g->info.controller = *controller;
  if (team) g->info.team = *team;
  if (owner && !owner->empty() && *owner != g->info.owner) {
    g->info.owner = *owner;
    g->owner_dirty = true;
  }
  const std::string label = make_label(g->info.name, g->info.controller, g->info.player_id);
  if (label != g->label) {
    g->label = label;
    g->label_dirty = true;
    log(kInfo, std::format("ghost relabelled: net={} label='{}'", net_id, label));
  }
  dirty_ = true;
}

void GhostDriver::on_despawn(std::uint32_t net_id, DespawnKind kind) {
  Ghost* g = find(net_id);
  if (!g) return;
  if (kind == DespawnKind::HideOnly) {
    g->hidden_by_server = true;
    const ghost::Interpolator* in = streams_.find(net_id);
    g->hidden_at_newest_us = in ? in->newest_sample_us() : 0;
    return;
  }
  log(kInfo, std::format("ghost removed by the server: net={} label='{}'", net_id, g->label));
  erase_record(net_id);
}

void GhostDriver::remove_all() { remove_all_ = true; }

void GhostDriver::erase_record(std::uint32_t net_id) {
  const auto it = std::ranges::find_if(ghosts_, [&](const Ghost& g) { return g.info.net_id == net_id; });
  if (it == ghosts_.end()) return;
  if (it->local_id != 0) remove_queue_.push_back(it->local_id);
  regs_.ghosts.remove(net_id);
  regs_.netmap.unbind_net(net_id);
  streams_.erase(net_id);
  ghosts_.erase(it);
  dirty_ = true;
}

// ---------------------------------------------------------------------------------------------------------------------
// registry helpers
// ---------------------------------------------------------------------------------------------------------------------
void GhostDriver::register_ghost(Ghost& g) {
  regs_.ghosts.add({g.info.net_id, g.info.player_id, g.local_id, g.info.team});
  regs_.netmap.bind(g.info.net_id, g.local_id, ghost::NetKind::Ghost, g.info.player_id);
  dirty_ = true;
}

void GhostDriver::unregister_ghost(Ghost& g) {
  regs_.ghosts.remove(g.info.net_id);
  regs_.netmap.unbind_net(g.info.net_id);
  dirty_ = true;
}

void GhostDriver::hide(Ghost& g, const char* why) {
  if (g.local_id == 0) return;
  const std::uint64_t id = g.local_id;
  const RemoveOutcome r = world_ ? world_->remove(id) : RemoveOutcome::Failed;
  if (r == RemoveOutcome::Blocked) {
    ++counters_.blocked_removals;
    log(kError, std::format("ghost hide: the removal of {} was refused by the player guard; forgetting the id (net={})", id, g.info.net_id));
  } else if (r == RemoveOutcome::Failed) {
    ++counters_.remove_failed;
    log(kWarn, std::format("ghost hide: the game removal of {} did not run (net={}); the object may stay behind", id, g.info.net_id));
  } else {
    ++counters_.removed;
  }
  ++counters_.hidden;
  log(kInfo, std::format("ghost hidden ({}): net={} label='{}' id={}", why, g.info.net_id, g.label, id));
  unregister_ghost(g);
  g.local_id = 0;
  g.sector_id = 0;
  g.have_prev = false;
  g.left_by_hide = true;
}

void GhostDriver::drop_object(Ghost& g, const char* why) {
  ++counters_.invalid_dropped;
  log(kWarn, std::format("ghost object gone ({}): net={} label='{}' id={}; it respawns from the next sample", why, g.info.net_id, g.label, g.local_id));
  unregister_ghost(g);
  g.local_id = 0;
  g.sector_id = 0;
  g.have_prev = false;
}

void GhostDriver::process_removals() {
  if (!world_) {
    remove_queue_.clear();
    return;
  }
  if (remove_all_) {
    remove_all_ = false;
    for (auto& g : ghosts_) {
      if (g.local_id != 0) remove_queue_.push_back(g.local_id);
    }
    ghosts_.clear();
    regs_.clear();
    streams_.clear();
    dirty_ = true;
  }
  for (const std::uint64_t id : remove_queue_) {
    const RemoveOutcome r = world_->remove(id);
    if (r == RemoveOutcome::Removed) ++counters_.removed;
    else if (r == RemoveOutcome::Blocked) ++counters_.blocked_removals;
    else ++counters_.remove_failed;
  }
  remove_queue_.clear();
}

bool GhostDriver::note_respawn(Ghost& g, std::int64_t now_us) {
  // At most respawn_burst respawns per respawn_window_us; beyond that wait respawn_backoff_us (something keeps killing the ghost).
  int recent = 0;
  for (int i = 0; i < g.respawn_n && i < 8; ++i) {
    if (now_us - g.respawn_times[i] <= cfg_.respawn_window_us) ++recent;
  }
  if (recent >= cfg_.respawn_burst) {
    g.spawn_not_before_us = now_us + cfg_.respawn_backoff_us;
    log(kWarn, std::format("ghost respawn throttled: net={} label='{}' ({} respawns in {} s); waiting {} s", g.info.net_id, g.label, recent,
                           cfg_.respawn_window_us / 1'000'000, cfg_.respawn_backoff_us / 1'000'000));
    return false;
  }
  g.respawn_times[g.respawn_n % 8] = now_us;
  ++g.respawn_n;
  return true;
}

bool GhostDriver::try_spawn(Ghost& g, std::int64_t now_us, std::uint64_t sector_id, const Pose& pose) {
  if (g.info.macro.empty() || g.info.owner.empty()) {
    g.spawn_not_before_us = now_us + cfg_.spawn_retry_us;
    ++counters_.spawn_failed;
    log(kError, std::format("ghost spawn impossible: net={} has no macro/owner (macro='{}' owner='{}')", g.info.net_id, g.info.macro, g.info.owner));
    return false;
  }
  const FactionPresence presence = world_->faction(g.info.owner);
  if (presence == FactionPresence::NotReady) {
    g.spawn_not_before_us = now_us + 250'000;
    return false;
  }
  if (presence == FactionPresence::Missing) {
    g.spawn_not_before_us = now_us + cfg_.spawn_retry_us;
    ++counters_.faction_missing;
    log(kError, std::format("ghost spawn refused: faction '{}' does not exist in this game (net={}); the team libraries are missing?", g.info.owner, g.info.net_id));
    return false;
  }
  if (world_->local_ship_within(sector_id, pose.pos, cfg_.spawn_clearance_m)) {
    if (g.defer_since_us == 0) {
      g.defer_since_us = now_us;
      log(kInfo, std::format("ghost spawn waits: net={} would appear within {:.0f} m of your ship", g.info.net_id, cfg_.spawn_clearance_m));
    }
    ++counters_.deferred_clearance;
    g.spawn_not_before_us = now_us + 250'000;  // not a game query every frame
    return false;
  }
  g.defer_since_us = 0;
  const bool respawn = g.ever_shown && !g.left_by_hide;  // a re-show after a deliberate hide is not a respawn
  if (respawn && !note_respawn(g, now_us)) return false;
  const std::uint64_t id = world_->spawn(g.info.macro, sector_id, pose, g.info.owner);
  if (id == 0) {
    ++counters_.spawn_failed;
    g.spawn_not_before_us = now_us + cfg_.spawn_retry_us;
    log(kWarn, std::format("ghost spawn failed: net={} macro={} owner={} sector_id={}", g.info.net_id, g.info.macro, g.info.owner, sector_id));
    return false;
  }
  world_->make_inert(id);
  world_->dress(id, g.label, cfg_.min_hull_percent);
  g.idcode = world_->id_code(id);
  g.local_id = id;
  g.restored_id = 0;
  g.sector_id = sector_id;
  g.last_pose = pose;
  g.prev_pose = pose;
  g.have_prev = false;
  g.last_set_us = now_us;
  g.last_hint_us = now_us;
  g.last_inert_us = now_us;
  g.last_valid_us = now_us;
  g.hint_was_zero = true;
  g.dressed_ok = false;
  g.dress_retries = 0;
  g.dress_check_us = now_us + cfg_.dress_check_delay_us;
  g.label_dirty = false;
  g.owner_dirty = false;
  register_ghost(g);
  if (respawn) ++counters_.respawned;
  else if (g.ever_shown) ++counters_.shown;
  else ++counters_.spawned;
  const bool first = !g.ever_shown;
  g.ever_shown = true;
  g.left_by_hide = false;
  log(kInfo, std::format("ghost {}: net={} label='{}' id={} idcode={} sector_id={} pos=({:.0f},{:.0f},{:.0f})", first ? "spawned" : respawn ? "respawned" : "shown again", g.info.net_id,
                         g.label, id, g.idcode, sector_id, pose.pos.x, pose.pos.y, pose.pos.z));
  return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// the frame
// ---------------------------------------------------------------------------------------------------------------------
FrameStats GhostDriver::frame(std::int64_t now_us) {
  FrameStats fs;
  if (!world_) return fs;
  last_now_us_ = now_us;
  if (remove_all_ || !remove_queue_.empty()) process_removals();
  hint_batch_.clear();

  for (Ghost& g : ghosts_) {
    ghost::Interpolator* in = streams_.find(g.info.net_id);
    if (!in) continue;
    ghost::RenderPose rp = in->render(now_us);

    if (rp.state == ghost::PoseState::Empty && g.initial && !g.initial_used && g.initial->sector != 0 &&
        now_us - g.first_seen_us >= cfg_.spawn_pose_fallback_us) {
      // No Replication sample yet: the pose the spawn carried is better than an invisible ship.
      ghost::Sample s;
      s.t_us = now_us;
      s.sector = g.initial->sector;
      s.flags = g.initial->flags;
      s.pos = g.initial->pos;
      s.vel = g.initial->vel;
      s.rot = g.initial->rot;
      in->push(s, now_us);
      g.initial_used = true;
      rp = in->render(now_us);
    }
    g.last_state = rp.state;
    g.last_render_sector = rp.sector;
    g.last_render_pos = rp.pos;
    g.last_represented_us = rp.represented_t_us;
    if (rp.state == ghost::PoseState::Empty) continue;

    if (g.hidden_by_server && in->newest_sample_us() > g.hidden_at_newest_us) g.hidden_by_server = false;  // the ship reports again
    const bool visible = !rp.hidden && !g.hidden_by_server && rp.sector != 0;
    if (!visible) {
      if (g.local_id != 0) hide(g, rp.hidden ? "hidden flag or stale" : "server despawn");
      continue;
    }
    const std::uint64_t sec = world_->sector_id(rp.sector);
    if (sec == 0) {
      ++counters_.unmapped_frames;
      continue;
    }
    const Pose pose{rp.pos, rp.rot};

    if (g.local_id == 0) {
      if (fs.spawns >= cfg_.max_spawns_per_frame || now_us < g.spawn_not_before_us) continue;
      if (try_spawn(g, now_us, sec, pose)) {
        ++fs.spawns;
        ++fs.visible;
      }
      continue;
    }
    ++fs.visible;

    if (now_us - g.last_valid_us >= cfg_.validity_interval_us) {
      g.last_valid_us = now_us;
      if (!world_->valid(g.local_id) || world_->wrecked(g.local_id)) {
        drop_object(g, "invalid or wrecked");
        continue;
      }
    }

    const bool sector_changed = sec != g.sector_id;
    const bool moved = sector_changed || rp.snapped || pose_differs(pose, g.last_pose, cfg_.motion_epsilon_m, cfg_.motion_epsilon_rad);
    if (moved || now_us - g.last_set_us >= cfg_.parked_set_interval_us) {
      world_->place(g.local_id, sec, pose);
      ++counters_.places;
      ++fs.places;
      g.last_set_us = now_us;
      if (sector_changed && g.sector_id != 0) {
        ++counters_.sector_changes;
        log(kDebug, std::format("ghost sector change: net={} sector_id {} -> {} (index {})", g.info.net_id, g.sector_id, sec, rp.sector));
      }
      g.sector_id = sec;
    }

    // velocity hint (S13.2 mode c): the derivative of the rendered path, sent at 5 Hz as one batch per frame
    Vec3 v{};
    bool have_v = false;
    if (g.have_prev && !sector_changed && !rp.snapped) {
      const double dt = static_cast<double>(now_us - g.prev_t_us) / 1e6;
      if (dt > 1e-4) {
        v = (pose.pos - g.prev_pose.pos) * (1.0 / dt);
        have_v = true;
      }
    }
    if (now_us - g.last_hint_us >= cfg_.hint_interval_us && have_v) {
      const double sp = ghost::length(v);
      if (sp > 0.05) {
        hint_batch_.push_back({g.local_id, v.x, v.y, v.z});
        g.hint_was_zero = false;
        g.last_hint_us = now_us;
      } else if (!g.hint_was_zero || now_us - g.last_hint_us >= cfg_.zero_hint_interval_us) {
        hint_batch_.push_back({g.local_id, 0, 0, 0});
        g.hint_was_zero = true;
        g.last_hint_us = now_us;
      }
    }
    g.last_pose = pose;
    g.prev_pose = pose;
    g.prev_t_us = now_us;
    g.have_prev = true;

    if (g.owner_dirty) {
      g.owner_dirty = false;
      world_->set_owner(g.local_id, g.info.owner);
    }
    if (g.label_dirty) {
      g.label_dirty = false;
      world_->dress(g.local_id, g.label, cfg_.min_hull_percent);
      g.dressed_ok = false;
      g.dress_retries = 0;
      g.dress_check_us = now_us + cfg_.dress_check_delay_us;
    }
    if (now_us - g.last_inert_us >= cfg_.inert_interval_us) {
      g.last_inert_us = now_us;
      world_->make_inert(g.local_id);
    }
    if (!g.dressed_ok && now_us >= g.dress_check_us) {
      if (world_->name(g.local_id) == g.label) {
        g.dressed_ok = true;
      } else if (g.dress_retries < cfg_.dress_max_retries) {
        ++g.dress_retries;
        ++counters_.redressed;
        world_->dress(g.local_id, g.label, cfg_.min_hull_percent);
        g.dress_check_us = now_us + cfg_.dress_check_interval_us;
        log(kDebug, std::format("ghost dress retry {}: net={} label='{}'", g.dress_retries, g.info.net_id, g.label));
      } else {
        g.dressed_ok = true;
        log(kWarn, std::format("ghost dress gave up: net={} label='{}' (the game still shows '{}')", g.info.net_id, g.label, world_->name(g.local_id)));
      }
    }
  }

  if (!hint_batch_.empty()) {
    world_->hint_velocities(hint_batch_);
    counters_.hints_sent += hint_batch_.size();
  }
  fs.tracked = static_cast<int>(ghosts_.size());
  return fs;
}

void GhostDriver::forget_local_ids() {
  for (Ghost& g : ghosts_) {
    g.local_id = 0;
    g.restored_id = 0;
    g.sector_id = 0;
    g.have_prev = false;
  }
  regs_.clear();
  dirty_ = true;
}

// ---------------------------------------------------------------------------------------------------------------------
// persistence and adoption
// ---------------------------------------------------------------------------------------------------------------------
std::string GhostDriver::to_meta() const {
  std::string out(kMetaHeader);
  out += '\n';
  for (const Ghost& g : ghosts_) {
    out += std::format("G\t{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}\n", g.info.net_id, g.info.player_id, g.info.controller, g.info.team, g.local_id, dash(g.idcode),
                       dash(g.info.macro), dash(g.info.owner), dash(g.info.name));
  }
  out += std::format("end\t{}\n", ghosts_.size());
  return out;
}

void GhostDriver::save(session::IStash& stash) const {
  stash.put(kMetaKey, to_meta());
  regs_.save(stash, kRegistryEpoch);
}

bool GhostDriver::restore(std::string_view meta, std::int64_t now_us) {
  std::vector<Ghost> restored;
  std::size_t pos = 0;
  bool header = false, ended = false;
  std::size_t declared = 0;
  while (pos < meta.size()) {
    auto nl = meta.find('\n', pos);
    if (nl == std::string_view::npos) nl = meta.size();
    const std::string_view line = meta.substr(pos, nl - pos);
    pos = nl + 1;
    if (line.empty()) continue;
    if (!header) {
      if (line != kMetaHeader) return false;
      header = true;
      continue;
    }
    const auto f = split_tabs(line);
    if (f[0] == "end") {
      if (f.size() != 2 || !parse_num(f[1], declared)) return false;
      ended = true;
      continue;
    }
    if (f[0] != "G" || f.size() != 10) return false;
    Ghost g;
    std::uint32_t net = 0;
    unsigned player = 0, controller = 0, team = 0;
    std::uint64_t local = 0;
    if (!parse_num(f[1], net) || !parse_num(f[2], player) || !parse_num(f[3], controller) || !parse_num(f[4], team) || !parse_num(f[5], local)) return false;
    g.info.net_id = net;
    g.info.player_id = static_cast<std::uint16_t>(player);
    g.info.controller = static_cast<std::uint16_t>(controller);
    g.info.team = static_cast<std::uint16_t>(team);
    g.restored_id = local;
    g.idcode = undash(f[6]);
    g.info.macro = undash(f[7]);
    g.info.owner = undash(f[8]);
    g.info.name = undash(f[9]);
    g.label = make_label(g.info.name, g.info.controller, g.info.player_id);
    g.first_seen_us = now_us;
    g.ever_shown = local != 0;
    restored.push_back(std::move(g));
  }
  if (!header || !ended || declared != restored.size()) return false;
  for (Ghost& g : restored) {
    if (find(g.info.net_id)) continue;
    streams_.ensure(g.info.net_id);
    ghosts_.push_back(std::move(g));
  }
  adopt_pending_ = !ghosts_.empty();
  return true;
}

std::size_t GhostDriver::adopt(session::IStash& stash, std::int64_t now_us) {
  adopt_pending_ = false;
  if (!world_) return 0;
  const auto candidate = [&](ghost::LocalId id) -> Ghost* {
    for (auto& g : ghosts_) {
      if (g.restored_id == id && id != 0) return &g;
    }
    return nullptr;
  };
  const auto identity_ok = [&](ghost::LocalId id) {
    const Ghost* g = candidate(id);
    if (!g || !world_->valid(id)) return false;
    if (!g->idcode.empty() && world_->id_code(id) != g->idcode) return false;
    return world_->name(id).rfind(kNamePrefix, 0) == 0;
  };
  regs_.clear();
  const ghost::AdoptReport rep = regs_.adopt(stash, kRegistryEpoch, identity_ok);
  std::size_t adopted = 0, by_idcode = 0, lost = 0;
  std::vector<std::uint64_t> taken;
  for (Ghost& g : ghosts_) {
    g.local_id = 0;
    if (const ghost::GhostEntry* e = regs_.ghosts.find(g.info.net_id); e && e->local_id == g.restored_id && g.restored_id != 0) {
      g.local_id = e->local_id;
      taken.push_back(g.local_id);
      ++adopted;
    }
  }
  // Not adopted by id: find them again by idcode (component ids change on every save load).
  for (Ghost& g : ghosts_) {
    if (g.local_id != 0) continue;
    if (g.restored_id == 0 || g.idcode.empty()) continue;  // it was not in the game when the stash was written: nothing to find
    if (const auto found = world_->find_ghost_by_idcode(g.idcode); found && std::ranges::find(taken, *found) == taken.end()) {
      g.local_id = *found;
      taken.push_back(*found);
      ++by_idcode;
    } else {
      ++lost;
    }
  }
  // Registry entries nobody claims are ghosts we spawned whose records are gone: remove them (they are ours: name prefix + registry).
  for (const ghost::GhostEntry& e : std::vector<ghost::GhostEntry>(regs_.ghosts.entries())) {
    if (std::ranges::find(taken, e.local_id) == taken.end()) remove_queue_.push_back(e.local_id);
  }
  regs_.clear();
  for (Ghost& g : ghosts_) {
    g.restored_id = 0;
    if (g.local_id != 0) {
      register_ghost(g);
      g.sector_id = 0;  // unknown: the first frame places it
      g.last_set_us = now_us;
      g.last_hint_us = now_us;
      g.last_inert_us = now_us;
      g.last_valid_us = now_us;
      g.hint_was_zero = true;
      g.dressed_ok = false;
      g.dress_check_us = now_us + cfg_.dress_check_delay_us;
    }
  }
  counters_.adopted += adopted + by_idcode;
  counters_.adopt_dropped += lost + rep.dropped_ghosts;
  dirty_ = true;
  log(kInfo, std::format("ghost adoption: records={} adopted_by_id={} adopted_by_idcode={} lost={} (registry: found={} kept={} dropped={} parse_error={}) ", ghosts_.size(),
                         adopted, by_idcode, lost, rep.found, rep.kept_ghosts, rep.dropped_ghosts, rep.parse_error));
  return adopted + by_idcode;
}

// ---------------------------------------------------------------------------------------------------------------------
// reporting
// ---------------------------------------------------------------------------------------------------------------------
std::vector<GhostDriver::GhostView> GhostDriver::views() const {
  std::vector<GhostView> out;
  out.reserve(ghosts_.size());
  for (const Ghost& g : ghosts_) {
    GhostView v;
    v.net_id = g.info.net_id;
    v.player_id = g.info.player_id;
    v.controller = g.info.controller;
    v.label = g.label;
    v.local_id = g.local_id;
    v.shown = g.local_id != 0;
    v.state = g.last_state;
    v.sector = g.last_render_sector;
    v.pos = g.last_render_pos;
    v.represented_t_us = g.last_represented_us;
    v.sector_id = g.sector_id;
    out.push_back(std::move(v));
  }
  return out;
}

std::vector<std::string> GhostDriver::take_sync_lines() {
  std::vector<std::string> lines;
  for (Ghost& g : ghosts_) {
    ghost::Interpolator* in = streams_.find(g.info.net_id);
    if (!in) continue;
    const ghost::SyncReport rep = in->stats().report(true);
    if (rep.frames == 0) continue;
    std::string line = ghost::format_sync_line(g.info.net_id, rep);
    std::string who = g.label;
    if (who.rfind(kNamePrefix, 0) == 0) who.erase(0, kNamePrefix.size());
    static constexpr std::string_view tag = "[sync] ";
    if (line.rfind(tag, 0) == 0) line.insert(tag.size(), "player=" + who + " ");
    line += std::format(" delay_ms={} newest_age_ms={}", in->delay_us() / 1000, (last_now_us_ - in->newest_sample_us()) / 1000);
    lines.push_back(std::move(line));
  }
  return lines;
}

}  // namespace x4mp::features::ghosts
