#pragma once
// core/config/remembered: the Join form's remembered fields in our own x4mp.json (M3-07; docs/mod-design.md 2.6).
//
//   last_address  the last server the player joined ("host:port")
//   last_name     the player name used there
//
// They used to live in the Lua saved variable __X4MP_USER (uidata.xml), which X4 rewrites without our entries when it runs once
// without the mod. Lua now sends the verb x4mp.remember on Connect, the native side stores the fields here and pushes them back as
// the topic x4mp.remembered. __X4MP_USER is only read once, as the migration source.
//
// NEVER a password: no secret-looking key can be written through this API (is_secret_key), and the payload parser has no field for one.
//
// The writer is atomic: the new document is written to "<file>.tmp", flushed, then renamed over the file, so a crash leaves the
// old file (or no file) and never a half-written one. Keys we do not know and keys the user set by hand (log_level, ...) are
// kept, in their original order. A file that is not a JSON object is NOT overwritten (the write fails and says so).
// No exceptions cross this API; no BOM is written (a BOM in the existing file is accepted on read).

#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace x4mp::config {

struct Remembered {
  std::string address;  // empty = none
  std::string name;     // empty = none
  [[nodiscard]] bool empty() const { return address.empty() && name.empty(); }
};

struct WriteResult {
  bool ok = false;
  std::string error;  // key names / paths only, never a value
};

// True for keys that look like secrets (pass, pwd, secret, token; case-insensitive). The writer refuses them.
[[nodiscard]] bool is_secret_key(std::string_view key);

// Sets the given string keys in the JSON object file `path` (created, with its folder, when missing), atomically.
// Refuses (nothing written) when any key is_secret_key or the existing file is not a JSON object.
[[nodiscard]] WriteResult update_string_keys(const std::filesystem::path& path,
                                             const std::vector<std::pair<std::string, std::string>>& keys);

// Reads last_address / last_name; missing file or keys give empty fields.
[[nodiscard]] Remembered read_remembered(const std::filesystem::path& path);

// One x4mp.remember request. migrate=true: the values come from the legacy __X4MP_USER and only fill fields the file does not have.
struct RememberRequest {
  std::string address;
  std::string name;
  bool migrate = false;
};

// Parses the x4mp.remember payload {"v":1,"address":"...","name":"...","migrate":true|false}. Unknown fields (a "password" a
// careless caller adds) are ignored, never read. Control characters are stripped; address <= 255, name <= 64 bytes. nullopt (and
// `error`) when it is not an object or carries neither address nor name.
[[nodiscard]] std::optional<RememberRequest> parse_remember(std::string_view payload, std::string* error = nullptr);

// Applies a request to the file (migrate only fills empty fields). Returns the resulting remembered fields in `after` when given.
[[nodiscard]] WriteResult apply_remember(const std::filesystem::path& path, const RememberRequest& request, Remembered* after = nullptr);

// {"v":1,"address":"...","name":"..."} for the bridge topic x4mp.remembered (empty fields are present as "").
[[nodiscard]] std::string make_remembered_json(const Remembered& remembered);

}  // namespace x4mp::config
