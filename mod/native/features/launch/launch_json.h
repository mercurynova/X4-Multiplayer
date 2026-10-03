#pragma once
// features/launch/launch_json: the one-shot launch.json auto-connect request (M2-12; docs/mod-design.md 2.6).
//
//   {"server":"host[:port]","name":"Pilot","password":"...","role":"client|authority","admin_password":"...",
//    "expires_utc":"2026-10-03T12:00:00Z"}
//
// Only "server" and "name" are required. "expires_utc" (ISO 8601, UTC, "Z" suffix) is optional: a request past it is ignored.
// The launcher is expected to write it; the file is deleted on first read regardless. SDK-free so Catch2 tests it directly.
// Secrets (password, admin_password) never appear in any text this module produces except to_join_payload(), which is the x4mp.join
// body handed to the join flow.

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>

namespace x4mp::features::launch {

struct LaunchRequest {
  std::string server;          // "host", "host:port", "[::1]:port"
  std::string name;
  std::string password;        // SECRET
  std::string admin_password;  // SECRET
  bool want_authority = false;
  std::optional<std::int64_t> expires_unix;  // from expires_utc
};

enum class LaunchStatus { Ok, Invalid, Expired };

struct LaunchParse {
  LaunchStatus status = LaunchStatus::Invalid;
  LaunchRequest request;  // filled for Ok; for Expired the secrets are already wiped
  std::string error;      // short machine code ("bad_json", "no_server", "no_name", "bad_expires", "expired" ...); never a value
};

// ISO 8601 UTC "YYYY-MM-DDTHH:MM:SS[.fff]Z" -> unix seconds. false when malformed.
[[nodiscard]] bool parse_utc_timestamp(std::string_view text, std::int64_t& unix_seconds);

// Parses and validates; `now_unix` decides expiry (expires_unix < now is expired).
[[nodiscard]] LaunchParse parse_launch(std::string_view text, std::int64_t now_unix);

// The x4mp.join JSON body the UI would send (parsed by join::parse_join). Contains the secrets.
[[nodiscard]] std::string to_join_payload(const LaunchRequest& request);

// One log-safe line: server, name, role, whether a password / admin password is present, expiry. Never the secrets.
[[nodiscard]] std::string describe(const LaunchRequest& request);

struct ConsumeResult {
  bool existed = false;
  bool removed = false;   // the file is gone afterwards (or was never there)
  bool scrubbed = false;  // removal failed but the content was overwritten with nothing
  LaunchParse parse;      // meaningful when `existed`
};

// Reads `path`, deletes it IMMEDIATELY (before parsing, so an invalid or expired file is gone too), then parses. When the file cannot
// be removed its content is truncated instead so the secrets do not stay on disk. Never throws.
[[nodiscard]] ConsumeResult consume_launch_file(const std::filesystem::path& path, std::int64_t now_unix);

// Overwrites the secrets in place (best effort) and clears them.
void wipe(LaunchRequest& request);

}  // namespace x4mp::features::launch
