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

// Session-4 sitting-0 spike (S13) parameters (M3-001). All optional; names in the JSON are the field names.
struct S13Config {
  std::string scratch_slot;                                      // save name the takeover/persist blocks require ("" = they refuse)
  std::string ghost_macro_s = "ship_arg_s_fighter_01_a_macro";   // S ghost
  std::string ghost_macro_m = "ship_arg_m_bomber_01_a_macro";    // M ghost
  std::string ghost_faction = "x4mp_team_2";                     // owner of the ghosts when this faction exists
  std::string ghost_fallback_faction = "ownerless";              // owner otherwise (never "player")
  std::string takeover_macro = "ship_arg_s_fighter_01_a_macro";  // the player-owned ship of the takeover blocks
  std::string xsector_name;                                     // target sector for ghost_xsector: substring of its name ("" = pick one: same cluster first)
  int spawn_distance_m = 1000;                                   // ghost spawn distance ahead of the player's ship
  int takeover_distance_m = 300;
  int drift_seconds = 60;                                        // ghost_spawn drift sampling
  int motion_seconds = 20;                                       // per path segment of ghost_motion
  int sample_seconds = 120;                                      // sample
  int sample_hz = 20;
  int seat_seconds = 60;
  int seta_seconds = 120;
  int pause_wait_seconds = 60;                                   // pause_move: how long to wait for the user to pause
  int xsector_hold_seconds = 20;
  int pitch_sign = 1;                                            // 1 or -1: sign of pitch in the forward vector (convention check)
  bool angles_in_radians = true;                                 // GetObjectPositionInSector angles are radians (x4n_math.h); SetObjectSectorPos wants degrees
};

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
  S13Config s13;                           // M3-001 blocks (ghost_spawn, ghost_motion, sample, ...)
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
