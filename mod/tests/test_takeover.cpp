// M3-12 client takeover: the AvatarTakeover state machine over a fake game (no game, no session).
#include <algorithm>
#include <deque>
#include <map>
#include <set>

#include <catch2/catch_test_macros.hpp>

#include "core/authority/checkpoint_messages.h"
#include "core/authority/entity_spawn.h"
#include "features/avatars/avatar_takeover.h"
#include "features/avatars/avatar_wire.h"

using namespace x4mp::features::avatars;

namespace {

struct Obj {
  std::string macro, owner, name, idcode;
  std::uint64_t sector = 0;
  Pose pose;
};

struct FakeTake final : ITakeoverEnv {
  bool is_ready = true, enumeration = true, can_remove_current = false;
  std::uint16_t player = 7;
  std::map<std::uint64_t, Obj> objs;
  std::uint64_t next_id = 6000;
  std::uint64_t ship_standing = 0;  // the host's ship copy the player wakes up in
  std::uint64_t seat = 0;           // GetPlayerOccupiedShipID (0 = standing / none)
  std::uint32_t sits = 0;
  std::map<std::uint16_t, std::uint64_t> sector_of_index{{1, 900}, {2, 901}};
  // behaviour switches
  int deny_teleports = 0;           // CanTeleportPlayerTo answers this many "denied" first
  std::string deny_text = "the pilot is busy";
  bool teleport_returns_false = false;
  int seat_delay_frames = 0;        // frames between TeleportPlayerTo and the player being reported in the ship
  bool seat_never = false;
  int spawn_fail = 0;
  std::set<std::uint64_t> remove_refuse_forever;
  int remove_refuse_first = 0;
  bool request_ok = true;
  // recorded
  std::vector<std::string> calls;   // ordered game calls
  std::vector<std::uint64_t> removed;
  int requests = 0, spawns = 0, teleports = 0, can_calls = 0;
  bool held = false;
  int hold_edges = 0;
  std::uint32_t net_id_set = 0;
  std::vector<std::pair<bool, std::string>> hints;
  std::string record;
  std::vector<std::string> logs;
  std::uint64_t pending_seat = 0;
  int pending_frames = 0;
  // M3-30 create mode: MD create_ship answers
  struct PendingCreate {
    std::uint32_t seq;
    bool ok;
    std::uint64_t id;
    int frames;
  };
  std::vector<PendingCreate> create_pending;
  std::deque<Created> create_answers;
  int create_delay = 1, create_fail = 0, create_silent = 0, create_requests = 0, places = 0;
  bool create_refuse_send = false;
  std::vector<std::string> create_names;
  std::vector<std::uint32_t> create_seqs;
  std::vector<Pose> create_poses, placed_poses;

  void tick() {
    for (auto it = create_pending.begin(); it != create_pending.end();) {
      if (it->frames-- <= 0) {
        create_answers.push_back({it->seq, it->ok, it->id});
        it = create_pending.erase(it);
      } else {
        ++it;
      }
    }
    if (pending_seat != 0 && pending_frames-- <= 0) {
      seat = pending_seat;
      pending_seat = 0;
    }
  }
  std::uint64_t add(const std::string& macro, const std::string& owner, const std::string& name, const std::string& code, std::uint64_t sector = 900, Pose p = {}) {
    const auto id = next_id++;
    objs[id] = {macro, owner, name, code, sector, p};
    return id;
  }

  bool ready() override { return is_ready; }
  std::uint16_t own_player_id() override { return player; }
  std::uint64_t own_ship() override { return seat != 0 ? seat : ship_standing; }
  std::uint64_t seated_ship() override { return seat; }
  std::uint32_t sit_downs() override { return sits; }
  bool valid(std::uint64_t id) override { return objs.count(id) != 0; }
  std::string idcode(std::uint64_t id) override {
    const auto it = objs.find(id);
    return it == objs.end() ? "" : it->second.idcode;
  }
  std::optional<std::vector<Candidate>> team_candidates() override {
    if (!enumeration) return std::nullopt;
    std::vector<Candidate> out;
    for (const auto& [id, o] : objs) {
      if (o.owner.rfind("x4mp_team_", 0) != 0) continue;
      out.push_back({id, o.idcode, o.name, o.owner, o.pose});
    }
    return out;
  }
  std::string faction_of_team(std::uint16_t team) override { return team == 0 ? "" : "x4mp_team_" + std::to_string(team); }
  std::uint64_t sector_id_of_index(std::uint16_t index) override {
    const auto it = sector_of_index.find(index);
    return it == sector_of_index.end() ? 0 : it->second;
  }
  std::uint64_t spawn(const std::string& macro, std::uint64_t sector, const Pose& pose, const std::string& owner) override {
    calls.push_back("spawn");
    if (spawn_fail > 0) {
      --spawn_fail;
      return 0;
    }
    ++spawns;
    return add(macro, owner, "", "NEW-" + std::to_string(next_id), sector, pose);
  }
  bool set_owner(std::uint64_t id, const std::string& faction) override {
    calls.push_back("owner:" + faction);
    if (objs.count(id)) objs[id].owner = faction;
    return true;
  }
  void activate(std::uint64_t, bool a) override { calls.push_back(a ? "activate" : "deactivate"); }
  std::optional<std::string> can_teleport(std::uint64_t) override {
    ++can_calls;
    if (deny_teleports > 0) {
      --deny_teleports;
      return deny_text;
    }
    return std::string("granted");
  }
  bool teleport(std::uint64_t id) override {
    calls.push_back("teleport");
    if (teleport_returns_false) return false;
    ++teleports;
    if (!seat_never) {
      pending_seat = id;
      pending_frames = seat_delay_frames;
    }
    return true;
  }
  bool remove(std::uint64_t id) override {
    calls.push_back("remove:" + std::to_string(id));
    if (id == seat && !can_remove_current) return false;  // the player's own ship is never removed
    if (remove_refuse_forever.count(id)) return false;
    if (remove_refuse_first > 0) {
      --remove_refuse_first;
      return false;
    }
    objs.erase(id);
    removed.push_back(id);
    return true;
  }
  bool send_request(std::uint64_t) override {
    if (!request_ok) return false;
    ++requests;
    return true;
  }
  void set_own_net_id(std::uint32_t id) override { net_id_set = id; calls.push_back("net_id"); }
  void hold_states(bool h) override {
    held = h;
    ++hold_edges;
    calls.push_back(h ? "hold" : "release");
  }
  bool ghosts_held = false;
  void hold_ghosts(bool h) override {
    ghosts_held = h;
    calls.push_back(h ? "ghosts_hold" : "ghosts_release");
  }
  void show_hint(bool show, const std::string& text) override { hints.emplace_back(show, text); }
  void save_record(const std::string& text) override { record = text; }
  void log(LogLevel, const std::string& text) override { logs.push_back(text); }
  std::vector<std::string> probes;  // M3-23: the knowledge probes the machine asked for, in order
  void probe(const std::string& tag) override { probes.push_back(tag); }
  bool request_create(std::uint32_t seq, const std::string& macro, std::uint64_t sector, const Pose& pose, const std::string& name, const StarterSpec&) override {
    calls.push_back("create");
    if (create_refuse_send) return false;
    ++create_requests;
    create_seqs.push_back(seq);
    create_names.push_back(name);
    create_poses.push_back(pose);
    if (create_silent > 0) {
      --create_silent;
      return true;
    }
    if (create_fail > 0) {
      --create_fail;
      create_pending.push_back({seq, false, 0, create_delay});
      return true;
    }
    const auto id = add(macro, "player", name, "CRE-" + std::to_string(next_id), sector, pose);  // MD creates it player-owned
    create_pending.push_back({seq, true, id, create_delay});
    return true;
  }
  std::optional<Created> take_created() override {
    if (create_answers.empty()) return std::nullopt;
    const auto c = create_answers.front();
    create_answers.pop_front();
    return c;
  }
  void place(std::uint64_t id, std::uint64_t, const Pose& pose) override {
    calls.push_back("place");
    ++places;
    placed_poses.push_back(pose);
    if (objs.count(id)) objs[id].pose = pose;
  }
};

constexpr double kDt = 1.0 / 60.0;

enum class Mode { Reown, Keep, Create };

struct Rig {
  FakeTake env;
  AvatarTakeover m{env};
  x4mp::config::DiagConfig diag;
  double now = 100.0;
  std::uint64_t host = 0;
  // M3-30: three modes (create = default, keep = diag.takeover_keep_own_ship, reown = diag.takeover_mode_reown); most cases below test the old reown flow.
  explicit Rig(Mode mode = Mode::Reown) {
    host = env.ship_standing = env.add("ship_arg_m_fighter_01_a_macro", "player", "Host ship", "HST-001");
    diag.takeover_mode_reown = mode == Mode::Reown;
    diag.takeover_keep_own_ship = mode == Mode::Keep;
    m.set_diag(diag);
  }
  void frames(int n) {
    for (int i = 0; i < n; ++i) {
      now += kDt;
      env.tick();
      m.step(now);
    }
  }
  void seconds(double s) { frames(static_cast<int>(s / kDt + 0.5)); }
  [[nodiscard]] bool gone(std::uint64_t id) const { return env.objs.count(id) == 0; }
  AvatarInfo grant(std::uint32_t net = 41, const std::string& code = "AVA-001", const std::string& name = "[MP] Me") {
    AvatarInfo a;
    a.net_id = net;
    a.owner_player = env.player;
    a.controller_player = env.player;
    a.owner_team = 1;
    a.idcode = code;
    a.name = name;
    a.sector = 1;
    a.pose = {10, 20, 30, 0.5, 0, 0};
    return a;
  }
  // an avatar of someone else as the checkpoint holds it
  std::uint64_t other_avatar(const std::string& name, const std::string& code, std::uint16_t team = 1) {
    return env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_" + std::to_string(team), name, code);
  }
};

}  // namespace

// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("takeover: the record text round-trips and bad text is refused", "[takeover][record]") {
  TakeoverRecord r;
  r.phase = TakeoverRecord::Phase::Seated;
  r.player_id = 7;
  r.net_id = 41;
  r.idcode = "AVA-001";
  r.avatar_id = 6001;
  r.host_id = 6000;
  r.host_idcode = "HST-001";
  r.remove = {{6000, "HST-001"}, {6002, "XYZ-9"}};
  const auto text = takeover_record_to_text(r);
  const auto back = takeover_record_from_text(text);
  REQUIRE(back);
  CHECK(*back == r);
  TakeoverRecord empty;
  empty.phase = TakeoverRecord::Phase::Progress;
  const auto b2 = takeover_record_from_text(takeover_record_to_text(empty));
  REQUIRE(b2);
  CHECK(*b2 == empty);
  CHECK_FALSE(takeover_record_from_text(""));
  CHECK_FALSE(takeover_record_from_text("nonsense\n1|2|3"));
  CHECK_FALSE(takeover_record_from_text("x4tk 1\n9|7|41|A|1|2|B|"));      // phase out of range
  CHECK_FALSE(takeover_record_from_text("x4tk 1\n1|7|41|A|1|2|B|oops"));  // bad pending entry
}

TEST_CASE("takeover: pick_avatar_copy tiers, owner preference and exclusions", "[takeover][pick]") {
  AvatarInfo g;
  g.idcode = "AVA-001";
  g.name = "[MP] Me";
  g.pose = {0, 0, 0, 0, 0, 0};
  std::vector<Candidate> c;
  c.push_back({1, "AVA-001", "[MP] Me", "x4mp_team_2", {500, 0, 0, 0, 0, 0}});
  c.push_back({2, "AVA-001", "[MP] Me", "x4mp_team_1", {900, 0, 0, 0, 0, 0}});
  c.push_back({3, "AVA-001", "other", "x4mp_team_1", {1, 0, 0, 0, 0, 0}});
  c.push_back({4, "AVA-001", "[MP] Me", "argon", {0, 0, 0, 0, 0, 0}});  // not a team ship: never
  CHECK(pick_avatar_copy(g, "x4mp_team_1", c) == 2);  // tier 0, the expected owner beats distance
  CHECK(pick_avatar_copy(g, "", c) == 1);             // no expectation: the nearest of tier 0
  CHECK(pick_avatar_copy(g, "x4mp_team_1", c, 2) == 1);  // 2 excluded (the host copy)
  c.erase(c.begin(), c.begin() + 2);
  CHECK(pick_avatar_copy(g, "x4mp_team_1", c) == 3);  // tier 1: the idcode alone
  g.idcode = "ZZZ";
  CHECK_FALSE(pick_avatar_copy(g, "x4mp_team_1", c));
  g.name = "other";
  CHECK(pick_avatar_copy(g, "x4mp_team_1", c) == 3);  // tier 2: the name alone
  AvatarInfo none;
  CHECK_FALSE(pick_avatar_copy(none, "x4mp_team_1", c));  // nothing to match on
}

TEST_CASE("takeover: back-off table", "[takeover][backoff]") {
  const double expect[] = {2, 2, 2, 3, 3, 4, 4, 5, 5, 5};
  for (int i = 1; i <= 10; ++i) CHECK(teleport_backoff_s(i) == expect[i - 1]);
  CHECK(teleport_backoff_s(11) == 20.0);
  CHECK(teleport_backoff_s(500) == 20.0);
  CHECK(teleport_backoff_s(0) == 2.0);
}

// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("takeover: a new player (no avatar in the save) spawns a copy, teleports, and removes the host copy only after the guard", "[takeover][flow]") {
  Rig r;
  r.env.seat_delay_frames = 1;
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001", 2);
  r.frames(5);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Requesting);
  CHECK(r.env.held);            // the PlayerState stream is held from the first ready step
  CHECK(r.env.ghosts_held);     // and so are the ghosts (they must not stand on a copy that is about to go)
  CHECK(r.env.requests == 1);   // one request, not one per frame
  r.seconds(9);
  CHECK(r.env.requests == 1);
  r.seconds(2);
  CHECK(r.env.requests == 2);   // re-asked after 10 s

  r.m.on_spawn_avatars({r.grant()});
  r.frames(1);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Locating);
  r.frames(1);
  CHECK(r.env.spawns == 1);  // not in the save: a local copy is spawned, as "player", at the avatar's pose
  const auto avatar = r.m.avatar_id();
  REQUIRE(avatar != 0);
  CHECK(r.env.objs[avatar].owner == "player");
  CHECK(r.env.objs[avatar].sector == 900);
  CHECK(r.env.objs[avatar].pose.x == 10);
  CHECK(r.env.objs[avatar].macro == std::string(kBasicStarterMacro));
  // frame by frame from the teleport: nothing is removed until the guard saw 10 consecutive frames
  int guard_frames_seen = 0;
  for (int i = 0; i < 40 && r.m.stage() != AvatarTakeover::Stage::Done; ++i) {
    r.frames(1);
    if (r.env.seat == avatar) ++guard_frames_seen;
    if (guard_frames_seen < AvatarTakeover::kGuardFrames) {
      CHECK(r.env.removed.empty());
      CHECK(r.env.held);
      CHECK(r.env.net_id_set == 0);
    }
  }
  REQUIRE(r.m.done());
  CHECK(r.env.net_id_set == 41);
  CHECK_FALSE(r.env.held);
  CHECK_FALSE(r.env.ghosts_held);
  CHECK(std::find(r.env.calls.begin(), r.env.calls.end(), "ghosts_release") > std::find(r.env.calls.begin(), r.env.calls.end(), "remove:" + std::to_string(r.host)));  // after the removal
  CHECK(r.gone(r.host));          // the vacated host ship copy
  CHECK(r.gone(bob));             // another avatar's copy ("[MP] " team ship)
  CHECK_FALSE(r.gone(avatar));    // never the ship the player is in
  CHECK(r.env.teleports == 1);
  // the order of the game calls: spawn -> activate -> teleport -> net_id/release -> removals
  const auto pos = [&](const std::string& c) { return std::find(r.env.calls.begin(), r.env.calls.end(), c) - r.env.calls.begin(); };
  CHECK(pos("spawn") < pos("activate"));
  CHECK(pos("activate") < pos("teleport"));
  CHECK(pos("teleport") < pos("net_id"));
  CHECK(pos("net_id") < pos("remove:" + std::to_string(r.host)));
  CHECK(r.env.record.find("x4tk 1") == 0);
  const auto rec = takeover_record_from_text(r.env.record);
  REQUIRE(rec);
  CHECK(rec->phase == TakeoverRecord::Phase::Done);
}

TEST_CASE("takeover diag (M3-23): a knowledge probe is asked after each stage, in order", "[takeover][diag]") {
  Rig r;
  r.env.seat_delay_frames = 1;
  r.frames(5);
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(2);
  REQUIRE(r.m.done());
  const std::vector<std::string> want = {"takeover: avatar spawned", "takeover: teleported", "takeover: guard confirmed", "takeover: original removed"};
  CHECK(r.env.probes == want);
}

TEST_CASE("takeover diag (M3-23): takeover_keep_original runs the whole takeover but never removes the host ship copy", "[takeover][diag]") {
  Rig r;
  r.env.seat_delay_frames = 1;
  x4mp::config::DiagConfig d;
  d.takeover_keep_original = true;
  d.takeover_mode_reown = true;
  r.m.set_diag(d);
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001", 2);
  r.frames(5);
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(2);
  REQUIRE(r.m.done());
  CHECK_FALSE(r.gone(r.host));  // the original stays
  CHECK(r.gone(bob));           // everything else works as before
  CHECK(r.env.net_id_set == 41);
  CHECK(r.env.teleports == 1);
  CHECK_FALSE(r.env.held);
  CHECK(r.env.probes.back() == "takeover: original removal skipped (diag)");
  CHECK(std::none_of(r.env.removed.begin(), r.env.removed.end(), [&](std::uint64_t id) { return id == r.host; }));
}

TEST_CASE("takeover diag (M3-23): takeover_off does nothing to the game: no request, no spawn, no teleport, no removal; ghosts released, states held", "[takeover][diag]") {
  Rig r;
  x4mp::config::DiagConfig d;
  d.takeover_off = true;
  d.takeover_mode_reown = true;
  r.m.set_diag(d);
  r.frames(5);
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(15);
  CHECK(r.m.done());  // Done at once: the janitor must not wait for it for ever
  CHECK(r.env.requests == 0);
  CHECK(r.env.spawns == 0);
  CHECK(r.env.teleports == 0);
  CHECK(r.env.removed.empty());
  CHECK(r.env.held);  // the host's ship must never move the avatar on the authority
  CHECK_FALSE(r.env.ghosts_held);
  CHECK(r.env.net_id_set == 0);
  CHECK(r.env.record.empty());  // no Done record that a later run without the switch could trust
  CHECK_FALSE(r.gone(r.host));
  CHECK(r.env.probes == std::vector<std::string>{"takeover_off: nothing done"});
}

TEST_CASE("takeover: an avatar that is in the save is bound by idcode, becomes the player's and nothing is spawned", "[takeover][flow]") {
  Rig r;
  const auto mine = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001", 900, {10, 20, 30, 0.5, 0, 0});
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001", 2);                          // named: found by the name
  const auto carl = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_2", "", "CAR-777");  // the name never applied: found only through the manifest
  const auto cargo = r.env.add("ship_arg_l_trans_01_a_macro", "x4mp_team_1", "Team freighter", "FRT-123");  // a team ship that is NOT an avatar
  AvatarInfo carl_m;
  carl_m.net_id = 52;
  carl_m.owner_player = 9;
  carl_m.idcode = "CAR-777";
  AvatarInfo bob_m;
  bob_m.net_id = 51;
  bob_m.owner_player = 8;
  bob_m.idcode = "BOB-001";
  r.m.set_manifest_avatars({carl_m, bob_m});
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(2);
  REQUIRE(r.m.done());
  CHECK(r.env.spawns == 0);
  CHECK(r.m.avatar_id() == mine);
  CHECK(r.env.objs[mine].owner == "player");
  CHECK(std::find(r.env.calls.begin(), r.env.calls.end(), "owner:player") != r.env.calls.end());
  CHECK(r.gone(r.host));
  CHECK(r.gone(bob));
  CHECK(r.gone(carl));
  CHECK_FALSE(r.gone(cargo));
  CHECK_FALSE(r.gone(mine));
}

