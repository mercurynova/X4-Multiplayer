#pragma once
// core/version: mod version, supported-build pin and the init banner.

#include <string>
#include <string_view>

namespace x4mp::version {

[[nodiscard]] std::string_view mod_version() noexcept;      // e.g. "0.1.0"
[[nodiscard]] std::string_view game_build_pin() noexcept;   // "900-611726"
// "x4mp 0.1.0 hello (protocol 0.1, game build pin 900-611726)"
[[nodiscard]] std::string hello_line();

}  // namespace x4mp::version