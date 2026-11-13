#pragma once
// features/damage: the one hand-over point for hull/shield damage the mod applies through MD (M15; docs/protocol.md 12,
// docs/mod-design.md 5.4, ADR-037).
//
// Neither the DLL nor Lua can write hull or shield directly; MD can (<set_object_hull> / <set_object_shield>), so damage is a
// queue of lines that the damage feature flushes once a frame as ONE AddUITriggeredEvent (the same shape as economy's cargo
// and money queues). Both nodes use it:
//   authority  a HitReport intent from a client becomes an op on the target's real ship
//   client     the same report is applied locally at once, so the shooter sees the hit without waiting for the round trip
//
// Amounts are percentages of the maximum (the unit of `set_object_hull exact=` and of `$Obj.hullpercentage`), not absolute
// hull points: MD applies them relative to the value it reads at that moment, so two nodes that disagree slightly about the
// current hull still land the same damage.

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <vector>

namespace x4mp::features::damage {

// One line of the actions shim: `component` loses `hull` percent of hull and `shield` percent of shield.
struct DamageOp {
  std::uint64_t component = 0;
  double hull = 0.0;
  double shield = 0.0;
};

struct DamageStats {
  std::uint64_t ops_queued = 0, ops_dropped = 0, ops_applied = 0, ops_skipped = 0;
  std::uint64_t frames = 0, raises_failed = 0, hits_rejected = 0;
};

class DamageHub {
 public:
  static constexpr std::size_t kMaxOps = 256;  // a burst beyond this is dropped, never grown

  // Adds one op. A line with no component, no damage at all or a non-finite amount is dropped and counted, so a bad
  // HitReport can never reach MD.
  void queue(const DamageOp& op);

  // The ops queued so far, oldest first, at most `max` of them. The rest stay queued for the next frame.
  [[nodiscard]] std::vector<DamageOp> take_ops(std::size_t max);

  [[nodiscard]] std::size_t queued() const;
  [[nodiscard]] const DamageStats& stats() const noexcept { return stats_; }
  [[nodiscard]] DamageStats& stats() noexcept { return stats_; }
  void reset();

 private:
  mutable std::mutex mutex_;
  std::vector<DamageOp> ops_;
  DamageStats stats_{};
};

[[nodiscard]] DamageHub& damage_hub();

// True when the op is one MD can apply: a component, and at least one finite, non-negative amount.
[[nodiscard]] bool valid(const DamageOp& op) noexcept;

// Clamps one reported hit into what MD can apply: NaN and negative amounts become 0, and no single report moves a value by
// more than 100 percent (a client cannot take a ship from full to dead in one line; a kill is a KillClaim).
[[nodiscard]] DamageOp clamp_hit(std::uint64_t component, double hull, double shield) noexcept;

}  // namespace x4mp::features::damage
