#include "core/config/remembered.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <sstream>
#include <system_error>

#include <nlohmann/json.hpp>

namespace x4mp::config {

namespace {
using OJson = nlohmann::ordered_json;
namespace fs = std::filesystem;

constexpr std::size_t kMaxAddress = 255;
constexpr std::size_t kMaxName = 64;

std::string lower(std::string_view s) {
  std::string out(s);
  std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  return out;
}

std::optional<std::string> read_file(const fs::path& path) {
  std::ifstream in(path, std::ios::binary);
  if (!in) return std::nullopt;
  std::ostringstream ss;
  ss << in.rdbuf();
  return ss.str();
}

std::string_view strip_bom(std::string_view text) {
  if (text.size() >= 3 && static_cast<unsigned char>(text[0]) == 0xEF && static_cast<unsigned char>(text[1]) == 0xBB &&
      static_cast<unsigned char>(text[2]) == 0xBF) {
    text.remove_prefix(3);
  }
  return text;
}

std::string clean(std::string_view in, std::size_t max_bytes) {
  std::string out;
  for (const char c : in) {
    if (static_cast<unsigned char>(c) < 0x20 || c == 0x7F) continue;
    out.push_back(c);
  }
  const auto b = out.find_first_not_of(' ');
  if (b == std::string::npos) return {};
  out = out.substr(b, out.find_last_not_of(' ') - b + 1);
  if (out.size() > max_bytes) out.resize(max_bytes);
  return out;
}

std::string get_string(const OJson& doc, const char* key) {
  const auto it = doc.find(key);
  return (it != doc.end() && it->is_string()) ? it->get<std::string>() : std::string();
}
}  // namespace

bool is_secret_key(std::string_view key) {
  const std::string k = lower(key);
  for (const char* needle : {"pass", "pwd", "secret", "token"}) {
    if (k.find(needle) != std::string::npos) return true;
  }
  return false;
}

WriteResult update_string_keys(const fs::path& path, const std::vector<std::pair<std::string, std::string>>& keys) {
  WriteResult r;
  for (const auto& kv : keys) {
    if (is_secret_key(kv.first)) {
      r.error = "refused: key '" + kv.first + "' looks like a secret and is never written";
      return r;
    }
  }
  if (path.empty()) {
    r.error = "no config path";
    return r;
  }
  std::error_code ec;
  OJson doc = OJson::object();
  if (fs::exists(path, ec)) {
    const auto text = read_file(path);
    if (!text) {
      r.error = "cannot read the existing file";
      return r;
    }
    const std::string_view body = strip_bom(*text);
    if (body.find_first_not_of(" \t\r\n") != std::string_view::npos) {
      doc = OJson::parse(body.begin(), body.end(), nullptr, false);
      if (doc.is_discarded() || !doc.is_object()) {
        r.error = "the existing file is not a JSON object; left untouched";
        return r;
      }
    }
  }
  for (const auto& kv : keys) doc[kv.first] = kv.second;

  std::string out;
  try {
    out = doc.dump(2, ' ', false, OJson::error_handler_t::replace);
  } catch (const std::exception&) {
    r.error = "cannot serialise the document";
    return r;
  }
  out.push_back('\n');

  if (path.has_parent_path()) fs::create_directories(path.parent_path(), ec);
  fs::path tmp = path;
  tmp += ".tmp";
  {
    std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
    if (!f) {
      r.error = "cannot create the temporary file";
      return r;
    }
    f.write(out.data(), static_cast<std::streamsize>(out.size()));
    f.flush();
    if (!f) {
      f.close();
      fs::remove(tmp, ec);
      r.error = "cannot write the temporary file";
      return r;
    }
  }
  fs::rename(tmp, path, ec);  // replaces an existing file (MoveFileEx REPLACE_EXISTING)
  if (ec) {
    std::error_code rm;
    fs::remove(tmp, rm);
    r.error = "cannot rename the temporary file over the config file";
    return r;
  }
  r.ok = true;
  return r;
}

Remembered read_remembered(const fs::path& path) {
  Remembered out;
  if (path.empty()) return out;
  const auto text = read_file(path);
  if (!text) return out;
  const std::string_view body = strip_bom(*text);
  const OJson doc = OJson::parse(body.begin(), body.end(), nullptr, false);
  if (doc.is_discarded() || !doc.is_object()) return out;
  out.address = clean(get_string(doc, "last_address"), kMaxAddress);
  out.name = clean(get_string(doc, "last_name"), kMaxName);
  return out;
}

std::optional<RememberRequest> parse_remember(std::string_view payload, std::string* error) {
  const OJson doc = OJson::parse(payload.begin(), payload.end(), nullptr, false);
  if (doc.is_discarded() || !doc.is_object()) {
    if (error) *error = "not a JSON object";
    return std::nullopt;
  }
  RememberRequest req;
  req.address = clean(get_string(doc, "address"), kMaxAddress);
  req.name = clean(get_string(doc, "name"), kMaxName);
  if (const auto it = doc.find("migrate"); it != doc.end() && it->is_boolean()) req.migrate = it->get<bool>();
  if (req.address.empty() && req.name.empty()) {
    if (error) *error = "neither address nor name";
    return std::nullopt;
  }
  return req;
}

WriteResult apply_remember(const fs::path& path, const RememberRequest& request, Remembered* after) {
  const Remembered current = read_remembered(path);
  Remembered next = current;
  if (!request.address.empty() && (!request.migrate || current.address.empty())) next.address = request.address;
  if (!request.name.empty() && (!request.migrate || current.name.empty())) next.name = request.name;
  WriteResult r;
  r.ok = true;
  if (next.address != current.address || next.name != current.name) {
    std::vector<std::pair<std::string, std::string>> keys;
    if (next.address != current.address) keys.emplace_back("last_address", next.address);
    if (next.name != current.name) keys.emplace_back("last_name", next.name);
    r = update_string_keys(path, keys);
    if (!r.ok) next = current;
  }
  if (after) *after = next;
  return r;
}

std::string make_remembered_json(const Remembered& remembered) {
  OJson doc = OJson::object();
  doc["v"] = 1;
  doc["address"] = remembered.address;
  doc["name"] = remembered.name;
  return doc.dump(-1, ' ', false, OJson::error_handler_t::replace);
}

}  // namespace x4mp::config
