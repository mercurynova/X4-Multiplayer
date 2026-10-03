#include "features/launch/launch_json.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <sstream>
#include <system_error>

#include <nlohmann/json.hpp>

namespace x4mp::features::launch {

namespace {
using Json = nlohmann::json;

void wipe_string(std::string& s) {
  std::fill(s.begin(), s.end(), '\0');
  s.clear();
}

bool digits(std::string_view s, std::size_t pos, std::size_t n, int& out) {
  if (pos + n > s.size()) return false;
  int v = 0;
  for (std::size_t i = 0; i < n; ++i) {
    const char c = s[pos + i];
    if (c < '0' || c > '9') return false;
    v = v * 10 + (c - '0');
  }
  out = v;
  return true;
}

// Days from 1970-01-01 to y-m-d (proleptic Gregorian; Howard Hinnant's algorithm).
std::int64_t days_from_civil(int y, unsigned m, unsigned d) {
  y -= m <= 2;
  const std::int64_t era = (y >= 0 ? y : y - 399) / 400;
  const unsigned yoe = static_cast<unsigned>(y - era * 400);
  const unsigned doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1;
  const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
  return era * 146097 + static_cast<std::int64_t>(doe) - 719468;
}

bool string_field(const Json& doc, const char* key, std::string& out, bool& wrong_type) {
  const auto it = doc.find(key);
  if (it == doc.end() || it->is_null()) return false;
  if (!it->is_string()) {
    wrong_type = true;
    return false;
  }
  out = it->get<std::string>();
  return true;
}
}  // namespace

bool parse_utc_timestamp(std::string_view t, std::int64_t& unix_seconds) {
  int y = 0, mo = 0, d = 0, h = 0, mi = 0, s = 0;
  if (t.size() < 20) return false;
  if (!digits(t, 0, 4, y) || t[4] != '-' || !digits(t, 5, 2, mo) || t[7] != '-' || !digits(t, 8, 2, d)) return false;
  if ((t[10] != 'T' && t[10] != 't') || !digits(t, 11, 2, h) || t[13] != ':' || !digits(t, 14, 2, mi) || t[16] != ':' ||
      !digits(t, 17, 2, s)) {
    return false;
  }
  std::size_t pos = 19;
  if (pos < t.size() && t[pos] == '.') {  // fractional seconds are accepted and ignored
    ++pos;
    const std::size_t start = pos;
    while (pos < t.size() && t[pos] >= '0' && t[pos] <= '9') ++pos;
    if (pos == start) return false;
  }
  if (pos + 1 != t.size() || (t[pos] != 'Z' && t[pos] != 'z')) return false;
  if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 60) return false;
  static constexpr int kDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
  const bool leap = (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
  if (d > kDays[mo - 1] + (mo == 2 && leap ? 1 : 0)) return false;
  unix_seconds = days_from_civil(y, static_cast<unsigned>(mo), static_cast<unsigned>(d)) * 86400 + h * 3600 + mi * 60 + s;
  return true;
}

LaunchParse parse_launch(std::string_view text, std::int64_t now_unix) {
  LaunchParse out;
  const Json doc = Json::parse(text.begin(), text.end(), nullptr, false);
  if (doc.is_discarded() || !doc.is_object()) {
    out.error = "bad_json";
    return out;
  }
  LaunchRequest r;
  bool wrong_type = false;
  std::string role;
  std::string expires;
  string_field(doc, "server", r.server, wrong_type);
  string_field(doc, "name", r.name, wrong_type);
  string_field(doc, "password", r.password, wrong_type);
  string_field(doc, "admin_password", r.admin_password, wrong_type);
  string_field(doc, "role", role, wrong_type);
  const bool has_expires = string_field(doc, "expires_utc", expires, wrong_type);
  auto fail = [&](const char* code) -> LaunchParse {
    wipe(r);
    out.error = code;
    return out;
  };
  if (wrong_type) return fail("bad_type");
  if (r.server.empty()) return fail("no_server");
  if (r.server.size() > 255) return fail("bad_server");
  if (r.name.empty()) return fail("no_name");
  if (r.name.size() > 96) return fail("bad_name");
  std::transform(role.begin(), role.end(), role.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  if (!role.empty() && role != "client" && role != "authority") return fail("bad_role");
  r.want_authority = role == "authority";
  if (has_expires) {
    std::int64_t when = 0;
    if (!parse_utc_timestamp(expires, when)) return fail("bad_expires");
    r.expires_unix = when;
    if (when < now_unix) {
      wipe(r);  // an expired request must not keep its secrets around
      out.status = LaunchStatus::Expired;
      out.error = "expired";
      out.request = std::move(r);
      return out;
    }
  }
  out.status = LaunchStatus::Ok;
  out.request = std::move(r);
  return out;
}

ConsumeResult consume_launch_file(const std::filesystem::path& path, std::int64_t now_unix) {
  ConsumeResult out;
  std::error_code ec;
  if (path.empty() || !std::filesystem::exists(path, ec)) {
    out.removed = true;
    return out;
  }
  out.existed = true;
  std::string text;
  {
    std::ifstream in(path, std::ios::binary);
    if (in) {
      std::ostringstream ss;
      ss << in.rdbuf();
      text = ss.str();
    }
  }
  std::error_code rm_ec;
  std::filesystem::remove(path, rm_ec);
  out.removed = !rm_ec && !std::filesystem::exists(path, ec);
  if (!out.removed) {
    std::ofstream trunc(path, std::ios::binary | std::ios::trunc);  // last resort: do not leave the secrets on disk
    out.scrubbed = static_cast<bool>(trunc);
  }
  if (text.empty()) {
    out.parse.error = "unreadable";
  } else {
    out.parse = parse_launch(text, now_unix);
  }
  std::fill(text.begin(), text.end(), 'x');  // best effort scrub
  return out;
}

std::string to_join_payload(const LaunchRequest& r) {
  Json j = {{"v", 1}, {"address", r.server}, {"name", r.name}, {"password", r.password}, {"team", "auto"}};
  if (r.want_authority) j["role"] = "authority";
  if (!r.admin_password.empty()) j["admin_password"] = r.admin_password;
  return j.dump();
}

std::string describe(const LaunchRequest& r) {
  std::string s = "server=" + r.server + " name=" + r.name + " role=" + (r.want_authority ? "authority" : "client") +
                  " password=" + (r.password.empty() ? "<unset>" : "<redacted>") +
                  " admin_password=" + (r.admin_password.empty() ? "<unset>" : "<redacted>");
  if (r.expires_unix) s += " expires_unix=" + std::to_string(*r.expires_unix);
  return s;
}

void wipe(LaunchRequest& r) {
  wipe_string(r.password);
  wipe_string(r.admin_password);
}

}  // namespace x4mp::features::launch
