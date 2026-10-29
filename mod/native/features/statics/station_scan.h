#pragma once
// features/statics: the text a station scan arrives as (M4-18). MD is the only side that can enumerate a sector
// (`find_object class="class.station" space="$Sector" multiple="true"`); it pushes one batch per scan and a count at the end, both over
// the existing Lua bridge, and this file turns that text into rows. Consumer: features/statics/statics_feature.cpp.
//
// Wire text (verb x4mp.statics_scan, field "data"), one message per chunk:
//   S;<sector macro id>;<component>|<station macro>|<x>|<y>|<z>|<idcode>|<owner>;...   one row per station
//   E;<sector macro id>;<count>                                         the scan of that sector is complete
// The position is sector-space metres, the same frame md/x4mp_onfoot.xml reports a station container in, so the client can build the
// very same onfoot::station_match_key the on-foot flow uses. A row without a component, a macro or three numbers is skipped.
// The last two fields are optional: the idcode is the M4-18 tie-break (MD sends it empty today) and the owner is the faction id of
// the station, which M5-17 needs to pay the NPC side of a trade to the right faction.
//
// Pure logic: no SDK, no flatbuffers, no file IO.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::features::statics {

struct StationRow {
  std::uint64_t component = 0;  // X4 object id of the local station
  std::string macro;            // station macro id, e.g. station_arg_defence_macro
  double x = 0, y = 0, z = 0;   // sector-space position in metres
  std::string idcode;           // optional 6th field: breaks a tie between two manifest rows with one key (M4-18)
  std::string owner;            // optional 7th field: the station's owner faction id (M5-17); empty when MD could not read it
};

struct StationBatch {
  std::string sector_macro;      // the sector macro id as MD prints it (native owns the wire numbering)
  std::vector<StationRow> rows;  // the rows of this chunk (empty for the closing count)
  std::uint32_t count = 0;       // 'E' only: how many stations the whole pass listed
  bool end = false;              // true = the closing count of a pass
};

// Parses one scan message. nullopt = not a scan message at all (wrong prefix, empty sector macro, bad count).
[[nodiscard]] std::optional<StationBatch> parse_station_batch(std::string_view data);

}  // namespace x4mp::features::statics
