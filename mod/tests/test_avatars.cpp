// M3-11 avatars: the pure plan/binder/records logic and the AvatarDirector over a fake world (no game, no session).
#include <algorithm>
#include <cmath>
#include <map>
#include <set>

#include <catch2/catch_test_macros.hpp>

#include "features/avatars/avatar_director.h"
#include "features/avatars/avatar_plan.h"
#include "features/avatars/avatar_wire.h"

using namespace x4mp::features::avatars;
namespace auth = x4mp::authority;

namespace {

struct FakeObj {
  std::string macro, owner, name, idcode;
  std::uint64_t sector = 0;
  Pose pose;
  bool active = true;
};

// A fake world + session behind IAvatarEnv. Component ids change on renumber() like on a save load.
struct FakeEnv final : IAvatarEnv {
  bool ready = true, factions = true, net = true, enumeration = true;
  std::map<std::uint64_t, FakeObj> objs;
  std::uint64_t next_id = 5000;
  std::uint64_t player_ship = 1;  // never removable
  HostPlace host{900, {1000, 0, 2000, 0.5, 0, 0}};
  std::map<std::string, std::uint64_t> sector_by_macro{{"sec_a", 900}, {"sec_b", 901}};
  std::map<std::string, std::uint16_t> index_by_macro{{"sec_a", 1}, {"sec_b", 2}};
  std::map<std::uint16_t, std::string> macro_by_index{{1, "sec_a"}, {2, "sec_b"}};
  int spawn_fail_budget = 0;
  std::uint32_t next_net = 1;
  double gt = 5000.0;

  struct SafeAsk { std::uint32_t seq; std::uint64_t sector; Pose wanted; double radius; };
  struct DressAsk { std::uint32_t seq; std::uint64_t id; std::string name; StarterSpec starter; int min_hull; };
  std::vector<SafeAsk> safe_asks;
  std::vector<DressAsk> dress_asks;
  std::vector<std::vector<auth::SpawnEntity>> spawns_sent;
  std::vector<std::pair<std::uint32_t, std::uint16_t>> controllers;
  struct OwnerSent { std::uint32_t net; std::uint16_t team; std::string faction; };
  std::vector<OwnerSent> owners_sent;
  int set_owner_calls = 0;
  bool owner_fails = false;
  std::vector<std::vector<VelHint>> velocities;
  std::vector<std::uint64_t> removed;
  std::string saved;
  std::vector<std::string> logs;
  int spawn_calls = 0;
  std::map<std::string, std::uint32_t> strings;

