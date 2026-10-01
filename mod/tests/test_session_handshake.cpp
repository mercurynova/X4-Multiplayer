// core/session against an in-test fake server (M1-N3): ServerHello -> ClientHello (HMAC proof) -> Welcome, heartbeat only
// after ClientHello, resume token reuse, Control-frame retention across a resume, ClientReload + stash, halting codes,
// and the in-band save download end to end (including a cut at 50% and a SHA mismatch). No SDK, no real server.

#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/session/session.h"
#include "fake_server.h"

using namespace x4mp::session;
using namespace fake;
namespace net = x4mp::net;

namespace {

constexpr std::uint16_t kChat = 0x0500;

// player_key = A0..BF, nonce (fake server default) = 00..1F: the proof for "hunter2" is the one the C# HandshakeAuth produced.
constexpr const char* kProofHunter2 = "c3cb6b4cfbb31d47c803d4a410a467c348458e75ad7c44a4be2be59070be8914";

SessionOptions base_options(const Listener& server) {
  SessionOptions o;
  o.endpoint = server.endpoint();
  o.player_name = "Tester";
  for (std::size_t i = 0; i < o.player_key.size(); ++i) o.player_key[i] = static_cast<std::uint8_t>(0xA0 + i);
  o.net.backoff_first_ms = 80;
  o.net.backoff_cap_ms = 80;
  return o;
}

struct ListExt final : IExtensionProvider {
  ExtensionSnapshot snapshot() override {
    ExtensionSnapshot s;
    s.hash.assign(32, 0x5A);
    s.entries = {"ego_dlc_split@900", "x4mp_test@1.0"};
    return s;
  }
};

// Polls until `pred` is true, collecting all events.
struct Pump {
  Session& s;
  std::vector<SessionEvent> all;
  bool until(const std::function<bool()>& pred, std::chrono::milliseconds t = 8000ms) {
    return wait_until(
        [&] {
          s.poll(all);
          return pred();
        },
        t);
  }
  [[nodiscard]] std::size_t count(SessionEvent::Kind k) const {
    std::size_t n = 0;
    for (const auto& e : all) n += e.kind == k ? 1 : 0;
    return n;
  }
  [[nodiscard]] const SessionEvent* find(SessionEvent::Kind k) const {
    for (const auto& e : all) {
      if (e.kind == k) return &e;
    }
    return nullptr;
  }
  [[nodiscard]] bool state_seen(State st) const {
    for (const auto& e : all) {
      if (e.kind == SessionEvent::Kind::StateChanged && e.state == st) return true;
    }
    return false;
  }
};

std::string hex_of(const flatbuffers::Vector<std::uint8_t>* v) {
  if (v == nullptr) return {};
  return x4mp::crypto::to_hex(std::span<const std::uint8_t>(v->data(), v->size()));
}

std::vector<std::uint8_t> chat_payload(int i) { return {static_cast<std::uint8_t>(0xC0 + i), 7, 7, 7}; }

}  // namespace

TEST_CASE("session: handshake sends ClientHello first with the C# HMAC proof, then the heartbeat starts", "[session][handshake][interop]") {
  Listener server;
  ListExt ext;
  auto opt = base_options(server);
  opt.password = "hunter2";
  opt.extensions = &ext;
  opt.client_caps = 0x3;
  Session s(opt);
  REQUIRE(s.start());
  CHECK(s.state() == State::Connecting);

  Conn conn = server.accept();
  REQUIRE(conn.valid());
  const auto hello = do_server_hello(conn, /*password=*/true);
  REQUIRE(hello.has_value());  // ClientHello was the first frame
  const auto* h = hello->get();
  CHECK(h->protocol_major() == 0);
  CHECK(h->protocol_minor() == 1);
  CHECK(h->player_name()->str() == "Tester");
  CHECK(h->game_build()->str() == "900-611726");
  CHECK(h->platform()->str() == "win64");
  CHECK(h->client_caps() == 0x3);
  CHECK(static_cast<std::uint8_t>(h->requested_roles()) == 2);
  CHECK(h->player_key()->size() == 32);
  CHECK((*h->player_key())[0] == 0xA0);
  CHECK(hex_of(h->auth_proof()) == kProofHunter2);  // identical to C# HandshakeAuth.ComputeProof
  CHECK(h->admin_proof()->size() == 0);
  CHECK(h->resume_token()->lo() == 0);
  CHECK(h->resume_token()->hi() == 0);
  CHECK(h->extensions_hash()->size() == 32);
  REQUIRE(h->extensions()->size() == 2);
  CHECK(h->extensions()->Get(0)->str() == "ego_dlc_split@900");

  // Heartbeat pings only start after ClientHello: now they must flow.
  CHECK(conn.read_frame_of(T(MsgType::Ping), 5).has_value());

  conn.send(welcome(42, 0xAAAA, 0xBBBB, false));
  Pump p{s, {}};
  REQUIRE(p.until([&] { return p.find(SessionEvent::Kind::Welcome) != nullptr; }));
  CHECK(s.state() == State::Joining);
  CHECK(s.has_welcome());
  CHECK(s.welcome().player_id == 42);
  CHECK(s.welcome().resume_token == Id128{0xAAAA, 0xBBBB});
  CHECK_FALSE(s.welcome().resumed);
  CHECK(s.welcome().conn_id == 7);
  CHECK(s.server().server_name == "fake-server");
  CHECK(s.server().auth == 1);
  CHECK(s.server().session_id == Id128{0x1111, 0x2222});
  CHECK(p.state_seen(State::Handshaking));
  s.mark_in_session();
  CHECK(s.state() == State::InSession);
  s.stop();
  CHECK(s.state() == State::Disconnected);
}

