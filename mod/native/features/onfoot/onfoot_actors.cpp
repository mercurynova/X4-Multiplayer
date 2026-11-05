#include "features/onfoot/onfoot_actors.h"

#include <charconv>
#include <cmath>
#include <system_error>

namespace x4mp::features::onfoot {

namespace {

constexpr double kRadToDegD = 57.295779513082320876798154814105;

[[nodiscard]] bool on_foot(const RemoteAvatar& a) noexcept { return a.mode == Mode::Walking || a.mode == Mode::InTransit; }

// The wire's px/py/pz are ROOM space (1/1024 m, finer than the 1/64 m of cx/cy/cz). Both add_actor_to_room and SetPositionalOffset take
// an offset RELATIVE TO THE ACTOR'S PARENT, and an avatar created by add_actor_to_room is parented to the room - so room space is passed
// through unchanged. Adding the anchor here (as an earlier version did) put every avatar a whole room anchor away from the player.
// S10.4 measured the spike with the same room-local values: p95 0.163 m. same_room() guarantees the two clients agree on the room.

// Locale-independent number text (std::to_chars, not printf: a Russian Windows would print a decimal comma and break the JSON).
void append_fixed(std::string& out, double v, int decimals) {
  if (!std::isfinite(v)) v = 0.0;
  char buf[64];
  const auto res = std::to_chars(buf, buf + sizeof buf, v, std::chars_format::fixed, decimals);
  if (res.ec == std::errc{}) {
    out.append(buf, res.ptr);
  } else {
    out.push_back('0');
  }
}

void append_int(std::string& out, long long v) {
  char buf[24];
  const auto res = std::to_chars(buf, buf + sizeof buf, v);
  if (res.ec == std::errc{}) out.append(buf, res.ptr);
}

void append_json_string(std::string& out, std::string_view s) {
  static constexpr char kHex[] = "0123456789abcdef";
  out.push_back('"');
  for (const char raw : s) {
    const auto c = static_cast<unsigned char>(raw);
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (c < 0x20) {
          out += "\\u00";
          out.push_back(kHex[(c >> 4) & 0x0f]);
          out.push_back(kHex[c & 0x0f]);
        } else {
          out.push_back(raw);  // UTF-8 passes through: valid JSON, and Lua's decoder reads the bytes back unchanged
        }
    }
  }
  out.push_back('"');
}

[[nodiscard]] std::string_view trim(std::string_view s) noexcept {
  while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\r' || s.front() == '\n')) s.remove_prefix(1);
  while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r' || s.back() == '\n')) s.remove_suffix(1);
  return s;
}

[[nodiscard]] bool parse_u16(std::string_view s, std::uint16_t& out) noexcept {
  s = trim(s);
  if (s.empty()) return false;
  unsigned value = 0;
  const auto res = std::from_chars(s.data(), s.data() + s.size(), value);
  if (res.ec != std::errc{} || res.ptr != s.data() + s.size() || value > 0xffffu) return false;
  out = static_cast<std::uint16_t>(value);
  return true;
}

[[nodiscard]] bool parse_u64(std::string_view s, std::uint64_t& out) noexcept {
  s = trim(s);
  if (s.empty()) return false;
  // MD prints a component id in hex ("0x18ead5") and Lua hands the reply through unchanged, so both forms arrive here.
  // std::from_chars has no prefix handling: strip the 0x and pick base 16 ourselves.
  int base = 10;
  if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
    base = 16;
    s.remove_prefix(2);
    if (s.empty()) return false;
  }
  std::uint64_t value = 0;
  const auto res = std::from_chars(s.data(), s.data() + s.size(), value, base);
  if (res.ec != std::errc{} || res.ptr != s.data() + s.size()) return false;
  out = value;
  return true;
}

}  // namespace

ActorRegistry& onfoot_actors() noexcept {
  static ActorRegistry registry;
  return registry;
}

