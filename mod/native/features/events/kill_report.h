#pragma once
// features/events/kill_report: turning a captured kill into an Intent (M5-06; docs/m5-plan.md W2).
//
// The MD half says "this component died, and that component killed it". The wire says "net id 42 died, killed by net id 43". This
// class is the translation, and it is deliberately dependency-injected: the feature supplies the netmap lookup, the session's
// send_control and the two ids, so a test can pin the decision table without a game, a socket or a ghost registry.
//
// Decisions this pins (AC item 2 and M5-06):
//   - Only EventKind::Kill produces a claim. A Destroyed (X) line has no killer and is the authority's problem, not a claim.
//   - The victim must be a ghost THIS node holds. A kill of a local NPC that is not a ghost is not ours to report; the authority
//     sees it as a row that disappeared from its scan.
//   - The killer is this player's own ship when it is known, else 0. It is never derived from the MD owner string: a weapon or a
//     station can be the MD killer, and the wire wants a ship net id.
//   - The request key is (self player id, monotonic counter): unique per node in a session, and stable across a resend, because a
//     resend is a new call with a new key only if the caller decides so.

#include <cstdint>
#include <functional>
#include <span>
#include <utility>
#include <vector>

#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "message_ids_generated.h"

namespace x4mp::features::events {

struct KillReportDeps {
  // local X4 component -> net id; false when this node holds no ghost for it.
  std::function<bool(std::uint64_t local_id, std::uint32_t& net_id)> net_of_local;
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_control;
  std::function<std::uint16_t()> self_player_id;
  std::function<std::uint32_t()> self_ship_net_id;  // 0 when the player's own ship net id is not known
};

struct KillReportStats {
  std::uint32_t sent = 0;         // claims that went out
  std::uint32_t unmapped = 0;     // kills of something this node does not hold as a ghost
  std::uint32_t send_failed = 0;  // the session refused the frame
};

class KillReporter {
 public:
  explicit KillReporter(KillReportDeps deps) : deps_(std::move(deps)) {}

  // True when a claim was sent. A false return is one of the three counters above, never an exception.
  bool on_event(const MdEvent& event) {
    if (event.kind != EventKind::Kill) return false;
    std::uint32_t target = 0;
    if (event.component == 0 || !deps_.net_of_local || !deps_.net_of_local(event.component, target) || target == 0) {
      ++stats_.unmapped;
      return false;
    }
    const std::uint16_t self = deps_.self_player_id ? deps_.self_player_id() : 0;
    IntentRequest request;
    request.key_lo = ++next_key_;
    request.key_hi = self;
    request.request_id = static_cast<std::uint32_t>(next_key_);
    request.player_id = self;
    KillClaimData claim;
    claim.target = target;
    claim.killer = deps_.self_ship_net_id ? deps_.self_ship_net_id() : 0;
    claim.weapon_macro_ref = 0;
    const std::vector<std::uint8_t> bytes = encode_kill_claim(request, claim);
    if (!deps_.send_control || !deps_.send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent), bytes)) {
      ++stats_.send_failed;
      return false;
    }
    ++stats_.sent;
    return true;
  }

  [[nodiscard]] const KillReportStats& stats() const noexcept { return stats_; }

  void reset() {
    stats_ = KillReportStats{};
    next_key_ = 0;
  }

 private:
  KillReportDeps deps_;
  KillReportStats stats_{};
  std::uint64_t next_key_ = 0;
};

}  // namespace x4mp::features::events
