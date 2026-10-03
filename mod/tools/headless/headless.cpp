// x4mp-headless: the mod's session layer without X4 (M1-N3). Joins a server with name / password / key, prints the
// Welcome, pings, optionally does a planned ClientReload + resume, and downloads the session save if one is offered.
//
//   x4mp-headless [--host H] [--port P] --name N [--password PW] [--admin-password PW]
//                 [--key HEX64 | --key-file FILE] [--save-dir DIR] [--ping SECONDS] [--reload]
//                 [--game-build B] [--mod-version V] [--ext id@version]... [--save-wait SECONDS] [--timeout SECONDS]
//   x4mp-headless --from-env ...       reads X4MP_TEST_SERVER=host:port; exit 77 (ctest "skipped") if unset
//   x4mp-headless --authority --save-file FILE [--roles N] [--work-dir DIR] [--step-timeout SECONDS] [--ping HOLD_SECONDS] ...
//       (M2-08) joins as the authority (roles default Authority|Client), reports ready, answers RequestSave by uploading FILE
//       (a gzip whose root element is <savegame>) plus an empty-station manifest, then sends one self-spawn EntitySpawn with a
//       nonzero game_time and stays HOLD_SECONDS (--ping) before leaving. Survives a dropped connection: the upload resumes.
//
// UDP realtime lane (M3-03, core/net/udp_lane.h): --udp auto|force|off (default auto; force = Realtime frames never use TCP, no
// fallback; off = capability not advertised), --udp-loss PCT (drop that share of datagrams in each direction), --udp-block (drop
// all of them from the start: a firewall), --udp-block-after SECONDS (the firewall appears mid-session; the fall-back time to TCP is
// measured and must be <= 3.5 s), --udp-keepalive-ms N (send a UdpHello every N ms when idle: a steady probe stream for loss tests),
// --udp-seed N, --expect-udp active|fallback|off (exit 1 unless the lane is in that state at the end of the --ping window and the
// TCP connection never dropped). Prints a `udp:` line every second while pinging.
//
// --key-file reads the 32-byte player key as hex, or creates it (random) if the file does not exist.
// --reload: after joining, unload_for_reload() (Disconnect ClientReload, intent -> in-memory stash), then a NEW Session is
// built from the stash exactly as main.cpp will after X4Native restarts the extension, and the Welcome must say
// resumed=true with the same player id. Exit codes: 0 ok, 1 failure/timeout, 2 bad arguments, 77 skipped.
// (Env vars are fine here: this is a test tool, not the mod runtime.)

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#include "core/crypto/crypto.h"
#include "authority_driver.h"
#include "core/session/session.h"

namespace {
using namespace std::chrono_literals;
using namespace x4mp;
using session::Session;
using session::SessionEvent;
using Clock = std::chrono::steady_clock;

struct Args {
  std::string host = "127.0.0.1";
  std::uint16_t port = 47780;
  std::string name = "headless";
  std::string password;
  std::string admin_password;
  std::string key_hex;
  std::string key_file;
  std::string save_dir;
  std::string game_build = "900-611726";
  std::string mod_version = "0.1.0";
  std::vector<std::string> extensions;
  int ping_seconds = 3;
  int save_wait_seconds = 3;
  int timeout_seconds = 20;
  bool reload = false;
  bool authority = false;
  std::string save_file;
  std::string work_dir;
  int roles = 0;  // 0 = default for the mode (Client; Authority|Client with --authority)
  int step_timeout_seconds = 60;
  std::string udp_mode = "auto";
  double udp_loss = 0.0;
  bool udp_block = false;
  int udp_block_after = -1;
  int udp_keepalive_ms = 0;
  std::uint64_t udp_seed = 0;
  std::string expect_udp;
};

class StaticExtensions final : public session::IExtensionProvider {
 public:
  explicit StaticExtensions(std::vector<std::string> entries) : entries_(std::move(entries)) {}
  session::ExtensionSnapshot snapshot() override {
    session::ExtensionSnapshot s;
    auto sorted = entries_;
    std::sort(sorted.begin(), sorted.end());
    std::string joined;
    for (const auto& e : sorted) joined += e + "\n";
    if (!sorted.empty()) {
      if (const auto d = crypto::sha256(std::span<const std::uint8_t>(reinterpret_cast<const std::uint8_t*>(joined.data()), joined.size()))) {
        s.hash.assign(d->begin(), d->end());
      }
    }
    s.entries = sorted;
    return s;
  }