  bool game_ready() override { return ready; }
  bool factions_ready() override { return factions; }
  std::string faction_of_team(std::uint16_t team) override { return team == 0 ? "" : "x4mp_team_" + std::to_string(team); }
  std::optional<HostPlace> host_place() override { return host; }
  std::uint64_t sector_id_of_index(std::uint16_t i) override {
    const auto it = macro_by_index.find(i);
    return it == macro_by_index.end() ? 0 : sector_by_macro[it->second];
  }
  std::uint64_t sector_id_of_macro(const std::string& m) override {
    const auto it = sector_by_macro.find(m);
    return it == sector_by_macro.end() ? 0 : it->second;
  }
  std::string sector_macro_of_id(std::uint64_t id) override {
    for (const auto& [m, i] : sector_by_macro) {
      if (i == id) return m;
    }
    return {};
  }
  std::uint16_t sector_index_of_macro(const std::string& m) override {
    const auto it = index_by_macro.find(m);
    return it == index_by_macro.end() ? 0 : it->second;
  }
  std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const Pose& pose, const std::string& owner) override {
    ++spawn_calls;
    if (spawn_fail_budget > 0) {
      --spawn_fail_budget;
      return 0;
    }
    const auto id = next_id++;
    objs[id] = {macro, owner, "", "IDC-" + std::to_string(id % 997), sector, pose, true};
    return id;
  }
  void activate(std::uint64_t id, bool a) override {
    if (auto it = objs.find(id); it != objs.end()) it->second.active = a;
  }
  bool set_owner(std::uint64_t id, const std::string& f) override {
    ++set_owner_calls;
    if (owner_fails || !objs.count(id)) return false;
    objs[id].owner = f;
    return true;
  }
  bool send_owner(std::uint32_t n, std::uint16_t t, const std::string& f) override {
    owners_sent.push_back({n, t, f});
    return true;
  }
  bool valid(std::uint64_t id) override { return objs.count(id) != 0; }
  std::string idcode(std::uint64_t id) override { return objs.count(id) ? objs[id].idcode : ""; }
  bool read_pose(std::uint64_t id, std::uint64_t& sector, Pose& pose) override {
    const auto it = objs.find(id);
    if (it == objs.end()) return false;
    sector = it->second.sector;
    pose = it->second.pose;
    return true;
  }
  bool set_pose(std::uint64_t id, std::uint64_t sector, const Pose& pose) override {
    const auto it = objs.find(id);
    if (it == objs.end()) return false;
    it->second.sector = sector;
    it->second.pose = pose;
    return true;
  }
  bool remove(std::uint64_t id) override {
    if (id == player_ship || !objs.count(id)) return false;
    objs.erase(id);
    removed.push_back(id);
    return true;
  }
  std::optional<std::vector<Candidate>> team_candidates() override {
    if (!enumeration) return std::nullopt;
    std::vector<Candidate> out;
    for (const auto& [id, o] : objs) {
      if (o.owner.rfind("x4mp_team_", 0) == 0) out.push_back({id, o.idcode, o.name, o.owner, o.pose});
    }
    return out;
  }
  void ask_safepos(std::uint32_t seq, std::uint64_t sector, const Pose& w, double r) override { safe_asks.push_back({seq, sector, w, r}); }
  void ask_dress(std::uint32_t seq, std::uint64_t id, const std::string& name, const StarterSpec& st, int mh) override {
    dress_asks.push_back({seq, id, name, st, mh});
    if (auto it = objs.find(id); it != objs.end()) it->second.name = name;  // MD would do this
  }
  void send_velocity(const std::vector<VelHint>& h) override { velocities.push_back(h); }
  bool net_ready() override { return net; }
  std::uint32_t alloc_net_id() override { return next_net++; }
  std::uint32_t string_ref(StrKind k, const std::string& v) override {
    const std::string key = std::to_string(static_cast<int>(k)) + v;
    if (!strings.count(key)) strings[key] = static_cast<std::uint32_t>(strings.size() + 1);
    return strings[key];
  }
  double game_time() override { return gt; }
  bool send_spawn(const std::vector<auth::SpawnEntity>& e, double) override {
    spawns_sent.push_back(e);
    return true;
  }
  bool send_controller(std::uint32_t n, std::uint16_t p) override {
    controllers.emplace_back(n, p);
    return true;
  }
  void save_records(const std::string& t) override { saved = t; }
  void log(LogLevel, const std::string& t) override { logs.push_back(t); }

  // a save load: every id changes, positions and idcodes stay
  void renumber() {
    std::map<std::uint64_t, FakeObj> n;
    for (auto& [id, o] : objs) n[id + 70000] = o;
    objs = std::move(n);
  }
  [[nodiscard]] std::size_t count_owned(const std::string& owner) const {
    return static_cast<std::size_t>(std::count_if(objs.begin(), objs.end(), [&](const auto& kv) { return kv.second.owner == owner; }));
  }
};

RosterIn roster_with(std::initializer_list<std::pair<int, std::pair<const char*, int>>> players, bool full = true) {
  RosterIn r;
  r.full = full;
  for (const auto& [id, np] : players) {
    RosterRow row;
    row.id = static_cast<std::uint16_t>(id);
    row.name = np.first;
    row.team = static_cast<std::uint16_t>(np.second);
    r.players.push_back(row);
  }
  return r;
}

PlayerShipReq request(int player) {
  PlayerShipReq q;
  q.player_id = static_cast<std::uint16_t>(player);
  q.ship_macro = "ship_arg_s_fighter_01_a_macro";
  return q;
}

struct Clock {
  double t = 0;
};

void run(AvatarDirector& d, Clock& c, double seconds) {
  const int frames = static_cast<int>(seconds * 60);
  for (int i = 0; i < frames; ++i) {
    c.t += 1.0 / 60.0;
    d.step(c.t, static_cast<std::int64_t>(c.t * 1e6));
  }
}

