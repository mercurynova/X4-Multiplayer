#include "core/authority/entity_spawn.h"

#include <algorithm>
#include <cassert>
#include <cmath>

#include "world_generated.h"

namespace x4mp::authority {

const char* to_string(SpawnError error) noexcept {
  switch (error) {
    case SpawnError::InvalidGameTime: return "InvalidGameTime";
    case SpawnError::NoEntities: return "NoEntities";
    case SpawnError::InvalidNetId: return "InvalidNetId";
    case SpawnError::BadBatchSize: return "BadBatchSize";
  }
  return "?";
}

bool EntitySpawnBuilder::is_valid_game_time(double game_time) noexcept { return std::isfinite(game_time) && game_time > 0.0; }

EntitySpawnBuilder::EntitySpawnBuilder(double game_time) noexcept : game_time_(game_time), valid_(is_valid_game_time(game_time)) {
  // A zero game_time makes the server fall back to lane-order dependent timing: a programming error, loud in debug.
  assert(valid_ && "EntitySpawn.game_time must be finite and > 0");
  if (!valid_) game_time_ = 0.0;
}

bool EntitySpawnBuilder::add(SpawnEntity entity) {
  if (!valid_) return false;
  if (entity.net_id == 0 || entity.net_id == 0xFFFFFFFFu) return false;
  entities_.push_back(std::move(entity));
  return true;
}

std::expected<SpawnPayload, SpawnError> EntitySpawnBuilder::encode(std::size_t first, std::size_t count) const {
  namespace P = X4MP::Proto;
  if (!valid_ || !is_valid_game_time(game_time_)) return std::unexpected(SpawnError::InvalidGameTime);
  if (count == 0) return std::unexpected(SpawnError::NoEntities);
  flatbuffers::FlatBufferBuilder fbb(256 + count * 160);
  std::vector<flatbuffers::Offset<P::EntityRecord>> records;
  records.reserve(count);
  for (std::size_t i = first; i < first + count; ++i) {
    const SpawnEntity& e = entities_[i];
    if (e.net_id == 0 || e.net_id == 0xFFFFFFFFu) return std::unexpected(SpawnError::InvalidNetId);
    const P::EntityState state(e.net_id, e.sector, e.flags, e.px, e.py, e.pz, e.yaw, e.pitch, e.roll, e.vx, e.vy, e.vz);
    const auto name = fbb.CreateString(e.name);
    const auto idcode = fbb.CreateString(e.idcode);
    records.push_back(P::CreateEntityRecord(fbb, e.net_id, static_cast<P::EntityKind>(e.kind), static_cast<P::EntityOrigin>(e.origin),
                                            e.macro_ref, e.owner_ref, e.owner_team, e.owner_player, e.parent_net_id,
                                            e.controller_player, name, idcode, e.hull, e.shield, &state));
  }
  // game_time is always written: the only EntitySpawn serialisation in the mod, and it cannot be reached with 0.
  fbb.Finish(P::CreateEntitySpawnDirect(fbb, 0, &records, game_time_));
  return SpawnPayload(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::expected<SpawnPayload, SpawnError> EntitySpawnBuilder::build() const { return encode(0, entities_.size()); }

std::expected<std::vector<SpawnPayload>, SpawnError> EntitySpawnBuilder::build_batches(std::size_t max_per_message) const {
  if (!valid_) return std::unexpected(SpawnError::InvalidGameTime);
  if (max_per_message == 0) return std::unexpected(SpawnError::BadBatchSize);
  if (entities_.empty()) return std::unexpected(SpawnError::NoEntities);
  std::vector<SpawnPayload> out;
  for (std::size_t at = 0; at < entities_.size(); at += max_per_message) {
    auto one = encode(at, std::min(max_per_message, entities_.size() - at));
    if (!one) return std::unexpected(one.error());
    out.push_back(std::move(*one));
  }
  return out;
}

}  // namespace x4mp::authority
