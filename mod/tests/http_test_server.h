#pragma once
// Test-only loopback HTTP/1.1 server (protocol.md 6.4): one request per connection, optional Range support, an optional
// token check and a forced status for the refusals. Used by test_save_http.cpp (the downloader on its own) and by
// test_session_handshake.cpp (a session that fetches its save over HTTP instead of the Bulk lane). Nothing here leaves
// this machine: it binds 127.0.0.1:0 and the tests stop it before they end.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <atomic>
#include <charconv>
#include <cctype>
#include <cstdint>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#include "core/crypto/crypto.h"
#include "core/session/save_http.h"

namespace fake {

// Case-insensitive lookup of one header in the head block (request line + headers, up to CRLFCRLF).
inline std::string http_header_value(const std::string& head, std::string_view name) {
  const std::size_t block_end = head.find("\r\n\r\n");
  const std::string block = head.substr(0, block_end == std::string::npos ? head.size() : block_end);
  std::size_t pos = block.find("\r\n");
  while (pos != std::string::npos) {
    const std::size_t start = pos + 2;
    const std::size_t eol = block.find("\r\n", start);
    const std::string line = block.substr(start, eol == std::string::npos ? std::string::npos : eol - start);
    const std::size_t colon = line.find(':');
    if (colon != std::string::npos) {
      std::string key = line.substr(0, colon);
      for (auto& ch : key) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
      if (key == name) {
        std::size_t value = colon + 1;
        while (value < line.size() && (line[value] == ' ' || line[value] == '\t')) ++value;
        return line.substr(value);
      }
    }
    pos = eol;
  }
  return {};
}

struct HttpRequest {
  std::string head;

  [[nodiscard]] std::string header(std::string_view name) const { return http_header_value(head, name); }
  [[nodiscard]] std::string line() const { return head.substr(0, head.find("\r\n")); }
};

inline bool http_send_all(SOCKET socket, const void* data, std::size_t size) {
  const auto* bytes = static_cast<const char*>(data);
  std::size_t sent = 0;
  while (sent < size) {
    const int n = ::send(socket, bytes + sent, static_cast<int>(size - sent), 0);
    if (n <= 0) return false;
    sent += static_cast<std::size_t>(n);
  }
  return true;
}

// Answers every accepted connection with the same body. `honour_range` makes it serve a 206 for "Range: bytes=N-",
// `forced_status` replaces the answer with an empty one of that code, and `token` (when set) is the only accepted
// Authorization value.
class HttpServer {
 public:
  explicit HttpServer(std::vector<std::uint8_t> body, bool honour_range = true, int forced_status = 0,
                      std::string token = {})
      : body_(std::move(body)), honour_range_(honour_range), forced_status_(forced_status), token_(std::move(token)) {
    listen_ = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listen_ == INVALID_SOCKET) throw std::runtime_error("socket() failed");
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    if (::bind(listen_, reinterpret_cast<const sockaddr*>(&addr), sizeof addr) == SOCKET_ERROR) {
      ::closesocket(listen_);
      listen_ = INVALID_SOCKET;
      throw std::runtime_error("bind() failed");
    }
    int len = sizeof addr;
    if (::getsockname(listen_, reinterpret_cast<sockaddr*>(&addr), &len) == SOCKET_ERROR ||
        ::listen(listen_, 8) == SOCKET_ERROR) {
      ::closesocket(listen_);
      listen_ = INVALID_SOCKET;
      throw std::runtime_error("listen() failed");
    }
    port_ = ::ntohs(addr.sin_port);
    thread_ = std::thread([this] { serve(); });
  }

  ~HttpServer() { stop(); }

  HttpServer(const HttpServer&) = delete;
  HttpServer& operator=(const HttpServer&) = delete;

  [[nodiscard]] std::uint16_t port() const { return port_; }

  [[nodiscard]] x4mp::session::HttpTarget target(std::string path = "/files/saves/save") const {
    return x4mp::session::HttpTarget{"127.0.0.1", port_, std::move(path)};
  }

  [[nodiscard]] std::string url(std::string path = "/files/saves/save") const {
    return "http://127.0.0.1:" + std::to_string(port_) + std::move(path);
  }

  [[nodiscard]] std::vector<HttpRequest> requests() const {
    const std::lock_guard lock(mutex_);
    return requests_;
  }

