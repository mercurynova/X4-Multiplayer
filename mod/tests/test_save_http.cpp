// core/session HttpSaveDownloader (protocol.md 6.4, ADR-007): URL parsing, a fresh download, a resumed download from a
// part file, SHA-256 verification, and the refusals a real server can answer. The server is fake::HttpServer from
// http_test_server.h: a loopback HTTP/1.1 listener on 127.0.0.1:0, so nothing touches the network outside this machine.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "core/crypto/crypto.h"
#include "core/session/save_http.h"
#include "fake_server.h"
#include "http_test_server.h"

using namespace x4mp::session;
using fake::HttpServer;
namespace fs = std::filesystem;

namespace {

// ---------------------------------------------------------------------------------------------- fixtures

struct TempDir {
  fs::path path;
  explicit TempDir(const char* tag) {
    path = fs::temp_directory_path() / (std::string("x4mp_http_") + tag);
    std::error_code ec;
    fs::remove_all(path, ec);
    fs::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    fs::remove_all(path, ec);
  }
};

std::vector<std::uint8_t> make_data(std::size_t n) {
  std::vector<std::uint8_t> data(n);
  std::uint32_t state = 0x12345678u;
  for (std::size_t i = 0; i < n; ++i) {
    state = state * 1664525u + 1013904223u;
    data[i] = static_cast<std::uint8_t>(state >> 24);
  }
  return data;
}

std::vector<std::uint8_t> read_all(const fs::path& file) {
  std::ifstream in(file, std::ios::binary);
  return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
}

void write_all(const fs::path& file, const std::vector<std::uint8_t>& data) {
  std::ofstream out(file, std::ios::binary | std::ios::trunc);
  out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
}

std::vector<std::uint8_t> digest_of(const std::vector<std::uint8_t>& data) {
  const auto digest = x4mp::crypto::sha256(data);
  REQUIRE(digest.has_value());
  return std::vector<std::uint8_t>(digest->begin(), digest->end());
}

std::string hex_of(std::span<const std::uint8_t> bytes) {
  return x4mp::crypto::to_hex(bytes);
}

HttpSpec spec_for(const HttpTarget& target, const std::vector<std::uint8_t>& data, std::string token,
                  const fs::path& final_path) {
  HttpSpec spec;
  spec.kind = TransferKind::Save;
  spec.target = target;
  spec.token = std::move(token);
  spec.sha256 = digest_of(data);
  spec.size = data.size();
  spec.final_path = final_path;
  return spec;
}

}  // namespace

// ---------------------------------------------------------------------------------------------- URL parsing

TEST_CASE("A root-relative download URL resolves against the connected host on the HTTP port", "[save][http]") {
  HttpTarget target;
  std::string error;
  REQUIRE(parse_http_target("/files/saves/abc", "10.0.0.5", target, error));
  CHECK(target.host == "10.0.0.5");
  CHECK(target.port == kDefaultHttpPort);
  CHECK(target.path == "/files/saves/abc");
  CHECK(error.empty());
}

TEST_CASE("An absolute download URL keeps its own host and port", "[save][http]") {
  HttpTarget target;
  std::string error;
  REQUIRE(parse_http_target("http://127.0.0.1:1234/files/saves/abc", "", target, error));
  CHECK(target.host == "127.0.0.1");
  CHECK(target.port == 1234);
  CHECK(target.path == "/files/saves/abc");

  REQUIRE(parse_http_target("http://example.com/files/saves/abc", "", target, error));
  CHECK(target.host == "example.com");
  CHECK(target.port == kDefaultHttpPort);
  CHECK(target.path == "/files/saves/abc");
}

TEST_CASE("A download URL that is neither http nor root-relative is refused", "[save][http]") {
  HttpTarget target;
  std::string error;
  CHECK_FALSE(parse_http_target("", "127.0.0.1", target, error));
  CHECK(error.find("empty") != std::string::npos);

  error.clear();
  CHECK_FALSE(parse_http_target("https://example.com/files/saves/abc", "127.0.0.1", target, error));
  CHECK(error.find("https") != std::string::npos);

  error.clear();
  CHECK_FALSE(parse_http_target("files/saves/abc", "127.0.0.1", target, error));
  CHECK(error.find("neither") != std::string::npos);

  error.clear();
  CHECK_FALSE(parse_http_target("/files/saves/abc", "", target, error));  // no host to resolve against
  CHECK_FALSE(error.empty());
}

// ---------------------------------------------------------------------------------------------- downloads

TEST_CASE("A save downloads over HTTP, is verified and stored without a part file", "[save][http]") {
  TempDir dir("fresh");
  const auto data = make_data(200000);
  HttpServer server(data, true, 0, "tok");

  HttpSaveDownloader dl;
  const auto spec = spec_for(server.target(), data, "tok", dir.path / "save.xml.gz");
  REQUIRE(dl.begin(spec));
  REQUIRE(fake::wait_until([&] { return !dl.active(); }));

  const DownloadProgress p = dl.progress();
  CHECK(p.state == DownloadState::Completed);
  CHECK(p.error == DownloadError::None);
  CHECK(p.bytes_done == data.size());
  CHECK(p.size == data.size());
  CHECK(p.requests_sent == 1);
  CHECK(read_all(spec.final_path) == data);
  CHECK_FALSE(fs::exists(fs::path(spec.final_path.string() + ".part")));

  const auto requests = server.requests();
  REQUIRE(requests.size() == 1);
  CHECK(requests[0].line() == "GET /files/saves/save HTTP/1.1");
  CHECK(requests[0].header("authorization") == "X4MP-Download tok");
  CHECK(requests[0].header("range").empty());
}

