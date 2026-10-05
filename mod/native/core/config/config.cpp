#include "core/config/config.h"

#include <chrono>
#include <fstream>
#include <sstream>
#include <system_error>

#include <nlohmann/json.hpp>

namespace x4mp::config {

namespace {
using Json = nlohmann::json;
using Diags = std::vector<Diagnostic>;

void add(Diags& diags, log::Level level, std::string message) { diags.push_back({level, std::move(message)}); }

void bad(Diags& diags, std::string_view source, std::string_view key, std::string_view want) {
  std::string m = "config ";
  m += source;
  m += ": key '";
  m += key;
  m += "' ";
  m += want;
  m += "; using default";
  add(diags, log::Level::Error, std::move(m));
}

// Each reader returns true when the key was present and valid (out updated). Present-but-invalid reports an
// Error naming the key and leaves `out` alone, so the previous layer's value (or the default) survives.
bool read_string(const Json& doc, std::string_view key, bool allow_empty, std::string& out, std::string_view source,
                 Diags& diags) {
  const auto it = doc.find(std::string(key));
  if (it == doc.end()) return false;
  if (it->is_string() && (allow_empty || !it->get_ref<const std::string&>().empty())) {
    out = it->get<std::string>();
    return true;
  }
  bad(diags, source, key, allow_empty ? "must be a string" : "must be a non-empty string");
  return false;
}

bool read_int(const Json& doc, std::string_view key, long long lo, long long hi, long long& out, std::string_view source,
              Diags& diags) {
  const auto it = doc.find(std::string(key));
  if (it == doc.end()) return false;
  if (it->is_number_integer()) {
    const long long v = it->get<long long>();
    if (v >= lo && v <= hi) {
      out = v;
      return true;
    }
  }
  bad(diags, source, key, "must be an integer in " + std::to_string(lo) + ".." + std::to_string(hi));
  return false;
}

void warn_unknown(const Json& doc, std::initializer_list<std::string_view> known, std::string_view source, Diags& diags) {
  for (auto it = doc.begin(); it != doc.end(); ++it) {
    bool found = false;
    for (auto k : known) found = found || (it.key() == k);
    if (!found) add(diags, log::Level::Warn, "config " + std::string(source) + ": unknown key '" + it.key() + "' ignored");
  }
}

bool read_bool(const Json& doc, std::string_view key, bool& out, std::string_view source, Diags& diags, std::string_view shown_key = {}) {
  const auto it = doc.find(std::string(key));
  if (it == doc.end()) return false;
  if (it->is_boolean()) {
    out = it->get<bool>();
    return true;
  }
  bad(diags, source, shown_key.empty() ? key : shown_key, "must be true or false");
  return false;
}

// M3-23: {"diag": {"takeover_keep_original": true, ...}} and/or the flat keys "diag.takeover_keep_original" (the flat one wins when both exist).
void apply_diag(DiagConfig& d, const Json& doc, std::string_view source, Diags& diags) {
  struct Field {
    const char* name;
    bool DiagConfig::*member;
  };
  static constexpr Field kFields[] = {{"takeover_keep_original", &DiagConfig::takeover_keep_original},
                                      {"takeover_off", &DiagConfig::takeover_off},
                                      {"ghosts_off", &DiagConfig::ghosts_off},
                                      {"janitor_off", &DiagConfig::janitor_off}};
  if (const auto it = doc.find("diag"); it != doc.end()) {
    if (!it->is_object()) {
      bad(diags, source, "diag", "must be an object of switch name to true or false");
    } else {
      for (const auto& f : kFields) read_bool(*it, f.name, d.*f.member, source, diags, std::string("diag.") + f.name);
      for (auto cit = it->begin(); cit != it->end(); ++cit) {
        bool known = false;
        for (const auto& f : kFields) known = known || cit.key() == f.name;
        if (!known) add(diags, log::Level::Warn, "config " + std::string(source) + ": unknown key 'diag." + cit.key() + "' ignored");
      }
    }
  }
  for (const auto& f : kFields) {
    const std::string flat = std::string("diag.") + f.name;
    read_bool(doc, flat, d.*f.member, source, diags);
  }
}

void apply_user(Config& cfg, const Json& doc, std::string_view source, Diags& diags) {
  read_string(doc, "server_host", false, cfg.server_host, source, diags);
  if (long long v = 0; read_int(doc, "tcp_port", 1, 65535, v, source, diags)) cfg.tcp_port = static_cast<int>(v);
  if (auto it = doc.find("log_level"); it != doc.end()) {
    const auto level = it->is_string() ? log::parse_level(it->get<std::string>()) : std::nullopt;
    if (level) cfg.log_level = *level;
    else bad(diags, source, "log_level", "must be one of debug, info, warn, error");
  }
  read_string(doc, "log_file", true, cfg.log_file, source, diags);
  if (long long v = 0; read_int(doc, "log_rate_limit", 1, 1000, v, source, diags)) cfg.log_rate_limit = static_cast<int>(v);
  read_string(doc, "player_name", true, cfg.player_name, source, diags);
  read_string(doc, "password", true, cfg.password, source, diags);
  if (long long v = 0; read_int(doc, "outbox_byte_cap", 64LL * 1024, 1LL << 30, v, source, diags)) {
    cfg.outbox_byte_cap = static_cast<std::size_t>(v);
  }
  if (long long v = 0; read_int(doc, "frame_budget_us", 100, 50000, v, source, diags)) cfg.frame_budget_us = static_cast<int>(v);
  if (auto it = doc.find("selftest"); it != doc.end()) {
    if (it->is_boolean()) cfg.selftest = it->get<bool>();
    else bad(diags, source, "selftest", "must be true or false");
  }
  if (auto it = doc.find("log_categories"); it != doc.end()) {
    if (!it->is_object()) {
      bad(diags, source, "log_categories", "must be an object of category name to level");
    } else {
      std::vector<std::pair<std::string, log::Level>> cats;
      bool ok = true;
      for (auto cit = it->begin(); cit != it->end(); ++cit) {
        const auto level = cit.value().is_string() ? log::parse_level(cit.value().get<std::string>()) : std::nullopt;
        if (!level) {
          bad(diags, source, "log_categories." + cit.key(), "must be one of debug, info, warn, error");
          ok = false;
          continue;
        }
        cats.emplace_back(cit.key(), *level);
      }
      if (ok || !cats.empty()) cfg.log_categories = std::move(cats);
    }
  }
  apply_diag(cfg.diag, doc, source, diags);
  warn_unknown(doc,
               {"diag", "diag.takeover_keep_original", "diag.takeover_off", "diag.ghosts_off", "diag.janitor_off", "server_host", "tcp_port", "log_level", "log_file", "log_rate_limit", "player_name", "password",
                "outbox_byte_cap", "frame_budget_us", "log_categories", "selftest", "last_address", "last_name"},  // last_*: remembered Join fields (M3-07)
               source, diags);
}

// launch.json: { "server": "host[:port]", "name": "...", "password": "...", "password_ref": "prompt|inline",
//                "save": "...", "expires": <unix seconds> }
void apply_launch(Config& cfg, const Json& doc, std::int64_t now_unix, std::string_view source, Diags& diags) {
  long long expires = 0;
  read_int(doc, "expires", 0, (1LL << 62), expires, source, diags);
  if (expires > 0 && now_unix > expires) {
    add(diags, log::Level::Warn, "config " + std::string(source) + ": launch request expired; ignored");
    return;
  }
  Config next = cfg;
  std::string server;
  if (read_string(doc, "server", false, server, source, diags)) {
    std::string host = server;
    int port = cfg.tcp_port;
    bool ok = true;
    const bool bracketed = !server.empty() && server.front() == '[';
    const auto colon = bracketed ? server.find("]:") : server.rfind(':');
    const bool has_port = colon != std::string::npos && (bracketed || server.find(':') == colon);
    if (has_port) {
      const std::size_t host_end = bracketed ? colon + 1 : colon;
      host = server.substr(0, host_end);
      const std::string port_text = server.substr(colon + (bracketed ? 2 : 1));
      long long p = 0;
      ok = !port_text.empty() && port_text.size() <= 5;
      for (char ch : port_text) {
        if (ch < '0' || ch > '9') ok = false;
        else p = p * 10 + (ch - '0');
      }
      ok = ok && p >= 1 && p <= 65535;
      port = static_cast<int>(p);
    }
    if (ok && !host.empty()) {
      next.server_host = host;
      next.tcp_port = port;
    } else {
      bad(diags, source, "server", "must be host or host:port with port 1..65535");
    }
  }
  read_string(doc, "name", true, next.player_name, source, diags);
  read_string(doc, "password", true, next.password, source, diags);
  read_string(doc, "password_ref", true, next.launch.password_ref, source, diags);
  read_string(doc, "save", true, next.launch.save, source, diags);
  next.launch.active = true;
  warn_unknown(doc, {"server", "name", "password", "password_ref", "save", "expires"}, source, diags);
  cfg = std::move(next);
}

std::optional<std::string> read_file(const std::filesystem::path& path) {
  std::ifstream in(path, std::ios::binary);
  if (!in) return std::nullopt;
  std::ostringstream ss;
  ss << in.rdbuf();
  return ss.str();
}

std::int64_t system_now_unix() {
  return std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}
}  // namespace

Config defaults() { return Config{}; }

std::optional<Config> parse_json(std::string_view text, std::string* errors) {
  // allow_exceptions = false: a malformed document yields a discarded value instead of throwing.
  const Json doc = Json::parse(text.begin(), text.end(), nullptr, false);
  if (doc.is_discarded() || !doc.is_object()) return std::nullopt;
  Config cfg = defaults();
  Diags diags;
  apply_user(cfg, doc, "json", diags);
  if (errors) {
    for (const auto& d : diags) {
      if (d.level != log::Level::Error) continue;
      if (!errors->empty()) *errors += "; ";
      *errors += d.message;
    }
  }
  return cfg;
}

LoadResult load(const LoadOptions& options) {
  LoadResult result;
  result.config = defaults();
  std::error_code ec;

  if (!options.user_file.empty() && std::filesystem::exists(options.user_file, ec)) {
    const auto text = read_file(options.user_file);
    if (!text) {
      add(result.diagnostics, log::Level::Error, "config user file: cannot read; using defaults");
    } else {
      const Json doc = Json::parse(text->begin(), text->end(), nullptr, false);
      if (doc.is_discarded() || !doc.is_object()) {
        add(result.diagnostics, log::Level::Error, "config user file: not a JSON object; using defaults");
      } else {
        apply_user(result.config, doc, "user file", result.diagnostics);
      }
    }
  }

  if (!options.launch_file.empty() && std::filesystem::exists(options.launch_file, ec)) {
    const auto text = read_file(options.launch_file);
    // Consume first: whatever happens next, a second load must not see this file again.
    auto consumed = options.launch_file;
    consumed += ".consumed";
    std::error_code rename_ec;
    std::filesystem::remove(consumed, rename_ec);
    std::filesystem::rename(options.launch_file, consumed, rename_ec);
    if (rename_ec) {
      std::error_code rm_ec;
      std::filesystem::remove(options.launch_file, rm_ec);
      if (rm_ec) add(result.diagnostics, log::Level::Error, "config launch file: could not rename or delete it after reading");
      else result.launch_consumed = true;
    } else {
      result.launch_consumed = true;
    }

    if (!text) {
      add(result.diagnostics, log::Level::Error, "config launch file: cannot read; ignored");
    } else {
      const Json doc = Json::parse(text->begin(), text->end(), nullptr, false);
      if (doc.is_discarded() || !doc.is_object()) {
        add(result.diagnostics, log::Level::Error, "config launch file: not a JSON object; ignored");
      } else {
        apply_launch(result.config, doc, options.now_unix >= 0 ? options.now_unix : system_now_unix(), "launch file",
                     result.diagnostics);
      }
    }
  }
  return result;
}

void report(const LoadResult& result, log::Logger& logger) {
  for (const auto& d : result.diagnostics) logger.log_raw(d.level, d.message);
}

std::string describe(const Config& c) {
  std::string out;
  out += "server_host=" + c.server_host + "\n";
  out += "tcp_port=" + std::to_string(c.tcp_port) + "\n";
  out += std::string("log_level=") + log::level_name(c.log_level) + "\n";
  out += "log_file=" + c.log_file + "\n";
  out += "log_rate_limit=" + std::to_string(c.log_rate_limit) + "\n";
  out += "player_name=" + c.player_name + "\n";
  out += std::string("password=") + (c.password.empty() ? "<unset>" : "<redacted>") + "\n";
  out += "outbox_byte_cap=" + std::to_string(c.outbox_byte_cap) + "\n";
  out += "frame_budget_us=" + std::to_string(c.frame_budget_us) + "\n";
  for (const auto& [name, level] : c.log_categories) out += "log_category." + name + "=" + log::level_name(level) + "\n";
  if (c.diag.any()) {
    out += std::string("diag.takeover_keep_original=") + (c.diag.takeover_keep_original ? "true" : "false") + "\n";
    out += std::string("diag.takeover_off=") + (c.diag.takeover_off ? "true" : "false") + "\n";
    out += std::string("diag.ghosts_off=") + (c.diag.ghosts_off ? "true" : "false") + "\n";
    out += std::string("diag.janitor_off=") + (c.diag.janitor_off ? "true" : "false") + "\n";
  }
  out += std::string("launch_active=") + (c.launch.active ? "true" : "false") + "\n";
  return out;
}

}  // namespace x4mp::config
