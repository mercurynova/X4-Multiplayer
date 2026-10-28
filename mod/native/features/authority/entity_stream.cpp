#include "features/authority/entity_stream.h"

#include <algorithm>
#include <cmath>

#include <x4mp/wire.h>

#include "core/authority/entity_spawn.h"
#include "features/authority/capture_set_wire.h"
#include "message_ids_generated.h"

namespace x4mp::features::auth {

namespace {

constexpr std::uint16_t T(X4MP::Proto::MsgType t) { return static_cast<std::uint16_t>(t); }

// Mirrors common.fbs StateFlags / world.fbs ChangeField. Kept local so this translation unit is the only one that needs
// the generated enums.
constexpr std::uint16_t kFlagVelCoarse = 1u << 0;
constexpr std::uint16_t kFlagKeyframe = 1u << 6;
constexpr std::uint16_t kChangeOwner = 1u << 0;
constexpr std::uint16_t kChangeMacro = 1u << 5;

}  // namespace

EntityStream::EntityStream(PoseReader reader, Sink sink, StreamConfig cfg)
    : reader_(std::move(reader)), sink_(std::move(sink)), cfg_(cfg) {
  states_.reserve(kMaxStatesPerWorldUpdate);
  statuses_.reserve(kMaxStatusesPerBatch);
}

const EntityTrack* EntityStream::find(std::uint32_t net_id) const {
  const auto it = tracks_.find(net_id);
  return it == tracks_.end() ? nullptr : &it->second;
}

std::size_t EntityStream::pending_despawns() const noexcept {
  std::size_t n = 0;
  for (const auto& [id, track] : tracks_) {
    (void)id;
    if (track.despawn_at_s >= 0.0) ++n;
  }
  return n;
}

void EntityStream::on_capture_set(const CaptureSet& set) {
  scheduler_.clear();
  focus_sweep_.clear();
  focus_ids_.clear();
  sectors_.clear();

  for (const auto& s : set.sectors()) {
    scheduler_.set_rate(s.sector, s.rate_hz);
    if (std::find(sectors_.begin(), sectors_.end(), s.sector) == sectors_.end()) sectors_.push_back(s.sector);
    focus_sweep_.insert(s.sector);
  }
  // A sector that only a focus sphere names is captured too (at the sphere's rate, outside the sphere at 0 Hz).
  for (const auto& f : set.focus()) {
    if (std::find(sectors_.begin(), sectors_.end(), f.sector) == sectors_.end()) sectors_.push_back(f.sector);
    focus_sweep_.insert(f.sector);
  }

  // A new focus sphere must be honoured at once: every entity is polled once on the next step to classify it, instead
  // of waiting for its sector slice to come round.
  for (auto& [id, track] : tracks_) {
    (void)id;
    track.in_focus = false;
  }
}

void EntityStream::on_scan_complete(std::uint16_t sector) {
  if (sector != 0) spawn_pending_.insert(sector);
}

void EntityStream::on_row_removed(std::uint32_t net_id, bool destroyed) {
  if (net_id == 0) return;
  despawn_now_.push_back(DespawnSample{net_id, 0, static_cast<std::uint8_t>(destroyed ? DespawnCause::Destroyed : DespawnCause::Removed)});
}

void EntityStream::on_killed(std::uint32_t net_id, std::uint32_t killer_net_id) {
  if (net_id == 0) return;
  despawn_now_.push_back(DespawnSample{net_id, killer_net_id, static_cast<std::uint8_t>(DespawnCause::Destroyed)});
}

void EntityStream::on_change(std::uint32_t net_id, std::uint32_t owner_ref, std::uint32_t macro_ref) {
  ChangeSample c;
  c.net_id = net_id;
  c.owner_ref = owner_ref;
  c.macro_ref = macro_ref;
  if (owner_ref != 0) c.fields |= kChangeOwner;
  if (macro_ref != 0) c.fields |= kChangeMacro;
  if (c.fields == 0) return;
  change_now_.push_back(c);
}

void EntityStream::note_status(std::uint32_t net_id, std::uint8_t hull, std::uint8_t shield) {
  const auto it = tracks_.find(net_id);
  if (it == tracks_.end() || !it->second.have_sent) return;
  EntityTrack& track = it->second;
  if (track.hull == hull && track.shield == shield) return;
  track.hull = hull;
  track.shield = shield;
  track.status_dirty = true;
}

void EntityStream::set_observer(std::uint16_t sector, double x, double y, double z) {
  observer_sector_ = sector;
  observer_x_ = x;
  observer_y_ = y;
  observer_z_ = z;
}

void EntityStream::on_disconnect() {
  tracks_.clear();
  focus_ids_.clear();
  focus_sweep_.clear();
  spawn_pending_.clear();
  despawn_now_.clear();
  change_now_.clear();
  states_.clear();
  statuses_.clear();
  scheduler_.clear();
  sectors_.clear();
  observer_sector_ = 0;
  tick_ = 0;
  visit_tick_ = 0;
  stats_ = StreamStats{};
}

void EntityStream::step(double game_time, std::int64_t capture_time_us, double now_s, const CaptureSet& set,
                        const UniverseIndex& index) {
  // EntitySpawn refuses a zero/NaN time, and every state is stamped with the same reference: without it nothing goes out.
  if (!authority::EntitySpawnBuilder::is_valid_game_time(game_time)) return;
  ++tick_;
  ++visit_tick_;
  housekeeping(set, index, now_s);
  flush_despawns(now_s);
  spawn_due(game_time, now_s, set, index);
  update_pass(now_s, set, index);
  flush_states(capture_time_us, game_time);
  flush_changes();
  flush_statuses(capture_time_us, now_s);
}

// A sector that leaves interest keeps its entities for the grace (M4-10); a sector that comes back cancels it. A row that
// moved to a captured sector is re-homed here so its next poll is a Sector discontinuity, not a stale bucket.
void EntityStream::housekeeping(const CaptureSet& set, const UniverseIndex& index, double now_s) {
  for (auto& [net_id, track] : tracks_) {
    (void)net_id;
    const auto* row = index.find(track.net_id);
    const std::uint16_t sector = row != nullptr ? row->sector : track.sector;
    if (set.contains(sector)) {
      track.despawn_at_s = -1.0;
      if (row != nullptr && track.sector != sector) {
        auto& old_list = focus_ids_[track.sector];
        std::erase(old_list, track.net_id);
        track.sector = sector;
        track.in_focus = false;
      }
    } else if (track.despawn_at_s < 0.0) {
      track.despawn_at_s = now_s + cfg_.out_of_interest_grace_s;
    }
  }
}

void EntityStream::flush_despawns(double now_s) {
  std::vector<DespawnSample> batch;
  batch.reserve(kMaxDespawnsPerMessage);
  const auto send_batch = [&] {
    if (batch.empty()) return;
    if (sink_.control(T(X4MP::Proto::MsgType::EntityDespawn), encode_entity_despawn(batch))) {
      stats_.despawns += static_cast<std::uint32_t>(batch.size());
    } else {
      ++stats_.send_failed;
    }
    batch.clear();
  };

  for (const auto& d : despawn_now_) {
    batch.push_back(d);
    if (batch.size() >= kMaxDespawnsPerMessage) send_batch();
  }
  despawn_now_.clear();

  // The OutOfInterest grace ran out.
  std::vector<std::uint32_t> due;
  for (const auto& [net_id, track] : tracks_) {
    if (track.despawn_at_s >= 0.0 && now_s >= track.despawn_at_s) due.push_back(net_id);
  }
  for (const auto net_id : due) {
    batch.push_back(DespawnSample{net_id, 0, static_cast<std::uint8_t>(DespawnCause::OutOfInterest)});
    auto& list = focus_ids_[tracks_[net_id].sector];
    std::erase(list, net_id);
    tracks_.erase(net_id);
    if (batch.size() >= kMaxDespawnsPerMessage) send_batch();
  }
  send_batch();
}

void EntityStream::spawn_due(double game_time, double now_s, const CaptureSet& set, const UniverseIndex& index) {
  for (auto it = spawn_pending_.begin(); it != spawn_pending_.end();) {
    const std::uint16_t sector = *it;
    if (!set.contains(sector)) {
      it = spawn_pending_.erase(it);
      continue;
    }
    const auto* ids = index.ids_of(sector);
    if (ids == nullptr) {
      it = spawn_pending_.erase(it);
      continue;
    }

    bool ok = true;
    std::vector<authority::SpawnEntity> chunk;
    std::vector<std::uint32_t> chunk_ids;
    std::vector<std::uint64_t> chunk_components;
    std::vector<TrackPose> chunk_poses;
    chunk.reserve(kMaxSpawnsPerMessage);
    chunk_ids.reserve(kMaxSpawnsPerMessage);
    chunk_components.reserve(kMaxSpawnsPerMessage);
    chunk_poses.reserve(kMaxSpawnsPerMessage);

    const auto send_chunk = [&]() -> bool {
      if (chunk.empty()) return true;
      authority::EntitySpawnBuilder builder(game_time);
      for (const auto& entity : chunk) {
        if (!builder.add(entity)) ++stats_.encode_failed;
      }
      if (builder.size() != chunk.size()) {
        ++stats_.encode_failed;  // a reserved net id: never commit a track the server did not receive
        chunk.clear();
        chunk_ids.clear();
        chunk_components.clear();
        chunk_poses.clear();
        return false;
      }
      const auto payload = builder.build();
      if (!payload) {
        ++stats_.encode_failed;
        chunk.clear();
        chunk_ids.clear();
        chunk_components.clear();
        chunk_poses.clear();
        return false;
      }
      if (!sink_.control(T(X4MP::Proto::MsgType::EntitySpawn), *payload)) {
        ++stats_.send_failed;
        return false;
      }
      // Commit only after the frame left: a retry must spawn exactly the entities the server does not have yet.
      for (std::size_t i = 0; i < chunk_ids.size(); ++i) {
        EntityTrack track;
        track.net_id = chunk_ids[i];
        track.sector = sector;
        track.component = chunk_components[i];
        track.sent = chunk_poses[i];
        track.sent_at_s = now_s;
        track.sent_sector = sector;
        track.sent_owner_ref = chunk[i].owner_ref;
        track.sent_macro_ref = chunk[i].macro_ref;
        track.read = chunk_poses[i];
        track.read_at_s = now_s;
        track.have_read = true;
        track.have_sent = true;
        track.status_sent_at_s = now_s;
        tracks_[chunk_ids[i]] = track;
      }
      ++stats_.spawn_messages;
      stats_.spawns += static_cast<std::uint32_t>(chunk.size());
      chunk.clear();
      chunk_ids.clear();
      chunk_components.clear();
      chunk_poses.clear();
      return true;
    };

    for (const auto net_id : *ids) {
      const auto existing = tracks_.find(net_id);
      if (existing != tracks_.end() && existing->second.have_sent) continue;
      const auto* row = index.find(net_id);
      if (row == nullptr || row->component == 0) continue;
      if (row->macro_ref == 0) {
        ++stats_.skipped_no_macro;  // the string table is not ready: the next scan pass retries
        continue;
      }
      TrackPose pose;
      if (!reader_ || !reader_(row->component, pose) || !is_finite(pose)) {
        ++stats_.poll_failed;
        continue;
      }
      const auto qx = wire::quantize_position(pose.x), qy = wire::quantize_position(pose.y), qz = wire::quantize_position(pose.z);
      const auto ry = wire::quantize_rotation(pose.yaw), rp = wire::quantize_rotation(pose.pitch),
                 rr = wire::quantize_rotation(pose.roll);
      if (!qx || !qy || !qz || !ry || !rp || !rr) {
        ++stats_.encode_failed;
        continue;
      }
      authority::SpawnEntity entity;
      entity.net_id = net_id;
      entity.kind = static_cast<authority::SpawnKind>(row->kind);
      entity.origin = authority::SpawnOrigin::AuthorityRuntime;
      entity.macro_ref = row->macro_ref;
      entity.owner_ref = row->owner_ref;
      entity.sector = sector;
      entity.hull = 255;
      entity.shield = 255;
      entity.px = *qx;
      entity.py = *qy;
      entity.pz = *qz;
      entity.yaw = *ry;
      entity.pitch = *rp;
      entity.roll = *rr;
      chunk.push_back(entity);
      chunk_ids.push_back(net_id);
      chunk_components.push_back(row->component);
      // The client holds the quantised pose, not the read one: keep exactly what it will decode so its prediction and
      // the authority's dead reckoning agree.
      chunk_poses.push_back(TrackPose{wire::dequantize_position(*qx), wire::dequantize_position(*qy), wire::dequantize_position(*qz),
                                      wire::dequantize_rotation(*ry), wire::dequantize_rotation(*rp), wire::dequantize_rotation(*rr)});
      if (chunk.size() >= kMaxSpawnsPerMessage && !send_chunk()) {
        ok = false;
        break;
      }
    }
    if (ok) ok = send_chunk();
    if (!ok) {
      ++it;  // retry the rest next tick; the already-sent tracks are skipped by have_sent
      continue;
    }

    const auto payload = encode_sector_complete(sector, set.epoch(), static_cast<std::uint32_t>(ids->size()));
    if (!sink_.control(T(X4MP::Proto::MsgType::SectorComplete), payload)) {
      ++stats_.send_failed;
      ++it;
      continue;
    }
    ++stats_.sectors_complete;
    it = spawn_pending_.erase(it);
  }
}

void EntityStream::update_pass(double now_s, const CaptureSet& set, const UniverseIndex& index) {
  states_.clear();
  for (const auto sector : sectors_) {
    const auto* ids = index.ids_of(sector);
    if (ids == nullptr || ids->empty()) continue;

    const auto poll = [&](std::uint32_t net_id) {
      const auto it = tracks_.find(net_id);
      if (it == tracks_.end() || !it->second.have_sent) return;
      EntityTrack& track = it->second;
      if (track.visited_tick == visit_tick_) return;
      track.visited_tick = visit_tick_;
      visit(track, sector, now_s, set, states_);
    };

    if (focus_sweep_.erase(sector) != 0) {
      for (const auto net_id : *ids) poll(net_id);
    } else {
      const auto slice = scheduler_.next(sector, ids->size());
      for (std::size_t i = 0; i < slice.count; ++i) poll((*ids)[(slice.begin + i) % ids->size()]);
    }
    // Entities inside a focus sphere need the sphere's rate, not the sector's: they are polled every tick in addition.
    if (const auto focus = focus_ids_.find(sector); focus != focus_ids_.end()) {
      for (const auto net_id : focus->second) poll(net_id);
    }
  }
}

void EntityStream::visit(EntityTrack& track, std::uint16_t sector, double now_s, const CaptureSet& set,
                         std::vector<StateSample>& out) {
  if (track.component == 0 || !reader_) return;
  TrackPose pose;
  if (!reader_(track.component, pose) || !is_finite(pose)) {
    ++stats_.poll_failed;
    return;
  }
  ++stats_.polls;

  if (track.have_read) {
    if (const auto velocity = finite_difference(track.read, track.read_at_s, pose, now_s, cfg_.dr)) track.velocity = *velocity;
  }
  track.read = pose;
  track.read_at_s = now_s;
  track.have_read = true;

  if (track.sector != sector) {
    auto& old_list = focus_ids_[track.sector];
    std::erase(old_list, track.net_id);
    track.sector = sector;
    track.in_focus = false;
  }
  const bool in_focus = set.rate_at(sector, static_cast<float>(pose.x), static_cast<float>(pose.y), static_cast<float>(pose.z)) >
                        set.rate_of(sector);
  if (in_focus != track.in_focus) {
    track.in_focus = in_focus;
    auto& list = focus_ids_[sector];
    if (in_focus) {
      list.push_back(track.net_id);
    } else {
      std::erase(list, track.net_id);
    }
  }

  DrInput in;
  in.sent = track.sent;
  in.now = pose;
  in.velocity = track.velocity;
  in.sent_at_s = track.sent_at_s;
  in.now_s = now_s;
  in.sent_flags = track.sent_flags;
  in.now_flags = 0;  // no docked/travel-drive source yet (W4 adds it with the manifest)
  in.sent_sector = track.sent_sector;
  in.now_sector = sector;
  in.observer_distance_m = observer_distance(sector, pose);
  const auto reason = classify(in, cfg_.dr);
  if (reason == UpdateReason::None) return;

  const auto qx = wire::quantize_position(pose.x), qy = wire::quantize_position(pose.y), qz = wire::quantize_position(pose.z);
  const auto ry = wire::quantize_rotation(pose.yaw), rp = wire::quantize_rotation(pose.pitch),
             rr = wire::quantize_rotation(pose.roll);
  if (!qx || !qy || !qz || !ry || !rp || !rr) {
    ++stats_.encode_failed;
    return;
  }
  const bool coarse = wire::needs_coarse_velocity(track.velocity.x, track.velocity.y, track.velocity.z).value_or(false);
  const auto vx = wire::quantize_velocity(track.velocity.x, coarse), vy = wire::quantize_velocity(track.velocity.y, coarse),
             vz = wire::quantize_velocity(track.velocity.z, coarse);
  if (!vx || !vy || !vz) {
    ++stats_.encode_failed;
    return;
  }

  std::uint16_t flags = coarse ? kFlagVelCoarse : 0;
  if (reason == UpdateReason::Keepalive) flags |= kFlagKeyframe;

  StateSample sample;
  sample.net_id = track.net_id;
  sample.sector = sector;
  sample.flags = flags;
  sample.px = *qx;
  sample.py = *qy;
  sample.pz = *qz;
  sample.yaw = *ry;
  sample.pitch = *rp;
  sample.roll = *rr;
  sample.vx = *vx;
  sample.vy = *vy;
  sample.vz = *vz;
  out.push_back(sample);

  switch (reason) {
    case UpdateReason::Threshold: ++stats_.reason_threshold; break;
    case UpdateReason::Rotation: ++stats_.reason_rotation; break;
    case UpdateReason::Flags: ++stats_.reason_flags; break;
    case UpdateReason::Sector: ++stats_.reason_sector; break;
    case UpdateReason::Keepalive: ++stats_.reason_keepalive; break;
    case UpdateReason::None: break;
  }

  // What the client will hold: dequantised position/rotation and the quantised velocity its predictor runs with.
  track.sent = TrackPose{wire::dequantize_position(*qx), wire::dequantize_position(*qy), wire::dequantize_position(*qz),
                         wire::dequantize_rotation(*ry), wire::dequantize_rotation(*rp), wire::dequantize_rotation(*rr)};
  track.sent_at_s = now_s;
  track.sent_flags = flags;
  track.sent_sector = sector;
  track.velocity = TrackVelocity{wire::dequantize_velocity(*vx, coarse), wire::dequantize_velocity(*vy, coarse),
                                 wire::dequantize_velocity(*vz, coarse)};
}

void EntityStream::flush_states(std::int64_t capture_time_us, double game_time) {
  // Always at least one WorldUpdate per tick: the server stamps every entity with its game time and does not replicate
  // until it has seen one (M3-14). An empty one is the 20 Hz keepalive.
  std::size_t offset = 0;
  do {
    const std::size_t count = std::min(kMaxStatesPerWorldUpdate, states_.size() - offset);
    const auto payload = encode_world_update(tick_, static_cast<std::uint64_t>(capture_time_us), game_time,
                                             std::span<const StateSample>(states_).subspan(offset, count));
    if (sink_.realtime(T(X4MP::Proto::MsgType::WorldUpdate), payload)) {
      ++stats_.update_messages;
      stats_.updates += static_cast<std::uint32_t>(count);
    } else {
      ++stats_.send_failed;
    }
    offset += count;
  } while (offset < states_.size());
  states_.clear();
}

void EntityStream::flush_changes() {
  for (const auto& change : change_now_) {
    const auto it = tracks_.find(change.net_id);
    if (it == tracks_.end() || !it->second.have_sent) continue;  // the spawn carries the new value instead
    if (sink_.control(T(X4MP::Proto::MsgType::EntityChange), encode_entity_change(change))) {
      ++stats_.changes;
      it->second.sent_owner_ref = change.owner_ref;
      it->second.sent_macro_ref = change.macro_ref;
    } else {
      ++stats_.send_failed;
    }
  }
  change_now_.clear();
}

void EntityStream::flush_statuses(std::int64_t capture_time_us, double now_s) {
  statuses_.clear();
  const auto send_batch = [&] {
    if (statuses_.empty()) return;
    if (sink_.realtime(T(X4MP::Proto::MsgType::EntityStatusBatch),
                       encode_entity_status_batch(static_cast<std::uint64_t>(capture_time_us), statuses_))) {
      stats_.statuses += static_cast<std::uint32_t>(statuses_.size());
    } else {
      ++stats_.send_failed;
    }
    statuses_.clear();
  };

  for (auto& [net_id, track] : tracks_) {
    const double age = now_s - track.status_sent_at_s;
    const bool changed = track.status_dirty && age >= cfg_.status_min_interval_s;
    if (!changed && age < cfg_.status_keepalive_s) continue;
    statuses_.push_back(StatusSample{net_id, track.hull, track.shield, 0});
    track.sent_hull = track.hull;
    track.sent_shield = track.shield;
    track.status_dirty = false;
    track.status_sent_at_s = now_s;
    if (statuses_.size() >= kMaxStatusesPerBatch) send_batch();
  }
  send_batch();
}

double EntityStream::observer_distance(std::uint16_t sector, const TrackPose& pose) const noexcept {
  if (observer_sector_ == 0 || sector != observer_sector_) return cfg_.observer_unknown_m;
  const double dx = pose.x - observer_x_, dy = pose.y - observer_y_, dz = pose.z - observer_z_;
  return std::sqrt(dx * dx + dy * dy + dz * dz);
}

}  // namespace x4mp::features::auth
