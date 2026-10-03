#pragma once
// features/selfship/seta_guard: SETA is unavailable in multiplayer, always (user decision 2026-10-03; docs/m3-plan.md Q13, 6.3 M3-09).
//
// Two layers:
//   1. At the source (MD, md/x4mp_galaxy.xml cue X4MP_Seta_Block): while the node is connected, native tells Lua/MD to block SETA
//      (verb x4mp.seta_block -> AddUITriggeredEvent("X4MP_Authority", "seta_block", "1")). Vanilla starts SETA with the
//      startactivity signal for activity.seta (md/modes.xml Mode_SETA); our cue listens to that same signal and stops the activity again
//      at once (signal 'stopactivity', then toggle_timewarp / set_timewarp_factor), and reports it (x4mp.md_seta -> verb x4mp.seta_blocked).
//   2. The safety net (this class): native watches IsSetaActive every frame; the moment it is true while connected the guard asks for a
//      switch-off (raise x4mp.seta_off -> MD) and repeats the request every 500 ms until it is off, with the notification
//      "SETA is disabled in multiplayer" (at most one per 5 s). The measured switch-off time is kept for the log.
// Pure C++ (no game calls): the feature feeds it the clock, the connection state and IsSetaActive.

#include <cstdint>

namespace x4mp::features::selfship {

struct SetaAction {
  bool request_off = false;  // ask MD to switch SETA off now
  bool notify = false;       // show "SETA is disabled in multiplayer"
  bool switched_off = false; // SETA was active and is off now (the episode ended)
  std::int64_t off_after_us = 0;  // how long the episode lasted (set with switched_off)
};

class SetaGuard {
 public:
  static constexpr std::int64_t kRepeatUs = 500'000;
  static constexpr std::int64_t kNotifyEveryUs = 5'000'000;

  SetaAction update(std::int64_t now_us, bool connected, bool seta_active) noexcept {
    SetaAction a;
    if (!connected) {
      active_ = false;
      return a;
    }
    if (seta_active) {
      if (!active_) {
        active_ = true;
        since_us_ = now_us;
        ++detections_;
        a.request_off = true;
        last_request_us_ = now_us;
        a.notify = notify_due(now_us);
      } else if (now_us - last_request_us_ >= kRepeatUs) {
        a.request_off = true;
        last_request_us_ = now_us;
      }
      if (a.request_off) ++requests_;
    } else if (active_) {
      active_ = false;
      a.switched_off = true;
      a.off_after_us = now_us - since_us_;
      last_off_us_ = a.off_after_us;
    }
    return a;
  }

  // MD reported that it stopped SETA at the source (x4mp.seta_blocked): true when the notification is due.
  bool blocked_at_source(std::int64_t now_us) noexcept {
    ++source_blocks_;
    return notify_due(now_us);
  }

  [[nodiscard]] bool active() const noexcept { return active_; }
  [[nodiscard]] std::uint64_t detections() const noexcept { return detections_; }
  [[nodiscard]] std::uint64_t requests() const noexcept { return requests_; }
  [[nodiscard]] std::uint64_t source_blocks() const noexcept { return source_blocks_; }
  [[nodiscard]] std::int64_t last_off_us() const noexcept { return last_off_us_; }

 private:
  bool notify_due(std::int64_t now_us) noexcept {
    if (notified_ && now_us - last_notify_us_ < kNotifyEveryUs) return false;
    notified_ = true;
    last_notify_us_ = now_us;
    return true;
  }
  bool active_ = false;
  bool notified_ = false;
  std::int64_t since_us_ = 0, last_request_us_ = 0, last_notify_us_ = 0, last_off_us_ = 0;
  std::uint64_t detections_ = 0, requests_ = 0, source_blocks_ = 0;
};

}  // namespace x4mp::features::selfship
