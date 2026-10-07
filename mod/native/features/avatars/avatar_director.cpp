#include "features/avatars/avatar_director.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <format>

#include "x4mp/wire.h"

namespace x4mp::features::avatars {

namespace {
constexpr double kValidityPeriodS = 1.0;
constexpr double kInertPeriodS = 5.0;
constexpr double kIdleReassertS = 0.25;
constexpr double kParkedSnapM = 2.0;  // a ship that merely settles (after its zero-velocity hint) is not a pushed ship (M3-20)
constexpr double kPersistPeriodS = 2.0;
constexpr double kRecordMoveM = 5.0;
constexpr std::int64_t kMaxClockSkewUs = 2'000'000;

ghost::Euler to_euler(const Pose& p) { return {p.yaw, p.pitch, p.roll}; }
}  // namespace

struct AvatarDirector::Avatar {
  Record rec;
  std::uint64_t local_id = 0;
  std::uint64_t hint_id = 0;          // a local id the registry adoption kept (checked against idcode + name at rebind)
  Stage stage = Stage::Unbound;
  bool suspended = false;             // the player's node lost its link (resume grace): do not drive, keep
  bool announced = false;
  bool wanted_dress = false;
  std::unique_ptr<ghost::Interpolator> interp;
  VelocityEstimator vel;
  VelocityEstimator state_vel;        // from the relayed states (the wire carries no velocity)
  bool have_prev_state = false;
  ghost::Sample prev_state{};
  std::size_t samples = 0;
  std::uint64_t sector_id = 0;        // the sector the ship is in now (local)
  std::uint16_t cached_index = 0;
  std::uint64_t cached_index_id = 0;
  Pose last_set{};
  bool have_last_set = false;
  double last_set_s = -1e9;
  Pose recorded_pose{};               // the pose in the last persisted record (movement threshold)
  std::uint32_t seq = 0;              // the outstanding MD safepos question
  double stage_since = 0;
  Pose spawn_target{};
  std::uint64_t spawn_sector = 0;
  int tries = 0;
  double retry_at = 0;
  double next_validity = 0;
  double next_inert = 0;
  double last_inert_s = -1e9;         // when ActivateObject(false) was last called (M3-28 inert throttle)
  double next_snap = 0;
  VelHint last_hint{};
  bool hint_nonzero = false;
  bool holding = false;               // M3-31: driven avatar currently not moved (hidden / stale): repaired like a parked one when something pushes it
  bool zero_pending = false;          // M3-20: one zero-velocity hint is owed (parked, suspended, snapped back): the inert ship keeps its last velocity otherwise
  std::string waiting;                // why a spawn waits (logged when it changes)
  std::uint32_t dress_seq = 0;
  bool clock_warned = false;
  std::uint16_t want_team = 0;        // the player moved to this team (roster); 0 = no move pending (M3-18)
  bool owner_applied = false;         // SetComponentOwner done for want_team, only the EntityChange is still owed
  double team_retry_at = 0;
};

AvatarDirector::AvatarDirector(IAvatarEnv& env) : env_(env) {}
AvatarDirector::~AvatarDirector() = default;

// ---------------------------------------------------------------------------------------------------------------------------
AvatarDirector::Avatar* AvatarDirector::find(std::uint16_t player) {
  for (auto& a : avatars_) {
    if (a->rec.player_id == player) return a.get();
  }
  return nullptr;
}

AvatarDirector::Avatar* AvatarDirector::find_net(std::uint32_t net_id) {
  if (net_id == 0) return nullptr;
  for (auto& a : avatars_) {
    if (a->rec.net_id == net_id) return a.get();
  }
  return nullptr;
}

AvatarDirector::Avatar& AvatarDirector::create(std::uint16_t player) {
  auto a = std::make_unique<Avatar>();
  a->rec.player_id = player;
  a->interp = std::make_unique<ghost::Interpolator>();
  avatars_.push_back(std::move(a));
  return *avatars_.back();
}

std::string AvatarDirector::owner_of(const Avatar& av) const {
  if (!av.rec.owner.empty()) return av.rec.owner;
  return env_.faction_of_team(av.rec.team);
}

std::size_t AvatarDirector::live_count() const noexcept {
  return static_cast<std::size_t>(std::count_if(avatars_.begin(), avatars_.end(), [](const auto& a) { return a->stage == Stage::Live; }));
}

std::uint32_t AvatarDirector::max_net_id() const noexcept {
  std::uint32_t m = 0;
  for (const auto& a : avatars_) m = std::max(m, a->rec.net_id);
  return m;
}

std::vector<AvatarDirector::View> AvatarDirector::views() const {
  std::vector<View> out;
  for (const auto& a : avatars_) out.push_back({a->rec, a->local_id, a->stage, a->suspended, a->samples});
  return out;
}

// ---------------------------------------------------------------------------------------------------------------------------
// records, rebind
// ---------------------------------------------------------------------------------------------------------------------------
void AvatarDirector::load_records(const std::vector<Record>& records, const std::vector<std::pair<std::uint16_t, std::uint64_t>>& hint_ids) {
  for (const auto& r : records) {
    if (find(r.player_id) != nullptr) continue;
    auto& av = create(r.player_id);
    av.rec = r;
    av.recorded_pose = r.pose;
    av.stage = Stage::Unbound;
    av.announced = false;
    for (const auto& [pid, lid] : hint_ids) {
      if (pid == r.player_id) av.hint_id = lid;
    }
  }
  rebind_pending_ = !avatars_.empty();
  rebind_since_ = now_s_;
  rebind_tries_ = 0;
}

void AvatarDirector::on_loaded_save(const std::string& sha_hex) {
  const auto n = avatars_.size();
  const CheckpointNote* note = nullptr;
  for (const auto& c : lineage_.ledger) {
    if (c.sha == sha_hex) note = &c;
  }
  if (n == 0) return;
  std::size_t kept = 0;
  if (note != nullptr) {
    std::vector<std::unique_ptr<Avatar>> keep;
    for (auto& a : avatars_) {
      if (!a->rec.idcode.empty() && std::find(note->idcodes.begin(), note->idcodes.end(), a->rec.idcode) != note->idcodes.end()) keep.push_back(std::move(a));
    }
    kept = keep.size();
    avatars_ = std::move(keep);
  } else {
    avatars_.clear();
  }
  env_.log(LogLevel::Info, std::string("avatars: loading ") + (note != nullptr ? "a checkpoint of the ledger" : "a save that is no checkpoint of the ledger") + ": " +
                               std::to_string(kept) + " of " + std::to_string(n) + " avatar record(s) kept");
  rebind_pending_ = !avatars_.empty();
  persist_now();
}

void AvatarDirector::on_checkpoint_stored(const std::string& sha_hex, const std::vector<std::string>& idcodes) {
  if (sha_hex.empty()) return;
  std::erase_if(lineage_.ledger, [&](const CheckpointNote& c) { return c.sha == sha_hex; });
  lineage_.ledger.push_back({sha_hex, idcodes});
  while (lineage_.ledger.size() > Lineage::kMaxLedger) lineage_.ledger.erase(lineage_.ledger.begin());
  persist_now();
}

void AvatarDirector::new_universe() {
  for (auto& a : avatars_) {
    a->local_id = 0;
    a->hint_id = 0;
    a->sector_id = 0;
    a->cached_index = 0;
    a->cached_index_id = 0;
    a->have_last_set = false;
    a->seq = 0;
    a->announced = false;
    a->stage = Stage::Unbound;
    a->vel.reset();
  }
  rebind_pending_ = !avatars_.empty();
  rebind_since_ = now_s_;
  rebind_tries_ = 0;
  announce_pending_ = true;
}

void AvatarDirector::do_rebind() {
  auto cands = env_.team_candidates();
  if (!cands) {
    if (rebind_tries_++ == 0) env_.log(LogLevel::Warn, "avatars: cannot list the team-owned ships yet; the binder waits (no avatar is respawned meanwhile)");
    if (now_s_ - rebind_since_ > 30.0 && rebind_tries_ > 1) {
      env_.log(LogLevel::Error, "avatars: the team-owned ships cannot be listed after 30 s; the avatars stay unbound (a missing game export?)");
      rebind_pending_ = false;
    }
    return;
  }
  std::vector<Candidate> pool = std::move(*cands);
  std::vector<Avatar*> open;
  for (auto& up : avatars_) {
    Avatar& av = *up;
    if (av.stage != Stage::Unbound) continue;
    bool done = false;
    if (av.hint_id != 0) {  // adoption: the id survived (same universe) and still is this avatar
      const auto it = std::find_if(pool.begin(), pool.end(), [&](const Candidate& c) { return c.id == av.hint_id; });
      if (it != pool.end() && it->name == av.rec.name && it->owner == owner_of(av) && (av.rec.idcode.empty() || it->idcode == av.rec.idcode)) {
        av.local_id = it->id;
        pool.erase(it);
        done = true;
        ++stats_.bound;
      }
    }
    av.hint_id = 0;
    if (!done) open.push_back(&av);
  }
  std::vector<Record> recs;
  for (auto* av : open) {
    Record r = av->rec;
    r.owner = owner_of(*av);
    recs.push_back(std::move(r));
  }
  const BindResult br = bind_records(recs, pool);
  for (const auto& [ri, id] : br.bound) {
    Avatar& av = *open[ri];
    av.local_id = id;
    ++stats_.bound;
    const auto it = std::find_if(pool.begin(), pool.end(), [&](const Candidate& c) { return c.id == id; });
    if (it != pool.end() && it->name != av.rec.name) av.wanted_dress = true;  // the name never got applied before the save: dress again
  }
  // A moved player whose ship already wears the new faction (the save came after the move, the record before): look under the new faction too.
  std::vector<std::size_t> lost = br.lost;
  std::vector<Candidate> pool2 = pool;
  for (const auto& [ri, id] : br.bound) pool2.erase(std::remove_if(pool2.begin(), pool2.end(), [&](const Candidate& c) { return c.id == id; }), pool2.end());
  std::vector<std::size_t> still_lost;
  for (const auto ri : lost) {
    Avatar& av = *open[ri];
    const std::string nf = av.want_team != 0 ? env_.faction_of_team(av.want_team) : std::string{};
    bool found = false;
    if (!nf.empty()) {
      Record r = av.rec;
      r.owner = nf;
      const BindResult b2 = bind_records({r}, pool2);
      if (!b2.bound.empty()) {
        av.local_id = b2.bound.front().second;
        pool2.erase(std::remove_if(pool2.begin(), pool2.end(), [&](const Candidate& c) { return c.id == av.local_id; }), pool2.end());
        av.rec.team = av.want_team;
        av.rec.owner = nf;
        av.want_team = 0;
        av.owner_applied = false;
        ++stats_.bound;
        found = true;
      }
    }
    if (!found) still_lost.push_back(ri);
  }
  for (const auto ri : still_lost) {
    Avatar& av = *open[ri];
    av.stage = Stage::Wanted;
    av.local_id = 0;
    av.waiting.clear();
    ++stats_.lost;
    env_.log(LogLevel::Warn, std::format("avatars: no ship found for the avatar of player {} ({}, idcode '{}'): it is respawned at its last pose", av.rec.player_id,
                                         av.rec.name, av.rec.idcode));
  }
  if (!br.strays.empty()) {
    stats_.strays += static_cast<std::uint32_t>(br.strays.size());
    env_.log(LogLevel::Warn, std::format("avatars: {} '[MP] ' ship(s) of team factions belong to no avatar record (left alone; the janitor decides)", br.strays.size()));
  }
  for (auto& up : avatars_) {
    Avatar& av = *up;
    if (av.stage != Stage::Unbound || av.local_id == 0) continue;
    std::uint64_t sector = 0;
    Pose p;
    if (env_.read_pose(av.local_id, sector, p)) {
      av.rec.pose = p;
      av.recorded_pose = p;
      av.sector_id = sector;
      const auto macro = env_.sector_macro_of_id(sector);
      if (!macro.empty()) av.rec.sector_macro = macro;
    }
    av.stage = Stage::Live;
    av.announced = false;
    env_.activate(av.local_id, false);
    av.last_inert_s = now_s_;
    env_.log(LogLevel::Info, std::format("avatars: rebound player {} net_id={} to the ship in the loaded universe (idcode '{}')", av.rec.player_id, av.rec.net_id, av.rec.idcode));
    if (av.wanted_dress) {
      av.wanted_dress = false;
      av.dress_seq = next_seq_++;
      env_.ask_dress(av.dress_seq, av.local_id, av.rec.name, resolve_starter(settings_, av.rec.team), kAvatarMinHullPercent);
    }
  }
  rebind_pending_ = false;
  announce_pending_ = true;
  mark_dirty();
}

// ---------------------------------------------------------------------------------------------------------------------------
// inputs
// ---------------------------------------------------------------------------------------------------------------------------
void AvatarDirector::on_roster(const RosterIn& roster) {
  if (roster.full) {
    roster_.clear();
    have_full_roster_ = true;
  }
  for (const auto& p : roster.players) {
    auto it = std::find_if(roster_.begin(), roster_.end(), [&](const auto& e) { return e.first == p.id; });
    if (it == roster_.end()) {
      roster_.push_back({p.id, {}});
      it = roster_.end() - 1;
    }
    it->second.name = p.name;
    it->second.team = p.team;
    it->second.online = p.online;
    if (Avatar* av = find(p.id)) {
      av->suspended = !p.online && av->rec.online;
      if (av->suspended) av->zero_pending = true;
      if (av->rec.online && av->rec.team == 0) av->rec.team = p.team;
      if (p.team != 0 && av->rec.team != 0) {
        if (p.team != av->rec.team) {  // the player moved team: the avatar follows (applied in step() once the ship is live)
          if (av->want_team != p.team) {
            av->want_team = p.team;
            av->owner_applied = false;
            av->team_retry_at = 0;
            env_.log(LogLevel::Info, std::format("avatars: player {} moved to team {}: the avatar (net_id {}) is re-owned", p.id, p.team, av->rec.net_id));
          }
        } else if (av->want_team != 0) {
          av->want_team = 0;  // moved back before it was applied
          av->owner_applied = false;
        }
      }
    }
  }
  for (const auto id : roster.removed) {
    roster_.erase(std::remove_if(roster_.begin(), roster_.end(), [&](const auto& e) { return e.first == id; }), roster_.end());
    if (Avatar* av = find(id)) {
      if (av->rec.online) park(*av);
    }
  }
  if (roster.full) {
    for (auto& up : avatars_) {
      const bool present = std::any_of(roster_.begin(), roster_.end(), [&](const auto& e) { return e.first == up->rec.player_id; });
      if (!present && up->rec.online) park(*up);
    }
  }
}

void AvatarDirector::on_player_ship(const PlayerShipReq& req) {
  if (req.player_id == 0) return;
  ++stats_.requests;
  Avatar* av = find(req.player_id);
  const auto rit = std::find_if(roster_.begin(), roster_.end(), [&](const auto& e) { return e.first == req.player_id; });
  if (!av) {
    av = &create(req.player_id);
    av->rec.macro = resolve_starter(settings_, rit != roster_.end() ? rit->second.team : 0).macro;
    av->stage = Stage::Wanted;
    env_.log(LogLevel::Info, std::format("avatars: PlayerShip from player {}: provisioning a new avatar", req.player_id));
  } else {
    env_.log(LogLevel::Info, std::format("avatars: PlayerShip from player {}: avatar net_id={} exists ({}), answering with the same ship", req.player_id, av->rec.net_id,
                                         av->stage == Stage::Live ? "live" : "not live yet"));
    ++stats_.refreshed;
  }
  av->rec.online = true;
  av->suspended = false;
  av->interp->reset();
  av->samples = 0;
  av->have_prev_state = false;
  av->state_vel.reset();
  av->announced = false;  // refresh: the answer EntitySpawn goes out again (controller = the player)
  if (av->stage == Stage::Failed) {
    av->stage = Stage::Wanted;
    av->tries = 0;
  }
  mark_dirty();
}

void AvatarDirector::on_player_state(const PlayerStateIn& s, std::int64_t arrival_server_us) {
  ++stats_.states_in;
  Avatar* av = find_net(s.net_id);
  if (!av || !av->rec.online) {
    ++stats_.states_unknown;
    return;
  }
  ghost::Sample g;
  g.t_us = s.sample_time_us;
  // A sender whose sample time is not on the server clock (no clock sync yet, a bot that counts ticks) would put every sample seconds away from the
  // render time: the avatar would never move. Such samples are stamped with their arrival time less a typical 50 ms of transport instead.
  // M3-32 (Finding 21): a hitch of the authority's own game (a 3.2 s frame gap at a client's superhighway transit) delivers the states that queued up
  // in the gap in one batch: their (correct) stamps are as old as the gap. The tolerance grows by the time since the previous frame, so a stall is not
  // mistaken for a wrong clock (stamping the whole batch with one arrival time collapsed it into a single sample); a real clock error stays far
  // outside it (it is constant, the gap is a one-off).
  const std::int64_t stall_us = (last_frame_server_us_ > 0 && arrival_server_us > last_frame_server_us_) ? arrival_server_us - last_frame_server_us_ : 0;
  if (arrival_server_us > 0 && std::llabs(g.t_us - arrival_server_us) > kMaxClockSkewUs + stall_us) {
    if (!av->clock_warned) {
      av->clock_warned = true;
      env_.log(LogLevel::Warn, std::format("avatars: PlayerState of player {} is not stamped with the server clock (off by {:.1f} s); using arrival times", av->rec.player_id,
                                           static_cast<double>(g.t_us - arrival_server_us) / 1e6));
    }
    g.t_us = arrival_server_us - 50'000;
  }
  g.sector = s.sector;
  g.flags = s.flags;
  g.pos = {s.pose.x, s.pose.y, s.pose.z};
  g.rot = to_euler(s.pose);
  g.hull = s.hull;
  g.shield = s.shield;
  // The wire has no velocity: derive it from consecutive states of the same sector (no teleport), lightly smoothed.
  if (av->have_prev_state && av->prev_state.sector == g.sector && (g.flags & ghost::kTeleport) == 0 && g.t_us > av->prev_state.t_us) {
    const double dt = static_cast<double>(g.t_us - av->prev_state.t_us) / 1e6;
    if (dt > 0.001 && dt < 1.0) {
      const ghost::Vec3 raw = (g.pos - av->prev_state.pos) * (1.0 / dt);
      g.vel = av->prev_state.vel + (raw - av->prev_state.vel) * 0.5;
    }
  }
  av->prev_state = g;
  av->have_prev_state = true;
  av->interp->push(g, arrival_server_us);
  ++av->samples;
}

void AvatarDirector::on_despawn(const DespawnIn& d) {
  for (const auto& [net_id, reason] : d.entries) {
    if (reason != kDespawnRemoved) continue;
    Avatar* av = find_net(net_id);
    if (!av) continue;
    bool removed = true;
    if (av->local_id != 0) {
      removed = env_.remove(av->local_id);  // SafeRemove: never the player's own ship
      if (removed) {
        ++stats_.removed;
      } else {
        ++stats_.remove_refused;
        env_.log(LogLevel::Warn, std::format("avatars: the server removed the avatar of player {} (net_id {}) but the ship could not be removed (guard or game); the record is dropped", av->rec.player_id, net_id));
      }
    } else {
      ++stats_.removed;
    }
    env_.log(LogLevel::Info, std::format("avatars: avatar of player {} (net_id {}) removed on the server's order", av->rec.player_id, net_id));
    const auto pid = av->rec.player_id;
    avatars_.erase(std::remove_if(avatars_.begin(), avatars_.end(), [&](const auto& a) { return a->rec.player_id == pid; }), avatars_.end());
    mark_dirty();
  }
}

void AvatarDirector::on_safepos(std::uint32_t seq, bool ok, const Pose& pos) {
  ++stats_.safepos_replies;
  for (auto& up : avatars_) {
    Avatar& av = *up;
    if (av.stage != Stage::AwaitSafePos || av.seq != seq) continue;
    Pose p = av.spawn_target;
    if (ok) {
      p.x = pos.x;
      p.y = pos.y;
      p.z = pos.z;
    }
    spawn_at(av, p, ok ? "safe position" : "MD found no safe position, using the wanted spot");
    return;
  }
}

void AvatarDirector::on_dress(std::uint32_t seq, bool ok, const std::string& detail) {
  for (auto& up : avatars_) {
    if (up->dress_seq != seq) continue;
    if (ok) ++stats_.dress_ok;
    else ++stats_.dress_failed;
    env_.log(ok ? LogLevel::Info : LogLevel::Warn, std::format("avatars: MD dress of player {} {} ({})", up->rec.player_id, ok ? "done" : "FAILED", detail));
    return;
  }
}

// ---------------------------------------------------------------------------------------------------------------------------
// provisioning
// ---------------------------------------------------------------------------------------------------------------------------
void AvatarDirector::try_start_spawn(Avatar& av) {
  const auto wait = [&](const char* why) {
    if (av.waiting != why) {
      av.waiting = why;
      env_.log(LogLevel::Info, std::format("avatars: the avatar of player {} waits: {}", av.rec.player_id, why));
    }
  };
  if (!env_.net_ready()) return wait("not connected as the authority");
  if (!env_.factions_ready()) return wait("the team factions are not active yet");
  const auto rit = std::find_if(roster_.begin(), roster_.end(), [&](const auto& e) { return e.first == av.rec.player_id; });
  if (rit != roster_.end()) {
    av.rec.team = rit->second.team;
    if (av.rec.name.empty()) av.rec.name = avatar_name(rit->second.name);
  }
  if (rit != roster_.end() && rit->second.team != 0) {  // a (re)spawn is under the team the player is in NOW, never an old faction (M3-18)
    const std::string f = env_.faction_of_team(rit->second.team);
    if (!f.empty()) av.rec.owner = f;
    av.want_team = 0;
    av.owner_applied = false;
  }
  if (av.rec.team == 0 && av.rec.owner.empty()) return wait("the roster does not name the player's team yet");
  if (av.rec.name.empty()) av.rec.name = avatar_name(std::format("Player{}", av.rec.player_id));
  if (av.rec.owner.empty()) av.rec.owner = env_.faction_of_team(av.rec.team);
  if (av.rec.owner.empty()) return wait("the player's team has no faction slot");
  const StarterSpec starter = resolve_starter(settings_, av.rec.team);
  if (av.rec.macro.empty()) av.rec.macro = starter.macro;

  Pose target{};
  std::uint64_t sector = 0;
  if (!av.rec.sector_macro.empty()) {  // a lost avatar comes back where it was
    sector = env_.sector_id_of_macro(av.rec.sector_macro);
    target = av.rec.pose;
  }
  if (sector == 0) {  // a new avatar appears next to the host's ship
    const auto host = env_.host_place();
    if (!host || host->sector_id == 0) return wait("the host's ship is not known");
    sector = host->sector_id;
    target = place_near(host->pose, settings_.spawn_offset_m, av.rec.player_id);
  }
  av.waiting.clear();
  av.spawn_sector = sector;
  av.spawn_target = target;
  av.seq = next_seq_++;
  av.stage = Stage::AwaitSafePos;
  av.stage_since = now_s_;
  env_.ask_safepos(av.seq, sector, target, kSafePosRadiusM);
}

void AvatarDirector::spawn_at(Avatar& av, const Pose& pose, const char* why) {
  const std::string owner = owner_of(av);
  const std::uint64_t id = env_.spawn(av.rec.macro, av.spawn_sector, pose, owner);
  if (id == 0) {
    ++stats_.spawn_failed;
    ++av.tries;
    av.stage = Stage::Failed;
    av.retry_at = now_s_ + kRetryS;
    env_.log(av.tries >= kMaxSpawnTries ? LogLevel::Error : LogLevel::Warn,
             std::format("avatars: SpawnObjectAtPos2 failed for player {} (macro {}, owner {}, try {}/{})", av.rec.player_id, av.rec.macro, owner, av.tries, kMaxSpawnTries));
    return;
  }
  env_.activate(id, false);  // inert: no pilot, no orders (S13.1: stays put, drift 0.000 m)
  av.last_inert_s = now_s_;
  av.local_id = id;
  av.sector_id = av.spawn_sector;
  av.tries = 0;
  av.rec.pose = pose;
  av.recorded_pose = pose;
  av.rec.idcode = env_.idcode(id);
  av.rec.sector_macro = env_.sector_macro_of_id(av.spawn_sector);
  if (av.rec.net_id == 0) av.rec.net_id = env_.alloc_net_id();
  av.stage = Stage::Live;
  av.announced = false;
  av.last_set = pose;
  av.have_last_set = true;
  av.last_set_s = now_s_;
  av.next_validity = now_s_ + kValidityPeriodS;
  av.next_inert = now_s_ + kInertPeriodS;
  const StarterSpec starter = resolve_starter(settings_, av.rec.team);
  av.dress_seq = next_seq_++;
  env_.ask_dress(av.dress_seq, id, av.rec.name, starter, kAvatarMinHullPercent);
  ++stats_.provisioned;
  env_.log(LogLevel::Info, std::format("avatars: spawned the avatar of player {}: id {} macro {} owner {} name '{}' idcode '{}' net_id {} at ({:.0f},{:.0f},{:.0f}) in sector {} ({})", av.rec.player_id, id,
                                       av.rec.macro, owner, av.rec.name, av.rec.idcode, av.rec.net_id, pose.x, pose.y, pose.z, av.rec.sector_macro, why));
  mark_dirty();
}

void AvatarDirector::announce(Avatar& av, bool force_controller_zero) {
  if (av.local_id == 0 || av.rec.net_id == 0) return;
  const double gt = env_.game_time();
  if (!authority::EntitySpawnBuilder::is_valid_game_time(gt)) return;
  const std::uint16_t sector_index = env_.sector_index_of_macro(av.rec.sector_macro);
  if (sector_index == 0) return;  // the sector table is not there yet: retried every frame
  Pose p = av.rec.pose;
  std::uint64_t sec = 0;
  Pose cur;
  if (env_.read_pose(av.local_id, sec, cur)) p = cur;
  authority::SpawnEntity e;
  e.net_id = av.rec.net_id;
  e.kind = static_cast<authority::SpawnKind>(ship_kind_of_macro(av.rec.macro));
  e.origin = authority::SpawnOrigin::PlayerShip;
  e.macro_ref = env_.string_ref(StrKind::Macro, av.rec.macro);
  e.owner_ref = env_.string_ref(StrKind::Faction, owner_of(av));
  e.owner_team = av.rec.team;
  e.owner_player = av.rec.player_id;
  e.controller_player = (av.rec.online && !force_controller_zero) ? av.rec.player_id : 0;
  e.name = av.rec.name;
  e.idcode = av.rec.idcode;
  e.sector = sector_index;
  const auto qx = wire::quantize_position(p.x), qy = wire::quantize_position(p.y), qz = wire::quantize_position(p.z);
  const auto ry = wire::quantize_rotation(p.yaw), rp = wire::quantize_rotation(p.pitch), rr = wire::quantize_rotation(p.roll);
  if (!qx || !qy || !qz || !ry || !rp || !rr) return;
  e.px = *qx;
  e.py = *qy;
  e.pz = *qz;
  e.yaw = *ry;
  e.pitch = *rp;
  e.roll = *rr;
  if (e.macro_ref == 0 || e.owner_ref == 0) return;  // the string table could not take them: retried
  if (!env_.send_spawn({e}, gt)) return;
  av.announced = true;
  ++stats_.announced;
  env_.log(LogLevel::Debug, std::format("avatars: EntitySpawn net_id={} player={} controller={} sector_index={}", e.net_id, av.rec.player_id, e.controller_player, sector_index));
}

void AvatarDirector::park(Avatar& av) {
  av.rec.online = false;
  av.suspended = false;
  av.interp->reset();
  av.have_prev_state = false;
  av.vel.reset();
  av.zero_pending = true;  // the MD velocity hint of the last driven moment would keep the inert ship drifting (M3-20)
  ++stats_.parked;
  if (av.rec.net_id != 0) env_.send_controller(av.rec.net_id, 0);
  env_.log(LogLevel::Info, std::format("avatars: player {} left: avatar net_id={} stays parked at its pose", av.rec.player_id, av.rec.net_id));
  remember(av);
  mark_dirty();
}

// M3-18: the player's team changed. Re-own the live ship to the new team faction (it stays inert, named, min-hull: SetComponentOwner changes
// nothing else), update and persist the record, tell the server once. Retried every kRetryS until both the game call and the send went through.
void AvatarDirector::apply_team_move(Avatar& av) {
  if (now_s_ < av.team_retry_at) return;
  av.team_retry_at = now_s_ + kRetryS;
  if (!env_.factions_ready()) return;
  const std::string faction = env_.faction_of_team(av.want_team);
  if (faction.empty()) return;  // the team has no faction slot yet: the next team table fixes it
  if (!av.owner_applied && diag_.team_move_respawn && respawn_for_team_move(av, faction)) return;
  if (!av.owner_applied) {
    if (!env_.valid(av.local_id) || !env_.set_owner(av.local_id, faction)) return;
    env_.activate(av.local_id, false);
    av.last_inert_s = now_s_;
    av.owner_applied = true;
    av.rec.team = av.want_team;
    av.rec.owner = faction;
    ++stats_.reowned;
    env_.log(LogLevel::Info, std::format("avatars: re-owned the avatar of player {} (id {}, net_id {}) to {} (team {})", av.rec.player_id, av.local_id, av.rec.net_id, faction, av.rec.team));
    mark_dirty();
  }
  if (av.rec.net_id != 0 && av.announced && !env_.send_owner(av.rec.net_id, av.rec.team, faction)) return;  // an unannounced avatar carries the new owner in its spawn
  av.want_team = 0;
  av.owner_applied = false;
}

// M3-28 diag.team_move_respawn (hypothesis H2: the native re-own of a live avatar leaves stale engine state): despawn the avatar through SafeRemove and spawn a
// fresh ship under the new team faction at the very same sector + pose (no safe-position round trip). The record keeps the player id, name and NET ID; the
// idcode changes (the new ship's), and the record is persisted at once (the next checkpoint manifest lists the new idcode, so the M3-22 ledger follows).
// The server mirror and the clients get ONE refreshing EntitySpawn for the same net id (new owner, team, idcode): no EntityChange and no despawn, so a ghost is
// updated in place. true = handled (done, or spawn failed and the normal retry path owns the avatar now); false = could not start, caller re-owns natively.
bool AvatarDirector::respawn_for_team_move(Avatar& av, const std::string& faction) {
  if (!env_.valid(av.local_id)) return false;
  remember(av);  // current pose + sector into the record
  const std::uint64_t sector = av.sector_id;
  if (sector == 0) return false;
  const Pose pose = av.rec.pose;
  const std::uint64_t old_id = av.local_id;
  if (!env_.remove(old_id)) return false;  // refused (SafeRemove guard): the caller falls back to the native re-own, as without the switch
  av.local_id = 0;
  av.rec.team = av.want_team;
  av.rec.owner = faction;
  av.spawn_sector = sector;
  av.spawn_target = pose;
  av.want_team = 0;
  av.owner_applied = false;
  av.have_last_set = false;
  ++stats_.team_respawned;
  env_.log(LogLevel::Warn, std::format("avatars: diag.team_move_respawn: despawned the avatar of player {} (old id {}) and spawning a fresh one under {} (team {}) at the same pose (net_id {} kept)",
                                       av.rec.player_id, old_id, faction, av.rec.team, av.rec.net_id));
  spawn_at(av, pose, "diag.team_move_respawn");  // on failure the stage is Failed: the normal retry path (try_start_spawn) respawns it under the roster's team
  mark_dirty();
  return true;
}

void AvatarDirector::remember(Avatar& av) {
  if (av.local_id == 0) return;
  std::uint64_t sec = 0;
  Pose p;
  if (env_.read_pose(av.local_id, sec, p)) {
    av.rec.pose = p;
    av.recorded_pose = p;
    av.sector_id = sec;
    const auto macro = env_.sector_macro_of_id(sec);
    if (!macro.empty()) av.rec.sector_macro = macro;
  }
}

// ---------------------------------------------------------------------------------------------------------------------------
// per frame
// ---------------------------------------------------------------------------------------------------------------------------
void AvatarDirector::drive(Avatar& av, std::int64_t server_now_us) {
  if (!av.rec.online || av.suspended || av.samples == 0) return;
  const ghost::RenderPose rp = av.interp->render(server_now_us);
  if (rp.state == ghost::PoseState::Empty) return;
  if (rp.hidden) {  // on foot / docked inside / superhighway transit / stale: the ship stays where it is
    av.vel.sample(now_s_, 0, 0, 0, true);
    av.holding = true;  // M3-31: and stays there (zero velocity hint, put back if the game moves it across a sector edge)
    return;
  }
  if (av.cached_index != rp.sector || av.cached_index_id == 0) {
    av.cached_index = rp.sector;
    av.cached_index_id = env_.sector_id_of_index(rp.sector);
  }
  const std::uint64_t sector = av.cached_index_id;
  if (sector == 0) {
    ++stats_.unmapped_sector;
    return;
  }
  Pose p;
  p.x = rp.pos.x;
  p.y = rp.pos.y;
  p.z = rp.pos.z;
  p.yaw = rp.rot.yaw;
  p.pitch = rp.rot.pitch;
  p.roll = rp.rot.roll;
  const bool sector_changed = sector != av.sector_id;
  // M3-31 (Finding 18): the velocity hint follows what is RENDERED, every frame. It used to be fed only when a pose was set, so a held avatar (no
  // state for > 500 ms: the interpolator holds the pose) kept its last flight speed for seconds (decaying 25 % per 250 ms re-assert): the game kept
  // flying the inert ship, across the sector edge near a superhighway ring, and the next set_pose put it back = a sector ping-pong.
  av.vel.sample(now_s_, p.x, p.y, p.z, rp.snapped || sector_changed || rp.state == ghost::PoseState::Held || rp.state == ghost::PoseState::Early);
  const bool same = av.have_last_set && !sector_changed && distance_m(p, av.last_set) < 1e-4 && p.yaw == av.last_set.yaw && p.pitch == av.last_set.pitch &&
                    p.roll == av.last_set.roll;
  if (same && now_s_ - av.last_set_s < kIdleReassertS) return;
  if (!env_.set_pose(av.local_id, sector, p)) return;
  ++stats_.set_pose_calls;
  av.holding = false;
  av.last_set = p;
  av.have_last_set = true;
  av.last_set_s = now_s_;
  av.rec.pose = p;
  if (sector_changed) {
    av.sector_id = sector;
    const auto macro = env_.sector_macro_of_id(sector);
    if (!macro.empty()) av.rec.sector_macro = macro;
    mark_dirty();
  } else if (distance_m(p, av.recorded_pose) > kRecordMoveM) {
    av.recorded_pose = p;
    mark_dirty();
  }
}

void AvatarDirector::maintain(Avatar& av) {
  if (now_s_ >= av.next_validity) {
    av.next_validity = now_s_ + kValidityPeriodS;
    if (!env_.valid(av.local_id)) {
      env_.log(LogLevel::Warn, std::format("avatars: the ship of player {} (id {}) is gone; respawning it at its last pose", av.rec.player_id, av.local_id));
      av.local_id = 0;
      av.stage = Stage::Wanted;
      av.announced = false;
      av.waiting.clear();
      ++stats_.respawned;
      return;
    }
    if (!av.rec.online || av.suspended || av.holding) {  // parked (or driven but hidden, M3-31): snap back when something pushed it
      std::uint64_t sec = 0;
      Pose p;
      if (env_.read_pose(av.local_id, sec, p) && av.sector_id != 0 && (sec != av.sector_id || distance_m(p, av.rec.pose) > kParkedSnapM)) {
        if (env_.set_pose(av.local_id, av.sector_id, av.rec.pose)) {
          ++stats_.repairs;
          env_.log(LogLevel::Info, std::format("avatars: repaired the parked avatar of player {} (net_id {}): it was {:.1f} m off its pose", av.rec.player_id, av.rec.net_id, distance_m(p, av.rec.pose)));
          av.zero_pending = true;  // a pushed ship moves on with its velocity: stop it, or it is snapped back again and again
        }
      }
    }
  }
  if (now_s_ >= av.next_inert) {
    av.next_inert = now_s_ + kInertPeriodS;
    // M3-28 diag.avatars_inert_once: only with a read-back that shows the ship active (the real adapters have none yet: then never)
    if (inert_reassert_due(diag_.inert_once, diag_.inert_once ? env_.is_active(av.local_id) : std::nullopt, now_s_, av.last_inert_s)) {
      env_.activate(av.local_id, false);
      av.last_inert_s = now_s_;
    }
  }
}

void AvatarDirector::step(double now_s, std::int64_t server_now_us) {
  now_s_ = now_s;
  if (server_now_us > 0) last_frame_server_us_ = server_now_us;
  if (!env_.game_ready()) return;
  if (rebind_pending_) do_rebind();
  if (rebind_pending_) return;

  for (auto& up : avatars_) {
    Avatar& av = *up;
    switch (av.stage) {
      case Stage::Unbound: break;
      case Stage::Wanted: try_start_spawn(av); break;
      case Stage::AwaitSafePos:
        if (now_s_ - av.stage_since > kSafePosTimeoutS) {
          ++stats_.safepos_timeouts;
          spawn_at(av, av.spawn_target, "no MD answer for the safe position, using the wanted spot");
        }
        break;
      case Stage::Failed:
        if (av.tries < kMaxSpawnTries && now_s_ >= av.retry_at) {
          av.stage = Stage::Wanted;
          av.waiting.clear();
        }
        break;
      case Stage::Live:
        drive(av, server_now_us);
        maintain(av);
        if (av.want_team != 0) apply_team_move(av);
        break;
    }
  }

  if (env_.net_ready()) {
    if (announce_pending_) {
      announce_pending_ = false;
      for (auto& up : avatars_) up->announced = false;
    }
    for (auto& up : avatars_) {
      if (up->stage == Stage::Live && !up->announced) announce(*up);
    }
  }

  if (now_s_ >= next_vel_s_) {
    next_vel_s_ = now_s_ + kVelHintPeriodS;
    std::vector<VelHint> hints;
    for (auto& up : avatars_) {
      Avatar& av = *up;
      if (av.stage != Stage::Live) continue;
      if (!av.rec.online || av.suspended) {  // parked / suspended: exactly one zero hint, never a stale velocity
        if (av.zero_pending || av.hint_nonzero) hints.push_back({av.local_id, 0, 0, 0});
        av.zero_pending = false;
        av.hint_nonzero = false;
        continue;
      }
      const bool zero_owed = av.zero_pending;  // a repaired hidden avatar (M3-31)
      av.zero_pending = false;
      if (av.vel.valid()) {
        const auto v = av.vel.velocity();
        const double sp = std::sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        if (zero_owed && sp <= 0.05) {
          hints.push_back({av.local_id, 0, 0, 0});
          av.hint_nonzero = false;
        } else if (sp > 0.05) {
          hints.push_back({av.local_id, v.x, v.y, v.z});
          av.hint_nonzero = true;
        } else if (av.hint_nonzero) {
          hints.push_back({av.local_id, 0, 0, 0});
          av.hint_nonzero = false;
        }
      } else if (av.hint_nonzero || zero_owed) {
        hints.push_back({av.local_id, 0, 0, 0});
        av.hint_nonzero = false;
      }
    }
    if (!hints.empty()) {
      stats_.vel_hints += static_cast<std::uint32_t>(hints.size());
      env_.send_velocity(hints);
    }
  }

  if (dirty_ && now_s_ >= next_persist_s_) {
    next_persist_s_ = now_s_ + kPersistPeriodS;
    persist_now();
  }
}

std::vector<Record> AvatarDirector::snapshot() {
  std::vector<Record> out;
  for (auto& up : avatars_) {
    Avatar& av = *up;
    if (av.stage == Stage::Live) remember(av);
    if (av.rec.net_id == 0 || av.rec.idcode.empty()) continue;  // never spawned: nothing for a manifest or a binder
    out.push_back(av.rec);
  }
  std::sort(out.begin(), out.end(), [](const Record& a, const Record& b) { return a.net_id < b.net_id; });
  return out;
}

void AvatarDirector::persist_now() {
  dirty_ = false;
  std::vector<Record> recs;
  for (const auto& up : avatars_) {
    if (up->rec.net_id == 0) continue;
    recs.push_back(up->rec);
  }
  env_.save_records(records_to_text(recs, lineage_));
}

}  // namespace x4mp::features::avatars
