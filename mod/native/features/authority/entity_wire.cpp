#include "features/authority/entity_wire.h"

#include <flatbuffers/flatbuffers.h>

#include "common_generated.h"
#include "world_generated.h"

namespace x4mp::features::auth {

namespace P = X4MP::Proto;

namespace {
template <class Flat>
std::vector<std::uint8_t> finish(flatbuffers::FlatBufferBuilder& fbb, flatbuffers::Offset<Flat> root) {
  fbb.Finish(root);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}
}  // namespace

std::vector<std::uint8_t> encode_world_update(std::uint32_t authority_tick, std::uint64_t capture_time_us, double game_time,
                                              std::span<const StateSample> states) {
  flatbuffers::FlatBufferBuilder fbb(64 + states.size() * sizeof(P::EntityState));
  std::vector<P::EntityState> wire;
  wire.reserve(states.size());
  for (const auto& s : states) {
    wire.emplace_back(s.net_id, s.sector, s.flags, s.px, s.py, s.pz, s.yaw, s.pitch, s.roll, s.vx, s.vy, s.vz);
  }
  const auto vector = fbb.CreateVectorOfStructs(wire);
  return finish(fbb, P::CreateWorldUpdate(fbb, authority_tick, capture_time_us, game_time, vector));
}

std::vector<std::uint8_t> encode_entity_despawn(std::span<const DespawnSample> entries) {
  flatbuffers::FlatBufferBuilder fbb(64 + entries.size() * sizeof(P::DespawnEntry));
  std::vector<P::DespawnEntry> wire;
  wire.reserve(entries.size());
  for (const auto& e : entries) {
    wire.emplace_back(e.net_id, e.killer_net_id, static_cast<P::DespawnReason>(e.reason));
  }
  const auto vector = fbb.CreateVectorOfStructs(wire);
  return finish(fbb, P::CreateEntityDespawn(fbb, 0, vector));
}

std::vector<std::uint8_t> encode_entity_change(const ChangeSample& change) {
  flatbuffers::FlatBufferBuilder fbb(160);
  const auto paint = fbb.CreateString(change.paint);
  const auto logo = fbb.CreateString(change.logo);
  return finish(fbb, P::CreateEntityChange(fbb, 0, change.net_id, static_cast<P::ChangeField>(change.fields),
                                           change.owner_ref, change.owner_team, change.owner_player, 0, change.parent_net_id,
                                           change.macro_ref, static_cast<P::EntityKind>(change.kind), change.controller_player,
                                           nullptr, paint, logo));
}

std::vector<std::uint8_t> encode_entity_status_batch(std::uint64_t capture_time_us, std::span<const StatusSample> statuses) {
  flatbuffers::FlatBufferBuilder fbb(64 + statuses.size() * sizeof(P::EntityStatus));
  std::vector<P::EntityStatus> wire;
  wire.reserve(statuses.size());
  for (const auto& s : statuses) wire.emplace_back(s.net_id, s.hull, s.shield, s.status_flags);
  const auto vector = fbb.CreateVectorOfStructs(wire);
  return finish(fbb, P::CreateEntityStatusBatch(fbb, capture_time_us, vector));
}

}  // namespace x4mp::features::auth
