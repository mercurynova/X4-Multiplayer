#include "features/authority/capture_set_wire.h"

#include <flatbuffers/flatbuffers.h>

#include "world_generated.h"

namespace x4mp::features::auth {
namespace P = X4MP::Proto;

std::optional<CaptureSetData> decode_capture_set(std::span<const std::uint8_t> payload, std::string* why) {
  const auto fail = [why](const char* reason) -> std::optional<CaptureSetData> {
    if (why != nullptr) *why = reason;
    return std::nullopt;
  };
  if (payload.empty()) return fail("empty payload");
  flatbuffers::Verifier verifier(payload.data(), payload.size());
  if (!verifier.VerifyBuffer<P::CaptureSet>(nullptr)) return fail("not a CaptureSet");

  const auto* set = flatbuffers::GetRoot<P::CaptureSet>(payload.data());
  if (set == nullptr) return fail("no root");

  CaptureSetData out;
  out.epoch = set->epoch();
  if (const auto* sectors = set->sectors()) {
    out.sectors.reserve(sectors->size());
    for (const auto* row : *sectors) {
      if (row == nullptr) continue;
      out.sectors.push_back(CaptureSector{row->sector(), row->rate_hz()});
    }
  }
  if (const auto* focus = set->focus()) {
    out.focus.reserve(focus->size());
    for (const auto* row : *focus) {
      if (row == nullptr) continue;
      CaptureFocus f;
      f.sector = row->sector();
      if (const auto* c = row->center()) {
        f.cx = c->x();
        f.cy = c->y();
        f.cz = c->z();
      }
      f.radius_m = row->radius_m();
      f.rate_hz = row->rate_hz();
      out.focus.push_back(f);
    }
  }
  return out;
}

std::vector<std::uint8_t> encode_sector_complete(std::uint16_t sector, std::uint32_t epoch, std::uint32_t entity_count) {
  flatbuffers::FlatBufferBuilder fbb(64);
  fbb.Finish(P::CreateSectorComplete(fbb, sector, epoch, entity_count));
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}

}  // namespace x4mp::features::auth
