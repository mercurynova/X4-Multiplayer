#include "features/authority/capture_ingest.h"

#include <charconv>

namespace x4mp::features::auth {
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

// One ';'-separated field of the batch.
std::string_view next_field(std::string_view& rest) { return next_sep(rest, ';'); }

// One '|'-separated row: component|macro|owner|mission. The mission field is optional (M4-13: the client's suppression scan appends it; the
// authority ignores it), so a three-field row stays valid.
bool parse_row(std::string_view text, ScanRow& out) {
  text = trim(text);
  if (text.empty()) return false;
  const auto component_text = trim(next_sep(text, '|'));
  if (component_text.empty()) return false;
  // MD prints a component id in hex ("0x1b665b"), so accept both bases (same rule as the on-foot spawn answer).
  int base = 10;
  std::string_view digits = component_text;
  if (digits.size() > 2 && digits[0] == '0' && (digits[1] == 'x' || digits[1] == 'X')) {
    base = 16;
    digits.remove_prefix(2);
  }
  if (digits.empty()) return false;
  std::uint64_t component = 0;
  const auto res = std::from_chars(digits.data(), digits.data() + digits.size(), component, base);
  if (res.ec != std::errc{} || res.ptr != digits.data() + digits.size() || component == 0) return false;
  const auto macro = trim(next_sep(text, '|'));
  if (macro.empty()) return false;
  const auto owner = trim(next_sep(text, '|'));
  const auto mission = trim(next_sep(text, '|'));
  out.component = component;
  out.macro.assign(macro);
  out.owner.assign(owner);
  out.mission = mission == "1" || mission == "true";
  return true;
}

}  // namespace

std::optional<ScanBatch> parse_scan_batch(std::string_view data) {
  data = trim(data);
  if (data.size() < 2) return std::nullopt;
  const char kind = data[0];
  if (data[1] != ';' || (kind != 'S' && kind != 'E')) return std::nullopt;
  data.remove_prefix(2);

  ScanBatch out;
  const auto sector = trim(next_field(data));
  if (sector.empty()) return std::nullopt;
  out.sector_macro.assign(sector);
  out.end = kind == 'E';

  if (out.end) {
    const auto count_text = trim(data);
    if (count_text.empty()) return std::nullopt;
    std::uint32_t count = 0;
    const auto res = std::from_chars(count_text.data(), count_text.data() + count_text.size(), count);
    if (res.ec != std::errc{} || res.ptr != count_text.data() + count_text.size()) return std::nullopt;
    out.count = count;
    return out;
  }

  while (!data.empty()) {
    const auto field = trim(next_field(data));
    if (field.empty()) continue;
    ScanRow row;
    if (parse_row(field, row)) out.rows.push_back(std::move(row));
  }
  return out;
}

}  // namespace x4mp::features::auth
