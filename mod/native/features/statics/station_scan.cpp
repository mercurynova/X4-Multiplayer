#include "features/statics/station_scan.h"

#include <charconv>

namespace x4mp::features::statics {
namespace {

std::string_view trim(std::string_view s) {
  while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\r' || s.front() == '\n')) s.remove_prefix(1);
  while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r' || s.back() == '\n')) s.remove_suffix(1);
  return s;
}

// One field, split on `sep`. `rest` is advanced past the separator.
std::string_view next_sep(std::string_view& rest, char sep) {
  const auto pos = rest.find(sep);
  if (pos == std::string_view::npos) {
    const auto out = rest;
    rest = {};
    return out;
  }
  const auto out = rest.substr(0, pos);
  rest.remove_prefix(pos + 1);
  return out;
}

bool parse_u64(std::string_view text, std::uint64_t& out) {
  text = trim(text);
  if (text.empty()) return false;
  // MD prints a component id in hex ("0x18ead5"), so accept both bases (same rule as the capture scan).
  int base = 10;
  if (text.size() > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) {
    base = 16;
    text.remove_prefix(2);
  }
  if (text.empty()) return false;
  const auto r = std::from_chars(text.data(), text.data() + text.size(), out, base);
  return r.ec == std::errc{} && r.ptr == text.data() + text.size();
}

// MD writes metres as a plain decimal number; from_chars is locale-independent, which is what two clients need to agree on.
bool parse_double(std::string_view text, double& out) {
  text = trim(text);
  if (text.empty()) return false;
  const auto r = std::from_chars(text.data(), text.data() + text.size(), out);
  return r.ec == std::errc{} && r.ptr == text.data() + text.size();
}

// One '|'-separated row: component|macro|x|y|z[|idcode][|owner]. Extra fields are ignored, so MD can append without breaking the
// parser.
bool parse_row(std::string_view text, StationRow& out) {
  text = trim(text);
  if (text.empty()) return false;
  if (!parse_u64(next_sep(text, '|'), out.component) || out.component == 0) return false;
  const auto macro = trim(next_sep(text, '|'));
  if (macro.empty()) return false;
  if (!parse_double(next_sep(text, '|'), out.x)) return false;
  if (!parse_double(next_sep(text, '|'), out.y)) return false;
  if (!parse_double(next_sep(text, '|'), out.z)) return false;
  out.macro.assign(macro);
  if (!text.empty()) out.idcode.assign(trim(next_sep(text, '|')));  // M4-18 tie-break, optional
  if (!text.empty()) out.owner.assign(trim(next_sep(text, '|')));   // M5-17, optional
  return true;
}

}  // namespace

std::optional<StationBatch> parse_station_batch(std::string_view data) {
  data = trim(data);
  if (data.size() < 3) return std::nullopt;
  const char kind = data[0];
  if ((kind != 'S' && kind != 'E') || data[1] != ';') return std::nullopt;
  std::string_view rest = data.substr(2);
  const auto sector = trim(next_sep(rest, ';'));
  if (sector.empty()) return std::nullopt;

  StationBatch batch;
  batch.sector_macro.assign(sector);
  if (kind == 'E') {
    std::uint64_t count = 0;
    if (!parse_u64(rest, count)) return std::nullopt;
    batch.count = static_cast<std::uint32_t>(count);
    batch.end = true;
    return batch;
  }
  while (!rest.empty()) {
    const auto row_text = next_sep(rest, ';');
    if (trim(row_text).empty()) continue;
    StationRow row;
    if (parse_row(row_text, row)) batch.rows.push_back(std::move(row));
  }
  return batch;
}

}  // namespace x4mp::features::statics
