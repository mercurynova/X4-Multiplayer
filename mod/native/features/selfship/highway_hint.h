#pragma once
// features/selfship/highway_hint: the client half of the region lookahead (M4-22; docs/m4-plan.md W5).
//
// The authority already prefetches the sectors one hop from the client's own (PrefetchDepth, InterestManager.Recompute), so on a normal
// flight the next sector is warm before the client gets there. A gate or a superhighway is different: it moves the client to a sector the
// graph walk did not name, and the arrival would spawn everything in front of the player ("a pop").
//
// The client knows the destination only from the game: md/x4mp_galaxy.xml raises the superhighway signal with the entry gate's destination
// sector macro attached ('gate|<macro>'), Lua forwards both fields, and this feature turns the macro into the wire sector index and sends
// InterestHint{target_sector, eta_ms, GateApproach}. The server (InterestManager.HandleHint) then captures and streams that sector before
// the client arrives. Nothing here talks to the wire: the feature encodes and sends; this file is the parsing and the rate limit, so the
// decisions are testable without a game, a map or a socket.
//
//   "gate|cluster_01_sector001_macro" -> {source: "gate", target: "cluster_01_sector001_macro"}
//   "zone" / "sector" / "exit"        -> {source: "zone", target: ""}          (no destination to prefetch)
//
// Pure logic: no SDK, no game calls, no allocation beyond the two strings.

#include <cstdint>
#include <string>
#include <string_view>

namespace x4mp::features::selfship {

// One MD superhighway signal as it reaches native. `source` is what the log calls the signal ('gate', 'zone', 'sector', 'exit'); `target`
// is the destination sector macro, empty when the signal does not name one.
struct HighwaySignal {
  std::string source;
  std::string target;

  [[nodiscard]] bool names_destination() const noexcept { return !target.empty(); }
};

// "source" or "source|target". An empty source means the text was not one of ours: the caller skips it.
[[nodiscard]] HighwaySignal parse_highway_signal(std::string_view raw);

// Sends at most one hint per destination per interval. A different destination is accepted at once: a second gate in a row is a new
// prefetch, not a repeat. The same destination is repeated only after `interval_us` (a re-entry, a re-approach).
class HighwayHintGate {
 public:
  explicit HighwayHintGate(std::int64_t interval_us = 15'000'000) noexcept : interval_us_(interval_us) {}

  // true = send the hint for this target now. Records the target and the time either way.
  [[nodiscard]] bool accept(std::string_view target, std::int64_t now_us);

  void reset() noexcept;

  [[nodiscard]] std::size_t sent() const noexcept { return sent_; }

 private:
  std::string last_target_;
  std::int64_t last_us_ = 0;
  std::int64_t interval_us_ = 15'000'000;
  std::size_t sent_ = 0;
};

}  // namespace x4mp::features::selfship
