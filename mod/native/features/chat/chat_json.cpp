#include "features/chat/chat_json.h"

#include <algorithm>
#include <set>
#include <utility>

#include <nlohmann/json.hpp>

#include "events_generated.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::features::chat {

namespace P = X4MP::Proto;
using nlohmann::json;

namespace {
std::string dump(const json& j) { return j.dump(-1, ' ', false, json::error_handler_t::replace); }

bool is_continuation(unsigned char c) { return (c & 0xC0) == 0x80; }

constexpr std::uint8_t kPhaseInGame = static_cast<std::uint8_t>(P::NodePhase::InGame);
}  // namespace

std::string clean_text(std::string_view text, std::size_t max_chars) {
  std::string out;
  out.reserve(std::min(text.size(), max_chars * 4));
  std::size_t chars = 0;
  for (std::size_t i = 0; i < text.size(); ++i) {
    const auto c = static_cast<unsigned char>(text[i]);
    if (c < 0x20 || c == 0x7F) continue;
    if (c == 0xC2 && i + 1 < text.size() && static_cast<unsigned char>(text[i + 1]) >= 0x80 &&
        static_cast<unsigned char>(text[i + 1]) <= 0x9F) {
      ++i;  // C1 control (U+0080..U+009F), like the server's char.IsControl
      continue;
    }
    if (!is_continuation(c)) {
      if (chars == max_chars) break;
      ++chars;
    }
    out.push_back(static_cast<char>(c));
  }
  const auto first = out.find_first_not_of(' ');
  if (first == std::string::npos) return {};
  const auto last = out.find_last_not_of(' ');
  return out.substr(first, last - first + 1);
}

std::string truncate_for_log(std::string_view text, std::size_t max_chars) {
  std::size_t chars = 0;
  std::size_t i = 0;
  for (; i < text.size(); ++i) {
    if (!is_continuation(static_cast<unsigned char>(text[i]))) {
      if (chars == max_chars) break;
      ++chars;
    }
  }
  std::string out(text.substr(0, i));
  if (i < text.size()) out += "...";
  for (char& c : out) {
    if (static_cast<unsigned char>(c) < 0x20) c = ' ';
  }
  return out;
}

std::string_view channel_name(Channel channel) noexcept {
  switch (channel) {
    case Channel::All: return "all";
    case Channel::Whisper: return "whisper";
    case Channel::Admin: return "admin";
    case Channel::System: return "system";
    case Channel::Team: return "team";
  }
  return "all";
}

std::optional<SendRequest> parse_send(std::string_view text, std::string* error) {
  const auto fail = [&](const char* code) -> std::optional<SendRequest> {
    if (error != nullptr) *error = code;
    return std::nullopt;
  };
  json j = json::parse(text.begin(), text.end(), nullptr, /*allow_exceptions=*/false);
  if (!j.is_object()) return fail("bad_json");
  SendRequest r;
  if (const auto it = j.find("channel"); it != j.end()) {
    if (!it->is_string()) return fail("bad_channel");
    const auto name = it->get<std::string>();
    if (name == "all") {
      r.channel = Channel::All;
    } else if (name == "team") {
      r.channel = Channel::Team;
    } else if (name == "whisper") {
      r.channel = Channel::Whisper;
    } else {
      return fail("bad_channel");  // admin / system are not for players (the server refuses them too)
    }
  }
  if (const auto it = j.find("text"); it != j.end() && it->is_string()) r.text = clean_text(it->get<std::string>());
  if (r.text.empty()) return fail("no_text");
  if (r.channel == Channel::Whisper) {
    const auto it = j.find("to");
    if (it == j.end() || !it->is_number_integer()) return fail("no_recipient");
    const auto to = it->get<long long>();
    if (to < 1 || to > 65535) return fail("no_recipient");
    r.to_player = static_cast<std::uint16_t>(to);
  }
  return r;
}

std::vector<std::uint8_t> encode_send(const SendRequest& request) {
  flatbuffers::FlatBufferBuilder fbb(128 + request.text.size());
  const auto text = fbb.CreateString(request.text);
  P::ChatSendBuilder b(fbb);
  b.add_channel(static_cast<P::ChatChannel>(request.channel));
  b.add_to_player(request.to_player);
  b.add_text(text);
  fbb.Finish(b.Finish());
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::uint16_t msg_chat_send() noexcept { return static_cast<std::uint16_t>(P::MsgType::ChatSend); }
std::uint16_t msg_chat_message() noexcept { return static_cast<std::uint16_t>(P::MsgType::ChatMessage); }
std::uint16_t msg_roster_update() noexcept { return static_cast<std::uint16_t>(P::MsgType::RosterUpdate); }

// ---- roster ----------------------------------------------------------------------------------------------------------------

void RosterTracker::reset() {
  rows_.clear();
  baseline_ = false;
}

bool RosterTracker::apply(std::span<const std::uint8_t> payload, std::uint16_t self_id, std::vector<RosterEvent>& events) {
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<P::RosterUpdate>(nullptr)) return false;
  const auto* update = flatbuffers::GetRoot<P::RosterUpdate>(payload.data());
  const bool announce = baseline_;
  if (update->full()) {
    // A full roster replaces everything: whoever is not in it any more has left.
    std::set<std::uint16_t> present;
    if (update->players() != nullptr) {
      for (const auto* p : *update->players()) present.insert(p->player_id());
    }
    for (auto it = rows_.begin(); it != rows_.end();) {
      if (present.count(it->first) != 0) {
        ++it;
        continue;
      }
      if (announce && it->second.online && it->first != self_id) events.push_back({false, it->first, it->second.name});
      it = rows_.erase(it);
    }
  }
  if (update->players() != nullptr) {
    for (const auto* p : *update->players()) {
      PlayerRow row;
      row.id = p->player_id();
      row.name = p->name() != nullptr ? p->name()->str() : std::string();
      row.roles = static_cast<std::uint8_t>(p->roles());
      row.phase = static_cast<std::uint8_t>(p->phase());
      row.team = p->team_id();
      row.team_role = static_cast<std::uint8_t>(p->team_role());
      row.ship_net_id = p->ship_net_id();
      row.sector = p->sector();
      row.ping_ms = p->ping_ms();
      row.online = p->online();
      const auto it = rows_.find(row.id);
      const bool was_online = it != rows_.end() && it->second.online;
      if (announce && row.id != self_id) {
        if (row.online && !was_online) events.push_back({true, row.id, row.name});
        if (!row.online && was_online) events.push_back({false, row.id, row.name});
      }
      rows_[row.id] = std::move(row);
    }
  }
  if (update->removed() != nullptr) {
    for (const auto id : *update->removed()) {
      const auto it = rows_.find(id);
      if (it == rows_.end()) continue;
      if (announce && it->second.online && id != self_id) events.push_back({false, id, it->second.name});
      rows_.erase(it);
    }
  }
  baseline_ = true;
  return true;
}