// provisions player `p` (team t) completely: request, step, MD answers with `safe`
std::uint64_t provision(AvatarDirector& d, FakeEnv& env, Clock& c, int p, int team, const char* name) {
  d.on_roster(roster_with({{p, {name, team}}}, false));
  d.on_player_ship(request(p));
  run(d, c, 0.1);
  REQUIRE_FALSE(env.safe_asks.empty());
  d.on_safepos(env.safe_asks.back().seq, true, {env.safe_asks.back().wanted.x + 1, env.safe_asks.back().wanted.y, env.safe_asks.back().wanted.z, 0, 0, 0});
  run(d, c, 0.1);
  const auto v = d.views();
  const auto it = std::find_if(v.begin(), v.end(), [&](const auto& x) { return x.rec.player_id == p; });
  REQUIRE(it != v.end());
  REQUIRE(it->local_id != 0);
  return it->local_id;
}

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("avatars.plan: settings and the single starter resolution", "[avatars]") {
  AvatarSettings s;
  CHECK(resolve_starter(s, 1).macro == "ship_arg_s_fighter_01_a_macro");
  CHECK(resolve_starter(s, 1).basic_loadout);
  CHECK(resolve_starter(s, 1).loadout.empty());
  CHECK(s.apply("Avatars.StarterShipMacro", "ship_par_s_fighter_01_a_macro"));
  CHECK(s.apply("Avatars.StarterLoadout", "scenario_basic_fighter"));
  const auto st = resolve_starter(s, 2);
  CHECK(st.macro == "ship_par_s_fighter_01_a_macro");
  CHECK(st.loadout == "scenario_basic_fighter");
  CHECK_FALSE(st.basic_loadout);
  CHECK(s.apply("Avatars.StarterShipMacro", "Bad Macro!"));  // invalid -> back to the default
  CHECK(resolve_starter(s, 1).macro == "ship_arg_s_fighter_01_a_macro");
  CHECK(s.apply("Avatars.SpawnOffsetMeters", "450"));
  CHECK(s.spawn_offset_m == 450.0);
  CHECK(s.apply("Avatars.SpawnOffsetMeters", "7"));  // below the range
  CHECK(s.spawn_offset_m == kDefaultSpawnOffsetM);
  CHECK_FALSE(s.apply("Mods.Whatever", "x"));
}

TEST_CASE("avatars.plan: names, placement and kinds", "[avatars]") {
  CHECK(avatar_name("Alice") == "[MP] Alice");
  CHECK(avatar_name("") == "[MP] Player");
  CHECK(avatar_name(std::string(100, 'x')).size() == kAvatarNamePrefix.size() + 48);
  const Pose host{100, 50, -20, 1, 0, 0};
  std::set<std::pair<int, int>> spots;
  for (std::uint16_t p = 1; p <= 8; ++p) {
    const Pose a = place_near(host, 300, p);
    const double dx = a.x - host.x, dz = a.z - host.z;
    const double d = std::sqrt(dx * dx + dz * dz);
    CHECK(d >= 299.9);
    CHECK(d <= 600.1);
    spots.insert({static_cast<int>(a.x), static_cast<int>(a.z)});
  }
  CHECK(spots.size() == 8);
  CHECK(ship_kind_of_macro("ship_arg_s_fighter_01_a_macro") == 2);
  CHECK(ship_kind_of_macro("ship_arg_m_trans_container_01_a_macro") == 3);
  CHECK(ship_kind_of_macro("ship_arg_xl_carrier_01_a_macro") == 5);
  CHECK(ship_kind_of_macro("ship_arg_xs_spacesuit_01_a_macro") == 1);
}

TEST_CASE("avatars.plan: records round trip and tolerate garbage", "[avatars]") {
  Record r;
  r.player_id = 3;
  r.team = 2;
  r.net_id = 17;
  r.name = "[MP] Bob|x";
  r.macro = "ship_arg_s_fighter_01_a_macro";
  r.idcode = "ABC-123";
  r.owner = "x4mp_team_2";
  r.sector_macro = "sec_a";
  r.pose = {1.5, -2.25, 3000, 0.1, 0.2, 0.3};
  r.online = true;
  auto text = records_to_text({r});
  text += "A|bad line\nA|0|1|1|0|m|i|o|s|0|0|0|0|0|0|n\n";
  const auto parsed = records_from_text(text);
  REQUIRE(parsed.header_ok);
  CHECK(parsed.bad_lines == 2);
  REQUIRE(parsed.records.size() == 1);
  Record expect = r;
  expect.name = "[MP] Bobx";  // '|' is not allowed inside a field
  CHECK(parsed.records[0] == expect);
  CHECK_FALSE(records_from_text("something else\nA|1").header_ok);
}

