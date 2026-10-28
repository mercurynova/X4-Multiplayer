#pragma once
// features/economy/stock_apply: the pure part of M5-16, the client's stock apply.
//
// The authority sends the whole hold of a station (EntityCargo). A client compares that snapshot with what its own game says the
// same station holds and applies only the difference through the actions shim - so a repeated snapshot, or one that arrived late,
// writes nothing. No game call here: the caller reads the local cargo and names the remote wares, this header only does the maths.

#include <cstdint>
#include <span>
#include <string>
#include <unordered_map>
#include <vector>

#include "features/economy/economy_hub.h"
#include "game/game_api.h"

namespace x4mp::features::economy {

// A remote snapshot line with its ware macro resolved (the wire only carries ware_ref).
struct NamedLine {
  std::string ware;
  std::int32_t amount = 0;
};

// `local` is what this node's game reports for `component`; `remote` is the authority's snapshot, named. Returns one op per ware
// whose amount differs, in the order of `remote`, and then one op per ware the local hold has and the remote does not (a ware the
// authority dropped to zero). An empty result means "already in step".
[[nodiscard]] inline std::vector<CargoOp> cargo_delta(std::uint64_t component, std::span<const game::CargoPod> local,
                                                     std::span<const NamedLine> remote) {
  std::vector<CargoOp> ops;
  if (component == 0) return ops;
  std::unordered_map<std::string, std::int32_t> have;
  std::unordered_map<std::string, bool> seen;
  have.reserve(local.size());
  for (const game::CargoPod& line : local) {
    if (line.ware.empty()) continue;
    have[line.ware] += line.amount;
  }
  for (const NamedLine& line : remote) {
    if (line.ware.empty()) continue;
    const auto it = have.find(line.ware);
    const std::int32_t mine = it == have.end() ? 0 : it->second;
    seen[line.ware] = true;  // handled here, whatever the delta is
    const std::int32_t delta = line.amount - mine;
    if (delta != 0) ops.push_back(CargoOp{component, line.ware, delta});
  }
  for (const auto& [ware, amount] : have) {
    if (amount == 0 || seen.count(ware) != 0) continue;  // in step, or already reported above
    ops.push_back(CargoOp{component, ware, -amount});
  }
  return ops;
}

}  // namespace x4mp::features::economy
