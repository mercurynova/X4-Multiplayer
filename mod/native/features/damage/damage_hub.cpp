#include "features/damage/damage_hub.h"

#include <algorithm>
#include <cmath>

namespace x4mp::features::damage {

bool valid(const DamageOp& op) noexcept {
  if (op.component == 0) return false;
  if (!std::isfinite(op.hull) || !std::isfinite(op.shield)) return false;
  if (op.hull < 0.0 || op.shield < 0.0) return false;
  return op.hull > 0.0 || op.shield > 0.0;
}

DamageOp clamp_hit(std::uint64_t component, double hull, double shield) noexcept {
  const auto clean = [](double v) {
    if (!std::isfinite(v) || v <= 0.0) return 0.0;
    return std::min(v, 100.0);
  };
  return DamageOp{component, clean(hull), clean(shield)};
}

Percent percent_delta(const Percent& before, const Percent& now) noexcept {
  const auto drop = [](double was, double is) {
    if (!std::isfinite(was) || !std::isfinite(is) || was < 0.0 || is < 0.0 || is >= was) return 0.0;
    return was - is;
  };
  return Percent{drop(before.hull, now.hull), drop(before.shield, now.shield)};
}

void DamageHub::queue(const DamageOp& op) {
  if (!valid(op)) {
    ++stats_.ops_dropped;
    return;
  }

  const std::lock_guard<std::mutex> lock(mutex_);
  if (ops_.size() >= kMaxOps) {
    ++stats_.ops_dropped;
    return;
  }

  // Two lines for the same component in one frame are one line: MD would apply them one after the other, and coalescing
  // keeps a burst of hits from spending the whole frame budget on the same ship.
  const auto same = std::find_if(ops_.begin(), ops_.end(), [&](const DamageOp& q) { return q.component == op.component; });
  if (same != ops_.end()) {
    same->hull += op.hull;
    same->shield += op.shield;
  } else {
    ops_.push_back(op);
  }
  ++stats_.ops_queued;
}

std::vector<DamageOp> DamageHub::take_ops(std::size_t max) {
  std::vector<DamageOp> out;
  const std::lock_guard<std::mutex> lock(mutex_);
  if (max == 0 || ops_.empty()) return out;
  const std::size_t n = std::min(max, ops_.size());
  out.assign(ops_.begin(), ops_.begin() + static_cast<std::ptrdiff_t>(n));
  ops_.erase(ops_.begin(), ops_.begin() + static_cast<std::ptrdiff_t>(n));
  return out;
}

std::size_t DamageHub::queued() const {
  const std::lock_guard<std::mutex> lock(mutex_);
  return ops_.size();
}

void DamageHub::reset() {
  const std::lock_guard<std::mutex> lock(mutex_);
  ops_.clear();
  stats_ = DamageStats{};
}

DamageHub& damage_hub() {
  static DamageHub hub;
  return hub;
}

}  // namespace x4mp::features::damage
