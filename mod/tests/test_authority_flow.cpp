#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <filesystem>
#include <fstream>

#include "core/crypto/crypto.h"
#include "features/authority/authority_data.h"
#include "features/join/join_json.h"

using namespace x4mp::features::auth;
namespace fs = std::filesystem;
using namespace std::chrono_literals;

namespace {
struct TempDir {
  fs::path path;
  TempDir() {
    path = fs::temp_directory_path() / ("x4mp-auth-test-" + std::to_string(reinterpret_cast<std::uintptr_t>(this)));
    fs::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    fs::remove_all(path, ec);
  }
};
void write_file(const fs::path& p, const std::string& bytes) {
  std::ofstream out(p, std::ios::binary | std::ios::trunc);
  out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
}
const std::string kGz = std::string("\x1f\x8b\x08\x00 rest of a gzip", 18);
}  // namespace

TEST_CASE("MdCollector collects sectors, the end marker and the ship", "[authority]") {
  MdCollector c;
  CHECK(c.add("G;cluster_01_sector001_macro|cluster_01_macro|Argon Prime|argon|1.5|0|-2|cluster_02_sector001_macro;"
              "cluster_02_sector001_macro|cluster_02_macro|Gate Two||0|0|0|cluster_01_sector001_macro,cluster_09_sector001_macro"));
  CHECK_FALSE(c.complete());
  CHECK(c.add("P;ship_arg_s_fighter_01_a_macro|Hawk|ABC-123|cluster_01_sector001_macro|ship_s|player"));
  CHECK(c.add("E;2"));
  CHECK(c.complete());
  REQUIRE(c.sectors().size() == 2);
  CHECK(c.sectors()[0].name == "Argon Prime");
  CHECK(c.sectors()[0].x == 1.5f);
  CHECK(c.sectors()[0].z == -2.0f);
  CHECK(c.sectors()[1].owner.empty());
  CHECK(c.sectors()[1].gates.size() == 2);
  REQUIRE(c.ship());
  CHECK(c.ship()->idcode == "ABC-123");
}

TEST_CASE("MdCollector drops malformed records and refuses junk", "[authority]") {
  MdCollector c;
  CHECK_FALSE(c.add(""));
  CHECK_FALSE(c.add("X;1"));
  CHECK_FALSE(c.add("E;abc"));
  CHECK(c.add("G;too|few|fields;ok_macro|c|n|o|0|0|0|"));
  CHECK(c.sectors().size() == 1);
  CHECK(c.dropped_records() == 1);
  CHECK(c.add("E;5"));
  CHECK_FALSE(c.complete());  // the count does not match
}

TEST_CASE("build_plan indexes sectors, dedupes links and interns strings", "[authority]") {
  MdCollector c;
  c.add("G;a_macro|ca|A|argon|0|0|0|b_macro;b_macro|cb|B|argon|1|1|1|a_macro,c_macro;c_macro|cc|C|teladi|2|2|2|");
  c.add("E;3");
  c.add("P;ship_macro|Ship|ID-1|b_macro|ship_m|player");
  const auto plan = build_plan(c);
  REQUIRE(plan.sectors.size() == 3);
  CHECK(plan.sectors[0].index == 1);
  CHECK(plan.sectors[2].index == 3);
  CHECK(plan.links.size() == 2);  // a-b (seen twice) and b-c
  CHECK(plan.ship_sector == 2);
  CHECK(plan.player_faction_ref != 0);
  CHECK(plan.ship_macro_ref != 0);
  CHECK(plan.sectors[0].owner_ref == plan.sectors[1].owner_ref);
  CHECK(plan.sectors[0].owner_ref != plan.sectors[2].owner_ref);
  // string indices are unique and 1-based
  for (std::size_t i = 0; i < plan.strings.size(); ++i) CHECK(plan.strings[i].index == i + 1);
  CHECK(build_plan(MdCollector{}).sectors.empty());
}

TEST_CASE("SaveFileWatcher waits until the file stops changing and can be opened", "[authority]") {
  TempDir dir;
  const auto file = dir.path / "x4mp_ckpt_0000000000000001.xml.gz";
  SaveFileWatcher w(file, 1000ms);
  const auto t0 = SaveFileWatcher::Clock::now();
  CHECK_FALSE(w.poll(t0));  // no file yet (the MD event fires before the game even created it)

  write_file(file, kGz);
  CHECK_FALSE(w.poll(t0 + 100ms));   // first sight
  CHECK_FALSE(w.poll(t0 + 600ms));   // stable, but not for long enough
  const std::string grown = kGz + "more data written by the game";
  write_file(file, grown);
  CHECK_FALSE(w.poll(t0 + 1200ms));  // changed again: the clock restarts
  CHECK_FALSE(w.poll(t0 + 2000ms));  // 800 ms stable
  CHECK(w.poll(t0 + 2300ms));        // 1100 ms stable
  CHECK(w.complete());
  CHECK(w.size() == grown.size());
}

TEST_CASE("SaveFileWatcher refuses a file that is not a gzip", "[authority]") {
  TempDir dir;
  const auto file = dir.path / "x4mp_ckpt_0000000000000002.xml.gz";
  write_file(file, "<savegame/>");
  SaveFileWatcher w(file, 0ms);
  const auto t0 = SaveFileWatcher::Clock::now();
  CHECK_FALSE(w.poll(t0));
  CHECK_FALSE(w.poll(t0 + 10ms));
  CHECK_FALSE(w.complete());
}