  void stop() {
    running_ = false;
    if (thread_.joinable()) thread_.join();
    if (listen_ != INVALID_SOCKET) {
      ::closesocket(listen_);
      listen_ = INVALID_SOCKET;
    }
  }

 private:
  void serve() {
    while (running_) {
      fd_set set;
      FD_ZERO(&set);
      FD_SET(listen_, &set);
      timeval wait{0, 100000};  // 100 ms: the stop flag is checked between waits
      const int ready = ::select(0, &set, nullptr, nullptr, &wait);
      if (!running_) break;
      if (ready <= 0) continue;
      const SOCKET client = ::accept(listen_, nullptr, nullptr);
      if (client == INVALID_SOCKET) continue;
      handle(client);
      ::closesocket(client);
    }
  }

  void handle(SOCKET client) {
    std::string head;
    char buffer[4096];
    while (head.find("\r\n\r\n") == std::string::npos && head.size() < 32768) {
      const int n = ::recv(client, buffer, sizeof buffer, 0);
      if (n <= 0) return;
      head.append(buffer, static_cast<std::size_t>(n));
    }
    {
      const std::lock_guard lock(mutex_);
      requests_.push_back(HttpRequest{head});
    }

    if (forced_status_ != 0) {
      answer(client, forced_status_, {}, {}, 0);
      return;
    }
    if (!token_.empty() && http_header_value(head, "authorization") != "X4MP-Download " + token_) {
      answer(client, 401, {}, {}, 0);
      return;
    }

    std::uint64_t first = 0;
    bool partial = false;
    if (honour_range_) {
      const std::string range = http_header_value(head, "range");
      if (range.rfind("bytes=", 0) == 0) {
        const std::string spec = range.substr(6);
        std::uint64_t value = 0;
        if (spec.size() > 1 && spec.back() == '-' &&
            std::from_chars(spec.data(), spec.data() + spec.size() - 1, value).ec == std::errc{}) {
          first = value;
          partial = true;
        }
      }
    }

    const std::uint64_t size = body_.size();
    if (first >= size && !(first == size && partial)) {
      answer(client, 416, {}, {}, size);
      return;
    }
    if (first == size) {  // "bytes=<size>-": nothing left, but the range itself is satisfiable
      answer(client, 206, {}, {}, size);
      return;
    }
    const std::vector<std::uint8_t> slice(body_.begin() + static_cast<std::ptrdiff_t>(first), body_.end());
    if (partial) {
      answer(client, 206, slice,
             "bytes " + std::to_string(first) + "-" + std::to_string(size - 1) + "/" + std::to_string(size), size);
    } else {
      answer(client, 200, slice, {}, size);
    }
  }

  void answer(SOCKET client, int status, const std::vector<std::uint8_t>& body, const std::string& content_range,
              std::uint64_t total) {
    std::string head = "HTTP/1.1 " + std::to_string(status) + " " + (status == 200   ? "OK"
                                                                     : status == 206 ? "Partial Content"
                                                                     : status == 401 ? "Unauthorized"
                                                                     : status == 404 ? "Not Found"
                                                                     : status == 416 ? "Range Not Satisfiable"
                                                                                     : "Error") +
                       "\r\n";
    if (!content_range.empty()) head += "Content-Range: " + content_range + "\r\n";
    if (status == 416) head += "Content-Range: bytes */" + std::to_string(total) + "\r\n";
    head += "Content-Length: " + std::to_string(body.size()) + "\r\n";
    head += "ETag: \"" + x4mp::crypto::to_hex(body_.empty() ? x4mp::crypto::Sha256Digest{}
                                                           : x4mp::crypto::sha256(body_).value_or(
                                                                 x4mp::crypto::Sha256Digest{})) +
            "\"\r\n";
    head += "Connection: close\r\n\r\n";
    if (!http_send_all(client, head.data(), head.size())) return;
    if (!body.empty()) (void)http_send_all(client, body.data(), body.size());
  }

  std::vector<std::uint8_t> body_;
  bool honour_range_ = true;
  int forced_status_ = 0;
  std::string token_;
  SOCKET listen_ = INVALID_SOCKET;
  std::uint16_t port_ = 0;
  std::atomic<bool> running_{true};
  std::thread thread_;
  mutable std::mutex mutex_;
  std::vector<HttpRequest> requests_;
};

}  // namespace fake
