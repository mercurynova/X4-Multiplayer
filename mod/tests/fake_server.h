#pragma once
// Test-only loopback "server" for the session tests (M1-N3): a blocking Winsock listener + connection, frame builders
// for the messages a real X4MP server sends (ServerHello, Welcome, SaveDownloadAccept, SaveChunk, ...), and a helper that
// plays the server half of the handshake (nonce -> ClientHello -> proof check -> Welcome). No X4 SDK, no real server.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <chrono>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "control_generated.h"
#include "core/crypto/crypto.h"
#include "core/net/net.h"
#include "message_ids_generated.h"
#include "session_generated.h"
#include "x4mp/wire.h"

namespace fake {

using x4mp::wire::Lane;
using X4MP::Proto::MsgType;
using namespace std::chrono_literals;

inline std::uint16_t T(MsgType t) { return static_cast<std::uint16_t>(t); }

struct WsaInit {
  WsaInit() {
    WSADATA d{};
    ::WSAStartup(MAKEWORD(2, 2), &d);
  }
  ~WsaInit() { ::WSACleanup(); }
};
inline const WsaInit g_wsa;

inline bool wait_until(const std::function<bool()>& pred, std::chrono::milliseconds timeout = 8000ms) {
  const auto end = std::chrono::steady_clock::now() + timeout;
  while (std::chrono::steady_clock::now() < end) {
    if (pred()) return true;
    std::this_thread::sleep_for(2ms);
  }
  return pred();
}

inline std::vector<std::uint8_t> to_vec(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

inline std::vector<std::uint8_t> frame(std::uint16_t type, Lane lane, const std::vector<std::uint8_t>& payload) {
  std::vector<std::uint8_t> out(x4mp::wire::kFrameHeaderSize + payload.size());
  (void)x4mp::wire::encode_frame(out, type, lane, payload);
  return out;
}

inline std::vector<std::uint8_t> server_hello(const std::vector<std::uint8_t>& nonce, bool password, std::uint8_t phase = 0) {
  flatbuffers::FlatBufferBuilder fbb;
  const X4MP::Proto::Id128 session_id(0x1111, 0x2222);
  const auto nonce_v = fbb.CreateVector(nonce);
  const auto name = fbb.CreateString("fake-server");
  const auto ver = fbb.CreateString("0.0.0-test");
  const auto build = fbb.CreateString("900-611726");
  X4MP::Proto::ServerHelloBuilder b(fbb);
  b.add_protocol_major(0);
  b.add_protocol_minor(1);
  b.add_server_version(ver);
  b.add_server_name(name);
  b.add_session_id(&session_id);
  b.add_nonce(nonce_v);
  b.add_auth(password ? X4MP::Proto::AuthMethod::SessionPassword : X4MP::Proto::AuthMethod::None);
  b.add_phase(static_cast<X4MP::Proto::SessionPhase>(phase));
  b.add_required_game_build(build);
  fbb.Finish(b.Finish());
  return frame(T(MsgType::ServerHello), Lane::Control, to_vec(fbb));
}

inline std::vector<std::uint8_t> welcome(std::uint16_t player_id, std::uint64_t token_lo, std::uint64_t token_hi, bool resumed) {
  flatbuffers::FlatBufferBuilder fbb;
  const X4MP::Proto::Id128 token(token_lo, token_hi);
  fbb.Finish(X4MP::Proto::CreateWelcome(fbb, player_id, X4MP::Proto::Role::Client, 0x3, &token, resumed, 7));
  return frame(T(MsgType::Welcome), Lane::Control, to_vec(fbb));
}

inline std::vector<std::uint8_t> disconnect(std::uint16_t code, std::uint32_t retry_ms = 0) {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(X4MP::Proto::CreateDisconnectDirect(fbb, static_cast<X4MP::Proto::DisconnectCode>(code), "test", "", retry_ms));
  return frame(T(MsgType::Disconnect), Lane::Control, to_vec(fbb));
}

inline std::vector<std::uint8_t> save_info(const std::vector<std::uint8_t>& sha, std::uint64_t size, const std::string& file) {
  flatbuffers::FlatBufferBuilder fbb;
  const X4MP::Proto::Id128 ck(5, 6);
  const auto sha_v = fbb.CreateVector(sha);
  const auto name = fbb.CreateString("test save");
  const auto fn = fbb.CreateString(file);
  X4MP::Proto::SessionSaveInfoBuilder b(fbb);
  b.add_checkpoint_id(&ck);
  b.add_sha256(sha_v);
  b.add_size(size);
  b.add_display_name(name);
  b.add_local_file_name(fn);
  fbb.Finish(b.Finish());
  return frame(T(MsgType::SessionSaveInfo), Lane::Control, to_vec(fbb));
}

inline std::vector<std::uint8_t> download_accept(std::uint32_t id, std::uint64_t size) {
  flatbuffers::FlatBufferBuilder fbb;
  fbb.Finish(X4MP::Proto::CreateSaveDownloadAccept(fbb, id, size));
  return frame(T(MsgType::SaveDownloadAccept), Lane::Control, to_vec(fbb));
}

inline std::vector<std::uint8_t> chunk(std::uint32_t id, std::uint64_t offset, std::span<const std::uint8_t> data) {
  flatbuffers::FlatBufferBuilder fbb(data.size() + 64);
  const auto d = fbb.CreateVector(data.data(), data.size());
  fbb.Finish(X4MP::Proto::CreateSaveChunk(fbb, id, offset, d));
  return frame(T(MsgType::SaveChunk), Lane::Bulk, to_vec(fbb));
}

// One accepted connection. Blocking with a receive timeout.
class Conn {
 public:
  explicit Conn(SOCKET s = INVALID_SOCKET) : s_(s) {
    if (s_ != INVALID_SOCKET) {
      const DWORD ms = 3000;
      ::setsockopt(s_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&ms), sizeof(ms));
      const BOOL nd = TRUE;
      ::setsockopt(s_, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&nd), sizeof(nd));
    }
  }
  Conn(Conn&& o) noexcept : s_(o.s_), buf_(std::move(o.buf_)) { o.s_ = INVALID_SOCKET; }
  Conn& operator=(Conn&& o) noexcept {
    close();
    s_ = o.s_;
    buf_ = std::move(o.buf_);
    o.s_ = INVALID_SOCKET;
    return *this;
  }
  Conn(const Conn&) = delete;
  Conn& operator=(const Conn&) = delete;
  ~Conn() { close(); }
  void close() {
    if (s_ != INVALID_SOCKET) ::closesocket(s_);
    s_ = INVALID_SOCKET;
  }
  [[nodiscard]] bool valid() const { return s_ != INVALID_SOCKET; }

