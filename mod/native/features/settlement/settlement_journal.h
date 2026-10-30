#pragma once
// features/settlement/settlement_journal: what the authority did with each order, written before the universe is touched
// (M5-31; docs/mod-design.md 12.6 step 4).
//
// A settlement mutates the running universe: a ship changes owner, cargo moves. If the game dies between the first line and
// the confirm, the server is left holding escrow for a trade it cannot see the end of - it asks TradeQuery after a reconnect.
// The journal is the answer: one small sidecar file next to x4mp.json, written with a temp-file-and-rename so a crash leaves
// either the old file or the new one, never half of one.
//
// An entry is the OUTCOME, not the plan: state, the line that failed, whether the undo ran, and the error text. A line list
// lives in the order frame, and the order is not replayed after a restart - replaying an owner change on a universe that may
// already carry it is worse than telling the server the truth ("restart during settlement").
//
// The journal never throws and never blocks the feature: an unreadable file is logged, the feature keeps running, and every
// later order is still answered (an unknown trade id answers ok=false with "no record").

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

#include "features/settlement/settlement_wire.h"

namespace x4mp::features::settlement {

// The states a trade can be in from the authority's side. `Applying` is only ever seen in memory: a file that still says
// Applying at load time means the process died mid-apply, and it is promoted to NeedsAdmin.
enum class State : std::uint8_t { Applying = 0, Applied, Compensated, NeedsAdmin, Refused };

[[nodiscard]] const char* state_name(State state) noexcept;
[[nodiscard]] State state_from_name(std::string_view name) noexcept;

struct JournalEntry {
  Id128Value trade_id;
  State state = State::Applying;
  std::int16_t failed_line = -1;
  std::uint32_t lines = 0;      // how many lines the order carried
  std::uint32_t applied = 0;    // how many were applied before the stop
  bool compensated = false;
  std::string error;
  std::uint64_t time_us = 0;
};

class Journal {
 public:
  static constexpr std::size_t kMaxEntries = 32;

  // Reads the file if it exists. A file that does not parse is logged by the caller through `problem()`; the journal stays
  // usable (empty), because a settlement that cannot be recorded must still be refused, not crashed on.
  void load(const std::filesystem::path& path);
  void set_path(std::filesystem::path path) { path_ = std::move(path); }
  [[nodiscard]] const std::filesystem::path& path() const noexcept { return path_; }
  [[nodiscard]] const std::string& problem() const noexcept { return problem_; }

  // Appends or replaces the entry for its trade id, then writes the file. A write failure is reported through `problem()`
  // and the in-memory entry is kept: the confirm still goes out, and the next write tries again.
  void record(const JournalEntry& entry);

  [[nodiscard]] const JournalEntry* find(const Id128Value& trade_id) const noexcept;
  [[nodiscard]] const std::vector<JournalEntry>& entries() const noexcept { return entries_; }

 private:
  void save();

  std::filesystem::path path_;
  std::string problem_;
  std::vector<JournalEntry> entries_;
};

}  // namespace x4mp::features::settlement
