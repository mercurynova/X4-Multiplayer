#pragma once
// features/ghosts: the process-wide hand-over between the join feature's session pump and the ghost feature (M3-10), like chat_hub():
//
//   join feature, K::Frame      ghosts::ghost_hub().on_frame_message(type, payload, self_player_id)
//   join feature, stop_session  ghosts::ghost_hub().session_ended()
//
// The hub only forwards to the GhostCore the ghost feature attached for this DLL incarnation (nothing happens before it attached or
// after it detached) and only on a pure client node (the authority has no ghosts: its players' ships are its avatars, M3-11).
// Main thread only (the session is pumped there).

#include <cstdint>
#include <span>

#include "features/ghosts/ghost_core.h"

namespace x4mp::features::ghosts {

class GhostHub {
 public:
  void attach(GhostCore* core) noexcept { core_ = core; }
  void detach() noexcept { core_ = nullptr; }
  [[nodiscard]] GhostCore* core() const noexcept { return core_; }

  // Every inbound session frame; ignored unless the node is a client and the type is one the ghost core reads.
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_player_id);
  // The session is over for good (leave, new join): all ghosts are removed on the next frame.
  void session_ended();
  // True when the local object id is a ghost this node spawned (the janitor and the takeover must skip those).
  [[nodiscard]] bool is_ghost_local(std::uint64_t local_id) const noexcept;

 private:
  GhostCore* core_ = nullptr;
};

[[nodiscard]] GhostHub& ghost_hub() noexcept;

}  // namespace x4mp::features::ghosts
