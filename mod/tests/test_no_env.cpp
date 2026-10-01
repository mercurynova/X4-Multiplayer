// Guard: nothing under native/core may read environment variables (Steam relaunch drops them; CLAUDE.md hard rule).
// Configuration comes from files only. This scans the core sources for the usual environment accessors.

#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#ifndef X4MP_CORE_SRC_DIR
#error "X4MP_CORE_SRC_DIR must be defined by the build (see mod/CMakeLists.txt)"
#endif

TEST_CASE("core sources never read environment variables", "[config][guard]") {
  const std::filesystem::path root = X4MP_CORE_SRC_DIR;
  REQUIRE(std::filesystem::is_directory(root));
  const std::vector<std::string> forbidden = {"getenv",          "_wgetenv",     "secure_getenv", "_dupenv_s",
                                              "_wdupenv_s",      "GetEnvironmentVariable", "GetEnvironmentStrings",
                                              "putenv",       "setenv",        "std::system"};
  std::size_t files = 0;
  for (const auto& entry : std::filesystem::recursive_directory_iterator(root)) {
    if (!entry.is_regular_file()) continue;
    const auto ext = entry.path().extension().string();
    if (ext != ".cpp" && ext != ".h" && ext != ".hpp") continue;
    ++files;
    std::ifstream in(entry.path());
    std::stringstream ss;
    ss << in.rdbuf();
    const std::string text = ss.str();
    for (const auto& word : forbidden) {
      INFO(entry.path().string() << " mentions " << word);
      CHECK(text.find(word) == std::string::npos);
    }
  }
  CHECK(files >= 8);  // sanity: we actually scanned the tree
}
