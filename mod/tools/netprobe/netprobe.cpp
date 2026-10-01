// netprobe: connects to an X4MP server with the real core/net client, prints the ServerHello, and exits.
//
//   netprobe [host] [port] [--hold SECONDS]     defaults 127.0.0.1 47780
//   netprobe --from-env                         reads X4MP_TEST_SERVER=host:port; exit 77 (ctest "skipped") if unset
//
// With --hold the connection is kept open and Ping/Pong status (RTT, clock offset) is printed once a second.
// Exit codes: 0 = ServerHello received, 1 = timeout or connection failure, 2 = bad arguments, 77 = skipped.
// (Env vars are fine here: this is a test tool, not the mod runtime.)

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

#include "control_generated.h"
#include "core/net/net.h"

namespace {
using namespace std::chrono_literals;

void print_hello(const X4MP::Proto::ServerHello* h) {
  std::printf("ServerHello: protocol %u.%u, server_version=%s, server_name=%s, auth=%d, phase=%d, nonce=%u bytes\n",
              static_cast<unsigned>(h->protocol_major()), static_cast<unsigned>(h->protocol_minor()),
              h->server_version() ? h->server_version()->c_str() : "", h->server_name() ? h->server_name()->c_str() : "",
              static_cast<int>(h->auth()), static_cast<int>(h->phase()),
              h->nonce() ? static_cast<unsigned>(h->nonce()->size()) : 0u);
  if (h->required_game_build() && h->required_game_build()->size() > 0) {
    std::printf("  required_game_build=%s\n", h->required_game_build()->c_str());
  }
}
}  // namespace

int main(int argc, char** argv) {
  x4mp::net::Endpoint ep;
  int hold_seconds = 0;
  for (int i = 1; i < argc; ++i) {
    const std::string a = argv[i];
    if (a == "--from-env") {
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
        ep.host = s;
      } else {
        ep.host = s.substr(0, colon);
        ep.port = static_cast<std::uint16_t>(std::atoi(s.c_str() + colon + 1));
      }
    } else if (a == "--hold" && i + 1 < argc) {
      hold_seconds = std::atoi(argv[++i]);
    } else if (i == 1 && a[0] != '-') {
      ep.host = a;
    } else if (i == 2 && a[0] != '-') {
      ep.port = static_cast<std::uint16_t>(std::atoi(a.c_str()));
    } else {
      std::fprintf(stderr, "usage: netprobe [host] [port] [--hold SECONDS] | --from-env\n");
      return 2;
    }
  }

  x4mp::net::NetClient client;
  if (!client.start(ep)) {
    std::fprintf(stderr, "failed to start the net thread\n");
    return 1;
  }
  std::printf("connecting to %s:%u ...\n", ep.host.c_str(), static_cast<unsigned>(ep.port));

  bool got_hello = false;
  const auto deadline = std::chrono::steady_clock::now() + 10s;
  std::vector<x4mp::net::InboundEvent> events;
  while (!got_hello && std::chrono::steady_clock::now() < deadline) {
    events.clear();
    client.poll_inbox(events);
    for (const auto& ev : events) {
      using Kind = x4mp::net::InboundEvent::Kind;
      if (ev.kind == Kind::Connected) {
        std::puts("connected");
      } else if (ev.kind == Kind::Disconnected) {
        std::printf("disconnected: %s (%s)\n", x4mp::net::to_string(ev.reason), ev.text.c_str());
      } else if (ev.kind == Kind::Frame && ev.type == 0x0001) {
        print_hello(flatbuffers::GetRoot<X4MP::Proto::ServerHello>(ev.payload.data()));
        got_hello = true;
      } else if (ev.kind == Kind::Frame) {
        std::printf("frame type=0x%04X (%zu bytes)\n", static_cast<unsigned>(ev.type), ev.payload.size());
      }
    }
    std::this_thread::sleep_for(10ms);
  }

  if (got_hello && hold_seconds > 0) {
    for (int s = 0; s < hold_seconds; ++s) {
      std::this_thread::sleep_for(1s);
      const auto st = client.status();
      std::printf("t+%ds state=%s rtt=%lldus min=%lldus clock_offset=%lldus valid=%d frames_in=%llu\n", s + 1,
                  x4mp::net::to_string(st.state), static_cast<long long>(st.rtt_us),
                  static_cast<long long>(st.rtt_min_us), static_cast<long long>(st.clock_offset_us),
                  static_cast<int>(st.clock_valid), static_cast<unsigned long long>(st.frames_in));
    }
  }

  const auto st = client.status();
  client.stop();
  if (!got_hello) {
    std::fprintf(stderr, "no ServerHello within 10 s (state=%s, attempts=%u)\n", x4mp::net::to_string(st.state),
                 static_cast<unsigned>(st.connect_attempt));
    return 1;
  }
  return 0;
}
