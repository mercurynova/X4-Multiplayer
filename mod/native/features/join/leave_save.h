#pragma once
// features/join: the state machine of a PLANNED authority leave with a checkpoint first (F24, ADR-054). Pure logic, no game or network calls:
// JoinFeature feeds it the player's leave actions, the server's LeaveSaveResult and the clock, and acts on the Decision it returns.
//
//   Disconnect (Multiplayer screen)  -> request(Disconnect)
//   Esc > Exit to main menu / Quit   -> request(ExitMenu / Quit)       (Lua wrappers around QuitModule / QuitGame hold the real call)
//   server LeaveSaveResult           -> on_result(saved)
//   every frame                      -> tick(now)
//
// A leave is only held back when it is worth it: this node is the authority, in game, with a welcomed session (`can_save`). Everything else
// leaves at once. While waiting, a second leave action leaves at once (the player may not want to wait), and so does the timeout.
// A crash, a lost socket or the F1 main-menu leave never come through here: the universe is gone, there is nothing to save.

#include <cstdint>

namespace x4mp::features::join {

enum class LeaveKind : std::uint8_t { Disconnect = 0, ExitMenu = 1, Quit = 2 };

// Why a leave ended (logged; part of the Decision).
enum class LeaveWhy : std::uint8_t {
  NoSaveNeeded,  // not the authority / not in game: leaves at once
  Saved,         // the server's checkpoint is current
  NotSaved,      // the server answered "saved = false"; leaves anyway
  TimedOut,      // no answer within the timeout
  Forced,        // a second leave action while waiting
  SessionLost,   // the connection ended while waiting; nothing to wait for
};

struct LeaveDecision {
  enum class Action : std::uint8_t {
    None,         // nothing to do
    SendRequest,  // send LeaveSaveRequest and show "saving before leaving"
    LeaveNow,     // send Disconnect (Disconnect / Quit kinds) and let a held Esc action go
  };
  Action action = Action::None;
  LeaveWhy why = LeaveWhy::NoSaveNeeded;
  LeaveKind kind = LeaveKind::Disconnect;  // the kind of the leave that STARTED the wait (or of this one when it did not wait)
  bool close_session = false;              // LeaveNow: the session is closed with a Disconnect(ClientQuit); false for ExitMenu (F1 and the reload decision own that path)
};

class PlannedLeave {
 public:
  [[nodiscard]] bool waiting() const noexcept { return waiting_; }
  [[nodiscard]] LeaveKind kind() const noexcept { return kind_; }

  // A leave action of the player. `can_save`: authority, in game, welcomed session. `timeout_ms` is clamped to at least 1.
  [[nodiscard]] LeaveDecision request(LeaveKind kind, bool can_save, std::int64_t now_ms, std::int64_t timeout_ms) noexcept {
    if (waiting_) {  // pressed again: the player does not want to wait
      return finish(LeaveWhy::Forced, kind == LeaveKind::ExitMenu ? close_ : true);
    }
    if (!can_save) return {LeaveDecision::Action::LeaveNow, LeaveWhy::NoSaveNeeded, kind, kind != LeaveKind::ExitMenu};
    waiting_ = true;
    kind_ = kind;
    close_ = kind != LeaveKind::ExitMenu;
    deadline_ms_ = now_ms + (timeout_ms < 1 ? 1 : timeout_ms);
    return {LeaveDecision::Action::SendRequest, LeaveWhy::Saved, kind, close_};
  }

  [[nodiscard]] LeaveDecision on_result(bool saved) noexcept {
    if (!waiting_) return {};
    return finish(saved ? LeaveWhy::Saved : LeaveWhy::NotSaved, close_);
  }

  [[nodiscard]] LeaveDecision tick(std::int64_t now_ms) noexcept {
    if (!waiting_ || now_ms < deadline_ms_) return {};
    return finish(LeaveWhy::TimedOut, close_);
  }

  // The session ended on its own (socket lost, server Disconnect, the player joined elsewhere).
  [[nodiscard]] LeaveDecision on_session_lost() noexcept {
    if (!waiting_) return {};
    return finish(LeaveWhy::SessionLost, close_);
  }

  // The leave is over without a LeaveNow (a stop_session of another path ended the session): forget it. Returns true when a leave was still
  // waiting: Lua then still holds the real QuitModule / QuitGame call, and the caller must release it (x4mp.leave_go), or Esc > Exit does nothing.
  [[nodiscard]] bool reset() noexcept {
    const bool was_waiting = waiting_;
    waiting_ = false;
    return was_waiting;
  }

 private:
  LeaveDecision finish(LeaveWhy why, bool close_session) noexcept {
    waiting_ = false;
    return {LeaveDecision::Action::LeaveNow, why, kind_, close_session};
  }

  bool waiting_ = false;
  bool close_ = false;
  LeaveKind kind_ = LeaveKind::Disconnect;
  std::int64_t deadline_ms_ = 0;
};

}  // namespace x4mp::features::join
