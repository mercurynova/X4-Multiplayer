#include "features/teams/team_plan.h"

#include <algorithm>
#include <cstdio>
#include <set>

#include <flatbuffers/flatbuffers.h>
#include <nlohmann/json.hpp>

#include "control_generated.h"
#include "message_ids_generated.h"
#include "teams_generated.h"

namespace x4mp::features::teams {

namespace P = X4MP::Proto;
using nlohmann::json;

double relation_value(Rel rel) noexcept {
  switch (rel) {
    case Rel::Allied: return kAlliedValue;
    case Rel::Hostile: return kHostileValue;
    default: return 0.0;
  }
}

namespace {
Rel to_rel(P::TeamRelation r) noexcept {
  switch (r) {
    case P::TeamRelation::Allied: return Rel::Allied;
    case P::TeamRelation::Hostile: return Rel::Hostile;
    default: return Rel::Neutral;
  }
}

std::pair<std::uint16_t, std::uint16_t> key(std::uint16_t a, std::uint16_t b) { return a < b ? std::make_pair(a, b) : std::make_pair(b, a); }

std::string fixed2(double v) {
  char buf[32];
  std::snprintf(buf, sizeof(buf), "%.2f", v);
  return buf;
}
}  // namespace

// ---- plan -> calls ---------------------------------------------------------------------------------------------------------------------

std::vector<Call> build_calls(const Plan& plan) {
  std::vector<Call> out;
  if (plan.slots.empty()) return out;
  out.reserve(plan.slots.size() * 4 + plan.relations.size() * 2);
  for (const auto s : plan.slots) out.push_back({Op::Unlock, s, 0, 0.0});  // ADR-016: unlock, set, relock; only team factions, never player
  for (const auto s : plan.slots) {
    out.push_back({Op::Activate, s, 0, 0.0});
    out.push_back({Op::Known, s, 0, 0.0});
  }
  for (const auto& r : plan.relations) {
    out.push_back({Op::SetRelation, r.a, r.b, r.value});
    out.push_back({Op::SetRelation, r.b, r.a, r.value});
  }
  for (const auto s : plan.slots) out.push_back({Op::Lock, s, 0, 0.0});
  return out;
}

std::string call_text(const Call& c) {
  switch (c.op) {
    case Op::Unlock: return "unlock " + std::to_string(c.a);
    case Op::Activate: return "activate " + std::to_string(c.a);
    case Op::Known: return "known " + std::to_string(c.a);
    case Op::SetRelation: return "relation " + std::to_string(c.a) + " " + std::to_string(c.b) + " " + fixed2(c.value);
    case Op::Lock: return "lock " + std::to_string(c.a);
  }
  return {};
}

std::string plan_json(const Plan& plan, std::uint32_t seq, std::string_view reason, bool skip_known) {
  json j;
  j["v"] = 1;
  j["seq"] = seq;
  j["reason"] = std::string(reason);
  j["own"] = plan.own_slot;
  if (skip_known) j["skip_known"] = true;
  j["slots"] = json::array();
  for (const auto s : plan.slots) j["slots"].push_back(static_cast<int>(s));
  j["rel"] = json::array();
  for (const auto& r : plan.relations) j["rel"].push_back(json::array({static_cast<int>(r.a), static_cast<int>(r.b), r.value}));
  return j.dump();
}

// ---- model -----------------------------------------------------------------------------------------------------------------------------

void TeamModel::reset() {
  teams_.clear();
  matrix_.clear();
  default_ = Rel::Neutral;
  own_team_ = 0;
  baseline_ = false;
}

std::uint8_t TeamModel::slot_of_team(std::uint16_t team_id) const {
  const auto it = teams_.find(team_id);
  return it == teams_.end() ? 0 : it->second.slot;
}

Rel TeamModel::relation(std::uint16_t team_a, std::uint16_t team_b) const {
  const auto it = matrix_.find(key(team_a, team_b));
  return it == matrix_.end() ? default_ : it->second;
}

namespace {
void apply_table(const P::TeamTable* t, std::map<std::uint16_t, TeamEntry>& teams) {
  if (t == nullptr) return;
  if (t->full()) teams.clear();
  if (t->teams() != nullptr) {
    for (const auto* info : *t->teams()) {
      TeamEntry e;
      e.id = info->team_id();
      e.slot = info->faction_slot();
      if (e.slot > kMaxSlots) e.slot = 0;
      if (info->name() != nullptr) e.name = info->name()->str();
      e.color_rgb = info->color_rgb();
      if (info->members() != nullptr) {
        for (const auto* m : *info->members()) e.members.push_back(m->player_id());
      }
      teams[e.id] = std::move(e);
    }
  }
  if (t->removed() != nullptr) {
    for (const auto id : *t->removed()) teams.erase(id);
  }
}

void apply_matrix(const P::TeamRelations* r, std::map<std::pair<std::uint16_t, std::uint16_t>, Rel>& matrix, Rel& def) {
  if (r == nullptr) return;
  if (r->full()) matrix.clear();
  def = to_rel(r->default_relation());
  if (r->entries() != nullptr) {
    for (const auto* e : *r->entries()) matrix[key(e->team_a(), e->team_b())] = to_rel(e->relation());
  }
}
}  // namespace

bool TeamModel::apply_welcome(std::span<const std::uint8_t> payload) {
  if (payload.size() < 8) return false;
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<P::Welcome>(nullptr)) return false;
  const auto* w = flatbuffers::GetRoot<P::Welcome>(payload.data());
  const std::uint16_t self = w->player_id();
  teams_.clear();
  matrix_.clear();
  default_ = Rel::Neutral;
  apply_table(w->teams(), teams_);
  apply_matrix(w->relations(), matrix_, default_);
  own_team_ = w->team_id();
  for (const auto& [id, t] : teams_) {  // the table is the better source when it lists the members
    if (std::find(t.members.begin(), t.members.end(), self) != t.members.end()) own_team_ = id;
  }
  baseline_ = true;
  return true;
}

