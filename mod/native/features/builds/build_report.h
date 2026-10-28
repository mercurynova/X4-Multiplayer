#pragma once
// features/builds: the client half of M5-18 - a finished station build becomes exactly one Intent{StationBuildRequest}.
//
// Detection is the MD capture ring (`event_player_build_finished` -> "B;<buildanchor>;<macro>"), the same ring the kill and trade
// reports use. The ring names the component and the macro but no position and no sector, so the reporter asks the game for those
// through BuildReportDeps::describe; while the galaxy map or the component is not ready the request waits in a short pending list
// (kRetryMs) instead of being guessed or dropped.
//
// Dedupe is by the local component: `event_player_build_finished` fires once per build, but a reload replays nothing and a
// double-fired cue must still produce one request. The request key follows the M5 rule: key_lo counts, key_hi is the player.
//
// The authority answers with IntentResult{Accepted, result_net_id}; BuildsHub::on_frame_message binds that id to the local
// station (statics_hub().pin_station), so the station the client already built is the station every other node names.
//
// Pure C++ apart from the wire encoder: no game, no SDK, no locks. Frame thread only.

#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <unordered_set>
#include <vector>

#include "features/events/event_types.h"
#include "features/events/event_wire.h"
#include "message_ids_generated.h"  // X4MP::Proto::MsgType, the type the request goes out as

namespace x4mp::features::builds {

// What this node can say about the station it just finished: the sector as the wire indexes it, and the pose in radians.
struct BuildDescribe {
  std::uint16_t sector = 0;             // wire sector index; 0 = this node cannot name the sector yet
  float position[3] = {0.0f, 0.0f, 0.0f};
  float rotation[3] = {0.0f, 0.0f, 0.0f};  // yaw, pitch, roll, radians
};

struct BuildReportDeps {
  // false: the component is gone or the sector index is not known yet - the request waits.
  std::function<bool(std::uint64_t component, BuildDescribe& out)> describe;
  std::function<std::uint16_t()> self_player_id;
  std::function<bool(X4MP::Proto::MsgType type, std::span<const std::uint8_t> payload)> send_control;
  std::function<std::uint64_t()> now_ms;  // 0 when the clock is unknown (the pending list then never expires)
};

struct BuildReportStats {
  std::uint64_t sent = 0;
  std::uint64_t deduped = 0;
  std::uint64_t unresolved = 0;  // the describe failed: counted once per event, retried from the pending list
  std::uint64_t bad = 0;         // the ring line carried no component or no macro
  std::uint64_t failed = 0;      // the send was refused
  std::uint64_t dropped = 0;     // the pending list expired without ever resolving
};

// One request in flight: what the IntentResult has to bind when it arrives.
struct OutstandingBuild {
  std::uint32_t request_id = 0;
  std::uint64_t component = 0;
  std::uint16_t sector = 0;
  std::string macro;
};

class BuildReporter {
 public:
  static constexpr std::uint64_t kRetryMs = 10'000;  // the AC gives a build 10 s to name its sector
  static constexpr std::size_t kMaxPending = 16;
  static constexpr std::size_t kMaxSeen = 512;

  explicit BuildReporter(BuildReportDeps deps) : deps_(std::move(deps)) {}

  // true only for EventKind::Build: the caller can keep one dispatch chain for every event kind.
  bool on_event(const events::MdEvent& event);
  // Re-describes every pending build; a build that stays unresolved past kRetryMs is dropped and counted.
  void retry(std::uint64_t now_ms);

  // The IntentResult for this request, once: false when the id is not ours.
  bool take_outstanding(std::uint32_t request_id, OutstandingBuild& out);

  [[nodiscard]] const BuildReportStats& stats() const noexcept { return stats_; }
  [[nodiscard]] std::size_t pending() const noexcept { return pending_.size(); }
  void reset();

 private:
  struct Pending {
    std::uint64_t component = 0;
    std::string macro;
    std::uint64_t at_ms = 0;
  };

  bool report(std::uint64_t component, const std::string& macro, std::uint64_t now_ms);

  BuildReportDeps deps_;
  BuildReportStats stats_;
  std::unordered_set<std::uint64_t> seen_;
  std::vector<Pending> pending_;
  std::vector<OutstandingBuild> outstanding_;
  std::uint64_t next_key_ = 0;
};

}  // namespace x4mp::features::builds
