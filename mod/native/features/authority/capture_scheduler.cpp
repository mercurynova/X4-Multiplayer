#include "features/authority/capture_scheduler.h"

#include <algorithm>

namespace x4mp::features::auth {

void CaptureScheduler::set_rate(std::uint16_t sector, std::uint8_t rate_hz) {
  if (sector == 0) return;  // "no sector" is never captured
  if (rate_hz == 0) {
    drop(sector);
    return;
  }
  auto [it, inserted] = rows_.try_emplace(sector);
  (void)inserted;
  it->second.rate_hz = rate_hz;
  // The cursor is deliberately kept: a rate change (a focus sphere appears, a tier changes) must not restart the sweep
  // in the middle of a sector, or the ships at the start of the list would be polled twice while the rest waits.
}

void CaptureScheduler::drop(std::uint16_t sector) noexcept { rows_.erase(sector); }

bool CaptureScheduler::active(std::uint16_t sector) const noexcept {
  const auto it = rows_.find(sector);
  return it != rows_.end() && it->second.rate_hz != 0;
}

std::uint8_t CaptureScheduler::rate_of(std::uint16_t sector) const noexcept {
  const auto it = rows_.find(sector);
  return it == rows_.end() ? 0 : it->second.rate_hz;
}

std::size_t CaptureScheduler::cursor_of(std::uint16_t sector) const noexcept {
  const auto it = rows_.find(sector);
  return it == rows_.end() ? 0 : it->second.cursor;
}

CaptureSlice CaptureScheduler::next(std::uint16_t sector, std::size_t total) noexcept {
  const auto it = rows_.find(sector);
  if (it == rows_.end() || it->second.rate_hz == 0 || total == 0) return {};
  Row& row = it->second;
  if (row.cursor >= total) row.cursor %= total;

  // ceil(total * rate / kTickHz) in integer arithmetic (no float, no overflow for realistic sector sizes).
  const std::uint64_t scaled = static_cast<std::uint64_t>(total) * row.rate_hz;
  const std::uint64_t n = (scaled + kTickHz - 1) / kTickHz;
  const std::size_t count = static_cast<std::size_t>(std::min<std::uint64_t>(std::max<std::uint64_t>(n, 1), total));

  CaptureSlice slice{row.cursor, count};
  row.cursor = (row.cursor + count) % total;
  return slice;
}

}  // namespace x4mp::features::auth
