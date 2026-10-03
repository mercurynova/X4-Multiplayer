#include "features/authority/authority_data.h"

#include <algorithm>
#include <charconv>
#include <fstream>
#include <map>
#include <set>
#include <system_error>

#include <nlohmann/json.hpp>

#include "core/crypto/crypto.h"
#include "common_generated.h"

namespace x4mp::features::auth {

namespace fs = std::filesystem;

namespace {
constexpr std::size_t kMaxSectors = 4000;      // far above the real galaxy (about 150); a runaway sender cannot grow memory
constexpr std::size_t kMaxFieldBytes = 160;
constexpr std::size_t kMaxGatesPerSector = 64;

std::vector<std::string_view> split(std::string_view text, char sep) {
  std::vector<std::string_view> out;
  std::size_t start = 0;
  while (true) {
    const auto at = text.find(sep, start);
    if (at == std::string_view::npos) {
      out.push_back(text.substr(start));
      break;
    }
    out.push_back(text.substr(start, at - start));
    start = at + 1;
  }
  return out;
}

float to_float(std::string_view s) {
  float v = 0;
  const auto r = std::from_chars(s.data(), s.data() + s.size(), v);
  if (r.ec != std::errc{}) return 0;
  return v;
}

std::string clip(std::string_view s) { return std::string(s.substr(0, kMaxFieldBytes)); }

bool all_digits(std::string_view s) {
  return !s.empty() && s.size() <= 9 && std::all_of(s.begin(), s.end(), [](char c) { return c >= '0' && c <= '9'; });
}
}  // namespace

// ---------------------------------------------------------------------------------------------------------------------
// MdCollector
// ---------------------------------------------------------------------------------------------------------------------
void MdCollector::reset() { *this = MdCollector{}; }

bool MdCollector::add(std::string_view data) {
  if (data.size() < 2 || data[1] != ';') return false;
  const char kind = data[0];
  const std::string_view body = data.substr(2);
  if (kind == 'G') {
    for (const auto rec : split(body, ';')) {
      if (rec.empty()) continue;
      const auto f = split(rec, '|');
      if (f.size() != 8 || f[0].empty() || sectors_.size() >= kMaxSectors) {
        ++dropped_;
        continue;
      }
      SectorRec s;
      s.macro = clip(f[0]);
      s.cluster = clip(f[1]);
      s.name = clip(f[2]);
      s.owner = clip(f[3]);
      s.x = to_float(f[4]);
      s.y = to_float(f[5]);
      s.z = to_float(f[6]);
      if (!f[7].empty()) {
        for (const auto g : split(f[7], ',')) {
          if (!g.empty() && s.gates.size() < kMaxGatesPerSector) s.gates.push_back(clip(g));
        }
      }
      sectors_.push_back(std::move(s));
    }
    return true;
  }
  if (kind == 'E') {
    if (!all_digits(body)) return false;
    expected_ = static_cast<std::size_t>(std::stoul(std::string(body)));
    end_seen_ = true;
    return true;
  }
  if (kind == 'P') {
    const auto f = split(body, '|');
    if (f.size() != 6 || f[0].empty()) return false;
    ShipRec s;
    s.macro = clip(f[0]);
    s.name = clip(f[1]);
    s.idcode = clip(f[2]);
    s.sector = clip(f[3]);
    s.cls = clip(f[4]);
    s.owner = clip(f[5]);
    ship_ = std::move(s);
    return true;
  }
  if (kind == 'N') {
    no_ship_ = true;
    return true;
  }
  return false;
}

// ---------------------------------------------------------------------------------------------------------------------
// plan
// ---------------------------------------------------------------------------------------------------------------------
x4mp::authority::SpawnKind spawn_kind_for_class(std::string_view cls) noexcept {
  using K = x4mp::authority::SpawnKind;
  if (cls == "ship_xs") return K::ShipXS;
  if (cls == "ship_s") return K::ShipS;
  if (cls == "ship_m") return K::ShipM;
  if (cls == "ship_l") return K::ShipL;
  if (cls == "ship_xl") return K::ShipXL;
  return K::ShipS;  // unknown class: a ship of some size (the M2 self-spawn only has to exist)
}

GalaxyPlan build_plan(const MdCollector& collected) {
  GalaxyPlan plan;
  if (collected.sectors().empty()) return plan;
  std::map<std::string, std::uint32_t> string_index;
  const auto intern = [&](const std::string& value, X4MP::Proto::StringKind kind) -> std::uint32_t {
    if (value.empty()) return 0;
    const std::string key = std::to_string(static_cast<int>(kind)) + ":" + value;
    if (const auto it = string_index.find(key); it != string_index.end()) return it->second;
    const auto index = static_cast<std::uint32_t>(plan.strings.size() + 1);
    plan.strings.push_back({index, static_cast<std::uint8_t>(kind), value});
    string_index.emplace(key, index);
    return index;
  };

  plan.player_faction_ref = intern("player", X4MP::Proto::StringKind::Faction);
  std::map<std::string, std::uint16_t> sector_index;
  for (const auto& s : collected.sectors()) {
    if (sector_index.size() >= 0xFFFF) break;
    if (sector_index.contains(s.macro)) continue;  // a duplicate macro would break the unique index
    x4mp::authority::SectorDesc d;
    d.index = static_cast<std::uint16_t>(sector_index.size() + 1);
    d.macro = s.macro;
    d.cluster_macro = s.cluster;
    d.name = s.name;
    d.owner_ref = intern(s.owner, X4MP::Proto::StringKind::Faction);
    d.x = s.x;
    d.y = s.y;
    d.z = s.z;
    sector_index.emplace(s.macro, d.index);
    plan.sectors.push_back(std::move(d));
  }

  std::set<std::pair<std::uint16_t, std::uint16_t>> seen;
  for (const auto& s : collected.sectors()) {
    const auto from = sector_index.find(s.macro);
    if (from == sector_index.end()) continue;
    for (const auto& g : s.gates) {
      const auto to = sector_index.find(g);
      if (to == sector_index.end() || to->second == from->second) continue;
      const auto key = std::minmax(from->second, to->second);
      if (seen.insert({key.first, key.second}).second) plan.links.push_back({key.first, key.second});
    }
  }

  if (const auto& ship = collected.ship()) {
    plan.ship_macro_ref = intern(ship->macro, X4MP::Proto::StringKind::Macro);
    if (const auto it = sector_index.find(ship->sector); it != sector_index.end()) plan.ship_sector = it->second;
  }
  return plan;
}

// ---------------------------------------------------------------------------------------------------------------------
// SaveFileWatcher
// ---------------------------------------------------------------------------------------------------------------------
SaveFileWatcher::SaveFileWatcher(fs::path file, std::chrono::milliseconds stable_for) : file_(std::move(file)), stable_for_(stable_for) {}

bool SaveFileWatcher::poll(Clock::time_point now) {
  if (complete_) return true;
  std::error_code ec;
  const auto size = fs::file_size(file_, ec);
  if (ec || size == 0) {
    seen_ = false;
    return false;
  }
  const auto mtime = fs::last_write_time(file_, ec);
  if (ec) {
    seen_ = false;
    return false;
  }
  if (!seen_ || size != size_ || mtime != mtime_) {
    seen_ = true;
    size_ = size;
    mtime_ = mtime;
    stable_since_ = now;
    return false;
  }
  if (now - stable_since_ < stable_for_) return false;
  // Looks finished. A writer that still holds the file open for writing (share mode) makes this open fail: keep waiting.
  std::ifstream in(file_, std::ios::binary);
  if (!in) return false;
  unsigned char magic[2] = {0, 0};
  in.read(reinterpret_cast<char*>(magic), 2);
  if (in.gcount() != 2 || magic[0] != 0x1f || magic[1] != 0x8b) return false;
  complete_ = true;
  return true;
}

// ---------------------------------------------------------------------------------------------------------------------
// ledger
// ---------------------------------------------------------------------------------------------------------------------
bool is_checkpoint_save_name(std::string_view name) noexcept {
  constexpr std::string_view prefix = "x4mp_ckpt_";
  if (name.size() != prefix.size() + 16 || name.substr(0, prefix.size()) != prefix) return false;
  return std::all_of(name.begin() + static_cast<std::ptrdiff_t>(prefix.size()), name.end(),
                     [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}

std::string checkpoint_save_name(std::uint64_t id) {
  static constexpr char kHex[] = "0123456789abcdef";
  std::string name = "x4mp_ckpt_";
  for (int shift = 60; shift >= 0; shift -= 4) name.push_back(kHex[(id >> shift) & 0xF]);
  return name;
}

std::vector<std::string> names_to_remove(const std::vector<std::string>& oldest_first, std::size_t keep) {
  std::vector<std::string> out;
  if (oldest_first.size() <= keep) return out;
  const auto excess = oldest_first.size() - keep;
  for (std::size_t i = 0; i < excess; ++i) {
    if (is_checkpoint_save_name(oldest_first[i])) out.push_back(oldest_first[i]);
  }
  return out;
}

std::vector<std::string> load_ledger(const fs::path& file) {
  std::vector<std::string> out;
  std::ifstream in(file, std::ios::binary);
  if (!in) return out;
  const auto doc = nlohmann::json::parse(in, nullptr, /*allow_exceptions=*/false);
  if (!doc.is_object()) return out;
  const auto it = doc.find("saves");
  if (it == doc.end() || !it->is_array()) return out;
  for (const auto& e : *it) {
    if (e.is_string() && is_checkpoint_save_name(e.get<std::string>())) out.push_back(e.get<std::string>());
  }
  return out;
}

bool store_ledger(const fs::path& file, const std::vector<std::string>& oldest_first) {
  std::error_code ec;
  if (file.has_parent_path()) fs::create_directories(file.parent_path(), ec);
  nlohmann::json doc;
  doc["v"] = 1;
  doc["saves"] = oldest_first;
  const fs::path tmp = file.string() + ".tmp";
  {
    std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
    if (!out) return false;
    out << doc.dump(2) << "\n";
    if (!out) return false;
  }
  fs::rename(tmp, file, ec);
  return !ec;
}

std::vector<std::string> record_and_trim(const fs::path& ledger_file, const fs::path& save_dir, const std::string& name, std::size_t keep) {
  auto ledger = load_ledger(ledger_file);
  if (is_checkpoint_save_name(name) && std::find(ledger.begin(), ledger.end(), name) == ledger.end()) ledger.push_back(name);
  std::vector<std::string> removed;
  for (const auto& old : names_to_remove(ledger, keep)) {
    for (const char* ext : {".xml.gz", ".xml"}) {
      std::error_code ec;
      fs::remove(save_dir / (old + ext), ec);  // only ever a name that is in the ledger and has the x4mp_ckpt_<16 hex> shape
    }
    removed.push_back(old);
  }
  if (!removed.empty()) ledger.erase(ledger.begin(), ledger.begin() + static_cast<std::ptrdiff_t>(removed.size()));
  (void)store_ledger(ledger_file, ledger);
  return removed;
}

// ---------------------------------------------------------------------------------------------------------------------
// AuthorityState
// ---------------------------------------------------------------------------------------------------------------------
std::string AuthorityState::to_json() const {
  nlohmann::json j;
  j["next_net_id"] = next_net_id;
  j["spawned"] = spawned;
  j["strings_sent"] = strings_sent;
  j["string_count"] = string_count;
  j["checkpoints"] = checkpoints;
  j["loaded_sha"] = crypto::to_hex(std::span<const std::uint8_t>(loaded_sha));
  return j.dump();
}

AuthorityState AuthorityState::from_json(std::string_view text) {
  AuthorityState s;
  const auto doc = nlohmann::json::parse(text.begin(), text.end(), nullptr, /*allow_exceptions=*/false);
  if (!doc.is_object()) return s;
  const auto u32 = doc.value("next_net_id", std::uint32_t{1});
  s.next_net_id = (u32 == 0 || u32 == 0xFFFFFFFFu) ? 1 : u32;
  s.spawned = doc.value("spawned", false);
  s.strings_sent = doc.value("strings_sent", false);
  s.string_count = doc.value("string_count", std::uint32_t{0});
  s.checkpoints = doc.value("checkpoints", std::uint64_t{0});
  std::vector<std::uint8_t> sha;
  if (crypto::from_hex(doc.value("loaded_sha", std::string{}), sha) && sha.size() == 32) s.loaded_sha = std::move(sha);
  return s;
}

}  // namespace x4mp::features::auth
