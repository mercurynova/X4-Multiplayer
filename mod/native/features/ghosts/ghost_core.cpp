#include "features/ghosts/ghost_core.h"

#include <charconv>
#include <format>

#include "message_ids_generated.h"
#include "session_generated.h"
#include "world_generated.h"
#include "features/teams/team_hub.h"
#include "x4mp/wire.h"

namespace x4mp::features::ghosts {

namespace P = X4MP::Proto;

namespace {
constexpr std::uint8_t kKindFaction = static_cast<std::uint8_t>(P::StringKind::Faction);
constexpr std::uint8_t kKindMacro = static_cast<std::uint8_t>(P::StringKind::Macro);

template <class T>
const T* verified_root(std::span<const std::uint8_t> payload) {
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<T>(nullptr)) return nullptr;
  return flatbuffers::GetRoot<T>(payload.data());
}

std::string str(const flatbuffers::String* s) { return s ? std::string(s->c_str(), s->size()) : std::string(); }

template <class T>
bool parse_num(std::string_view s, T& out) {
  const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
  return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}
}  // namespace

// ---- StringTable -----------------------------------------------------------------------------------------------------------

void StringTable::set(std::uint32_t index, std::uint8_t kind, std::string value) {
  if (index == 0) return;
  auto& e = map_[index];
  if (e.kind == kind && e.value == value) return;
  e.kind = kind;
  e.value = std::move(value);
  ++version_;
}

const StringTable::Entry* StringTable::find(std::uint32_t index) const noexcept {
  const auto it = map_.find(index);
  return it == map_.end() ? nullptr : &it->second;
}

std::string_view StringTable::value(std::uint32_t index) const noexcept {
  const Entry* e = find(index);
  return e ? std::string_view(e->value) : std::string_view();
}

void StringTable::clear() noexcept {
  map_.clear();
  ++version_;
}

std::string StringTable::to_text() const {
  std::string out = "x4gs 1\n";
  for (const auto& [index, e] : map_) {
    std::string v = e.value;
    for (char& c : v) {
      if (c == '\t' || c == '\n' || c == '\r') c = ' ';
    }
    out += std::format("S\t{}\t{}\t{}\n", index, static_cast<int>(e.kind), v);
  }
  out += std::format("end\t{}\n", map_.size());
  return out;
}

bool StringTable::from_text(std::string_view text) {
  std::map<std::uint32_t, Entry> parsed;
  std::size_t pos = 0;
  bool header = false, ended = false;
  std::size_t declared = 0;
  while (pos < text.size()) {
    auto nl = text.find('\n', pos);
    if (nl == std::string_view::npos) nl = text.size();
    const std::string_view line = text.substr(pos, nl - pos);
    pos = nl + 1;
    if (line.empty()) continue;
    if (!header) {
      if (line != "x4gs 1") return false;
      header = true;
      continue;
    }
    if (line.rfind("end\t", 0) == 0) {
      if (!parse_num(line.substr(4), declared)) return false;
      ended = true;
      continue;
    }
    if (line.rfind("S\t", 0) != 0) return false;
    const auto t1 = line.find('\t', 2);
    if (t1 == std::string_view::npos) return false;
    const auto t2 = line.find('\t', t1 + 1);
    if (t2 == std::string_view::npos) return false;
    std::uint32_t index = 0;
    int kind = 0;
    if (!parse_num(line.substr(2, t1 - 2), index) || !parse_num(line.substr(t1 + 1, t2 - t1 - 1), kind)) return false;
    parsed[index] = Entry{static_cast<std::uint8_t>(kind), std::string(line.substr(t2 + 1))};
  }
  if (!header || !ended || declared != parsed.size()) return false;
  for (auto& [index, e] : parsed) set(index, e.kind, std::move(e.value));
  return true;
}

// ---- GhostCore -------------------------------------------------------------------------------------------------------------

bool GhostCore::wants(std::uint16_t type) noexcept {
  switch (static_cast<P::MsgType>(type)) {
    case P::MsgType::StringTableAdd:
    case P::MsgType::EntitySpawn:
    case P::MsgType::EntityChange:
    case P::MsgType::EntityDespawn:
    case P::MsgType::Replication: return true;
    default: return false;
  }
}

bool GhostCore::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_player_id, std::int64_t now_server_us) {
  driver.set_self(self_player_id);
  switch (static_cast<P::MsgType>(type)) {
    case P::MsgType::StringTableAdd: on_string_table(payload); return true;
    case P::MsgType::EntitySpawn: on_entity_spawn(payload, now_server_us); return true;
    case P::MsgType::EntityChange: on_entity_change(payload); return true;
    case P::MsgType::EntityDespawn: on_entity_despawn(payload); return true;
    case P::MsgType::Replication: on_replication(payload, now_server_us); return true;
    default: return false;
  }
}

