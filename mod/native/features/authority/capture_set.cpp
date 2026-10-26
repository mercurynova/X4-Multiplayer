#include "features/authority/capture_set.h"

#include <algorithm>
#include <cmath>

namespace x4mp::features::auth {
namespace {

// "12@5Hz" for one sector row.
void append_sector(std::string& out, std::uint16_t sector, unsigned rate) {
  out += std::to_string(sector);
  out += '@';
  out += std::to_string(rate);
  out += "Hz";
}

// Sorted by sector and one row per sector: the server builds the set as a union over clients and may list a sector twice (two clients in the
// same sector at the same rate), and a duplicate would otherwise double-schedule it.
std::vector<CaptureSector> normalise_sectors(std::vector<CaptureSector> in) {
  std::erase_if(in, [](const CaptureSector& s) { return s.sector == 0 || s.rate_hz == 0; });
  std::sort(in.begin(), in.end(), [](const CaptureSector& a, const CaptureSector& b) { return a.sector < b.sector; });
  std::vector<CaptureSector> out;
  out.reserve(in.size());
  for (const auto& s : in) {
    if (!out.empty() && out.back().sector == s.sector) {
      out.back().rate_hz = std::max(out.back().rate_hz, s.rate_hz);
      continue;
    }
    out.push_back(s);
  }
  return out;
}

std::vector<CaptureFocus> normalise_focus(std::vector<CaptureFocus> in) {
  std::erase_if(in, [](const CaptureFocus& f) {
    return f.sector == 0 || f.rate_hz == 0 || !(f.radius_m > 0.0f) || !std::isfinite(f.radius_m) || !std::isfinite(f.cx) ||
           !std::isfinite(f.cy) || !std::isfinite(f.cz);
  });
  std::sort(in.begin(), in.end(), [](const CaptureFocus& a, const CaptureFocus& b) {
    if (a.sector != b.sector) return a.sector < b.sector;
    if (a.radius_m != b.radius_m) return a.radius_m > b.radius_m;
    return a.rate_hz > b.rate_hz;
  });
  return in;
}

}  // namespace

std::uint8_t CaptureSet::rate_of(std::uint16_t sector) const noexcept {
  if (sector == 0) return 0;
  std::uint8_t best = 0;
  for (const auto& s : sectors_) {
    if (s.sector == sector) best = std::max(best, s.rate_hz);
  }
  return best;
}

bool CaptureSet::contains(std::uint16_t sector) const noexcept {
  if (rate_of(sector) != 0) return true;
  if (sector == 0) return false;
  return std::any_of(focus_.begin(), focus_.end(), [sector](const CaptureFocus& f) { return f.sector == sector; });
}

std::uint8_t CaptureSet::rate_at(std::uint16_t sector, float x, float y, float z) const noexcept {
  std::uint8_t best = rate_of(sector);
  for (const auto& f : focus_) {
    if (f.sector != sector) continue;
    const float dx = x - f.cx;
    const float dy = y - f.cy;
    const float dz = z - f.cz;
    if (dx * dx + dy * dy + dz * dz <= f.radius_m * f.radius_m) best = std::max(best, f.rate_hz);
  }
  return best;
}

bool CaptureSet::apply(std::uint32_t epoch, std::vector<CaptureSector> sectors, std::vector<CaptureFocus> focus) {
  // A resend of the same epoch is not a change: restarting capture would re-send EntitySpawn for every entity of every sector.
  if (applied_ && epoch <= epoch_) return false;
  sectors_ = normalise_sectors(std::move(sectors));
  focus_ = normalise_focus(std::move(focus));
  epoch_ = epoch;
  applied_ = true;
  return true;
}

void CaptureSet::clear() noexcept {
  epoch_ = 0;
  applied_ = false;
  sectors_.clear();
  focus_.clear();
}

std::string CaptureSet::describe() const {
  std::string out;
  out += std::to_string(sectors_.size());
  out += sectors_.size() == 1 ? " sector [" : " sectors [";
  for (std::size_t i = 0; i < sectors_.size(); ++i) {
    if (i != 0) out += ' ';
    append_sector(out, sectors_[i].sector, sectors_[i].rate_hz);
  }
  out += "], ";
  out += std::to_string(focus_.size());
  out += focus_.size() == 1 ? " focus [" : " focus [";
  for (std::size_t i = 0; i < focus_.size(); ++i) {
    if (i != 0) out += ' ';
    out += std::to_string(focus_[i].sector);
    out += " r=";
    out += std::to_string(static_cast<long long>(std::lround(focus_[i].radius_m)));
    out += "m@";
    out += std::to_string(focus_[i].rate_hz);
    out += "Hz";
  }
  out += ']';
  return out;
}

std::string CaptureSet::describe_change(const CaptureSet& previous) const {
  std::string out;
  for (const auto& s : sectors_) {
    const auto before = previous.rate_of(s.sector);
    if (before == s.rate_hz) continue;
    if (!out.empty()) out += ", ";
    out += before == 0 ? "added " : "rate ";
    append_sector(out, s.sector, s.rate_hz);
    if (before != 0) {
      out += " (was ";
      out += std::to_string(before);
      out += "Hz)";
    }
  }
  for (const auto& s : previous.sectors_) {
    if (contains(s.sector)) continue;
    if (!out.empty()) out += ", ";
    out += "dropped ";
    append_sector(out, s.sector, s.rate_hz);
  }
  for (const auto& f : focus_) {
    const bool same = std::any_of(previous.focus_.begin(), previous.focus_.end(), [&f](const CaptureFocus& p) { return p == f; });
    if (same) continue;
    if (!out.empty()) out += ", ";
    out += "focus ";
    out += std::to_string(f.sector);
    out += " r=";
    out += std::to_string(static_cast<long long>(std::lround(f.radius_m)));
    out += "m@";
    out += std::to_string(f.rate_hz);
    out += "Hz";
  }
  for (const auto& f : previous.focus_) {
    const bool same = std::any_of(focus_.begin(), focus_.end(), [&f](const CaptureFocus& p) { return p == f; });
    if (same) continue;
    if (!out.empty()) out += ", ";
    out += "focus off ";
    out += std::to_string(f.sector);
  }
  return out;
}

}  // namespace x4mp::features::auth
