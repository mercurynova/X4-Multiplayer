#pragma once
// features/authority: the text a sector scan arrives as (M4-03). MD is the only side that can enumerate a sector
// (`find_object class="class.ship" space="$Sector" multiple="true"`); it pushes one batch per scan and a count at the end, both over the
// existing Lua bridge, and this file turns that text into rows. Positions never come through here: the streamer reads them natively.
//
// Wire text (verb x4mp.capture_scan, field "data"), one message per chunk:
//   S;<sector macro id>;<component>|<ship macro>|<owner faction>|<mission>;...   one row per ship
//   E;<sector macro id>;<count>                                                the scan of that sector is complete
// A row without a component or a ship macro is skipped. The 4th field is optional (M4-13: the client's suppression scan marks mission objects;
// the authority ignores it). The sector macro id is the *string*, because native owns the wire sector numbering
// (features/selfship/galaxy_map.h) and MD cannot produce it; the flow resolves the string to the u16 index.
//
// Pure logic: no SDK, no flatbuffers.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::features::auth {

struct ScanRow {
  std::uint64_t component = 0;  // X4 object id: the key the pose is read with
  std::string macro;            // ship macro id, e.g. ship_arg_s_fighter_01_a_macro
  std::string owner;            // owner faction id, e.g. argon
  bool mission = false;         // M4-13: the ship belongs to a mission; the client never suppresses it
};

struct ScanBatch {
  std::string sector_macro;      // as MD spells it; resolved to a u16 index by the caller
  std::vector<ScanRow> rows;     // empty for the terminating count
  std::uint32_t count = 0;       // only meaningful when `end` is true
  bool end = false;
};

// Parse one bridge message. nullopt when the text is not a scan batch at all (the caller counts it as a bad report).
[[nodiscard]] std::optional<ScanBatch> parse_scan_batch(std::string_view data);

// The bridge raises every verb as a platform event whose text is a JSON envelope (`{"v":1,"data":"S;..."}`, x4mp_bridge.lua B.send), while
// the authority reads the field directly. A native subscriber that only gets the raw event text must unwrap it first: this returns the
// scan text from either form and hands back anything else unchanged, for parse_scan_batch to reject. A scan never contains a quote or a
// backslash, so the envelope scan is deliberately simple.
[[nodiscard]] std::string_view scan_payload(std::string_view text);

}  // namespace x4mp::features::auth
