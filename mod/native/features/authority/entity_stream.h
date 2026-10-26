#pragma once
// features/authority: the world streamer (M4-06..M4-11; docs/m4-plan.md W2, docs/mod-design.md §9 "M4" sub-items 2-3).
//
// The authority polls the entities of the CaptureSet, dead-reckons each one against what the client last received, and
// emits the four authority -> server messages of the world:
//
//   EntitySpawn       once per entity of a newly captured sector, chunked, then SectorComplete (M4-09)
//   WorldUpdate       the entities whose state moved beyond the thresholds, at the CaptureSet rate (M4-07/08)
//   EntityDespawn     a sector that left interest after the 60 s grace, or a row the index pass no longer sees (M4-10)
//   EntityChange      an owner/macro change on a live entity (M4-11)
//   EntityStatusBatch hull/shield, <= 1 Hz per entity with a 10 s keepalive (M4-11)
//
// Ownership: this class owns the per-entity stream state (the last pose SENT, the derived velocity, the focus flag) but
// not the entity list: the UniverseIndex owns that, and the CaptureSet owns the interest. `step()` is handed both and
// reads poses through the PoseReader callback (the game's GetObjectPositionInSector), so nothing here needs the SDK.
//
// Pure C++ apart from the callbacks. Not thread-safe (one instance per authority flow, frame thread only).

#include <cstddef>
#include <cstdint>
#include <functional>
#include <span>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "features/authority/capture_scheduler.h"
#include "features/authority/capture_set.h"
#include "features/authority/dead_reckoning.h"
#include "features/authority/entity_wire.h"
#include "features/authority/universe_index.h"

namespace x4mp::features::auth {

// Mirrors P::DespawnReason (common.fbs) so the header needs no generated include.
enum class DespawnCause : std::uint8_t {
  Unknown = 0,
  OutOfInterest = 1,
  Destroyed = 2,
  Removed = 3,
  DockedInside = 4,
  PlayerLeft = 5,
};

struct StreamConfig {
  DeadReckoningConfig dr;
  // M4-10: a sector that leaves the CaptureSet keeps its entities on the wire for this long. The client has its own
  // leave grace (M4-14); this one only decides when the authority stops refreshing them.
  double out_of_interest_grace_s = 60.0;
  // M4-11: a hull/shield change is batched at most once per entity per second, with a 10 s keepalive.
  double status_min_interval_s = 1.0;
  double status_keepalive_s = 10.0;
  // The distance used for the dead-reckoning tiers when the authority's own ship (the observer) is unknown.
  double observer_unknown_m = 50000.0;
};

struct StreamStats {
  std::uint32_t spawns = 0;          // EntitySpawn records
  std::uint32_t spawn_messages = 0;  // EntitySpawn frames
  std::uint32_t sectors_complete = 0;
  std::uint32_t updates = 0;         // WorldUpdate entries
  std::uint32_t update_messages = 0; // WorldUpdate frames (keepalives included)
  std::uint32_t polls = 0;
  std::uint32_t poll_failed = 0;
  std::uint32_t despawns = 0;
  std::uint32_t changes = 0;
  std::uint32_t statuses = 0;
  std::uint32_t skipped_no_macro = 0;  // a row whose macro string was not in the table yet
  std::uint32_t encode_failed = 0;
  std::uint32_t send_failed = 0;
  std::uint32_t reason_threshold = 0, reason_rotation = 0, reason_flags = 0, reason_sector = 0, reason_keepalive = 0;
};

// The stream state of one entity.
struct EntityTrack {
  std::uint32_t net_id = 0;
  std::uint16_t sector = 0;
  std::uint64_t component = 0;

  TrackPose sent{};             // the pose the client holds (dequantised, so the prediction matches exactly)
  double sent_at_s = 0.0;
  std::uint16_t sent_flags = 0;
  std::uint16_t sent_sector = 0;
  std::uint32_t sent_owner_ref = 0;
  std::uint32_t sent_macro_ref = 0;

  TrackPose read{};             // the last pose read from the game
  double read_at_s = 0.0;
  TrackVelocity velocity{};
  bool have_read = false;
  bool have_sent = false;       // the EntitySpawn went out
  bool in_focus = false;        // polled every tick in addition to the sector slice

  std::uint8_t hull = 255, shield = 255;  // 255 = unknown
  std::uint8_t sent_hull = 255, sent_shield = 255;
  double status_sent_at_s = 0.0;
  bool status_dirty = false;

