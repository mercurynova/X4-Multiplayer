#include "core/config/config.h"

#include <nlohmann/json.hpp>

namespace x4mp::config {

namespace {
void add_error(std::string* errors, std::string_view message) {
  if (!errors) return;
  if (!errors->empty()) *errors += "; ";
  *errors += message;
}
}  // namespace

Config defaults() { return Config{}; }

std::optional<Config> parse_json(std::string_view text, std::string* errors) {
  // allow_exceptions = false: a malformed document yields a discarded value instead of throwing.
  const nlohmann::json doc = nlohmann::json::parse(text.begin(), text.end(), nullptr, false);
  if (doc.is_discarded() || !doc.is_object()) return std::nullopt;

  Config cfg = defaults();
  if (auto it = doc.find("server_host"); it != doc.end()) {
    if (it->is_string() && !it->get_ref<const std::string&>().empty()) cfg.server_host = it->get<std::string>();
    else add_error(errors, "server_host must be a non-empty string");
  }
  if (auto it = doc.find("tcp_port"); it != doc.end()) {
    if (it->is_number_integer() && it->get<long long>() >= 1 && it->get<long long>() <= 65535) cfg.tcp_port = it->get<int>();
    else add_error(errors, "tcp_port must be an integer in 1..65535");
  }
  if (auto it = doc.find("log_level"); it != doc.end()) {
    const auto level = it->is_string() ? log::parse_level(it->get<std::string>()) : std::nullopt;
    if (level) cfg.log_level = *level;
    else add_error(errors, "log_level must be one of debug, info, warn, error");
  }
  return cfg;
}

}  // namespace x4mp::config