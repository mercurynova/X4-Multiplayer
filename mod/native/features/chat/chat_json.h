#pragma once
// features/chat/chat_json: chat and roster payloads between the Lua UI, the server and back (M3-06; docs/mod-design.md 7.1 / 7.6).
// SDK-free and free of the session layer, so Catch2 tests them directly.
//
//   Lua -> native  x4mp.chat_send {"v":1,"channel":"all|team|whisper","to":<player id, whisper only>,"text":"..."}  -> ChatSend (Control lane)
//   native -> Lua  x4mp.chat      {"v":1,"replay":bool,"messages":[{"from":id,"name":"..","team":N,"channel":"all|whisper|admin|system|team",
//                                   "text":"..","t":<server us>,"self":bool,"code":"not_connected"|...}]}
//                  x4mp.players   {"v":1,"self":id,"players":[{"id","name","team","team_role","roles","phase","online","ping","sector",
//                                   "ship"}],"events":[{"kind":"join"|"leave","id","name"}]}
//
// Chat text is user content: it is never written to the log at Info (log lines carry the length; Debug carries truncate_for_log()).

#include <cstdint>
#include <deque>
#include <map>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::features::chat {

inline constexpr std::size_t kMaxChatChars = 256;  // protocol.md 22 / the server's RelayModule.MaxChatLength
inline constexpr std::size_t kHistoryMax = 50;     // messages kept for a Lua state that starts late (/reloadui)

enum class Channel : std::uint8_t { All = 0, Whisper = 1, Admin = 2, System = 3, Team = 4 };  // = ChatChannel on the wire

struct SendRequest {
  Channel channel = Channel::All;
  std::uint16_t to_player = 0;  // Whisper only
  std::string text;             // cleaned: control characters out, trimmed, at most kMaxChatChars code points
};

// nullopt (with `error` set to a short machine code) when the text is not a valid chat_send request: bad_json, bad_channel, no_text,
// no_recipient (whisper without a player id). "channel" defaults to "all".
[[nodiscard]] std::optional<SendRequest> parse_send(std::string_view json, std::string* error = nullptr);
[[nodiscard]] std::vector<std::uint8_t> encode_send(const SendRequest& request);
[[nodiscard]] std::uint16_t msg_chat_send() noexcept;
[[nodiscard]] std::uint16_t msg_chat_message() noexcept;
[[nodiscard]] std::uint16_t msg_roster_update() noexcept;

// Control characters out (also DEL), leading/trailing blanks trimmed, cut at `max_chars` UTF-8 code points.
[[nodiscard]] std::string clean_text(std::string_view text, std::size_t max_chars = kMaxChatChars);
[[nodiscard]] std::string_view channel_name(Channel channel) noexcept;
// First `max_chars` code points, with "..." appended when cut. For Debug log lines only.
[[nodiscard]] std::string truncate_for_log(std::string_view text, std::size_t max_chars = 40);

struct PlayerRow {
  std::uint16_t id = 0;
  std::string name;
  std::uint8_t roles = 0;
  std::uint8_t phase = 0;  // NodePhase (7 = InGame)
  std::uint16_t team = 0;
  std::uint8_t team_role = 0;
  std::uint32_t ship_net_id = 0;
  std::uint16_t sector = 0;
  std::uint16_t ping_ms = 0;
  bool online = true;
};

struct RosterEvent {
  bool joined = false;  // false = left
  std::uint16_t id = 0;
  std::string name;
};

// The roster as RosterUpdate frames build it, plus join/leave detection. The first update after reset() is the baseline: it creates no
// events (a player who joins a running session does not announce everyone already there).
class RosterTracker {
 public:
  void reset();
  // false when the payload is not a valid RosterUpdate. Events are appended to `events` (never for `self_id`).
  bool apply(std::span<const std::uint8_t> payload, std::uint16_t self_id, std::vector<RosterEvent>& events);
  [[nodiscard]] const PlayerRow* find(std::uint16_t id) const;
  [[nodiscard]] std::size_t size() const noexcept { return rows_.size(); }
  [[nodiscard]] bool has_baseline() const noexcept { return baseline_; }
  [[nodiscard]] std::string players_json(std::uint16_t self_id, const std::vector<RosterEvent>& events) const;

 private:
  std::map<std::uint16_t, PlayerRow> rows_;
  bool baseline_ = false;
};

// One ChatMessage frame -> one JSON object (see the header comment); empty string when the payload is not a ChatMessage. The sender's
// team comes from `roster` (0 when unknown).
[[nodiscard]] std::string message_json(std::span<const std::uint8_t> payload, const RosterTracker& roster, std::uint16_t self_id);
// A message the node makes itself (not from the server): channel "system", a machine `code` the Lua side localises, and a plain English
// `text` as the fallback.
[[nodiscard]] std::string local_notice_json(std::string_view code, std::string_view text);

// What the join feature's session pump hands over and the chat feature raises to Lua. Main thread only (both run in on_frame).
class ChatHub {
 public:
  struct Drain {
    std::vector<std::string> messages;
    std::vector<RosterEvent> events;
    bool players_changed = false;
  };

  // Called by the session pump for every frame the server sends (ChatMessage, RosterUpdate; anything else is ignored).
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);
  void push_local_notice(std::string_view code, std::string_view text);
  void session_ended();            // the connection is gone for good: roster and history cleared, Lua gets an empty players list
  void request_resend() noexcept;  // a fresh Lua state (ui_ready): history and the players list go out again

  // Moves what accumulated into `out`. Returns false when there was nothing.
  bool drain(Drain& out);
  [[nodiscard]] std::string players_json(const std::vector<RosterEvent>& events = {}) const;
  [[nodiscard]] const std::deque<std::string>& history() const noexcept { return history_; }
  [[nodiscard]] bool take_resend() noexcept;
  [[nodiscard]] const RosterTracker& roster() const noexcept { return roster_; }
  [[nodiscard]] std::uint16_t self_id() const noexcept { return self_id_; }
  void reset();  // tests

 private:
  RosterTracker roster_;
  std::deque<std::string> history_;
  std::vector<std::string> pending_messages_;
  std::vector<RosterEvent> pending_events_;
  bool players_changed_ = false;
  bool resend_ = false;
  std::uint16_t self_id_ = 0;
};

[[nodiscard]] ChatHub& chat_hub() noexcept;

}  // namespace x4mp::features::chat
