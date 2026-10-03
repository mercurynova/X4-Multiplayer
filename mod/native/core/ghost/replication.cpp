#include "core/ghost/replication.h"

#include <algorithm>

namespace x4mp::ghost {

ReplicationDecoder::Base& ReplicationDecoder::base_for(std::uint32_t net_id) {
  auto it = std::lower_bound(baselines_.begin(), baselines_.end(), net_id, [](const Base& b, std::uint32_t id) { return b.net_id < id; });
  if (it != baselines_.end() && it->net_id == net_id) return *it;
  Base nb;
  nb.net_id = net_id;
  return *baselines_.insert(it, nb);
}

void ReplicationDecoder::forget(std::uint32_t net_id) {
  auto it = std::lower_bound(baselines_.begin(), baselines_.end(), net_id, [](const Base& b, std::uint32_t id) { return b.net_id < id; });
  if (it != baselines_.end() && it->net_id == net_id) baselines_.erase(it);
}

void ReplicationDecoder::merge(std::uint64_t server_time_us, const wire::ReplicationEntry& e, EntityUpdate& out) {
  Base& b = base_for(e.net_id);
  Sample& s = b.s;
  if (e.mask & wire::kRepSector) s.sector = e.sector;
  if (e.mask & wire::kRepFlags) {
    s.flags = e.state_flags;
    b.coarse = (e.state_flags & kVelCoarse) != 0;
  }
  if (e.mask & wire::kRepPos) {
    s.pos = {wire::dequantize_position(e.pos_x), wire::dequantize_position(e.pos_y), wire::dequantize_position(e.pos_z)};
  }
  if (e.mask & wire::kRepRot) {
    s.rot = {wire::dequantize_rotation(e.yaw), wire::dequantize_rotation(e.pitch), wire::dequantize_rotation(e.roll)};
  }
  if (e.mask & wire::kRepVel) {
    s.vel = {wire::dequantize_velocity(e.vel_x, b.coarse), wire::dequantize_velocity(e.vel_y, b.coarse),
             wire::dequantize_velocity(e.vel_z, b.coarse)};
  }  // (a coarse-flag flip without a VEL field keeps the old dequantised velocity; the server resends VEL with such a flip)
  if (e.mask & wire::kRepStatus) {
    s.hull = e.hull;
    s.shield = e.shield;
  }
  s.t_us = static_cast<std::int64_t>(server_time_us);
  if (e.mask & wire::kRepTime) s.t_us = wire::time_from_offset_ms(e.time_ms, s.t_us);

  out.net_id = e.net_id;
  out.mask = e.mask;
  out.has_pose = (e.mask & wire::kRepPos) != 0;
  out.sample = s;
}

Interpolator& StreamSet::ensure(std::uint32_t net_id) {
  auto it = std::lower_bound(streams_.begin(), streams_.end(), net_id, [](const auto& p, std::uint32_t id) { return p.first < id; });
  if (it != streams_.end() && it->first == net_id) return *it->second;
  it = streams_.insert(it, {net_id, std::make_unique<Interpolator>(cfg_)});
  return *it->second;
}

Interpolator* StreamSet::find(std::uint32_t net_id) noexcept {
  auto it = std::lower_bound(streams_.begin(), streams_.end(), net_id, [](const auto& p, std::uint32_t id) { return p.first < id; });
  return (it != streams_.end() && it->first == net_id) ? it->second.get() : nullptr;
}

void StreamSet::erase(std::uint32_t net_id) {
  auto it = std::lower_bound(streams_.begin(), streams_.end(), net_id, [](const auto& p, std::uint32_t id) { return p.first < id; });
  if (it != streams_.end() && it->first == net_id) streams_.erase(it);
  decoder_.forget(net_id);
}

void StreamSet::clear() {
  streams_.clear();
  decoder_.reset();
}

}  // namespace x4mp::ghost
