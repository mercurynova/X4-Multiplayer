#include "features/settlement/settlement_journal.h"

#include <fstream>
#include <utility>

#include <nlohmann/json.hpp>

namespace x4mp::features::settlement {

const char* state_name(State state) noexcept {
  switch (state) {
    case State::Applying: return "applying";
    case State::Applied: return "applied";
    case State::Compensated: return "compensated";
    case State::NeedsAdmin: return "needs_admin";
    case State::Refused: return "refused";
  }
  return "unknown";
}

State state_from_name(std::string_view name) noexcept {
  if (name == "applied") return State::Applied;
  if (name == "compensated") return State::Compensated;
  if (name == "needs_admin") return State::NeedsAdmin;
  if (name == "refused") return State::Refused;
  return State::Applying;
}

void Journal::load(const std::filesystem::path& path) {
  path_ = path;
  entries_.clear();
  problem_.clear();
  std::error_code ec;
  if (path_.empty() || !std::filesystem::exists(path_, ec)) return;

  std::ifstream in(path_, std::ios::binary);
  if (!in) {
    problem_ = "the settlement journal could not be opened";
    return;
  }
  nlohmann::json j = nlohmann::json::parse(in, nullptr, false);
  if (j.is_discarded() || !j.is_object()) {
    problem_ = "the settlement journal does not parse";
    return;
  }
  const auto rows = j.find("entries");
  if (rows == j.end() || !rows->is_array()) {
    problem_ = "the settlement journal has no entries array";
    return;
  }
  for (const auto& row : *rows) {
    if (!row.is_object()) continue;
    JournalEntry entry;
    const auto id = row.find("trade_id");
    if (id == row.end() || !id->is_string() || !Id128Value::parse(id->get<std::string>(), entry.trade_id)) continue;
    if (const auto it = row.find("state"); it != row.end() && it->is_string()) entry.state = state_from_name(it->get<std::string>());
    if (const auto it = row.find("failed_line"); it != row.end() && it->is_number_integer()) {
      entry.failed_line = static_cast<std::int16_t>(it->get<int>());
    }
    if (const auto it = row.find("lines"); it != row.end() && it->is_number_unsigned()) entry.lines = it->get<std::uint32_t>();
    if (const auto it = row.find("applied"); it != row.end() && it->is_number_unsigned()) entry.applied = it->get<std::uint32_t>();
    if (const auto it = row.find("compensated"); it != row.end() && it->is_boolean()) entry.compensated = it->get<bool>();
    if (const auto it = row.find("error"); it != row.end() && it->is_string()) entry.error = it->get<std::string>();
    if (const auto it = row.find("time_us"); it != row.end() && it->is_number_unsigned()) entry.time_us = it->get<std::uint64_t>();
    if (entry.state == State::Applying) {
      // The process died between the first line and the confirm. The universe may carry some of it; only an admin can say
      // which, so the entry stops being a plan and becomes a report.
      entry.state = State::NeedsAdmin;
      entry.error = entry.error.empty() ? "the game stopped during settlement" : entry.error;
      entry.compensated = false;
    }
    entries_.push_back(std::move(entry));
  }
}

const JournalEntry* Journal::find(const Id128Value& trade_id) const noexcept {
  for (const JournalEntry& entry : entries_) {
    if (entry.trade_id.lo == trade_id.lo && entry.trade_id.hi == trade_id.hi) return &entry;
  }
  return nullptr;
}

void Journal::record(const JournalEntry& entry) {
  bool replaced = false;
  for (JournalEntry& existing : entries_) {
    if (existing.trade_id.lo == entry.trade_id.lo && existing.trade_id.hi == entry.trade_id.hi) {
      existing = entry;
      replaced = true;
      break;
    }
  }
  if (!replaced) {
    entries_.push_back(entry);
    if (entries_.size() > kMaxEntries) entries_.erase(entries_.begin());
  }
  save();
}

void Journal::save() {
  if (path_.empty()) {
    problem_ = "the settlement journal has no path";
    return;
  }
  nlohmann::json j;
  j["v"] = 1;
  nlohmann::json rows = nlohmann::json::array();
  for (const JournalEntry& entry : entries_) {
    nlohmann::json row;
    row["trade_id"] = entry.trade_id.hex();
    row["state"] = state_name(entry.state);
    row["failed_line"] = entry.failed_line;
    row["lines"] = entry.lines;
    row["applied"] = entry.applied;
    row["compensated"] = entry.compensated;
    row["error"] = entry.error;
    row["time_us"] = entry.time_us;
    rows.push_back(std::move(row));
  }
  j["entries"] = std::move(rows);

  // Temp file + rename: a crash leaves the previous journal or the new one, never a half-written file.
  std::filesystem::path temp = path_;
  temp += ".tmp";
  {
    std::ofstream out(temp, std::ios::binary | std::ios::trunc);
    if (!out) {
      problem_ = "the settlement journal could not be written";
      return;
    }
    const std::string text = j.dump();
    out.write(text.data(), static_cast<std::streamsize>(text.size()));
    if (!out) {
      problem_ = "the settlement journal could not be written";
      return;
    }
  }
  std::error_code ec;
  std::filesystem::rename(temp, path_, ec);
  if (ec) {
    problem_ = "the settlement journal could not be replaced: " + ec.message();
    return;
  }
  problem_.clear();
}

}  // namespace x4mp::features::settlement