 private:
  std::vector<std::string> entries_;
};

const char* kind_name(SessionEvent::Kind k) {
  using K = SessionEvent::Kind;
  switch (k) {
    case K::StateChanged: return "StateChanged";
    case K::ServerHello: return "ServerHello";
    case K::Welcome: return "Welcome";
    case K::ServerDisconnect: return "ServerDisconnect";
    case K::ControlReplayed: return "ControlReplayed";
    case K::ControlDropped: return "ControlDropped";
    case K::SaveInfo: return "SaveInfo";
    case K::SaveProgress: return "SaveProgress";
    case K::SaveReady: return "SaveReady";
    case K::SaveFailed: return "SaveFailed";
    case K::Frame: return "Frame";
    case K::NetDisconnected: return "NetDisconnected";
  }
  return "?";
}

std::string idhex(const session::Id128& id) {
  char buf[40];
  std::snprintf(buf, sizeof(buf), "%016llx%016llx", static_cast<unsigned long long>(id.hi), static_cast<unsigned long long>(id.lo));
  return buf;
}

void print_welcome(const session::WelcomeInfo& w) {
  std::printf(
      "Welcome: player_id=%u roles=0x%02x caps=0x%llx resumed=%s conn_id=%u udp_port=%u resume_token=%s "
      "grace=%us hb=%u/%ums team_id=%u team_role=%u faction_slot=%u max_ghosts=%u server_time_us=%llu\n",
      static_cast<unsigned>(w.player_id), static_cast<unsigned>(w.granted_roles), static_cast<unsigned long long>(w.negotiated_caps),
      w.resumed ? "true" : "false", static_cast<unsigned>(w.conn_id), static_cast<unsigned>(w.udp_port), idhex(w.resume_token).c_str(),
      static_cast<unsigned>(w.resume_grace_s), static_cast<unsigned>(w.heartbeat_interval_ms), static_cast<unsigned>(w.heartbeat_timeout_ms),
      static_cast<unsigned>(w.team_id), static_cast<unsigned>(w.team_role), static_cast<unsigned>(w.faction_slot), w.max_ghosts,
      static_cast<unsigned long long>(w.server_time_us));
}

// Prints one session event.
void print_event(Session& s, const SessionEvent& e) {
  switch (e.kind) {
    case SessionEvent::Kind::StateChanged:
      std::printf("state: %s -> %s\n", session::to_string(e.prev), session::to_string(e.state));
      break;
    case SessionEvent::Kind::ServerHello:
      std::printf("ServerHello: %s (%s) protocol %u.%u auth=%u phase=%u required_game_build=%s\n", s.server().server_name.c_str(),
                  s.server().server_version.c_str(), static_cast<unsigned>(s.server().protocol_major),
                  static_cast<unsigned>(s.server().protocol_minor), static_cast<unsigned>(s.server().auth),
                  static_cast<unsigned>(s.server().phase), s.server().required_game_build.c_str());
      break;
    case SessionEvent::Kind::Welcome: print_welcome(s.welcome()); break;
    case SessionEvent::Kind::ServerDisconnect:
      std::printf("server Disconnect: code=%u message='%s' expected='%s' retry_after_ms=%u\n", static_cast<unsigned>(e.code),
                  e.text.c_str(), e.expected.c_str(), e.retry_after_ms);
      break;
    case SessionEvent::Kind::ControlReplayed:
      std::printf("resume: replayed %llu queued Control frames\n", static_cast<unsigned long long>(e.count));
      break;
    case SessionEvent::Kind::ControlDropped:
      std::printf("fresh join: dropped %llu queued Control frames\n", static_cast<unsigned long long>(e.count));
      break;
    case SessionEvent::Kind::SaveInfo:
      std::printf("SaveInfo: %s size=%llu file=%s manifest=%llu bytes\n", e.save.display_name.c_str(),
                  static_cast<unsigned long long>(e.save.size), e.save.local_file_name.c_str(),
                  static_cast<unsigned long long>(e.save.manifest_size));
      break;
    case SessionEvent::Kind::SaveProgress:
      std::printf("save: %llu/%llu bytes (%.0f%%)\n", static_cast<unsigned long long>(e.progress.bytes_done),
                  static_cast<unsigned long long>(e.progress.size), 100.0 * e.progress.fraction());
      break;
    case SessionEvent::Kind::SaveReady: std::puts("save: verified, SaveReady sent"); break;
    case SessionEvent::Kind::SaveFailed: std::printf("save: FAILED %s\n", e.text.c_str()); break;
    case SessionEvent::Kind::NetDisconnected: std::printf("net: disconnected (%s)\n", e.text.c_str()); break;
    case SessionEvent::Kind::Frame:
      std::printf("frame type=0x%04X lane=%u (%zu bytes)\n", static_cast<unsigned>(e.type), static_cast<unsigned>(e.lane), e.payload.size());
      break;
  }
}

// Runs one Session until `done` returns true or the deadline passes. Prints events as they happen.
template <class Done>
bool pump(Session& s, std::vector<SessionEvent>& all, Clock::time_point deadline, Done&& done) {
  while (Clock::now() < deadline) {
    std::vector<SessionEvent> ev;
    s.poll(ev);
    for (auto& e : ev) {
      print_event(s, e);
      all.push_back(std::move(e));
    }
    if (done()) return true;
    std::this_thread::sleep_for(10ms);
  }
  return done();
}

// --authority: see the header comment of this file and headless/authority_driver.h.
int run_authority(const session::SessionOptions& opt, const Args& a, Clock::time_point overall) {
  if (a.save_file.empty()) {
    std::fprintf(stderr, "--authority needs --save-file FILE\n");
    return 2;
  }
  Session s(opt);
  if (!s.start()) {
    std::fprintf(stderr, "failed to start the session\n");
    return 1;
  }
  headless::AuthorityDriverOptions dopt;
  dopt.save_file = a.save_file;
  dopt.work_dir = a.work_dir;
  dopt.step_timeout = std::chrono::seconds(a.step_timeout_seconds);
  dopt.log = [](std::string_view line) { std::printf("%.*s\n", static_cast<int>(line.size()), line.data()); };
  dopt.on_event = [&s](const SessionEvent& e) { print_event(s, e); };
  headless::AuthorityDriver drv(s, std::move(dopt));
  while (Clock::now() < overall && !drv.done() && !drv.failed()) {
    drv.step();
    std::this_thread::sleep_for(10ms);
  }
  int rc = 0;
  if (!drv.done()) {
    std::fprintf(stderr, "authority: did not finish (%s)\n", drv.failed() ? drv.failure().c_str() : "timeout");
    rc = 1;
  } else {
    const auto hold_end = std::min(overall, Clock::now() + std::chrono::seconds(a.ping_seconds));
    while (Clock::now() < hold_end && !drv.failed()) {
      drv.step();
      std::this_thread::sleep_for(10ms);
    }
    if (drv.failed()) rc = 1;
  }
  const auto st = drv.upload_stats();
  std::printf("authority: generations=%llu jobs=%llu cancelled=%llu resumed_jobs=%llu stored=%llu cross_generation_reads=%llu stale_writes_blocked=%llu\n",
              static_cast<unsigned long long>(st.generations), static_cast<unsigned long long>(st.jobs_started),
              static_cast<unsigned long long>(st.jobs_cancelled), static_cast<unsigned long long>(st.resumed_jobs),
              static_cast<unsigned long long>(st.checkpoints_stored), static_cast<unsigned long long>(st.cross_generation_reads),
              static_cast<unsigned long long>(st.stale_writes_blocked));
  if (st.cross_generation_reads != 0 || st.stale_writes_blocked != 0) rc = 1;
  s.stop();
  return rc;
}

bool has(const std::vector<SessionEvent>& v, SessionEvent::Kind k) {
  return std::any_of(v.begin(), v.end(), [k](const SessionEvent& e) { return e.kind == k; });
}

bool load_key(const Args& a, std::array<std::uint8_t, 32>& key) {
  std::string hex = a.key_hex;
  if (hex.empty() && !a.key_file.empty()) {
    std::ifstream in(a.key_file);
    if (in) std::getline(in, hex);
  }
  if (!hex.empty()) {
    std::vector<std::uint8_t> b;
    if (!crypto::from_hex(hex, b) || b.size() != 32) {
      std::fprintf(stderr, "player key must be 64 hex chars\n");
      return false;
    }
    std::copy(b.begin(), b.end(), key.begin());
    return true;
  }
  if (!crypto::random_bytes(key)) {
    std::fprintf(stderr, "no random source\n");
    return false;
  }
  if (!a.key_file.empty()) {
    std::ofstream out(a.key_file);
    out << crypto::to_hex(key) << "\n";
  }
  return true;
}

}  // namespace

