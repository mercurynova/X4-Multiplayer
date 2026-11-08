#pragma once
// core/ghost: HintGate, the ONE rule that decides whether an inert object (a ghost, an avatar) may get a non-zero velocity hint (F32; M3-34 / F23c).
//
// The hint is for the target info's speed, but the ENGINE flies the inert object with it between our per-frame placements and may capture it into a
// superhighway. Two copies of "speed <= cap at the hint tick" used to guard that (ghost driver, avatar director); both looked at one number at the 5 Hz
// tick, so a ship that is really at travel speed let a hint through whenever the number they held was not (yet) a real measurement: the first frames after
// a sector change / a (re)spawn / a snap / a hold, a spike between two ticks. This gate keeps the evidence instead:
//   - discontinuity()  a placement jump (sector change, snap, spawn, show again, hold, engine guard hit): everything measured so far means nothing;
//   - speed()          EVERY frame's measured rendered speed (not only the one at the hint tick); a sample above the cap restarts the calm period;
//   - allow()          the WHOLE rule: true only if the speed being hinted is at or below the cap AND `settle_s` seconds of uninterrupted valid samples at or
//                      below the cap have passed since the last jump, i.e. real data after the jump. Callers do not test the cap themselves.
// A hint that is not allowed is a ZERO hint (the caller sends it exactly like a hold). Pure arithmetic, no game calls: Catch2 tests it directly.

namespace x4mp::ghost {

struct HintGateConfig {
  double max_mps = 1000.0;  // above this the engine would fly the object by the hint (and may capture it into a highway)
  double settle_s = 0.6;    // uninterrupted valid samples at or below max_mps needed after a discontinuity (longer than the 0.2 s braking window)
};

class HintGate {
 public:
  explicit HintGate(HintGateConfig cfg = {}) : cfg_(cfg) {}

  void discontinuity() noexcept { calm_since_s_ = -1.0; }

  // A valid rendered speed at time t_s (seconds, any monotonic clock), one per frame.
  void speed(double t_s, double mps) noexcept {
    if (mps > cfg_.max_mps) calm_since_s_ = -1.0;
    else if (calm_since_s_ < 0.0) calm_since_s_ = t_s;
  }

  [[nodiscard]] bool allow(double t_s, double speed_mps) const noexcept {
    return speed_mps <= cfg_.max_mps && calm_since_s_ >= 0.0 && t_s - calm_since_s_ >= cfg_.settle_s;
  }

  // Seconds of calm so far (0 when not calm), for the diagnostics line.
  [[nodiscard]] double calm_s(double t_s) const noexcept { return calm_since_s_ < 0.0 ? 0.0 : t_s - calm_since_s_; }

  [[nodiscard]] const HintGateConfig& config() const noexcept { return cfg_; }

 private:
  HintGateConfig cfg_;
  double calm_since_s_ = -1.0;  // start of the current calm period; < 0 = none
};

}  // namespace x4mp::ghost
