#include <set>

#include <catch2/catch_test_macros.hpp>

#include "core/ghost/registry.h"

using namespace x4mp::ghost;
using x4mp::session::MemoryStash;

namespace {
Registries sample_state() {
  Registries r;
  r.netmap.bind(101, 5001, NetKind::Ghost, 2);
  r.netmap.bind(102, 5002, NetKind::Ghost, 3);
  r.netmap.bind(100, 4000, NetKind::Self, 1);
  r.netmap.bind(200, 6001, NetKind::Avatar, 4);
  r.ghosts.add({101, 2, 5001, 1});
  r.ghosts.add({102, 3, 5002, 2});
  r.avatars.add({4, 200, 6001});
  return r;
}
}  // namespace

TEST_CASE("NetMap binds both ways and replaces conflicts", "[ghost][registry]") {
  NetMap m;
  m.bind(1, 10, NetKind::Ghost, 2);
  m.bind(2, 20, NetKind::Self, 1);
  CHECK(m.find_net(1)->local_id == 10);
  CHECK(m.find_local(20)->net_id == 2);
  CHECK(m.find_local(0) == nullptr);
  m.bind(3, 10, NetKind::Ghost, 2);  // local id 10 moves to net 3: net 1 is gone
  CHECK(m.find_net(1) == nullptr);
  CHECK(m.find_local(10)->net_id == 3);
  CHECK(m.unbind_net(3));
  CHECK_FALSE(m.unbind_net(3));
  CHECK(m.size() == 1);
}

TEST_CASE("stash round trip", "[ghost][registry][stash]") {
  const Registries a = sample_state();
  MemoryStash stash;
  a.save(stash, 77);
  Registries b;
  const AdoptReport rep = b.adopt(stash, 77, [](LocalId) { return true; });
  CHECK(rep.found);
  CHECK_FALSE(rep.parse_error);
  CHECK_FALSE(rep.epoch_mismatch);
  CHECK(rep.kept_net == 4);
  CHECK(rep.kept_ghosts == 2);
  CHECK(rep.kept_avatars == 1);
  CHECK(rep.dropped_local_ids.empty());
  CHECK(rep.rebuilt == 0);
  CHECK(rep.conflicts == 0);
  // same content (order-insensitive)
  CHECK(b.to_blob(77).size() == a.to_blob(77).size());
  for (const auto& e : a.netmap.entries()) CHECK(*b.netmap.find_net(e.net_id) == e);
  for (const auto& e : a.ghosts.entries()) CHECK(*b.ghosts.find(e.net_id) == e);
  for (const auto& e : a.avatars.entries()) CHECK(*b.avatars.find_player(e.player_id) == e);
  // the blob is text (survives any string-based stash) and stable across a second save
  Registries c;
  std::uint64_t epoch = 0;
  const auto parsed = Registries::from_blob(a.to_blob(5), epoch);
  REQUIRE(parsed.has_value());
  CHECK(epoch == 5);
  CHECK(parsed->to_blob(5).size() == a.to_blob(5).size());
}

TEST_CASE("adoption keeps valid ids and drops invalid ones", "[ghost][registry][stash]") {
  MemoryStash stash;
  sample_state().save(stash, 9);
  const std::set<LocalId> alive{5001, 4000, 6001};  // 5002 was destroyed during the reload
  Registries b;
  const AdoptReport rep = b.adopt(stash, 9, [&](LocalId id) { return alive.count(id) != 0; });
  CHECK(rep.kept_ghosts == 1);
  CHECK(rep.dropped_ghosts == 1);
  CHECK(rep.kept_net == 3);
  CHECK(rep.dropped_net == 1);
  CHECK(rep.kept_avatars == 1);
  CHECK(rep.dropped_avatars == 0);
  REQUIRE(rep.dropped_local_ids.size() >= 1);
  for (LocalId id : rep.dropped_local_ids) CHECK(id == 5002);
  CHECK(b.ghosts.find(101) != nullptr);
  CHECK(b.ghosts.find(102) == nullptr);
  CHECK(b.netmap.find_net(102) == nullptr);
  CHECK(b.netmap.find_net(100)->kind == NetKind::Self);
  CHECK(b.avatars.find_player(4)->local_id == 6001);
}

