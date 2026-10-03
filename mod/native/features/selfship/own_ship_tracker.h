#pragma once
// features/selfship/own_ship_tracker: the state machine behind PlayerState (M3-09; docs/m3-plan.md 4.4, 4.5, 6.3, spikes S13.4 / S13.5).
// Pure C++ (no SDK, no game calls, no allocation in update()): the adapter (game/selfship_api.*) fills an Observation once per frame,
// the tracker says what happened (seat edge) and whether a PlayerState is due now.
//
// Seat (S13.5): GetPlayerOccupiedShipID goes 0 -> ship at sit-down and ship -> 0 at stand-up. While the player stands the ship is still
// known (GetPlayerContainerID = the ship), but nothing is streamed: the stand-up edge sends ONE Hidden state (so the others hide the
// ghost at once) and then the stream stays silent until the next sit-down (OnFootState is M3b).
//
// Rates (m3-plan 4.5): 20 Hz moving, 5 Hz idle (< 1 m/s and < 0.05 rad/s over a 250 ms window), 1 Hz while Hidden (highway, docked);
// IMMEDIATE on a sector change, a teleport, a ship change, the first state after sit-down and a change of the Hidden / Docked /
// InHighway flags. The periodic schedule catches up (next = previous next + interval) so the average rate is exact whatever the frame
// rate, and it never bursts after a long frame gap (a gate jump is a ~3.5 s gap, S13.4).
//
// Flags: PlayerControlled always (the pilot is in the seat), Docked, InHighway (local and super highways cannot be told apart natively:
// GetContextByClass(.., "highway") answers for both), Hidden = Docked or InHighway (docked inside vs on a pad cannot be told apart
// either), Teleport on a sector change / ship change / sit-down / position jump (a jump bigger than 2 km + 15 km/s * frame time).
// Angles are the game's radians exactly as UIPosRot gives them (S13.4); positions are sector-local metres.

#include <cstdint>

#include "core/ghost/math.h"
#include "core/ghost/sample.h"
#include "features/selfship/galaxy_map.h"

namespace x4mp::features::selfship {

struct Observation {
  std::int64_t now_us = 0;           // local monotonic microseconds (steady clock)
  std::uint64_t occupied = 0;        // GetPlayerOccupiedShipID: the ship whose pilot seat the player sits in (0 = standing / on foot)
  std::uint64_t standing_ship = 0;   // the ship the player stands in (container) when not seated, else 0
  std::uint64_t sector = 0;          // UniverseID of the sector of the ship (0 = unknown)
  bool in_highway = false;
  bool docked = false;
  bool pose_valid = false;           // pos / rot are readable this frame
  ghost::Vec3 pos{};                 // metres, sector-local
  ghost::Euler rot{};                // radians as the game gives them
};

enum class SeatEdge : std::uint8_t { None, SatDown, StoodUp, ShipChanged };

// A PlayerState to send now (the caller adds seq, sample time and the net id).
struct StateOut {
  bool send = false;
  std::uint16_t sector = 0;
  std::uint16_t flags = 0;
  ghost::Vec3 pos{};
  ghost::Euler rot{};
  bool immediate = false;  // sent outside the periodic schedule (sector change, teleport, flag change, first state, stand-up)
};

// Why nothing was sent although the player sits (for the log and the status topic; the tracker never logs by itself).
enum class Blocked : std::uint8_t { None, NoPose, MapNotReady, UnknownSector };

struct Tick {
  SeatEdge edge = SeatEdge::None;
  bool seated = false;
  std::uint64_t ship = 0;  // own ship: the occupied one, else the one the player stands in, else 0
  StateOut out;
  Blocked blocked = Blocked::None;
};

struct TrackerConfig {
  std::int64_t moving_interval_us = 50'000;   // 20 Hz
  std::int64_t idle_interval_us = 200'000;    // 5 Hz
  std::int64_t hidden_interval_us = 1'000'000;  // 1 Hz
  std::int64_t idle_window_us = 250'000;
  double idle_speed_mps = 1.0;
  double idle_turn_rps = 0.05;
  double jump_base_m = 2000.0;
  double jump_speed_mps = 15000.0;
  std::int64_t schedule_slack_us = 1'000;      // a frame up to 1 ms early still counts as due
};

struct TrackerCounters {
  std::uint64_t sent = 0;
  std::uint64_t immediate = 0;
  std::uint64_t teleports = 0;
  std::uint64_t seat_edges = 0;  // SatDown + StoodUp + ShipChanged
  std::uint64_t blocked_frames = 0;
};

class OwnShipTracker {
 public:
  explicit OwnShipTracker(const TrackerConfig& cfg = {}) : cfg_(cfg) {}

  Tick update(const Observation& obs, const GalaxyMap& map) noexcept;
  void reset() noexcept;  // back to "nobody sits" (a new session / universe)
  // The link to the server came up (or came back): the next frame sends a full state with Teleport, whatever was sent before.
  void force_resend() noexcept {
    sent_any_ = false;
    first_after_seat_ = true;
    pending_teleport_ = true;
  }

  [[nodiscard]] bool seated() const noexcept { return seated_; }
  [[nodiscard]] std::uint64_t ship() const noexcept { return ship_; }
  [[nodiscard]] bool moving() const noexcept { return moving_; }
  [[nodiscard]] std::uint16_t last_flags() const noexcept { return sent_flags_; }  // the flags of the last state sent (without Teleport)
  [[nodiscard]] const TrackerCounters& counters() const noexcept { return counters_; }
  [[nodiscard]] const TrackerConfig& config() const noexcept { return cfg_; }

 private:
  [[nodiscard]] std::int64_t interval_for(std::uint16_t flags) const noexcept;

  TrackerConfig cfg_;
  TrackerCounters counters_;
  bool seated_ = false;
  std::uint64_t ship_ = 0;
  // last frame (jump detection)
  bool have_prev_ = false;
  std::int64_t prev_t_us_ = 0;
  std::uint64_t prev_sector_ = 0;
  ghost::Vec3 prev_pos_{};
  // idle window anchor
  bool anchor_valid_ = false;
  std::int64_t anchor_t_us_ = 0;
  ghost::Vec3 anchor_pos_{};
  ghost::Euler anchor_rot_{};
  bool moving_ = true;
  // what was last sent
  bool sent_any_ = false;
  std::uint16_t sent_sector_ = 0;
  std::uint16_t sent_flags_ = 0;  // without Teleport
  std::int64_t sent_t_us_ = 0;
  std::int64_t next_due_us_ = 0;
  bool pending_teleport_ = false;
  bool first_after_seat_ = false;
  ghost::Vec3 last_pos_{};  // last known pose (for the stand-up state)
  ghost::Euler last_rot_{};
  std::uint16_t last_sector_idx_ = 0;
};

}  // namespace x4mp::features::selfship