bool TeamModel::apply_frame(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id) {
  if (payload.size() < 8) return false;
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (type == static_cast<std::uint16_t>(P::MsgType::TeamTable)) {
    if (!v.VerifyBuffer<P::TeamTable>(nullptr)) return false;
    apply_table(flatbuffers::GetRoot<P::TeamTable>(payload.data()), teams_);
    for (const auto& [id, t] : teams_) {
      if (self_id != 0 && std::find(t.members.begin(), t.members.end(), self_id) != t.members.end()) own_team_ = id;
    }
    baseline_ = true;
    return true;
  }
  if (type == static_cast<std::uint16_t>(P::MsgType::TeamRelations)) {
    if (!v.VerifyBuffer<P::TeamRelations>(nullptr)) return false;
    apply_matrix(flatbuffers::GetRoot<P::TeamRelations>(payload.data()), matrix_, default_);
    baseline_ = true;
    return true;
  }
  if (type == static_cast<std::uint16_t>(P::MsgType::TeamMemberChanged)) {
    if (!v.VerifyBuffer<P::TeamMemberChanged>(nullptr)) return false;
    const auto* c = flatbuffers::GetRoot<P::TeamMemberChanged>(payload.data());
    if (self_id != 0 && c->player_id() == self_id && own_team_ != c->to_team()) {
      own_team_ = c->to_team();
      return true;
    }
  }
  return false;
}

Plan TeamModel::plan(double own_value) const {
  Plan p;
  std::set<std::uint8_t> slots;
  std::map<std::uint8_t, std::uint16_t> team_of_slot;
  for (const auto& [id, t] : teams_) {
    if (t.slot < 1 || t.slot > kMaxSlots) continue;
    slots.insert(t.slot);
    team_of_slot[t.slot] = id;
  }
  p.slots.assign(slots.begin(), slots.end());
  for (auto a = slots.begin(); a != slots.end(); ++a) {
    for (auto b = std::next(a); b != slots.end(); ++b) {
      p.relations.push_back({*a, *b, relation_value(relation(team_of_slot[*a], team_of_slot[*b]))});
    }
  }
  const std::uint8_t own = slot_of_team(own_team_);
  if (own >= 1 && own <= kMaxSlots && slots.count(own) != 0) {
    p.own_slot = own;
    for (const auto s : slots) {
      p.relations.push_back({0, s, s == own ? own_value : relation_value(relation(own_team_, team_of_slot[s]))});
    }
  }
  return p;
}

}  // namespace x4mp::features::teams
