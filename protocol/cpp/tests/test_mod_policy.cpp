// Mod-policy shared constants (M1-X1): the generated C++ header must equal protocol/constants/mod_policy.json,
// the same source the C# ModPolicyConstants.g.cs is generated from (checked by X4MP.Protocol.Tests).

#include <fstream>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include "x4mp/mod_policy_constants.h"

namespace {

using nlohmann::json;
namespace mp = x4mp::mod_policy;

const json& source() {
  static const json j = [] {
    std::ifstream in(std::string(X4MP_CONSTANTS_DIR) + "/mod_policy.json");
    REQUIRE(in.good());
    return json::parse(in);
  }();
  return j;
}

template <typename Array>
std::vector<std::string> to_strings(const Array& a) {
  std::vector<std::string> out;
  for (const auto& s : a) out.emplace_back(s);
  return out;
}

}  // namespace

TEST_CASE("mod policy constants equal the JSON source", "[mod_policy]") {
  const json& j = source();
  CHECK(to_strings(mp::kClientOnlyLibraryIds) == j.at("clientOnlyLibraryIds").get<std::vector<std::string>>());
  CHECK(to_strings(mp::kHashExcludedIds) == j.at("hashExcludedIds").get<std::vector<std::string>>());
  CHECK(std::string(mp::kDlcIdPrefix) == j.at("dlcIdPrefix").get<std::string>());
  CHECK(std::string(mp::kWorkshopIdPrefix) == j.at("workshopIdPrefix").get<std::string>());
  CHECK(std::string(mp::kNexusUrlRegex) == j.at("nexusUrlRegex").get<std::string>());
  CHECK(std::string(mp::kNexusUrlFormat) == j.at("nexusUrlFormat").get<std::string>());
  CHECK(std::string(mp::kWorkshopUrlFormat) == j.at("workshopUrlFormat").get<std::string>());
  CHECK(std::string(mp::kWorkshopSteamUrlFormat) == j.at("workshopSteamUrlFormat").get<std::string>());
  CHECK(std::string(mp::kHashLineFormat) == j.at("hashLineFormat").get<std::string>());
  CHECK(mp::kMaxExtensionEntries == j.at("maxExtensionEntries").get<int>());
}