  std::uint64_t visited_tick = 0;  // guards against polling an entity twice in one tick
  double despawn_at_s = -1.0;      // >= 0: the sector left interest, despawn at this time unless it comes back
};

class EntityStream {
 public:
  // component id -> sector-local pose. False = the object cannot be read right now (warping, removed, export missing).
  using PoseReader = std::function<bool(std::uint64_t component, TrackPose& out)>;
  using Sender = std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)>;
  struct Sink {
    Sender control;   // EntitySpawn, EntityDespawn, EntityChange, SectorComplete
    Sender realtime;  // WorldUpdate, EntityStatusBatch
  };

  EntityStream(PoseReader reader, Sink sink, StreamConfig cfg = {});

  // The CaptureSet changed (M4-01). Sectors that left start the OutOfInterest grace; a sector that comes back cancels
  // it. Every entity is re-classified against the focus spheres on the next step (one poll each), so a new sphere is
  // honoured at once instead of waiting for the sector slice to come round.
  void on_capture_set(const CaptureSet& set);

  // A scan pass closed for `sector` (M4-09). The next step spawns every index row that is not on the wire yet, then
  // sends SectorComplete. Idempotent: a rescan only spawns the rows that are new.
  void on_scan_complete(std::uint16_t sector);

  // The index dropped a row (M4-10): the scan no longer sees the ship. `destroyed` picks Destroyed over Removed.
  void on_row_removed(std::uint32_t net_id, bool destroyed);

  // An attribute changed on an entity (M4-11). Only the owner and the macro exist in the index today; the other bits
  // wait for the manifest (W4). Ignored for an entity that is not on the wire yet (its spawn carries the new value).
  void on_change(std::uint32_t net_id, std::uint32_t owner_ref, std::uint32_t macro_ref);

  // A hull/shield sample (M4-11). 255 means unknown; a sample is only ever sent if it differs or the keepalive is due.
  void note_status(std::uint32_t net_id, std::uint8_t hull, std::uint8_t shield);

  // One authority tick (20 Hz). Always emits exactly one WorldUpdate (possibly empty: the server needs its clock
  // reference, M3-14) plus whatever else is due. `game_time` must be a valid EntitySpawn time.
  void step(double game_time, std::int64_t capture_time_us, double now_s, const CaptureSet& set, const UniverseIndex& index);

  // The authority's own ship: entities of its sector are measured from here for the dead-reckoning tiers. Unknown
  // (sector 0) uses the far tier.
  void set_observer(std::uint16_t sector, double x, double y, double z);

  // The link is gone: forget the wire state. The index (and therefore every net id) survives in the flow.
  void on_disconnect();

  [[nodiscard]] const StreamStats& stats() const noexcept { return stats_; }
  [[nodiscard]] std::size_t track_count() const noexcept { return tracks_.size(); }
  [[nodiscard]] const EntityTrack* find(std::uint32_t net_id) const;
  [[nodiscard]] std::size_t pending_despawns() const noexcept;
  [[nodiscard]] const StreamConfig& config() const noexcept { return cfg_; }

 private:
  void housekeeping(const CaptureSet& set, const UniverseIndex& index, double now_s);
  void spawn_due(double game_time, double now_s, const CaptureSet& set, const UniverseIndex& index);
  void update_pass(double now_s, const CaptureSet& set, const UniverseIndex& index);
  void visit(EntityTrack& track, std::uint16_t sector, double now_s, const CaptureSet& set,
             std::vector<StateSample>& out);
  void flush_states(std::int64_t capture_time_us, double game_time);
  void flush_despawns(double now_s);
  void flush_changes();
  void flush_statuses(std::int64_t capture_time_us, double now_s);
  [[nodiscard]] double observer_distance(std::uint16_t sector, const TrackPose& pose) const noexcept;

  PoseReader reader_;
  Sink sink_;
  StreamConfig cfg_;
  CaptureScheduler scheduler_;
  std::unordered_map<std::uint32_t, EntityTrack> tracks_;
  std::unordered_map<std::uint16_t, std::vector<std::uint32_t>> focus_ids_;
  std::unordered_set<std::uint16_t> focus_sweep_;
  std::unordered_set<std::uint16_t> spawn_pending_;
  std::vector<std::uint16_t> sectors_;  // the union of set.sectors() and set.focus() sectors, rebuilt per CaptureSet
  std::vector<DespawnSample> despawn_now_;
  std::vector<ChangeSample> change_now_;
  std::vector<StateSample> states_;
  std::vector<StatusSample> statuses_;
  std::uint32_t tick_ = 0;
  std::uint64_t visit_tick_ = 0;
  std::uint16_t observer_sector_ = 0;
  double observer_x_ = 0.0, observer_y_ = 0.0, observer_z_ = 0.0;
  StreamStats stats_;
};

}  // namespace x4mp::features::auth
