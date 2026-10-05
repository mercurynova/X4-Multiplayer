#include "features/avatars/avatar_plan.h"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <sstream>

namespace x4mp::features::avatars {

namespace {
constexpr double kGoldenAngle = 2.399963229728653;  // rad
constexpr double kGoldenFrac = 0.6180339887498949;

std::string clean_field(std::string_view s, std::size_t max_len) {
  std::string out;
  for (const char c : s) {
    if (out.size() >= max_len) break;
    if (c == '|' || c == '\n' || c == '\r' || static_cast<unsigned char>(c) < 0x20) continue;
    out.push_back(c);
  }
  return out;
}

bool parse_u(std::string_view s, std::uint64_t& out) {
  if (s.empty()) return false;
  const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
  return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}

bool parse_d(std::string_view s, double& out) {
  if (s.empty()) return false;
  const std::string tmp(s);
  char* end = nullptr;
  out = std::strtod(tmp.c_str(), &end);
  return end != nullptr && *end == '\0' && std::isfinite(out);
}

std::vector<std::string_view> split_bar(std::string_view line) {
  std::vector<std::string_view> parts;
  std::size_t pos = 0;
  while (true) {
    const auto i = line.find('|', pos);
    if (i == std::string_view::npos) {
      parts.push_back(line.substr(pos));
      break;
    }
    parts.push_back(line.substr(pos, i - pos));
    pos = i + 1;
  }
  return parts;
}

std::string fmt_double(double v) {
  char buf[40];
  const auto r = std::to_chars(buf, buf + sizeof(buf), v);
  return std::string(buf, r.ptr);
}
}  // namespace

double distance_m(const Pose& a, const Pose& b) noexcept {
  const double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
  return std::sqrt(dx * dx + dy * dy + dz * dz);
}

// ---- settings -------------------------------------------------------------------------------------------------------------------
bool valid_game_id(std::string_view id) noexcept {
  if (id.empty() || id.size() > 96) return false;
  return std::all_of(id.begin(), id.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; });
}

bool AvatarSettings::apply(std::string_view key, std::string_view value) {
  if (key == "Avatars.StarterShipMacro") {
    starter_macro = valid_game_id(value) ? std::string(value) : std::string{};
    return true;
  }
  if (key == "Avatars.StarterLoadout") {
    starter_loadout = valid_game_id(value) ? std::string(value) : std::string{};
    return true;
  }
  if (key == "Avatars.SpawnOffsetMeters") {
    double v = 0;
    spawn_offset_m = (parse_d(value, v) && v >= 50.0 && v <= 5000.0) ? v : kDefaultSpawnOffsetM;
    return true;
  }
  return false;
}

StarterSpec resolve_starter(const AvatarSettings& settings, std::uint16_t team, std::string_view race) {
  (void)team;  // later: the team's origin picks the race-based starter (ADR-049/051)
  (void)race;
  StarterSpec s;
  s.macro = settings.starter_macro.empty() ? std::string(kBasicStarterMacro) : settings.starter_macro;
  s.loadout = settings.starter_loadout;
  s.basic_loadout = s.loadout.empty();
  return s;
}

std::string avatar_name(std::string_view player_name) {
  std::string clean;
  for (const char c : player_name) {
    if (static_cast<unsigned char>(c) < 0x20) continue;
    if (clean.size() >= 48) break;
    clean.push_back(c);
  }
  if (clean.empty()) clean = "Player";
  return std::string(kAvatarNamePrefix) + clean;
}

// ---- placement ------------------------------------------------------------------------------------------------------------------
Pose place_near(const Pose& host, double offset_m, std::uint16_t player_id) {
  const double slot = static_cast<double>(player_id);
  double frac = std::fmod(slot * kGoldenFrac, 1.0);
  if (frac < 0) frac += 1.0;
  const double dist = offset_m * (1.0 + frac);
  const double ang = slot * kGoldenAngle;
  Pose p = host;
  p.x = host.x + dist * std::sin(ang);
  p.z = host.z + dist * std::cos(ang);
  p.y = host.y + ((player_id % 2) != 0 ? 40.0 : -40.0);
  return p;
}

