#pragma once
// features/join/join_json: the Lua -> native verb payloads of the M2 bridge contract (docs/mod-design.md section 7) and the
// mapping of server rejection codes to the reject tokens the Lua UI shows. SDK-free and free of the session layer, so Catch2
// tests them directly.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/mods/mods.h"

namespace x4mp::features::join {

// x4mp.join {"v":1,"address":"host:port","name":"Player","password":"...","team":"auto"} (+ optional M2-09 fields).
struct JoinRequest {
  std::string host;
  std::uint16_t port = 47780;
  std::string name;
  std::string password;        // SECRET: never logged; moved into the Session and wiped here
  std::string admin_password;  // SECRET: optional, M2-09 (authority role); empty = none
  bool want_authority = false; // optional "role":"authority" (M2-09); the join flow only forwards it
  std::uint16_t team = 0;      // 0 = auto
};

// nullopt (with `error` set to a short machine code) when the text is not a valid join request.
[[nodiscard]] std::optional<JoinRequest> parse_join(std::string_view json, std::string* error = nullptr);

// Splits "host:port", "host", "[::1]:47780" or "[::1]". Port defaults to 47780.
[[nodiscard]] bool parse_endpoint(std::string_view text, std::string& host, std::uint16_t& port);

// x4mp.extensions {"v":1,"source":...,"count":N,"list":[{"id","name","version","enabled","egosoftextension","error","warning",...}]}
// Entries without an id are skipped; at most 512 entries.
[[nodiscard]] std::optional<std::vector<mods::ReportedExtension>> parse_extensions(std::string_view json);

// ---- rejection codes -------------------------------------------------------------------------------------------------------
// Wire DisconnectCode -> the "reject" token of x4mp.status (build|mod|auth|full|banned|name|other).
// A rejection ends the attempt: the join flow stops the session instead of letting the net layer redial forever.
// nullopt: not a rejection (ClientQuit, ServerShutdown, ResumeExpired, heartbeat ...; the net layer redials on its own).
[[nodiscard]] std::optional<std::string_view> reject_for_code(std::uint16_t disconnect_code) noexcept;

}  // namespace x4mp::features::join