TEST_CASE("session: no auth and an admin proof", "[session][handshake]") {
  Listener server;
  auto opt = base_options(server);
  opt.admin_password = "hunter2";  // the server's auth is None: only the admin proof is sent
  Session s(opt);
  REQUIRE(s.start());
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  const auto hello = do_server_hello(conn, /*password=*/false);
  REQUIRE(hello.has_value());
  CHECK(hello->get()->auth_proof()->size() == 0);
  CHECK(hex_of(hello->get()->admin_proof()) == kProofHunter2);
  s.stop();
}

TEST_CASE("session: a reconnect resumes with the token and replays Control frames queued while down, in order", "[session][resume][retention]") {
  Listener server;
  auto opt = base_options(server);
  opt.password = "hunter2";
  Session s(opt);
  REQUIRE(s.start());
  Pump p{s, {}};
  Conn c1 = server.accept();
  REQUIRE(do_server_hello(c1, true).has_value());
  c1.send(welcome(5, 0x1234, 0x5678, false));
  REQUIRE(p.until([&] { return s.state() == State::Joining; }));
  s.mark_in_session();

  c1.close();
  REQUIRE(p.until([&] { return s.state() == State::Reconnecting; }));
  for (int i = 0; i < 4; ++i) CHECK(s.send(Lane::Control, kChat, chat_payload(i)) == net::SendResult::Ok);
  CHECK(s.send(Lane::Realtime, 0x0300, chat_payload(9)) == net::SendResult::Ok);  // dropped, never replayed

  Conn c2 = server.accept();
  REQUIRE(c2.valid());
  const auto hello = do_server_hello(c2, true);  // fails if anything preceded ClientHello
  REQUIRE(hello.has_value());
  CHECK(hello->get()->resume_token()->lo() == 0x1234);  // the token from the first Welcome
  CHECK(hello->get()->resume_token()->hi() == 0x5678);
  CHECK(hex_of(hello->get()->auth_proof()) == kProofHunter2);
  c2.send(welcome(5, 0x1234, 0x5678, /*resumed=*/true));

  for (int i = 0; i < 4; ++i) {
    const auto f = c2.read_frame_of(kChat);
    REQUIRE(f.has_value());
    CHECK(f->payload[0] == 0xC0 + i);
  }
  REQUIRE(p.until([&] { return p.count(SessionEvent::Kind::ControlReplayed) == 1; }));
  CHECK(p.find(SessionEvent::Kind::ControlReplayed)->count == 4);
  CHECK(p.count(SessionEvent::Kind::ControlDropped) == 0);
  CHECK(s.welcome().resumed);
  CHECK(s.state() == State::InSession);  // resumed: straight back
  s.stop();
}