void GhostCore::on_string_table(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::StringTableAdd>(payload);
  if (!m) {
    ++counters.bad_frames;
    return;
  }
  ++counters.strings;
  if (!m->entries()) return;
  for (const auto* e : *m->entries()) {
    if (e && e->value()) strings.set(e->index(), static_cast<std::uint8_t>(e->kind()), str(e->value()));
  }
}

void GhostCore::on_entity_spawn(std::span<const std::uint8_t> payload, std::int64_t now_us) {
  const auto* m = verified_root<P::EntitySpawn>(payload);
  if (!m) {
    ++counters.spawn_bad;
    ++counters.bad_frames;
    return;
  }
  ++counters.spawn_messages;
  if (!m->entities()) return;
  for (const auto* r : *m->entities()) {
    if (!r) continue;
    if (r->origin() != P::EntityOrigin::PlayerShip) {  // M3: players only (M4 brings NPC streaming)
      ++counters.spawn_ignored;
      continue;
    }
    SpawnInfo info;
    info.net_id = r->net_id();
    info.controller = r->controller_player();
    info.player_id = r->controller_player() != 0 ? r->controller_player() : r->owner_player();
    info.team = r->owner_team();
    info.name = str(r->name());
    info.macro = std::string(strings.value(r->macro_ref()));
    info.owner = std::string(strings.value(r->owner_ref()));
    // The canonical team faction (M3-08: slot of the team). A ghost must never be owned by the `player` faction (it would show up as the
    // local player's property): fall back to the team id only when the team model does not know the team yet.
    if (info.team != 0) {
      const std::string canonical = teams::team_hub().faction_of_team(info.team);
      if (!canonical.empty()) info.owner = canonical;
      else if (info.owner.empty() || info.owner == "player") info.owner = "x4mp_team_" + std::to_string(info.team);
    }
    if (info.macro.empty() || info.owner.empty() || info.owner == "player") ++counters.spawn_unresolved;
    std::optional<InitialState> st;
    if (const auto* s = r->state(); s && s->sector() != 0) {
      InitialState i;
      i.sector = s->sector();
      i.flags = s->flags();
      i.pos = {wire::dequantize_position(s->px()), wire::dequantize_position(s->py()), wire::dequantize_position(s->pz())};
      i.rot = {wire::dequantize_rotation(s->yaw()), wire::dequantize_rotation(s->pitch()), wire::dequantize_rotation(s->roll())};
      const bool coarse = (s->flags() & ghost::kVelCoarse) != 0;
      i.vel = {wire::dequantize_velocity(s->vx(), coarse), wire::dequantize_velocity(s->vy(), coarse), wire::dequantize_velocity(s->vz(), coarse)};
      st = i;
    }
    if (driver.on_spawn(info, now_us, st)) ++counters.spawn_player_ships;
    else ++counters.spawn_ignored;
  }
}

void GhostCore::on_entity_change(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::EntityChange>(payload);
  if (!m) {
    ++counters.bad_frames;
    return;
  }
  ++counters.change;
  if (!driver.has(m->net_id())) return;
  const P::ChangeField f = m->fields();
  std::optional<std::string> name, owner;
  std::optional<std::uint16_t> controller, team;
  if ((f & P::ChangeField::Name) != P::ChangeField::NONE) name = str(m->name());
  if ((f & P::ChangeField::Controller) != P::ChangeField::NONE) controller = m->controller_player();
  if ((f & P::ChangeField::Owner) != P::ChangeField::NONE) {
    const std::string_view v = strings.value(m->owner_ref());
    if (!v.empty() && v != "player") owner = std::string(v);
  }
  if ((f & P::ChangeField::OwnerTeam) != P::ChangeField::NONE) {
    team = m->owner_team();
    if (!owner && m->owner_team() != 0) owner = "x4mp_team_" + std::to_string(m->owner_team());
  }
  driver.on_change(m->net_id(), std::move(name), controller, std::move(owner), team);
}

void GhostCore::on_entity_despawn(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::EntityDespawn>(payload);
  if (!m) {
    ++counters.bad_frames;
    return;
  }
  ++counters.despawn;
  if (!m->entries()) return;
  for (const auto* e : *m->entries()) {
    if (!e) continue;
    const DespawnKind k = e->reason() == P::DespawnReason::DockedInside ? DespawnKind::HideOnly : DespawnKind::Remove;
    driver.on_despawn(e->net_id(), k);
  }
}

void GhostCore::on_replication(std::span<const std::uint8_t> payload, std::int64_t now_us) {
  const auto* m = verified_root<P::Replication>(payload);
  if (!m || !m->entries()) {
    ++counters.replication_bad;
    ++counters.bad_frames;
    return;
  }
  ++counters.replication;
  stream_clock.on_message(now_us, m->server_time_us());
  counters.replication_entries += m->entry_count();
  const std::span<const std::uint8_t> entries(m->entries()->data(), m->entries()->size());
  const auto r = driver.streams().ingest(m->server_time_us(), entries, m->entry_count(), now_us - stream_clock.bias_us());  // arrival in the stream's base
  if (!r) ++counters.replication_bad;
}

}  // namespace x4mp::features::ghosts
