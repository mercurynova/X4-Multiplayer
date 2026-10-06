#pragma once
// core/config: typed configuration (docs/mod-design.md 2.6).
//
// Load order, last wins:
//   1. built-in defaults (Config{})
//   2. the user config file (JSON object), path supplied by the caller (X4Native settings / extension folder)
//   3. a one-shot launch.json written by the launcher: read once, then renamed to "<name>.consumed" (or deleted)
//      so a second load never sees it. Expired requests (expires < now) are consumed but ignored.
// An invalid value keeps the previous (default) value and yields an Error diagnostic that names the KEY only;
// values are never echoed, so secrets cannot leak through diagnostics. Unknown keys yield a Warn diagnostic.
// Nothing in core/ reads environment variables (Steam relaunch drops them); a unit test greps for that.
//
// No exceptions cross this API: load() returns diagnostics instead of throwing.

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "core/log/log.h"

namespace x4mp::config {

// Set by launch.json (auto-connect request). `active` is false when no (valid, unexpired) request was consumed.
struct LaunchRequest {
  bool active = false;
  std::string save;          // optional save to load
  std::string password_ref;  // "prompt" | "inline" | ""
};

// M3-23: diagnostic switches for the "client loses the map knowledge" investigation (docs/in-game-session-4.md "Finding 4 experiments").
// All default to false = no behaviour change. x4mp.json: {"diag": {"takeover_keep_original": true, ...}}; the flat keys
// "diag.takeover_keep_original" etc. are accepted too (the same names the docs use).
struct DiagConfig {
  bool takeover_keep_original = false;  // the takeover runs but never removes the host's ship copy (the save's original player ship)
  bool takeover_off = false;            // no takeover at all: the player stays in the save's ship, the avatar stays unbound
  bool ghosts_off = false;              // the client spawns no ghosts
  bool janitor_off = false;             // the load-time janitor sweep never removes anything
  // M3-28: the map fog flip experiment (docs/spikes/finding-4-map-fog-analysis.md, docs/in-game-session-4.md "M3-28 map fog experiment").
  bool team_self_relation_099 = false;   // the team apply writes +0.99 instead of +1.0 for player <-> own team (H1: +1.0 is the engine's `self` relation range)
  bool team_move_respawn = false;        // authority: a team move despawns the avatar and spawns a fresh one under the new faction instead of SetComponentOwner (H2)
  bool avatars_inert_once = false;       // ActivateObject(false) on avatars / ghosts only at spawn / re-own / rebind, then only after a read-back shows the ship active (H3)
  bool dress_no_radar_no_known = false;  // the MD dress of avatars and ghosts skips set_object_forced_radar_visible and set_known (H4)
  bool no_set_faction_known = false;     // the team apply skips set_faction_known
  [[nodiscard]] bool any() const {
    return takeover_keep_original || takeover_off || ghosts_off || janitor_off || team_self_relation_099 || team_move_respawn || avatars_inert_once ||
           dress_no_radar_no_known || no_set_faction_known;
  }
  bool operator==(const DiagConfig&) const = default;
};

struct Config {
  std::string server_host = "127.0.0.1";
  int tcp_port = 47780;
  log::Level log_level = log::Level::Info;
  std::string log_file;                       // empty: the caller picks the default location
  int log_rate_limit = 5;                     // lines per second per call site (1..1000)
  std::string player_name;                    // empty: decided at join time
  std::string password;                       // SECRET: never logged, see describe()
  std::size_t outbox_byte_cap = 8u * 1024u * 1024u;  // reliable outbox cap in bytes (64 KiB..1 GiB)
  int frame_budget_us = 1500;                 // main-thread budget per frame in microseconds (100..50000), M2-04
  bool selftest = false;                      // run the in-game self-test at on_universe_ready (M2-10)
  // Per-category log levels ("net": "debug"); categories not listed use log_level. Names are validated by the host
  // (known list in host/host_log.h), not here. Last occurrence wins; order is the JSON key order.
  std::vector<std::pair<std::string, log::Level>> log_categories;
  LaunchRequest launch;
  DiagConfig diag;  // M3-23
};

struct Diagnostic {
  log::Level level = log::Level::Error;
  std::string message;  // never contains a configuration value, only key names / sources
};

struct LoadOptions {
  std::filesystem::path user_file;    // may be empty or missing (not an error)
  std::filesystem::path launch_file;  // may be empty or missing
  std::int64_t now_unix = -1;         // for expiry checks; < 0 means "use the system clock"
};

struct LoadResult {
  Config config;
  std::vector<Diagnostic> diagnostics;
  bool launch_consumed = false;  // a launch file existed and was renamed/deleted by this call
};

[[nodiscard]] Config defaults();

// Parses a JSON object, overlaying known keys on the defaults. Unknown keys are ignored; a known key with
// an invalid value keeps its default and is reported in `errors` (when given). Returns nullopt when the
// text is not a JSON object. (Used for the user file; launch.json goes through load().)
[[nodiscard]] std::optional<Config> parse_json(std::string_view text, std::string* errors = nullptr);

// Full layered load. Never throws.
[[nodiscard]] LoadResult load(const LoadOptions& options);

// Writes every diagnostic to the logger at its level (one line each).
void report(const LoadResult& result, log::Logger& logger);

// Human-readable dump for the session header: one "key=value" per line, password shown as <redacted>/<unset>.
[[nodiscard]] std::string describe(const Config& config);

}  // namespace x4mp::config