TEST_CASE("session: a fresh join (resume refused) drops the queued Control frames and reports it", "[session][resume][retention]") {
  Listener server;
  auto opt = base_options(server);
  Session s(opt);
  REQUIRE(s.start());
  Pump p{s, {}};
  Conn c1 = server.accept();
  REQUIRE(do_server_hello(c1, false).has_value());
  c1.send(welcome(5, 0x1234, 0x5678, false));
  REQUIRE(p.until([&] { return s.state() == State::Joining; }));
  s.mark_in_session();
  c1.close();
  REQUIRE(p.until([&] { return s.state() == State::Reconnecting; }));
  for (int i = 0; i < 3; ++i) (void)s.send(Lane::Control, kChat, chat_payload(i));

  Conn c2 = server.accept();
  REQUIRE(c2.valid());
  REQUIRE(do_server_hello(c2, false).has_value());
  c2.send(disconnect(19));  // ResumeExpired: "fresh join". (The server would then close; we just reconnect.)
  c2.close();
  Conn c3 = server.accept();
  REQUIRE(c3.valid());
  const auto hello = do_server_hello(c3, false);
  REQUIRE(hello.has_value());
  CHECK(hello->get()->resume_token()->lo() == 0);  // ResumeExpired cleared the token: a fresh join
  c3.send(welcome(6, 0x9999, 0x8888, /*resumed=*/false));

  REQUIRE(p.until([&] { return p.count(SessionEvent::Kind::ControlDropped) == 1 && s.welcome().player_id == 6; }));
  CHECK(p.find(SessionEvent::Kind::ControlDropped)->count == 3);
  CHECK(p.count(SessionEvent::Kind::ControlReplayed) == 0);
  CHECK_FALSE(s.welcome().resumed);
  CHECK(s.state() == State::Joining);  // fresh join: not back in session on its own
  // None of the old frames arrives; a new one does.
  (void)s.send(Lane::Control, kChat, chat_payload(7));
  const auto f = c3.read_frame_of(kChat);
  REQUIRE(f.has_value());
  CHECK(f->payload[0] == 0xC7);
  CHECK(p.find(SessionEvent::Kind::ServerDisconnect) != nullptr);
  s.stop();
}

TEST_CASE("session: ClientReload goodbye, stash intent, and a new Session resumes (no plain password needed)", "[session][reload]") {
  Listener server;
  MemoryStash stash;
  auto opt = base_options(server);
  opt.password = "hunter2";
  opt.stash = &stash;
  SessionIntent intent;
  {
    Session s(opt);
    REQUIRE(s.start());
    Pump p{s, {}};
    Conn c1 = server.accept();
    REQUIRE(do_server_hello(c1, true).has_value());
    c1.send(welcome(11, 0xCAFE, 0xF00D, false));
    REQUIRE(p.until([&] { return s.state() == State::Joining; }));
    s.mark_in_session();

    s.unload_for_reload();
    CHECK(s.state() == State::Disconnected);
    const auto bye = c1.read_frame_of(T(MsgType::Disconnect));
    REQUIRE(bye.has_value());
    CHECK(static_cast<std::uint16_t>(flatbuffers::GetRoot<X4MP::Proto::Disconnect>(bye->payload.data())->code()) == 6);
    const auto loaded = load_intent(stash);
    REQUIRE(loaded.has_value());
    intent = *loaded;
  }
  CHECK(intent.name == "Tester");
  CHECK(intent.resume_token == Id128{0xCAFE, 0xF00D});
  CHECK(intent.session_id == Id128{0x1111, 0x2222});
  CHECK(intent.port == server.endpoint().port);

  // "X4Native restarted the extension": a new Session from the stash, with NO password in its options.
  auto opt2 = base_options(server);
  opt2.stash = &stash;
  opt2.resume = intent;
  Session s2(opt2);
  REQUIRE(s2.start());
  Pump p2{s2, {}};
  Conn c2 = server.accept();
  REQUIRE(c2.valid());
  const auto hello = do_server_hello(c2, true);
  REQUIRE(hello.has_value());
  CHECK(hello->get()->resume_token()->lo() == 0xCAFE);
  CHECK(hello->get()->resume_token()->hi() == 0xF00D);
  CHECK(hex_of(hello->get()->auth_proof()) == kProofHunter2);  // from the stashed hash
  c2.send(welcome(11, 0xCAFE, 0xF00D, true));
  REQUIRE(p2.until([&] { return s2.has_welcome() && s2.welcome().resumed; }));
  CHECK(s2.state() == State::InSession);
  s2.stop();
  CHECK_FALSE(load_intent(stash).has_value());  // a normal leave clears the intent
}

