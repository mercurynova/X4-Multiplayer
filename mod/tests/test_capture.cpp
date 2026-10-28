// M5-20: the client capture detector. A ghost that turns into the local player's property is reported once, through the
// same Intent{TradeReport}-shaped path as every other claim, and nothing else is: an owner change on a non-ghost, a ghost
// that changed hands between two other factions, an unreadable owner and a refused send are all counted, never guessed.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

#include "features/capture/capture_report.h"

using namespace x4mp;
using namespace x4mp::features;
using namespace x4mp::features::capture;

namespace {

struct Harness {
  std::unordered_map<std::uint64_t, std::uint32_t> ghosts;  // component -> net id
  std::unordered_map<std::uint64_t, std::string> owners;    // component -> faction
  std::vector<std::vector<std::uint8_t>> sent;
  bool send_ok = true;
  std::uint64_t now = 1'000'000;  // ms

  CaptureReportDeps deps() {
    CaptureReportDeps d;
    d.ghost_net_id = [this](std::uint64_t c) {
      const auto it = ghosts.find(c);
      return it == ghosts.end() ? 0u : it->second;
    };
    d.owner_faction = [this](std::uint64_t c) {
      const auto it = owners.find(c);
      return it == owners.end() ? std::string() : it->second;
    };
    d.self_player_id = [] { return std::uint16_t{2}; };
    d.send_control = [this](X4MP::Proto::MsgType, std::span<const std::uint8_t> payload) {
      if (!send_ok) return false;
      sent.emplace_back(payload.begin(), payload.end());
      return true;
    };
    d.now_ms = [this] { return now; };
    return d;
  }
};

}  // namespace

TEST_CASE("M5-20: a ghost that becomes the player's is reported once, and only once", "[capture][m520]") {
  Harness h;
  h.ghosts[0xABC] = 0x1234;
  h.owners[0xABC] = "player";
  CaptureReporter reporter(h.deps());

  CHECK(reporter.on_owner_change(0xABC));
  REQUIRE(h.sent.size() == 1);
  const auto* body = events::capture_report_of(h.sent.front());
  REQUIRE(body != nullptr);
  CHECK(body->target() == 0x1234);
  CHECK(body->new_owner_ref() == 0);  // a client never names the capturing team (M5-21)

  events::IntentView view;
  REQUIRE(events::decode_intent(h.sent.front(), view));
  CHECK(view.request.player_id == 2);
  CHECK(view.request.request_id != 0);
  CHECK(view.type == X4MP::Proto::IntentBody::CaptureReport);

  // the hook can fire again for the same capture; inside the window it is not a second report
  h.now += 1'000;
  CHECK_FALSE(reporter.on_owner_change(0xABC));
  CHECK(h.sent.size() == 1);
  CHECK(reporter.stats().deduped == 1);

  // past the window a fresh capture of the same ship is a fresh report
  h.now += CaptureReporter::kDedupeMs;
  CHECK(reporter.on_owner_change(0xABC));
  CHECK(h.sent.size() == 2);
  CHECK(reporter.stats().sent == 2);
}

TEST_CASE("M5-20: an owner change that is not a capture is counted, never reported", "[capture][m520]") {
  Harness h;
  h.ghosts[0x1] = 0x11;
  h.owners[0x1] = "argon";
  h.ghosts[0x2] = 0x22;  // no owner entry: the game could not answer
  CaptureReporter reporter(h.deps());

  CHECK_FALSE(reporter.on_owner_change(0x1));  // a ghost that changed hands between two other factions
  CHECK_FALSE(reporter.on_owner_change(0x2));  // the game gave no owner
  CHECK_FALSE(reporter.on_owner_change(0x3));  // not a ghost on this node
  CHECK_FALSE(reporter.on_owner_change(0));    // nothing to judge
  CHECK(h.sent.empty());
  CHECK(reporter.stats().not_player == 1);
  CHECK(reporter.stats().unknown_owner == 1);
  CHECK(reporter.stats().not_ghost == 1);
  CHECK(reporter.stats().bad == 1);
}

TEST_CASE("M5-20: a refused send is counted and not remembered as reported", "[capture][m520]") {
  Harness h;
  h.ghosts[0xABC] = 0x1234;
  h.owners[0xABC] = "player";
  CaptureReporter reporter(h.deps());

  h.send_ok = false;
  CHECK_FALSE(reporter.on_owner_change(0xABC));
  CHECK(reporter.stats().send_failed == 1);
  CHECK(h.sent.empty());

  h.send_ok = true;
  CHECK(reporter.on_owner_change(0xABC));  // the same capture is still reportable
  CHECK(h.sent.size() == 1);
}
