// Tiny blocking HTTP/1.0 client for the server admin REST API (hostsim `expect-admin`). Winsock only, no TLS: the
// server in CI/e2e listens on plain http://127.0.0.1:<port>. HTTP/1.0 means no chunked bodies: read until close.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <cstdint>
#include <map>
#include <string>

namespace hostsim {

struct HttpResponse {
  int status = 0;
  std::string body;
  std::string error;  // non-empty when the request could not be made at all
};

class AdminClient {
 public:
  // base_url: "http://127.0.0.1:47790" (no path). Empty = admin API not configured.
  void configure(std::string base_url, std::string user, std::string password);
  [[nodiscard]] bool configured() const { return !host_.empty(); }
  [[nodiscard]] const std::string& host() const { return host_; }
  [[nodiscard]] std::uint16_t port() const { return port_; }

  // GET with the session cookie; logs in first (and again on a 401, e.g. after a server restart).
  HttpResponse get(const std::string& path);
  HttpResponse request(const std::string& method, const std::string& path, const std::string& body, bool with_cookie);

 private:
  bool login(std::string& error);

  std::string host_;
  std::uint16_t port_ = 0;
  std::string user_, password_;
  std::map<std::string, std::string> cookies_;
};

}  // namespace hostsim
