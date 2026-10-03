// host/extension_roots (M2-03): the Windows roots handed to core/mods. Pure path arithmetic.

#include <catch2/catch_test_macros.hpp>

#include "host/extension_roots.h"

namespace fs = std::filesystem;

TEST_CASE("host: extension roots are install, Documents\\Egosoft\\X4 and the sibling workshop folder", "[host][mods]") {
  const fs::path x4 = fs::path("D:/lib/steamapps/common/X4 Foundations");
  const auto r = x4mp::host::make_extension_roots(x4, fs::path("C:/docs"));
  CHECK(r.install_extensions == x4 / "extensions");
  REQUIRE(r.user_game_dirs.size() == 1);
  CHECK(r.user_game_dirs[0] == fs::path("C:/docs") / "Egosoft" / "X4");
  REQUIRE(r.workshop_roots.size() == 1);
  CHECK(r.workshop_roots[0] == fs::path("D:/lib/steamapps") / "workshop" / "content" / "392160");
}

TEST_CASE("host: no X4 directory gives no install or workshop root", "[host][mods]") {
  const auto r = x4mp::host::make_extension_roots({}, fs::path("C:/docs"));
  CHECK(r.install_extensions.empty());
  CHECK(r.workshop_roots.empty());
  CHECK(r.user_game_dirs.size() == 1);
}
