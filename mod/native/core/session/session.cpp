#include "core/session/session.h"

namespace x4mp::session {

const char* to_string(State state) noexcept {
  switch (state) {
    case State::Disconnected: return "Disconnected";
    case State::Connecting: return "Connecting";
    case State::Handshaking: return "Handshaking";
    case State::Joining: return "Joining";
    case State::InSession: return "InSession";
    case State::Reconnecting: return "Reconnecting";
  }
  return "?";
}

bool can_transition(State from, State to) noexcept {
  if (to == State::Disconnected) return from != State::Disconnected;  // any live state can drop
  switch (from) {
    case State::Disconnected: return to == State::Connecting;
    case State::Connecting: return to == State::Handshaking;
    case State::Handshaking: return to == State::Joining;
    case State::Joining: return to == State::InSession;
    case State::InSession: return to == State::Reconnecting;
    case State::Reconnecting: return to == State::Handshaking;
  }
  return false;
}

}  // namespace x4mp::session