TEST_CASE("takeover: with a manifest read only the listed avatar copies go; a named ship the manifest does not list stays", "[takeover][remove]") {
  Rig r;
  const auto listed = r.other_avatar("[MP] Bob", "BOB-001", 2);
  const auto unlisted = r.other_avatar("[MP] Eve", "EVE-001", 2);  // not named by any manifest / EntitySpawn
  AvatarInfo bob_m;
  bob_m.net_id = 51;
  bob_m.owner_player = 8;
  bob_m.idcode = "BOB-001";
  r.m.set_manifest_avatars({bob_m});
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(3);
  REQUIRE(r.m.done());
  CHECK(r.gone(listed));
  CHECK_FALSE(r.gone(unlisted));
  CHECK(r.gone(r.host));
}

TEST_CASE("takeover: a teleport refusal retries with back-off, shows the hint from the 3rd refusal, and a sit-down retries at once", "[takeover][refusal]") {
  Rig r;
  r.env.deny_teleports = 100;
  r.m.on_spawn_avatars({r.grant()});
  r.frames(5);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Teleporting);
  const auto t0 = r.now;
  CHECK(r.env.can_calls == 1);
  CHECK_FALSE(r.m.hint_shown());
  r.seconds(1.5);
  CHECK(r.env.can_calls == 1);  // 2 s back-off
  r.seconds(1.0);
  CHECK(r.env.can_calls == 2);
  r.seconds(2.1);
  CHECK(r.env.can_calls == 3);
  CHECK(r.m.hint_shown());      // the 3rd refusal
  REQUIRE(!r.env.hints.empty());
  CHECK(r.env.hints.back().first);
  CHECK(r.env.hints.back().second == std::string(kTakeoverHint));
  CHECK(r.env.teleports == 0);
  CHECK(r.env.removed.empty());
  CHECK(r.now - t0 < 6.0);
  // a sit-down (the pilot seat, what the hint asks for) retries NOW, not at the next back-off tick
  const int before = r.env.can_calls;
  r.env.sits = 1;
  r.frames(1);
  CHECK(r.env.can_calls == before + 1);
  // and now the game allows it
  r.env.deny_teleports = 0;
  r.env.sits = 2;
  r.frames(1);
  CHECK(r.env.teleports == 1);
  r.seconds(1);
  REQUIRE(r.m.done());
  CHECK_FALSE(r.m.hint_shown());
  CHECK(r.env.hints.back().first == false);  // the hint was hidden again
  CHECK(r.gone(r.host));
  CHECK(r.m.stats().refusals >= 3);
}

TEST_CASE("takeover: the hint is repeated while the refusal lasts and the retries never stop (slow cadence after 10)", "[takeover][refusal]") {
  Rig r;
  r.env.deny_teleports = 1000;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(200);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Teleporting);
  CHECK(r.m.hint_shown());
  CHECK(r.env.can_calls > 10);
  CHECK(r.env.can_calls < 25);  // 10 back-off tries, then one per 20 s
  int shows = 0;
  for (const auto& h : r.env.hints) shows += h.first ? 1 : 0;
  CHECK(shows >= 4);             // repeated roughly every 30 s
  CHECK(r.env.removed.empty());  // nothing ever removed without the takeover
  CHECK(r.env.held);
  // one day it works
  r.env.deny_teleports = 0;
  r.seconds(25);
  CHECK(r.m.done());
}

TEST_CASE("takeover: TeleportPlayerTo returning false and a missing CanTeleportPlayerTo are refusals, not failures", "[takeover][refusal]") {
  Rig r;
  r.env.teleport_returns_false = true;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(10);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Teleporting);
  CHECK(r.m.stats().refusals >= 2);
  CHECK(r.env.removed.empty());
  r.env.teleport_returns_false = false;
  r.seconds(25);
  CHECK(r.m.done());
}

TEST_CASE("takeover: the guard needs 10 CONSECUTIVE frames; a flicker resets it and nothing is removed meanwhile", "[takeover][guard]") {
  Rig r;
  r.m.on_spawn_avatars({r.grant()});
  r.frames(4);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  const auto avatar = r.m.avatar_id();
  r.frames(1);  // seated now
  CHECK(r.env.seat == avatar);
  r.frames(5);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Confirming);
  r.env.seat = 0;  // flicker: the game reports no ship for a frame
  r.frames(1);
  r.env.seat = avatar;
  r.frames(8);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Confirming);  // 8 frames after the flicker: not yet
  CHECK(r.env.removed.empty());
  CHECK(r.m.stats().guard_resets == 1);
  r.frames(3);
  CHECK(r.m.stage() != AvatarTakeover::Stage::Confirming);
  r.frames(3);
  REQUIRE(r.m.done());
  CHECK(r.gone(r.host));
}

TEST_CASE("takeover: a guard that never confirms times out into a refusal and the teleport is tried again", "[takeover][guard]") {
  Rig r;
  r.env.seat_never = true;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(12);
  CHECK(r.env.teleports == 1);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Teleporting);
  CHECK(r.m.stats().refusals >= 1);
  CHECK(r.env.removed.empty());
  CHECK(r.env.held);
  r.env.seat_never = false;
  r.seconds(5);
  CHECK(r.m.done());
  CHECK(r.env.teleports == 2);
}

TEST_CASE("takeover: the player's ship is never removed; a removal is retried a few frames and then left in place", "[takeover][remove]") {
  Rig r;
  const auto stubborn = r.other_avatar("[MP] Dora", "DOR-001");
  r.env.remove_refuse_forever.insert(stubborn);
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(3);
  REQUIRE(r.m.done());
  CHECK_FALSE(r.gone(stubborn));
  CHECK(r.m.stats().remove_refused == 1);
  CHECK(r.gone(r.host));
  CHECK_FALSE(r.gone(r.m.avatar_id()));
  const auto attempts = std::count(r.env.calls.begin(), r.env.calls.end(), "remove:" + std::to_string(stubborn));
  CHECK(attempts == AvatarTakeover::kRemoveFrames);
  // never an attempt on the avatar the player sits in
  CHECK(std::count(r.env.calls.begin(), r.env.calls.end(), "remove:" + std::to_string(r.m.avatar_id())) == 0);
}

TEST_CASE("takeover: nothing is removed while the player is not in the avatar (a later ship change), and the wait ends", "[takeover][remove]") {
  Rig r;
  r.env.deny_teleports = 0;
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001");
  r.env.remove_refuse_first = 1000;  // keep the removal pending
  r.m.on_spawn_avatars({r.grant()});
  for (int i = 0; i < 120 && r.m.stage() != AvatarTakeover::Stage::Removing; ++i) r.frames(1);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Removing);
  r.env.seat = 0;  // the player leaves the avatar before the removals went through
  r.env.calls.clear();
  r.seconds(10);
  CHECK(std::none_of(r.env.calls.begin(), r.env.calls.end(), [](const std::string& c) { return c.rfind("remove:", 0) == 0; }));
  r.seconds(25);
  CHECK(r.m.done());  // gave up waiting; the copies stay
  CHECK_FALSE(r.gone(bob));
  CHECK_FALSE(r.gone(r.host));
}