TEST_CASE("avatars.binder: idcode match after ids changed, nearest, never twice", "[avatars]") {
  const auto rec = [](int p, const char* idc, Pose pose) {
    Record r;
    r.player_id = static_cast<std::uint16_t>(p);
    r.name = "[MP] P" + std::to_string(p);
    r.owner = "x4mp_team_1";
    r.idcode = idc;
    r.pose = pose;
    return r;
  };
  const auto cand = [](std::uint64_t id, const char* idc, const char* name, Pose pose, const char* owner = "x4mp_team_1") {
    return Candidate{id, idc, name, owner, pose};
  };
  SECTION("by idcode, other objects ignored") {
    const auto res = bind_records({rec(1, "AAA-111", {}), rec(2, "BBB-222", {})},
                                  {cand(9, "BBB-222", "[MP] P2", {}), cand(8, "AAA-111", "[MP] P1", {}), cand(7, "ZZZ", "[MP] P3", {})});
    REQUIRE(res.bound.size() == 2);
    CHECK(res.bound[0] == std::pair<std::size_t, std::uint64_t>{0, 8});
    CHECK(res.bound[1] == std::pair<std::size_t, std::uint64_t>{1, 9});
    CHECK(res.lost.empty());
    CHECK(res.strays == std::vector<std::uint64_t>{7});
  }
  SECTION("two candidates with the same idcode: the nearest wins, the other is a stray") {
    const auto res = bind_records({rec(1, "AAA-111", {100, 0, 0, 0, 0, 0})},
                                  {cand(8, "AAA-111", "[MP] P1", {5000, 0, 0, 0, 0, 0}), cand(9, "AAA-111", "[MP] P1", {101, 0, 0, 0, 0, 0})});
    REQUIRE(res.bound.size() == 1);
    CHECK(res.bound[0].second == 9);
    CHECK(res.strays == std::vector<std::uint64_t>{8});
  }
  SECTION("the name was never applied: idcode alone binds; name alone binds too") {
    const auto res = bind_records({rec(1, "AAA-111", {}), rec(2, "BBB-222", {})},
                                  {cand(8, "AAA-111", "Argon Elite", {}), cand(9, "OTHER", "[MP] P2", {})});
    CHECK(res.bound.size() == 2);
  }
  SECTION("a wrong owner never binds; nothing found = lost") {
    const auto res = bind_records({rec(1, "AAA-111", {})}, {cand(8, "AAA-111", "[MP] P1", {}, "player")});
    CHECK(res.bound.empty());
    CHECK(res.lost == std::vector<std::size_t>{0});
  }
}

// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("avatars.director: provisioning on PlayerShip", "[avatars]") {
  FakeEnv env;
  AvatarDirector d(env);
  Clock c;
  d.on_roster(roster_with({{2, {"Alice", 1}}}));

  SECTION("waits for the team factions, then asks for a safe spot and spawns there") {
    env.factions = false;
    d.on_player_ship(request(2));
    run(d, c, 0.5);
    CHECK(env.safe_asks.empty());
    CHECK(env.spawn_calls == 0);
    env.factions = true;
    run(d, c, 0.1);
    REQUIRE(env.safe_asks.size() == 1);
    CHECK(env.safe_asks[0].sector == 900);  // the host's sector
    CHECK(env.safe_asks[0].radius == AvatarDirector::kSafePosRadiusM);
    const double dx = env.safe_asks[0].wanted.x - env.host.pose.x, dz = env.safe_asks[0].wanted.z - env.host.pose.z;
    CHECK(std::sqrt(dx * dx + dz * dz) >= 299.9);
    d.on_safepos(env.safe_asks[0].seq, true, {env.safe_asks[0].wanted.x + 40, 0, env.safe_asks[0].wanted.z, 0, 0, 0});
    run(d, c, 0.1);
    REQUIRE(env.spawn_calls == 1);
    const auto& obj = env.objs.begin()->second;
    CHECK(obj.owner == "x4mp_team_1");
    CHECK(obj.macro == "ship_arg_s_fighter_01_a_macro");
    CHECK_FALSE(obj.active);  // inert
    CHECK(obj.pose.x == env.safe_asks[0].wanted.x + 40);  // the MD safe position, not the raw offset
    REQUIRE(env.dress_asks.size() == 1);
    CHECK(env.dress_asks[0].name == "[MP] Alice");
    CHECK(env.dress_asks[0].min_hull == 100);
    CHECK(env.dress_asks[0].starter.basic_loadout);  // early-game loadout, never the spawn default
    REQUIRE(env.spawns_sent.size() == 1);
    const auto& e = env.spawns_sent[0][0];
    CHECK(e.origin == auth::SpawnOrigin::PlayerShip);
    CHECK(e.controller_player == 2);
    CHECK(e.owner_player == 2);
    CHECK(e.owner_team == 1);
    CHECK(e.name == "[MP] Alice");
    CHECK(e.net_id == 1);
    CHECK(e.sector == 1);
    CHECK(e.kind == auth::SpawnKind::ShipS);
    CHECK_FALSE(e.idcode.empty());
  }

  SECTION("the configured starter macro and loadout are used") {
    AvatarSettings s;
    s.apply("Avatars.StarterShipMacro", "ship_par_s_fighter_01_a_macro");
    s.apply("Avatars.StarterLoadout", "scenario_basic_fighter");
    d.set_settings(s);
    provision(d, env, c, 2, 1, "Alice");
    CHECK(env.objs.begin()->second.macro == "ship_par_s_fighter_01_a_macro");
    CHECK(env.dress_asks.at(0).starter.loadout == "scenario_basic_fighter");
    CHECK_FALSE(env.dress_asks.at(0).starter.basic_loadout);
  }

  SECTION("no MD answer: after the timeout it spawns at the wanted spot") {
    d.on_player_ship(request(2));
    run(d, c, 0.2);
    REQUIRE(env.safe_asks.size() == 1);
    run(d, c, AvatarDirector::kSafePosTimeoutS + 0.5);
    CHECK(env.spawn_calls == 1);
    CHECK(d.stats().safepos_timeouts == 1);
  }

  SECTION("a failing spawn is retried a bounded number of times") {
    env.spawn_fail_budget = 100;
    d.on_player_ship(request(2));
    run(d, c, 0.2);
    d.on_safepos(env.safe_asks[0].seq, true, env.safe_asks[0].wanted);
    run(d, c, 60);
    CHECK(env.spawn_calls == AvatarDirector::kMaxSpawnTries);
    CHECK(env.objs.empty());
    CHECK(env.spawns_sent.empty());
  }

  SECTION("the same player asking again gets the SAME avatar (refresh, no second ship)") {
    const auto id = provision(d, env, c, 2, 1, "Alice");
    REQUIRE(env.spawns_sent.size() == 1);
    d.on_player_ship(request(2));
    run(d, c, 0.2);
    CHECK(env.spawn_calls == 1);
    CHECK(env.objs.size() == 1);
    REQUIRE(env.spawns_sent.size() == 2);
    CHECK(env.spawns_sent[1][0].net_id == env.spawns_sent[0][0].net_id);
    CHECK(env.spawns_sent[1][0].controller_player == 2);
    CHECK(d.views()[0].local_id == id);
  }

  SECTION("two players get two ships, two net ids, different spots") {
    d.on_roster(roster_with({{3, {"Bob", 2}}}, false));
    provision(d, env, c, 2, 1, "Alice");
    provision(d, env, c, 3, 2, "Bob");
    CHECK(env.objs.size() == 2);
    CHECK(env.count_owned("x4mp_team_1") == 1);
    CHECK(env.count_owned("x4mp_team_2") == 1);
    CHECK(env.spawns_sent[0][0].net_id != env.spawns_sent[1][0].net_id);
  }
}

