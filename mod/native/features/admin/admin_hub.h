#pragma once
// features/admin: the in-game half of the admin bridge (docs/m3-plan.md Q6, protocol.md "AdminCommand").
//
//   ui/ command -> bridge verb x4mp.admin -> admin_feature -> AdminCommand on the Control lane -> the server's AdminModule
//   -> AdminResult on the Control lane -> admin_hub() -> raised back to Lua as x4mp.admin_result.
//
// Only kick and ban are sent from here: they are the two operations whose target the game can name (an online player id).
// Everything else in AdminBody belongs to the web admin API. The requesting node must hold the Admin role - the server checks
// that in MessagePolicy, so a client that is not an admin gets no answer beyond the policy refusal.
//
// This header is the inbound half: the join feature fans Control-lane frames in from its switch, admin_feature drains them.

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <span>
#include <string>
#include <vector>

namespace x4mp::features::admin {

// What the server said about one request; the feature raises it to Lua.
struct Result {
  std::uint32_t request_id = 0;  // the id the command carried, so the UI can match it
  bool ok = false;
  std::string message;
};

// Process-wide, like onfoot_hub(): one instance, main thread only.
class AdminHub {
 public:
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);
  [[nodiscard]] std::vector<Result> take_results();

 private:
  static constexpr std::size_t kMaxResults = 32;

  std::mutex m_;
  std::vector<Result> results_;
};

AdminHub& admin_hub();

}  // namespace x4mp::features::admin