TEST_CASE("takeover: an enumeration that is not available never leads to a spawn", "[takeover][locate]") {
  Rig r;
  r.env.enumeration = false;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(20);
  CHECK(r.env.spawns == 0);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Locating);
  r.env.enumeration = true;
  r.seconds(3);
  CHECK(r.env.spawns == 1);
  CHECK(r.m.done());
}

TEST_CASE("takeover: an unknown sector index waits, a failing spawn retries with back-off", "[takeover][locate]") {
  Rig r;
  r.env.sector_of_index.clear();
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(5);
  CHECK(r.env.spawns == 0);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Locating);
  r.env.sector_of_index[1] = 900;
  r.env.spawn_fail = 2;
  r.seconds(15);
  CHECK(r.m.stats().spawn_failed == 2);
  CHECK(r.env.spawns == 1);
  CHECK(r.m.done());
}

TEST_CASE("takeover: nothing happens until the node is ready and the own ship is known; a request that is refused by the link is retried", "[takeover][gating]") {
  Rig r;
  r.env.is_ready = false;
  r.frames(30);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Idle);
  CHECK(r.env.requests == 0);
  CHECK_FALSE(r.env.held);  // not even the hold: the hub's client link did that
  r.env.is_ready = true;
  r.env.ship_standing = 0;
  r.frames(5);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Idle);
  r.env.ship_standing = r.host;
  r.env.request_ok = false;
  r.frames(120);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Requesting);
  CHECK(r.env.requests == 0);
  r.env.request_ok = true;
  r.seconds(2);
  CHECK(r.env.requests == 1);
}

TEST_CASE("takeover: the own avatar is told from the others by the player id", "[takeover][grant]") {
  Rig r;
  AvatarInfo other = r.grant(50, "BOB-001", "[MP] Bob");
  other.owner_player = 9;
  other.controller_player = 9;
  r.m.on_spawn_avatars({other});
  r.seconds(1);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Requesting);
  r.m.on_spawn_avatars({r.grant(41)});
  r.seconds(2);
  CHECK(r.m.net_id() == 41);
  CHECK(r.m.done());
}

TEST_CASE("takeover: a parked avatar (controller 0) of the own player is the grant too", "[takeover][grant]") {
  Rig r;
  auto g = r.grant();
  g.controller_player = 0;  // a rejoin: the authority answers with the parked avatar
  r.m.on_spawn_avatars({g});
  r.seconds(2);
  CHECK(r.m.done());
}

// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("takeover reload: after /reloadui in the Done state the new instance is Done at once with the own net id", "[takeover][reload]") {
  Rig r;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(2);
  REQUIRE(r.m.done());
  const auto text = r.env.record;
  const auto avatar = r.m.avatar_id();
  // the DLL is re-initialised: a new machine over the SAME universe
  r.env.held = true;
  r.env.net_id_set = 0;
  r.env.requests = 0;
  AvatarTakeover again(r.env);
  again.set_diag(r.diag);
  again.load_record(*takeover_record_from_text(text));
  r.env.ship_standing = r.host;  // (gone) the selfship status says the ship the player is in
  r.env.ship_standing = avatar;
  again.step(r.now + 1);
  CHECK(again.done());
  CHECK(r.env.net_id_set == 41);
  CHECK_FALSE(r.env.held);
  CHECK(r.env.requests == 0);  // no new request, no new takeover
  CHECK(again.avatar_id() == avatar);
}

TEST_CASE("takeover reload: a reload in the middle of the guard resumes at Confirming (no second spawn, no early removal)", "[takeover][reload]") {
  Rig r;
  r.m.on_spawn_avatars({r.grant()});
  r.frames(7);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  const auto text = r.env.record;
  const auto avatar = r.m.avatar_id();
  REQUIRE(r.env.seat == avatar);
  REQUIRE(r.env.removed.empty());
  AvatarTakeover again(r.env);
  again.set_diag(r.diag);
  again.load_record(*takeover_record_from_text(text));
  const int spawns = r.env.spawns;
  double now = r.now;
  for (int i = 0; i < 5; ++i) {
    now += kDt;
    again.step(now);
    CHECK(r.env.removed.empty());
  }
  CHECK(again.stage() == AvatarTakeover::Stage::Confirming);
  for (int i = 0; i < 10; ++i) {
    now += kDt;
    again.step(now);
  }
  CHECK(again.done());
  CHECK(r.env.spawns == spawns);
  CHECK(r.env.teleports == 1);
  CHECK(r.gone(r.host));  // the host id came from the record: the host copy is not forgotten
  CHECK(r.env.net_id_set == 41);
}

TEST_CASE("takeover reload: a reload after the teleport was started but before the player moved reuses the avatar copy", "[takeover][reload]") {
  Rig r;
  r.env.deny_teleports = 5;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(1);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Teleporting);
  const auto avatar = r.m.avatar_id();
  const auto text = r.env.record;  // phase Progress; the copy is already player-owned (no longer in the team list)
  AvatarTakeover again(r.env);
  again.set_diag(r.diag);
  again.load_record(*takeover_record_from_text(text));
  r.env.deny_teleports = 0;
  AvatarInfo g = r.grant();
  again.on_spawn_avatars({g});
  double now = r.now;
  for (int i = 0; i < 200 && !again.done(); ++i) {
    now += kDt;
    r.env.tick();
    again.step(now);
  }
  CHECK(again.done());
  CHECK(r.env.spawns == 1);  // still the first copy only
  CHECK(again.avatar_id() == avatar);
  CHECK(r.gone(r.host));
}

TEST_CASE("takeover reload: a record of another universe (the avatar's idcode differs) is dropped and the takeover starts over", "[takeover][reload]") {
  Rig r;
  TakeoverRecord rec;
  rec.phase = TakeoverRecord::Phase::Done;
  rec.player_id = r.env.player;
  rec.net_id = 41;
  rec.idcode = "OLD-001";
  rec.avatar_id = r.host;  // the id exists in the new universe, but it is another object
  rec.host_id = r.host;
  rec.host_idcode = "HST-001";
  r.m.load_record(rec);
  r.frames(2);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Requesting);  // not Done: the record was not trusted
  CHECK(r.env.net_id_set == 0);
  CHECK(r.env.held);
  CHECK(r.env.requests == 1);  // it asked for the avatar
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(3);
  CHECK(r.m.done());
  CHECK(r.env.spawns == 1);
}

