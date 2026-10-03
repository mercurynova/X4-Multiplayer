// M2-08: the EntitySpawn encoder cannot emit game_time 0 (docs/mod-design.md 6.2, protocol.md EntitySpawn.game_time).

#include <cmath>
#include <filesystem>
#include <fstream>
#include <limits>
#include <random>
#include <sstream>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/authority/checkpoint_messages.h"
#include "core/authority/entity_spawn.h"
#include "session_generated.h"
#include "world_generated.h"

#ifndef X4MP_NATIVE_SRC_DIR
#error "X4MP_NATIVE_SRC_DIR must be defined by the build (see mod/CMakeLists.txt)"
#endif

using namespace x4mp::authority;
namespace P = X4MP::Proto;

namespace {
SpawnEntity ship(std::uint32_t id) {
  SpawnEntity e;
  e.net_id = id;
  e.kind = SpawnKind::ShipM;
  e.origin = SpawnOrigin::PlayerShip;
  e.owner_team = 2;
  e.controller_player = 3;
  e.name = "Test ship";
  e.idcode = "ABC-123";
  e.sector = 7;
  e.px = 640;
  e.py = -64;
  e.pz = 1280;
  return e;
}

const P::EntitySpawn* decode(const SpawnPayload& p) {
  flatbuffers::Verifier v(p.data(), p.size());
  REQUIRE(v.VerifyBuffer<P::EntitySpawn>(nullptr));
  return flatbuffers::GetRoot<P::EntitySpawn>(p.data());
}
}  // namespace

TEST_CASE("spawn builder writes the constructor game_time into every payload", "[authority][spawn]") {
  EntitySpawnBuilder b(1234.5);
  REQUIRE(b.valid());
  REQUIRE(b.add(ship(10)));
  REQUIRE(b.add(ship(11)));
  const auto one = b.build();
  REQUIRE(one.has_value());
  const auto* spawn = decode(*one);
  CHECK(spawn->game_time() == 1234.5);
  REQUIRE(spawn->entities()->size() == 2);
  const auto* r = spawn->entities()->Get(0);
  CHECK(r->net_id() == 10);
  CHECK(r->kind() == P::EntityKind::ShipM);
  CHECK(r->origin() == P::EntityOrigin::PlayerShip);
  CHECK(r->controller_player() == 3);
  CHECK(r->name()->str() == "Test ship");
  CHECK(r->state()->sector() == 7);
  CHECK(r->state()->px() == 640);
}

TEST_CASE("spawn batches all carry the same nonzero game_time", "[authority][spawn]") {
  EntitySpawnBuilder b(99.25);
  for (std::uint32_t i = 1; i <= 25; ++i) REQUIRE(b.add(ship(i)));
  const auto batches = b.build_batches(10);
  REQUIRE(batches.has_value());
  REQUIRE(batches->size() == 3);
  std::size_t total = 0;
  for (const auto& p : *batches) {
    const auto* s = decode(p);
    CHECK(s->game_time() == 99.25);
    total += s->entities()->size();
  }
  CHECK(total == 25);
  CHECK(b.build_batches(0).error() == SpawnError::BadBatchSize);
}

TEST_CASE("is_valid_game_time accepts only finite positive times", "[authority][spawn]") {
  CHECK(EntitySpawnBuilder::is_valid_game_time(0.001));
  CHECK(EntitySpawnBuilder::is_valid_game_time(1e9));
  CHECK_FALSE(EntitySpawnBuilder::is_valid_game_time(0.0));
  CHECK_FALSE(EntitySpawnBuilder::is_valid_game_time(-0.0));
  CHECK_FALSE(EntitySpawnBuilder::is_valid_game_time(-1.0));
  CHECK_FALSE(EntitySpawnBuilder::is_valid_game_time(std::numeric_limits<double>::quiet_NaN()));
  CHECK_FALSE(EntitySpawnBuilder::is_valid_game_time(std::numeric_limits<double>::infinity()));
  CHECK_FALSE(EntitySpawnBuilder::is_valid_game_time(-std::numeric_limits<double>::infinity()));
}

#ifdef NDEBUG
// Release: a bad time makes the builder invalid and nothing is ever encoded. (Debug builds assert instead, so this is not run there.)
TEST_CASE("spawn builder refuses a bad game_time and emits nothing", "[authority][spawn]") {
  const double bad[] = {0.0, -0.0, -1.0, -1e-300, std::numeric_limits<double>::quiet_NaN(), std::numeric_limits<double>::infinity(),
                        -std::numeric_limits<double>::infinity()};
  for (const double t : bad) {
    EntitySpawnBuilder b(t);
    INFO("game_time " << t);
    CHECK_FALSE(b.valid());
    CHECK(b.game_time() == 0.0);
    CHECK_FALSE(b.add(ship(1)));
    CHECK(b.size() == 0);
    const auto r = b.build();
    REQUIRE_FALSE(r.has_value());
    CHECK(r.error() == SpawnError::InvalidGameTime);
    const auto batches = b.build_batches(8);
    REQUIRE_FALSE(batches.has_value());
    CHECK(batches.error() == SpawnError::InvalidGameTime);
  }
}
#endif

