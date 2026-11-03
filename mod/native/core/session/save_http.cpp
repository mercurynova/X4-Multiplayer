#include "core/session/save_http.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <cstring>
#include <fstream>
#include <system_error>
#include <utility>

#include <winsock2.h>
#include <ws2tcpip.h>

#include "core/crypto/crypto.h"
#include "core/log/log.h"

namespace x4mp::session {
namespace {

constexpr std::uint32_t kConnectTimeoutMs = 5000;
constexpr std::uint32_t kIoTimeoutMs = 15000;  // a stalled link fails the transfer instead of hanging the join
constexpr std::size_t kHeaderCap = 64 * 1024;
constexpr std::size_t kReadBuffer = 64 * 1024;
constexpr std::uintptr_t kNoSocket = ~std::uintptr_t{0};
constexpr const char* kDownloadScheme = "X4MP-Download";

void ensure_winsock() {
  static const bool ready = [] {
    WSADATA data{};
    const int rc = ::WSAStartup(MAKEWORD(2, 2), &data);
    if (rc != 0) X4MP_LOGW("save: HTTP download unavailable, WSAStartup failed ({})", rc);
    return rc == 0;
  }();
  (void)ready;
}

char lower(char c) noexcept { return (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c; }

bool equals_ci(std::string_view a, std::string_view b) noexcept {
  if (a.size() != b.size()) return false;
  for (std::size_t i = 0; i < a.size(); ++i) {
    if (lower(a[i]) != lower(b[i])) return false;
  }
  return true;
}

// Host, path and token are pasted into a raw request line / header, so a space, a control byte or a CR would let the
// server (or a compromised manifest) smuggle a second header. Values must be printable ASCII with no spaces; paths
// carry their escapes ("%20") already.
bool header_safe(std::string_view value) noexcept {
  for (const char c : value) {
    if (c <= 0x20 || c >= 0x7f) return false;
  }
  return true;
}

bool header_value(std::string_view head, std::string_view name, std::string& out) {
  std::size_t pos = head.find("\r\n");
  while (pos != std::string_view::npos) {
    const std::size_t start = pos + 2;
    if (start >= head.size()) return false;
    const std::size_t end = head.find("\r\n", start);
    const std::string_view line = head.substr(start, end == std::string_view::npos ? std::string_view::npos : end - start);
    const std::size_t colon = line.find(':');
    if (colon != std::string_view::npos && equals_ci(line.substr(0, colon), name)) {
      std::string_view value = line.substr(colon + 1);
      while (!value.empty() && (value.front() == ' ' || value.front() == '\t')) value.remove_prefix(1);
      while (!value.empty() && (value.back() == ' ' || value.back() == '\t')) value.remove_suffix(1);
      out.assign(value);
      return true;
    }
    pos = end;
  }
  return false;
}

SOCKET connect_socket(const std::string& host, std::uint16_t port, std::string& error) {
  addrinfo hints{};
  hints.ai_family = AF_UNSPEC;
  hints.ai_socktype = SOCK_STREAM;
  hints.ai_protocol = IPPROTO_TCP;
  addrinfo* list = nullptr;
  const std::string service = std::to_string(port);
  const int rc = ::getaddrinfo(host.c_str(), service.c_str(), &hints, &list);
  if (rc != 0 || list == nullptr) {
    error = "getaddrinfo(" + host + ") failed (" + std::to_string(rc) + ")";
    return INVALID_SOCKET;
  }

  SOCKET result = INVALID_SOCKET;
  for (addrinfo* a = list; a != nullptr && result == INVALID_SOCKET; a = a->ai_next) {
    const SOCKET s = ::socket(a->ai_family, a->ai_socktype, a->ai_protocol);
    if (s == INVALID_SOCKET) continue;
    u_long nonblocking = 1;
    ::ioctlsocket(s, FIONBIO, &nonblocking);
    bool ok = ::connect(s, a->ai_addr, static_cast<int>(a->ai_addrlen)) == 0;
    if (!ok && ::WSAGetLastError() == WSAEWOULDBLOCK) {
      fd_set writable;
      FD_ZERO(&writable);
      FD_SET(s, &writable);
      timeval tv{};
      tv.tv_sec = kConnectTimeoutMs / 1000;
      tv.tv_usec = static_cast<long>((kConnectTimeoutMs % 1000) * 1000);
      if (::select(0, nullptr, &writable, nullptr, &tv) == 1) {
        int so_error = 0;
        int len = static_cast<int>(sizeof(so_error));
        ok = ::getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&so_error), &len) == 0 && so_error == 0;
      }
    }
    if (!ok) {
      ::closesocket(s);
      continue;
    }
    nonblocking = 0;
    ::ioctlsocket(s, FIONBIO, &nonblocking);
    const DWORD timeout = kIoTimeoutMs;
    ::setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    ::setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
    result = s;
  }
  ::freeaddrinfo(list);
  if (result == INVALID_SOCKET) error = "could not connect to " + host + ":" + service;
  return result;
}

bool send_all(SOCKET s, std::string_view data) {
  std::size_t sent = 0;
  while (sent < data.size()) {
    const int n = ::send(s, data.data() + sent, static_cast<int>(std::min<std::size_t>(data.size() - sent, 1u << 20)), 0);
    if (n <= 0) return false;
    sent += static_cast<std::size_t>(n);
  }
  return true;
}

int status_code(std::string_view head) {
  if (head.size() < 12 || head.substr(0, 5) != "HTTP/") return 0;
  const std::size_t space = head.find(' ');
  if (space == std::string_view::npos || space + 4 > head.size()) return 0;
  int code = 0;
  const auto* first = head.data() + space + 1;
  if (std::from_chars(first, first + 3, code).ec != std::errc{}) return 0;
  return code;
}

}  // namespace

