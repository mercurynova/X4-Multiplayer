#pragma once
// features/authority: the encoders of the authority -> server stream (M4-08/M4-10/M4-11; docs/m4-plan.md W2).
//
// WorldUpdate, EntityDespawn, EntityChange and EntityStatusBatch are flatbuffers whose struct fields are quantised wire
// values. EntitySpawn keeps its own builder (core/authority/entity_spawn.h) because its game_time rule is a protocol
// invariant; this file covers the other four.
//
// The structs below mirror the generated flatbuffer structs (common.fbs) so that the streamer's hot path never includes a
// generated header and never allocates a flatbuffer struct vector it cannot reuse. The .cpp converts one to the other.
//
// Batch caps: WorldUpdate is a Realtime message and the protocol caps a datagram at 1200 B (protocol.md 10.1); 32 states
// of 32 B plus the flatbuffer header fit under 1100 B, so a WorldUpdate is always one datagram once the UDP lane lands
// (M4-25). Spawns and despawns are Control messages (chunked up to 256 KiB), but small chunks keep a frame cheap.

#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace x4mp::features::auth {

inline constexpr std::size_t kMaxStatesPerWorldUpdate = 32;
inline constexpr std::size_t kMaxStatusesPerBatch = 64;
inline constexpr std::size_t kMaxSpawnsPerMessage = 32;
inline constexpr std::size_t kMaxDespawnsPerMessage = 64;

// Mirrors P::EntityState (32 B).
struct StateSample {
  std::uint32_t net_id = 0;
  std::uint16_t sector = 0;
  std::uint16_t flags = 0;
  std::int32_t px = 0, py = 0, pz = 0;
  std::int16_t yaw = 0, pitch = 0, roll = 0;
  std::int16_t vx = 0, vy = 0, vz = 0;
};

// Mirrors P::EntityStatus (8 B).
struct StatusSample {
  std::uint32_t net_id = 0;
  std::uint8_t hull = 255;
  std::uint8_t shield = 255;
  std::uint16_t status_flags = 0;
};

// Mirrors P::DespawnEntry; `reason` is a P::DespawnReason value.
struct DespawnSample {
  std::uint32_t net_id = 0;
  std::uint32_t killer_net_id = 0;
  std::uint8_t reason = 0;
};

// The subset of EntityChange the authority fills today (fields is a P::ChangeField bit mask).
struct ChangeSample {
  std::uint32_t net_id = 0;
  std::uint16_t fields = 0;
  std::uint32_t owner_ref = 0;
  std::uint16_t owner_team = 0;
  std::uint16_t owner_player = 0;
  std::uint32_t parent_net_id = 0;
  std::uint32_t macro_ref = 0;
  std::uint8_t kind = 0;
  std::uint16_t controller_player = 0;
  std::string paint;  // EntityChange{Paint}: paint mod ware id; "" = no paint (M12)
};

[[nodiscard]] std::vector<std::uint8_t> encode_world_update(std::uint32_t authority_tick, std::uint64_t capture_time_us,
                                                            double game_time, std::span<const StateSample> states);
[[nodiscard]] std::vector<std::uint8_t> encode_entity_despawn(std::span<const DespawnSample> entries);
[[nodiscard]] std::vector<std::uint8_t> encode_entity_change(const ChangeSample& change);
[[nodiscard]] std::vector<std::uint8_t> encode_entity_status_batch(std::uint64_t capture_time_us,
                                                                   std::span<const StatusSample> statuses);

}  // namespace x4mp::features::auth
