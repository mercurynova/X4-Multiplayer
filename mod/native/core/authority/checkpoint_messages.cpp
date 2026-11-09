#include "core/authority/checkpoint_messages.h"

#include <algorithm>

#include "core/authority/entity_spawn.h"
#include "manifest_generated.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::authority {

namespace P = X4MP::Proto;

namespace {
Payload to_payload(const flatbuffers::FlatBufferBuilder& fbb) {
  return Payload(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::vector<flatbuffers::Offset<P::SectorInfo>> make_sectors(flatbuffers::FlatBufferBuilder& fbb, const std::vector<SectorDesc>& sectors) {
  std::vector<flatbuffers::Offset<P::SectorInfo>> out;
  out.reserve(sectors.size());
  for (const auto& s : sectors) {
    const P::Vec3f pos(s.x, s.y, s.z);
    out.push_back(P::CreateSectorInfoDirect(fbb, s.index, s.macro.c_str(), s.cluster_macro.c_str(), s.name.c_str(), s.owner_ref, &pos));
  }
  return out;
}

std::vector<flatbuffers::Offset<P::StringEntry>> make_strings(flatbuffers::FlatBufferBuilder& fbb, const std::vector<StringDesc>& strings) {
  std::vector<flatbuffers::Offset<P::StringEntry>> out;
  out.reserve(strings.size());
  for (const auto& s : strings) out.push_back(P::CreateStringEntryDirect(fbb, s.index, static_cast<P::StringKind>(s.kind), s.value.c_str()));
  return out;
}
}  // namespace

std::optional<Payload> encode_save_started(std::uint32_t request_id, const session::Id128& checkpoint, double game_time,
                                           std::uint32_t next_net_id) {
  if (!EntitySpawnBuilder::is_valid_game_time(game_time)) return std::nullopt;
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 cp(checkpoint.lo, checkpoint.hi);
  fbb.Finish(P::CreateSaveStarted(fbb, request_id, &cp, game_time, next_net_id));
  return to_payload(fbb);
}

std::optional<Payload> encode_manifest(const session::Id128& checkpoint, double game_time, std::uint32_t next_net_id,
                                       const std::vector<StringDesc>& strings, const std::vector<SectorDesc>& sectors,
                                       const std::vector<ManifestAvatar>& avatars, const std::vector<ManifestStatic>& statics) {
  if (!EntitySpawnBuilder::is_valid_game_time(game_time)) return std::nullopt;
  flatbuffers::FlatBufferBuilder fbb(1024);
  const P::Id128 cp(checkpoint.lo, checkpoint.hi);
  const auto str = make_strings(fbb, strings);
  const auto sec = make_sectors(fbb, sectors);
  std::vector<flatbuffers::Offset<P::ManifestEntry>> entries;
  // Stations (origin Manifest) and avatars (origin PlayerShip) share one list so the entries come out sorted by net_id.
  struct Pending {
    std::uint32_t net_id = 0;
    std::uint8_t kind = 0;
    std::uint64_t component_id = 0;
    std::uint32_t macro_ref = 0;
    std::uint32_t owner_ref = 0;
    std::uint16_t owner_team = 0;
    std::uint16_t owner_player = 0;
    std::uint16_t sector = 0;
    const std::string* idcode = nullptr;
    float x = 0, y = 0, z = 0;
    std::uint16_t controller_player = 0;
    P::EntityOrigin origin = P::EntityOrigin::Manifest;
  };
  std::vector<Pending> sorted;
  sorted.reserve(avatars.size() + statics.size());
  for (const auto& a : avatars) {
    sorted.push_back(Pending{a.net_id, a.kind, 0, a.macro_ref, a.owner_ref, a.owner_team, a.owner_player, a.sector, &a.idcode,
                             a.x, a.y, a.z, a.controller_player, P::EntityOrigin::PlayerShip});
  }
  for (const auto& s : statics) {
    sorted.push_back(Pending{s.net_id, s.kind, s.component_id, s.macro_ref, s.owner_ref, 0, 0, s.sector, &s.idcode,
                             s.x, s.y, s.z, 0, P::EntityOrigin::Manifest});
  }
  std::stable_sort(sorted.begin(), sorted.end(), [](const Pending& a, const Pending& b) { return a.net_id < b.net_id; });  // entries are sorted by net_id
  for (const auto& e : sorted) {
    const P::Vec3f pos(e.x, e.y, e.z);
    entries.push_back(P::CreateManifestEntryDirect(fbb, e.net_id, static_cast<P::EntityKind>(e.kind), e.component_id, e.macro_ref,
                                                   e.owner_ref, e.owner_team, e.owner_player, e.sector, e.idcode->c_str(), &pos, 0,
                                                   e.origin, e.controller_player));
  }
  P::FinishManifestBuffer(fbb, P::CreateManifestDirect(fbb, &cp, game_time, next_net_id, &str, &sec, &entries));
  return to_payload(fbb);
}

Payload encode_galaxy_metadata(std::span<const std::uint8_t> save_sha256, const std::vector<SectorDesc>& sectors,
                               const std::vector<LinkDesc>& links) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  const std::vector<std::uint8_t> sha(save_sha256.begin(), save_sha256.end());
  const auto sec = make_sectors(fbb, sectors);
  std::vector<flatbuffers::Offset<P::SectorLink>> lk;
  lk.reserve(links.size() * 2);
  const P::Vec3f zero(0, 0, 0);
  for (const auto& l : links) {  // gates are two-way: the server mirrors a link seen in one direction, sending both is what FakeNode does
    lk.push_back(P::CreateSectorLink(fbb, l.from, l.to, P::LinkKind::Gate, &zero, &zero));
    lk.push_back(P::CreateSectorLink(fbb, l.to, l.from, P::LinkKind::Gate, &zero, &zero));
  }
  fbb.Finish(P::CreateGalaxyMetadataDirect(fbb, &sha, &sec, &lk));
  return to_payload(fbb);
}

Payload encode_string_table_add(const std::vector<StringDesc>& strings) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  const auto str = make_strings(fbb, strings);
  fbb.Finish(P::CreateStringTableAddDirect(fbb, &str));
  return to_payload(fbb);
}

std::optional<RequestSaveInfo> parse_request_save(std::span<const std::uint8_t> payload) {
  if (payload.empty()) return std::nullopt;
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<P::RequestSave>(nullptr)) return std::nullopt;
  const auto* r = flatbuffers::GetRoot<P::RequestSave>(payload.data());
  RequestSaveInfo info;
  info.request_id = r->request_id();
  info.reason = static_cast<std::uint8_t>(r->reason());
  if (r->slot_name() != nullptr) info.slot_name = r->slot_name()->str();
  return info;
}

}  // namespace x4mp::authority
