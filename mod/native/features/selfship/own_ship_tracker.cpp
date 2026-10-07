#include "features/selfship/own_ship_tracker.h"

#include <algorithm>
#include <cmath>

namespace x4mp::features::selfship {

namespace {
constexpr double kPi = 3.14159265358979323846;

double wrap_angle(double a) noexcept {
  while (a > kPi) a -= 2 * kPi;
  while (a < -kPi) a += 2 * kPi;
  return a;
}

constexpr std::uint16_t kFlagMask = ghost::kDocked | ghost::kInHighway | ghost::kHidden;  // the flags whose change is sent at once
}  // namespace

void OwnShipTracker::reset() noexcept {
  const auto cfg = cfg_;
  *this = OwnShipTracker(cfg);
}

std::int64_t OwnShipTracker::interval_for(std::uint16_t flags) const noexcept {
  if ((flags & ghost::kHidden) != 0) return cfg_.hidden_interval_us;
  return moving_ ? cfg_.moving_interval_us : cfg_.idle_interval_us;
}

Tick OwnShipTracker::update(const Observation& obs, const GalaxyMap& map) noexcept {
  Tick tick;
  const bool seated_now = obs.occupied != 0;
  tick.seated = seated_now;
  tick.ship = seated_now ? obs.occupied : obs.standing_ship;

  // ---- seat edge ----
  if (seated_now && !seated_) {
    tick.edge = SeatEdge::SatDown;
  } else if (!seated_now && seated_) {
    tick.edge = SeatEdge::StoodUp;
  } else if (seated_now && seated_ && obs.occupied != ship_) {
    tick.edge = SeatEdge::ShipChanged;
  }
  if (tick.edge != SeatEdge::None) ++counters_.seat_edges;

  if (tick.edge == SeatEdge::StoodUp) {
    // One last, Hidden state so the others hide the ghost now; then silence (no OnFootState before M3b).
    seated_ = false;
    ship_ = tick.ship;
    have_prev_ = false;
    anchor_valid_ = false;
    if (sent_any_ && last_sector_idx_ != 0) {
      tick.out.send = true;
      tick.out.immediate = true;
      tick.out.sector = last_sector_idx_;
      tick.out.flags = static_cast<std::uint16_t>(ghost::kHidden | (sent_flags_ & ghost::kDocked));
      tick.out.pos = last_pos_;
      tick.out.rot = last_rot_;
      sent_flags_ = tick.out.flags;
      sent_t_us_ = obs.now_us;
      ++counters_.sent;
      ++counters_.immediate;
    }
    sent_any_ = false;
    return tick;
  }
  if (!seated_now) {
    seated_ = false;
    ship_ = tick.ship;
    return tick;
  }

  // ---- seated ----
  if (tick.edge == SeatEdge::SatDown || tick.edge == SeatEdge::ShipChanged) {
    pending_teleport_ = true;
    first_after_seat_ = true;
    have_prev_ = false;
    anchor_valid_ = false;
    moving_ = true;
    sh_signal_ = false;
    sent_any_ = false;
  }
  seated_ = true;
  ship_ = obs.occupied;

  // ---- M3-33: the game's own superhighway signal (never a speed guess) ----
  if (obs.sh_entered) {
    sh_signal_ = true;
    sh_saw_unknown_ = false;
    sh_since_us_ = obs.now_us;
    sh_sector_idx_ = last_sector_idx_;
  }
  if (obs.sh_exited) sh_signal_ = false;

  if (!obs.pose_valid) {
    tick.blocked = Blocked::NoPose;
    ++counters_.blocked_frames;
    return tick;
  }
  if (!map.ready()) {
    tick.blocked = Blocked::MapNotReady;
    ++counters_.blocked_frames;
    return tick;
  }
  const std::uint16_t sector_idx = map.index_of(obs.sector);
  if (sector_idx == 0) {
    tick.blocked = Blocked::UnknownSector;
    ++counters_.blocked_frames;
    if (sh_signal_) sh_saw_unknown_ = true;
    // M3-31 (Finding 18): inside a superhighway the ship's sector is not in the sector map, so nothing is sent for a long time. Say it ONCE, at the
    // last known place: the others hide/freeze the ghost or avatar now (no extrapolation, no stale velocity, no hold that later jumps back) and the
    // first state after the gap is a teleport (the position is unknown for the whole gap).
    if (sent_any_ && last_sector_idx_ != 0 && (sent_flags_ & ghost::kHidden) == 0) {
      std::uint16_t flags = static_cast<std::uint16_t>(ghost::kPlayerControlled | ghost::kHidden | (sent_flags_ & ghost::kDocked));
      if (obs.in_highway || sh_signal_) flags |= ghost::kInHighway;
      tick.out.send = true;
      tick.out.immediate = true;
      tick.out.sector = last_sector_idx_;
      tick.out.flags = flags;
      tick.out.pos = last_pos_;
      tick.out.rot = last_rot_;
      sent_flags_ = flags;
      sent_sector_ = last_sector_idx_;
      sent_t_us_ = obs.now_us;
      next_due_us_ = obs.now_us + cfg_.hidden_interval_us;
      ++counters_.sent;
      ++counters_.immediate;
    }
    if (sent_any_) pending_teleport_ = true;
    return tick;
  }

  if (sh_signal_) {
    if (sh_sector_idx_ == 0) sh_sector_idx_ = sector_idx;  // the signal came before any sector was known: this one is the entry sector
    if (sh_saw_unknown_ || sector_idx != sh_sector_idx_ || obs.now_us - sh_since_us_ > cfg_.sh_max_us) sh_signal_ = false;
  }

  // ---- teleport detection (frame to frame) ----
  if (have_prev_) {
    if (sector_idx != last_sector_idx_ && prev_sector_ != obs.sector) {
      pending_teleport_ = true;
    } else {
      const double dt_s = static_cast<double>(obs.now_us - prev_t_us_) / 1e6;
      const double jump = ghost::distance(obs.pos, prev_pos_);
      if (jump > cfg_.jump_base_m + cfg_.jump_speed_mps * (dt_s > 0 ? dt_s : 0)) {
        pending_teleport_ = true;
      }
    }
  }
  const bool sector_changed = sent_any_ && sector_idx != sent_sector_;
  have_prev_ = true;
  prev_t_us_ = obs.now_us;
  prev_sector_ = obs.sector;
  prev_pos_ = obs.pos;
  last_pos_ = obs.pos;
  last_rot_ = obs.rot;
  last_sector_idx_ = sector_idx;

  // ---- idle / moving (window anchor) ----
  if (pending_teleport_ || !anchor_valid_) {
    anchor_valid_ = true;
    anchor_t_us_ = obs.now_us;
    anchor_pos_ = obs.pos;
    anchor_rot_ = obs.rot;
    moving_ = true;  // unknown yet: treat as moving until a window says otherwise
  } else if (obs.now_us - anchor_t_us_ >= cfg_.idle_window_us) {
    const double dt_s = static_cast<double>(obs.now_us - anchor_t_us_) / 1e6;
    const double speed = ghost::distance(obs.pos, anchor_pos_) / dt_s;
    const double turn = std::max({std::fabs(wrap_angle(obs.rot.yaw - anchor_rot_.yaw)), std::fabs(wrap_angle(obs.rot.pitch - anchor_rot_.pitch)),
                                  std::fabs(wrap_angle(obs.rot.roll - anchor_rot_.roll))}) / dt_s;
    moving_ = speed >= cfg_.idle_speed_mps || turn >= cfg_.idle_turn_rps;
    anchor_t_us_ = obs.now_us;
    anchor_pos_ = obs.pos;
    anchor_rot_ = obs.rot;
  }

  // ---- flags ----
  std::uint16_t flags = ghost::kPlayerControlled;
  if (obs.docked) flags |= ghost::kDocked;
  if (obs.in_highway) flags |= ghost::kInHighway;
  if (sh_signal_) flags |= ghost::kInHighway;
  if (obs.docked || obs.in_highway || sh_signal_) flags |= ghost::kHidden;

  // ---- decide ----
  const bool flags_changed = sent_any_ && ((flags ^ sent_flags_) & kFlagMask) != 0;
  const std::int64_t interval = interval_for(flags);
  bool due = false;
  bool immediate = false;
  if (!sent_any_ || first_after_seat_ || pending_teleport_ || sector_changed || flags_changed) {
    due = true;
    immediate = true;
  } else {
    // a shorter interval than the schedule assumed (idle -> moving): do not wait out the old one
    next_due_us_ = std::min(next_due_us_, sent_t_us_ + interval);
    due = obs.now_us + cfg_.schedule_slack_us >= next_due_us_;
  }
  if (!due) return tick;

  if (pending_teleport_) {
    flags |= ghost::kTeleport;
    ++counters_.teleports;
  }
  tick.out.send = true;
  tick.out.immediate = immediate;
  tick.out.sector = sector_idx;
  tick.out.flags = flags;
  tick.out.pos = obs.pos;
  tick.out.rot = obs.rot;

  if (immediate) {
    next_due_us_ = obs.now_us + interval;
  } else {
    next_due_us_ += interval;
    if (obs.now_us - next_due_us_ > interval) next_due_us_ = obs.now_us + interval;  // never burst after a long gap
  }
  sent_any_ = true;
  sent_sector_ = sector_idx;
  sent_flags_ = static_cast<std::uint16_t>(flags & ~ghost::kTeleport);
  sent_t_us_ = obs.now_us;
  pending_teleport_ = false;
  first_after_seat_ = false;
  ++counters_.sent;
  if (immediate) ++counters_.immediate;
  return tick;
}

}  // namespace x4mp::features::selfship