TEST_CASE("avatars.director: driving from relayed PlayerState, velocity hint, parking", "[avatars]") {
  FakeEnv env;
  AvatarDirector d(env);
  Clock c;
  d.on_roster(roster_with({{2, {"Alice", 1}}}));
  const auto id = provision(d, env, c, 2, 1, "Alice");
  const auto net = env.spawns_sent.at(0).at(0).net_id;

  // 100 m/s along +x in sector 1 (index 1 = sec_a = 900), 20 Hz states, server time = c.t
  std::uint16_t feed_sector = 1;
  const auto feed = [&](double secs) {
    const int frames = static_cast<int>(secs * 60);
    for (int i = 0; i < frames; ++i) {
      c.t += 1.0 / 60.0;
      if (i % 3 == 0) {
        PlayerStateIn s;
        s.net_id = net;
        s.sector = feed_sector;
        s.sample_time_us = static_cast<std::int64_t>(c.t * 1e6);
        s.pose = {2000 + 100.0 * c.t, 0, 0, 0, 0, 0};
        d.on_player_state(s, s.sample_time_us);
      }
      d.step(c.t, static_cast<std::int64_t>(c.t * 1e6));
    }
  };
  feed(3.0);
  CHECK(env.objs[id].sector == 900);
  const double expected = 2000 + 100.0 * (c.t - 0.15);  // interpolation delay ~100..150 ms
  CHECK(std::fabs(env.objs[id].pose.x - expected) < 25.0);
  CHECK(d.stats().set_pose_calls > 100);
  REQUIRE_FALSE(env.velocities.empty());
  CHECK(env.velocities.back().at(0).id == id);
  CHECK(std::fabs(env.velocities.back().at(0).vx - 100.0) < 15.0);
  // about 5 hint batches per second
  CHECK(env.velocities.size() >= 10);
  CHECK(env.velocities.size() <= 20);

  SECTION("a gate jump moves it to the other sector") {
    PlayerStateIn s;
    s.net_id = net;
    s.sector = 2;
    s.flags = x4mp::ghost::kTeleport;
    s.sample_time_us = static_cast<std::int64_t>(c.t * 1e6) + 50'000;
    s.pose = {10, 0, 0, 0, 0, 0};
    d.on_player_state(s, s.sample_time_us);
    feed_sector = 2;
    feed(1.0);
    CHECK(env.objs[id].sector == 901);
    CHECK(d.views()[0].rec.sector_macro == "sec_b");
  }

  SECTION("leaving parks it: controller 0, no more driving, stays where it is") {
    const Pose where = env.objs[id].pose;
    RosterIn gone;
    gone.removed = {2};
    d.on_roster(gone);
    REQUIRE(env.controllers.size() == 1);
    CHECK(env.controllers[0] == std::pair<std::uint32_t, std::uint16_t>{net, 0});
    const auto calls = d.stats().set_pose_calls;
    PlayerStateIn s;  // a late state after the leave is ignored
    s.net_id = net;
    s.sector = 1;
    s.sample_time_us = static_cast<std::int64_t>(c.t * 1e6);
    s.pose = {99999, 0, 0, 0, 0, 0};
    d.on_player_state(s, s.sample_time_us);
    run(d, c, 1.0);
    CHECK(d.stats().set_pose_calls == calls);
    CHECK(env.objs[id].pose.x == where.x);
    CHECK(d.views()[0].rec.online == false);
    // something pushes the parked ship: it is snapped back
    env.objs[id].pose.x += 40;
    run(d, c, 1.5);
    CHECK(std::fabs(env.objs[id].pose.x - where.x) < 0.01);
    CHECK(d.stats().repairs >= 1);
    // the rejoin: PlayerShip again -> same ship, controller restored
    d.on_player_ship(request(2));
    run(d, c, 0.2);
    CHECK(env.objs.size() == 1);
    CHECK(env.spawns_sent.back()[0].controller_player == 2);
  }

  SECTION("a node in its resume grace is suspended, not parked") {
    RosterIn r;
    RosterRow row;
    row.id = 2;
    row.name = "Alice";
    row.team = 1;
    row.online = false;
    r.players = {row};
    d.on_roster(r);
    CHECK(env.controllers.empty());
    CHECK(d.views()[0].suspended);
    const auto calls = d.stats().set_pose_calls;
    feed(0.5);
    CHECK(d.stats().set_pose_calls == calls);
    row.online = true;
    r.players = {row};
    d.on_roster(r);
    feed(0.5);
    CHECK(d.stats().set_pose_calls > calls);
  }

  SECTION("a full roster without the player parks it") {
    d.on_roster(roster_with({{9, {"Zed", 1}}}));
    CHECK(env.controllers.size() == 1);
  }
}