std::uint8_t ship_kind_of_macro(std::string_view macro) noexcept {
  if (macro.find("_xs_") != std::string_view::npos) return 1;
  if (macro.find("_xl_") != std::string_view::npos) return 5;
  if (macro.find("_s_") != std::string_view::npos) return 2;
  if (macro.find("_m_") != std::string_view::npos) return 3;
  if (macro.find("_l_") != std::string_view::npos) return 4;
  return 0;
}

// ---- records --------------------------------------------------------------------------------------------------------------------
// A|player|team|net|online|macro|idcode|owner|sector_macro|x|y|z|yaw|pitch|roll|name
std::string records_to_text(const std::vector<Record>& records, const Lineage& lineage) {
  std::ostringstream out;
  out << "x4av 2\n";
  for (const auto& c : lineage.ledger) {
    out << "C|" << clean_field(c.sha, 64) << '|';
    for (std::size_t i = 0; i < c.idcodes.size(); ++i) out << (i ? ";" : "") << clean_field(c.idcodes[i], 24);
    out << '\n';
  }
  for (const auto& r : records) {
    out << "A|" << r.player_id << '|' << r.team << '|' << r.net_id << '|' << (r.online ? 1 : 0) << '|' << clean_field(r.macro, 96) << '|'
        << clean_field(r.idcode, 24) << '|' << clean_field(r.owner, 32) << '|' << clean_field(r.sector_macro, 96) << '|' << fmt_double(r.pose.x) << '|'
        << fmt_double(r.pose.y) << '|' << fmt_double(r.pose.z) << '|' << fmt_double(r.pose.yaw) << '|' << fmt_double(r.pose.pitch) << '|'
        << fmt_double(r.pose.roll) << '|' << clean_field(r.name, 80) << '\n';
  }
  return out.str();
}

ParsedRecords records_from_text(std::string_view text) {
  ParsedRecords out;
  std::size_t pos = 0;
  bool first = true;
  while (pos < text.size()) {
    auto nl = text.find('\n', pos);
    if (nl == std::string_view::npos) nl = text.size();
    std::string_view line = text.substr(pos, nl - pos);
    pos = nl + 1;
    if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
    if (line.empty()) continue;
    if (first) {
      first = false;
      if (line == "x4av 1") {
        out.header_ok = true;  // M3-22: the old format has no ledger: a save load keeps none of its records
      } else if (line == "x4av 2" || line.substr(0, 7) == "x4av 2 ") {
        out.header_ok = true;
      } else {
        return out;  // not ours / another version: nothing is read
      }
      continue;
    }
    const auto f = split_bar(line);
    if (!f.empty() && f[0] == "C") {
      if (f.size() != 3 || f[1].empty()) {
        ++out.bad_lines;
        continue;
      }
      CheckpointNote c;
      c.sha = std::string(f[1]);
      std::string_view ids = f[2];
      while (!ids.empty()) {
        const auto semi = ids.find(';');
        const auto one = ids.substr(0, semi);
        if (!one.empty()) c.idcodes.emplace_back(one);
        if (semi == std::string_view::npos) break;
        ids.remove_prefix(semi + 1);
      }
      out.lineage.ledger.push_back(std::move(c));
      continue;
    }
    if (f.size() != 16 || f[0] != "A") {
      ++out.bad_lines;
      continue;
    }
    Record r;
    std::uint64_t pid = 0, team = 0, net = 0, online = 0;
    double v[6] = {};
    bool ok = parse_u(f[1], pid) && parse_u(f[2], team) && parse_u(f[3], net) && parse_u(f[4], online) && pid > 0 && pid <= 0xFFFF && team <= 0xFFFF &&
              net <= 0xFFFFFFFFull && net != 0;
    for (int i = 0; i < 6 && ok; ++i) ok = parse_d(f[9 + i], v[i]);
    if (!ok) {
      ++out.bad_lines;
      continue;
    }
    r.player_id = static_cast<std::uint16_t>(pid);
    r.team = static_cast<std::uint16_t>(team);
    r.net_id = static_cast<std::uint32_t>(net);
    r.online = online != 0;
    r.macro = std::string(f[5]);
    r.idcode = std::string(f[6]);
    r.owner = std::string(f[7]);
    r.sector_macro = std::string(f[8]);
    r.pose = {v[0], v[1], v[2], v[3], v[4], v[5]};
    r.name = std::string(f[15]);
    out.records.push_back(std::move(r));
  }
  return out;
}

