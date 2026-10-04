#include "features/avatars/avatar_wire.h"

#include <flatbuffers/flatbuffers.h>

#include "x4mp/wire.h"

#include "common_generated.h"
#include "manifest_generated.h"
#include "session_generated.h"
#include "world_generated.h"

namespace x4mp::features::avatars {

namespace P = X4MP::Proto;

namespace {
template <class T>
const T* verified_root(std::span<const std::uint8_t> payload) {
  if (payload.empty()) return nullptr;
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<T>(nullptr)) return nullptr;
  return flatbuffers::GetRoot<T>(payload.data());
}
std::string str_of(const flatbuffers::String* s) { return s ? s->str() : std::string{}; }

Pose pose_of(std::int32_t px, std::int32_t py, std::int32_t pz, std::int16_t yaw, std::int16_t pitch, std::int16_t roll) {
  return {wire::dequantize_position(px), wire::dequantize_position(py), wire::dequantize_position(pz),
          wire::dequantize_rotation(yaw), wire::dequantize_rotation(pitch), wire::dequantize_rotation(roll)};
}
}  // namespace

std::optional<PlayerShipReq> decode_player_ship(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::PlayerShip>(payload);
  if (!m) return std::nullopt;
  PlayerShipReq r;
  r.player_id = m->player_id();
  r.ship_macro = str_of(m->ship_macro());
  r.name = str_of(m->name());
  r.idcode = str_of(m->idcode());
  r.sector = m->sector();
  r.pose = pose_of(m->px(), m->py(), m->pz(), m->yaw(), m->pitch(), m->roll());
  return r;
}

std::optional<PlayerStateIn> decode_player_state(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::PlayerState>(payload);
  if (!m) return std::nullopt;
  PlayerStateIn s;
  s.net_id = m->net_id();
  s.sector = m->sector();
  s.flags = static_cast<std::uint16_t>(m->flags());
  s.sample_time_us = static_cast<std::int64_t>(m->sample_time_us());
  s.pose = pose_of(m->px(), m->py(), m->pz(), m->yaw(), m->pitch(), m->roll());
  s.hull = m->hull();
  s.shield = m->shield();
  return s;
}

std::optional<RosterIn> decode_roster(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::RosterUpdate>(payload);
  if (!m) return std::nullopt;
  RosterIn r;
  r.full = m->full();
  if (m->players()) {
    for (const auto* p : *m->players()) {
      RosterRow row;
      row.id = p->player_id();
      row.name = str_of(p->name());
      row.team = p->team_id();
      row.phase = static_cast<std::uint8_t>(p->phase());
      row.ship_net_id = p->ship_net_id();
      row.online = p->online();
      r.players.push_back(std::move(row));
    }
  }
  if (m->removed()) {
    for (const auto id : *m->removed()) r.removed.push_back(id);
  }
  return r;
}

std::optional<DespawnIn> decode_despawn(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::EntityDespawn>(payload);
  if (!m) return std::nullopt;
  DespawnIn d;
  if (m->entries()) {
    for (const auto* e : *m->entries()) d.entries.emplace_back(e->net_id(), static_cast<std::uint8_t>(e->reason()));
  }
  return d;
}

std::optional<SettingsIn> decode_settings(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::ServerSettingsUpdate>(payload);
  if (!m) return std::nullopt;
  SettingsIn s;
  s.version = m->version();
  if (m->entries()) {
    for (const auto* e : *m->entries()) s.entries.emplace_back(str_of(e->key()), str_of(e->value()));
  }
  return s;
}

std::optional<std::vector<AvatarInfo>> decode_avatar_spawns(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::EntitySpawn>(payload);
  if (!m) return std::nullopt;
  std::vector<AvatarInfo> out;
  if (m->entities()) {
    for (const auto* e : *m->entities()) {
      if (e->origin() != P::EntityOrigin::PlayerShip) continue;
      AvatarInfo a;
      a.net_id = e->net_id();
      a.owner_player = e->owner_player();
      a.controller_player = e->controller_player();
      a.owner_team = e->owner_team();
      a.name = str_of(e->name());
      a.idcode = str_of(e->idcode());
      if (const auto* s = e->state()) {
        a.sector = s->sector();
        a.pose = pose_of(s->px(), s->py(), s->pz(), s->yaw(), s->pitch(), s->roll());
      }
      out.push_back(std::move(a));
    }
  }
  return out;
}

std::optional<std::vector<AvatarInfo>> decode_manifest_avatars(std::span<const std::uint8_t> payload) {
  if (payload.size() < 8) return std::nullopt;
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!P::VerifyManifestBuffer(v)) return std::nullopt;
  const auto* m = P::GetManifest(payload.data());
  if (!m) return std::nullopt;
  std::vector<AvatarInfo> out;
  if (m->entries()) {
    for (const auto* e : *m->entries()) {
      if (e->origin() != P::EntityOrigin::PlayerShip) continue;
      AvatarInfo a;
      a.net_id = e->net_id();
      a.owner_player = e->owner_player();
      a.controller_player = e->controller_player();
      a.owner_team = e->owner_team();
      a.idcode = str_of(e->idcode());
      a.sector = e->sector();
      if (const auto* p = e->position()) a.pose = {p->x(), p->y(), p->z(), 0, 0, 0};
      out.push_back(std::move(a));
    }
  }
  return out;
}

std::vector<std::uint8_t> encode_player_ship(const PlayerShipReq& r, std::uint64_t key_lo, std::uint64_t key_hi, std::uint64_t local_component_id) {
  flatbuffers::FlatBufferBuilder fbb(256);
  const P::Id128 key(key_lo, key_hi);
  const auto q = [](double v) { return wire::quantize_position(v).value_or(0); };
  const auto qr = [](double v) { return wire::quantize_rotation(v).value_or(0); };
  const auto off = P::CreatePlayerShipDirect(fbb, &key, r.ship_macro.c_str(), r.name.c_str(), r.idcode.c_str(), r.sector, q(r.pose.x), q(r.pose.y), q(r.pose.z), qr(r.pose.yaw),
                                             qr(r.pose.pitch), qr(r.pose.roll), 255, 255, local_component_id, 0);
  fbb.Finish(off);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::vector<std::uint8_t> encode_controller_change(std::uint32_t net_id, std::uint16_t player) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto off = P::CreateEntityChange(fbb, /*journal_seq*/ 0, net_id, P::ChangeField::Controller, 0, 0, 0, 0, 0, 0, P::EntityKind::Unknown, player);
  fbb.Finish(off);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::vector<std::uint8_t> encode_owner_change(std::uint32_t net_id, std::uint32_t owner_ref, std::uint16_t team) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto off = P::CreateEntityChange(fbb, /*journal_seq*/ 0, net_id, P::ChangeField::Owner | P::ChangeField::OwnerTeam, owner_ref, team);
  fbb.Finish(off);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

}  // namespace x4mp::features::avatars
