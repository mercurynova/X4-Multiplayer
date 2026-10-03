// core/net reliable-Control retention (M1-N3, roadmap follow-up): Control frames queued while the link is down (or while
// the per-connection send gate is closed) are kept, in order, and replayed after a RESUME; a fresh join drops them and
// says so. Realtime/Bulk stay droppable. Loopback fake server, no SDK.

#include <atomic>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "fake_server.h"

using namespace x4mp::net;
using namespace fake;

namespace {

constexpr std::uint16_t kChat = 0x0500;      // Control lane in the catalog
constexpr std::uint16_t kPlayerState = 0x0300;  // Realtime

std::vector<std::uint8_t> payload_of(int i) { return {static_cast<std::uint8_t>(0xC0 + i), 1, 2, 3}; }

NetOptions gated_options(std::atomic<bool>& replay, std::size_t cap_frames = 512) {
  NetOptions o;
  o.backoff_first_ms = 100;
  o.backoff_cap_ms = 100;
  o.gate_outbox_until_released = true;
  o.control_retention_frames = cap_frames;
  o.frame_hook = [&replay](std::uint16_t type, Lane, std::span<const std::uint8_t>, HookContext& ctx) {
    if (type == T(MsgType::Welcome)) (void)ctx.release_outbox(replay.load());
  };
  return o;
}

// Next kChat frame's first byte, skipping anything else.
std::optional<std::uint8_t> next_chat(Conn& c) {
  auto f = c.read_frame_of(kChat);
  if (!f) return std::nullopt;
  return f->payload[0];
}

// Connects, lets the first connection settle, kills it and waits for the client to sit in Backoff.
struct Bounce {
  Listener server;
  NetClient client;
  Conn first;
  explicit Bounce(NetOptions o) {
    REQUIRE(client.start(server.endpoint(), std::move(o)));
    first = server.accept();
    REQUIRE(first.valid());
    first.send(welcome(1, 1, 1, false));
    REQUIRE(wait_until([&] { return client.status().frames_in >= 1; }));
    first.close();
    REQUIRE(wait_until([&] { return client.status().state == ConnState::Backoff; }));
  }
};

}  // namespace

TEST_CASE("retention: Control frames queued while down are replayed in order after a resume", "[net][retention]") {
  std::atomic<bool> replay{true};
  Bounce b(gated_options(replay));
  for (int i = 0; i < 5; ++i) REQUIRE(b.client.send(Lane::Control, kChat, payload_of(i)) == SendResult::Ok);
  REQUIRE(b.client.send(Lane::Realtime, kPlayerState, payload_of(9)) == SendResult::Ok);  // droppable
  // The net thread drains the outbox in batches and publishes status after each, so the retained count and
  // the dropped count can become visible in separate snapshots: wait for both (do not read one right after the other).
  REQUIRE(wait_until([&] {
    const auto s = b.client.status();
    return s.control_retained_frames == 5 && s.outbox_dropped >= 1;  // outbox_dropped: the Realtime frame
  }));

  Conn second = b.server.accept();
  REQUIRE(second.valid());
  // The gate is closed: nothing the main thread queued reaches the wire before the Welcome.
  std::this_thread::sleep_for(150ms);
  b.client.send(Lane::Control, kChat, payload_of(5));  // queued during the handshake: also held, also in order
  std::this_thread::sleep_for(100ms);
  CHECK(b.client.status().frames_out == 0);
  second.send(welcome(1, 1, 1, true));

  for (int i = 0; i < 6; ++i) {
    const auto got = next_chat(second);
    REQUIRE(got.has_value());
    CHECK(*got == 0xC0 + i);
  }
  // The net thread writes the replayed frames inside the same loop iteration that handled the Welcome and publishes
  // status only at the end of it, so the server can have read all six frames while the published snapshot still shows
  // the pre-replay counters. Wait for the snapshot to catch up, then assert on that one snapshot.
  NetStatus st{};
  REQUIRE(wait_until([&] {
    st = b.client.status();
    return st.control_replayed == 6 && st.control_retained_frames == 0;
  }));
  CHECK(st.control_replayed == 6);
  CHECK(st.control_discarded_fresh == 0);
  CHECK(st.control_retained_frames == 0);
  b.client.stop();
}

