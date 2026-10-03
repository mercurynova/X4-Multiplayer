#include "features/chat/chat_feature.h"

#include <utility>

#include "features/chat/chat_json.h"
#include "features/diag/diag_hub.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 32;
constexpr std::size_t kMaxTextBytes = 4096;
constexpr double kPlayersMinIntervalS = 0.5;  // x4mp.players at most 2 Hz (a join/leave goes out at once)
}  // namespace

struct ChatFeature::Inbox {
  std::mutex m;
  std::vector<std::pair<std::string, std::string>> q;  // verb, text
  void push(const std::string& verb, std::string_view text) {
    if (text.size() > kMaxTextBytes) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(verb, std::string(text));
  }
  std::vector<std::pair<std::string, std::string>> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

ChatFeature::ChatFeature() : inbox_(std::make_shared<Inbox>()) {}

void ChatFeature::on_init(host::HostContext& ctx) {
  static constexpr const char* kVerbs[] = {"chat_send", "ui_ready"};
  int ok = 0;
  for (const char* verb : kVerbs) {
    auto inbox = inbox_;
    const std::string v = verb;
    if (ctx.platform.subscribe_event(("x4mp." + v).c_str(), [inbox, v](std::string_view text) { inbox->push(v, text); })) ++ok;
  }
  // The Lua state may be new (after a DLL reload the session resumes and the hub is empty): send what we have once it asks, and now.
  chat::chat_hub().request_resend();
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "chat: {} of {} bridge verbs subscribed", ok, static_cast<int>(std::size(kVerbs)));
}

void ChatFeature::handle_send(host::HostContext& ctx, const std::string& text) {
  std::string error;
  const auto request = chat::parse_send(text, &error);
  if (!request) {
    ++counters_.bad_requests;
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Debug, "chat: chat_send refused ({})", error);
    if (error == "no_text") return;  // an empty line is not worth an error line
    chat::chat_hub().push_local_notice(error == "no_recipient" ? "no_recipient" : "bad_request", "The message could not be sent.");
    return;
  }
  if (!diag_hub().send_control(chat::msg_chat_send(), chat::encode_send(*request))) {
    ++counters_.send_failed;
    chat::chat_hub().push_local_notice("not_connected", "Not connected to a multiplayer session: the message was not sent.");
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "chat: not sent (no live session), channel={}", chat::channel_name(request->channel));
    return;
  }
  ++counters_.sent;
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "chat: sent channel={} bytes={}", chat::channel_name(request->channel), request->text.size());
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Debug, "chat: text='{}'", chat::truncate_for_log(request->text));
}

void ChatFeature::raise_messages(host::HostContext& ctx, const std::vector<std::string>& messages, bool replay) {
  if (messages.empty()) return;
  std::string payload = std::string("{\"v\":1,\"replay\":") + (replay ? "true" : "false") + ",\"messages\":[";
  for (std::size_t i = 0; i < messages.size(); ++i) {
    if (i != 0) payload += ',';
    payload += messages[i];
  }
  payload += "]}";
  if (ctx.platform.raise_lua("x4mp.chat", payload)) {
    ++counters_.topics_raised;
  } else {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Debug, "chat: raise_lua x4mp.chat failed ({} messages)", messages.size());
  }
}

void ChatFeature::raise_players(host::HostContext& ctx, const std::string& json) {
  if (ctx.platform.raise_lua("x4mp.players", json)) {
    ++counters_.topics_raised;
  } else {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Debug, "chat: raise_lua x4mp.players failed");
  }
}

void ChatFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  auto& hub = chat::chat_hub();
  since_players_s_ += info.delta_s;
  for (auto& [verb, text] : inbox_->take()) {
    try {
      if (verb == "chat_send") {
        handle_send(ctx, text);
      } else if (verb == "ui_ready") {
        hub.request_resend();
      }
    } catch (const std::exception& e) {
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "chat: bad '{}' message ignored ({})", verb, e.what());
    }
  }

  // A fresh Lua state: the history first (marked replay: no toasts), then the players list.
  if (hub.take_resend()) {
    const std::vector<std::string> history(hub.history().begin(), hub.history().end());
    raise_messages(ctx, history, true);
    players_pending_ = true;
    since_players_s_ = 1e9;
  }

  chat::ChatHub::Drain drained;
  if (hub.drain(drained)) {
    raise_messages(ctx, drained.messages, false);
    if (!drained.events.empty()) {
      raise_players(ctx, hub.players_json(drained.events));  // a join or leave is announced at once
      since_players_s_ = 0.0;
      players_pending_ = false;
    } else if (drained.players_changed) {
      players_pending_ = true;
    }
  }
  if (players_pending_ && since_players_s_ >= kPlayersMinIntervalS) {
    raise_players(ctx, hub.players_json());
    since_players_s_ = 0.0;
    players_pending_ = false;
  }
}

}  // namespace x4mp::features
