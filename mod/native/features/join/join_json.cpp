#include "features/join/join_json.h"

#include <algorithm>
#include <cctype>
#include <charconv>

#include <nlohmann/json.hpp>

#include "core/crypto/crypto.h"

namespace x4mp::features::join {

namespace {
using nlohmann::json;

std::string get_string(const json& obj, const char* key) {
  const auto it = obj.find(key);
  if (it == obj.end() || !it->is_string()) return {};
  return it->get<std::string>();
}

bool get_bool(const json& obj, const char* key, bool fallback = false) {
  const auto it = obj.find(key);
  return (it != obj.end() && it->is_boolean()) ? it->get<bool>() : fallback;
}

bool parse_port(std::string_view digits, std::uint16_t& port) {
  if (digits.empty() || digits.size() > 5) return false;
  unsigned value = 0;
  const auto r = std::from_chars(digits.data(), digits.data() + digits.size(), value);
  if (r.ec != std::errc{} || r.ptr != digits.data() + digits.size() || value < 1 || value > 65535) return false;
  port = static_cast<std::uint16_t>(value);
  return true;
}

bool valid_host_char(char c) { return std::isalnum(static_cast<unsigned char>(c)) != 0 || c == '.' || c == '-' || c == '_'; }
}  // namespace

bool parse_endpoint(std::string_view text, std::string& host, std::uint16_t& port) {
  port = 47780;
  if (text.empty() || text.size() > 255) return false;
  if (text.front() == '[') {
    const auto close = text.find(']');
    if (close == std::string_view::npos || close < 2) return false;
    const std::string_view inner = text.substr(1, close - 1);
    if (inner.find(':') == std::string_view::npos) return false;
    for (const char c : inner) {
      if (!(std::isxdigit(static_cast<unsigned char>(c)) || c == ':' || c == '.')) return false;
    }
    const std::string_view rest = text.substr(close + 1);
    if (!rest.empty()) {
      if (rest.front() != ':' || !parse_port(rest.substr(1), port)) return false;
    }
    host = std::string(inner);
    return true;
  }
  std::string_view name = text;
  const auto colon = text.rfind(':');
  if (colon != std::string_view::npos) {
    if (!parse_port(text.substr(colon + 1), port)) return false;
    name = text.substr(0, colon);
  }
  if (name.empty() || !std::all_of(name.begin(), name.end(), valid_host_char)) return false;
  host = std::string(name);
  return true;
}

std::optional<JoinRequest> parse_join(std::string_view text, std::string* error) {
  const auto fail = [&](const char* code) -> std::optional<JoinRequest> {
    if (error) *error = code;
    return std::nullopt;
  };
  const json doc = json::parse(text.begin(), text.end(), nullptr, /*allow_exceptions=*/false);
  if (!doc.is_object()) return fail("bad_json");
  JoinRequest r;
  if (!parse_endpoint(get_string(doc, "address"), r.host, r.port)) return fail("bad_address");
  r.name = get_string(doc, "name");
  if (r.name.empty() || r.name.size() > 96) return fail("bad_name");  // the UI limits to 24 characters; bytes here
  r.password = get_string(doc, "password");
  r.admin_password = get_string(doc, "admin_password");
  r.want_authority = get_string(doc, "role") == "authority" || get_bool(doc, "authority");
  {
    std::vector<std::uint8_t> sha;
    if (crypto::from_hex(get_string(doc, "loaded_save_sha256"), sha) && sha.size() == 32) r.loaded_save_sha256 = std::move(sha);
  }
  const auto team = doc.find("team");
  if (team != doc.end() && team->is_number_unsigned()) {
    const auto v = team->get<std::uint64_t>();
    if (v > 0 && v <= 8) r.team = static_cast<std::uint16_t>(v);
  }
  return r;
}

std::optional<std::vector<mods::ReportedExtension>> parse_extensions(std::string_view text) {
  const json doc = json::parse(text.begin(), text.end(), nullptr, /*allow_exceptions=*/false);
  if (!doc.is_object()) return std::nullopt;
  const auto list = doc.find("list");
  if (list == doc.end() || !list->is_array()) return std::nullopt;
  std::vector<mods::ReportedExtension> out;
  for (const auto& e : *list) {
    if (!e.is_object() || out.size() >= 512) continue;
    mods::ReportedExtension r;
    r.id = get_string(e, "id");
    if (r.id.empty()) continue;
    r.name = get_string(e, "name");
    r.version = get_string(e, "version");
    r.enabled = get_bool(e, "enabled");
    r.egosoft = get_bool(e, "egosoftextension");
    r.error = get_string(e, "error");
    r.warning = get_string(e, "warning");
    out.push_back(std::move(r));
  }
  return out;
}

std::optional<std::string_view> reject_for_code(std::uint16_t code) noexcept {
  switch (code) {
    case 4: return "banned";
    case 10: return "mod";   // ProtocolMismatch: the mod is too old or too new for this server
    case 11: return "mod";   // ModVersionMismatch
    case 12: return "build"; // GameVersionMismatch
    case 13: return "mod";   // ExtensionsMismatch
    case 14: return "auth";
    case 15: return "full";
    case 16: return "name";
    case 21: return "full";  // NoFactionSlot
    case 3:                  // Kicked
    case 5:                  // SupersededByNewConnection
    case 17:                 // RoleUnavailable
    case 18:                 // NotJoinable
    case 20:                 // HandshakeTimeout
      return "other";
    default: return std::nullopt;
  }
}

}  // namespace x4mp::features::join
