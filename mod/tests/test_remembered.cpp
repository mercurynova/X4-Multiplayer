// M3-07: the remembered Join fields in x4mp.json (core/config/remembered.h): atomic writer, preserved keys, never a secret.
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>

#include <catch2/catch_test_macros.hpp>

#include "core/config/config.h"
#include "core/config/remembered.h"

using namespace x4mp::config;
namespace fs = std::filesystem;

namespace {
struct TempDir {
  fs::path path;
  TempDir() {
    static std::atomic<int> n{0};
    path = fs::temp_directory_path() /
           ("x4mp_rem_test_" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + "_" + std::to_string(n++));
    fs::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    fs::remove_all(path, ec);
  }
  [[nodiscard]] fs::path write(const std::string& name, const std::string& text) const {
    const auto p = path / name;
    std::ofstream(p, std::ios::binary) << text;
    return p;
  }
};

std::string slurp(const fs::path& p) {
  std::ifstream in(p, std::ios::binary);
  std::ostringstream ss;
  ss << in.rdbuf();
  return ss.str();
}

bool has(const std::string& text, const std::string& needle) { return text.find(needle) != std::string::npos; }
}  // namespace

TEST_CASE("remembered: writes a new file with the two keys and no BOM", "[remembered]") {
  TempDir d;
  const auto file = d.path / "sub" / "x4mp.json";  // the folder is created too
  RememberRequest req;
  req.address = "play.example.com:47780";
  req.name = "Alice";
  Remembered after;
  const auto r = apply_remember(file, req, &after);
  REQUIRE(r.ok);
  CHECK(after.address == "play.example.com:47780");
  const std::string text = slurp(file);
  REQUIRE(text.size() > 3);
  CHECK(static_cast<unsigned char>(text[0]) == '{');  // no BOM
  CHECK(has(text, "\"last_address\": \"play.example.com:47780\""));
  CHECK(has(text, "\"last_name\": \"Alice\""));
  const auto back = read_remembered(file);
  CHECK(back.address == "play.example.com:47780");
  CHECK(back.name == "Alice");
  CHECK_FALSE(fs::exists(fs::path(file.string() + ".tmp")));
}

TEST_CASE("remembered: unknown and user-set keys are preserved, in order", "[remembered]") {
  TempDir d;
  const auto file = d.write("x4mp.json",
                            "\xEF\xBB\xBF{\"log_level\":\"debug\",\"my_future_key\":{\"a\":[1,2,3]},\"selftest\":true,"
                            "\"last_name\":\"Old\",\"tcp_port\":47780}");
  RememberRequest req;
  req.address = "10.0.0.5:47780";
  req.name = "New";
  REQUIRE(apply_remember(file, req).ok);
  const std::string text = slurp(file);
  CHECK(static_cast<unsigned char>(text[0]) == '{');  // the BOM of the old file is gone, none is written
  CHECK(has(text, "\"log_level\": \"debug\""));
  CHECK(has(text, "\"my_future_key\""));
  CHECK(has(text, "\"selftest\": true"));
  CHECK(has(text, "\"tcp_port\": 47780"));
  CHECK(has(text, "\"last_name\": \"New\""));
  CHECK_FALSE(has(text, "Old"));
  CHECK(text.find("log_level") < text.find("my_future_key"));
  CHECK(text.find("my_future_key") < text.find("selftest"));
  // the config loader still accepts the file: no unknown-key warning for last_*, the user keys still apply
  LoadOptions opt;
  opt.user_file = file;
  const auto loaded = load(opt);
  CHECK(loaded.config.tcp_port == 47780);
  CHECK(loaded.config.selftest);
  for (const auto& dgn : loaded.diagnostics) CHECK_FALSE(has(dgn.message, "last_"));
}

TEST_CASE("remembered: a stale temp file from a crash does not matter", "[remembered]") {
  TempDir d;
  const auto file = d.write("x4mp.json", "{\"last_address\":\"keep.example.com:1\"}");
  (void)d.write("x4mp.json.tmp", "{ half written garbage");
  CHECK(read_remembered(file).address == "keep.example.com:1");  // the old file is what a reader sees
  RememberRequest req;
  req.name = "Bob";
  REQUIRE(apply_remember(file, req).ok);
  CHECK(read_remembered(file).address == "keep.example.com:1");
  CHECK(read_remembered(file).name == "Bob");
  CHECK_FALSE(fs::exists(d.path / "x4mp.json.tmp"));
}

TEST_CASE("remembered: a failed write leaves the original file untouched", "[remembered]") {
  TempDir d;
  const std::string original = "{\"last_address\":\"keep.example.com:1\",\"log_level\":\"info\"}";
  const auto file = d.write("x4mp.json", original);
  fs::create_directories(d.path / "x4mp.json.tmp");  // the temp path is a folder: the temp file cannot be created
  RememberRequest req;
  req.address = "other.example.com:2";
  const auto r = apply_remember(file, req);
  CHECK_FALSE(r.ok);
  CHECK(slurp(file) == original);
  CHECK(read_remembered(file).address == "keep.example.com:1");
}

