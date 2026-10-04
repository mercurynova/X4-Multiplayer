#include "features/avatars/avatar_wire.h"

#include <flatbuffers/flatbuffers.h>

#include "x4mp/wire.h"

#include "common_generated.h"
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

std::vector<std::uint8_t> encode_controller_change(std::uint32_t net_id, std::uint16_t player) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto off = P::CreateEntityChange(fbb, /*journal_seq*/ 0, net_id, P::ChangeField::Controller, 0, 0, 0, 0, 0, 0, P::EntityKind::Unknown, player);
  fbb.Finish(off);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

}  // namespace x4mp::features::avatars
