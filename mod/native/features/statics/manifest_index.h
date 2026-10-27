#pragma once
// features/statics: the client-side index of the checkpoint manifest (M4-17; docs/m4-plan.md W4, docs/protocol.md 8.4).
//
// The manifest is the single source of the static world's net ids (ADR-009): the authority uploads an X4MF file next to every checkpoint save,
// the client downloads it with the save (core/session/save_download.h TransferKind::Manifest) and matches its OWN local objects to the rows.
// A row is found by the same key the on-foot flow already uses for a station container: (sector macro, object macro, position rounded to 50 m)
// - so both sides derive the key with the same function (onfoot::station_match_key) and can never drift apart. The owner and the id code only
// break a tie: two identical station macros at the same 50 m bucket do not happen in a real save, but the save format does not forbid it.
//
//   bytes  <- the .x4mf file the avatars feature already knows the path of (avatar_hub().manifest_file())
//   load()  -> VerifyManifestBuffer, then strings (macro/owner refs), sectors (index -> macro) and entries
//   lookup() -> a row for a match key, or nullptr
//
// Pure logic: FlatBuffers only, no SDK, no game calls, no file IO (the caller reads the file). Everything is built once at on_universe_ready.

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace x4mp::features::statics {

// One ManifestEntry as the client needs it: the net id to bind, and the match key it is filed under.
struct StaticRow {
  std::uint32_t net_id = 0;
  std::uint16_t kind = 0;         // EntityKind
  std::uint64_t component_id = 0; // the authority's UniverseID: diagnostic only, never a key
  std::uint16_t sector = 0;       // wire sector index
  std::uint16_t origin = 0;       // EntityOrigin
  std::uint16_t owner_team = 0;
  std::uint16_t owner_player = 0;
  std::string sector_macro;
  std::string macro;
  std::string owner;
  std::string idcode;
  double x = 0, y = 0, z = 0;
  std::string key;  // onfoot::station_match_key(sector_macro, macro, x, y, z)
};

struct ManifestStats {
  std::uint64_t entries = 0;      // rows in the file
  std::uint64_t indexed = 0;      // rows with a usable key (macro + sector macro resolved)
  std::uint64_t no_macro = 0;     // macro_ref missing from the string table
  std::uint64_t no_sector = 0;    // sector index not in the sectors table
  std::uint64_t collisions = 0;   // two rows with the same key (the first wins; the tie-break is the caller's)
};

class ManifestIndex {
 public:
  // Parses an X4MF buffer. false = not a verifiable manifest (nothing is kept). The index is replaced, never merged.
  bool load(std::span<const std::uint8_t> bytes);

  // The row for a match key, or nullptr. Two rows with one key keep the first unless `idcode` names one of them (M4-18 tie-break: a
  // real save has no such pair; collisions are counted and reported as ambiguous).
  [[nodiscard]] const StaticRow* lookup(std::string_view key, std::string_view idcode = {}) const;

  // True when more than one row carries this key (the caller counts it as an ambiguous match).
  [[nodiscard]] bool ambiguous(std::string_view key) const;

  // Every row whose sector is one of `sectors` (M4-18: match the stations of the interest sectors).
  [[nodiscard]] std::vector<const StaticRow*> in_sectors(std::span<const std::uint16_t> sectors) const;
  
  // Every row whose sector is one of `sectors` with a specific kind filter (M4-18: match the stations of the interest sectors).
  [[nodiscard]] std::vector<const StaticRow*> in_sectors_filtered(std::span<const std::uint16_t> sectors, std::uint16_t kind_filter) const;

  // Get all sector macros present in the manifest
  [[nodiscard]] std::vector<std::string> get_sector_macros() const;
  
  // Get all macro names present in the manifest
  [[nodiscard]] std::vector<std::string> get_macro_list() const;
  
  // Get count of unique sectors in manifest
  [[nodiscard]] std::size_t get_sector_count() const;
  
  // Get count of unique stations in manifest
  [[nodiscard]] std::size_t get_station_count() const;

  [[nodiscard]] bool loaded() const noexcept { return loaded_; }
  [[nodiscard]] std::size_t size() const noexcept { return rows_.size(); }
  [[nodiscard]] const ManifestStats& stats() const noexcept { return stats_; }
  [[nodiscard]] std::string_view checkpoint() const noexcept { return checkpoint_; }  // hex of the Id128, "" when not loaded
  void clear();

 private:
  bool loaded_ = false;
  std::string checkpoint_;
  std::vector<StaticRow> rows_;                              // every indexed row, duplicates included
  std::unordered_map<std::string, std::size_t> by_key_;      // key -> first row with it
  std::unordered_map<std::string, std::size_t> by_key_idcode_;  // key + '\x1f' + idcode -> row (the M4-18 tie-break)
  std::unordered_set<std::string> ambiguous_;                // keys carried by more than one row
  ManifestStats stats_;
};

}  // namespace x4mp::features::statics