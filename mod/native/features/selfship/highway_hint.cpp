#include "features/selfship/highway_hint.h"

namespace x4mp::features::selfship {

HighwaySignal parse_highway_signal(std::string_view raw) {
  HighwaySignal out;
  if (raw.empty()) return out;
  const auto bar = raw.find('|');
  if (bar == std::string_view::npos) {
    out.source.assign(raw);
    return out;
  }
  out.source.assign(raw.substr(0, bar));
  out.target.assign(raw.substr(bar + 1));
  return out;
}

bool HighwayHintGate::accept(std::string_view target, std::int64_t now_us) {
  if (target.empty()) return false;
  const bool other_target = target != last_target_;
  if (!other_target && last_us_ != 0 && now_us - last_us_ < interval_us_) return false;
  last_target_.assign(target);
  last_us_ = now_us;
  ++sent_;
  return true;
}

void HighwayHintGate::reset() noexcept {
  last_target_.clear();
  last_us_ = 0;
  sent_ = 0;
}

}  // namespace x4mp::features::selfship
