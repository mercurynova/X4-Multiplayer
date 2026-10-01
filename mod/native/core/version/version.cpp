#include "core/version/version.h"

#include "core/net/net.h"

#ifndef X4MP_VERSION_STRING
#error "X4MP_VERSION_STRING must be defined by the build (see mod/CMakeLists.txt)"
#endif
#ifndef X4MP_GAME_BUILD_PIN
#error "X4MP_GAME_BUILD_PIN must be defined by the build (see mod/CMakeLists.txt)"
#endif

namespace x4mp::version {

std::string_view mod_version() noexcept { return X4MP_VERSION_STRING; }
std::string_view game_build_pin() noexcept { return X4MP_GAME_BUILD_PIN; }

std::string hello_line() {
  std::string line = "x4mp ";
  line += mod_version();
  line += " hello (protocol ";
  line += net::protocol_version_string();
  line += ", game build pin ";
  line += game_build_pin();
  line += ')';
  return line;
}

}  // namespace x4mp::version