#pragma once
// x4mp_probe config (throwaway spike, M2-005). Pure C++: no X4 SDK, unit-tested.
// File: Documents\Egosoft\X4\x4mp\x4mp_probe.json (resolved with SHGetKnownFolderPath; never env vars).
// Parsing never throws and never reports a VALUE (the file may hold a password): diagnostics name keys only.

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace x4mp_probe {

struct Config {
  std::string server = "127.0.0.1:47780";  // "host:port" or "host"; empty = do not connect
  std::string name = "Tester";
  std::string password;                    // SECRET: never logged
  bool connect = true;                     // false = do not start a session
  bool auto_load = true;                   // load the downloaded session save, once per X4 process
  std::string load_mode = "event";         // "event" (raise vanilla Lua event loadSave) | "lua" (shim calls LoadGame)
  bool pin_module = false;                 // GetModuleHandleEx(PIN): the DLL is never unloaded (B4)
  bool hooks = true;                       // install the GetCurrentGameTime / TriggerAutosave hooks
  bool skip_autosave = false;              // TriggerAutosave hook sets skip_original (C2 fallback)
  bool pause_on_ready = true;              // Pause() at on_universe_ready (B3)
  int pause_seconds = 20;
  bool allow_native_thread_calls = false;  // run game/Lua calls from on_native_frame_update if no UI frames tick
  // Actions (acted on once per change, never at startup):
  std::string spike_block;                 // "" = none. "money" also runs the native money test; "reloadui" and
                                           // "pin_on" are handled natively; everything else -> Lua event x4mp_spike.run
  std::int64_t spike_block_seq = 0;        // a change re-runs spike_block (same block twice)
  int reloadui_after_s = 0;                // > 0: ExecuteDebugCommand("reloadui") after N s, once; the probe then rewrites 0
  std::string save_test;                   // non-empty: SaveGame(<name>) via Lua with QPC timing (C4), on seq change
  bool money_test = false;                 // run the native money test on seq change
};

struct ParseResult {
  bool ok = false;                 // the text was a JSON object
  Config config;
  std::vector<std::string> notes;  // key names only
};

[[nodiscard]] ParseResult parse_config(std::string_view text);
// "host:port" / "host" / "[::1]:p" -> (host, port); nullopt if empty or invalid.
[[nodiscard]] std::optional<std::pair<std::string, std::uint16_t>> split_endpoint(std::string_view s);
// One line, no secrets: "server=... name=... password=<set>|<unset> ..."
[[nodiscard]] std::string describe(const Config& c);
// Identity of the spike_block action ("<seq>|<block>"): when it changes the block runs again.
[[nodiscard]] std::string action_token(const Config& c);

// nearest-rank percentile of an unsorted sample (copy is sorted). p in [0,100]. Empty -> 0.
[[nodiscard]] double percentile(std::vector<double> samples, double p);

// Documents\Egosoft\X4\x4mp (SHGetKnownFolderPath); empty on failure.
[[nodiscard]] std::filesystem::path default_config_dir();

}  // namespace x4mp_probe
