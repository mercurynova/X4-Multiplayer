#include "features/selfship/galaxy_map.h"

#include <algorithm>
#include <charconv>

namespace x4mp::features::selfship {

namespace {
bool parse_u64(std::string_view s, std::uint64_t& out) {
  if (s.empty()) return false;
  const auto* end = s.data() + s.size();
  const auto [p, ec] = std::from_chars(s.data(), end, out);
  return ec == std::errc{} && p == end;
}
}  // namespace

void GalaxyMap::reset() {
  pending_.clear();
  by_index_.clear();
  by_id_.clear();
  expected_ = 0;
  end_seen_ = false;
  ready_ = false;
  dropped_ = 0;
  cache_id_ = 0;
  cache_index_ = 0;
}

bool GalaxyMap::add_message(std::string_view data) {
  if (data.size() < 2 || data[1] != ';') return false;
  if (data[0] == 'E') {
    std::uint64_t n = 0;
    if (!parse_u64(data.substr(2), n) || n > 0xFFFF) return false;
    expected_ = static_cast<std::size_t>(n);
    end_seen_ = true;
    finish();
    return true;
  }
  if (data[0] != 'S') return false;
  if (ready_ || end_seen_) reset();  // a new collection
  std::size_t pos = 2;
  bool any = false;
  while (pos <= data.size()) {
    const auto semi = data.find(';', pos);
    const auto rec = data.substr(pos, semi == std::string_view::npos ? std::string_view::npos : semi - pos);
    pos = semi == std::string_view::npos ? data.size() + 1 : semi + 1;
    if (rec.empty()) continue;
    const auto bar = rec.find('|');
    std::uint64_t id = 0;
    if (bar == std::string_view::npos || bar == 0 || !parse_u64(rec.substr(bar + 1), id) || id == 0) {
      ++dropped_;
      continue;
    }
    pending_.push_back({std::string(rec.substr(0, bar)), id});
    any = true;
  }
  return any || dropped_ > 0;
}

void GalaxyMap::finish() {
  ready_ = false;
  if (!end_seen_ || pending_.size() != expected_ || pending_.empty()) return;
  by_index_ = pending_;
  std::sort(by_index_.begin(), by_index_.end(), [](const Rec& a, const Rec& b) { return a.macro < b.macro; });
  by_index_.erase(std::unique(by_index_.begin(), by_index_.end(), [](const Rec& a, const Rec& b) { return a.macro == b.macro; }), by_index_.end());
  if (by_index_.size() > 0xFFFF) by_index_.resize(0xFFFF);
  by_id_.clear();
  by_id_.reserve(by_index_.size());
  for (std::size_t i = 0; i < by_index_.size(); ++i) by_id_.push_back({by_index_[i].id, static_cast<std::uint16_t>(i + 1)});
  std::sort(by_id_.begin(), by_id_.end(), [](const ById& a, const ById& b) { return a.id < b.id; });
  cache_id_ = 0;
  cache_index_ = 0;
  ready_ = true;
}

std::uint16_t GalaxyMap::index_of(std::uint64_t universe_id) const noexcept {
  if (!ready_ || universe_id == 0) return 0;
  if (universe_id == cache_id_) return cache_index_;
  const auto it = std::lower_bound(by_id_.begin(), by_id_.end(), universe_id, [](const ById& r, std::uint64_t id) { return r.id < id; });
  const std::uint16_t index = (it != by_id_.end() && it->id == universe_id) ? it->index : 0;
  cache_id_ = universe_id;
  cache_index_ = index;
  return index;
}

std::uint64_t GalaxyMap::universe_id_of(std::uint16_t index) const noexcept {
  if (!ready_ || index == 0 || index > by_index_.size()) return 0;
  return by_index_[index - 1].id;
}

std::string_view GalaxyMap::macro_of(std::uint16_t index) const noexcept {
  if (!ready_ || index == 0 || index > by_index_.size()) return {};
  return by_index_[index - 1].macro;
}

std::uint16_t GalaxyMap::index_of_macro(std::string_view macro) const noexcept {
  if (!ready_) return 0;
  const auto it = std::lower_bound(by_index_.begin(), by_index_.end(), macro,
                                   [](const Rec& r, std::string_view m) { return std::string_view(r.macro) < m; });
  return (it != by_index_.end() && it->macro == macro) ? static_cast<std::uint16_t>(it - by_index_.begin() + 1) : 0;
}

}  // namespace x4mp::features::selfship
