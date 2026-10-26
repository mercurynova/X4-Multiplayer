#pragma once
// features/authority: the authority's capture set (M4-01; protocol.md §9.1, protocol/schema/world.fbs `CaptureSet`).
//
// The server owns interest: it sends one `CaptureSet` on the Control lane at most once per 500 ms after any change, and the authority must
// capture ONLY what the set names - every entity of a listed sector at its rate, every entity inside a focus sphere at the sphere's rate. This
// file is the model and its rules; `capture_set_wire.cpp` decodes the flatbuffer and `universe_index.cpp` holds the entities themselves.
//
// Pure logic: no SDK, no flatbuffers, so the unit tests drive it directly.

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace x4mp::features::auth {

struct CaptureSector {
  std::uint16_t sector = 0;  // wire sector index (features/selfship/galaxy_map.h); 0 is "no sector" and never captured
  std::uint8_t rate_hz = 0;
  bool operator==(const CaptureSector&) const = default;
};

struct CaptureFocus {
  std::uint16_t sector = 0;
  float cx = 0.0f;  // sector-local metres
  float cy = 0.0f;
  float cz = 0.0f;
  float radius_m = 0.0f;
  std::uint8_t rate_hz = 0;
  bool operator==(const CaptureFocus&) const = default;
};

// What the wire carried, before the staleness rule is applied. Kept separate from CaptureSet so that decoding never decides whether a set is
// still current: that is apply()'s job and it is what the tests pin.
struct CaptureSetData {
  std::uint32_t epoch = 0;
  std::vector<CaptureSector> sectors;
  std::vector<CaptureFocus> focus;
};

class CaptureSet {
 public:
  // The rate the sector as a whole is captured at: the row from `sectors`, 0 when the sector is not listed there. A sector that only a focus
  // sphere names is not captured as a whole; use contains()/rate_at() for that case.
  [[nodiscard]] std::uint8_t rate_of(std::uint16_t sector) const noexcept;
  [[nodiscard]] bool contains(std::uint16_t sector) const noexcept;
  // The rate for one entity: the sector rate, raised to a focus rate when the entity stands inside a focus sphere of that sector. A sector that
  // only a focus names has rate 0 outside its spheres.
  [[nodiscard]] std::uint8_t rate_at(std::uint16_t sector, float x, float y, float z) const noexcept;

  [[nodiscard]] bool empty() const noexcept { return sectors_.empty() && focus_.empty(); }
  [[nodiscard]] std::uint32_t epoch() const noexcept { return epoch_; }
  [[nodiscard]] bool applied() const noexcept { return applied_; }
  [[nodiscard]] const std::vector<CaptureSector>& sectors() const noexcept { return sectors_; }
  [[nodiscard]] const std::vector<CaptureFocus>& focus() const noexcept { return focus_; }

  // Replace the whole set when `epoch` is newer than the one held. False for a stale or repeated epoch: the server may resend a set, and a
  // resend must not restart capture (that would re-spawn every entity of every sector). The first set is always accepted, including epoch 0.
  bool apply(std::uint32_t epoch, std::vector<CaptureSector> sectors, std::vector<CaptureFocus> focus);
  void clear() noexcept;

  // "3 sectors [12@5Hz 44@1Hz], 1 focus [44 r=2000m@20Hz]" - the shape the 30 s line and the change log print.
  [[nodiscard]] std::string describe() const;
  // What changed against `previous`, one clause per difference; empty when the two sets capture the same thing.
  [[nodiscard]] std::string describe_change(const CaptureSet& previous) const;

 private:
  std::uint32_t epoch_ = 0;
  bool applied_ = false;
  std::vector<CaptureSector> sectors_;  // one row per sector, sorted by sector
  std::vector<CaptureFocus> focus_;
};

}  // namespace x4mp::features::auth
