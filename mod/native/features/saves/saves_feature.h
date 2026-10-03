#pragma once
// features/saves: client save control, native half (M2-10, m2-plan 5.3; mod-design 6.2 / 7).
//
// The blocking itself is Lua (ui/x4mp_saves.lua wraps SaveGame / IsSavingPossible and greys the Esc-menu row) plus the
// MD diff on md/notifications.xml (drops the vanilla autosave request). This feature only DRIVES it from native state,
// because the Lua flag is in-memory and resets on every save load / reload (session 2, C1):
//
//   native -> Lua  x4mp.saves {"v":1,"block":true|false}   pushed on ui_ready, on game load, on universe ready and
//                                                            whenever the connection state changes (DiagHub)
//   Lua -> native  x4mp.ui_ready      (bridge) -> push the current flag
//                  x4mp.saves_status  {"v":1,"save_game":bool,"is_saving_possible":bool,"menu_row":bool,"tooltip":bool,
//                                      "blocking":bool,"detail":"..."} what the wrappers installed (for the self-test)
//                  x4mp.game_saved    {"v":1,"success":1,"age":123.4}   MD event_game_saved, every save in the game
//                  x4mp.saves_debug   {"v":1,"role":"client|authority|none","connected":bool}   TEST SEAM, honoured only
//                                     when selftest=true in x4mp.json (hostsim drives the client state with it)
//
// A game_saved while connected as a client means a save slipped past the Lua wrapper (quicksave bypasses Lua, session 2
// C3): log a warning, forward it (LogForward) and tell the player. The hard block is M4. No hooks, no module pin.
//
// All bridge callbacks may arrive on any thread: they only copy the text into an inbox; everything is handled in
// on_frame (main thread).

#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "host/feature.h"

namespace x4mp::features {

class SavesFeature final : public host::IFeature {
 public:
  struct Counters {
    std::uint32_t pushes = 0;          // x4mp.saves raised
    std::uint32_t saves_while_client = 0;
    std::uint32_t warnings_sent = 0;   // rate-limited warnings actually emitted
  };

  SavesFeature();
  [[nodiscard]] std::string_view name() const noexcept override { return "saves"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_universe_ready(host::HostContext& ctx) override;
  [[nodiscard]] const Counters& counters() const noexcept { return counters_; }

 private:
  struct Inbox;  // shared with the bridge callbacks (they may outlive this object by a moment)
  void handle(host::HostContext& ctx, const std::string& verb, const std::string& text);
  void push_block(host::HostContext& ctx, bool block);
  void on_game_saved(host::HostContext& ctx, const std::string& text);

  std::shared_ptr<Inbox> inbox_;
  std::atomic<bool> push_needed_{true};
  int last_pushed_ = -1;                 // -1 never, 0 false, 1 true
  std::uint64_t frames_since_try_ = 0;   // retry pacing when raise_lua fails (Lua side not up yet)
  std::chrono::steady_clock::time_point last_warning_{};
  bool warned_once_ = false;
  Counters counters_;
};

}  // namespace x4mp::features
