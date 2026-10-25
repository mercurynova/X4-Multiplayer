#pragma once
// features/onfoot: the remote-avatar pipeline (M3b W4; docs/research/on-foot-presence.md 3.5, docs/mod-design.md 4.12).
//
// Native cannot create an actor and cannot read a room, MD can do both but cannot see the network. So the work is split:
//
//   native (this class)  decides WHO is visible (same container, same quantised room key, on foot) and WHERE they stand right now,
//                        and raises one JSON batch over the Lua bridge -> MD spawns/removes and replies with the actor id;
//   MD                   create_cue_actor + add_actor_to_room, then nothing else: the pose is not animated by MD.
//   native (per frame)   once the id is known, SetPositionalOffset moves the actor every frame (S10.4: p95 0.16 m at ~30 Hz, no
//                        measurable frame cost; a walk animation is NOT available - MD has no locomotion action, so v1 shows the idle pose).
//
// This class is pure logic: no game calls, no flatbuffers, no JSON library. The caller injects the two side effects (raise_lua, place),
// which is what makes every rule below testable without a game.
//
// Clock: `now_us` must be the SAME clock as RemoteAvatar::at_us (the server clock), because the interpolator compares them.

#include <cstdint>
#include <functional>
#include <map>
#include <string>
#include <string_view>

#include "core/ghost/interpolator.h"
#include "features/onfoot/onfoot_types.h"

namespace x4mp::features::onfoot {

// The two side effects. `raise_lua` gets the whole batch as one JSON string (bridge verb x4mp.onfoot_actors):
//   {"v":1,"spawn":[{"pid":3,"name":"Ann","race":"argon","rmacro":123,"ax":-20598,"ay":-11760,"az":2,"kind":1,
//                    "x":1.25,"y":0.0,"z":-3.5,"yaw":90.0}],"despawn":[7]}
// spawn x/y/z are CONTAINER-local metres with y ALREADY at foot height (add_actor_to_room places in container space, like the spike did),
// yaw is DEGREES (MD rotations are degrees). `place` is SetPositionalOffset: container-local metres, y at foot height, yaw RADIANS.
struct ActorCallbacks {
  std::function<void(std::string_view json)> raise_lua;
  std::function<void(std::uint64_t actor, float x, float y, float z, float yaw)> place;
  std::function<std::string(std::uint16_t player_id)> name;  // roster name; empty -> "Player <id>"
};

class ActorRegistry {
 public:
  // The player entity reports EYE height (S10.4/S10.5); an actor's origin is at its feet, so every write subtracts this.
  static constexpr float kEyeToFoot = 1.72f;
  static constexpr std::int64_t kSpawnRetryUs = 2'000'000;  // an unanswered spawn request is repeated this often
  static constexpr std::int64_t kStaleUs = 5'000'000;       // no state for this long: remove the avatar

  void set_callbacks(ActorCallbacks cb) { cb_ = std::move(cb); }
  [[nodiscard]] bool has_callbacks() const noexcept { return static_cast<bool>(cb_.raise_lua); }

  // One frame. `local` is the last room context MD pushed, `remotes` the hub's view, `appearances` its look table.
  // Never throws; a missing callback simply skips that side effect (the entry still tracks the state).
  void update(const RoomContext& local, const std::map<std::uint16_t, RemoteAvatar>& remotes,
              const std::map<std::uint16_t, Appearance>& appearances, std::int64_t now_us);

  // MD's answer over the bridge: "spawn;<pid>;<actor id>" or "gone;<pid>" (the actor was destroyed: the janitor, a room unload, ...).
  void on_reply(std::string_view reply);

  // Session end / save loaded: forget every entry and ask MD to remove whatever it still has.
  void clear();

  struct Counters {
    std::uint32_t spawns = 0;      // spawn commands sent
    std::uint32_t despawns = 0;    // despawn commands sent
    std::uint32_t places = 0;      // SetPositionalOffset writes
    std::uint32_t replies = 0;     // well-formed MD answers
    std::uint32_t bad_replies = 0;
    std::uint32_t ignored = 0;     // states for a player who is not visible here
  };
  [[nodiscard]] const Counters& counters() const noexcept { return counters_; }

  [[nodiscard]] std::size_t tracked() const noexcept { return entries_.size(); }  // players this frame wanted to see
  [[nodiscard]] std::size_t live() const noexcept;                                 // ... of which MD confirmed the actor
  [[nodiscard]] bool actor_of(std::uint16_t player_id, std::uint64_t& out) const noexcept;

  void reset() { *this = ActorRegistry{}; }

 private:
  struct Entry {
    RemoteAvatar avatar;
    ghost::Interpolator interp;
    std::uint64_t actor = 0;
    bool requested = false;   // asked MD, no answer yet
    bool live = false;        // actor id known, being moved
    bool seen = false;        // the hub still lists this player in the current frame
    std::int64_t last_request_us = 0;
    std::int64_t last_seen_us = 0;  // server time of the newest STATE (a repeated frame is not a sign of life)
  };

  void append_despawn(std::string& out, std::uint16_t player_id);
  void send_batch(const std::string& spawns, const std::string& despawns);

  ActorCallbacks cb_;
  std::map<std::uint16_t, Entry> entries_;
  Counters counters_;
};

[[nodiscard]] ActorRegistry& onfoot_actors() noexcept;

}  // namespace x4mp::features::onfoot
