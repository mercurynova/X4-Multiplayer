#pragma once
// features/avatars/avatar_hub: the hand-over points of the authority's avatars (M3-11), like chat_hub()/team_hub():
//
//   join feature (hook lines in join_feature.cpp, main thread):  on_welcome / on_frame_message / session_ended
//       -> decoded PlayerShip, PlayerState, EntityDespawn, RosterUpdate and ServerSettingsUpdate wait here until the AvatarsFeature drains them
//   authority flow (features/authority):  set_services(...) while the node is the authority: net ids, string-table refs and the Control-lane
//       send come from it (one counter, one string table: the flow owns them); the flow asks for the manifest records at SaveGame time.
//   avatars feature:  set_snapshot_fn(...) so the flow can read the avatars' current poses.
//
// Main thread only.

#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <vector>

#include "features/avatars/avatar_director.h"
#include "features/avatars/avatar_plan.h"
#include "features/avatars/avatar_wire.h"

namespace x4mp::features::avatars {

struct AuthorityServices {
  std::function<bool()> connected;                                            // welcomed as the authority
  std::function<std::uint32_t()> alloc_net_id;
  std::function<void(std::uint32_t)> reserve_net_ids_above;                    // the counter must be > this
  std::function<std::uint32_t(StrKind, const std::string&)> string_ref;       // 0 = could not
  std::function<bool(std::uint16_t type, std::vector<std::uint8_t> payload)> send_control;
};

struct HubInputs {
  std::vector<RosterIn> rosters;
  std::vector<PlayerShipReq> ships;
  std::vector<PlayerStateIn> states;
  std::vector<DespawnIn> despawns;
  std::vector<SettingsIn> settings;
  bool welcomed = false;
  bool session_ended = false;
};

class AvatarHub {
 public:
  static constexpr std::size_t kMaxQueue = 256;

  // ---- join feature ----
  void on_welcome() { inputs_.welcomed = true; }
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);
  void session_ended();

  // ---- authority flow ----
  void set_services(AuthorityServices s) { services_ = std::move(s); have_services_ = true; }
  void clear_services() {
    services_ = {};
    have_services_ = false;
  }
  [[nodiscard]] bool authority() const noexcept { return have_services_; }
  [[nodiscard]] const AuthorityServices& services() const noexcept { return services_; }
  // The records (current poses) for the checkpoint manifest, taken at SaveGame time. Empty without an avatars feature.
  void set_snapshot_fn(std::function<std::vector<Record>()> fn) { snapshot_fn_ = std::move(fn); }
  [[nodiscard]] std::vector<Record> manifest_records() const { return snapshot_fn_ ? snapshot_fn_() : std::vector<Record>{}; }
  void set_max_net_id(std::uint32_t id) noexcept { max_net_id_ = id; }
  [[nodiscard]] std::uint32_t max_net_id() const noexcept { return max_net_id_; }

  // ---- avatars feature ----
  [[nodiscard]] HubInputs take_inputs();

  void reset();

 private:
  HubInputs inputs_;
  AuthorityServices services_;
  bool have_services_ = false;
  std::function<std::vector<Record>()> snapshot_fn_;
  std::uint32_t max_net_id_ = 0;
};

[[nodiscard]] AvatarHub& avatar_hub() noexcept;

}  // namespace x4mp::features::avatars
