#include "admin_client.h"

#include <cstdlib>
#include <mutex>

#include <nlohmann/json.hpp>

namespace hostsim {
namespace {

void ensure_winsock() {
  static std::once_flag once;
  std::call_once(once, [] {
    WSADATA d{};
    WSAStartup(MAKEWORD(2, 2), &d);
  });
}

}  // namespace

void AdminClient::configure(std::string base_url, std::string user, std::string password) {
  user_ = std::move(user);
  password_ = std::move(password);
  cookies_.clear();
  host_.clear();
  port_ = 0;
  const std::string prefix = "http://";
  if (base_url.rfind(prefix, 0) == 0) base_url.erase(0, prefix.size());
  if (const auto slash = base_url.find('/'); slash != std::string::npos) base_url.erase(slash);
  const auto colon = base_url.rfind(':');
  if (colon == std::string::npos) {
    host_ = base_url;
    port_ = 80;
  } else {
    host_ = base_url.substr(0, colon);
    port_ = static_cast<std::uint16_t>(std::atoi(base_url.c_str() + colon + 1));
  }
}

HttpResponse AdminClient::request(const std::string& method, const std::string& path, const std::string& body, bool with_cookie) {
  ensure_winsock();
  HttpResponse out;
  addrinfo hints{};
  hints.ai_family = AF_INET;
  hints.ai_socktype = SOCK_STREAM;
  addrinfo* res = nullptr;
  if (getaddrinfo(host_.c_str(), std::to_string(port_).c_str(), &hints, &res) != 0 || !res) {
    out.error = "cannot resolve " + host_;
    return out;
  }
  SOCKET s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
  if (s == INVALID_SOCKET || connect(s, res->ai_addr, static_cast<int>(res->ai_addrlen)) != 0) {
    out.error = "cannot connect to " + host_ + ":" + std::to_string(port_);
    if (s != INVALID_SOCKET) closesocket(s);
    freeaddrinfo(res);
    return out;
  }
  freeaddrinfo(res);
  const DWORD tmo = 8000;
  setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&tmo), sizeof(tmo));
  setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&tmo), sizeof(tmo));

  std::string req = method + " " + path + " HTTP/1.0\r\nHost: " + host_ + ":" + std::to_string(port_) +
                    "\r\nX-X4MP: 1\r\nAccept: application/json\r\n";
  if (with_cookie && !cookies_.empty()) {
    req += "Cookie: ";
    bool first = true;
    for (const auto& [k, v] : cookies_) {
      if (!first) req += "; ";
      req += k + "=" + v;
      first = false;
    }
    req += "\r\n";
  }
  if (!body.empty() || method == "POST") {
    req += "Content-Type: application/json\r\nContent-Length: " + std::to_string(body.size()) + "\r\n";
  }
  req += "\r\n" + body;
  for (std::size_t sent = 0; sent < req.size();) {
    const int n = send(s, req.data() + sent, static_cast<int>(req.size() - sent), 0);
    if (n <= 0) {
      out.error = "send failed";
      closesocket(s);
      return out;
    }
    sent += static_cast<std::size_t>(n);
  }
  std::string raw;
  char buf[8192];
  for (;;) {
    const int n = recv(s, buf, sizeof(buf), 0);
    if (n <= 0) break;
    raw.append(buf, static_cast<std::size_t>(n));
  }
  closesocket(s);
  const auto hdr_end = raw.find("\r\n\r\n");
  if (raw.rfind("HTTP/", 0) != 0 || hdr_end == std::string::npos) {
    out.error = "malformed HTTP response";
    return out;
  }
  const auto sp = raw.find(' ');
  out.status = std::atoi(raw.c_str() + sp + 1);
  out.body = raw.substr(hdr_end + 4);
  // Set-Cookie: name=value; ...
  std::size_t pos = raw.find("\r\n");
  while (pos != std::string::npos && pos < hdr_end) {
    const auto next = raw.find("\r\n", pos + 2);
    const std::string line = raw.substr(pos + 2, (next == std::string::npos ? hdr_end : next) - pos - 2);
    if (line.size() > 11 && _strnicmp(line.c_str(), "Set-Cookie:", 11) == 0) {
      std::string kv = line.substr(11);
      kv.erase(0, kv.find_first_not_of(' '));
      kv = kv.substr(0, kv.find(';'));
      if (const auto eq = kv.find('='); eq != std::string::npos) cookies_[kv.substr(0, eq)] = kv.substr(eq + 1);
    }
    pos = next;
  }
  return out;
}

bool AdminClient::login(std::string& error) {
  cookies_.clear();
  const nlohmann::json body = {{"username", user_}, {"password", password_}};
  const auto r = request("POST", "/api/v1/auth/login", body.dump(), false);
  if (!r.error.empty()) {
    error = r.error;
    return false;
  }
  if (r.status < 200 || r.status >= 300) {
    error = "admin login failed: HTTP " + std::to_string(r.status);
    return false;
  }
  return true;
}

HttpResponse AdminClient::get(const std::string& path) {
  HttpResponse r;
  if (!configured()) {
    r.error = "admin API not configured (use --admin-url)";
    return r;
  }
  std::string err;
  if (cookies_.empty() && !user_.empty() && !login(err)) {
    r.error = err;
    return r;
  }
  r = request("GET", path, "", true);
  if (r.status == 401 && !user_.empty()) {
    if (!login(err)) {
      r.error = err;
      return r;
    }
    r = request("GET", path, "", true);
  }
  return r;
}

}  // namespace hostsim
