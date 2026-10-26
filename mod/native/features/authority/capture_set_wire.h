#pragma once
// features/authority: the wire half of the capture set (M4-01). Decoding lives here so `capture_set.h` stays free of flatbuffers and the rules
// stay testable without a generated header. Also encodes `SectorComplete`, the marker that closes a sector pass (world.fbs, protocol.md §9.2).

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "features/authority/capture_set.h"

namespace x4mp::features::auth {

// Decode a `CaptureSet` flatbuffer. nullopt on a failed verifier or an empty buffer; `why` then names the reason. The epoch is NOT checked
// here: staleness is CaptureSet::apply()'s decision, so a resend decodes fine and is then refused there.
[[nodiscard]] std::optional<CaptureSetData> decode_capture_set(std::span<const std::uint8_t> payload, std::string* why = nullptr);

// Encode `SectorComplete{sector, epoch, entity_count}`: "every entity of `sector` has been sent as EntitySpawn for this epoch". The authority
// sends it only after a full index pass of that sector (the reference's empty-FULL bug).
[[nodiscard]] std::vector<std::uint8_t> encode_sector_complete(std::uint16_t sector, std::uint32_t epoch, std::uint32_t entity_count);

}  // namespace x4mp::features::auth