TEST_CASE("takeover reload: a Seated record continues the removal; stale ids are skipped", "[takeover][reload]") {
  Rig r;
  const auto avatar = r.env.add("ship_arg_s_fighter_01_a_macro", "player", "[MP] Me", "AVA-001");
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001");
  r.env.seat = avatar;
  r.env.ship_standing = avatar;
  TakeoverRecord rec;
  rec.phase = TakeoverRecord::Phase::Seated;
  rec.player_id = r.env.player;
  rec.net_id = 41;
  rec.idcode = "AVA-001";
  rec.avatar_id = avatar;
  rec.host_id = r.host;
  rec.host_idcode = "HST-001";
  rec.remove = {{r.host, "HST-001"}, {bob, "BOB-001"}, {bob + 1000, "GONE-1"}, {avatar, "AVA-001"}};  // the last two must be skipped
  r.m.load_record(rec);
  r.seconds(1);
  REQUIRE(r.m.done());
  CHECK(r.gone(r.host));
  CHECK(r.gone(bob));
  CHECK_FALSE(r.gone(avatar));
  CHECK(r.env.requests == 0);
  CHECK(r.env.net_id_set == 41);
}

TEST_CASE("takeover: the session ending releases the state and a new session starts over from the persisted record", "[takeover][session]") {
  Rig r;
  r.env.seat_delay_frames = 1000;  // never gets there in this test
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(1);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  r.m.session_ended();
  CHECK(r.m.stage() == AvatarTakeover::Stage::Idle);
  CHECK(r.m.net_id() == 0);
  r.env.seat_delay_frames = 0;
  r.env.pending_seat = 0;
  r.frames(3);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Requesting);  // asks again; the avatar is unchanged (the authority answers with the same ship)
  CHECK(r.env.requests >= 1);
}

// ---------------------------------------------------------------------------------------------------------------------------
// M3-30 (Finding 4): the DEFAULT takeover keeps the ship the player sits in (the save's own player ship, which the engine records explored space
// for) and binds it to the avatar's net id; the avatar copy of the save goes. diag.takeover_mode_reown restores the old takeover (cases above).
TEST_CASE("takeover M3-30 keep (diag.takeover_keep_own_ship): keeps the player's own ship: no spawn, no re-own, no teleport, the net id is bound after the guard", "[takeover][m330]") {
  Rig r(Mode::Keep);
  r.env.seat = r.host;  // the player is in the ship (standing counts: GetPlayerOccupiedShipID)
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001", 2);
  r.frames(5);
  CHECK(r.env.held);
  r.m.on_spawn_avatars({r.grant()});
  r.frames(3);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Confirming);
  CHECK(r.env.net_id_set == 0);  // nothing is sent before the guard
  r.seconds(1);
  REQUIRE(r.m.done());
  CHECK(r.env.net_id_set == 41);
  CHECK_FALSE(r.env.held);
  CHECK_FALSE(r.env.ghosts_held);
  CHECK(r.env.spawns == 0);
  CHECK(r.env.teleports == 0);
  CHECK(r.env.can_calls == 0);
  CHECK(std::none_of(r.env.calls.begin(), r.env.calls.end(), [](const std::string& c) { return c.rfind("owner:", 0) == 0 || c == "teleport" || c == "spawn"; }));
  CHECK_FALSE(r.gone(r.host));  // the player's ship stays
  CHECK(r.env.objs[r.host].owner == "player");
  CHECK(r.gone(bob));           // other avatars' copies still go
  CHECK(r.m.avatar_id() == r.host);
  CHECK(r.m.host_id() == r.host);
  CHECK(r.m.stats().kept == 1);
  const auto rec = takeover_record_from_text(r.env.record);
  REQUIRE(rec);
  CHECK(rec->phase == TakeoverRecord::Phase::Done);
  CHECK(rec->avatar_id == r.host);
  CHECK(rec->idcode == "HST-001");
  const std::vector<std::string> want = {"takeover: own ship kept", "takeover: guard confirmed", "takeover: done, own ship kept"};
  CHECK(r.env.probes == want);
}

TEST_CASE("takeover M3-30: the avatar copy of the save is removed after the guard, never before; the own ship is never touched", "[takeover][m330]") {
  Rig r(Mode::Keep);
  r.env.seat = r.host;
  const auto copy = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001", 900, {10, 20, 30, 0.5, 0, 0});
  r.frames(2);
  r.m.on_spawn_avatars({r.grant()});
  r.frames(5);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  CHECK_FALSE(r.gone(copy));  // the guard has not confirmed yet
  r.seconds(1);
  REQUIRE(r.m.done());
  CHECK(r.gone(copy));
  CHECK_FALSE(r.gone(r.host));
  CHECK(r.env.spawns == 0);
  CHECK(r.env.objs.count(r.host) == 1);
  CHECK(std::none_of(r.env.removed.begin(), r.env.removed.end(), [&](std::uint64_t id) { return id == r.host; }));
  CHECK(r.env.probes.front() == "takeover: own ship kept (save copy found)");
}

TEST_CASE("takeover M3-30: a flicker resets the guard; a ship change before the guard retargets; a refused copy removal is left in place", "[takeover][m330]") {
  Rig r(Mode::Keep);
  r.env.seat = r.host;
  const auto copy = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001");
  r.env.remove_refuse_forever.insert(copy);
  r.m.on_spawn_avatars({r.grant()});
  r.frames(4);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  r.env.seat = 0;  // flicker
  r.frames(3);
  r.env.seat = r.host;
  r.frames(5);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Confirming);
  CHECK(r.env.net_id_set == 0);
  r.seconds(2);
  REQUIRE(r.m.done());
  CHECK_FALSE(r.gone(copy));  // SafeRemove refused it 30 times: left in place
  CHECK(r.m.stats().remove_refused == 1);

  Rig r2(Mode::Keep);
  const auto other = r2.env.add("ship_arg_m_fighter_01_a_macro", "player", "Other", "OTH-001");
  r2.env.seat = r2.host;
  r2.m.on_spawn_avatars({r2.grant()});
  r2.frames(3);
  r2.env.seat = other;  // the player moved to another ship before the guard confirmed
  r2.seconds(1);
  REQUIRE(r2.m.done());
  CHECK(r2.m.avatar_id() == other);
  CHECK_FALSE(r2.gone(r2.host));
  CHECK_FALSE(r2.gone(other));
}

TEST_CASE("takeover M3-30: no team ship list = the takeover goes on after a while (nothing is spawned either way)", "[takeover][m330]") {
  Rig r(Mode::Keep);
  r.env.seat = r.host;
  r.env.enumeration = false;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(5);
  CHECK(r.m.stage() == AvatarTakeover::Stage::Locating);
  r.seconds(8);
  REQUIRE(r.m.done());
  CHECK(r.env.spawns == 0);
  CHECK(r.env.removed.empty());
}

