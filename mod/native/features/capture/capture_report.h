#pragma once
// features/capture: the client half of M5-20 (docs/m5-plan.md W5, docs/mod-design.md 336-350).
//
// Capture is the one M5 action the MD event ring cannot see: `event_object_changed_owner` needs an `object` or `group`
// source, so the ring (md/x4mp_events.xml) never fires for an arbitrary ship. The client therefore subscribes to the game's
// own typed MD event EntityChangedOwner (type 175) through the platform, and this reporter judges each change:
//
//   * the changed component must be a ghost this node holds (ghost_hub().net_id_of_local), because only another player's
//     ship can be captured across the network;
//   * its owner must now be the local `player` faction (GameApi::owner_faction), asked of the game rather than inferred;
//   * a change that passes both tests sends ONE Intent{CaptureReport{target, new_owner_ref = 0}}. The authority resolves
//     the capturing team from its own TeamTable and ignores the owner ref, so nothing about the team travels upward.
//
// Dedupe: the hook can fire more than once for one capture (a wreck's pieces, a re-own after a save load), so a net id is
// reported at most once per kDedupeMs.
//
// Pure C++ and testable: every game and hub call arrives through CaptureReportDeps.

#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <unordered_map>

#include "features/events/event_wire.h"
#include "message_ids_generated.h"

namespace x4mp::features::capture {

struct CaptureReportDeps {
  std::function<std::uint32_t(std::uint64_t component)> ghost_net_id;      // 0 when the component is not a ghost here
  std::function<std::string(std::uint64_t component)> owner_faction;       // "" when the game could not answer
  std::function<std::uint16_t()> self_player_id;
  std::function<bool(X4MP::Proto::MsgType, std::span<const std::uint8_t>)> send_control;
  std::function<std::uint64_t()> now_ms;
};

struct CaptureReportStats {
  std::uint64_t sent = 0;
  std::uint64_t not_ghost = 0;    // an owner change on something this node does not hold as a ghost
  std::uint64_t not_player = 0;   // a ghost that changed hands between two other factions
  std::uint64_t unknown_owner = 0;  // the game gave no owner faction for the component
  std::uint64_t send_failed = 0;
  std::uint64_t deduped = 0;
  std::uint64_t bad = 0;
};

class CaptureReporter {
 public:
  static constexpr std::uint64_t kDedupeMs = 30'000;
  static constexpr std::size_t kMaxSeen = 256;

  explicit CaptureReporter(CaptureReportDeps deps);

  // Judges one EntityChangedOwner hook. Returns true when the change was a capture this node reported.
  bool on_owner_change(std::uint64_t component);

  [[nodiscard]] const CaptureReportStats& stats() const noexcept { return stats_; }
  void reset();

 private:
  CaptureReportDeps deps_;
  CaptureReportStats stats_;
  std::unordered_map<std::uint32_t, std::uint64_t> reported_ms_;  // net id -> the ms of the last report
  std::uint64_t next_key_ = 0;
};

}  // namespace x4mp::features::capture
