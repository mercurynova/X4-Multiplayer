// host/paths + host/player_key (M3-24): per-machine config dir, one-time x4mp.json copy, machine-tagged player.key.

#include <catch2/catch_test_macros.hpp>

#include <array>
#include <fstream>
#include <sstream>

#include "host/paths.h"
#include "host/player_key.h"
#include "host_fakes.h"

namespace fs = std::filesystem;
using namespace x4mp::host;

namespace {
std::string slurp(const fs::path& p) {
  std::ifstream in(p, std::ios::binary);
  std::ostringstream ss;
  ss << in.rdbuf();
  return ss.str();
}
void spit(const fs::path& p, const std::string& text) {
  fs::create_directories(p.parent_path());
  std::ofstream(p, std::ios::binary) << text;
}
const std::string kHex(64, 'a');
}  // namespace

TEST_CASE("paths: normal mode is %LocalAppData%\\X4MP, with the old Documents dir kept as legacy", "[host][paths]") {
  x4mp::test::TempDir ext;
  fs::remove(ext.path / "x4mp.portable");
  const auto p = resolve_config_paths(ext.path, fs::path("C:/docs"), fs::path("C:/local"));
  CHECK_FALSE(p.portable);
  CHECK(p.dir == fs::path("C:/local") / "X4MP");
  CHECK(p.legacy_dir == fs::path("C:/docs") / "Egosoft" / "X4" / "x4mp");
  CHECK(p.user_file == p.dir / "x4mp.json");
  CHECK(p.launch_file == p.dir / "launch.json");
  CHECK(p.default_log_file == p.dir / "logs" / "x4mp.log");
}

TEST_CASE("paths: the portable marker puts everything in the extension folder", "[host][paths]") {
  x4mp::test::TempDir ext;  // has x4mp.portable
  const auto p = resolve_config_paths(ext.path, fs::path("C:/docs"), fs::path("C:/local"));
  CHECK(p.portable);
  CHECK(p.dir == ext.path);
  CHECK(p.legacy_dir.empty());
}

TEST_CASE("paths: x4mp.json is copied once from Documents, the old file stays, player.key is never copied", "[host][paths]") {
  x4mp::test::TempDir ext;
  x4mp::test::TempDir docs;
  x4mp::test::TempDir local;
  fs::remove(ext.path / "x4mp.portable");
  const auto p = resolve_config_paths(ext.path, docs.path, local.path);
  const auto old_dir = docs.path / "Egosoft" / "X4" / "x4mp";
  spit(old_dir / "x4mp.json", "{\"last_name\":\"Alice\"}");
  spit(old_dir / "player.key", kHex + "\n");

  CHECK(migrate_user_config(p));
  CHECK(slurp(p.user_file) == "{\"last_name\":\"Alice\"}");
  CHECK(fs::exists(old_dir / "x4mp.json"));
  CHECK_FALSE(fs::exists(p.dir / "player.key"));

  spit(p.user_file, "{\"last_name\":\"Bob\"}");  // later edits are never overwritten
  CHECK_FALSE(migrate_user_config(p));
  CHECK(slurp(p.user_file) == "{\"last_name\":\"Bob\"}");
}

TEST_CASE("paths: no migration in portable mode or without an old file", "[host][paths]") {
  x4mp::test::TempDir ext;
  CHECK_FALSE(migrate_user_config(resolve_config_paths(ext.path, fs::path("C:/docs"), fs::path("C:/local"))));
  x4mp::test::TempDir local;
  fs::remove(ext.path / "x4mp.portable");
  CHECK_FALSE(migrate_user_config(resolve_config_paths(ext.path, fs::path("C:/no-such-docs"), local.path)));
}

TEST_CASE("player key: machine tag is a stable hash and never the raw id", "[host][paths][key]") {
  const auto t = machine_tag("some-machine-guid");
  CHECK(t.size() == 64);
  CHECK(t == machine_tag("some-machine-guid"));
  CHECK(t != machine_tag("another-guid"));
  CHECK(t.find("some-machine-guid") == std::string::npos);
  CHECK(machine_tag("").empty());
}

TEST_CASE("player key: a new key is written with this machine's tag and kept on the next start", "[host][paths][key]") {
  x4mp::test::TempDir dir;
  const auto file = dir.path / "player.key";
  const auto tag = machine_tag("machine-A");
  std::array<std::uint8_t, 32> k1{};
  std::array<std::uint8_t, 32> k2{};
  const auto r1 = load_or_create_player_key(file, tag, k1);
  REQUIRE(r1.ok);
  CHECK(r1.origin == KeyOrigin::CreatedNew);
  CHECK(slurp(file).find("machine=" + tag) != std::string::npos);
  const auto r2 = load_or_create_player_key(file, tag, k2);
  REQUIRE(r2.ok);
  CHECK(r2.origin == KeyOrigin::Kept);
  CHECK(k1 == k2);
}

TEST_CASE("player key: an untagged old key is not adopted, a fresh tagged key replaces it", "[host][paths][key]") {
  x4mp::test::TempDir dir;
  const auto file = dir.path / "player.key";
  spit(file, kHex + "\n");
  std::array<std::uint8_t, 32> k{};
  const auto r = load_or_create_player_key(file, machine_tag("machine-A"), k);
  REQUIRE(r.ok);
  CHECK(r.origin == KeyOrigin::ReplacedUntagged);
  CHECK(slurp(file).substr(0, 64) != kHex);
  std::array<std::uint8_t, 32> again{};
  CHECK(load_or_create_player_key(file, machine_tag("machine-A"), again).origin == KeyOrigin::Kept);
  CHECK(again == k);
}

TEST_CASE("player key: a key tagged with another machine is not adopted (two PCs never share one)", "[host][paths][key]") {
  x4mp::test::TempDir dir;
  const auto file = dir.path / "player.key";
  std::array<std::uint8_t, 32> a{};
  REQUIRE(load_or_create_player_key(file, machine_tag("machine-A"), a).ok);
  std::array<std::uint8_t, 32> b{};
  const auto r = load_or_create_player_key(file, machine_tag("machine-B"), b);
  REQUIRE(r.ok);
  CHECK(r.origin == KeyOrigin::ReplacedForeign);
  CHECK(a != b);
}

TEST_CASE("player key: without a machine id any valid key is kept and new files are untagged", "[host][paths][key]") {
  x4mp::test::TempDir dir;
  const auto file = dir.path / "player.key";
  spit(file, kHex + "\n");
  std::array<std::uint8_t, 32> k{};
  const auto r = load_or_create_player_key(file, "", k);
  CHECK(r.origin == KeyOrigin::Kept);
  CHECK(k[0] == 0xAA);
  fs::remove(file);
  REQUIRE(load_or_create_player_key(file, "", k).ok);
  CHECK(slurp(file).find("machine=") == std::string::npos);
}

TEST_CASE("player key: describe never contains key material", "[host][paths][key]") {
  CHECK(describe_key_origin(KeyOrigin::Kept).empty());
  CHECK_FALSE(describe_key_origin(KeyOrigin::ReplacedUntagged).empty());
}