void ActorRegistry::update(const RoomContext& local, const std::map<std::uint16_t, RemoteAvatar>& remotes,
                           const std::map<std::uint16_t, Appearance>& appearances, std::int64_t now_us) {
  const bool local_ok = local.valid && local.mode != Mode::None && local.container_net_id != 0;

  // 1. Ingest. A new seq / sample time becomes an interpolation sample in ROOM space: px/py/pz are 1/1024 m, finer than cx/cy/cz (1/64 m).
  //    The anchor is added back only when the actor is written (container_axis), so the interpolation stays in the fine space.
  //    Velocity is unknown on foot, so the interpolator runs the linear branch.
  for (const auto& [pid, avatar] : remotes) {
    if (avatar.mode == Mode::None) continue;
    Entry& e = entries_[pid];
    if (e.last_seen_us == 0) e.last_request_us = now_us - kSpawnRetryUs;  // the first spawn of a new entry is immediate
    const bool fresh = e.last_seen_us == 0 || avatar.seq != e.avatar.seq || avatar.at_us != e.avatar.at_us;
    if (fresh) {
      ghost::Sample s;
      s.t_us = avatar.at_us != 0 ? avatar.at_us : now_us;
      s.flags = (avatar.flags & 0x1u) != 0 ? ghost::kTeleport : std::uint16_t{0};  // on-foot bit 0 = teleport / room change
      s.pos = ghost::Vec3{avatar.px, avatar.py, avatar.pz};
      s.rot = ghost::Euler{avatar.yaw, avatar.pitch, 0.0};
      e.interp.push(s, now_us);
      e.last_seen_us = now_us;  // only a new state proves the sender is alive: a frame repeated from the hub's cache is not
    }
    e.avatar = avatar;
    e.seen = true;
    if (!local_ok || !on_foot(avatar) || !same_container(avatar, local)) ++counters_.ignored;
  }

  // 2. Decide: who is wanted here and now, who has to go, who has to move.
  std::string spawns;
  std::string despawns;
  for (auto it = entries_.begin(); it != entries_.end();) {
    const auto pid = it->first;
    Entry& e = it->second;
    // M3c: any room of the shared container, not only the viewer's own (Tier 2). An unknown room key cannot be placed, so it is
    // no actor at all - the HUD still lists the player (fallback F3).
    const bool visible = e.seen && local_ok && on_foot(e.avatar) && same_container(e.avatar, local) && e.avatar.room.known() &&
                         now_us - e.last_seen_us <= kStaleUs;
    e.seen = false;  // the next update sets it again if the hub still lists the player
    if (!visible) {
      if (e.requested || e.live) {
        append_despawn(despawns, pid);
        ++counters_.despawns;
      }
      if (e.walking && cb_.stop_walk) cb_.stop_walk(pid);  // never leave an actor walking to a slot after it is gone
      it = entries_.erase(it);
      continue;
    }
    // M3c: the actor lives in another room than the player does now. MD created it in the old room and the engine keeps an actor
    // inside the room it was placed in, so the player who walked through a door has to be removed and created again.
    if (e.live && !(e.avatar.room == e.placed_room)) {
      append_despawn(despawns, pid);
      ++counters_.despawns;
      if (e.walking && cb_.stop_walk) cb_.stop_walk(pid);
      e.walking = false;
      e.live = false;
      e.requested = false;
      e.actor = 0;
      e.last_request_us = now_us - kSpawnRetryUs;  // ask again in this same frame
    }
    if (!e.live) {
      if (now_us - e.last_request_us >= kSpawnRetryUs) {
        const auto pose = e.interp.render(now_us);
        const bool have_pose = pose.visible();
        // Room space, foot height: add_actor_to_room positions inside the room it is given, exactly like the spike's of_place.
        const double x = have_pose ? pose.pos.x : e.avatar.px;
        const double y = (have_pose ? pose.pos.y : e.avatar.py) - kEyeToFoot;  // origin at the feet
        const double z = have_pose ? pose.pos.z : e.avatar.pz;
        const double yaw = (have_pose ? pose.rot.yaw : e.avatar.yaw) * kRadToDegD;

        std::string name = cb_.name ? cb_.name(pid) : std::string();
        if (name.empty()) {
          name = "Player ";
          append_int(name, pid);
        }
        std::string race;
        std::string macro;
        if (const auto a = appearances.find(pid); a != appearances.end()) {
          race = a->second.race;
          macro = a->second.character_macro;  // ADR-051: a curated macro beats a race-only <select> (S10.7)
        }

        // The room macro id, not its hash: MD matches $Rm.macro.id. The viewer's own id is the fast path; another room comes from
        // the catalogue MD pushed for this container.
        std::string rmacro;
        if (e.avatar.room.macro_ref == local.room_macro_ref) {
          rmacro = local.room_macro;
        } else if (cb_.room_id) {
          rmacro = cb_.room_id(e.avatar.room.macro_ref);
        }
        if (rmacro.empty()) {
          ++counters_.no_room;  // F3: no actor, the presence list still names the player. Retried, the catalogue may arrive late.
          e.last_request_us = now_us;
          ++it;
          continue;
        }

        if (!spawns.empty()) spawns.push_back(',');
        spawns += "{\"pid\":";
        append_int(spawns, pid);
        spawns += ",\"name\":";
        append_json_string(spawns, name);
        spawns += ",\"race\":";
        append_json_string(spawns, race);
        spawns += ",\"macro\":";
        append_json_string(spawns, macro);
        spawns += ",\"rmacro\":";
        append_json_string(spawns, rmacro);  // MD matches $Rm.macro.id against this string; a hash would never match
        spawns += ",\"ax\":";
        append_int(spawns, e.avatar.room.anchor_x);
        spawns += ",\"ay\":";
        append_int(spawns, e.avatar.room.anchor_y);
        spawns += ",\"az\":";
        append_int(spawns, e.avatar.room.anchor_z);
        spawns += ",\"kind\":";
        append_int(spawns, e.avatar.room.kind);
        spawns += ",\"x\":";
        append_fixed(spawns, x, 3);
        spawns += ",\"y\":";
        append_fixed(spawns, y, 3);
        spawns += ",\"z\":";
        append_fixed(spawns, z, 3);
        spawns += ",\"yaw\":";
        append_fixed(spawns, yaw, 2);
        spawns.push_back('}');

        e.requested = true;
        e.placed_room = e.avatar.room;
        e.last_request_us = now_us;
        ++counters_.spawns;
      }
      ++it;
      continue;
    }

    // Live: MD owns the actor, this frame decides where it stands - except while the remote is walking, where the animation is
    // worth more than the centimetres: a slot walk is started and native stops writing the pose for kWalkHoldUs so the engine's own
    // locomotion keeps the actor on its navmesh. On the way back to Idle the walk is stopped and exact placement resumes (the idle
    // pose is what S10.4 mode D showed the override produces anyway).
    const auto pose = e.interp.render(now_us);
    if (pose.visible()) {
      const bool wants_walk = e.avatar.anim == Anim::Walk || e.avatar.anim == Anim::Run;
      if (wants_walk && cb_.walk) {
        if (!e.walking || now_us - e.last_walk_us >= kWalkCmdUs) {
          cb_.walk(pid, static_cast<float>(pose.speed_mps));
          e.last_walk_us = now_us;
          ++counters_.walks;
        }
        e.walking = true;
      } else if (!wants_walk && e.walking) {
        if (cb_.stop_walk) cb_.stop_walk(pid);
        e.walking = false;
        ++counters_.stops;
      }
      if (cb_.place && !(e.walking && now_us - e.last_walk_us < kWalkHoldUs)) {
        // Room space again: the actor is parented to the room, so SetPositionalOffset is a room-local offset (S10.4 mode C).
        cb_.place(e.actor, static_cast<float>(pose.pos.x), static_cast<float>(pose.pos.y - kEyeToFoot),
                  static_cast<float>(pose.pos.z), static_cast<float>(pose.rot.yaw));
        ++counters_.places;
      }
      // Measurement for the M3b acceptance run: the avatar is drawn this many metres behind the newest sample.
      if (cb_.diag && now_us - e.last_diag_us >= kDiagUs) {
        const auto behind_s = static_cast<double>(pose.render_t_us - pose.represented_t_us) / 1e6;
        // How far the local player stands from this avatar, in the same room space: proves the two players share a room even when
        // the camera is not pointed at the avatar.
        const double dx = pose.pos.x - self_x_;
        const double dy = pose.pos.y - self_y_;
        const double dz = pose.pos.z - self_z_;
        const auto dist_m = self_known_ ? static_cast<float>(std::sqrt(dx * dx + dy * dy + dz * dz)) : -1.0f;
        cb_.diag(pid, static_cast<float>(pose.speed_mps * behind_s), static_cast<float>(pose.speed_mps),
                 now_us - e.avatar.at_us, dist_m);
        e.last_diag_us = now_us;
      }
    }
    ++it;
  }

  send_batch(spawns, despawns);
}