const PlayerRow* RosterTracker::find(std::uint16_t id) const {
  const auto it = rows_.find(id);
  return it == rows_.end() ? nullptr : &it->second;
}

std::string RosterTracker::players_json(std::uint16_t self_id, const std::vector<RosterEvent>& events) const {
  json out = {{"v", 1}, {"self", self_id}, {"players", json::array()}, {"events", json::array()}};
  for (const auto& [id, r] : rows_) {
    out["players"].push_back({{"id", r.id},
                              {"name", r.name},
                              {"team", r.team},
                              {"team_role", r.team_role},
                              {"roles", r.roles},
                              {"phase", r.phase},
                              {"in_game", r.phase == kPhaseInGame},
                              {"online", r.online},
                              {"ping", r.ping_ms},
                              {"sector", r.sector},
                              {"ship", r.ship_net_id}});
  }
  for (const auto& e : events) out["events"].push_back({{"kind", e.joined ? "join" : "leave"}, {"id", e.id}, {"name", e.name}});
  return dump(out);
}

// ---- chat messages ---------------------------------------------------------------------------------------------------------

std::string message_json(std::span<const std::uint8_t> payload, const RosterTracker& roster, std::uint16_t self_id) {
  flatbuffers::Verifier v(payload.data(), payload.size());
  if (!v.VerifyBuffer<P::ChatMessage>(nullptr)) return {};
  const auto* m = flatbuffers::GetRoot<P::ChatMessage>(payload.data());
  const auto from = m->from_player();
  const auto* row = from != 0 ? roster.find(from) : nullptr;
  json j = {{"from", from},
            {"name", m->from_name() != nullptr ? m->from_name()->str() : std::string()},
            {"team", row != nullptr ? row->team : 0},
            {"channel", channel_name(static_cast<Channel>(m->channel()))},
            {"text", m->text() != nullptr ? m->text()->str() : std::string()},
            {"t", m->server_time_us()},
            {"self", from != 0 && from == self_id}};
  return dump(j);
}

std::string local_notice_json(std::string_view code, std::string_view text) {
  json j = {{"from", 0}, {"name", ""}, {"team", 0}, {"channel", "system"}, {"text", std::string(text)}, {"t", 0}, {"self", false},
            {"code", std::string(code)}};
  return dump(j);
}

// ---- hub -------------------------------------------------------------------------------------------------------------------

void ChatHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id) {
  self_id_ = self_id;
  if (type == msg_roster_update()) {
    if (roster_.apply(payload, self_id, pending_events_)) players_changed_ = true;
  } else if (type == msg_chat_message()) {
    std::string json_text = message_json(payload, roster_, self_id);
    if (json_text.empty()) return;
    history_.push_back(json_text);
    while (history_.size() > kHistoryMax) history_.pop_front();
    pending_messages_.push_back(std::move(json_text));
    if (pending_messages_.size() > 200) pending_messages_.erase(pending_messages_.begin());
  }
}

void ChatHub::push_local_notice(std::string_view code, std::string_view text) {
  pending_messages_.push_back(local_notice_json(code, text));
  if (pending_messages_.size() > 200) pending_messages_.erase(pending_messages_.begin());
}

void ChatHub::session_ended() {
  const bool had = roster_.has_baseline() || !history_.empty();
  roster_.reset();
  history_.clear();
  pending_messages_.clear();
  pending_events_.clear();
  self_id_ = 0;
  if (had) players_changed_ = true;  // Lua gets an empty list
}

void ChatHub::request_resend() noexcept { resend_ = true; }

bool ChatHub::take_resend() noexcept { return std::exchange(resend_, false); }

bool ChatHub::drain(Drain& out) {
  const bool any = !pending_messages_.empty() || !pending_events_.empty() || players_changed_;
  out.messages = std::exchange(pending_messages_, {});
  out.events = std::exchange(pending_events_, {});
  out.players_changed = std::exchange(players_changed_, false);
  return any;
}

std::string ChatHub::players_json(const std::vector<RosterEvent>& events) const { return roster_.players_json(self_id_, events); }

void ChatHub::reset() { *this = ChatHub{}; }

ChatHub& chat_hub() noexcept {
  static ChatHub hub;
  return hub;
}

}  // namespace x4mp::features::chat
