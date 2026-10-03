#pragma once
// features/selfship/selfship_hub: the small process-wide rendezvous between the own-ship feature (this directory), the join feature
// (it owns the session) and the authority flow (it reacts to the seat) (M3-09). Main thread only (the frame thread: both features run
// in on_frame), so no locking.
//
//  * The join feature installs a SelfShipLink while the node is InGame (welcomed, NodeReady sent) and clears it otherwise. The link is
//    the Realtime-lane send (UDP when the lane is active, TCP otherwise: the Session decides) and the server-clock estimate.
//  * The selfship feature publishes its status every frame: seated, own ship, sector index, seat edge counter. The authority flow and
//    later features (M3-11/12) read it.
//  * Later tasks tell the hub the net id of the own ship (the avatar, M3-12); until then PlayerState carries 0 and the server stamps it.

#include <cstdint>
#include <functional>
#include <span>
#include <utility>

namespace x4mp::features::selfship {

struct SelfShipLink {
  // The frame goes out on the Realtime lane. false = refused (not connected, outbox full).
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_realtime;
  // The estimate of the server clock in microseconds (local steady clock + the applied offset); false until the clock sync has a sample.
  std::function<bool(std::int64_t& server_now_us)> server_now;
};

struct SelfShipStatus {
  bool seated = false;               // the player sits in the pilot seat (GetPlayerOccupiedShipID != 0)
  std::uint64_t ship = 0;            // the own ship's local UniverseID (also while standing in it), 0 = unknown
  std::uint16_t sector = 0;          // sector index of the own ship (0 = unknown / map not ready)
  std::uint32_t seat_edges = 0;      // counts every sit-down, stand-up and ship change
  std::uint32_t sit_downs = 0;       // counts the sit-downs only
  bool map_ready = false;
  bool seta_blocked = false;         // SETA is being blocked right now (connected)
};

class SelfShipHub {
 public:
  void set_link(SelfShipLink link) { link_ = std::move(link); }
  void clear_link() { link_ = {}; }
  [[nodiscard]] bool linked() const noexcept { return static_cast<bool>(link_.send_realtime); }
  bool send_realtime(std::uint16_t type, std::span<const std::uint8_t> payload) const {
    return link_.send_realtime && link_.send_realtime(type, payload);
  }
  [[nodiscard]] bool server_now(std::int64_t& out) const { return link_.server_now && link_.server_now(out); }

  void publish(const SelfShipStatus& s) noexcept { status_ = s; }
  [[nodiscard]] const SelfShipStatus& status() const noexcept { return status_; }

  void set_own_net_id(std::uint32_t id) noexcept { own_net_id_ = id; }
  [[nodiscard]] std::uint32_t own_net_id() const noexcept { return own_net_id_; }

  void reset() { *this = SelfShipHub{}; }

 private:
  SelfShipLink link_;
  SelfShipStatus status_;
  std::uint32_t own_net_id_ = 0;
};

[[nodiscard]] SelfShipHub& selfship_hub() noexcept;

}  // namespace x4mp::features::selfship
