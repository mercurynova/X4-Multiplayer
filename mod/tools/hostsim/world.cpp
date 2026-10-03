#include "world.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace hostsim {
namespace {
constexpr double kPi = 3.14159265358979323846;
constexpr double kRadToDeg = 180.0 / kPi;

UIPosRot make(const Vec3& p, double yaw_deg) {
  UIPosRot u{};
  u.x = static_cast<float>(p.x);
  u.y = static_cast<float>(p.y);
  u.z = static_cast<float>(p.z);
  u.yaw = static_cast<float>(yaw_deg);
  return u;
}
double heading_deg(double dx, double dz) { return std::atan2(dx, dz) * kRadToDeg; }  // 0 = +z, 90 = +x
}  // namespace

std::optional<Vec3> parse_vec3(const std::string& s) {
  Vec3 v;
  char* end = nullptr;
  const char* p = s.c_str();
  double* out[3] = {&v.x, &v.y, &v.z};
  for (int i = 0; i < 3; ++i) {
    *out[i] = std::strtod(p, &end);
    if (end == p) return std::nullopt;
    p = end;
    if (i < 2) {
      if (*p != ',') return std::nullopt;
      ++p;
    }
  }
  if (*p != '\0') return std::nullopt;
  return v;
}

World::World() {
  sectors_ = {kSector1, kSector2};
  Obj ship;
  ship.id = kPlayerShip;
  ship.macro = "ship_arg_m_fighter_01_a_macro";  // hostsim's stand-in; the real macro depends on the save
  ship.owner = "player";
  ship.name = "Player Ship";
  ship.idcode = "HSP-001";
  ship.sector = kSector1;
  objs_[ship.id] = ship;
  Obj st;
  st.id = kStation;
  st.cls = "station";
  st.macro = "station_gen_dock_01_macro";
  st.owner = "argon";
  st.name = "Hostsim Dock";
  st.idcode = "HSS-001";
  st.sector = kSector1;
  objs_[st.id] = st;
}

std::uint64_t World::spawn(const std::string& macro, std::uint64_t sector, const UIPosRot& pos, const std::string& owner) {
  if (macro.empty() || !has_sector(sector)) return 0;
  if (spawn_fail_budget > 0) {
    --spawn_fail_budget;
    return 0;
  }
  Obj o;
  o.id = next_id_++;
  o.macro = macro;
  o.owner = owner;
  o.sector = sector;
  o.pos = pos;
  char code[24];
  std::snprintf(code, sizeof(code), "HS%c-%03llu", static_cast<char>('A' + o.id % 26), static_cast<unsigned long long>(o.id % 1000));
  o.idcode = code;
  o.cls = macro.rfind("station", 0) == 0 ? "station" : "ship";
  objs_[o.id] = o;
  last_ = o.id;
  ++spawns;
  return o.id;
}

Obj* World::find(std::uint64_t id) {
  const auto it = objs_.find(id);
  return it == objs_.end() ? nullptr : &it->second;
}
bool World::exists(std::uint64_t id) const { return objs_.count(id) != 0; }
std::size_t World::count() const { return objs_.size(); }

bool World::remove(std::uint64_t id) {
  if (id == player_ship || id == kStation) return false;  // the player's own ship is never removed (project hard rule)
  if (objs_.erase(id) == 0) return false;
  ++removed;
  return true;
}

std::vector<Obj> World::snapshot() const {
  std::vector<Obj> out;
  out.reserve(objs_.size());
  for (const auto& kv : objs_) out.push_back(kv.second);
  return out;
}

std::uint64_t World::context(std::uint64_t id, const std::string& cls, bool include_self) {
  if (id == kPlayerEntity) {  // the player character sits in the ship (seat) or in the station / ship room (otherwise)
    id = seat ? player_ship : container();
    include_self = true;
  }
  const Obj* o = find(id);
  if (cls == "sector") return o ? o->sector : (has_sector(id) ? id : 0);
  if (!o) return 0;
  if (include_self && o->cls == cls) return o->id;
  if (cls == "station" && id == player_ship && docked) return kStation;
  return 0;
}

std::string World::can_teleport(std::uint64_t id) {
  const Obj* o = find(id);
  if (!o) return "hostsim: no such object";
  if (o->cls != "ship") return "hostsim: not a ship";
  if (!teleport_allowed) return teleport_reason;
  return "";
}

bool World::teleport(std::uint64_t id, bool allow_controlling) {
  if (!can_teleport(id).empty()) return false;
  if (allow_controlling) {
    player_ship = id;
    seat = true;
    docked = false;
  } else {
    seat = false;  // the player stands next to it; control stays where it was
  }
  ++teleports;
  return true;
}

void World::start_path(const Path& p) {
  path_ = p;
  Obj* s = find(player_ship);
  if (!s) return;
  if (p.sector != 0) s->sector = p.sector;
  switch (p.kind) {
    case PathKind::Circle:
      path_.angle = 0;
      s->pos = make({p.center.x + p.radius, p.center.y, p.center.z}, 0);
      break;
    case PathKind::Line:
    case PathKind::Gate:
      path_.angle = 0;
      s->pos = make(p.from, heading_deg(p.to.x - p.from.x, p.to.z - p.from.z));
      break;
    case PathKind::None: break;
  }
}

void World::advance(double dt) {
  if (path_.kind == PathKind::None || dt <= 0) return;
  Obj* s = find(player_ship);
  if (!s) return;
  if (path_.kind == PathKind::Circle) {
    path_.angle += dt * path_.speed / path_.radius;
    const double a = path_.angle;
    const Vec3 p{path_.center.x + path_.radius * std::cos(a), path_.center.y, path_.center.z + path_.radius * std::sin(a)};
    // tangent of (cos a, sin a) in the x-z plane is (-sin a, cos a)
    s->pos = make(p, heading_deg(-std::sin(a), std::cos(a)));
    return;
  }
  const double dx = path_.to.x - path_.from.x, dy = path_.to.y - path_.from.y, dz = path_.to.z - path_.from.z;
  const double len = std::sqrt(dx * dx + dy * dy + dz * dz);
  path_.angle += dt * path_.speed;
  if (len <= 0 || path_.angle >= len) {
    if (path_.kind == PathKind::Gate) {
      if (path_.to_sector != 0) s->sector = path_.to_sector;
      s->pos = make(path_.exit, heading_deg(dx, dz));
      ++gate_jumps_;
      path_.kind = PathKind::None;
    } else if (path_.loop) {
      path_.angle = std::fmod(path_.angle, len > 0 ? len : 1.0);
    } else {
      s->pos = make(path_.to, heading_deg(dx, dz));
      path_.kind = PathKind::None;
    }
    if (path_.kind == PathKind::None) return;
  }
  const double t = len > 0 ? path_.angle / len : 0;
  s->pos = make({path_.from.x + dx * t, path_.from.y + dy * t, path_.from.z + dz * t}, heading_deg(dx, dz));
}

}  // namespace hostsim
