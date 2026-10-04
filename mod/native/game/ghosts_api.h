#pragma once
// game/ghosts_api: the game calls the ghost feature needs, on top of GameApi (M3-10; GameFns stays frozen, docs/m3-plan.md 6).
//
//   * The ONE place where our pose convention meets the game's UIPosRot: positions are sector-local metres, angles RADIANS (S13.4:
//     UIPosRot angles are radians). The axis order / signs of yaw, pitch, roll are the convention of core/ghost/math.h
//     (R = Ry(yaw) Rx(pitch) Rz(roll)); if the first in-game look shows a ghost flying sideways or nose-down, flip kPitchSign /
//     kRollSign / kYawSign below and nowhere else.
//   * Exports that are not in GameFns (faction and ship lists, used for the faction check and for finding our ghosts by idcode after
//     a save load) are resolved BY NAME through the loader's lookup, like the janitor does. A missing export is an "unknown", never
//     a crash.
//   * Spawning, moving, hiding: spawn() + make_inert() give a ghost that stays where it is put (S13.1: drift 0.000 m in 60 s).
//     Removal is NOT here: the only removal path is game::safe_remove().
//
// Main thread only (every call goes through GameApi's thread check). SDK-free.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "game/game_api.h"

namespace x4mp::game {

// Sign constants of the UIPosRot angle convention (S13.4: radians; axis order guessed, see the header comment).
inline constexpr double kYawSign = 1.0;
inline constexpr double kPitchSign = 1.0;
inline constexpr double kRollSign = 1.0;

// A pose in the game's sector frame: metres and radians.
struct GhostPose {
  double x = 0, y = 0, z = 0;
  double yaw = 0, pitch = 0, roll = 0;
};

[[nodiscard]] PosRotPod to_pos_rot(const GhostPose& p) noexcept;
[[nodiscard]] GhostPose from_pos_rot(const PosRotPod& p) noexcept;

enum class FactionState : std::uint8_t { Unknown, Present, Missing };

class GhostsApi {
 public:
  // `get` resolves exports by name (X4NativeAPI::get_game_function or a fake); may be empty.
  GhostsApi(const GameApi& api, GetFunctionFn get);

  // 0 = refused / missing export / the game returned 0. The ship is NOT inert yet: call make_inert() right after.
  [[nodiscard]] UniverseId spawn(std::string_view macro, UniverseId sector, const GhostPose& pose, std::string_view owner) const;
  // ActivateObject(false) + forced radar visibility (the MD dress also sets radar; the native call is the one that never waits a frame).
  bool make_inert(UniverseId id) const;
  // One SetObjectSectorPos: sector id + sector-local pose. The only call per frame and ghost.
  bool place(UniverseId id, UniverseId sector, const GhostPose& pose) const;
  [[nodiscard]] std::optional<GhostPose> pose(UniverseId id) const;
  [[nodiscard]] bool valid(UniverseId id) const;
  [[nodiscard]] bool wrecked(UniverseId id) const;
  [[nodiscard]] std::string id_code(UniverseId id) const;
  [[nodiscard]] std::string name(UniverseId id) const;
  [[nodiscard]] UniverseId sector_of(UniverseId id) const;  // GetContextByClass(id, "sector"); 0 = unknown

  // The ship the local player flies / stands in (occupied, else controlled, else the player object); 0 = none.
  [[nodiscard]] UniverseId local_ship() const;

  // Present / Missing from GetAllFactions (hidden ones included); Unknown when the exports do not exist (hostsim, an old game).
  // The list is cached; it is re-read when `refresh` is true or nothing has been read yet.
  [[nodiscard]] FactionState faction_state(std::string_view faction, bool refresh = false);
  // All ships owned by `faction` (GetAllFactionShips); empty when the export is missing or the faction does not exist.
  [[nodiscard]] std::vector<UniverseId> ships_of(std::string_view faction) const;

  [[nodiscard]] const GameApi& api() const noexcept { return api_; }

 private:
  const GameApi& api_;
  GetFunctionFn get_;
  std::vector<std::string> factions_;
  bool factions_known_ = false;
};

}  // namespace x4mp::game