void ActorRegistry::on_reply(std::string_view reply) {
  const auto first = reply.find(';');
  if (first == std::string_view::npos) {
    ++counters_.bad_replies;
    return;
  }
  const auto kind = trim(reply.substr(0, first));
  const auto rest = reply.substr(first + 1);
  const auto second = rest.find(';');
  const auto pid_text = second == std::string_view::npos ? rest : rest.substr(0, second);
  const auto value_text = second == std::string_view::npos ? std::string_view{} : rest.substr(second + 1);

  std::uint16_t pid = 0;
  if (!parse_u16(pid_text, pid) || pid == 0) {
    ++counters_.bad_replies;
    return;
  }
  const auto it = entries_.find(pid);
  if (it == entries_.end()) return;  // a late answer for a player who already left: nothing to do

  Entry& e = it->second;
  if (kind == "spawn") {
    std::uint64_t actor = 0;
    if (!parse_u64(value_text, actor) || actor == 0) {
      ++counters_.bad_replies;
      return;
    }
    e.actor = actor;
    e.live = true;
    e.requested = false;
    ++counters_.replies;
  } else if (kind == "gone") {
    // MD destroyed the actor on its own (janitor, room unload). Forget it; the retry throttle keeps this from becoming a loop.
    e.actor = 0;
    e.live = false;
    e.requested = false;
    e.last_request_us = e.last_seen_us;
    ++counters_.replies;
  } else {
    ++counters_.bad_replies;
  }
}