TEST_CASE("no input yields an EntitySpawn with game_time 0", "[authority][spawn]") {
  // Property check over many times (valid and not): either nothing is emitted, or the payload carries exactly the time given, > 0.
  std::mt19937_64 rng(0x5EEDull);
  std::uniform_real_distribution<double> small(-10.0, 10.0);
  std::uniform_real_distribution<double> large(0.0, 1e7);
  std::size_t emitted = 0;
  for (int i = 0; i < 4000; ++i) {
    double t;
    switch (i % 5) {
      case 0: t = small(rng); break;
      case 1: t = large(rng); break;
      case 2: t = std::nextafter(0.0, 1.0) * (1 + (i % 7)); break;   // denormal, smallest positive
      case 3: t = -std::nextafter(0.0, 1.0) * (1 + (i % 7)); break;  // denormal negative
      default: t = (i % 2 == 0) ? 0.0 : std::numeric_limits<double>::quiet_NaN(); break;
    }
    if (!EntitySpawnBuilder::is_valid_game_time(t)) {
#ifdef NDEBUG
      EntitySpawnBuilder b(t);
      (void)b.add(ship(5));
      CHECK_FALSE(b.build().has_value());
      CHECK_FALSE(b.build_batches(4).has_value());
#endif
      continue;
    }
    EntitySpawnBuilder b(t);
    REQUIRE(b.add(ship(5)));
    const auto r = b.build();
    REQUIRE(r.has_value());
    const auto* s = decode(*r);
    CHECK(s->game_time() == t);
    CHECK(s->game_time() > 0.0);
    ++emitted;
  }
  CHECK(emitted > 1000);
}

TEST_CASE("spawn builder rejects reserved net ids", "[authority][spawn]") {
  EntitySpawnBuilder b(10.0);
  CHECK_FALSE(b.add(ship(0)));
  CHECK_FALSE(b.add(ship(0xFFFFFFFFu)));
  CHECK(b.build().error() == SpawnError::NoEntities);
  CHECK(b.add(ship(1)));
  CHECK(b.build().has_value());
}

TEST_CASE("SaveStarted and the manifest refuse a bad game_time too", "[authority][spawn]") {
  const x4mp::session::Id128 cp{1, 2};
  CHECK_FALSE(encode_save_started(1, cp, 0.0, 1).has_value());
  CHECK_FALSE(encode_save_started(1, cp, std::numeric_limits<double>::quiet_NaN(), 1).has_value());
  CHECK_FALSE(encode_manifest(cp, 0.0, 1, {}, {}).has_value());
  const auto ok = encode_save_started(1, cp, 61.5, 9);
  REQUIRE(ok.has_value());
  CHECK(flatbuffers::GetRoot<P::SaveStarted>(ok->data())->game_time() == 61.5);
}

TEST_CASE("only entity_spawn.cpp serialises an EntitySpawn", "[authority][spawn][guard]") {
  // The encoder is the single path: no other source under native/ or tools/headless may call the generated EntitySpawn builders.
  const std::filesystem::path root = X4MP_NATIVE_SRC_DIR;
  REQUIRE(std::filesystem::is_directory(root));
  std::size_t scanned = 0, users = 0;
  for (const auto& dir : {root, root.parent_path() / "tools"}) {
    if (!std::filesystem::is_directory(dir)) continue;
    for (const auto& entry : std::filesystem::recursive_directory_iterator(dir)) {
      if (!entry.is_regular_file()) continue;
      const auto ext = entry.path().extension().string();
      if (ext != ".cpp" && ext != ".h") continue;
      std::ifstream in(entry.path());
      std::stringstream ss;
      ss << in.rdbuf();
      const std::string text = ss.str();
      ++scanned;
      // Decoding a received EntitySpawn (clients) is fine; building one is not.
      const bool uses = text.find("CreateEntitySpawn") != std::string::npos;
      if (uses) {
        ++users;
        INFO(entry.path().string());
        CHECK(entry.path().filename() == "entity_spawn.cpp");
      }
    }
  }
  CHECK(scanned > 10);
  CHECK(users == 1);
}
