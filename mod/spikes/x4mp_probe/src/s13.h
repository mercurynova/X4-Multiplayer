#pragma once
// x4mp_probe S13 blocks (M3-001): the native half of the session-4 sitting-0 spikes (docs/m3-plan.md section 5).
// Throwaway. Interface to probe.cpp plus the pure helpers (unit-tested without the game). Block names, parameters and
// events: ../README.md ("S13 blocks"). Threading: start()/tick() run on the on_frame_update thread only.

#include <array>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

#include "probe_config.h"

namespace x4mp_probe::s13 {

// ---- Pure helpers ------------------------------------------------------------------------------------------------

struct Vec3 {
  double x = 0, y = 0, z = 0;
};
inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline Vec3 operator-(Vec3 a, Vec3 b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline Vec3 operator*(Vec3 a, double k) { return {a.x * k, a.y * k, a.z * k}; }
inline double length(Vec3 a) { return std::sqrt(a.x * a.x + a.y * a.y + a.z * a.z); }
inline double distance(Vec3 a, Vec3 b) { return length(a - b); }
inline Vec3 lerp(Vec3 a, Vec3 b, double t) { return a + (b - a) * t; }

constexpr double kPi = 3.14159265358979323846;
constexpr double kRadToDeg = 180.0 / kPi;

// Forward vector for (yaw, pitch) given in RADIANS. X4 convention assumed: y up, yaw 0 faces +z. `pitch_sign` (+1/-1) is
// the config convention check: if the ghosts appear above/below instead of ahead, flip it.
Vec3 forward_from_angles(double yaw_rad, double pitch_rad, int pitch_sign);
Vec3 right_from_yaw(double yaw_rad);

// Paths (all return a position and, via velocity(), the exact derivative in m/s).
struct Path {
  enum class Kind { Circle, Line } kind = Kind::Circle;
  Vec3 center;       // circle: centre; line: the start point (t = 0)
  double radius = 1000.0;  // circle
  double speed = 100.0;    // m/s (circle: tangential)
  Vec3 dir{1, 0, 0};       // line direction (unit)
};
Vec3 path_position(const Path& p, double t_s);
Vec3 path_velocity(const Path& p, double t_s, double duration_s);

// Block string: "ghost_motion a", "ghost_motion_a", "GHOST_MOTION  B" -> {"ghost_motion", "a"}. Lower-cased, trimmed.
struct BlockName {
  std::string name;
  std::string arg;
};
BlockName parse_block(std::string_view s);
// True when `name` (already parsed) is one of the S13 native blocks.
bool is_s13_block(std::string_view name);

// "save_007", "save_007.xml.gz", "SAVE_007.xml", " save_007 " -> "save_007" (lower-cased). Used to compare the current
// save with the configured scratch slot.
std::string normalize_save_name(std::string_view s);
bool is_scratch_slot(std::string_view current, std::string_view configured);  // false when configured is empty

// Copy of the product guard logic (native/game/safe_remove.cpp + player_guard.cpp, M2-04): never remove 0 or an id in the
// guard set. The probe re-collects the guard from the game right before every removal.
enum class RemoveVerdict { Allowed, BlockedInvalidId, BlockedGuard, BlockedNotValid };
struct RemoveGate {
  std::vector<std::uint64_t> guard;
  [[nodiscard]] RemoveVerdict check(std::uint64_t id, bool game_says_valid) const;
};
const char* to_string(RemoveVerdict v);

// Keyframe interpolation used by motion mode (a)/(c): keys at t_k = k * key_dt; returns the position at display time t.
Vec3 interpolate_keys(const std::vector<Vec3>& keys, double key_dt_s, double t_s);

// ---- Interface to probe.cpp -------------------------------------------------------------------------------------

struct Env {
  // level: 1 info, 2 warn, 3 error. `body` has no tag; probe.cpp adds "[X4MP-PROBE] t= tid= s13 ".
  void (*log)(int level, const std::string& body) = nullptr;
  std::filesystem::path dir;  // Documents\Egosoft\X4\x4mp (or the test override)
};
void set_env(const Env& env);

bool handles(std::string_view block);                    // is `block` an S13 block (any case, with args)
bool start(const Config& cfg, std::string_view block);   // false: not an S13 block. UI thread.
void tick();                                             // every on_frame_update. UI thread. Cheap when idle.
void native_tick();                                      // every on_native_frame_update (any thread): a counter only
void shutdown();                                         // DLL unload: abandon the active block, log it
[[nodiscard]] bool active();
[[nodiscard]] std::string active_name();

}  // namespace x4mp_probe::s13
