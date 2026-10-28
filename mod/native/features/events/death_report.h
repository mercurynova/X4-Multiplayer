#pragma once
// features/events/death_report: the node's own ship died, so say so once (M5-10; docs/m5-plan.md W3).
//
// The MD half already emits `P;` when the destroyed object is `player.occupiedship` (md/x4mp_events.xml). This is the one hop
// between that line and the wire: `Intent{PlayerDeath{killer}}` upward, with a fresh request key so the server books it once.
//
// What this file deliberately does NOT do: it never removes, destroys, hides or renames anything. The player's ship is the
// game's to destroy (mod-design 6.1); the mod only reports that it happened. The authority answers with the despawn for the
// player's *avatar* on the other nodes, and the local game runs its own death flow.
//
// Same shape as kill_report.h: SDK-free, dependency-injected, and the tests drive it with lambdas.

#include <cstdint>
#include <functional>
#include <span>
#include <utility>
#include <vector>

#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "message_ids_generated.h"

namespace x4mp::features::events {

struct DeathReportDeps {
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_control;
  std::function<std::uint16_t()> self_player_id;
};

struct DeathReportStats {
  std::uint32_t sent = 0;
  std::uint32_t send_failed = 0;
};

class DeathReporter {
 public:
  explicit DeathReporter(DeathReportDeps deps) : deps_(std::move(deps)) {}

  // True when a PlayerDeath intent went out. A line that is not EventKind::PlayerDeath is not this class's business and is
  // ignored without touching a counter.
  bool on_event(const MdEvent& event) {
    if (event.kind != EventKind::PlayerDeath) return false;
    const std::uint16_t self = deps_.self_player_id ? deps_.self_player_id() : 0;
    IntentRequest request;
    request.key_lo = ++next_key_;
    request.key_hi = self;
    request.request_id = static_cast<std::uint32_t>(next_key_);
    request.player_id = self;
    PlayerDeathData death;
    death.killer = 0;  // the MD line carries no killer for the player's own ship; the authority fills what it knows
    const std::vector<std::uint8_t> bytes = encode_player_death(request, death);
    if (!deps_.send_control || !deps_.send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent), bytes)) {
      ++stats_.send_failed;
      return false;
    }
    ++stats_.sent;
    return true;
  }

  [[nodiscard]] const DeathReportStats& stats() const noexcept { return stats_; }
  void reset() noexcept { stats_ = DeathReportStats{}; }

 private:
  DeathReportDeps deps_;
  DeathReportStats stats_{};
  std::uint64_t next_key_ = 0;
};

}  // namespace x4mp::features::events
