// M3-06: features/chat payload helpers (chat_send parsing, ChatSend/ChatMessage wire, roster tracker with join/leave events, the hub).
#include <catch2/catch_test_macros.hpp>

#include <nlohmann/json.hpp>

#include "events_generated.h"
#include "features/chat/chat_json.h"
#include "message_ids_generated.h"
#include "session_generated.h"

using namespace x4mp::features::chat;
namespace P = X4MP::Proto;

namespace {
struct Player {
  std::uint16_t id;
  const char* name;
  std::uint16_t team;
  bool online = true;
  P::NodePhase phase = P::NodePhase::InGame;
  std::uint16_t ping = 20;
};

std::vector<std::uint8_t> roster(bool full, const std::vector<Player>& players, const std::vector<std::uint16_t>& removed = {}) {
  flatbuffers::FlatBufferBuilder fbb(256);
  std::vector<flatbuffers::Offset<P::PlayerInfo>> offsets;
  for (const auto& p : players) {
    const auto name = fbb.CreateString(p.name);
    P::PlayerInfoBuilder b(fbb);
    b.add_player_id(p.id);
    b.add_name(name);
    b.add_phase(p.phase);
    b.add_team_id(p.team);
    b.add_ping_ms(p.ping);
    b.add_sector(3);
    b.add_online(p.online);
    offsets.push_back(b.Finish());
  }
  const auto vec = fbb.CreateVector(offsets);
  const auto rem = fbb.CreateVector(removed);
  P::RosterUpdateBuilder rb(fbb);
  rb.add_full(full);
  rb.add_players(vec);
  rb.add_removed(rem);
  fbb.Finish(rb.Finish());
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}

std::vector<std::uint8_t> message(std::uint16_t from, const char* name, P::ChatChannel channel, const char* text) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto n = fbb.CreateString(name);
  const auto t = fbb.CreateString(text);
  P::ChatMessageBuilder b(fbb);
  b.add_from_player(from);
  b.add_from_name(n);
  b.add_channel(channel);
  b.add_text(t);
  b.add_server_time_us(123456);
  fbb.Finish(b.Finish());
  return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()};
}
}  // namespace

TEST_CASE("chat: clean_text strips control characters, trims and cuts by code points", "[chat]") {
  CHECK(clean_text("  hello\tworld\x01\x7f  ") == "helloworld");
  CHECK(clean_text("   ").empty());
  CHECK(clean_text("a\xC2\x85" "b") == "ab");  // C1 control U+0085
  // 300 two-byte characters are cut at 256 characters, never in the middle of one
  std::string many;
  for (int i = 0; i < 300; ++i) many += "\xC3\xA9";
  const auto cut = clean_text(many);
  CHECK(cut.size() == 512);
  CHECK(truncate_for_log("abcdefghij", 4) == "abcd...");
  CHECK(truncate_for_log("abc", 4) == "abc");
}

TEST_CASE("chat: parse_send accepts all/team/whisper and refuses the rest", "[chat]") {
  std::string err;
  auto r = parse_send(R"({"v":1,"text":"hi there"})", &err);
  REQUIRE(r);
  CHECK(r->channel == Channel::All);
  CHECK(r->text == "hi there");

  r = parse_send(R"({"v":1,"channel":"team","text":"  go  "})", &err);
  REQUIRE(r);
  CHECK(r->channel == Channel::Team);
  CHECK(r->text == "go");

  r = parse_send(R"({"v":1,"channel":"whisper","to":7,"text":"psst"})", &err);
  REQUIRE(r);
  CHECK(r->channel == Channel::Whisper);
  CHECK(r->to_player == 7);

  CHECK_FALSE(parse_send(R"({"channel":"whisper","text":"x"})", &err));
  CHECK(err == "no_recipient");
  CHECK_FALSE(parse_send(R"({"channel":"admin","text":"x"})", &err));
  CHECK(err == "bad_channel");
  CHECK_FALSE(parse_send(R"({"channel":"system","text":"x"})", &err));
  CHECK_FALSE(parse_send(R"({"text":"   "})", &err));
  CHECK(err == "no_text");
  CHECK_FALSE(parse_send("not json", &err));
  CHECK(err == "bad_json");
}

TEST_CASE("chat: ChatSend round trips through the wire encoding", "[chat]") {
  SendRequest req;
  req.channel = Channel::Whisper;
  req.to_player = 9;
  req.text = "h\xC3\xA9llo";
  const auto bytes = encode_send(req);
  flatbuffers::Verifier v(bytes.data(), bytes.size());
  REQUIRE(v.VerifyBuffer<P::ChatSend>(nullptr));
  const auto* send = flatbuffers::GetRoot<P::ChatSend>(bytes.data());
  CHECK(send->channel() == P::ChatChannel::Whisper);
  CHECK(send->to_player() == 9);
  CHECK(send->text()->str() == req.text);
  CHECK(msg_chat_send() == static_cast<std::uint16_t>(P::MsgType::ChatSend));
}