TEST_CASE("takeover M3-30: reload resumes: Progress keeps the own ship and still removes the avatar copy, Done is Done at once", "[takeover][m330][reload]") {
  Rig r(Mode::Keep);
  r.env.seat = r.host;
  const auto copy = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001");
  r.m.on_spawn_avatars({r.grant()});
  r.frames(4);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  const auto text = r.env.record;
  REQUIRE(takeover_record_from_text(text)->phase == TakeoverRecord::Phase::Progress);
  AvatarTakeover again(r.env);
  again.set_diag(r.diag);
  again.load_record(*takeover_record_from_text(text));
  r.env.held = true;
  double now = r.now;
  for (int i = 0; i < 30 && !again.done(); ++i) {
    now += kDt;
    again.step(now);
  }
  REQUIRE(again.done());
  CHECK(again.avatar_id() == r.host);
  CHECK(r.gone(copy));
  CHECK_FALSE(r.gone(r.host));
  CHECK(r.env.requests == 0);
  CHECK(r.env.net_id_set == 41);

  const auto done_text = r.env.record;
  REQUIRE(takeover_record_from_text(done_text)->phase == TakeoverRecord::Phase::Done);
  AvatarTakeover third(r.env);
  third.set_diag(r.diag);
  third.load_record(*takeover_record_from_text(done_text));
  r.env.held = true;
  r.env.net_id_set = 0;
  third.step(now + 1);
  CHECK(third.done());
  CHECK(r.env.net_id_set == 41);
  CHECK_FALSE(r.env.held);
}

TEST_CASE("takeover M3-30: the reown switch keeps the old takeover (re-own + teleport + host copy removal)", "[takeover][m330]") {
  Rig r(Mode::Reown);
  const auto copy = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001");
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(2);
  REQUIRE(r.m.done());
  CHECK(r.m.avatar_id() == copy);
  CHECK(r.env.teleports == 1);
  CHECK(r.gone(r.host));
  CHECK(r.env.objs[copy].owner == "player");
  CHECK(r.m.stats().kept == 0);
}

// ---------------------------------------------------------------------------------------------------------------------------
// M3-30 DEFAULT (create): the client's ship is created player-owned by MD (create_ship owner=faction.player), never re-owned, at the avatar's pose; the
// teleport / guard / removal are the old ones.
TEST_CASE("takeover M3-30 create: a new player gets an MD-created player-owned ship at the grant's pose; no native spawn, no re-own", "[takeover][m330][create]") {
  Rig r(Mode::Create);
  const auto bob = r.other_avatar("[MP] Bob", "BOB-001", 2);
  r.frames(5);
  CHECK(r.env.held);
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(2);
  REQUIRE(r.m.done());
  CHECK(r.env.create_requests == 1);
  CHECK(r.env.create_names.front().empty());
  CHECK(r.env.create_poses.front().x == 10);
  CHECK(r.env.spawns == 0);  // the native SpawnObjectAtPos2 path is not used
  CHECK(std::none_of(r.env.calls.begin(), r.env.calls.end(), [](const std::string& c) { return c.rfind("owner:", 0) == 0 || c == "spawn"; }));
  const auto ship = r.m.avatar_id();
  CHECK(ship != r.host);
  CHECK(r.env.objs.at(ship).owner == "player");
  CHECK(r.env.seat == ship);
  CHECK(r.env.places == 1);
  CHECK(r.env.placed_poses.front().z == 30);
  CHECK(r.gone(r.host));  // the host copy goes after the guard, as before
  CHECK(r.gone(bob));
  CHECK(r.env.net_id_set == 41);
  CHECK_FALSE(r.env.held);
  const std::vector<std::string> want = {"takeover: avatar spawned", "takeover: teleported", "takeover: guard confirmed", "takeover: original removed"};
  CHECK(r.env.probes == want);
  // order: create, place, activate, teleport, ... removals only after the guard
  const auto at = [&](const std::string& c) { return std::find(r.env.calls.begin(), r.env.calls.end(), c) - r.env.calls.begin(); };
  CHECK(at("create") < at("place"));
  CHECK(at("place") < at("teleport"));
  CHECK(at("teleport") < at("remove:" + std::to_string(r.host)));
}

TEST_CASE("takeover M3-30 create: a team copy of the own avatar in the save gives the pose and is removed after the guard, never re-owned", "[takeover][m330][create]") {
  Rig r(Mode::Create);
  const auto copy = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001", 900, {111, 222, 333, 0, 0, 0});
  r.m.on_spawn_avatars({r.grant()});
  r.frames(8);
  CHECK_FALSE(r.gone(copy));  // nothing is removed before the guard
  CHECK(r.env.objs.at(copy).owner == "x4mp_team_1");
  r.seconds(2);
  REQUIRE(r.m.done());
  CHECK(r.env.create_poses.front().x == 111);  // the copy's pose, not the grant's
  CHECK(r.gone(copy));
  CHECK(r.gone(r.host));
  CHECK(r.m.avatar_id() != copy);
  CHECK(r.env.objs.at(r.m.avatar_id()).owner == "player");
  CHECK(std::none_of(r.env.calls.begin(), r.env.calls.end(), [](const std::string& c) { return c.rfind("owner:", 0) == 0; }));
  CHECK(r.m.stats().bound == 1);
}

TEST_CASE("takeover M3-30 create: a failed or silent MD answer retries; a late answer's surplus ship is removed; no list or no sector waits", "[takeover][m330][create]") {
  Rig r(Mode::Create);
  r.env.create_fail = 1;
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(12);
  REQUIRE(r.m.done());
  CHECK(r.env.create_requests == 2);

  Rig q(Mode::Create);
  q.env.create_silent = 1;  // the first request is never answered
  q.m.on_spawn_avatars({q.grant()});
  q.seconds(40);
  REQUIRE(q.m.done());
  CHECK(q.env.create_requests == 2);
  // a late answer of the first request (while the second is out): its ship is a surplus
  Rig z(Mode::Create);
  z.env.create_silent = 1;
  z.m.on_spawn_avatars({z.grant()});
  z.seconds(16);  // timeout -> the second request is out
  const auto surplus = z.env.add("ship_arg_s_fighter_01_a_macro", "player", "[MP] Me", "LATE-2");
  z.env.create_answers.push_back({z.env.create_seqs.front(), true, surplus});
  z.seconds(10);
  REQUIRE(z.m.done());
  CHECK(z.gone(surplus));
  CHECK_FALSE(z.gone(z.m.avatar_id()));

  Rig n(Mode::Create);
  n.env.enumeration = false;
  n.m.on_spawn_avatars({n.grant()});
  n.seconds(5);
  CHECK(n.env.create_requests == 0);  // never create on an unknown team ship list
  Rig u(Mode::Create);
  u.env.sector_of_index.clear();
  u.m.on_spawn_avatars({u.grant()});
  u.seconds(5);
  CHECK(u.env.create_requests == 0);
  Rig w(Mode::Create);
  w.env.create_refuse_send = true;
  w.m.on_spawn_avatars({w.grant()});
  w.seconds(5);
  CHECK(w.m.stage() == AvatarTakeover::Stage::Locating);
  CHECK(w.m.stats().spawn_failed >= 1);
}

