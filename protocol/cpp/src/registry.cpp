#include "x4mp/registry.h"

#include <array>

#include "admin_generated.h"
#include "common_generated.h"
#include "control_generated.h"
#include "economy_generated.h"
#include "events_generated.h"
#include "flatbuffers/flatbuffers.h"
#include "message_ids_generated.h"
#include "session_generated.h"
#include "teams_generated.h"
#include "world_generated.h"

namespace x4mp::wire {
namespace {

template <class Table>
bool verify_table(ByteSpan payload) noexcept {
  flatbuffers::Verifier::Options options;  // defaults: depth 64, 1M tables, alignment checked
  flatbuffers::Verifier verifier(payload.data(), payload.size(), options);
  return verifier.VerifyBuffer<Table>(nullptr);
}

#define X4MP_MESSAGE(Name, Id, LaneName) \
  MessageDescriptor{Id, #Name, Lane::LaneName, &verify_table<X4MP::Proto::Name>},

constexpr std::array kCatalog = {
#include "message_table.inc"
};

#undef X4MP_MESSAGE

}  // namespace

std::span<const MessageDescriptor> message_catalog() noexcept { return kCatalog; }

const MessageDescriptor* find_message(std::uint16_t type) noexcept {
  std::size_t lo = 0, hi = kCatalog.size();
  while (lo < hi) {
    const std::size_t mid = lo + (hi - lo) / 2;
    if (kCatalog[mid].type < type) lo = mid + 1;
    else hi = mid;
  }
  return lo < kCatalog.size() && kCatalog[lo].type == type ? &kCatalog[lo] : nullptr;
}

Result<const MessageDescriptor*> lookup_message(std::uint16_t type) noexcept {
  if (const MessageDescriptor* d = find_message(type)) return d;
  return std::unexpected(type == kDamageReportType ? ViolationCode::ReservedMessageType
                                                   : ViolationCode::UnknownMessageType);
}

Result<void> verify_payload(std::uint16_t type, ByteSpan payload, std::uint32_t max_frame_bytes) noexcept {
  auto d = lookup_message(type);
  if (!d) return std::unexpected(d.error());
  if (payload.empty()) return std::unexpected(ViolationCode::ZeroLengthFrame);
  if (payload.size() > max_frame_bytes) return std::unexpected(ViolationCode::FrameTooLarge);
  if (!(*d)->verify(payload)) return std::unexpected(ViolationCode::MalformedPayload);
  return {};
}

Result<const MessageDescriptor*> validate_frame(const FrameView& frame) noexcept {
  auto d = lookup_message(frame.header.type);
  if (!d) return std::unexpected(d.error());
  if (frame.header.lane != (*d)->lane) return std::unexpected(ViolationCode::LaneMismatch);
  auto v = verify_payload(frame.header.type, frame.payload);
  if (!v) return std::unexpected(v.error());
  return *d;
}

}  // namespace x4mp::wire