TEST_CASE("session: stash intent round trip and garbage", "[session][reload]") {
  MemoryStash stash;
  CHECK_FALSE(load_intent(stash).has_value());
  SessionIntent in;
  in.connect = true;
  in.host = "example.org";
  in.port = 4242;
  in.name = "Zed";
  in.roles = 2;
  in.resume_token = Id128{1, 2};
  in.session_id = Id128{3, 4};
  in.last_journal_seq = 99;
  in.auth_hash_hex = std::string(64, 'a');
  REQUIRE(save_intent(stash, in));
  const auto out = load_intent(stash);
  REQUIRE(out.has_value());
  CHECK(out->host == "example.org");
  CHECK(out->port == 4242);
  CHECK(out->resume_token == Id128{1, 2});
  CHECK(out->session_id == Id128{3, 4});
  CHECK(out->last_journal_seq == 99);
  CHECK(out->auth_hash_hex == std::string(64, 'a'));
  stash.put(kStashIntentKey, "{not json");
  CHECK_FALSE(load_intent(stash).has_value());
}

TEST_CASE("session: a no-retry rejection halts and reports the server's code", "[session][handshake]") {
  Listener server;
  Session s(base_options(server));
  REQUIRE(s.start());
  Pump p{s, {}};
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  REQUIRE(do_server_hello(conn, false).has_value());
  conn.send(disconnect(12));  // GameVersionMismatch; the server closes after a Disconnect
  conn.close();
  REQUIRE(p.until([&] { return s.state() == State::Disconnected && p.find(SessionEvent::Kind::ServerDisconnect) != nullptr; }));
  CHECK(p.find(SessionEvent::Kind::ServerDisconnect)->code == 12);
  CHECK(s.net_status().state == net::ConnState::Halted);
  s.stop();
}

// ---------------------------------------------------------------------------------------------------------------------
// save download through the Session
// ---------------------------------------------------------------------------------------------------------------------
namespace {

constexpr std::size_t kChunk = 256 * 1024;

std::vector<std::uint8_t> make_data(std::size_t n) {
  std::vector<std::uint8_t> d(n);
  std::uint32_t x = 0xBEEF1234u;
  for (auto& b : d) {
    x = x * 1664525u + 1013904223u;
    b = static_cast<std::uint8_t>(x >> 24);
  }
  return d;
}

struct TempDir {
  std::filesystem::path path;
  explicit TempDir(const char* tag) {
    path = std::filesystem::temp_directory_path() / (std::string("x4mp_test_") + tag);
    std::filesystem::remove_all(path);
    std::filesystem::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    std::filesystem::remove_all(path, ec);
  }
};

// Reads the next SaveDownloadRequest and returns its offset.
std::optional<std::uint64_t> read_request(Conn& c) {
  const auto f = c.read_frame_of(T(MsgType::SaveDownloadRequest), 200);
  if (!f) return std::nullopt;
  return flatbuffers::GetRoot<X4MP::Proto::SaveDownloadRequest>(f->payload.data())->offset();
}

void send_chunks(Conn& c, std::uint32_t id, const std::vector<std::uint8_t>& data, std::size_t from, std::size_t to) {
  c.send(download_accept(id, data.size()));
  for (std::size_t off = from; off < to; off += kChunk) {
    c.send(chunk(id, off, std::span<const std::uint8_t>(data).subspan(off, std::min(kChunk, data.size() - off))));
  }
}

}  // namespace