TEST_CASE("chat: roster tracker baseline, join, leave, offline, removal; never for self", "[chat]") {
  RosterTracker t;
  std::vector<RosterEvent> ev;
  REQUIRE(t.apply(roster(true, {{1, "Alice", 1}, {2, "Bob", 1}}), 1, ev));
  CHECK(ev.empty());  // baseline
  CHECK(t.size() == 2);

  REQUIRE(t.apply(roster(false, {{3, "Cy", 2}}), 1, ev));
  REQUIRE(ev.size() == 1);
  CHECK(ev[0].joined);
  CHECK(ev[0].name == "Cy");

  ev.clear();
  REQUIRE(t.apply(roster(false, {{2, "Bob", 1, false, P::NodePhase::Detached}}), 1, ev));  // offline
  REQUIRE(ev.size() == 1);
  CHECK_FALSE(ev[0].joined);
  CHECK(t.find(2) != nullptr);  // still listed (its avatar is parked)
  CHECK_FALSE(t.find(2)->online);

  ev.clear();
  REQUIRE(t.apply(roster(false, {{2, "Bob", 1, true}}), 1, ev));  // back online
  REQUIRE(ev.size() == 1);
  CHECK(ev[0].joined);

  ev.clear();
  REQUIRE(t.apply(roster(false, {}, {3}), 1, ev));  // removed
  REQUIRE(ev.size() == 1);
  CHECK_FALSE(ev[0].joined);
  CHECK(t.find(3) == nullptr);

  ev.clear();
  REQUIRE(t.apply(roster(false, {{1, "Alice", 1, false}}), 1, ev));  // self going offline is not announced
  CHECK(ev.empty());

  // a full roster drops whoever is missing, with a leave event
  ev.clear();
  REQUIRE(t.apply(roster(true, {{1, "Alice", 1}}), 1, ev));
  CHECK(t.size() == 1);
  REQUIRE(ev.size() == 1);
  CHECK(ev[0].name == "Bob");

  CHECK_FALSE(t.apply(std::vector<std::uint8_t>{1, 2, 3}, 1, ev));
}

TEST_CASE("chat: players_json carries the table and the events", "[chat]") {
  RosterTracker t;
  std::vector<RosterEvent> ev;
  REQUIRE(t.apply(roster(true, {{1, "Alice", 2, true, P::NodePhase::InGame, 31}, {2, "Bob", 0, true, P::NodePhase::Loading}}), 1, ev));
  const auto j = nlohmann::json::parse(t.players_json(1, {{true, 2, "Bob"}}));
  CHECK(j["v"] == 1);
  CHECK(j["self"] == 1);
  REQUIRE(j["players"].size() == 2);
  CHECK(j["players"][0]["name"] == "Alice");
  CHECK(j["players"][0]["team"] == 2);
  CHECK(j["players"][0]["ping"] == 31);
  CHECK(j["players"][0]["in_game"] == true);
  CHECK(j["players"][0]["sector"] == 3);
  CHECK(j["players"][1]["in_game"] == false);
  REQUIRE(j["events"].size() == 1);
  CHECK(j["events"][0]["kind"] == "join");
}

TEST_CASE("chat: hub turns frames into message json, events and a bounded history", "[chat]") {
  auto& hub = chat_hub();
  hub.reset();
  hub.on_frame_message(msg_roster_update(), roster(true, {{1, "Alice", 4}, {2, "Bob", 5}}), 1);
  hub.on_frame_message(msg_chat_message(), message(2, "Bob", P::ChatChannel::Team, "hello \"team\"\n"), 1);
  hub.on_frame_message(msg_chat_message(), message(1, "Alice", P::ChatChannel::All, "mine"), 1);
  hub.on_frame_message(msg_chat_message(), std::vector<std::uint8_t>{9, 9}, 1);  // garbage: ignored
  hub.on_frame_message(0x7777, std::vector<std::uint8_t>{}, 1);                   // unrelated frame: ignored

  ChatHub::Drain d;
  REQUIRE(hub.drain(d));
  REQUIRE(d.messages.size() == 2);
  const auto m0 = nlohmann::json::parse(d.messages[0]);
  CHECK(m0["name"] == "Bob");
  CHECK(m0["team"] == 5);
  CHECK(m0["channel"] == "team");
  CHECK(m0["self"] == false);
  CHECK(m0["text"] == "hello \"team\"\n");  // JSON-escaped on the way, intact after parsing
  const auto m1 = nlohmann::json::parse(d.messages[1]);
  CHECK(m1["self"] == true);
  CHECK(d.players_changed);
  CHECK(d.events.empty());  // the first roster is the baseline
  CHECK_FALSE(hub.drain(d));

  for (int i = 0; i < 80; ++i) hub.on_frame_message(msg_chat_message(), message(2, "Bob", P::ChatChannel::All, "spam"), 1);
  CHECK(hub.history().size() == kHistoryMax);

  hub.push_local_notice("not_connected", "Not connected");
  REQUIRE(hub.drain(d));
  CHECK(nlohmann::json::parse(d.messages.back())["code"] == "not_connected");

  hub.session_ended();
  CHECK(hub.history().empty());
  REQUIRE(hub.drain(d));
  CHECK(d.players_changed);
  CHECK(nlohmann::json::parse(hub.players_json())["players"].empty());
  hub.reset();
}
