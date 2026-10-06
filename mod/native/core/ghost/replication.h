#pragma once
// core/ghost: decode of Replication messages into per-entity sample streams (M3-02; protocol.md 10.2, docs/m3-plan.md 4.6).
//
// Replication fields are ABSOLUTE values and an omitted field means "same as this client's baseline" (world.fbs), so the decoder
// keeps one baseline Sample per net_id and merges every entry onto it. The decoder is the ONLY place that reads Replication entries
// for ghosts; this is where schema deltas land (D1: StateFlags.Hidden is bit 10 and arrives in FLAGS like any other bit, so it is
// already covered; D2 touches ManifestEntry, not Replication).
//
// Sample time = Replication.server_time_us + TIME offset (ms); without TIME the entry stands for server_time_us itself.
//
//   * entry with Pos, Rot or Vel (the timed fields): emits a pose sample to the sink -> Interpolator::push. An omitted Pos means "same
//     as the baseline" (the ship did not move), so a rotation-only entry (a ship turning in place) is a true pose sample (M3-26).
//   * entry with none of them (flags / status only): emits a state update -> Interpolator::apply_state (no TIME, no pose).
//
// Allocation: the baseline table is a sorted vector reserved at construction; the only allocation after that is the first entry of
// a new net_id beyond the reserve (a spawn, not the per-frame path). decode() itself never allocates.

#include <cstddef>
#include <cstdint>
#include <memory>
#include <utility>
#include <vector>

#include "x4mp/wire.h"

#include "core/ghost/interpolator.h"
#include "core/ghost/sample.h"

namespace x4mp::ghost {

// What one entry means for a ghost.
struct EntityUpdate {
  std::uint32_t net_id = 0;
  std::uint8_t mask = 0;       // wire mask of the entry (which fields were present)
  bool has_pose = false;       // kRepPos, kRepRot or kRepVel present: `sample` is a full pose sample
  Sample sample{};             // merged with the baseline; t_us is the sample time
};

class ReplicationDecoder {
 public:
  explicit ReplicationDecoder(std::size_t reserve = 32) { baselines_.reserve(reserve); }

  // Decodes `Replication.entries` (exactly entry_count entries) and calls sink(const EntityUpdate&) for each. Returns the wire
  // error for malformed input (entries decoded before the bad one were already delivered).
  template <class Sink>
  wire::Result<void> decode(std::uint64_t server_time_us, wire::ByteSpan entries, std::size_t entry_count, Sink&& sink) {
    return wire::decode_replication(entries, entry_count, [&](const wire::ReplicationEntry& e) {
      EntityUpdate u;
      merge(server_time_us, e, u);
      sink(static_cast<const EntityUpdate&>(u));
    });
  }

  void forget(std::uint32_t net_id);  // EntityDespawn
  void reset() noexcept { baselines_.clear(); }  // server reset the baselines (resume / InterestResync)
  [[nodiscard]] std::size_t tracked() const noexcept { return baselines_.size(); }

 private:
  struct Base {
    std::uint32_t net_id = 0;
    Sample s{};
    bool coarse = false;
  };
  void merge(std::uint64_t server_time_us, const wire::ReplicationEntry& e, EntityUpdate& out);
  Base& base_for(std::uint32_t net_id);
  std::vector<Base> baselines_;  // sorted by net_id
};

// The live streams of one node: decoder + one Interpolator per net_id. Convenience used by the ghost driver (client) and the
// avatar driver (authority). Interpolators are heap objects created on first sight of a net_id (spawn), never in the frame path.
class StreamSet {
 public:
  explicit StreamSet(const InterpolatorConfig& cfg = {}) : cfg_(cfg) { streams_.reserve(16); }

  // Feeds one Replication message. arrival_us = the receiver's server-time estimate now (jitter statistics).
  // Entries for net_ids the caller has not `track`ed are still decoded (baselines stay right) but dropped when `only_tracked`.
  wire::Result<void> ingest(std::uint64_t server_time_us, wire::ByteSpan entries, std::size_t entry_count, std::int64_t arrival_us,
                            bool only_tracked = true) {
    return decoder_.decode(server_time_us, entries, entry_count, [&](const EntityUpdate& u) {
      Interpolator* in = find(u.net_id);
      if (!in) {
        if (only_tracked) return;
        in = &ensure(u.net_id);
      }
      if (u.has_pose) in->push(u.sample, arrival_us);
      else in->apply_state(u.sample.t_us, u.sample.flags, u.sample.hull, u.sample.shield);
    });
  }

  Interpolator& ensure(std::uint32_t net_id);  // creates on first call (allocates)
  [[nodiscard]] Interpolator* find(std::uint32_t net_id) noexcept;
  void erase(std::uint32_t net_id);  // also forgets the decoder baseline
  void clear();
  [[nodiscard]] std::size_t size() const noexcept { return streams_.size(); }
  [[nodiscard]] ReplicationDecoder& decoder() noexcept { return decoder_; }

  template <class Fn>
  void for_each(Fn&& fn) {
    for (auto& s : streams_) fn(s.first, *s.second);
  }

 private:
  InterpolatorConfig cfg_;
  ReplicationDecoder decoder_;
  std::vector<std::pair<std::uint32_t, std::unique_ptr<Interpolator>>> streams_;  // sorted by net_id
};

}  // namespace x4mp::ghost
