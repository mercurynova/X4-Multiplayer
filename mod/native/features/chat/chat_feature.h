#pragma once
// features/chat: chat + roster routing between the Lua UI and the server (M3-06; docs/mod-design.md 7.6 / 7.7, m3-plan 4.11).
//
//   Lua x4mp.chat_send {"v":1,"channel","to","text"} -> ChatSend on the Control lane (through the diag hub's send link, so this feature
//   needs nothing from the join feature but the frames it hands over)
//   server ChatMessage / RosterUpdate -> the join feature's session pump calls chat_hub().on_frame_message(...) -> this feature
//   raises x4mp.chat (batched, at most one per frame) and x4mp.players (changes, at most 2 Hz, immediately on a join/leave).
//   Lua x4mp.ui_ready (a fresh Lua state, /reloadui) -> history and the players list go out again.
//
// Chat text is user content: Info log lines carry only the channel and the length, Debug lines a truncated text.
// Bridge callbacks may run on any thread: they only copy the text into an inbox that on_frame drains.

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "host/feature.h"

namespace x4mp::features {

class ChatFeature final : public host::IFeature {
 public:
  struct Counters {
    std::uint32_t sent = 0;
    std::uint32_t send_failed = 0;
    std::uint32_t bad_requests = 0;
    std::uint32_t topics_raised = 0;
  };

  ChatFeature();
  [[nodiscard]] std::string_view name() const noexcept override { return "chat"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  [[nodiscard]] const Counters& counters() const noexcept { return counters_; }

 private:
  struct Inbox;
  void handle_send(host::HostContext& ctx, const std::string& text);
  void raise_messages(host::HostContext& ctx, const std::vector<std::string>& messages, bool replay);
  void raise_players(host::HostContext& ctx, const std::string& json);

  std::shared_ptr<Inbox> inbox_;
  double since_players_s_ = 1e9;  // seconds of frame time since x4mp.players went out
  bool players_pending_ = false;
  Counters counters_;
};

}  // namespace x4mp::features
