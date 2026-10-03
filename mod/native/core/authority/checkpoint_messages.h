#pragma once
// core/authority: encoders/decoders for the messages around an authority checkpoint (M2-08). The upload frames themselves
// are in upload_job.h; the self-spawn is in entity_spawn.h. All payloads are complete FlatBuffers buffers, ready for
// Session::send(lane, type, payload).
//
// Order of an authority checkpoint (protocol.md 6.3, FakeAuthoritySaves.cs): StringTableAdd (once), GalaxyMetadata (keyed by
// the save's sha256), SaveStarted (the journal marker; carries game_time), then the uploads: save, manifest.

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "core/session/session.h"

namespace x4mp::authority {

struct SectorDesc {
  std::uint16_t index = 0;  // 1-based, unique
  std::string macro;
  std::string cluster_macro;
  std::string name;
  std::uint32_t owner_ref = 0;
  float x = 0, y = 0, z = 0;  // galaxy position (GUI map)
};
struct LinkDesc {
  std::uint16_t from = 0;
  std::uint16_t to = 0;
};
struct StringDesc {
  std::uint32_t index = 0;
  std::uint8_t kind = 0;  // StringKind
  std::string value;
};

using Payload = std::vector<std::uint8_t>;

// SaveStarted. nullopt when game_time is not finite and > 0 (the server stamps the checkpoint with it).
[[nodiscard]] std::optional<Payload> encode_save_started(std::uint32_t request_id, const session::Id128& checkpoint, double game_time,
                                                         std::uint32_t next_net_id);

// The checkpoint manifest (file identifier "X4MF") with no entities (what an authority whose save has no stations uploads).
// nullopt for an invalid game_time.
[[nodiscard]] std::optional<Payload> encode_manifest(const session::Id128& checkpoint, double game_time, std::uint32_t next_net_id,
                                                     const std::vector<StringDesc>& strings, const std::vector<SectorDesc>& sectors);

[[nodiscard]] Payload encode_galaxy_metadata(std::span<const std::uint8_t> save_sha256, const std::vector<SectorDesc>& sectors,
                                             const std::vector<LinkDesc>& links);
[[nodiscard]] Payload encode_string_table_add(const std::vector<StringDesc>& strings);

struct RequestSaveInfo {
  std::uint32_t request_id = 0;
  std::uint8_t reason = 0;  // SaveReason
  std::string slot_name;
};
[[nodiscard]] std::optional<RequestSaveInfo> parse_request_save(std::span<const std::uint8_t> payload);

}  // namespace x4mp::authority