TEST_CASE("checkpoint save names", "[authority]") {
  CHECK(checkpoint_save_name(0x0123456789abcdefull) == "x4mp_ckpt_0123456789abcdef");
  CHECK(is_checkpoint_save_name("x4mp_ckpt_0123456789abcdef"));
  CHECK_FALSE(is_checkpoint_save_name("x4mp_ckpt_0123456789ABCDEF"));
  CHECK_FALSE(is_checkpoint_save_name("x4mp_ckpt_0123"));
  CHECK_FALSE(is_checkpoint_save_name("save_001"));
  CHECK_FALSE(is_checkpoint_save_name("x4mp_0123456789ab"));
  CHECK_FALSE(is_checkpoint_save_name("../x4mp_ckpt_0123456789abcdef"));
}

TEST_CASE("record_and_trim keeps the newest two checkpoint saves and never touches other files", "[authority]") {
  TempDir dir;
  const auto saves = dir.path / "saves";
  fs::create_directories(saves);
  const auto ledger = dir.path / "authority-saves.json";
  write_file(saves / "save_001.xml.gz", kGz);
  write_file(saves / "quicksave.xml.gz", kGz);
  write_file(saves / "x4mp_ckpt_ffffffffffffffff.xml.gz", kGz);  // looks like ours but was never recorded: untouched

  std::vector<std::string> names;
  for (std::uint64_t i = 1; i <= 4; ++i) {
    names.push_back(checkpoint_save_name(i));
    write_file(saves / (names.back() + ".xml.gz"), kGz);
    const auto removed = record_and_trim(ledger, saves, names.back(), 2);
    if (i <= 2) CHECK(removed.empty());
    if (i == 3) CHECK(removed == std::vector<std::string>{names[0]});
    if (i == 4) CHECK(removed == std::vector<std::string>{names[1]});
  }
  CHECK_FALSE(fs::exists(saves / (names[0] + ".xml.gz")));
  CHECK_FALSE(fs::exists(saves / (names[1] + ".xml.gz")));
  CHECK(fs::exists(saves / (names[2] + ".xml.gz")));
  CHECK(fs::exists(saves / (names[3] + ".xml.gz")));
  CHECK(fs::exists(saves / "save_001.xml.gz"));
  CHECK(fs::exists(saves / "quicksave.xml.gz"));
  CHECK(fs::exists(saves / "x4mp_ckpt_ffffffffffffffff.xml.gz"));
  CHECK(load_ledger(ledger) == std::vector<std::string>{names[2], names[3]});
}

TEST_CASE("a tampered ledger cannot make the janitor delete a foreign file", "[authority]") {
  TempDir dir;
  const auto saves = dir.path / "saves";
  fs::create_directories(saves);
  write_file(saves / "save_001.xml.gz", kGz);
  const auto ledger = dir.path / "authority-saves.json";
  write_file(ledger, R"({"v":1,"saves":["save_001","../save_001","x4mp_ckpt_0000000000000001","x4mp_ckpt_0000000000000002","x4mp_ckpt_0000000000000003"]})");
  CHECK(load_ledger(ledger).size() == 3);  // only valid names survive loading
  const auto removed = record_and_trim(ledger, saves, checkpoint_save_name(4), 2);
  CHECK(removed.size() == 2);
  CHECK(fs::exists(saves / "save_001.xml.gz"));
}

TEST_CASE("AuthorityState round-trips and tolerates garbage", "[authority]") {
  AuthorityState s;
  s.next_net_id = 7;
  s.spawned = true;
  s.strings_sent = true;
  s.checkpoints = 3;
  s.loaded_sha.assign(32, 0xAB);
  const auto back = AuthorityState::from_json(s.to_json());
  CHECK(back.next_net_id == 7);
  CHECK(back.spawned);
  CHECK(back.strings_sent);
  CHECK(back.checkpoints == 3);
  CHECK(back.loaded_sha == s.loaded_sha);

  const auto bad = AuthorityState::from_json("not json");
  CHECK(bad.next_net_id == 1);
  CHECK_FALSE(bad.spawned);
  CHECK(bad.loaded_sha.empty());
  CHECK(AuthorityState::from_json(R"({"next_net_id":0,"loaded_sha":"abcd"})").next_net_id == 1);
  CHECK(AuthorityState::from_json(R"({"loaded_sha":"abcd"})").loaded_sha.empty());  // wrong length
}

TEST_CASE("the join payload carries the authority role, the admin password and the loaded-save seam", "[authority]") {
  const std::string sha(64, 'a');
  const auto r = x4mp::features::join::parse_join(
      R"({"v":1,"address":"127.0.0.1:47780","name":"Host","password":"","team":"auto","role":"authority","admin_password":"pw","loaded_save_sha256":")" + sha + R"("})");
  REQUIRE(r);
  CHECK(r->want_authority);
  CHECK(r->admin_password == "pw");
  CHECK(r->loaded_save_sha256.size() == 32);

  const auto plain = x4mp::features::join::parse_join(R"({"v":1,"address":"127.0.0.1","name":"P","loaded_save_sha256":"zz"})");
  REQUIRE(plain);
  CHECK_FALSE(plain->want_authority);
  CHECK(plain->loaded_save_sha256.empty());
}
