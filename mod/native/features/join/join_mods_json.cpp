#include "features/join/join_mods_json.h"

#include <algorithm>
#include <string_view>

#include "control_generated.h"
#include "host/status_json.h"
#include "mods_generated.h"
#include "teams_generated.h"

namespace x4mp::features::join {

namespace P = X4MP::Proto;

namespace {
constexpr std::size_t kMaxText = 256;
constexpr std::size_t kMaxEntries = 100;

std::string cut(const flatbuffers::String* s) {
  if (s == nullptr) return {};
  std::string_view v(s->c_str(), s->size());
  if (v.size() > kMaxText) {
    std::size_t n = kMaxText;
    while (n > 0 && (static_cast<unsigned char>(v[n]) & 0xC0) == 0x80) --n;  // do not end inside a UTF-8 sequence
    v = v.substr(0, n);
  }
  return std::string(v);
}

void put_str(std::string& out, const char* key, const std::string& value) {
  if (value.empty()) return;
  out += ",\"";
  out += key;
  out += "\":\"";
  out += host::json_escape(value);
  out += '"';
}

void put_workshop(std::string& out, std::uint64_t id) {
  if (id == 0) return;
  out += ",\"workshop_id\":";
  out += std::to_string(id);
}

void put_ref_list(std::string& out, const char* key, const flatbuffers::Vector<flatbuffers::Offset<P::ModRef>>* list) {
  out += ",\"";
  out += key;
  out += "\":[";
  const std::size_t total = list ? list->size() : 0;
  for (std::size_t i = 0; i < std::min(total, kMaxEntries); ++i) {
    const P::ModRef* r = list->Get(static_cast<flatbuffers::uoffset_t>(i));
    if (i != 0) out += ',';
    out += "{\"id\":\"" + host::json_escape(cut(r->id())) + "\"";
    put_str(out, "name", cut(r->name()));
    put_str(out, "version", cut(r->version()));
    put_str(out, "have_version", cut(r->have_version()));
    put_str(out, "nexus_url", cut(r->nexus_url()));
    put_workshop(out, r->workshop_id());
    put_str(out, "notes", cut(r->notes()));
    out += '}';
  }
  out += ']';
  if (total > kMaxEntries) {
    out += ",\"";
    out += key;
    out += "_more\":" + std::to_string(total - kMaxEntries);
  }
}

template <typename T>
bool verified(std::span<const std::uint8_t> payload) {
  if (payload.empty()) return false;
  flatbuffers::Verifier v(payload.data(), payload.size());
  return v.VerifyBuffer<T>(nullptr);
}

const char* rule_name(P::ModRule r) {
  switch (r) {
    case P::ModRule::Blocked: return "blocked";
    case P::ModRule::Allowed: return "allowed";
    default: return "required";
  }
}

const char* version_rule_name(P::VersionRule r) {
  switch (r) {
    case P::VersionRule::AtLeast: return "at_least";
    case P::VersionRule::Any: return "any";
    default: return "exact";
  }
}

std::string policy_json(const P::ModPolicy* p) {
  if (p == nullptr) return {};
  std::string out = "{\"v\":1,\"version\":" + std::to_string(p->version());
  out += p->source_mode() == P::ModSourceMode::AdminList ? ",\"source_mode\":\"admin\"" : ",\"source_mode\":\"authority\"";
  switch (p->unknown_default()) {
    case P::UnknownModDefault::Block: out += ",\"unknown_default\":\"block\""; break;
    case P::UnknownModDefault::AllowAll: out += ",\"unknown_default\":\"allow_all\""; break;
    default: out += ",\"unknown_default\":\"client_only\""; break;
  }
  out += p->enforcement() == P::ModEnforcement::Warn ? ",\"enforcement\":\"warn\"" : ",\"enforcement\":\"strict\"";
  out += ",\"entries\":[";
  const auto* entries = p->entries();
  const std::size_t total = entries ? entries->size() : 0;
  for (std::size_t i = 0; i < std::min(total, kMaxEntries); ++i) {
    const P::ModPolicyEntry* e = entries->Get(static_cast<flatbuffers::uoffset_t>(i));
    if (i != 0) out += ',';
    out += "{\"id\":\"" + host::json_escape(cut(e->id())) + "\"";
    put_str(out, "name", cut(e->name()));
    out += std::string(",\"rule\":\"") + rule_name(e->rule()) + "\"";
    out += e->enabled() ? ",\"enabled\":true" : ",\"enabled\":false";
    out += std::string(",\"version_rule\":\"") + version_rule_name(e->version_rule()) + "\"";
    put_str(out, "version", cut(e->version()));
    put_str(out, "nexus_url", cut(e->nexus_url()));
    put_workshop(out, e->workshop_id());
    put_str(out, "notes", cut(e->notes()));
    out += '}';
  }
  out += ']';
  if (total > kMaxEntries) out += ",\"entries_more\":" + std::to_string(total - kMaxEntries);
  out += '}';
  return out;
}
}  // namespace

std::string mod_refusal_json(std::span<const std::uint8_t> payload) {
  if (!verified<P::Disconnect>(payload)) return {};
  const auto* d = flatbuffers::GetRoot<P::Disconnect>(payload.data());
  const auto* v = d->mod_violation();
  if (v == nullptr) return {};
  std::string out = "{\"v\":1,\"policy_version\":" + std::to_string(v->policy_version());
  put_ref_list(out, "install", v->install());
  put_ref_list(out, "enable", v->enable());
  put_ref_list(out, "disable", v->disable());
  put_ref_list(out, "update", v->update());
  out += '}';
  return out;
}

std::string mod_policy_json_from_welcome(std::span<const std::uint8_t> payload) {
  if (!verified<P::Welcome>(payload)) return {};
  const auto* w = flatbuffers::GetRoot<P::Welcome>(payload.data());
  return w->settings() ? policy_json(w->settings()->mod_policy()) : std::string();
}

std::string mod_policy_json_from_changed(std::span<const std::uint8_t> payload) {
  if (!verified<P::ModPolicyChanged>(payload)) return {};
  return policy_json(flatbuffers::GetRoot<P::ModPolicyChanged>(payload.data())->policy());
}

}  // namespace x4mp::features::join
