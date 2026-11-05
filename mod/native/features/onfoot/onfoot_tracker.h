#pragma once
// features/onfoot: how often the local on-foot sample goes on the wire. Same shape as selfship/own_ship_tracker.h, different rates.
//
//   docs/research/on-foot-presence.md 3.1: 20 Hz while moving (more than 5 cm or 5 deg since the last sample), 1 Hz keepalive while idle,
//   immediately on a room / container / mode change (with the Teleport flag so the remote side snaps instead of interpolating).
//   On foot the sample is ~40 B, so the idle keepalive is cheap; the 20 Hz PlayerState stream is suspended by MD/native while on foot.

#include <cstdint>

#include "features/onfoot/onfoot_types.h"

namespace x4mp::features::onfoot {

struct TrackerConfig {
  std::int64_t moving_interval_us = 50'000;    // 20 Hz: a walking avatar is watched up close, so the samples must be dense
  std::int64_t idle_interval_us = 1'000'000;   // 1 Hz keepalive
  std::int64_t hidden_interval_us = 5'000'000;  // mode None (left the interior): one leave sample, then a rare repeat
  float moving_metres = 0.05f;
  float moving_yaw_radians = 0.0873f;  // 5 deg
  float still_metres = 0.001f;         // per-call change below this is "the player did not move since the previous call"
};

class OnFootTracker {
 public:
  struct Sample {
    RoomContext ctx;
    float cx = 0;  // container-local metres
    float cy = 0;
    float cz = 0;
    float yaw = 0;  // radians
    float pitch = 0;
  };

  struct Step {
    bool send = false;
    bool keyframe = false;  // the remote side must accept this sample without interpolation
    bool teleport = false;  // a room change: snap, do not glide
    Anim anim = Anim::Idle;
  };

  void reset() { *this = OnFootTracker{}; }
  void force_resend() noexcept { force_ = true; }

  [[nodiscard]] Step update(const Sample& s, std::int64_t now_us) noexcept;

 private:
  TrackerConfig cfg_{};
  RoomContext last_ctx_{};
  float last_x_ = 0;  // pose at the last SEND (the rate and the speed are measured from there)
  float last_y_ = 0;
  float last_z_ = 0;
  float last_yaw_ = 0;
  float speed_mps_ = 0;
  std::int64_t last_send_us_ = 0;
  bool have_last_ = false;
  bool have_send_ = false;
  bool force_ = true;
  // Latches "the previous call moved", so the first still call after a walk is sent at once instead of waiting up to
  // idle_interval_us (1 s) while the receiver extrapolates past the stop and then springs back. `moving` above is measured from the
  // last SEND and stays true right after one, so the stop has to be read from two consecutive calls instead.
  bool was_moving_ = false;
  float step_x_ = 0;  // pose at the previous CALL (not the previous send)
  float step_y_ = 0;
  float step_z_ = 0;
  bool have_step_ = false;
};

}  // namespace x4mp::features::onfoot
