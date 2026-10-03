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
                                       const std::vector<ManifestAvatar>& avatars) {
  if (!EntitySpawnBuilder::is_valid_game_time(game_time)) return std::nullopt;
  flatbuffers::FlatBufferBuilder fbb(1024);
  const P::Id128 cp(checkpoint.lo, checkpoint.hi);
  const auto str = make_strings(fbb, strings);
  const auto sec = make_sectors(fbb, sectors);
  std::vector<flatbuffers::Offset<P::ManifestEntry>> entries;
  std::vector<const ManifestAvatar*> sorted;
  for (const auto& a : avatars) sorted.push_back(&a);
  std::sort(sorted.begin(), sorted.end(), [](const ManifestAvatar* a, const ManifestAvatar* b) { return a->net_id < b->net_id; });  // entries are sorted by net_id
  for (const auto* a : sorted) {
    const P::Vec3f pos(a->x, a->y, a->z);
    entries.push_back(P::CreateManifestEntryDirect(fbb, a->net_id, static_cast<P::EntityKind>(a->kind), 0, a->macro_ref, a->owner_ref, a->owner_team,
                                                   a->owner_player, a->sector, a->idcode.c_str(), &pos, 0, P::EntityOrigin::PlayerShip,
                                                   a->controller_player));
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