TEST_CASE("avatars.director: EntityDespawn{Removed} goes through the guarded remove", "[avatars]") {
  FakeEnv env;
  AvatarDirector d(env);
  Clock c;
  d.on_roster(roster_with({{2, {"Alice", 1}}}));
  const auto id = provision(d, env, c, 2, 1, "Alice");
  const auto net = env.spawns_sent.at(0).at(0).net_id;

  SECTION("other reasons and unknown ids are ignored") {
    d.on_despawn({{{net, 2}, {9999, kDespawnRemoved}}});
    CHECK(env.objs.count(id) == 1);
    CHECK(env.removed.empty());
  }
  SECTION("Removed removes the avatar's ship and forgets it") {
    d.on_despawn({{{net, kDespawnRemoved}}});
    CHECK(env.objs.count(id) == 0);
    CHECK(env.removed == std::vector<std::uint64_t>{id});
    CHECK(d.size() == 0);
  }
  SECTION("when the guard refuses (it is the player's ship) nothing is removed") {
    env.player_ship = id;  // pretend this ship is the player's own
    d.on_despawn({{{net, kDespawnRemoved}}});
    CHECK(env.objs.count(id) == 1);
    CHECK(d.stats().remove_refused == 1);
  }
}

TEST_CASE("avatars.director: rebind after a save load, no duplicates", "[avatars]") {
  FakeEnv env;
  std::string saved;
  std::uint64_t id = 0;
  std::uint32_t net = 0;
  {
    AvatarDirector d(env);
    Clock c;
    d.on_roster(roster_with({{2, {"Alice", 1}}, {3, {"Bob", 2}}}));
    id = provision(d, env, c, 2, 1, "Alice");
    provision(d, env, c, 3, 2, "Bob");
    net = env.spawns_sent.at(0).at(0).net_id;
    RosterIn gone;
    gone.removed = {3};
    d.on_roster(gone);
    d.persist_now();
    saved = env.saved;
    REQUIRE(env.objs.size() == 2);
  }
  const auto spawns_before = env.spawn_calls;

  SECTION("every id changed: the binder finds both ships by idcode") {
    env.renumber();
    env.spawns_sent.clear();
    AvatarDirector d2(env);
    d2.load_records(records_from_text(saved).records);
    CHECK(d2.rebind_pending());
    Clock c;
    run(d2, c, 0.5);
    CHECK_FALSE(d2.rebind_pending());
    CHECK(env.spawn_calls == spawns_before);  // nothing respawned
    CHECK(env.objs.size() == 2);
    const auto views = d2.views();
    REQUIRE(views.size() == 2);
    for (const auto& v : views) {
      CHECK(v.stage == AvatarDirector::Stage::Live);
      CHECK(env.objs.count(v.local_id) == 1);
      CHECK(v.local_id >= 70000);
    }
    CHECK(d2.stats().bound == 2);
    // both are announced again (the server may have lost its mirror), the parked one with controller 0
    REQUIRE(env.spawns_sent.size() == 2);
    std::map<std::uint32_t, std::uint16_t> ctrl;
    for (const auto& m : env.spawns_sent) ctrl[m[0].net_id] = m[0].controller_player;
    CHECK(ctrl.at(net) == 2);  // Alice was connected when the records were written (the roster parks her if she is gone)
    CHECK(ctrl.size() == 2);
    for (const auto& [n, p] : ctrl) {
      if (n != net) CHECK(p == 0);  // Bob was parked
    }
  }

  SECTION("the registry kept the ids (same universe): adopted after the idcode and name check") {
    AvatarDirector d2(env);
    const auto views_before = env.objs;
    std::vector<std::pair<std::uint16_t, std::uint64_t>> hints;
    for (const auto& [oid, o] : views_before) hints.emplace_back(o.owner == "x4mp_team_1" ? 2 : 3, oid);
    d2.load_records(records_from_text(saved).records, hints);
    Clock c;
    run(d2, c, 0.5);
    CHECK(env.spawn_calls == spawns_before);
    CHECK(d2.stats().bound == 2);
    bool alice = false;
    for (const auto& v : d2.views()) alice = alice || (v.rec.player_id == 2 && v.local_id == id);
    CHECK(alice);
  }

  SECTION("a ship that disappeared is respawned once at its last pose, same net id") {
    env.objs.erase(id);
    env.renumber();
    env.spawns_sent.clear();
    AvatarDirector d2(env);
    d2.load_records(records_from_text(saved).records);
    Clock c;
    run(d2, c, 0.5);
    CHECK(d2.stats().lost == 1);
    REQUIRE_FALSE(env.safe_asks.empty());
    d2.on_safepos(env.safe_asks.back().seq, true, env.safe_asks.back().wanted);
    run(d2, c, 0.5);
    CHECK(env.spawn_calls == spawns_before + 1);
    CHECK(env.objs.size() == 2);
    bool found = false;
    for (const auto& m : env.spawns_sent) found = found || m[0].net_id == net;
    CHECK(found);
  }

  SECTION("the listing is unavailable: nothing is respawned") {
    env.enumeration = false;
    const auto asks_before = env.safe_asks.size();
    AvatarDirector d2(env);
    d2.load_records(records_from_text(saved).records);
    Clock c;
    run(d2, c, 5);
    CHECK(env.spawn_calls == spawns_before);
    CHECK(env.safe_asks.size() == asks_before);
  }
}

