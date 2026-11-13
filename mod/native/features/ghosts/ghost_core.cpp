#include "features/ghosts/ghost_core.h"

#include <charconv>
#include <algorithm>
#include <format>
#include <utility>

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
    case P::MsgType::Replication:
    // M4-13/M4-15: the sector marker that unlocks local suppression, and the interest that scopes its sweep.
    case P::MsgType::SectorComplete:
    case P::MsgType::InterestUpdate: return true;
    default: return false;
  }
}

std::vector<SectorClosed> GhostCore::take_sector_closed() { return std::exchange(sector_closed, {}); }

bool GhostCore::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_player_id, std::int64_t now_server_us) {
  driver.set_self(self_player_id);
  switch (static_cast<P::MsgType>(type)) {
    case P::MsgType::StringTableAdd: on_string_table(payload); return true;
    case P::MsgType::EntitySpawn: on_entity_spawn(payload, now_server_us); return true;
    case P::MsgType::EntityChange: on_entity_change(payload); return true;
    case P::MsgType::EntityDespawn: on_entity_despawn(payload); return true;
    case P::MsgType::Replication: on_replication(payload, now_server_us); return true;
    case P::MsgType::SectorComplete: on_sector_complete(payload); return true;
    case P::MsgType::InterestUpdate: on_interest(payload); return true;
    default: return false;
  }
}

// M4-13: "every entity of this sector in your interest has been sent". The feature asks MD for the sector's local ships and suppresses the
// ones the authority does not stream.
void GhostCore::on_sector_complete(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::SectorComplete>(payload);
  if (!m) {
    ++counters.bad_frames;
    return;
  }
  ++counters.sector_complete;
  if (m->sector() == 0) return;
  sector_closed.push_back(SectorClosed{m->sector(), m->epoch(), m->entity_count()});
}

// M4-15: the sectors this client's interest covers; a sector that leaves stops being swept. `full` means the list is the whole set, which is
// what the server sends today; a delta would need merging, so a non-full update is treated as a delta by union (safe: a stale sector is only
// an extra scan).
void GhostCore::on_interest(std::span<const std::uint8_t> payload) {
  const auto* m = verified_root<P::InterestUpdate>(payload);
  if (!m) {
    ++counters.bad_frames;
    return;
  }
  ++counters.interest;
  if (!m->sectors()) return;
  std::vector<std::uint16_t> next;
  next.reserve(m->sectors()->size());
  for (const auto* tier : *m->sectors()) {
    if (tier != nullptr && tier->sector() != 0) next.push_back(tier->sector());
  }
  if (m->full()) interest_ = std::move(next);
  else {
    for (const auto sector : next) {
      if (std::find(interest_.begin(), interest_.end(), sector) == interest_.end()) interest_.push_back(sector);
    }
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
    // M3: player ships. M4-13: the authority's world (NPC ships of a captured sector) arrives as AuthorityRuntime / Manifest and is admitted
    // as an NPC ghost with the entity's real owner faction. Player-built stations and the rest wait for W4 (the manifest).
    const bool player_ship = r->origin() == P::EntityOrigin::PlayerShip;
    const bool npc = r->origin() == P::EntityOrigin::AuthorityRuntime || r->origin() == P::EntityOrigin::Manifest;
    if (!player_ship && !npc) {
      ++counters.spawn_ignored;
      continue;
    }
    SpawnInfo info;
    info.net_id = r->net_id();
    info.npc = npc;
    info.controller = npc ? 0 : r->controller_player();
    info.player_id = npc ? 0 : (r->controller_player() != 0 ? r->controller_player() : r->owner_player());
    info.team = npc ? 0 : r->owner_team();
    info.name = npc ? std::string() : str(r->name());
    info.macro = std::string(strings.value(r->macro_ref()));
    info.owner = std::string(strings.value(r->owner_ref()));
    // M12: paint is a plain string on the record (not a string-table ref) and is only meaningful for player ships.
    if (!npc) info.paint = str(r->paint());
    if (!npc) info.logo = str(r->logo());  // M16: same plain-string rule as paint
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
    if (driver.on_spawn(info, now_us, st)) {
      if (npc) ++counters.spawn_npcs;
      else ++counters.spawn_player_ships;
    } else {
      ++counters.spawn_ignored;
    }
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
  std::optional<std::string> name, owner, paint, logo;
  std::optional<std::uint16_t> controller, team;
  if ((f & P::ChangeField::Name) != P::ChangeField::NONE) name = str(m->name());
  if ((f & P::ChangeField::Paint) != P::ChangeField::NONE) paint = str(m->paint());
  if ((f & P::ChangeField::Logo) != P::ChangeField::NONE) logo = str(m->logo());
  if ((f & P::ChangeField::Controller) != P::ChangeField::NONE) controller = m->controller_player();
  if ((f & P::ChangeField::Owner) != P::ChangeField::NONE) {
    const std::string_view v = strings.value(m->owner_ref());
    if (!v.empty() && v != "player") owner = std::string(v);
  }
  if ((f & P::ChangeField::OwnerTeam) != P::ChangeField::NONE) {
    team = m->owner_team();
    if (!owner && m->owner_team() != 0) owner = "x4mp_team_" + std::to_string(m->owner_team());
  }
  driver.on_change(m->net_id(), std::move(name), controller, std::move(owner), team, std::move(paint), std::move(logo));
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
    const DespawnKind k = e->reason() == P::DespawnReason::DockedInside ? DespawnKind::HideOnly
                           : e->reason() == P::DespawnReason::OutOfInterest ? DespawnKind::OutOfInterest
                                                                           : DespawnKind::Remove;
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