TEST_CASE("takeover M3-30 create: a reload in the guard resumes without a second create and still removes the save's copy", "[takeover][m330][create][reload]") {
  Rig r(Mode::Create);
  r.env.seat_delay_frames = 1000;
  const auto copy = r.env.add("ship_arg_s_fighter_01_a_macro", "x4mp_team_1", "[MP] Me", "AVA-001");
  r.m.on_spawn_avatars({r.grant()});
  r.seconds(1);
  REQUIRE(r.m.stage() == AvatarTakeover::Stage::Confirming);
  const auto ship = r.m.avatar_id();
  const auto text = r.env.record;
  AvatarTakeover again(r.env);
  again.set_diag(r.diag);
  again.load_record(*takeover_record_from_text(text));
  r.env.pending_seat = 0;
  r.env.seat = ship;
  double now = r.now;
  for (int i = 0; i < 40 && !again.done(); ++i) {
    now += kDt;
    again.step(now);
  }
  REQUIRE(again.done());
  CHECK(r.env.create_requests == 1);
  CHECK(r.gone(copy));
  CHECK(r.gone(r.host));
  CHECK_FALSE(r.gone(ship));
}

// ---------------------------------------------------------------------------------------------------------------------------
TEST_CASE("takeover wire: EntitySpawn avatars, manifest avatars and the PlayerShip request round-trip", "[takeover][wire]") {
  // The PlayerShip request is decoded by the authority's decoder
  PlayerShipReq req;
  req.name = "Host ship";
  req.idcode = "HST-001";
  req.sector = 3;
  req.pose = {1500, 0, -1200, 1.0, 0.0, 0.0};
  const auto bytes = encode_player_ship(req, 11, 22, 4242);
  const auto back = decode_player_ship(bytes);
  REQUIRE(back);
  CHECK(back->name == "Host ship");
  CHECK(back->idcode == "HST-001");
  CHECK(back->sector == 3);
  CHECK(back->pose.x == 1500);
  CHECK(back->pose.z == -1200);
  CHECK(back->player_id == 0);  // the server stamps it
  // an EntitySpawn built by the authority's encoder: only the PlayerShip entities come out
  x4mp::authority::EntitySpawnBuilder b(5000.0);
  x4mp::authority::SpawnEntity av;
  av.net_id = 41;
  av.kind = x4mp::authority::SpawnKind::ShipS;
  av.origin = x4mp::authority::SpawnOrigin::PlayerShip;
  av.owner_team = 1;
  av.owner_player = 7;
  av.controller_player = 7;
  av.name = "[MP] Me";
  av.idcode = "AVA-001";
  av.sector = 2;
  av.px = 64 * 100;
  av.py = 0;
  av.pz = -64 * 50;
  REQUIRE(b.add(av));
  x4mp::authority::SpawnEntity npc;
  npc.net_id = 42;
  npc.origin = x4mp::authority::SpawnOrigin::AuthorityRuntime;
  REQUIRE(b.add(npc));
  const auto spawn = b.build();
  REQUIRE(spawn);
  const auto avs = decode_avatar_spawns(*spawn);
  REQUIRE(avs);
  REQUIRE(avs->size() == 1);
  CHECK((*avs)[0].net_id == 41);
  CHECK((*avs)[0].owner_player == 7);
  CHECK((*avs)[0].controller_player == 7);
  CHECK((*avs)[0].owner_team == 1);
  CHECK((*avs)[0].name == "[MP] Me");
  CHECK((*avs)[0].idcode == "AVA-001");
  CHECK((*avs)[0].sector == 2);
  CHECK((*avs)[0].pose.x == 100.0);
  CHECK((*avs)[0].pose.z == -50.0);
  // a manifest written by the checkpoint code
  x4mp::authority::ManifestAvatar ma;
  ma.net_id = 41;
  ma.owner_team = 1;
  ma.owner_player = 7;
  ma.sector = 2;
  ma.idcode = "AVA-001";
  ma.x = 100;
  ma.z = -50;
  ma.controller_player = 0;
  const auto manifest = x4mp::authority::encode_manifest({1, 2}, 5000.0, 50, {}, {}, {ma});
  REQUIRE(manifest);
  const auto mav = decode_manifest_avatars(*manifest);
  REQUIRE(mav);
  REQUIRE(mav->size() == 1);
  CHECK((*mav)[0].net_id == 41);
  CHECK((*mav)[0].idcode == "AVA-001");
  CHECK((*mav)[0].owner_player == 7);
  CHECK((*mav)[0].sector == 2);
  CHECK((*mav)[0].pose.x == 100.0);
  CHECK_FALSE(decode_manifest_avatars(*spawn));  // an EntitySpawn is not a manifest
  CHECK_FALSE(decode_avatar_spawns(*manifest));  // and a manifest is not an EntitySpawn
  CHECK_FALSE(decode_avatar_spawns(std::vector<std::uint8_t>{1, 2, 3}));
  CHECK_FALSE(decode_manifest_avatars(std::vector<std::uint8_t>{1, 2, 3, 4, 5, 6, 7, 8, 9}));
}

// M3-29 ------------------------------------------------------------------------------------------------------------------------------
TEST_CASE("takeover (M3-29): a new avatar net id for the own player after Done replaces the net id and leaves the own ship alone", "[takeover][replaced]") {
  Rig r;
  r.m.on_spawn_avatars({r.grant(41)});
  r.seconds(2);
  REQUIRE(r.m.done());
  const auto avatar = r.m.avatar_id();
  const int teleports = r.env.teleports, requests = r.env.requests, spawns = r.env.spawns;
  r.env.removed.clear();
  r.env.net_id_set = 0;

  // an avatar of somebody else is not "mine replaced"
  auto other = r.grant(77, "OTH-001", "[MP] Bob");
  other.owner_player = other.controller_player = 9;
  r.m.on_spawn_avatars({other});
  CHECK(r.env.net_id_set == 0);

  // the authority re-provisioned the own avatar under a new net id
  r.m.on_spawn_avatars({r.grant(52, "AVA-009")});
  CHECK(r.m.net_id() == 52);
  CHECK(r.env.net_id_set == 52);
  CHECK(r.m.stats().replaced == 1);
  const auto rec = takeover_record_from_text(r.env.record);
  REQUIRE(rec);
  CHECK(rec->phase == TakeoverRecord::Phase::Done);
  CHECK(rec->net_id == 52);
  CHECK(std::any_of(r.env.logs.begin(), r.env.logs.end(), [](const std::string& l) { return l.find("my avatar was replaced") != std::string::npos; }));

  // nothing else happens: no re-takeover, no teleport, no request, no removal; the same spawn again changes nothing
  r.m.on_spawn_avatars({r.grant(52, "AVA-009")});
  r.seconds(3);
  CHECK(r.m.done());
  CHECK(r.m.stats().replaced == 1);
  CHECK(r.env.teleports == teleports);
  CHECK(r.env.requests == requests);
  CHECK(r.env.spawns == spawns);
  CHECK(r.env.removed.empty());
  CHECK(r.m.avatar_id() == avatar);
}
