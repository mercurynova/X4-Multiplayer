#pragma once
// features/authority: the capture scheduler (M4-06; docs/m4-plan.md W2).
//
// The authority ticks at kTickHz (the WorldUpdate rate). A sector captured at `rate_hz` must visit every entity of that
// sector `rate_hz` times per second - but never all of them in one tick: a 200-ship sector at 1 Hz would otherwise emit a
// 200-entry burst every second. So the scheduler hands out a rotating SLICE of the sector's id list every tick:
//
//   n = ceil(total * rate_hz / kTickHz)          how many entities to visit this tick
//   begin = cursor; cursor = (cursor + n) % total
//
// A 200-ship sector at 1 Hz visits 10 per tick and completes a sweep every 20 ticks; at 5 Hz it visits 50 per tick and
// sweeps every 4 ticks (5x as often, as the AC says). A 20 Hz sector visits everything every tick. A sector with fewer
// entities than the tick rate still visits at least one per tick: otherwise a 3-ship 1 Hz sector would never be swept.
//
// The slice is a *poll* order, not a send order: the streamer dead-reckons every visited entity and only sends the ones
// whose state moved beyond the thresholds (M4-07). Entities inside a focus sphere are polled every tick in addition to
// the slice (they need 20 Hz); that is the streamer's job, not the scheduler's.
//
// Pure logic: no SDK, no flatbuffers.

#include <cstddef>
#include <cstdint>
#include <unordered_map>

namespace x4mp::features::auth {

struct CaptureSlice {
  std::size_t begin = 0;  // index into the sector's id list
  std::size_t count = 0;  // 0 = visit nothing (sector not active, or no entities)
};

class CaptureScheduler {
 public:
  static constexpr std::uint32_t kTickHz = 20;  // the authority's WorldUpdate rate

  // The rate the CaptureSet gives a sector. 0 removes the sector (it is no longer captured).
  void set_rate(std::uint16_t sector, std::uint8_t rate_hz);
  void drop(std::uint16_t sector) noexcept;
  [[nodiscard]] bool active(std::uint16_t sector) const noexcept;
  [[nodiscard]] std::uint8_t rate_of(std::uint16_t sector) const noexcept;
  [[nodiscard]] std::size_t cursor_of(std::uint16_t sector) const noexcept;

  // The slice of a `total`-entity sector to poll this tick; advances the sector's cursor by the slice size.
  // `total` is the current size of the sector's id list (it changes when the index adds or drops a ship).
  [[nodiscard]] CaptureSlice next(std::uint16_t sector, std::size_t total) noexcept;

  [[nodiscard]] std::size_t sector_count() const noexcept { return rows_.size(); }
  void clear() noexcept { rows_.clear(); }

 private:
  struct Row {
    std::uint8_t rate_hz = 0;
    std::size_t cursor = 0;
  };
  std::unordered_map<std::uint16_t, Row> rows_;
};

}  // namespace x4mp::features::auth
