#include "core/authority/map_messages.h"

#include <cstring>

#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::authority {

namespace P = X4MP::Proto;

namespace {
Payload to_payload(const flatbuffers::FlatBufferBuilder& fbb) {
  return Payload(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::vector<flatbuffers::Offset<P::MapSector>> make_sectors(flatbuffers::FlatBufferBuilder& fbb,
                                                            const std::vector<MapSectorDesc>& sectors) {
  std::vector<flatbuffers::Offset<P::MapSector>> out;
  out.reserve(sectors.size());
  for (const auto& s : sectors) {
    const P::Vec3f pos(s.x, s.y, s.z);
    out.push_back(P::CreateMapSectorDirect(fbb, s.index, s.macro.c_str(), s.cluster_macro.c_str(), s.name.c_str(), s.owner.c_str(), &pos));
  }
  return out;
}

std::vector<flatbuffers::Offset<P::MapLink>> make_links(flatbuffers::FlatBufferBuilder& fbb,
                                                        const std::vector<MapLinkDesc>& links) {
  std::vector<flatbuffers::Offset<P::MapLink>> out;
  out.reserve(links.size());
  for (const auto& l : links) out.push_back(P::CreateMapLink(fbb, l.from, l.to, static_cast<P::LinkKind>(l.kind)));
  return out;
}

void hash_bytes(std::uint64_t& h, const void* data, std::size_t size) noexcept {
  const auto* bytes = static_cast<const std::uint8_t*>(data);
  for (std::size_t i = 0; i < size; ++i) {
    h ^= bytes[i];
    h *= 1099511628211ull;
  }
}

void hash_string(std::uint64_t& h, const std::string& value) noexcept {
  const auto size = static_cast<std::uint32_t>(value.size());
  hash_bytes(h, &size, sizeof(size));
  hash_bytes(h, value.data(), value.size());
}
}  // namespace

Payload encode_known_sectors(std::uint64_t revision, const MapPlan& map) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  const auto sectors = make_sectors(fbb, map.sectors);
  const auto links = make_links(fbb, map.links);
  fbb.Finish(P::CreateKnownSectorsDirect(fbb, revision, &sectors, &links));
  return to_payload(fbb);
}

Payload encode_map_snapshot(std::uint64_t version, const MapPlan& map) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  const auto sectors = make_sectors(fbb, map.sectors);
  const auto links = make_links(fbb, map.links);
  fbb.Finish(P::CreateMapSnapshotDirect(fbb, version, &sectors, &links));
  return to_payload(fbb);
}

Payload encode_map_delta(std::uint64_t version, const std::vector<MapSectorDesc>& upserts,
                         const std::vector<MapLinkDesc>& links, std::span<const std::uint16_t> removed) {
  flatbuffers::FlatBufferBuilder fbb(512);
  const auto sectors = make_sectors(fbb, upserts);
  const auto map_links = make_links(fbb, links);
  std::vector<std::uint16_t> removed_sectors(removed.begin(), removed.end());
  fbb.Finish(P::CreateMapDeltaDirect(fbb, version, &sectors, &map_links, &removed_sectors));
  return to_payload(fbb);
}

std::uint64_t map_hash(const MapPlan& map) noexcept {
  std::uint64_t h = 1469598103934665603ull;
  const auto sector_count = static_cast<std::uint32_t>(map.sectors.size());
  hash_bytes(h, &sector_count, sizeof(sector_count));
  for (const auto& s : map.sectors) {
    hash_bytes(h, &s.index, sizeof(s.index));
    hash_string(h, s.macro);
    hash_string(h, s.cluster_macro);
    hash_string(h, s.name);
    hash_string(h, s.owner);
    hash_bytes(h, &s.x, sizeof(s.x));
    hash_bytes(h, &s.y, sizeof(s.y));
    hash_bytes(h, &s.z, sizeof(s.z));
  }
  const auto link_count = static_cast<std::uint32_t>(map.links.size());
  hash_bytes(h, &link_count, sizeof(link_count));
  for (const auto& l : map.links) {
    hash_bytes(h, &l.from, sizeof(l.from));
    hash_bytes(h, &l.to, sizeof(l.to));
    hash_bytes(h, &l.kind, sizeof(l.kind));
  }
  return h;
}

}  // namespace x4mp::authority