bool parse_http_target(std::string_view url, std::string_view fallback_host, HttpTarget& out, std::string& error) {
  out = HttpTarget{};
  error.clear();
  if (url.empty()) {
    error = "the server sent an empty download URL";
    return false;
  }

  std::string_view rest = url;
  if (rest.size() >= 7 && equals_ci(rest.substr(0, 7), "http://")) {
    rest.remove_prefix(7);
  } else if (rest.size() >= 8 && equals_ci(rest.substr(0, 8), "https://")) {
    error = "https download URLs are not supported; the mod talks plain HTTP to its server";
    return false;
  } else if (rest.front() == '/') {
    // Root-relative: the server's default (PublicHttpBaseUrl is empty), resolved against the node's host on the
    // HTTP port. That is two ports away from the node port, so it must not reuse endpoint.port.
    if (fallback_host.empty()) {
      error = "a root-relative download URL needs a known node host";
      return false;
    }
    out.host.assign(fallback_host);
    out.port = kDefaultHttpPort;
    out.path.assign(rest);
    if (!header_safe(out.host) || !header_safe(out.path)) {
      error = "the download URL contains unusable characters";
      return false;
    }
    return true;
  } else {
    error = "the download URL is neither http:// nor root-relative: " + std::string(url);
    return false;
  }

  const std::size_t slash = rest.find('/');
  if (slash == std::string_view::npos || slash + 1 >= rest.size()) {
    error = "the download URL has no path: " + std::string(url);
    return false;
  }
  std::string_view authority = rest.substr(0, slash);
  std::string_view path = rest.substr(slash);
  std::string_view host = authority;
  std::string_view port_text;

  if (!authority.empty() && authority.front() == '[') {
    const std::size_t close = authority.find(']');
    if (close == std::string_view::npos) {
      error = "the download URL has an unterminated IPv6 host: " + std::string(url);
      return false;
    }
    host = authority.substr(1, close - 1);
    const std::string_view after = authority.substr(close + 1);
    if (!after.empty()) {
      if (after.front() != ':') {
        error = "the download URL has junk after the host: " + std::string(url);
        return false;
      }
      port_text = after.substr(1);
    }
  } else {
    const std::size_t colon = authority.rfind(':');
    if (colon != std::string_view::npos) {
      host = authority.substr(0, colon);
      port_text = authority.substr(colon + 1);
    }
  }

  if (host.empty()) {
    error = "the download URL has no host: " + std::string(url);
    return false;
  }
  std::uint32_t port = kDefaultHttpPort;
  if (!port_text.empty()) {
    const auto parsed = std::from_chars(port_text.data(), port_text.data() + port_text.size(), port);
    if (parsed.ec != std::errc{} || parsed.ptr != port_text.data() + port_text.size() || port == 0 || port > 65535) {
      error = "the download URL has an invalid port: " + std::string(port_text);
      return false;
    }
  }

  out.host.assign(host);
  out.port = static_cast<std::uint16_t>(port);
  out.path.assign(path);
  if (!header_safe(out.host) || !header_safe(out.path)) {
    error = "the download URL contains unusable characters";
    return false;
  }
  return true;
}

