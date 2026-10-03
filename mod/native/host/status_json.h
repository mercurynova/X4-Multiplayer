#pragma once
// host/status_json: the native -> Lua topic payloads of the M2 bridge contract (docs/mod-design.md section 7, "M2 bridge
// contract"). SDK-free text builders; the join feature and the refused-host responder both use them. Every payload is one
// UTF-8 JSON object with "v":1; only "state" is required in x4mp.status.

#include <optional>
#include <string>
#include <string_view>

namespace x4mp::host {

// JSON string escaping (quotes, backslash, control characters; non-ASCII bytes pass through as UTF-8).
[[nodiscard]] std::string json_escape(std::string_view text);

// x4mp.status. Empty strings / nullopt fields are omitted.
struct StatusFields {
  std::string_view state = "disconnected";  // disconnected|connecting|handshaking|checking_save|downloading|loading|matching|ingame|rejected|error
  std::string_view detail;
  std::string_view reject;  // build|mod|auth|full|banned|name|protocol|role|kicked|other
  std::string_view server;  // host:port
  std::string_view role;    // client|authority
  std::optional<int> team;
  std::string_view team_name;
  std::optional<int> ping_ms;
  std::optional<int> players;
  std::optional<double> progress;  // 0..1
};
[[nodiscard]] std::string make_status_json(const StatusFields& fields);

// x4mp.notify (level info|warn|error), x4mp.error, x4mp.load_save.
[[nodiscard]] std::string make_notify_json(std::string_view text, std::string_view level = "info");
[[nodiscard]] std::string make_error_json(std::string_view code, std::string_view text);
// `fallback` = true: the vanilla loadSave event did not start a load; Lua must call LoadGame itself.
[[nodiscard]] std::string make_load_save_json(std::string_view name, bool fallback);

}  // namespace x4mp::host
