#include <catch2/catch_test_macros.hpp>

#include <functional>
#include <nlohmann/json.hpp>
#include <string>
#include <vector>

#include "control_generated.h"
#include "features/join/join_mods_json.h"
#include "mods_generated.h"
#include "teams_generated.h"

using namespace x4mp::features::join;
namespace P = X4MP::Proto;

namespace {
using Buf = std::vector<std::uint8_t>;

Buf take(const flatbuffers::FlatBufferBuilder& fbb) { return Buf(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()); }

flatbuffers::Offset<P::ModRef> ref(flatbuffers::FlatBufferBuilder& fbb, const std::string& id, const std::string& name, const std::string& version,
                                   const std::string& have, const std::string& nexus, std::uint64_t ws) {
  return P::CreateModRefDirect(fbb, id.c_str(), name.c_str(), version.c_str(), have.c_str(), nexus.c_str(), ws, "");
}

Buf disconnect_with(const std::function<void(flatbuffers::FlatBufferBuilder&, std::vector<flatbuffers::Offset<P::ModRef>>&,
                                             std::vector<flatbuffers::Offset<P::ModRef>>&, std::vector<flatbuffers::Offset<P::ModRef>>&,
                                             std::vector<flatbuffers::Offset<P::ModRef>>&)>& fill) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  std::vector<flatbuffers::Offset<P::ModRef>> install, enable, disable, update;
  fill(fbb, install, enable, disable, update);
  const auto v = P::CreateModPolicyViolationDirect(fbb, 7, &install, &enable, &disable, &update);
  fbb.Finish(P::CreateDisconnectDirect(fbb, P::DisconnectCode::ExtensionsMismatch, "your mods do not match", nullptr, 0, v));
  return take(fbb);
}
}  // namespace

TEST_CASE("mod refusal json carries the four groups", "[join][mods]") {
  const Buf b = disconnect_with([](auto& fbb, auto& install, auto& enable, auto& disable, auto& update) {
    install.push_back(ref(fbb, "ws_2458720435", "Warehouse Fleets", "1.4", "", "", 2458720435ULL));
    install.push_back(ref(fbb, "sn_better_traders", "Better Traders", "2.0", "", "https://www.nexusmods.com/x4foundations/mods/12", 0));
    enable.push_back(ref(fbb, "a_off", "Off \"Mod\"", "1.0", "1.0", "", 0));
    disable.push_back(ref(fbb, "ws_9000000001", "Extra", "", "1.0", "", 9000000001ULL));
    update.push_back(ref(fbb, "ws_5", "Old", "1.4", "1.3", "", 5));
  });
  const auto j = nlohmann::json::parse(mod_refusal_json(b));
  CHECK(j["v"] == 1);
  CHECK(j["policy_version"] == 7);
  REQUIRE(j["install"].size() == 2);
  CHECK(j["install"][0]["workshop_id"] == 2458720435ULL);
  CHECK(j["install"][1]["nexus_url"] == "https://www.nexusmods.com/x4foundations/mods/12");
  CHECK_FALSE(j["install"][1].contains("workshop_id"));
  CHECK(j["enable"][0]["name"] == "Off \"Mod\"");
  CHECK(j["disable"][0]["have_version"] == "1.0");
  CHECK(j["update"][0]["version"] == "1.4");
  CHECK(j["update"][0]["have_version"] == "1.3");
}

TEST_CASE("mod refusal json caps lists and long strings", "[join][mods]") {
  const std::string longName(300, 'x');
  const Buf b = disconnect_with([&](auto& fbb, auto& install, auto&, auto&, auto&) {
    for (int i = 0; i < 130; ++i) install.push_back(ref(fbb, "m" + std::to_string(i), longName, "", "", "", 0));
  });
  const auto j = nlohmann::json::parse(mod_refusal_json(b));
  CHECK(j["install"].size() == 100);
  CHECK(j["install_more"] == 30);
  CHECK(j["install"][0]["name"].get<std::string>().size() == 256);
}

TEST_CASE("mod refusal json is empty without a violation or on junk", "[join][mods]") {
  flatbuffers::FlatBufferBuilder fbb(64);
  fbb.Finish(P::CreateDisconnectDirect(fbb, P::DisconnectCode::ServerShutdown, "bye"));
  CHECK(mod_refusal_json(take(fbb)).empty());
  CHECK(mod_refusal_json({}).empty());
  const Buf junk{1, 2, 3, 4, 5};
  CHECK(mod_refusal_json(junk).empty());
}

TEST_CASE("mod policy json from a ModPolicyChanged frame", "[join][mods]") {
  flatbuffers::FlatBufferBuilder fbb(512);
  std::vector<flatbuffers::Offset<P::ModPolicyEntry>> entries;
  entries.push_back(P::CreateModPolicyEntryDirect(fbb, "ws_1", "One", P::ModRule::Required, true, P::ExtensionClass::Sim, P::VersionRule::AtLeast,
                                                  "1.2", nullptr, "", 1, "note"));
  entries.push_back(P::CreateModPolicyEntryDirect(fbb, "bad", "Bad", P::ModRule::Blocked, true));
  const auto policy = P::CreateModPolicyDirect(fbb, 3, P::ModSourceMode::AdminList, P::UnknownModDefault::Block, P::ModEnforcement::Warn, &entries);
  fbb.Finish(P::CreateModPolicyChanged(fbb, policy));
  const auto j = nlohmann::json::parse(mod_policy_json_from_changed(take(fbb)));
  CHECK(j["version"] == 3);
  CHECK(j["source_mode"] == "admin");
  CHECK(j["unknown_default"] == "block");
  CHECK(j["enforcement"] == "warn");
  REQUIRE(j["entries"].size() == 2);
  CHECK(j["entries"][0]["rule"] == "required");
  CHECK(j["entries"][0]["version_rule"] == "at_least");
  CHECK(j["entries"][0]["version"] == "1.2");
  CHECK(j["entries"][0]["workshop_id"] == 1);
  CHECK(j["entries"][1]["rule"] == "blocked");
}