HttpSaveDownloader::~HttpSaveDownloader() { cancel(); }

void HttpSaveDownloader::publish(DownloadState state, DownloadError error, std::string detail) {
  const std::lock_guard lock(mutex_);
  prog_.state = state;
  prog_.error = error;
  prog_.detail = std::move(detail);
  if (state == DownloadState::Failed) {
    X4MP_LOGW("save: HTTP download failed ({}): {}", to_string(error), prog_.detail);
  }
}

bool HttpSaveDownloader::begin(const HttpSpec& spec) {
  cancel();

  {
    const std::lock_guard lock(mutex_);
    prog_ = DownloadProgress{};
    prog_.kind = spec.kind;
    prog_.size = spec.size;
  }

  std::string error;
  if (spec.sha256.size() != crypto::kSha256Size || spec.size == 0 || spec.final_path.empty()) {
    error = "bad download spec (need a 32-byte sha256, a size and a path)";
  } else if (spec.target.host.empty() || spec.target.path.empty()) {
    error = "bad HTTP target (need a host and a path)";
  } else if (!spec.token.empty() && !header_safe(spec.token)) {
    error = "the download token contains unusable characters";
  }
  if (!error.empty()) {
    publish(DownloadState::Failed, DownloadError::ProtocolError, std::move(error));
    return false;
  }

  stop_.store(false);
  publish(DownloadState::Requesting, DownloadError::None, std::string{});
  worker_ = std::thread([this, spec] { run(spec); });
  return true;
}

