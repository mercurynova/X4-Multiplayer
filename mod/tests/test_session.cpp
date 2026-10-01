#include <catch2/catch_test_macros.hpp>

#include "core/session/session.h"

using namespace x4mp::session;

TEST_CASE("session transitions", "[session]") {
  CHECK(can_transition(State::Disconnected, State::Connecting));
  CHECK(can_transition(State::Joining, State::InSession));
  CHECK(can_transition(State::InSession, State::Disconnected));
  CHECK_FALSE(can_transition(State::Disconnected, State::InSession));
  // A drop during the handshake or the join also goes to Reconnecting (the net layer redials by itself).
  CHECK(can_transition(State::Handshaking, State::Reconnecting));
  CHECK(can_transition(State::Joining, State::Reconnecting));
  CHECK(can_transition(State::Reconnecting, State::Handshaking));
  CHECK_FALSE(can_transition(State::Reconnecting, State::InSession));
  CHECK_FALSE(can_transition(State::Disconnected, State::Disconnected));
  CHECK(std::string_view(to_string(State::InSession)) == "InSession");
}