#include "features/ghosts/local_suppression.h"

#include <algorithm>

namespace x4mp::features::ghosts {

void LocalSuppression::on_sector_closed(std::uint16_t sector) {
  if (sector == 0) return;
  SectorState& state = sectors_[sector];
  state.closed = true;
  state.last_ask_us = -1;  // a (re-)close asks at once, even if the sector was scanned a moment ago
}

void LocalSuppression::on_interest(std::span<const std::uint16_t> sectors) {
  interest_.clear();
  for (const auto sector : sectors) {
    if (sector != 0) interest_.insert(sector);
  }
}

void LocalSuppression::on_scan(std::uint16_t sector, std::span<const auth::ScanRow> rows, const SkipFn& skip) {
  if (sector == 0) return;
  SectorState& state = sectors_[sector];
  ++stats_.scans;
  stats_.rows += rows.size();
  // M4-13: nothing is suppressed before the server closed the sector (SectorComplete): until then the authority may not have streamed every
  // ship of it, and a ship we removed too early would simply vanish.
  if (!state.closed) return;
  for (const auto& row : rows) {
    if (row.component == 0) continue;
    if (row.mission) {
      ++stats_.skipped_mission;
      continue;
    }
    if (row.owner == "player") {
      ++stats_.skipped_player;
      continue;
    }
    if (skip && skip(row.component)) {
      ++stats_.skipped_known;
      continue;
    }
    if (!state.suppressed.insert(row.component).second) {
      ++stats_.skipped_dup;
      continue;
    }
    queue_.push_back(row.component);
    ++stats_.queued;
  }
}

void LocalSuppression::frame(std::int64_t now_us, const AskFn& ask, const RemoveFn& remove) {
  // M4-13: ask for the ships of every closed sector in interest. The first ask is immediate; after that the sweep re-asks once a second.
  for (auto& [sector, state] : sectors_) {
    if (!state.closed || !interest_.contains(sector)) continue;
    if (state.last_ask_us >= 0 && now_us - state.last_ask_us < cfg_.rescan_interval_us) continue;
    state.last_ask_us = now_us;
    ++stats_.requests;
    if (ask) ask(sector);
  }

  // M4-13: remove what is queued, but never more than the frame budget, so a 400-ship sector does not stall the game.
  std::size_t done = 0;
  while (queue_head_ < queue_.size() && done < cfg_.removals_per_frame) {
    const std::uint64_t component = queue_[queue_head_++];
    ++done;
    ++stats_.removed;
    if (remove) remove(component);
  }
  // Keep the queue from growing without bound when nobody drains it (a frame budget below the arrival rate is fine; a dead caller is not).
  if (queue_head_ == queue_.size()) {
    queue_.clear();
    queue_head_ = 0;
  } else if (queue_head_ > 4096) {
    queue_.erase(queue_.begin(), queue_.begin() + static_cast<std::ptrdiff_t>(queue_head_));
    queue_head_ = 0;
  }
}

std::size_t LocalSuppression::suppressed_of(std::uint16_t sector) const {
  const auto it = sectors_.find(sector);
  return it == sectors_.end() ? 0 : it->second.suppressed.size();
}

std::size_t LocalSuppression::total_suppressed() const {
  std::size_t n = 0;
  for (const auto& [sector, state] : sectors_) {
    (void)sector;
    n += state.suppressed.size();
  }
  return n;
}

bool LocalSuppression::closed(std::uint16_t sector) const {
  const auto it = sectors_.find(sector);
  return it != sectors_.end() && it->second.closed;
}

void LocalSuppression::clear() {
  sectors_.clear();
  interest_.clear();
  queue_.clear();
  queue_head_ = 0;
  stats_ = Stats{};
}

}  // namespace x4mp::features::ghosts
