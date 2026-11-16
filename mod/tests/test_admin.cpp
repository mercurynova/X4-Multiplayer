// In-game admin bridge (m3-plan Q6, docs/protocol.md 17): the mod half of AdminCommand/AdminResult. The feature itself (bridge
// verb, JSON, link) is exercised by the hostsim kit; what is unit-testable here is the frame -> hub path the join feature calls.
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <span>
#include <vector>

#include <flatbuffers/flatbuffers.h>

#include "admin_generated.h"
#include "features/admin/admin_hub.h"
#include "message_ids_generated.h"

using namespace x4mp::features::admin;

namespace {

std::vector<std::uint8_t> encode_result(std::uint32_t request_id, bool ok, const char* message) {
  flatbuffers::FlatBufferBuilder fbb;
  const auto root = X4MP::Proto::CreateAdminResult(fbb, request_id, ok, fbb.CreateString(message));
  fbb.Finish(root);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

void feed(const std::vector<std::uint8_t>& payload) {
  admin_hub().on_frame_message(static_cast<std::uint16_t>(X4MP::Proto::MsgType::AdminResult), std::span<const std::uint8_t>(payload.data(), payload.size()), 0);
}

}  // namespace

TEST_CASE("admin hub: an AdminResult reaches the queue with its request id, verdict and message", "[admin][wire]") {
  (void)admin_hub().take_results();  // start from an empty queue whatever ran before

  feed(encode_result(7, true, "Pia was kicked"));
  feed(encode_result(8, false, "player 999 is not in the session"));

  const auto results = admin_hub().take_results();
  REQUIRE(results.size() == 2);
  CHECK(results[0].request_id == 7);
  CHECK(results[0].ok);
  CHECK(results[0].message == "Pia was kicked");
  CHECK(results[1].request_id == 8);
  CHECK_FALSE(results[1].ok);
  CHECK(results[1].message == "player 999 is not in the session");
  CHECK(admin_hub().take_results().empty());  // taking the queue clears it
}

TEST_CASE("admin hub: another message type or a truncated frame is not queued", "[admin][wire]") {
  (void)admin_hub().take_results();
  const auto payload = encode_result(9, true, "x");
  admin_hub().on_frame_message(static_cast<std::uint16_t>(X4MP::Proto::MsgType::ChatSend), std::span<const std::uint8_t>(payload.data(), payload.size()), 0);

  const std::uint8_t short_frame[4] = {1, 2, 3, 4};
  admin_hub().on_frame_message(static_cast<std::uint16_t>(X4MP::Proto::MsgType::AdminResult), std::span<const std::uint8_t>(short_frame, 4), 0);

  CHECK(admin_hub().take_results().empty());
}

TEST_CASE("admin hub: the queue keeps the newest results and drops the oldest", "[admin][wire]") {
  (void)admin_hub().take_results();
  for (std::uint32_t id = 1; id <= 40; ++id) feed(encode_result(id, true, "ok"));

  const auto results = admin_hub().take_results();
  REQUIRE(results.size() == 32);           // kMaxResults
  CHECK(results.front().request_id == 9);  // 1..8 were dropped
  CHECK(results.back().request_id == 40);
}
