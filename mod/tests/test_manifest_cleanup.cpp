// core/session manifest cleanup (close-out A item 7): only the manifests this mod created are removed, only the current one is kept.

#include <filesystem>
#include <fstream>
#include <set>
#include <string>

#include <catch2/catch_test_macros.hpp>

#include "core/session/manifest_cleanup.h"

using namespace x4mp::session;
namespace fs = std::filesystem;

namespace {
struct Dir {
  fs::path path;
  explicit Dir(const char* tag) {
    path = fs::temp_directory_path() / (std::string("x4mp_manifest_test_") + tag);
    std::error_code ec;
    fs::remove_all(path, ec);
    fs::create_directories(path);
  }
  ~Dir() {
    std::error_code ec;
    fs::remove_all(path, ec);
  }
  void touch(const std::string& name) const {
    std::ofstream f(path / name, std::ios::binary);
    f << "x";
  }
  [[nodiscard]] std::set<std::string> names() const {
    std::set<std::string> out;
    for (const auto& e : fs::directory_iterator(path)) out.insert(e.path().filename().string());
    return out;
  }
};
}  // namespace

TEST_CASE("is_manifest_file_name matches exactly what the mod creates", "[manifest]") {
  CHECK(is_manifest_file_name("x4mp_4d80a1d650fa.x4mf"));
  CHECK(is_manifest_file_name("x4mp_000000000000.x4mf"));
  CHECK_FALSE(is_manifest_file_name("x4mp_4d80a1d650f.x4mf"));     // 11 digits
  CHECK_FALSE(is_manifest_file_name("x4mp_4d80a1d650faa.x4mf"));   // 13 digits
  CHECK_FALSE(is_manifest_file_name("x4mp_4D80A1D650FA.x4mf"));    // upper case: not ours
  CHECK_FALSE(is_manifest_file_name("x4mp_4d80a1d650fg.x4mf"));    // not hex
  CHECK_FALSE(is_manifest_file_name("x4mp_4d80a1d650fa.xml.gz"));  // the save itself
  CHECK_FALSE(is_manifest_file_name("x4mp_4d80a1d650fa.x4mf.part"));
  CHECK_FALSE(is_manifest_file_name("save_001.xml.gz"));
  CHECK_FALSE(is_manifest_file_name("../x4mp_4d80a1d650fa.x4mf"));
  CHECK_FALSE(is_manifest_file_name(""));
}

TEST_CASE("prune_old_manifests keeps the current manifest and touches nothing else", "[manifest]") {
  Dir d("keep");
  const std::string current = "x4mp_aaaaaaaaaaaa.x4mf";
  d.touch(current);
  d.touch("x4mp_111111111111.x4mf");
  d.touch("x4mp_222222222222.x4mf");
  d.touch("x4mp_4d80a1d650fa.x4mf");
  // never touched: the player's saves, our own downloaded saves, a transfer in progress, lookalikes, other mods' files
  d.touch("save_001.xml.gz");
  d.touch("quicksave.xml.gz");
  d.touch("x4mp_111111111111.xml.gz");
  d.touch("x4mp_333333333333.x4mf.part");
  d.touch("x4mp_ckpt_0123456789abcdef.xml.gz");
  d.touch("x4mp_ABCDEF123456.x4mf");
  d.touch("x4mp_short.x4mf");
  d.touch("notes.x4mf");
  d.touch("other_mod_444444444444.x4mf");
  fs::create_directories(d.path / "x4mp_555555555555.x4mf");  // a directory with a manifest's name

  const auto removed = prune_old_manifests(d.path, current);
  CHECK(removed == 3);
  const auto left = d.names();
  CHECK(left.contains(current));
  CHECK_FALSE(left.contains("x4mp_111111111111.x4mf"));
  CHECK_FALSE(left.contains("x4mp_222222222222.x4mf"));
  CHECK_FALSE(left.contains("x4mp_4d80a1d650fa.x4mf"));
  for (const char* keep : {"save_001.xml.gz", "quicksave.xml.gz", "x4mp_111111111111.xml.gz", "x4mp_333333333333.x4mf.part",
                           "x4mp_ckpt_0123456789abcdef.xml.gz", "x4mp_ABCDEF123456.x4mf", "x4mp_short.x4mf", "notes.x4mf",
                           "other_mod_444444444444.x4mf", "x4mp_555555555555.x4mf"}) {
    CHECK(left.contains(keep));
  }
  CHECK(fs::is_directory(d.path / "x4mp_555555555555.x4mf"));

  CHECK(prune_old_manifests(d.path, current) == 0);  // idempotent
}

TEST_CASE("prune_old_manifests with no current manifest removes ours only, and a bad folder is harmless", "[manifest]") {
  Dir d("none");
  d.touch("x4mp_aaaaaaaaaaaa.x4mf");
  d.touch("save_001.xml.gz");
  CHECK(prune_old_manifests(d.path, "") == 1);
  CHECK(d.names() == std::set<std::string>{"save_001.xml.gz"});

  CHECK(prune_old_manifests(d.path / "does_not_exist", "") == 0);
  CHECK(prune_old_manifests(fs::path{}, "") == 0);
}