  void send(const std::vector<std::uint8_t>& bytes) {
    std::size_t off = 0;
    while (off < bytes.size()) {
      const int n = ::send(s_, reinterpret_cast<const char*>(bytes.data() + off), static_cast<int>(bytes.size() - off), 0);
      if (n <= 0) return;
      off += static_cast<std::size_t>(n);
    }
  }

  struct Frame {
    std::uint16_t type = 0;
    Lane lane = Lane::Control;
    std::vector<std::uint8_t> payload;
  };

  std::optional<Frame> read_frame() {
    for (;;) {
      const auto d = x4mp::wire::try_decode_frame(buf_);
      if (d && d->has_value()) {
        Frame f;
        f.type = (*d)->header.type;
        f.lane = (*d)->header.lane;
        f.payload.assign((*d)->payload.begin(), (*d)->payload.end());
        buf_.erase(buf_.begin(), buf_.begin() + static_cast<std::ptrdiff_t>((*d)->consumed));
        return f;
      }
      if (!d) return std::nullopt;
      std::uint8_t tmp[8192];
      const int n = ::recv(s_, reinterpret_cast<char*>(tmp), sizeof(tmp), 0);
      if (n <= 0) return std::nullopt;
      buf_.insert(buf_.end(), tmp, tmp + n);
    }
  }

  // Reads frames until one of `type` shows up (skipping e.g. heartbeat Pings, answering none).
  std::optional<Frame> read_frame_of(std::uint16_t type, int max_skipped = 50) {
    for (int i = 0; i < max_skipped; ++i) {
      auto f = read_frame();
      if (!f) return std::nullopt;
      if (f->type == type) return f;
    }
    return std::nullopt;
  }

 private:
  SOCKET s_;
  std::vector<std::uint8_t> buf_;
};

class Listener {
 public:
  Listener() {
    s_ = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = 0;
    ::bind(s_, reinterpret_cast<sockaddr*>(&a), sizeof(a));
    ::listen(s_, 8);
    int len = sizeof(a);
    ::getsockname(s_, reinterpret_cast<sockaddr*>(&a), &len);
    port_ = ntohs(a.sin_port);
  }
  ~Listener() {
    if (s_ != INVALID_SOCKET) ::closesocket(s_);
  }
  Listener(const Listener&) = delete;
  Listener& operator=(const Listener&) = delete;
  [[nodiscard]] x4mp::net::Endpoint endpoint() const { return x4mp::net::Endpoint{"127.0.0.1", port_}; }
  Conn accept(std::chrono::milliseconds timeout = 8000ms) {
    WSAPOLLFD p{};
    p.fd = s_;
    p.events = POLLRDNORM;
    if (::WSAPoll(&p, 1, static_cast<INT>(timeout.count())) <= 0) return Conn{};
    return Conn(::accept(s_, nullptr, nullptr));
  }

 private:
  SOCKET s_ = INVALID_SOCKET;
  std::uint16_t port_ = 0;
};

// Owns a decoded ClientHello buffer.
struct Hello {
  std::vector<std::uint8_t> buf;
  [[nodiscard]] const X4MP::Proto::ClientHello* get() const { return flatbuffers::GetRoot<X4MP::Proto::ClientHello>(buf.data()); }
};

inline std::vector<std::uint8_t> test_nonce() {
  std::vector<std::uint8_t> n(32);
  for (std::size_t i = 0; i < n.size(); ++i) n[i] = static_cast<std::uint8_t>(i);
  return n;
}

// Plays the server half up to ClientHello: sends ServerHello, returns the ClientHello (nullopt on timeout/other).
inline std::optional<Hello> do_server_hello(Conn& c, bool password, const std::vector<std::uint8_t>& nonce = test_nonce()) {
  c.send(server_hello(nonce, password));
  auto f = c.read_frame();  // the FIRST frame after ServerHello must be ClientHello (the server rejects anything else)
  if (!f || f->type != T(MsgType::ClientHello)) return std::nullopt;
  return Hello{std::move(f->payload)};
}

}  // namespace fake
