#include <catch2/catch_test_macros.hpp>

#include "features/join/join_json.h"

using namespace x4mp::features::join;

TEST_CASE("parse_endpoint handles host, host:port, IPv6 and rejects junk", "[join]") {
  std::string host;
  std::uint16_t port = 0;
  CHECK(parse_endpoint("play.example.com", host, port));
  CHECK(host == "play.example.com");
  CHECK(port == 47780);
  CHECK(parse_endpoint("192.168.1.20:5000", host, port));
  CHECK(host == "192.168.1.20");
  CHECK(port == 5000);
  CHECK(parse_endpoint("[::1]:47780", host, port));
  CHECK(host == "::1");
  CHECK(port == 47780);
  CHECK(parse_endpoint("[fe80::1]", host, port));
  CHECK(host == "fe80::1");
  CHECK(port == 47780);

  CHECK_FALSE(parse_endpoint("", host, port));
  CHECK_FALSE(parse_endpoint("host:0", host, port));
  CHECK_FALSE(parse_endpoint("host:70000", host, port));
  CHECK_FALSE(parse_endpoint("host:abc", host, port));
  CHECK_FALSE(parse_endpoint("[::1", host, port));
  CHECK_FALSE(parse_endpoint("[host]:1", host, port));
  CHECK_FALSE(parse_endpoint("bad host", host, port));
  CHECK_FALSE(parse_endpoint(":47780", host, port));
}

TEST_CASE("parse_join reads the contract fields and refuses bad input", "[join]") {
  std::string error;
  auto r = parse_join(R"({"v":1,"address":"play.example.com:5000","name":"Alice","password":"pw-secret","team":"auto"})", &error);
  REQUIRE(r);
  CHECK(r->host == "play.example.com");
  CHECK(r->port == 5000);
  CHECK(r->name == "Alice");
  CHECK(r->password == "pw-secret");
  CHECK(r->team == 0);  // "auto"
  CHECK_FALSE(r->want_authority);

  r = parse_join(R"({"address":"h","name":"Bob","team":3,"role":"authority","admin_password":"adm"})");
  REQUIRE(r);
  CHECK(r->team == 3);
  CHECK(r->want_authority);
  CHECK(r->admin_password == "adm");
  CHECK(r->password.empty());

  CHECK_FALSE(parse_join("not json", &error));
  CHECK(error == "bad_json");
  CHECK_FALSE(parse_join(R"({"address":"bad host","name":"x"})", &error));
  CHECK(error == "bad_address");
  CHECK_FALSE(parse_join(R"({"address":"h","name":""})", &error));
  CHECK(error == "bad_name");
  CHECK_FALSE(parse_join(R"({"address":"h"})", &error));
}

TEST_CASE("parse_extensions maps the Lua list and skips entries without an id", "[join]") {
  const auto list = parse_extensions(R"({"v":1,"source":"load","count":3,"list":[
    {"id":"ego_dlc_boron","name":"Kingdom End","version":"9.00","enabled":true,"egosoftextension":true},
    {"name":"no id"},
    {"id":"ws_123","version":"1.2","enabled":false,"error":"missing dependency"}]})");
  REQUIRE(list);
  REQUIRE(list->size() == 2);
  CHECK((*list)[0].id == "ego_dlc_boron");
  CHECK((*list)[0].enabled);
  CHECK((*list)[0].egosoft);
  CHECK((*list)[1].id == "ws_123");
  CHECK_FALSE((*list)[1].enabled);
  CHECK((*list)[1].error == "missing dependency");

  CHECK_FALSE(parse_extensions("nope"));
  CHECK_FALSE(parse_extensions(R"({"v":1})"));
}

TEST_CASE("rejection codes map to the reject tokens the UI shows", "[join]") {
  CHECK(reject_for_code(12) == "build");   // GameVersionMismatch
  CHECK(reject_for_code(11) == "mod");     // ModVersionMismatch
  CHECK(reject_for_code(13) == "mod");     // ExtensionsMismatch
  CHECK(reject_for_code(10) == "mod");     // ProtocolMismatch
  CHECK(reject_for_code(14) == "auth");    // AuthFailed
  CHECK(reject_for_code(15) == "full");    // SessionFull
  CHECK(reject_for_code(4) == "banned");   // Banned
  CHECK(reject_for_code(16) == "name");    // NameTaken
  CHECK(reject_for_code(3) == "other");    // Kicked
  CHECK(reject_for_code(17) == "other");   // RoleUnavailable
  // not rejections: the net layer redials on its own
  CHECK_FALSE(reject_for_code(1));   // ClientQuit
  CHECK_FALSE(reject_for_code(2));   // ServerShutdown
  CHECK_FALSE(reject_for_code(6));   // ClientReload
  CHECK_FALSE(reject_for_code(19));  // ResumeExpired
  CHECK_FALSE(reject_for_code(30));  // HeartbeatTimeout
}