TEST_CASE("avatars.director: a destroyed ship is respawned and the snapshot lists the avatars with current poses", "[avatars]") {
  FakeEnv env;
  AvatarDirector d(env);
  Clock c;
  d.on_roster(roster_with({{2, {"Alice", 1}}}));
  const auto id = provision(d, env, c, 2, 1, "Alice");
  env.objs[id].pose.x = 4242;
  const auto snap = d.snapshot();
  REQUIRE(snap.size() == 1);
  CHECK(snap[0].pose.x == 4242);
  CHECK(snap[0].owner == "x4mp_team_1");
  CHECK(snap[0].sector_macro == "sec_a");
  CHECK(snap[0].online);
  env.objs.erase(id);
  run(d, c, 1.5);
  REQUIRE_FALSE(env.safe_asks.empty());
  d.on_safepos(env.safe_asks.back().seq, true, env.safe_asks.back().wanted);
  run(d, c, 0.5);
  CHECK(env.objs.size() == 1);
  CHECK(d.stats().respawned == 1);
}

TEST_CASE("avatars.director: no avatar before the authority is connected and the sector table exists", "[avatars]") {
  FakeEnv env;
  AvatarDirector d(env);
  Clock c;
  d.on_roster(roster_with({{2, {"Alice", 1}}}));
  d.on_player_ship(request(2));
  env.ready = false;
  run(d, c, 1);
  CHECK(env.spawn_calls == 0);
  env.ready = true;
  env.net = false;
  run(d, c, 1);
  CHECK(env.safe_asks.empty());
  env.net = true;
  run(d, c, 0.2);
  CHECK(env.safe_asks.size() == 1);
}