// ---- binder ---------------------------------------------------------------------------------------------------------------------
BindResult bind_records(const std::vector<Record>& records, const std::vector<Candidate>& candidates) {
  BindResult res;
  struct Pair {
    double dist;
    std::size_t rec;
    std::size_t cand;
    int tier;  // 0 = owner + name + idcode, 1 = owner + idcode (the name never got applied), 2 = owner + name (idcode unknown or changed)
  };
  std::vector<Pair> pairs;
  for (std::size_t r = 0; r < records.size(); ++r) {
    const auto& rec = records[r];
    for (std::size_t c = 0; c < candidates.size(); ++c) {
      const auto& cand = candidates[c];
      if (cand.owner != rec.owner) continue;
      const bool id_eq = !rec.idcode.empty() && cand.idcode == rec.idcode;
      const bool name_eq = cand.name == rec.name;
      int tier = -1;
      if (id_eq && name_eq) tier = 0;
      else if (id_eq) tier = 1;
      else if (name_eq) tier = 2;
      if (tier >= 0) pairs.push_back({distance_m(rec.pose, cand.pose), r, c, tier});
    }
  }
  std::stable_sort(pairs.begin(), pairs.end(), [](const Pair& a, const Pair& b) {
    if (a.tier != b.tier) return a.tier < b.tier;
    if (a.dist != b.dist) return a.dist < b.dist;
    if (a.rec != b.rec) return a.rec < b.rec;
    return a.cand < b.cand;
  });
  std::vector<bool> rec_done(records.size(), false), cand_done(candidates.size(), false);
  for (const auto& p : pairs) {
    if (rec_done[p.rec] || cand_done[p.cand]) continue;
    rec_done[p.rec] = true;
    cand_done[p.cand] = true;
    res.bound.emplace_back(p.rec, candidates[p.cand].id);
  }
  std::sort(res.bound.begin(), res.bound.end());
  for (std::size_t r = 0; r < records.size(); ++r) {
    if (!rec_done[r]) res.lost.push_back(r);
  }
  for (std::size_t c = 0; c < candidates.size(); ++c) {
    if (!cand_done[c] && candidates[c].name.starts_with(kAvatarNamePrefix)) res.strays.push_back(candidates[c].id);
  }
  return res;
}

// ---- velocity -------------------------------------------------------------------------------------------------------------------
void VelocityEstimator::sample(double t_s, double x, double y, double z, bool discontinuity) {
  if (discontinuity || !have_prev_) {
    have_prev_ = true;
    valid_ = false;
    v_ = {};
  } else {
    const double dt = t_s - t_;
    if (dt > 0.002 && dt < 0.5) {
      const V inst{(x - x_) / dt, (y - y_) / dt, (z - z_) / dt};
      if (!valid_) {
        v_ = inst;
        valid_ = true;
      } else {
        constexpr double kAlpha = 0.25;  // per frame; settles within ~100 ms at 60 fps
        v_.x += (inst.x - v_.x) * kAlpha;
        v_.y += (inst.y - v_.y) * kAlpha;
        v_.z += (inst.z - v_.z) * kAlpha;
      }
    } else if (dt >= 0.5) {
      valid_ = false;  // a long gap: no speed to claim
      v_ = {};
    }
  }
  t_ = t_s;
  x_ = x;
  y_ = y;
  z_ = z;
}

}  // namespace x4mp::features::avatars
