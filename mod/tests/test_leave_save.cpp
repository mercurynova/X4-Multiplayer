// F24 (ADR-054): the planned-leave state machine and the two wire messages.
#include <catch2/catch_test_macros.hpp>
#include <flatbuffers/flatbuffers.h>

#include "features/join/join_messages.h"
#include "features/join/leave_save.h"
#include "session_generated.h"

using namespace x4mp::features::join;
using A = LeaveDecision::Action;

TEST_CASE("PlannedLeave: a non-authority or not-in-game leave goes at once", "[leave]") {
  PlannedLeave l;
  const auto d = l.request(LeaveKind::Disconnect, /*can_save=*/false, 1000, 60000);
  CHECK(d.action == A::LeaveNow);
  CHECK(d.why == LeaveWhy::NoSaveNeeded);
  CHECK(d.close_session);
  CHECK_FALSE(l.waiting());

  // Esc > Exit does not close the session itself (F1 and the reload decision own that path); Quit does
  const auto exit_menu = l.request(LeaveKind::ExitMenu, false, 1000, 60000);
  CHECK(exit_menu.action == A::LeaveNow);
  CHECK_FALSE(exit_menu.close_session);
  CHECK(l.request(LeaveKind::Quit, false, 1000, 60000).close_session);
}

TEST_CASE("PlannedLeave: the authority waits for the checkpoint, then leaves", "[leave]") {
  PlannedLeave l;
  const auto d = l.request(LeaveKind::Disconnect, true, 1000, 60000);
  CHECK(d.action == A::SendRequest);
  CHECK(l.waiting());
  CHECK(l.tick(30000).action == A::None);  // still waiting

  const auto done = l.on_result(true);
  CHECK(done.action == A::LeaveNow);
  CHECK(done.why == LeaveWhy::Saved);
  CHECK(done.close_session);
  CHECK_FALSE(l.waiting());
  CHECK(l.on_result(true).action == A::None);  // a late duplicate answer does nothing
}

TEST_CASE("PlannedLeave: the server could not save, the leave still happens", "[leave]") {
  PlannedLeave l;
  (void)l.request(LeaveKind::Quit, true, 0, 60000);
  const auto d = l.on_result(false);
  CHECK(d.action == A::LeaveNow);
  CHECK(d.why == LeaveWhy::NotSaved);
  CHECK(d.close_session);
}

TEST_CASE("PlannedLeave: the timeout leaves, exactly at the deadline", "[leave]") {
  PlannedLeave l;
  (void)l.request(LeaveKind::Disconnect, true, 1000, 60000);
  CHECK(l.tick(60999).action == A::None);
  const auto d = l.tick(61000);
  CHECK(d.action == A::LeaveNow);
  CHECK(d.why == LeaveWhy::TimedOut);
  CHECK(l.tick(70000).action == A::None);
  // a timeout below 1 ms is clamped, never an immediate leave without the request
  PlannedLeave z;
  CHECK(z.request(LeaveKind::Disconnect, true, 1000, 0).action == A::SendRequest);
}

TEST_CASE("PlannedLeave: pressing again while waiting leaves at once", "[leave]") {
  PlannedLeave l;
  (void)l.request(LeaveKind::Disconnect, true, 0, 60000);
  const auto d = l.request(LeaveKind::Disconnect, true, 5000, 60000);
  CHECK(d.action == A::LeaveNow);
  CHECK(d.why == LeaveWhy::Forced);
  CHECK(d.close_session);
  CHECK_FALSE(l.waiting());

  // Exit held, then Disconnect pressed: the session is closed; Exit held then Exit again: the F1 path stays in charge
  PlannedLeave e;
  (void)e.request(LeaveKind::ExitMenu, true, 0, 60000);
  CHECK(e.request(LeaveKind::Disconnect, true, 1, 60000).close_session);
  PlannedLeave e2;
  (void)e2.request(LeaveKind::ExitMenu, true, 0, 60000);
  const auto second_exit = e2.request(LeaveKind::ExitMenu, true, 1, 60000);
  CHECK(second_exit.why == LeaveWhy::Forced);
  CHECK_FALSE(second_exit.close_session);
}

TEST_CASE("PlannedLeave: a lost session ends the wait; reset forgets it", "[leave]") {
  PlannedLeave l;
  CHECK(l.on_session_lost().action == A::None);
  (void)l.request(LeaveKind::Disconnect, true, 0, 60000);
  const auto d = l.on_session_lost();
  CHECK(d.action == A::LeaveNow);
  CHECK(d.why == LeaveWhy::SessionLost);

  (void)l.request(LeaveKind::Disconnect, true, 0, 60000);
  l.reset();
  CHECK_FALSE(l.waiting());
  CHECK(l.tick(1000000).action == A::None);
}

TEST_CASE("LeaveSaveRequest / LeaveSaveResult wire", "[leave][wire]") {
  const auto request = encode_leave_save_request(2);
  flatbuffers::Verifier v(request.data(), request.size());
  REQUIRE(v.VerifyBuffer<X4MP::Proto::LeaveSaveRequest>(nullptr));
  CHECK(flatbuffers::GetRoot<X4MP::Proto::LeaveSaveRequest>(request.data())->kind() == 2);
  CHECK(msg_leave_save_request() == 0x0118);
  CHECK(msg_leave_save_result() == 0x0119);

  flatbuffers::FlatBufferBuilder fbb(64);
  fbb.Finish(X4MP::Proto::CreateLeaveSaveResultDirect(fbb, true, "checkpoint is current"));
  const auto info = parse_leave_save_result(std::span<const std::uint8_t>(fbb.GetBufferPointer(), fbb.GetSize()));
  REQUIRE(info);
  CHECK(info->saved);
  CHECK(info->detail == "checkpoint is current");

  const std::vector<std::uint8_t> junk{1, 2, 3};
  CHECK_FALSE(parse_leave_save_result(junk));
  CHECK_FALSE(parse_leave_save_result({}));
}
