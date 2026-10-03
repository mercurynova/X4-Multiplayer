#include "probe_config.h"

#include <algorithm>
#include <cmath>
#include <nlohmann/json.hpp>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <knownfolders.h>
#include <shlobj.h>

namespace x4mp_probe {
namespace {
using nlohmann::json;

template <class T>
void read(const json& j, const char* key, T& out, std::vector<std::string>& notes) {
  const auto it = j.find(key);
  if (it == j.end() || it->is_null()) return;
  if constexpr (std::is_same_v<T, bool>) {
    if (it->is_boolean()) out = it->get<bool>();
    else notes.push_back(std::string("bad type for key ") + key);
  } else if constexpr (std::is_same_v<T, std::string>) {
    if (it->is_string()) out = it->get<std::string>();
    else notes.push_back(std::string("bad type for key ") + key);
  } else {
    if (it->is_number_integer()) out = static_cast<T>(it->get<std::int64_t>());
    else notes.push_back(std::string("bad type for key ") + key);
  }
}
}  // namespace

ParseResult parse_config(std::string_view text) {
  ParseResult r;
  // Exceptions off: nlohmann's error messages can quote the input, which may contain the password.
  const json j = json::parse(text.begin(), text.end(), nullptr, false);
  if (j.is_discarded() || !j.is_object()) return r;
  r.ok = true;
  Config& c = r.config;
  auto& n = r.notes;
  read(j, "server", c.server, n);
  read(j, "name", c.name, n);
  read(j, "password", c.password, n);
  read(j, "connect", c.connect, n);
  read(j, "auto_load", c.auto_load, n);
  read(j, "load_mode", c.load_mode, n);
  read(j, "pin_module", c.pin_module, n);
  read(j, "hooks", c.hooks, n);
  read(j, "skip_autosave", c.skip_autosave, n);
  read(j, "pause_on_ready", c.pause_on_ready, n);
  read(j, "pause_seconds", c.pause_seconds, n);
  read(j, "allow_native_thread_calls", c.allow_native_thread_calls, n);
  read(j, "spike_block", c.spike_block, n);
  read(j, "spike_block_seq", c.spike_block_seq, n);
  read(j, "reloadui_after_s", c.reloadui_after_s, n);
  read(j, "save_test", c.save_test, n);
  read(j, "money_test", c.money_test, n);
  c.pause_seconds = std::clamp(c.pause_seconds, 0, 600);
  if (c.load_mode != "event" && c.load_mode != "lua") {
    n.push_back("bad value for key load_mode");
    c.load_mode = "event";
  }
  return r;
}

std::optional<std::pair<std::string, std::uint16_t>> split_endpoint(std::string_view s) {
  while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
  while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
  if (s.empty()) return std::nullopt;
  std::string host(s);
  std::uint16_t port = 47780;
  const auto colon = s.rfind(':');
  const bool bracketed_v6 = s.front() == '[';
  if (colon != std::string_view::npos && (!bracketed_v6 || s.find(']') < colon)) {
    host = std::string(s.substr(0, colon));
    const std::string p(s.substr(colon + 1));
    if (p.empty() || p.size() > 5 || !std::all_of(p.begin(), p.end(), [](char ch) { return ch >= '0' && ch <= '9'; })) return std::nullopt;
    const long v = std::stol(p);
    if (v < 1 || v > 65535) return std::nullopt;
    port = static_cast<std::uint16_t>(v);
  }
  if (host.size() >= 2 && host.front() == '[' && host.back() == ']') host = host.substr(1, host.size() - 2);
  if (host.empty()) return std::nullopt;
  return std::make_pair(host, port);
}

std::string describe(const Config& c) {
  std::string s = "server=" + c.server + " name=" + c.name + " password=" + (c.password.empty() ? "<unset>" : "<set>");
  s += " connect=" + std::to_string(c.connect) + " auto_load=" + std::to_string(c.auto_load) + " load_mode=" + c.load_mode;
  s += " pin_module=" + std::to_string(c.pin_module) + " hooks=" + std::to_string(c.hooks) +
       " skip_autosave=" + std::to_string(c.skip_autosave) + " pause_on_ready=" + std::to_string(c.pause_on_ready) +
       " pause_seconds=" + std::to_string(c.pause_seconds) + " allow_native_thread_calls=" + std::to_string(c.allow_native_thread_calls);
  s += " spike_block=" + c.spike_block + " spike_block_seq=" + std::to_string(c.spike_block_seq) +
       " reloadui_after_s=" + std::to_string(c.reloadui_after_s) + " save_test=" + c.save_test +
       " money_test=" + std::to_string(c.money_test);
  return s;
}

std::string action_token(const Config& c) {
  return std::to_string(c.spike_block_seq) + "|" + c.spike_block;
}

double percentile(std::vector<double> v, double p) {
  if (v.empty()) return 0.0;
  std::sort(v.begin(), v.end());
  const double rank = std::ceil(std::clamp(p, 0.0, 100.0) / 100.0 * static_cast<double>(v.size()));
  const std::size_t idx = rank < 1.0 ? 0 : static_cast<std::size_t>(rank) - 1;
  return v[std::min(idx, v.size() - 1)];
}

std::filesystem::path default_config_dir() {
  PWSTR p = nullptr;
  if (FAILED(SHGetKnownFolderPath(FOLDERID_Documents, KF_FLAG_DEFAULT, nullptr, &p)) || p == nullptr) {
    if (p) CoTaskMemFree(p);
    return {};
  }
  std::filesystem::path out = std::filesystem::path(p) / L"Egosoft" / L"X4" / L"x4mp";
  CoTaskMemFree(p);
  return out;
}

}  // namespace x4mp_probe