TEST_CASE("remembered: a file that is not a JSON object is never overwritten", "[remembered]") {
  TempDir d;
  const auto file = d.write("x4mp.json", "{ \"log_level\": \"debug\", broken");
  RememberRequest req;
  req.address = "a.example.com:1";
  const auto r = apply_remember(file, req);
  CHECK_FALSE(r.ok);
  CHECK(slurp(file) == "{ \"log_level\": \"debug\", broken");
  const auto arr = d.write("arr.json", "[1,2]");
  CHECK_FALSE(apply_remember(arr, req).ok);
  CHECK(slurp(arr) == "[1,2]");
}

TEST_CASE("remembered: a secret-looking key is refused, a password is never written", "[remembered][secret]") {
  TempDir d;
  const auto file = d.write("x4mp.json", "{\"log_level\":\"info\"}");
  for (const char* key : {"password", "Password", "last_password", "admin_pwd", "api_token", "mySecret"}) {
    const auto r = update_string_keys(file, {{"last_name", "Alice"}, {key, "hunter2-very-secret"}});
    CHECK_FALSE(r.ok);
    CHECK_FALSE(has(r.error, "hunter2"));
  }
  CHECK(slurp(file) == "{\"log_level\":\"info\"}");  // nothing at all was written, not even the harmless key
  CHECK(is_secret_key("PASSWORD"));
  CHECK_FALSE(is_secret_key("last_address"));
  CHECK_FALSE(is_secret_key("last_name"));

  // the x4mp.remember payload has no password field: one a caller adds is ignored and never reaches the file
  const auto req = parse_remember(R"({"v":1,"address":"h.example.com:5","name":"Eve","password":"hunter2-very-secret","admin_password":"x","token":"t"})");
  REQUIRE(req.has_value());
  REQUIRE(apply_remember(file, *req).ok);
  const std::string text = slurp(file);
  CHECK_FALSE(has(text, "hunter2"));
  CHECK_FALSE(has(text, "password"));
  CHECK_FALSE(has(text, "token"));
  CHECK(has(text, "last_address"));
}

TEST_CASE("remembered: parse_remember sanitises and rejects", "[remembered]") {
  std::string err;
  CHECK_FALSE(parse_remember("not json", &err).has_value());
  CHECK_FALSE(err.empty());
  CHECK_FALSE(parse_remember("[]").has_value());
  CHECK_FALSE(parse_remember(R"({"v":1})").has_value());
  CHECK_FALSE(parse_remember(R"({"v":1,"address":"   ","name":""})").has_value());
  const auto r = parse_remember("{\"address\":\"  a.b:1\\n\",\"name\":\"Al\\tice\",\"migrate\":true}");
  REQUIRE(r.has_value());
  CHECK(r->address == "a.b:1");
  CHECK(r->name == "Alice");
  CHECK(r->migrate);
  const auto big = parse_remember("{\"address\":\"" + std::string(400, 'a') + "\",\"name\":\"" + std::string(100, 'n') + "\"}");
  REQUIRE(big.has_value());
  CHECK(big->address.size() == 255);
  CHECK(big->name.size() == 64);
}

TEST_CASE("remembered: migration only fills fields the file does not have, so it happens once", "[remembered]") {
  TempDir d;
  const auto file = d.write("x4mp.json", "{\"log_level\":\"warn\"}");
  RememberRequest legacy;
  legacy.address = "legacy.example.com:1";
  legacy.name = "LegacyName";
  legacy.migrate = true;
  Remembered after;
  REQUIRE(apply_remember(file, legacy, &after).ok);
  CHECK(after.address == "legacy.example.com:1");
  CHECK(after.name == "LegacyName");
  // a second migration (another legacy value) changes nothing
  RememberRequest again;
  again.address = "other-legacy.example.com:2";
  again.name = "Other";
  again.migrate = true;
  REQUIRE(apply_remember(file, again, &after).ok);
  CHECK(after.address == "legacy.example.com:1");
  CHECK(after.name == "LegacyName");
  // a real join overrides
  RememberRequest join;
  join.address = "real.example.com:3";
  join.name = "Real";
  REQUIRE(apply_remember(file, join, &after).ok);
  CHECK(read_remembered(file).address == "real.example.com:3");
  CHECK(read_remembered(file).name == "Real");
  CHECK(has(slurp(file), "\"log_level\": \"warn\""));
  // partial: the file has an address but no name -> only the name is migrated
  const auto half = d.write("half.json", "{\"last_address\":\"keep:1\"}");
  REQUIRE(apply_remember(half, legacy, &after).ok);
  CHECK(after.address == "keep:1");
  CHECK(after.name == "LegacyName");
}

TEST_CASE("remembered: make_remembered_json", "[remembered]") {
  Remembered r;
  r.address = "a.b:1";
  CHECK(make_remembered_json(r) == R"({"v":1,"address":"a.b:1","name":""})");
  CHECK(r.name.empty());
}