TEST_CASE("A part file makes the HTTP download resume with a Range request", "[save][http]") {
  TempDir dir("resume");
  const auto data = make_data(150000);
  const std::size_t half = data.size() / 2;
  HttpServer server(data);

  const fs::path final_path = dir.path / "save.xml.gz";
  fs::path part = final_path;
  part += ".part";
  write_all(part, std::vector<std::uint8_t>(data.begin(), data.begin() + static_cast<std::ptrdiff_t>(half)));

  HttpSaveDownloader dl;
  const auto spec = spec_for(server.target(), data, "", final_path);
  REQUIRE(dl.begin(spec));
  REQUIRE(fake::wait_until([&] { return !dl.active(); }));

  const DownloadProgress p = dl.progress();
  CHECK(p.state == DownloadState::Completed);
  CHECK(p.resumed_from == half);
  CHECK(read_all(final_path) == data);

  const auto requests = server.requests();
  REQUIRE(requests.size() == 1);
  CHECK(requests[0].header("range") == "bytes=" + std::to_string(half) + "-");
  CHECK(requests[0].header("if-range") == "\"" + hex_of(spec.sha256) + "\"");
}

TEST_CASE("A part file that already holds every byte is verified without a request", "[save][http]") {
  TempDir dir("complete");
  const auto data = make_data(4096);
  HttpServer server(data);

  const fs::path final_path = dir.path / "save.xml.gz";
  fs::path part = final_path;
  part += ".part";
  write_all(part, data);

  HttpSaveDownloader dl;
  const auto spec = spec_for(server.target(), data, "", final_path);
  REQUIRE(dl.begin(spec));
  REQUIRE(fake::wait_until([&] { return !dl.active(); }));

  const DownloadProgress p = dl.progress();
  CHECK(p.state == DownloadState::Completed);
  CHECK(p.requests_sent == 0);
  CHECK(read_all(final_path) == data);
  CHECK(server.requests().empty());
}

TEST_CASE("A downloaded save with the wrong SHA-256 fails and the part file is deleted", "[save][http]") {
  TempDir dir("mismatch");
  const auto data = make_data(10000);
  HttpServer server(data);

  HttpSaveDownloader dl;
  auto spec = spec_for(server.target(), data, "", dir.path / "save.xml.gz");
  spec.sha256 = digest_of(make_data(10000));  // make_data is deterministic, so flip a byte to get a different digest
  spec.sha256[0] ^= 0xFF;
  REQUIRE(dl.begin(spec));
  REQUIRE(fake::wait_until([&] { return !dl.active(); }));

  const DownloadProgress p = dl.progress();
  CHECK(p.state == DownloadState::Failed);
  CHECK(p.error == DownloadError::HashMismatch);
  CHECK_FALSE(fs::exists(spec.final_path));
  CHECK_FALSE(fs::exists(fs::path(spec.final_path.string() + ".part")));
}

TEST_CASE("A refused token and a missing save are reported as protocol errors", "[save][http]") {
  TempDir dir("refused");
  const auto data = make_data(2048);

  SECTION("401") {
    HttpServer server(data, true, 0, "right");
    HttpSaveDownloader dl;
    const auto spec = spec_for(server.target(), data, "wrong", dir.path / "save.xml.gz");
    REQUIRE(dl.begin(spec));
    REQUIRE(fake::wait_until([&] { return !dl.active(); }));
    const DownloadProgress p = dl.progress();
    CHECK(p.state == DownloadState::Failed);
    CHECK(p.error == DownloadError::ProtocolError);
    CHECK(p.detail.find("token was refused") != std::string::npos);
  }

  SECTION("404") {
    HttpServer server(data, true, 404);
    HttpSaveDownloader dl;
    const auto spec = spec_for(server.target(), data, "", dir.path / "save.xml.gz");
    REQUIRE(dl.begin(spec));
    REQUIRE(fake::wait_until([&] { return !dl.active(); }));
    const DownloadProgress p = dl.progress();
    CHECK(p.state == DownloadState::Failed);
    CHECK(p.error == DownloadError::ProtocolError);
    CHECK(p.detail.find("not on the server") != std::string::npos);
  }
}

TEST_CASE("A bad HTTP spec is refused before a worker starts", "[save][http]") {
  TempDir dir("bad");
  HttpSaveDownloader dl;

  HttpSpec spec;
  spec.sha256 = digest_of(make_data(16));
  spec.size = 16;
  spec.final_path = dir.path / "save.xml.gz";  // no target
  auto progress = [&] { return dl.progress(); };

  CHECK_FALSE(dl.begin(spec));
  CHECK(progress().state == DownloadState::Failed);
  CHECK(progress().error == DownloadError::ProtocolError);
  CHECK(progress().detail.find("HTTP target") != std::string::npos);

  spec.target = HttpTarget{"127.0.0.1", 1, "/files/saves/save"};
  spec.token = "bad\r\ntoken";
  CHECK_FALSE(dl.begin(spec));
  CHECK(progress().error == DownloadError::ProtocolError);
  CHECK(progress().detail.find("token") != std::string::npos);
}