void HttpSaveDownloader::run(HttpSpec spec) {
  ensure_winsock();

  const std::uint64_t size = spec.size;
  auto fail = [this](DownloadError error, std::string detail) {
    publish(DownloadState::Failed, error, std::move(detail));
  };
  auto stopped = [this] { return stop_.load(std::memory_order_relaxed); };

  std::filesystem::path part = spec.final_path;
  part += ".part";

  std::error_code ec;
  if (spec.final_path.has_parent_path()) std::filesystem::create_directories(spec.final_path.parent_path(), ec);
  ec.clear();
  std::uint64_t existing = 0;
  if (std::filesystem::exists(part, ec)) {
    existing = static_cast<std::uint64_t>(std::filesystem::file_size(part, ec));
    if (ec) existing = 0;
    if (existing > size) {  // not ours (or a different file reusing the name): start over
      std::filesystem::remove(part, ec);
      existing = 0;
    }
  }

  crypto::Sha256Hasher hasher;
  if (!hasher.ok()) {
    fail(DownloadError::HashUnavailable, "SHA-256 provider unavailable");
    return;
  }
  if (existing > 0) {
    // Resume: hash what is already on disk, then continue appending.
    std::ifstream in(part, std::ios::binary);
    std::vector<std::uint8_t> buf(1u << 20);
    std::uint64_t left = existing;
    while (in && left > 0) {
      in.read(reinterpret_cast<char*>(buf.data()),
              static_cast<std::streamsize>(std::min<std::uint64_t>(buf.size(), left)));
      const auto got = static_cast<std::uint64_t>(in.gcount());
      if (got == 0) break;
      hasher.update(std::span<const std::uint8_t>(buf.data(), static_cast<std::size_t>(got)));
      left -= got;
    }
    if (left != 0) {
      fail(DownloadError::IoError, "could not read the existing part file");
      return;
    }
  }
  {
    const std::lock_guard lock(mutex_);
    prog_.resumed_from = existing;
    prog_.bytes_done = existing;
  }

  std::ofstream out;
  std::uint64_t next = existing;

  auto finish = [&]() {
    if (out.is_open()) {
      out.flush();
      const bool wrote_ok = static_cast<bool>(out);
      out.close();
      if (!wrote_ok) {
        fail(DownloadError::IoError, "write error on the part file");
        return;
      }
    }
    const auto digest = hasher.finish();
    if (!digest) {
      fail(DownloadError::HashUnavailable, "could not finish SHA-256");
      return;
    }
    if (!std::equal(digest->begin(), digest->end(), spec.sha256.begin())) {
      std::error_code e;
      std::filesystem::remove(part, e);
      fail(DownloadError::HashMismatch, "SHA-256 of the downloaded file does not match; part file deleted");
      return;
    }
    std::error_code e;
    std::filesystem::rename(part, spec.final_path, e);  // replaces an existing target on Windows
    if (e) {
      fail(DownloadError::IoError, "rename failed: " + e.message());
      return;
    }
    const std::lock_guard lock(mutex_);
    prog_.state = DownloadState::Completed;
    prog_.bytes_done = size;
    prog_.error = DownloadError::None;
    prog_.detail.clear();
    X4MP_LOGI("save: HTTP download verified and stored as {}", spec.final_path.string());
  };

  if (existing == size) {  // a complete part file: verify without the network
    finish();
    return;
  }

  out.open(part, existing > 0 ? (std::ios::binary | std::ios::app) : (std::ios::binary | std::ios::trunc));
  if (!out) {
    fail(DownloadError::IoError, "could not open " + part.string());
    return;
  }

  std::string connect_error;
  const SOCKET s = connect_socket(spec.target.host, spec.target.port, connect_error);
  if (s == INVALID_SOCKET) {
    out.close();
    if (!stopped()) fail(DownloadError::IoError, connect_error);
    return;
  }
  {
    const std::lock_guard lock(mutex_);
    socket_ = static_cast<std::uintptr_t>(s);
  }

  std::string request = "GET " + spec.target.path + " HTTP/1.1\r\n";
  request += "Host: " + spec.target.host + (spec.target.port == 80 ? "" : ":" + std::to_string(spec.target.port)) + "\r\n";
  if (!spec.token.empty()) request += "Authorization: " + std::string(kDownloadScheme) + " " + spec.token + "\r\n";
  request += "User-Agent: X4MP\r\nAccept: */*\r\nConnection: close\r\n";
  if (existing > 0) {
    request += "Range: bytes=" + std::to_string(existing) + "-\r\n";
    request += "If-Range: \"" + crypto::to_hex(spec.sha256) + "\"\r\n";
  }
  request += "\r\n";

  auto drop_socket = [&] {
    const std::lock_guard lock(mutex_);
    if (socket_ != kNoSocket) {
      ::closesocket(static_cast<SOCKET>(socket_));
      socket_ = kNoSocket;
    }
  };

  if (!send_all(s, request)) {
    drop_socket();
    out.close();
    if (!stopped()) fail(DownloadError::IoError, "could not send the HTTP request");
    return;
  }
  {
    const std::lock_guard lock(mutex_);
    ++prog_.requests_sent;
  }

  std::string head;
  std::string body_prefix;
  {
    std::array<char, 4096> buf{};
    while (head.size() < kHeaderCap) {
      const int n = ::recv(s, buf.data(), static_cast<int>(buf.size()), 0);
      if (n <= 0) {
        drop_socket();
        out.close();
        if (!stopped()) fail(DownloadError::IoError, "the server closed the connection before the headers");
        return;
      }
      head.append(buf.data(), static_cast<std::size_t>(n));
      const std::size_t end = head.find("\r\n\r\n");
      if (end != std::string::npos) {
        body_prefix = head.substr(end + 4);
        head.resize(end + 4);
        break;
      }
    }
  }
  if (head.empty() || head.find("\r\n\r\n") == std::string::npos) {
    drop_socket();
    out.close();
    fail(DownloadError::ProtocolError, "the response headers are missing or too large");
    return;
  }

  const int status = status_code(head);
  const std::string status_text = "HTTP " + std::to_string(status);
  if (status == 401 || status == 403) {
    drop_socket();
    out.close();
    fail(DownloadError::ProtocolError, status_text + ": the download token was refused");
    return;
  }
  if (status == 404) {
    drop_socket();
    out.close();
    fail(DownloadError::ProtocolError, status_text + ": the save is not on the server");
    return;
  }
  if (status == 416) {
    drop_socket();
    if (existing == size) {
      finish();
    } else {
      out.close();
      fail(DownloadError::ProtocolError, status_text + " but the part file holds " + std::to_string(existing) +
                                                   " of " + std::to_string(size) + " bytes");
    }
    return;
  }

  std::string transfer_encoding;
  if (header_value(head, "Transfer-Encoding", transfer_encoding) && !equals_ci(transfer_encoding, "identity")) {
    drop_socket();
    out.close();
    fail(DownloadError::ProtocolError, status_text + " uses " + transfer_encoding + "; chunked bodies are not supported");
    return;
  }

  if (status == 206) {
    std::string range;
    std::uint64_t start = 0;
    if (!header_value(head, "Content-Range", range)) {
      drop_socket();
      out.close();
      fail(DownloadError::ProtocolError, status_text + " without Content-Range");
      return;
    }
    const std::size_t space = range.find(' ');
    const std::size_t dash = range.find('-');
    if (space == std::string::npos || dash == std::string::npos) {
      drop_socket();
      out.close();
      fail(DownloadError::ProtocolError, "unreadable Content-Range: " + range);
      return;
    }
    const auto parsed = std::from_chars(range.data() + space + 1, range.data() + dash, start);
    if (parsed.ec != std::errc{} || start != existing) {
      drop_socket();
      out.close();
      fail(DownloadError::ProtocolError, "Content-Range starts at " + range.substr(space + 1, dash - space - 1) +
                                               ", the part file holds " + std::to_string(existing) + " bytes");
      return;
    }
    publish(DownloadState::Receiving, DownloadError::None, std::string{});
  } else if (status == 200) {
    // The server ignored Range (a fresh save, or a validator mismatch): start over from zero.
    out.close();
    out.open(part, std::ios::binary | std::ios::trunc);
    if (!out) {
      drop_socket();
      fail(DownloadError::IoError, "could not open " + part.string());
      return;
    }
    hasher = crypto::Sha256Hasher();
    next = 0;
    {
      const std::lock_guard lock(mutex_);
      prog_.bytes_done = 0;
    }
    publish(DownloadState::Receiving, DownloadError::None, std::string{});
  } else {
    drop_socket();
    out.close();
    fail(DownloadError::ProtocolError, status_text + ": the server refused the download");
    return;
  }

  auto consume = [&](const char* data, std::size_t n) -> bool {
    if (n == 0) return true;
    if (next + n > size) {
      fail(DownloadError::ProtocolError, "the server sent more bytes than the announced size");
      return false;
    }
    out.write(data, static_cast<std::streamsize>(n));
    if (!out) {
      fail(DownloadError::IoError, "write error on the part file");
      return false;
    }
    hasher.update(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(data), n));
    next += n;
    const std::lock_guard lock(mutex_);
    prog_.state = DownloadState::Receiving;
    prog_.bytes_done = next;
    ++prog_.chunks_received;
    return true;
  };

  if (!body_prefix.empty() && !consume(body_prefix.data(), body_prefix.size())) {
    drop_socket();
    out.close();
    return;
  }

  std::array<char, kReadBuffer> buf{};
  while (next < size && !stopped()) {
    const int n = ::recv(s, buf.data(), static_cast<int>(buf.size()), 0);
    if (n == 0) break;
    if (n < 0) {
      drop_socket();
      out.close();
      fail(DownloadError::IoError, "the connection failed after " + std::to_string(next) + " of " +
                                       std::to_string(size) + " bytes");
      return;
    }
    if (!consume(buf.data(), static_cast<std::size_t>(n))) {
      drop_socket();
      out.close();
      return;
    }
  }

  drop_socket();
  if (stopped()) {
    out.close();
    return;
  }
  if (next < size) {
    out.close();
    fail(DownloadError::IoError, "the connection closed after " + std::to_string(next) + " of " +
                                     std::to_string(size) + " bytes; the part file is kept for a resume");
    return;
  }
  finish();
}

bool HttpSaveDownloader::active() const {
  const std::lock_guard lock(mutex_);
  return prog_.state == DownloadState::Requesting || prog_.state == DownloadState::Receiving;
}

DownloadProgress HttpSaveDownloader::progress() const {
  const std::lock_guard lock(mutex_);
  return prog_;
}

void HttpSaveDownloader::cancel() {
  stop_.store(true);
  {
    const std::lock_guard lock(mutex_);
    if (socket_ != kNoSocket) {
      // shutdown, not closesocket: the handle stays valid for the worker's recv, which then returns 0.
      ::shutdown(static_cast<SOCKET>(socket_), SD_BOTH);
    }
  }
  if (worker_.joinable()) worker_.join();
  stop_.store(false);
  const std::lock_guard lock(mutex_);
  if (prog_.state == DownloadState::Requesting || prog_.state == DownloadState::Receiving) {
    prog_.state = DownloadState::Idle;
  }
}

}  // namespace x4mp::session
