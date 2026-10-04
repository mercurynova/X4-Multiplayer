#pragma once
// features/ghosts: GhostCore = the driver + the wire-side state around it (M3-10): the string table, the sector map and the decoding of
// EntitySpawn / EntityChange / EntityDespawn / StringTableAdd / Replication into driver calls. SDK-free (FlatBuffers only), no game
// calls: unit-testable with hand-built messages. The feature (ghost_feature.*) owns one GhostCore per DLL incarnation; the join
// feature's frame pump hands the frames over through ghost_hub() (ghost_hub.h).

#include <cstdint>
#include <map>
#include <span>
#include <string>
#include <string_view>

#include "features/ghosts/ghost_driver.h"

namespace x4mp::features::ghosts {

// The session string table (StringTableAdd): macro and faction names the entity messages refer to by index. It is lost with the DLL on a
// reload and the server does not replay it on a resume, so the feature keeps it in the stash (to_text / from_text).
class StringTable {
 public:
  struct Entry {
    std::uint8_t kind = 0;
    std::string value;
  };
  void set(std::uint32_t index, std::uint8_t kind, std::string value);
  [[nodiscard]] const Entry* find(std::uint32_t index) const noexcept;
  [[nodiscard]] std::string_view value(std::uint32_t index) const noexcept;  // "" when unknown
  [[nodiscard]] std::size_t size() const noexcept { return map_.size(); }
  [[nodiscard]] std::uint32_t version() const noexcept { return version_; }  // bumps on every change
  void clear() noexcept;
  [[nodiscard]] std::string to_text() const;
  bool from_text(std::string_view text);

 private:
  std::map<std::uint32_t, Entry> map_;
  std::uint32_t version_ = 0;
};

struct MessageCounters {
  std::uint64_t replication = 0, replication_entries = 0, replication_bad = 0;
  std::uint64_t spawn_messages = 0, spawn_player_ships = 0, spawn_ignored = 0, spawn_bad = 0, spawn_unresolved = 0;
  std::uint64_t change = 0, despawn = 0, strings = 0, bad_frames = 0;
  std::map<std::uint16_t, std::uint32_t> frames_seen;  // every inbound frame type the hub saw (diagnostics: which messages reached us)
};

// The Replication time base is the AUTHORITY's capture clock (Replication.server_time_us = the latest WorldUpdate's capture time). It is
// the server clock only as far as the authority's estimate is right; a node's own clock-sync estimate (core/net) can disagree with it by
// anything from milliseconds to seconds (the FakeNode authority stamps ticks since its own start). Rendering "now - delay" in the wrong
// base leaves the ghost past its newest sample (held, 200+ m off). So the render clock is the STREAM clock: now minus the smallest
// (arrival estimate - Replication time) of the last kWindow messages, i.e. the stream's own "present", whatever its offset to the server
// clock; with a consistent time base it differs from the server clock by the minimum network latency only.
class StreamClock {
 public:
  static constexpr std::size_t kWindow = 128;
  void on_message(std::int64_t arrival_server_us, std::uint64_t reference_us) noexcept {
    const std::int64_t d = arrival_server_us - static_cast<std::int64_t>(reference_us);
    ring_[head_] = d;
    head_ = (head_ + 1) % kWindow;
    if (n_ < kWindow) ++n_;
  }
  // arrival - reference, minimised over the window; 0 until the first message.
  [[nodiscard]] std::int64_t bias_us() const noexcept {
    if (n_ == 0) return 0;
    std::int64_t m = ring_[0];
    for (std::size_t i = 1; i < n_; ++i) m = ring_[i] < m ? ring_[i] : m;
    return m;
  }
  [[nodiscard]] bool ready() const noexcept { return n_ >= 4; }
  void reset() noexcept { n_ = 0; head_ = 0; }

 private:
  std::int64_t ring_[kWindow]{};
  std::size_t head_ = 0, n_ = 0;
};

class GhostCore {
 public:
  GhostCore(GhostConfig cfg, GhostDriver::LogFn log) : driver(cfg, std::move(log)) {}

  GhostDriver driver;
  StringTable strings;
  MessageCounters counters;
  StreamClock stream_clock;  // see StreamClock: the render clock is now_server - stream_clock.bias_us()
  std::int64_t clock_offset_us = 0;  // server - local steady clock; 0 until the first Pong

  // Handles one inbound frame of the session. Returns true when the type is one this core consumes (the caller keeps routing it
  // to the other features anyway). `now_server_us` = the receiver's server-clock estimate now.
  bool on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_player_id, std::int64_t now_server_us);

  // Message type ids this core reacts to (the join feature's hook can pre-filter).
  [[nodiscard]] static bool wants(std::uint16_t type) noexcept;

 private:
  void on_string_table(std::span<const std::uint8_t> payload);
  void on_entity_spawn(std::span<const std::uint8_t> payload, std::int64_t now_us);
  void on_entity_change(std::span<const std::uint8_t> payload);
  void on_entity_despawn(std::span<const std::uint8_t> payload);
  void on_replication(std::span<const std::uint8_t> payload, std::int64_t now_us);
};

}  // namespace x4mp::features::ghosts