TEST_CASE("adoption: everything invalid, local id 0, wrong epoch, garbage", "[ghost][registry][stash]") {
  {
    MemoryStash stash;
    sample_state().save(stash, 1);
    Registries b;
    const AdoptReport rep = b.adopt(stash, 1, [](LocalId) { return false; });
    CHECK(b.empty());
    CHECK(rep.kept_net + rep.kept_ghosts + rep.kept_avatars == 0);
  }
  {
    Registries r;
    r.ghosts.add({1, 1, 0, 0});  // never spawned
    MemoryStash stash;
    r.save(stash, 1);
    Registries b;
    (void)b.adopt(stash, 1, [](LocalId) { return true; });
    CHECK(b.ghosts.size() == 0);
  }
  {
    MemoryStash stash;
    sample_state().save(stash, 1);
    Registries b;
    const AdoptReport rep = b.adopt(stash, 2, [](LocalId) { return true; });  // another universe
    CHECK(rep.epoch_mismatch);
    CHECK(b.empty());
    CHECK_FALSE(stash.get(kStashKey).has_value());  // erased: never adopted later by mistake
  }
  for (const char* junk : {"", "garbage", "x4gr 1 5\n", "x4gr 2 5\nend 0\n", "x4gr 1 5\nN 1 2 9 0\nend 1\n",
                           "x4gr 1 5\nG 1 2 3\nend 1\n", "x4gr 1 5\nG 1 2 3 4\nend 2\n", "x4gr 1 5\nend 0\nG 1 2 3 4\n",
                           "x4gr 1 5\nG 1 2 3 4 extra\nend 1\n", "x4gr 1 5\nG 1 2 3 4\nend 1"}) {
    MemoryStash stash;
    stash.put(kStashKey, junk);
    Registries b;
    const AdoptReport rep = b.adopt(stash, 5, [](LocalId) { return true; });
    CHECK((rep.parse_error || !rep.found));
    CHECK(b.empty());
  }
  {
    MemoryStash stash;  // no blob at all
    Registries b;
    const AdoptReport rep = b.adopt(stash, 1, [](LocalId) { return true; });
    CHECK_FALSE(rep.found);
    CHECK(b.empty());
  }
}

TEST_CASE("adoption reconciles registries that disagree", "[ghost][registry][stash]") {
  Registries r;
  r.ghosts.add({7, 2, 700, 1});                    // ghost the NetMap does not know
  r.netmap.bind(8, 800, NetKind::Ghost, 3);        // NetMap ghost without a ghost entry
  r.ghosts.add({9, 4, 900, 1});                    // contradicts the NetMap below
  r.netmap.bind(9, 901, NetKind::Ghost, 4);
  MemoryStash stash;
  r.save(stash, 3);
  Registries b;
  const AdoptReport rep = b.adopt(stash, 3, [](LocalId) { return true; });
  CHECK(b.netmap.find_net(7)->local_id == 700);
  CHECK(b.ghosts.find(8)->local_id == 800);
  CHECK(b.ghosts.find(9)->local_id == 901);  // the NetMap wins; the contradicting entry was dropped, then rebuilt from the NetMap
  CHECK(rep.rebuilt >= 3);
  CHECK(rep.conflicts >= 1);
  // a local id used twice is adopted once
  Registries d;
  d.ghosts.add({20, 1, 2000, 0});
  d.ghosts.add({21, 2, 2000, 0});
  MemoryStash s2;
  d.save(s2, 1);
  Registries e;
  const AdoptReport rep2 = e.adopt(s2, 1, [](LocalId) { return true; });
  CHECK(e.ghosts.size() == 1);
  CHECK(rep2.conflicts == 1);
}
