#pragma once
// A tiny fake game for the ghost driver tests (M3-10): objects with sector id + sector-local pose, a sector map, factions, the local
// player's ship, dress / velocity records, removal through a guard. It is exact on purpose: whatever place() gets is what it stores,
// so a test can compare the object with the analytic track.
#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "features/ghosts/ghost_driver.h"

namespace x4mp::test {

using namespace x4mp::features::ghosts;

struct FakeObj {
  std::uint64_t id = 0;
  std::string macro, owner, name, idcode;
  std::uint64_t sector = 0;
  Pose pose{};
  bool inert = false;
  bool valid = true;
  bool wrecked = false;
  int min_hull = -1;
  Vec3 velocity{};
  bool has_velocity = false;
  int places = 0;
};

class FakeWorld final : public IGhostWorld {
 public:
  std::map<std::uint64_t, FakeObj> objs;
  std::map<std::uint16_t, std::uint64_t> sectors;  // wire index -> local sector id
  std::set<std::string> factions{"x4mp_team_1", "x4mp_team_2", "x4mp_team_3"};
  bool factions_known = true;
  std::set<std::uint64_t> guarded;       // ids safe_remove refuses (the player's ship)
  std::uint64_t next_id = 5000;
  int fail_spawns = 0;                    // the next N spawns return 0
  bool dress_applies = true;              // false: MD "lost" the event (the name stays the macro)
  bool local_present = false;
  std::uint64_t local_sector = 0;
  Vec3 local_pos{};

  // call log
  int spawn_calls = 0, place_calls = 0, remove_calls = 0, dress_calls = 0, hint_calls = 0, hints_total = 0, inert_calls = 0, owner_calls = 0;
  std::vector<std::vector<VelocityHint>> hint_log;
  std::vector<std::uint64_t> removed_ids;
  std::uint64_t idcode_counter = 100;

  FakeWorld() {
    for (std::uint16_t i = 1; i <= 8; ++i) sectors[i] = 9000 + i;
  }

  std::uint64_t sector_id(std::uint16_t index) override {
    const auto it = sectors.find(index);
    return it == sectors.end() ? 0 : it->second;
  }
  std::uint64_t spawn(std::string_view macro, std::uint64_t sector, const Pose& pose, std::string_view owner) override {
    ++spawn_calls;
    if (fail_spawns > 0) {
      --fail_spawns;
      return 0;
    }
    FakeObj o;
    o.id = next_id++;
    o.macro = std::string(macro);
    o.owner = std::string(owner);
    o.name = o.macro;
    o.idcode = "FK" + std::to_string(idcode_counter++);
    o.sector = sector;
    o.pose = pose;
    objs[o.id] = o;
    return o.id;
  }
  void make_inert(std::uint64_t id) override {
    ++inert_calls;
    if (auto it = objs.find(id); it != objs.end()) it->second.inert = true;
  }
  void place(std::uint64_t id, std::uint64_t sector, const Pose& pose) override {
    ++place_calls;
    auto it = objs.find(id);
    if (it == objs.end()) return;
    it->second.sector = sector;
    it->second.pose = pose;
    ++it->second.places;
  }
  void dress(std::uint64_t id, std::string_view label, int min_hull_percent) override {
    ++dress_calls;
    auto it = objs.find(id);
    if (it == objs.end() || !dress_applies) return;
    it->second.name = std::string(label);
    it->second.min_hull = min_hull_percent;
  }
  void hint_velocities(std::span<const VelocityHint> hints) override {
    ++hint_calls;
    hints_total += static_cast<int>(hints.size());
    hint_log.emplace_back(hints.begin(), hints.end());
    for (const auto& h : hints) {
      if (auto it = objs.find(h.id); it != objs.end()) {
        it->second.velocity = {h.vx, h.vy, h.vz};
        it->second.has_velocity = true;
      }
    }
  }
  void set_owner(std::uint64_t id, std::string_view owner) override {
    ++owner_calls;
    if (auto it = objs.find(id); it != objs.end()) it->second.owner = std::string(owner);
  }
  bool valid(std::uint64_t id) override {
    const auto it = objs.find(id);
    return it != objs.end() && it->second.valid;
  }
  bool wrecked(std::uint64_t id) override {
    const auto it = objs.find(id);
    return it != objs.end() && it->second.wrecked;
  }
  RemoveOutcome remove(std::uint64_t id) override {
    ++remove_calls;
    if (guarded.count(id)) return RemoveOutcome::Blocked;
    if (objs.erase(id) == 0) return RemoveOutcome::Failed;
    removed_ids.push_back(id);
    return RemoveOutcome::Removed;
  }
  std::string id_code(std::uint64_t id) override {
    const auto it = objs.find(id);
    return it == objs.end() ? std::string() : it->second.idcode;
  }
  std::string name(std::uint64_t id) override {
    const auto it = objs.find(id);
    return it == objs.end() ? std::string() : it->second.name;
  }
  std::optional<std::uint64_t> find_ghost_by_idcode(std::string_view idcode) override {
    for (const auto& [id, o] : objs) {
      if (o.idcode == idcode && o.name.rfind(kNamePrefix, 0) == 0 && o.owner.rfind("x4mp_team_", 0) == 0) return id;
    }
    return std::nullopt;
  }
  FactionPresence faction(std::string_view f) override {
    if (!factions_known) return FactionPresence::Unknown;
    return factions.count(std::string(f)) ? FactionPresence::Present : FactionPresence::Missing;
  }
  bool local_ship_within(std::uint64_t sector, const Vec3& pos, double radius) override {
    return local_present && local_sector == sector && ghost::distance(local_pos, pos) <= radius;
  }

  // helpers for tests
  [[nodiscard]] std::size_t count_owned(const std::string& owner) const {
    std::size_t n = 0;
    for (const auto& [id, o] : objs) n += o.owner == owner ? 1 : 0;
    return n;
  }
  [[nodiscard]] const FakeObj* only() const { return objs.size() == 1 ? &objs.begin()->second : nullptr; }
  // The id of the object whose name is `label`, 0 when none.
  [[nodiscard]] std::uint64_t by_name(const std::string& label) const {
    for (const auto& [id, o] : objs) {
      if (o.name == label) return id;
    }
    return 0;
  }
};

}  // namespace x4mp::test
