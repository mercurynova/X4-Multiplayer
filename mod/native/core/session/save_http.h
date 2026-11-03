#pragma once
// core/session: save download over HTTP (protocol.md 6.4, decisions.md ADR-007). The fallback transport for a node
// that advertised Capability::SaveHttp: the server then puts an absolute or root-relative URL plus a player-bound
// token into SessionSaveInfo, and the bytes come from "GET <url>" with "Authorization: X4MP-Download <token>",
// Range/If-Range for a resume and ETag = "<sha256>".
//
// Why it exists: a Bulk transfer competes with the realtime lane for the same TCP connection, so a big save on a
// slow link stalls the session for minutes. HTTP is a second connection, resumable with plain Range, and the server
// can serve it from a different process or a CDN later.
//
//   node -> GET <path> HTTP/1.1                      (the worker thread)
//   node <- 200 (whole file) or 206 (from the resume offset) or 416 (nothing left)
//
// The worker owns the socket and the part file; the session thread only reads progress() and calls cancel(). Bytes
// land in "<final>.part" and are hashed incrementally (BCrypt SHA-256), exactly like the in-band SaveDownloader:
// a match renames the part file into place, a mismatch deletes it (HashMismatch) and the caller sends LoadStatus
// Failed. An interrupted transfer keeps the part file, so the next begin() resumes with Range from its length.
//
// Cancellation closes the socket from under the blocking recv, so cancel() returns in well under a second. No
// exceptions cross the API; core/ only.

#include <atomic>
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "core/session/save_download.h"  // TransferKind, DownloadState, DownloadError, DownloadProgress

namespace x4mp::session {

// The server's HTTP port (admin GUI and /files/saves; the node port is 47780). Used for a root-relative URL such as
// "/files/saves/<sha>", which is what the server sends unless SaveOptions.PublicHttpBaseUrl names a host.
inline constexpr std::uint16_t kDefaultHttpPort = 47790;

struct HttpTarget {
  std::string host;
  std::uint16_t port = kDefaultHttpPort;
  std::string path;  // always starts with '/', e.g. "/files/saves/ab12..."
};

// Turns a SessionSaveInfo URL into a target. Accepts "http://host[:port]/path" and "/path" (resolved against
// `fallback_host`, i.e. the node endpoint's host, on port 47790). https is not supported by this transport: the mod
// talks plain HTTP to the server it already trusts. Returns false with a human-readable `error` otherwise.
[[nodiscard]] bool parse_http_target(std::string_view url, std::string_view fallback_host, HttpTarget& out,
                                     std::string& error);

struct HttpSpec {
  TransferKind kind = TransferKind::Save;
  HttpTarget target;
  std::string token;                 // SessionSaveInfo.download_token; empty means no Authorization header
  std::vector<std::uint8_t> sha256;  // 32 bytes: the expected digest and the If-Range validator
  std::uint64_t size = 0;            // announced size; more bytes from the server is a protocol error
  std::filesystem::path final_path;  // the part file is "<final>.part"
};

class HttpSaveDownloader {
 public:
  HttpSaveDownloader() = default;
  ~HttpSaveDownloader();
  HttpSaveDownloader(const HttpSaveDownloader&) = delete;
  HttpSaveDownloader& operator=(const HttpSaveDownloader&) = delete;

  // Arms a transfer on a worker thread, replacing (and joining) any previous one. Returns false with
  // progress().state == Failed on a bad spec; every later failure shows up in progress() as well.
  bool begin(const HttpSpec& spec);

  [[nodiscard]] bool active() const;  // Requesting or Receiving
  [[nodiscard]] DownloadProgress progress() const;

  // Stops the worker and drops the transfer; the part file stays for a later begin().
  void cancel();

 private:
  void run(HttpSpec spec);
  void publish(DownloadState state, DownloadError error, std::string detail);

  mutable std::mutex mutex_;
  DownloadProgress prog_;
  std::thread worker_;
  std::atomic<bool> stop_{false};
  std::uintptr_t socket_ = ~std::uintptr_t{0};  // the live socket while run() is connected
};

}  // namespace x4mp::session
