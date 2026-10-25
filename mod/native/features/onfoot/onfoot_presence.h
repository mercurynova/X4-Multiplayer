#pragma once
// features/onfoot: the presence list the HUD draws (M3b Tier 0, ADR-046).
//
//   The relay sends PresenceRoster galaxy-wide, so a node knows that every other player is on foot somewhere. What it cannot do is name
//   the place: a PresenceEntry carries a container net id and nothing else, and a container the local player is not in has no name on
//   this side until the station mirror arrives with M4. So the only place worth drawing is "here", and this file owns the two decisions
//   that go with it - whether a row is here, and whether the roster changed since the last raise - as pure functions the tests can pin.

#include <cstdint>
#include <string>
#include <vector>

#include "features/onfoot/onfoot_hub.h"

namespace x4mp::features::onfoot {

// A row is "here" when the local player is in a container and the peer reports the same one. Seated and in-transit rows are not "here":
// they are in a cockpit or in a lift, not standing next to the player.
[[nodiscard]] bool presence_here(const PresenceRow& row, std::uint32_t local_container) noexcept;

// A compact text of what the HUD would draw: the local container and mode, then every row with its id, mode and container. Equal text
// means an identical line, which is what keeps the bridge quiet while nothing changes. Rebuilt per frame, compared as text.
[[nodiscard]] std::string presence_signature(const std::vector<PresenceRow>& rows, std::uint32_t local_container, Mode local_mode);

}  // namespace x4mp::features::onfoot