TEST_CASE("avatars.director: a player moving team re-owns the avatar (M3-18)", "[avatars]") {
  FakeEnv env;
  AvatarDirector d(env);
  Clock c;
  d.on_roster(roster_with({{2, {"Alice", 1}}}));
  const auto id = provision(d, env, c, 2, 1, "Alice");
  const auto net = env.spawns_sent.at(0).at(0).net_id;
  env.dress_asks.clear();
  CHECK(env.owners_sent.empty());

  SECTION("the roster's new team: SetComponentOwner once, one EntityChange, record + persist follow, ship stays inert and unmoved") {
    const auto pose = env.objs[id].pose;
    d.on_roster(roster_with({{2, {"Alice", 2}}}, false));
    run(d, c, 0.5);
    CHECK(env.objs[id].owner == "x4mp_team_2");
    CHECK(env.set_owner_calls == 1);
    CHECK_FALSE(env.objs[id].active);
    CHECK(env.objs[id].name == "[MP] Alice");
    CHECK(env.objs[id].pose.x == pose.x);
    REQUIRE(env.owners_sent.size() == 1);
    CHECK(env.owners_sent[0].net == net);
    CHECK(env.owners_sent[0].team == 2);
    CHECK(env.owners_sent[0].faction == "x4mp_team_2");
    CHECK(d.views()[0].rec.team == 2);
    CHECK(d.views()[0].rec.owner == "x4mp_team_2");
    CHECK(d.stats().reowned == 1);
    CHECK(env.spawn_calls == 1);  // never a second ship
    CHECK(env.removed.empty());
    d.persist_now();
    CHECK(records_from_text(env.saved).records.at(0).owner == "x4mp_team_2");
    // the same roster again changes nothing
    d.on_roster(roster_with({{2, {"Alice", 2}}}, false));
    run(d, c, 3);
    CHECK(env.set_owner_calls == 1);
    CHECK(env.owners_sent.size() == 1);
  }

  SECTION("a failing game call is retried, the change is sent only after it worked") {
    env.owner_fails = true;
    d.on_roster(roster_with({{2, {"Alice", 2}}}, false));
    run(d, c, 5);
    CHECK(env.set_owner_calls >= 2);
    CHECK(env.owners_sent.empty());
    env.owner_fails = false;
    run(d, c, 3);
    CHECK(env.objs[id].owner == "x4mp_team_2");
    CHECK(env.owners_sent.size() == 1);
  }

  SECTION("waits for the team factions") {
    env.factions = false;
    d.on_roster(roster_with({{2, {"Alice", 2}}}, false));
    run(d, c, 3);
    CHECK(env.set_owner_calls == 0);
    env.factions = true;
    run(d, c, 3);
    CHECK(env.objs[id].owner == "x4mp_team_2");
    CHECK(env.owners_sent.size() == 1);
  }

  SECTION("a rebind after a save load finds the ship under the new faction (record written after the move)") {
    d.on_roster(roster_with({{2, {"Alice", 2}}}, false));
    run(d, c, 0.5);
    d.persist_now();
    const std::string saved = env.saved;
    env.renumber();
    AvatarDirector d2(env);
    d2.load_records(records_from_text(saved).records);
    run(d2, c, 0.5);
    CHECK(d2.stats().bound == 1);
    CHECK(env.spawn_calls == 1);
  }

  SECTION("a rebind where the ship wears the new faction but the record is old: found through the pending team") {
    d.persist_now();
    const std::string saved = env.saved;  // team 1
    env.objs[id].owner = "x4mp_team_2";   // the move was applied in game, the record write never happened
    env.renumber();
    AvatarDirector d2(env);
    d2.on_roster(roster_with({{2, {"Alice", 1}}}));
    d2.load_records(records_from_text(saved).records);
    d2.on_roster(roster_with({{2, {"Alice", 2}}}, false));
    run(d2, c, 0.5);
    CHECK(d2.stats().bound == 1);
    CHECK(d2.stats().lost == 0);
    CHECK(env.spawn_calls == 1);
  }

  SECTION("a lost avatar of a moved player is respawned under the new team") {
    d.persist_now();
    const std::string saved = env.saved;
    env.objs.erase(id);
    AvatarDirector d2(env);
    d2.on_roster(roster_with({{2, {"Alice", 2}}}));
    d2.load_records(records_from_text(saved).records);
    run(d2, c, 0.5);
    REQUIRE_FALSE(env.safe_asks.empty());
    d2.on_safepos(env.safe_asks.back().seq, true, env.safe_asks.back().wanted);
    run(d2, c, 0.5);
    CHECK(env.count_owned("x4mp_team_2") == 1);
    CHECK(env.count_owned("x4mp_team_1") == 0);
  }
}