int main(int argc, char** argv) {
  Args a;
  bool from_env = false;
  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];
    const auto next = [&]() -> std::string { return i + 1 < argc ? std::string(argv[++i]) : std::string(); };
    if (arg == "--from-env") from_env = true;
    else if (arg == "--host") a.host = next();
    else if (arg == "--port") a.port = static_cast<std::uint16_t>(std::atoi(next().c_str()));
    else if (arg == "--name") a.name = next();
    else if (arg == "--password") a.password = next();
    else if (arg == "--admin-password") a.admin_password = next();
    else if (arg == "--key") a.key_hex = next();
    else if (arg == "--key-file") a.key_file = next();
    else if (arg == "--save-dir") a.save_dir = next();
    else if (arg == "--game-build") a.game_build = next();
    else if (arg == "--mod-version") a.mod_version = next();
    else if (arg == "--ext") a.extensions.push_back(next());
    else if (arg == "--ping") a.ping_seconds = std::atoi(next().c_str());
    else if (arg == "--save-wait") a.save_wait_seconds = std::atoi(next().c_str());
    else if (arg == "--timeout") a.timeout_seconds = std::atoi(next().c_str());
    else if (arg == "--reload") a.reload = true;
    else if (arg == "--authority") a.authority = true;
    else if (arg == "--save-file") a.save_file = next();
    else if (arg == "--work-dir") a.work_dir = next();
    else if (arg == "--roles") a.roles = std::atoi(next().c_str());
    else if (arg == "--step-timeout") a.step_timeout_seconds = std::atoi(next().c_str());
    else if (arg == "--udp") a.udp_mode = next();
    else if (arg == "--udp-loss") a.udp_loss = std::atof(next().c_str());
    else if (arg == "--udp-block") a.udp_block = true;
    else if (arg == "--udp-block-after") a.udp_block_after = std::atoi(next().c_str());
    else if (arg == "--udp-keepalive-ms") a.udp_keepalive_ms = std::atoi(next().c_str());
    else if (arg == "--udp-seed") a.udp_seed = static_cast<std::uint64_t>(std::strtoull(next().c_str(), nullptr, 10));
    else if (arg == "--expect-udp") a.expect_udp = next();
    else {
      std::fprintf(stderr, "unknown argument %s (see the header of headless.cpp)\n", arg.c_str());
      return 2;
    }
  }
  if (from_env) {
    char* v = nullptr;
    std::size_t vlen = 0;
    if (_dupenv_s(&v, &vlen, "X4MP_TEST_SERVER") != 0 || v == nullptr || *v == '\0') {
      std::free(v);
      std::puts("X4MP_TEST_SERVER not set: skipping");
      return 77;
    }
    const std::string s = v;
    std::free(v);
    const auto colon = s.rfind(':');
    if (colon == std::string::npos) {
      a.host = s;
    } else {
      a.host = s.substr(0, colon);
      a.port = static_cast<std::uint16_t>(std::atoi(s.c_str() + colon + 1));
    }
  }

  session::SessionOptions opt;
  opt.endpoint = net::Endpoint{a.host, a.port};
  opt.player_name = a.name;
  if (!a.password.empty()) opt.password = a.password;
  if (!a.admin_password.empty()) opt.admin_password = a.admin_password;
  opt.identity.game_build = a.game_build;
  opt.identity.mod_version = a.mod_version;
  opt.save_dir = a.save_dir;
  opt.net.backoff_first_ms = 500;
  const auto udp_mode = net::parse_udp_mode(a.udp_mode);
  if (!udp_mode || (!a.expect_udp.empty() && a.expect_udp != "active" && a.expect_udp != "fallback" && a.expect_udp != "off")) {
    std::fprintf(stderr, "--udp must be auto|force|off and --expect-udp active|fallback|off\n");
    return 2;
  }
  opt.net.udp.mode = *udp_mode;
  opt.net.udp.loss_pct = a.udp_loss;
  opt.net.udp.block = a.udp_block;
  opt.net.udp.seed = a.udp_seed;
  if (a.udp_keepalive_ms > 0) opt.net.udp.keepalive_ms = a.udp_keepalive_ms;
  if (a.authority) {
    opt.requested_roles = static_cast<std::uint8_t>(a.roles != 0 ? a.roles : 3);  // Authority | Client (ADR: mod hosts as both)
    opt.auto_download = false;
  } else if (a.roles != 0) {
    opt.requested_roles = static_cast<std::uint8_t>(a.roles);
  }
  if (!load_key(a, opt.player_key)) return 2;
  StaticExtensions exts(a.extensions);
  opt.extensions = &exts;
  session::MemoryStash stash;
  opt.stash = &stash;

  std::printf("joining %s:%u as '%s' (player key %s...)\n", a.host.c_str(), static_cast<unsigned>(a.port), a.name.c_str(),
              crypto::to_hex(std::span<const std::uint8_t>(opt.player_key).first(4)).c_str());
  const auto overall = Clock::now() + std::chrono::seconds(a.timeout_seconds);

  if (a.authority) {
    const int arc = run_authority(opt, a, overall);
    std::puts(arc == 0 ? "OK" : "FAILED");
    return arc;
  }

  std::vector<SessionEvent> events;
  int rc = 0;
  {
    Session s(opt);
    if (!s.start()) {
      std::fprintf(stderr, "failed to start the session\n");
      return 1;
    }
    const bool welcomed = pump(s, events, overall, [&] { return has(events, SessionEvent::Kind::Welcome) || s.state() == session::State::Disconnected; });
    if (!welcomed || !s.has_welcome()) {
      std::fprintf(stderr, "no Welcome (state=%s)\n", session::to_string(s.state()));
      return 1;
    }
    const std::uint16_t player_id = s.welcome().player_id;

    // Wait briefly for a SessionSaveInfo / download.
    if (!a.save_dir.empty()) {
      (void)pump(s, events, std::min(overall, Clock::now() + std::chrono::seconds(a.save_wait_seconds)),
                 [&] { return has(events, SessionEvent::Kind::SaveReady) || has(events, SessionEvent::Kind::SaveFailed); });
      if (has(events, SessionEvent::Kind::SaveFailed)) rc = 1;
      if (!has(events, SessionEvent::Kind::SaveInfo)) std::puts("no save offered by the server");
    }
    s.mark_in_session();

    // Ping for a while: the net layer's own heartbeat measures RTT.
    const auto ping_start = Clock::now();
    const auto ping_end = ping_start + std::chrono::seconds(a.ping_seconds);
    const std::uint32_t connections0 = s.net_status().connections;
    std::int64_t last_print = -1;
    bool blocked_now = false;
    Clock::time_point t_block{};
    std::int64_t fallback_ms = -1;
    pump(s, events, ping_end, [&] {
      const auto st = s.net_status();
      const auto sec = std::chrono::duration_cast<std::chrono::seconds>(ping_end - Clock::now()).count();
      if (a.udp_block_after >= 0 && !blocked_now && Clock::now() - ping_start >= std::chrono::seconds(a.udp_block_after)) {
        blocked_now = true;
        t_block = Clock::now();
        s.set_udp_block(true);
        std::printf("udp: BLOCKED now (state=%s)\n", net::to_string(st.udp.state));
      }
      if (blocked_now && fallback_ms < 0 && st.udp.state == net::UdpState::Fallback) {
        fallback_ms = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - t_block).count();
        std::printf("udp: fell back to TCP %lld ms after the block\n", static_cast<long long>(fallback_ms));
      }
      if (sec != last_print) {
        last_print = sec;
        if (st.rtt_us > 0) {
          std::printf("ping: rtt=%lldus min=%lldus clock_offset=%lldus frames_in=%llu\n", static_cast<long long>(st.rtt_us),
                      static_cast<long long>(st.rtt_min_us), static_cast<long long>(st.clock_offset_us),
                      static_cast<unsigned long long>(st.frames_in));
        }
        const auto& u = st.udp;
        std::printf("udp: state=%s out=%llu in=%llu sim_drops=%llu/%llu acked_seq=%u rx_loss=%.1f%% binds=%u fallbacks=%u reprobes=%u\n",
                    net::to_string(u.state), static_cast<unsigned long long>(u.datagrams_out),
                    static_cast<unsigned long long>(u.datagrams_in), static_cast<unsigned long long>(u.sim_drops_tx),
                    static_cast<unsigned long long>(u.sim_drops_rx), u.acked_seq, static_cast<double>(u.rx_loss_pct), u.binds,
                    u.fallbacks, u.reprobes);
      }
      return false;
    });
    const auto st = s.net_status();
    if (!a.expect_udp.empty()) {
      const char* got = net::to_string(st.udp.state);
      const bool state_ok = (a.expect_udp == "active" && st.udp.state == net::UdpState::Active) ||
                            (a.expect_udp == "fallback" && st.udp.state == net::UdpState::Fallback) ||
                            (a.expect_udp == "off" && st.udp.state == net::UdpState::Off);
      const bool conn_ok =
          st.state == net::ConnState::Connected && st.connections == connections0 && s.state() != session::State::Disconnected;
      std::printf("udp: expect %s, got %s; tcp connection %s (connections %u -> %u)\n", a.expect_udp.c_str(), got,
                  conn_ok ? "stayed up" : "DROPPED", connections0, st.connections);
      if (!state_ok || !conn_ok) rc = 1;
      if (a.udp_block_after >= 0 && (fallback_ms < 0 || fallback_ms > 3500)) {
        std::printf("udp: fallback after the block took %lld ms (need <= 3500)\n", static_cast<long long>(fallback_ms));
        rc = 1;
      }
    }
    if (st.rtt_us == 0 && a.ping_seconds > 0) {
      std::fprintf(stderr, "no Pong received\n");
      rc = 1;
    }

    if (a.reload) {
      std::puts("--- planned unload: Disconnect(ClientReload), state -> stash ---");
      s.unload_for_reload();
      const auto intent = session::load_intent(stash);
      if (!intent) {
        std::fprintf(stderr, "no intent in the stash after unload\n");
        return 1;
      }
      std::printf("intent: host=%s port=%u name=%s resume_token=%s\n", intent->host.c_str(), static_cast<unsigned>(intent->port),
                  intent->name.c_str(), idhex(intent->resume_token).c_str());
      // The "restarted extension": a brand-new Session built from the stash.
      session::SessionOptions opt2 = opt;
      opt2.resume = intent;
      Session s2(opt2);
      std::vector<SessionEvent> ev2;
      if (!s2.start()) {
        std::fprintf(stderr, "failed to start the resumed session\n");
        return 1;
      }
      const bool ok2 = pump(s2, ev2, overall, [&] { return has(ev2, SessionEvent::Kind::Welcome) || s2.state() == session::State::Disconnected; });
      if (!ok2 || !s2.has_welcome()) {
        std::fprintf(stderr, "no Welcome after the reload\n");
        return 1;
      }
      const bool same = s2.welcome().player_id == player_id;
      std::printf("reload result: resumed=%s same_player_id=%s (player_id %u -> %u)\n", s2.welcome().resumed ? "true" : "false",
                  same ? "true" : "false", static_cast<unsigned>(player_id), static_cast<unsigned>(s2.welcome().player_id));
      if (!s2.welcome().resumed || !same) rc = 1;
      s2.stop();
    } else {
      s.stop();
    }
  }
  std::puts(rc == 0 ? "OK" : "FAILED");
  return rc;
}