TEST_CASE("session: save download resumes at the offset after a cut at 50% and ends with SaveReady", "[session][save][resume]") {
  TempDir dir("sess_dl");
  const auto data = make_data(kChunk * 8);
  const auto sha = *x4mp::crypto::sha256(data);
  const std::vector<std::uint8_t> sha_v(sha.begin(), sha.end());

  Listener server;
  auto opt = base_options(server);
  opt.save_dir = dir.path;
  Session s(opt);
  REQUIRE(s.start());
  Pump p{s, {}};
  Conn c1 = server.accept();
  REQUIRE(do_server_hello(c1, false).has_value());
  c1.send(welcome(3, 0x77, 0x88, false));
  c1.send(save_info(sha_v, data.size(), "x4mp_test.xml.gz"));
  REQUIRE(p.until([&] { return p.find(SessionEvent::Kind::SaveInfo) != nullptr; }));

  const auto off0 = read_request(c1);
  REQUIRE(off0.has_value());
  CHECK(*off0 == 0);
  send_chunks(c1, 1, data, 0, data.size() / 2);  // 50%, then the link dies
  REQUIRE(wait_until([&] {
    s.poll(p.all);
    return s.save_progress().bytes_done >= data.size() / 2;
  }));
  c1.close();

  REQUIRE(p.until([&] { return s.state() == State::Reconnecting; }));
  Conn c2 = server.accept();
  REQUIRE(c2.valid());
  REQUIRE(do_server_hello(c2, false).has_value());
  c2.send(welcome(3, 0x77, 0x88, true));
  const auto off1 = read_request(c2);  // the Welcome made the downloader ask again, at its contiguous offset
  REQUIRE(off1.has_value());
  CHECK(*off1 == data.size() / 2);
  CHECK((data.size() - *off1) * 100 < data.size() * 60);  // under 60% crosses the wire the second time
  send_chunks(c2, 2, data, static_cast<std::size_t>(*off1), data.size());

  REQUIRE(p.until([&] { return p.find(SessionEvent::Kind::SaveReady) != nullptr; }));
  CHECK(s.save_ready());
  const auto ready = c2.read_frame_of(T(MsgType::SaveReady));
  REQUIRE(ready.has_value());
  const auto* r = flatbuffers::GetRoot<X4MP::Proto::SaveReady>(ready->payload.data());
  CHECK(hex_of(r->sha256()) == x4mp::crypto::to_hex(sha));
  const auto final_path = dir.path / "x4mp_test.xml.gz";
  REQUIRE(std::filesystem::exists(final_path));
  std::ifstream in(final_path, std::ios::binary);
  const std::vector<std::uint8_t> got((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
  CHECK(got == data);
  CHECK(p.count(SessionEvent::Kind::SaveFailed) == 0);
  s.stop();
}

TEST_CASE("session: a save whose bytes do not match the announced SHA-256 is rejected", "[session][save]") {
  TempDir dir("sess_badsha");
  const auto data = make_data(kChunk * 2 + 9);
  auto sha = *x4mp::crypto::sha256(data);
  sha[3] ^= 0x55;  // announce the wrong hash
  const std::vector<std::uint8_t> sha_v(sha.begin(), sha.end());

  Listener server;
  auto opt = base_options(server);
  opt.save_dir = dir.path;
  Session s(opt);
  REQUIRE(s.start());
  Pump p{s, {}};
  Conn c1 = server.accept();
  REQUIRE(do_server_hello(c1, false).has_value());
  c1.send(welcome(3, 0x77, 0x88, false));
  c1.send(save_info(sha_v, data.size(), "x4mp_bad.xml.gz"));
  REQUIRE(p.until([&] { return p.find(SessionEvent::Kind::SaveInfo) != nullptr; }));
  REQUIRE(read_request(c1).has_value());
  send_chunks(c1, 1, data, 0, data.size());

  REQUIRE(p.until([&] { return p.find(SessionEvent::Kind::SaveFailed) != nullptr; }));
  CHECK(p.find(SessionEvent::Kind::SaveFailed)->progress.error == DownloadError::HashMismatch);
  CHECK_FALSE(s.save_ready());
  CHECK(p.count(SessionEvent::Kind::SaveReady) == 0);
  CHECK_FALSE(std::filesystem::exists(dir.path / "x4mp_bad.xml.gz"));
  // The failure is reported to the server (LoadStatus Failed / SaveChecksumMismatch), and no SaveReady is sent.
  const auto ls = c1.read_frame_of(T(MsgType::LoadStatus));
  REQUIRE(ls.has_value());
  const auto* l = flatbuffers::GetRoot<X4MP::Proto::LoadStatus>(ls->payload.data());
  CHECK(l->phase() == X4MP::Proto::NodePhase::Failed);
  CHECK(static_cast<std::uint16_t>(l->error()) == 50);
  s.stop();
}

TEST_CASE("session: an existing, matching save is not downloaded again", "[session][save]") {
  TempDir dir("sess_cached");
  const auto data = make_data(kChunk + 1);
  const auto sha = *x4mp::crypto::sha256(data);
  const std::vector<std::uint8_t> sha_v(sha.begin(), sha.end());
  {
    std::ofstream out(dir.path / "x4mp_cached.xml.gz", std::ios::binary);
    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
  }
  Listener server;
  auto opt = base_options(server);
  opt.save_dir = dir.path;
  Session s(opt);
  REQUIRE(s.start());
  Pump p{s, {}};
  Conn c1 = server.accept();
  REQUIRE(do_server_hello(c1, false).has_value());
  c1.send(welcome(3, 1, 2, false));
  c1.send(save_info(sha_v, data.size(), "x4mp_cached.xml.gz"));
  REQUIRE(p.until([&] { return p.find(SessionEvent::Kind::SaveReady) != nullptr; }));
  const auto ready = c1.read_frame_of(T(MsgType::SaveReady));
  CHECK(ready.has_value());
  s.stop();
}
