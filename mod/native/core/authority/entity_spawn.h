#pragma once
// core/authority: the only encoder of EntitySpawn (M2-08; docs/mod-design.md 6.2, protocol.md EntitySpawn.game_time).
//
// EntitySpawn.game_time is the authority game time at which the entities' states were sampled. The server stamps the entities
// with it; a spawn that arrives with 0 falls back to the latest WorldUpdate's time, which makes spawns depend on lane ordering
// (the M1 lesson). So the authority must ALWAYS fill it: EntitySpawnBuilder takes the time as a constructor argument, refuses
// anything that is not a finite number above zero, and is the only code in the mod that serialises an EntitySpawn. There is no
// default, no setter and no overload that leaves the field out.
//
// Refusal: a debug build asserts (a bad game_time is a programming error); a release build marks the builder invalid and
// build()/build_batches() return SpawnError::InvalidGameTime. Nothing is ever emitted for an invalid builder.
//
// Pure C++, no X4 SDK. Not thread-safe (one builder per caller).

#include <cstddef>
#include <cstdint>
#include <expected>
#include <string>
#include <vector>

namespace x4mp::authority {

// Mirrors of the wire enums (common.fbs) so that headers above core/authority need no generated includes.
enum class SpawnKind : std::uint8_t {
  Unknown = 0, ShipXS, ShipS, ShipM, ShipL, ShipXL, Station, Gate, Accelerator, HighwayEntry, Satellite, NavBeacon,
  ResourceProbe, Mine, LaserTower, Drone, Lockbox, Crate, Other
};
enum class SpawnOrigin : std::uint8_t { Manifest = 0, AuthorityRuntime, PlayerShip, PlayerBuilt };

// One EntityRecord (+ its EntityState). Quantised state values as on the wire (protocol.md 11).
struct SpawnEntity {
  std::uint32_t net_id = 0;  // must be neither 0 nor 0xFFFFFFFF
  SpawnKind kind = SpawnKind::Unknown;
  SpawnOrigin origin = SpawnOrigin::AuthorityRuntime;
  std::uint32_t macro_ref = 0;
  std::uint32_t owner_ref = 0;
  std::uint16_t owner_team = 0;
  std::uint16_t owner_player = 0;
  std::uint32_t parent_net_id = 0;
  std::uint16_t controller_player = 0;
  std::string name;
  std::string idcode;
  std::uint8_t hull = 255;
  std::uint8_t shield = 255;
  std::uint16_t sector = 0;
  std::uint16_t flags = 0;
  std::int32_t px = 0, py = 0, pz = 0;
  std::int16_t yaw = 0, pitch = 0, roll = 0;
  std::int16_t vx = 0, vy = 0, vz = 0;
};

enum class SpawnError : std::uint8_t {
  InvalidGameTime,  // the builder was constructed with 0, a negative, NaN or infinite time
  NoEntities,
  InvalidNetId,     // an entity has net_id 0 or 0xFFFFFFFF
  BadBatchSize,
};
[[nodiscard]] const char* to_string(SpawnError error) noexcept;

using SpawnPayload = std::vector<std::uint8_t>;  // a complete FlatBuffers buffer: the payload of one EntitySpawn frame

class EntitySpawnBuilder {
 public:
  // True for a time the server can use: finite and > 0.
  [[nodiscard]] static bool is_valid_game_time(double game_time) noexcept;

  // Debug: asserts is_valid_game_time(). Release: the builder is invalid and every build returns InvalidGameTime.
  explicit EntitySpawnBuilder(double game_time) noexcept;

  [[nodiscard]] bool valid() const noexcept { return valid_; }
  [[nodiscard]] double game_time() const noexcept { return game_time_; }
  [[nodiscard]] std::size_t size() const noexcept { return entities_.size(); }

  // Returns false (and adds nothing) when the builder is invalid or the net_id is reserved.
  bool add(SpawnEntity entity);

  // One EntitySpawn carrying every added entity; game_time is the constructor argument.
  [[nodiscard]] std::expected<SpawnPayload, SpawnError> build() const;
  // The entities split over several EntitySpawn payloads of at most `max_per_message` records (frames stay under the cap);
  // every payload carries the same game_time.
  [[nodiscard]] std::expected<std::vector<SpawnPayload>, SpawnError> build_batches(std::size_t max_per_message) const;

 private:
  [[nodiscard]] std::expected<SpawnPayload, SpawnError> encode(std::size_t first, std::size_t count) const;

  double game_time_ = 0.0;
  bool valid_ = false;
  std::vector<SpawnEntity> entities_;
};

}  // namespace x4mp::authority
