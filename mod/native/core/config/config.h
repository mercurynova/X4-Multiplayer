#pragma once
// core/config: configuration values and JSON parsing. Placeholder for defaults -> user file -> one-shot
// launch.json layering (M1-N1). Never reads environment variables (Steam relaunch drops them).

#include <optional>
#include <string>
#include <string_view>

#include "core/log/log.h"

namespace x4mp::config {

struct Config {
  std::string server_host = "127.0.0.1";
  int tcp_port = 47780;
  log::Level log_level = log::Level::Info;
};

[[nodiscard]] Config defaults();

// Parses a JSON object, overlaying known keys on the defaults. Unknown keys are ignored; a known key with
// an invalid value keeps its default and is reported in `errors` (when given). Returns nullopt when the
// text is not a JSON object.
[[nodiscard]] std::optional<Config> parse_json(std::string_view text, std::string* errors = nullptr);

}  // namespace x4mp::config