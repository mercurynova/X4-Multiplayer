#include "host/status_json.h"

#include <cstdio>

namespace x4mp::host {

std::string json_escape(std::string_view text) {
  std::string out;
  out.reserve(text.size() + 2);
  for (const char c : text) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      case '\b': out += "\\b"; break;
      case '\f': out += "\\f"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20) {
          char buf[8];
          std::snprintf(buf, sizeof buf, "\\u%04x", static_cast<unsigned>(static_cast<unsigned char>(c)));
          out += buf;
        } else {
          out += c;
        }
    }
  }
  return out;
}

namespace {
void put_str(std::string& out, std::string_view key, std::string_view value) {
  if (value.empty()) return;
  out += ",\"";
  out += key;
  out += "\":\"";
  out += json_escape(value);
  out += '"';
}
void put_int(std::string& out, std::string_view key, std::optional<int> value) {
  if (!value) return;
  out += ",\"";
  out += key;
  out += "\":";
  out += std::to_string(*value);
}
}  // namespace

std::string make_status_json(const StatusFields& f) {
  std::string out = "{\"v\":1,\"state\":\"";
  out += json_escape(f.state);
  out += '"';
  put_str(out, "detail", f.detail);
  put_str(out, "reject", f.reject);
  put_str(out, "server", f.server);
  put_str(out, "role", f.role);
  put_int(out, "team", f.team);
  put_str(out, "team_name", f.team_name);
  put_int(out, "ping_ms", f.ping_ms);
  put_int(out, "players", f.players);
  if (f.progress) {
    char buf[32];
    std::snprintf(buf, sizeof buf, "%.3f", *f.progress < 0.0 ? 0.0 : (*f.progress > 1.0 ? 1.0 : *f.progress));
    out += ",\"progress\":";
    out += buf;
  }
  out += '}';
  return out;
}

std::string make_notify_json(std::string_view text, std::string_view level) {
  return "{\"v\":1,\"text\":\"" + json_escape(text) + "\",\"level\":\"" + json_escape(level) + "\"}";
}

std::string make_error_json(std::string_view code, std::string_view text) {
  return "{\"v\":1,\"code\":\"" + json_escape(code) + "\",\"text\":\"" + json_escape(text) + "\"}";
}

std::string make_load_save_json(std::string_view name, bool fallback) {
  return "{\"v\":1,\"name\":\"" + json_escape(name) + "\"" + (fallback ? ",\"fallback\":true" : "") + "}";
}

}  // namespace x4mp::host
