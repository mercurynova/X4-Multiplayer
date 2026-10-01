#pragma once
// core/net: transport. Placeholder (sockets, framing glue and reconnect arrive in M1-N2). Today it only
// exposes the protocol version from the shared x4mp::wire library so the link is exercised.

#include <string>

namespace x4mp::net {

[[nodiscard]] std::string protocol_version_string();  // "0.1"
[[nodiscard]] int default_tcp_port() noexcept;        // 47780

}  // namespace x4mp::net