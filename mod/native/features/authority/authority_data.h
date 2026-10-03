#pragma once
// features/authority/authority_data: the pure (SDK-free, session-free) helpers of the in-game authority (M2-09). Catch2 tests them
// directly (tests/test_authority_flow.cpp).
//
//   MdCollector      what md/x4mp_galaxy.xml sends (through Lua) about the galaxy and the player's ship
//   build_plan()     collector -> GalaxyMetadata sectors/links + string table + self-spawn references
//   SaveFileWatcher  "the save file is complete": exists, size stable, and openable for reading (session 2 C4: the MD game_saved
//                    event fires ~30 ms after SaveGame, long before the file is finished)
//   save ledger      the x4mp_ckpt_* saves THIS mod created; the janitor removes only those, keeping the newest two
//   AuthorityState   what the authority keeps in the stash across the extension reloads of one game run
//
// MD -> Lua -> native text format of `x4mp.auth_md` {"v":1,"data":"<message>"} (messages are independent, any order):
//   G;<sector>;<sector>;...   a chunk of sectors, each  macro|cluster_macro|name|owner_faction|x|y|z|gate_dest_macro,gate_dest_macro
//   E;<count>                  end marker: <count> sectors were sent in total
//   P;macro|name|idcode|sector_macro|class|owner      the player's ship (class: ship_xs ship_s ship_m ship_l ship_xl or empty)
//   N;                         the ship-only answer (control "ship", close-out A item 3): MD has no player ship right now
// Fields never contain '|' or ';' (MD cannot escape them; a record with the wrong field count is dropped and counted).

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/authority/checkpoint_messages.h"
#include "core/authority/entity_spawn.h"

namespace x4mp::features::auth {

struct SectorRec {
  std::string macro, cluster, name, owner;
  float x = 0, y = 0, z = 0;
  std::vector<std::string> gates;  // destination sector macros
};

struct ShipRec {
  std::string macro, name, idcode, sector, cls, owner;
};

class MdCollector {
 public:
  void reset();
  // false: not a valid message (it changed nothing).
  bool add(std::string_view data);
  // The end marker arrived and the sector count matches what was announced.
  [[nodiscard]] bool complete() const noexcept { return end_seen_ && sectors_.size() == expected_; }
  [[nodiscard]] bool end_seen() const noexcept { return end_seen_; }
  [[nodiscard]] std::size_t expected() const noexcept { return expected_; }
  [[nodiscard]] const std::vector<SectorRec>& sectors() const noexcept { return sectors_; }
  [[nodiscard]] const std::optional<ShipRec>& ship() const noexcept { return ship_; }
  [[nodiscard]] bool no_ship_seen() const noexcept { return no_ship_; }  // an "N;" message arrived
  [[nodiscard]] std::size_t dropped_records() const noexcept { return dropped_; }

 private:
  std::vector<SectorRec> sectors_;
  std::optional<ShipRec> ship_;
  bool no_ship_ = false;
  std::size_t expected_ = 0;
  bool end_seen_ = false;
  std::size_t dropped_ = 0;
};

struct GalaxyPlan {
  std::vector<x4mp::authority::SectorDesc> sectors;  // index 1.., owner_ref = string index of the owner faction (0 none)
  std::vector<x4mp::authority::LinkDesc> links;      // one entry per unordered pair
  std::vector<x4mp::authority::StringDesc> strings;  // factions + the ship macro; indices are stable per plan
  std::uint32_t player_faction_ref = 0;
  std::uint32_t ship_macro_ref = 0;
  std::uint16_t ship_sector = 0;  // sector index of the ship, 0 = unknown
};
// An empty sector list (nothing collected) gives an empty plan; the caller must treat that as "no galaxy".
[[nodiscard]] GalaxyPlan build_plan(const MdCollector& collected);

[[nodiscard]] x4mp::authority::SpawnKind spawn_kind_for_class(std::string_view cls) noexcept;

// ---- save file watcher ---------------------------------------------------------------------------------------------------------
class SaveFileWatcher {
 public:
  using Clock = std::chrono::steady_clock;
  explicit SaveFileWatcher(std::filesystem::path file, std::chrono::milliseconds stable_for = std::chrono::milliseconds(1500));

  // Observes the file at `now` (cheap: one stat, plus one open when it looks stable). True once the file is complete: it exists, is
  // non-empty, has had the same size and modification time for `stable_for`, can be opened for reading and starts with the gzip magic.
  bool poll(Clock::time_point now);
  [[nodiscard]] bool complete() const noexcept { return complete_; }
  [[nodiscard]] std::uintmax_t size() const noexcept { return size_; }
  [[nodiscard]] const std::filesystem::path& file() const noexcept { return file_; }

 private:
  std::filesystem::path file_;
  std::chrono::milliseconds stable_for_;
  bool seen_ = false;
  std::uintmax_t size_ = 0;
  std::filesystem::file_time_type mtime_{};
  Clock::time_point stable_since_{};
  bool complete_ = false;
};

// ---- the ledger of saves this mod made --------------------------------------------------------------------------------------------
// "x4mp_ckpt_" + 16 lowercase hex digits: nothing else is ever created or removed by the janitor below.
[[nodiscard]] bool is_checkpoint_save_name(std::string_view name) noexcept;
[[nodiscard]] std::string checkpoint_save_name(std::uint64_t id);

// Names (oldest first) that exceed `keep`: the ones to remove. Names that are not checkpoint save names are never returned.
[[nodiscard]] std::vector<std::string> names_to_remove(const std::vector<std::string>& oldest_first, std::size_t keep);

[[nodiscard]] std::vector<std::string> load_ledger(const std::filesystem::path& file);  // empty when absent/unreadable; keeps only valid names
[[nodiscard]] bool store_ledger(const std::filesystem::path& file, const std::vector<std::string>& oldest_first);

// Appends `name` to the ledger, removes the checkpoint saves beyond the newest `keep` (their <name>.xml.gz and <name>.xml in
// `save_dir`) and rewrites the ledger. Files are only touched for names that are in the ledger AND are checkpoint save names.
// Returns the names removed.
std::vector<std::string> record_and_trim(const std::filesystem::path& ledger_file, const std::filesystem::path& save_dir,
                                         const std::string& name, std::size_t keep = 2);

// ---- stash state ---------------------------------------------------------------------------------------------------------------------
struct AuthorityState {
  std::uint32_t next_net_id = 1;
  bool spawned = false;
  bool strings_sent = false;           // StringTableAdd already sent in this session
  std::uint32_t string_count = 0;      // highest string index the server holds (a later self-spawn adds its macro after it)
  std::uint64_t checkpoints = 0;       // stored in this session (informational)
  std::vector<std::uint8_t> loaded_sha;  // the save this game runs (a loaded session save, or our newest stored checkpoint); empty = unknown

  [[nodiscard]] std::string to_json() const;
  [[nodiscard]] static AuthorityState from_json(std::string_view text);  // tolerant: bad text -> defaults
};

}  // namespace x4mp::features::auth
