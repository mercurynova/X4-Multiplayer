#pragma once
// core/log: log levels and line formatting. Placeholder for the async, rate-limited logger (M1-N1).
// Pure C++: the X4Native logging API is only reached from main.cpp / game/.

#include <optional>
#include <string>
#include <string_view>

namespace x4mp::log {

enum class Level { Debug = 0, Info = 1, Warn = 2, Error = 3 };

[[nodiscard]] const char* level_name(Level level) noexcept;
[[nodiscard]] std::optional<Level> parse_level(std::string_view text) noexcept;  // case-insensitive
[[nodiscard]] bool should_log(Level minimum, Level message) noexcept;
[[nodiscard]] std::string format_line(Level level, std::string_view message);    // "[INFO] message"

}  // namespace x4mp::log