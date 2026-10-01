#include "core/net/net.h"

#include "x4mp/wire.h"

namespace x4mp::net {

std::string protocol_version_string() {
  return std::to_string(wire::kProtocolMajor) + "." + std::to_string(wire::kProtocolMinor);
}

int default_tcp_port() noexcept { return wire::kDefaultTcpPort; }

}  // namespace x4mp::net