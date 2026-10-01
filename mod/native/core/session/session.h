#pragma once
// core/session: connection/session state machine. Placeholder (full machine incl. resume token in M1-N3).

namespace x4mp::session {

enum class State { Disconnected, Connecting, Handshaking, Joining, InSession, Reconnecting };

[[nodiscard]] const char* to_string(State state) noexcept;
[[nodiscard]] bool can_transition(State from, State to) noexcept;

}  // namespace x4mp::session