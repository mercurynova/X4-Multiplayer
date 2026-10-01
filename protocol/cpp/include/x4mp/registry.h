// Message catalog + payload verification (task M0-07, ADR-041). Counterpart of C# MessageRegistry.
//
// Per ADR-041 the C++ side ALWAYS runs the FlatBuffers Verifier on every payload before reading it.
// (The C# runtime's Verifier is unusable for unions and above 32 KB; the C++ one has neither bug.)
// The generated verifier recurses into union members, so the four union messages (Intent, GameEvent,
// AdminCommand, WorldCatchUp) are verified end to end; after verification, generated accessors may
// read any field without further bounds checks.
//
// This header does not include generated code. To read a verified payload include the generated header
// of the table and call flatbuffers::GetRoot<X4MP::Proto::Ping>(payload.data()).
#pragma once

#include <cstddef>
#include <cstdint>
#include <span>

#include "x4mp/wire.h"

namespace x4mp::wire {

/// msg_type 0x0403 (DamageReport) is reserved: it has no table yet and is never reused.
inline constexpr std::uint16_t kDamageReportType = 0x0403;

struct MessageDescriptor {
  std::uint16_t type;
  const char* name;  ///< table name, e.g. "Ping"
  Lane lane;         ///< catalog lane (protocol.md section 20); the frame header lane must equal it
  /// Runs the generated FlatBuffers Verifier for this message's root table.
  bool (*verify)(ByteSpan payload) noexcept;
};

/// The whole catalog, ascending by type.
[[nodiscard]] std::span<const MessageDescriptor> message_catalog() noexcept;

/// Descriptor for a catalogued type, or nullptr (unknown or reserved).
[[nodiscard]] const MessageDescriptor* find_message(std::uint16_t type) noexcept;

/// Descriptor or UnknownMessageType / ReservedMessageType.
[[nodiscard]] Result<const MessageDescriptor*> lookup_message(std::uint16_t type) noexcept;

/// Verifies a payload for `type` with the FlatBuffers Verifier. ZeroLengthFrame for an empty payload,
/// FrameTooLarge over `max_frame_bytes`, Unknown/ReservedMessageType, MalformedPayload on failure.
[[nodiscard]] Result<void> verify_payload(std::uint16_t type, ByteSpan payload,
                                          std::uint32_t max_frame_bytes = kDefaultMaxFrameBytes) noexcept;

/// Full frame validation: catalog lookup, header lane == catalog lane (LaneMismatch), then
/// verify_payload. On success returns the descriptor. Order matches C# MessageRegistry.Decode(Frame).
[[nodiscard]] Result<const MessageDescriptor*> validate_frame(const FrameView& frame) noexcept;

}  // namespace x4mp::wire