TEST_CASE("retention: a fresh join drops the queued Control frames and reports the count", "[net][retention]") {
  std::atomic<bool> replay{false};
  Bounce b(gated_options(replay));
  for (int i = 0; i < 4; ++i) REQUIRE(b.client.send(Lane::Control, kChat, payload_of(i)) == SendResult::Ok);
  REQUIRE(wait_until([&] { return b.client.status().control_retained_frames == 4; }));

  Conn second = b.server.accept();
  REQUIRE(second.valid());
  second.send(welcome(2, 9, 9, false));
  REQUIRE(wait_until([&] { return b.client.status().control_discarded_fresh == 4; }));
  // After the gate opens, new frames flow; the old ones are gone.
  REQUIRE(b.client.send(Lane::Control, kChat, payload_of(7)) == SendResult::Ok);
  const auto got = next_chat(second);
  REQUIRE(got.has_value());
  CHECK(*got == 0xC0 + 7);
  CHECK(b.client.status().control_replayed == 0);
  b.client.stop();
}

TEST_CASE("retention: the caps refuse new Control frames (oldest prefix kept) and count them", "[net][retention]") {
  std::atomic<bool> replay{true};
  Bounce b(gated_options(replay, 2));
  for (int i = 0; i < 5; ++i) (void)b.client.send(Lane::Control, kChat, payload_of(i));
  REQUIRE(wait_until([&] { return b.client.status().control_retention_overflow == 3; }));
  CHECK(b.client.status().control_retained_frames == 2);

  Conn second = b.server.accept();
  REQUIRE(second.valid());
  second.send(welcome(1, 1, 1, true));
  CHECK(next_chat(second) == 0xC0);
  CHECK(next_chat(second) == 0xC1);
  b.client.stop();
}

TEST_CASE("retention: without the gate behaviour is unchanged (queued frames are discarded on reconnect)", "[net][retention]") {
  NetOptions o;
  o.backoff_first_ms = 100;
  o.backoff_cap_ms = 100;
  Bounce b(std::move(o));
  for (int i = 0; i < 3; ++i) (void)b.client.send(Lane::Control, kChat, payload_of(i));
  REQUIRE(wait_until([&] { return b.client.status().outbox_dropped >= 3; }));
  CHECK(b.client.status().control_retained_frames == 0);
  Conn second = b.server.accept();
  REQUIRE(second.valid());
  second.send(welcome(1, 1, 1, true));
  // Ungated, a frame queued before the net thread has finished connecting is (by design) dropped as belonging to a
  // dead handshake, and accept() on the server side only proves the TCP handshake, not that the client reached
  // Connected. The Welcome being processed (second inbound frame overall) proves it did.
  REQUIRE(wait_until([&] {
    const auto s = b.client.status();
    return s.state == ConnState::Connected && s.frames_in >= 2;
  }));
  REQUIRE(b.client.send(Lane::Control, kChat, payload_of(8)) == SendResult::Ok);
  CHECK(next_chat(second) == 0xC8);  // the first chat frame on the new connection is the new one
  b.client.stop();
}

TEST_CASE("retention: set_goodbye_code sends Disconnect(ClientReload) on stop", "[net][reload]") {
  Listener server;
  NetClient client;
  REQUIRE(client.start(server.endpoint()));
  Conn conn = server.accept();
  REQUIRE(conn.valid());
  conn.send(welcome(1, 1, 1, false));
  REQUIRE(wait_until([&] { return client.status().frames_in >= 1; }));
  client.set_goodbye_code(kDisconnectClientReload);
  client.stop();
  const auto f = conn.read_frame_of(T(MsgType::Disconnect));
  REQUIRE(f.has_value());
  const auto* d = flatbuffers::GetRoot<X4MP::Proto::Disconnect>(f->payload.data());
  CHECK(static_cast<std::uint16_t>(d->code()) == 6);
}