void ActorRegistry::clear() {
  if (entries_.empty()) return;
  std::string despawns;
  for (const auto& [pid, e] : entries_) {
    if (e.requested || e.live) append_despawn(despawns, pid);
    if (e.walking && cb_.stop_walk) cb_.stop_walk(pid);
  }
  entries_.clear();
  send_batch(std::string(), despawns);
}

std::size_t ActorRegistry::live() const noexcept {
  std::size_t n = 0;
  for (const auto& [pid, e] : entries_) {
    if (e.live) ++n;
  }
  return n;
}

bool ActorRegistry::actor_of(std::uint16_t player_id, std::uint64_t& out) const noexcept {
  const auto it = entries_.find(player_id);
  if (it == entries_.end() || !it->second.live) return false;
  out = it->second.actor;
  return true;
}

void ActorRegistry::append_despawn(std::string& out, std::uint16_t player_id) {
  if (!out.empty()) out.push_back(',');
  append_int(out, player_id);
}

void ActorRegistry::send_batch(const std::string& spawns, const std::string& despawns) {
  if (spawns.empty() && despawns.empty()) return;
  if (!cb_.raise_lua) return;
  std::string json;
  json.reserve(64 + spawns.size() + despawns.size());
  json += "{\"v\":1,\"spawn\":[";
  json += spawns;
  json += "],\"despawn\":[";
  json += despawns;
  json += "]}";
  cb_.raise_lua(json);
}

}  // namespace x4mp::features::onfoot
