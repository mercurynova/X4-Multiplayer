#include "core/log/log.h"

#include <array>
#include <cctype>

namespace x4mp::log {

const char* level_name(Level level) noexcept {
  switch (level) {
    case Level::Debug: return "DEBUG";
    case Level::Info: return "INFO";
    case Level::Warn: return "WARN";
    case Level::Error: return "ERROR";
  }
  return "?";
}

std::optional<Level> parse_level(std::string_view text) noexcept {
  constexpr std::array<Level, 4> kAll{Level::Debug, Level::Info, Level::Warn, Level::Error};
  for (Level level : kAll) {
    std::string_view name = level_name(level);
    if (name.size() != text.size()) continue;
    bool equal = true;
    for (std::size_t i = 0; i < name.size(); ++i) {
      if (std::toupper(static_cast<unsigned char>(text[i])) != name[i]) {
        equal = false;
        break;
      }
    }
    if (equal) return level;
  }
  return std::nullopt;
}

bool should_log(Level minimum, Level message) noexcept {
  return static_cast<int>(message) >= static_cast<int>(minimum);
}

std::string format_line(Level level, std::string_view message) {
  std::string line;
  line.reserve(message.size() + 10);
  line += '[';
  line += level_name(level);
  line += "] ";
  line += message;
  return line;
}

}  // namespace x4mp::log