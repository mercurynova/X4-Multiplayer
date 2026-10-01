// Generic FlatBuffers table walker used by the golden-vector tests. It walks a payload with the
// FlatBuffers reflection API (so it needs no per-message code) and produces the same canonical JSON that
// the C# golden generator embeds in index.json as each frame vector's "fields"
// (server/tests/X4MP.Protocol.Tests/CanonicalFields.cs):
//   keys = .fbs field names in UpperCamelCase; integers and enums as numbers; bool as bool; float and
//   double as the exact double; string as string; [ubyte] as lower-case hex; vectors as arrays; structs
//   and tables as objects; a union as {"type": n, "value": {...}}; absent vectors/strings/tables/unions
//   are omitted.
// Walking visits every reachable field of every union member, so it doubles as the "full walk" that
// ADR-041 requires for Intent, GameEvent, AdminCommand and WorldCatchUp.
#pragma once

#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include <nlohmann/json.hpp>

#include "flatbuffers/reflection.h"

namespace x4mp::test {

/// The binary schema (x4mp_all.bfbs) for every table in protocol/schema.
class Schema {
 public:
  static Schema load(const std::string& path);
  [[nodiscard]] const reflection::Schema& get() const { return *schema_; }

 private:
  std::vector<std::uint8_t> bytes_;
  const reflection::Schema* schema_ = nullptr;
};

/// Walks a payload whose root table is `table_name` (e.g. "Ping"). Throws std::runtime_error if the
/// table is unknown or the buffer is walked out of bounds (callers verify with the Verifier first).
nlohmann::json dump_message(const reflection::Schema& schema, std::string_view table_name,
                            std::span<const std::uint8_t> payload);

}  // namespace x4mp::test
