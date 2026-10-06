// M3-08: features/teams: the team model from the server frames, the matrix -> plan -> MD call list mapping, and the apply state machine of the hub.
#include <catch2/catch_test_macros.hpp>

#include <nlohmann/json.hpp>

#include "control_generated.h"
#include "features/teams/team_hub.h"
#include "features/teams/team_plan.h"
#include "message_ids_generated.h"
#include "teams_generated.h"

using namespace x4mp::features::teams;
namespace P = X4MP::Proto;

namespace {
struct TeamSpec {
  std::uint16_t id;
  std::uint8_t slot;
  std::vector<std::uint16_t> members;
};
struct RelSpec {
  std::uint16_t a, b;
  P::TeamRelation rel;
};

using Buf = std::vector<std::uint8_t>;

Buf finish(flatbuffers::FlatBufferBuilder& fbb) { return {fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize()}; }

flatbuffers::Offset<P::TeamTable> table_offset(flatbuffers::FlatBufferBuilder& fbb, bool full, const std::vector<TeamSpec>& teams,
                                               const std::vector<std::uint16_t>& removed = {}) {
  std::vector<flatbuffers::Offset<P::TeamInfo>> infos;
  for (const auto& t : teams) {
    std::vector<flatbuffers::Offset<P::TeamMember>> members;
    for (const auto m : t.members) {
      const auto name = fbb.CreateString("p" + std::to_string(m));
      P::TeamMemberBuilder mb(fbb);
      mb.add_player_id(m);
      mb.add_name(name);
      members.push_back(mb.Finish());
    }
    const auto name = fbb.CreateString("Team " + std::to_string(t.id));
    const auto mv = fbb.CreateVector(members);
    P::TeamInfoBuilder b(fbb);
    b.add_team_id(t.id);
    b.add_name(name);
    b.add_faction_slot(t.slot);
    b.add_members(mv);
    infos.push_back(b.Finish());
  }
  const auto tv = fbb.CreateVector(infos);
  const auto rv = fbb.CreateVector(removed);
  P::TeamTableBuilder tb(fbb);
  tb.add_full(full);
  tb.add_teams(tv);
  tb.add_removed(rv);
  return tb.Finish();
}

flatbuffers::Offset<P::TeamRelations> relations_offset(flatbuffers::FlatBufferBuilder& fbb, bool full, P::TeamRelation def,
                                                       const std::vector<RelSpec>& entries) {
  std::vector<flatbuffers::Offset<P::TeamRelationEntry>> es;
  for (const auto& e : entries) {
    P::TeamRelationEntryBuilder b(fbb);
    b.add_team_a(e.a);
    b.add_team_b(e.b);
    b.add_relation(e.rel);
    es.push_back(b.Finish());
  }
  const auto ev = fbb.CreateVector(es);
  P::TeamRelationsBuilder rb(fbb);
  rb.add_full(full);
  rb.add_default_relation(def);
  rb.add_entries(ev);
  return rb.Finish();
}

Buf welcome(std::uint16_t self, std::uint16_t team, const std::vector<TeamSpec>& teams, P::TeamRelation def, const std::vector<RelSpec>& rels) {
  flatbuffers::FlatBufferBuilder fbb(512);
  const auto t = table_offset(fbb, true, teams);
  const auto r = relations_offset(fbb, true, def, rels);
  P::WelcomeBuilder b(fbb);
  b.add_player_id(self);
  b.add_team_id(team);
  b.add_teams(t);
  b.add_relations(r);
  fbb.Finish(b.Finish());
  return finish(fbb);
}

Buf relations_frame(bool full, P::TeamRelation def, const std::vector<RelSpec>& rels) {
  flatbuffers::FlatBufferBuilder fbb(128);
  fbb.Finish(relations_offset(fbb, full, def, rels));
  return finish(fbb);
}

Buf table_frame(bool full, const std::vector<TeamSpec>& teams, const std::vector<std::uint16_t>& removed = {}) {
  flatbuffers::FlatBufferBuilder fbb(256);
  fbb.Finish(table_offset(fbb, full, teams, removed));
  return finish(fbb);
}

Buf member_changed(std::uint16_t player, std::uint16_t from, std::uint16_t to) {
  flatbuffers::FlatBufferBuilder fbb(64);
  P::TeamMemberChangedBuilder b(fbb);
  b.add_player_id(player);
  b.add_from_team(from);
  b.add_to_team(to);
  fbb.Finish(b.Finish());
  return finish(fbb);
}

constexpr auto kTable = static_cast<std::uint16_t>(P::MsgType::TeamTable);
constexpr auto kRelations = static_cast<std::uint16_t>(P::MsgType::TeamRelations);
constexpr auto kMember = static_cast<std::uint16_t>(P::MsgType::TeamMemberChanged);

// two teams, self (player 7) in team 10 = slot 1, team 11 = slot 2 hostile to team 10
Buf two_team_welcome() {
  return welcome(7, 10, {{10, 1, {7}}, {11, 2, {8}}}, P::TeamRelation::Neutral, {{10, 11, P::TeamRelation::Hostile}});
}

std::vector<std::string> texts(const std::vector<Call>& calls) {
  std::vector<std::string> out;
  for (const auto& c : calls) out.push_back(call_text(c));
  return out;
}
}  // namespace

TEST_CASE("teams: relation values follow ADR-016", "[teams]") {
  CHECK(relation_value(Rel::Allied) == 0.75);
  CHECK(relation_value(Rel::Neutral) == 0.0);
  CHECK(relation_value(Rel::Hostile) == -1.0);
}

TEST_CASE("teams: a welcome with two teams gives slots, the team pair and the player-vs-team values", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(two_team_welcome()));
  CHECK(m.own_team() == 10);
  const Plan p = m.plan();
  CHECK(p.slots == std::vector<std::uint8_t>{1, 2});
  CHECK(p.own_slot == 1);
  REQUIRE(p.relations.size() == 3);
  CHECK(p.relations[0] == RelationTriple{1, 2, -1.0});   // team 1 <-> team 2 hostile
  CHECK(p.relations[1] == RelationTriple{0, 1, 1.0});    // player <-> own team +1.0
  CHECK(p.relations[2] == RelationTriple{0, 2, -1.0});   // player <-> the hostile team
}

TEST_CASE("teams: allied pair = +0.75, unlisted pairs use the default relation", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(welcome(7, 10, {{10, 1, {7}}, {11, 2, {}}, {12, 3, {}}}, P::TeamRelation::Neutral, {{11, 10, P::TeamRelation::Allied}})));
  const Plan p = m.plan();
  REQUIRE(p.relations.size() == 6);
  CHECK(p.relations[0] == RelationTriple{1, 2, 0.75});  // key order does not matter (11,10 == 10,11)
  CHECK(p.relations[1] == RelationTriple{1, 3, 0.0});
  CHECK(p.relations[2] == RelationTriple{2, 3, 0.0});
  CHECK(p.relations[4] == RelationTriple{0, 2, 0.75});
  CHECK(p.relations[5] == RelationTriple{0, 3, 0.0});
}

TEST_CASE("teams: a one-team session has one slot and only the own-team value for player", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(welcome(7, 1, {{1, 1, {7}}}, P::TeamRelation::Neutral, {})));
  const Plan p = m.plan();
  CHECK(p.slots == std::vector<std::uint8_t>{1});
  REQUIRE(p.relations.size() == 1);
  CHECK(p.relations[0] == RelationTriple{0, 1, 1.0});
}

TEST_CASE("teams: without an own team nothing about player is planned; teams without a slot are ignored", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(welcome(7, 0, {{10, 1, {}}, {11, 2, {}}, {12, 0, {7}}}, P::TeamRelation::Neutral, {})));
  // player 7 is a member of team 12, which has no slot: no player triples
  const Plan p = m.plan();
  CHECK(p.slots == std::vector<std::uint8_t>{1, 2});
  CHECK(p.own_slot == 0);
  REQUIRE(p.relations.size() == 1);
  CHECK(p.relations[0].a == 1);
  CHECK(p.relations[0].b == 2);
}

TEST_CASE("teams: the call list unlocks, activates, sets both directions and relocks; player is never locked", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(two_team_welcome()));
  const auto calls = texts(build_calls(m.plan()));
  const std::vector<std::string> expected = {"unlock 1",        "unlock 2",        "activate 1",      "known 1",         "activate 2",      "known 2",
                                             "relation 1 2 -1.00", "relation 2 1 -1.00", "relation 0 1 1.00", "relation 1 0 1.00",
                                             "relation 0 2 -1.00", "relation 2 0 -1.00", "lock 1",          "lock 2"};
  CHECK(calls == expected);
  for (const auto& c : calls) CHECK(c != "lock 0");
  CHECK(build_calls(Plan{}).empty());
}

TEST_CASE("teams: a TeamRelations delta changes one pair; a table delta adds and removes teams", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(two_team_welcome()));
  REQUIRE(m.apply_frame(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{11, 10, P::TeamRelation::Allied}}), 7));
  CHECK(m.plan().relations[0] == RelationTriple{1, 2, 0.75});
  CHECK(m.plan().relations[2] == RelationTriple{0, 2, 0.75});
  REQUIRE(m.apply_frame(kTable, table_frame(false, {{12, 3, {9}}}), 7));
  CHECK(m.plan().slots == std::vector<std::uint8_t>{1, 2, 3});
  REQUIRE(m.apply_frame(kTable, table_frame(false, {}, {11}), 7));
  CHECK(m.plan().slots == std::vector<std::uint8_t>{1, 3});
  REQUIRE(m.apply_frame(kRelations, relations_frame(true, P::TeamRelation::Hostile, {}), 7));  // a full matrix replaces everything
  CHECK(m.plan().relations[0] == RelationTriple{1, 3, -1.0});
}

TEST_CASE("teams: TeamMemberChanged for the own player moves the own team; others do not", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(two_team_welcome()));
  CHECK_FALSE(m.apply_frame(kMember, member_changed(8, 11, 10), 7));
  CHECK(m.own_team() == 10);
  CHECK(m.apply_frame(kMember, member_changed(7, 10, 11), 7));
  CHECK(m.own_team() == 11);
  const Plan p = m.plan();
  CHECK(p.own_slot == 2);
  CHECK(p.relations[1] == RelationTriple{0, 1, -1.0});
  CHECK(p.relations[2] == RelationTriple{0, 2, 1.0});
}

TEST_CASE("teams: garbage and unrelated frames are ignored", "[teams]") {
  TeamModel m;
  CHECK_FALSE(m.apply_welcome(Buf{1, 2, 3}));
  CHECK_FALSE(m.apply_welcome(Buf(64, 0xFF)));
  REQUIRE(m.apply_welcome(two_team_welcome()));
  CHECK_FALSE(m.apply_frame(kRelations, Buf(64, 0xAB), 7));
  CHECK_FALSE(m.apply_frame(0x0999, two_team_welcome(), 7));
  CHECK(m.plan().slots.size() == 2);
}

TEST_CASE("teams: plan_json is what the Lua side flattens", "[teams]") {
  TeamModel m;
  REQUIRE(m.apply_welcome(two_team_welcome()));
  const auto j = nlohmann::json::parse(plan_json(m.plan(), 3, "welcome"));
  CHECK(j["v"] == 1);
  CHECK(j["seq"] == 3);
  CHECK(j["reason"] == "welcome");
  CHECK(j["own"] == 1);
  CHECK(j["slots"] == nlohmann::json::array({1, 2}));
  REQUIRE(j["rel"].size() == 3);
  CHECK(j["rel"][0] == nlohmann::json::array({1, 2, -1.0}));
  CHECK(j["rel"][1] == nlohmann::json::array({0, 1, 1.0}));
}

TEST_CASE("teams (M3-28): diag.team_self_relation_099 writes 0.99 for player <-> own team only; skip_known rides in the payload", "[teams][m328]") {
  CHECK(own_team_value(false) == 1.0);
  CHECK(own_team_value(true) == 0.99);
  TeamModel m;
  REQUIRE(m.apply_welcome(two_team_welcome()));
  const Plan def = m.plan();
  CHECK(def.relations[1] == RelationTriple{0, 1, 1.0});
  const Plan diag = m.plan(own_team_value(true));
  REQUIRE(diag.relations.size() == def.relations.size());
  CHECK(diag.relations[0] == def.relations[0]);                   // team <-> team untouched
  CHECK(diag.relations[1] == RelationTriple{0, 1, 0.99});         // player <-> own team
  CHECK(diag.relations[2] == def.relations[2]);                   // player <-> the other team untouched
  // the call list writes the value in both directions
  const auto calls = texts(build_calls(diag));
  CHECK(std::find(calls.begin(), calls.end(), "relation 0 1 0.99") != calls.end());
  CHECK(std::find(calls.begin(), calls.end(), "relation 1 0 0.99") != calls.end());
  // payload
  CHECK_FALSE(nlohmann::json::parse(plan_json(diag, 1, "x")).contains("skip_known"));
  CHECK(nlohmann::json::parse(plan_json(diag, 1, "x", true))["skip_known"] == true);
  CHECK(nlohmann::json::parse(plan_json(diag, 1, "x", true))["rel"][1][2] == 0.99);
}

TEST_CASE("teams hub (M3-28): the diag values reach the pending plan", "[teams][m328]") {
  TeamHub hub;
  hub.set_diag(own_team_value(true), true);
  hub.on_welcome(two_team_welcome());
  const auto p = hub.poll(true, 1, 0.016);
  REQUIRE(p);
  CHECK(p->skip_known);
  CHECK(p->plan.relations[1] == RelationTriple{0, 1, 0.99});
}

// ---- hub -------------------------------------------------------------------------------------------------------------------------------

TEST_CASE("teams hub: setup waits for the universe, applies once, re-applies a relation change in the same poll", "[teams]") {
  TeamHub hub;
  CHECK_FALSE(hub.has_session());
  hub.on_welcome(two_team_welcome());
  CHECK(hub.has_session());
  CHECK_FALSE(hub.poll(false, 0, 0.016));          // universe not ready: nothing, the change stays pending
  const auto first = hub.poll(true, 1, 0.016);
  REQUIRE(first);
  CHECK(first->seq == 1);
  CHECK(first->reason == "universe_ready");
  CHECK(first->plan.slots.size() == 2);
  CHECK(hub.state() == SetupState::Starting);
  CHECK_FALSE(hub.poll(true, 1, 0.016));          // nothing changed
  CHECK(hub.faction_of_team(10) == "x4mp_team_1");
  CHECK(hub.faction_of_team(11) == "x4mp_team_2");
  CHECK(hub.faction_of_team(99).empty());

  // the MD answers: Ok
  CHECK(hub.on_md_report("R;1;2;0;0;2;3"));
  CHECK(hub.state() == SetupState::Ok);
  CHECK(hub.factions_ready());

  // a TeamRelations delta (hostile -> allied) arrives in the join pump; the very next poll (same frame) yields the new plan
  hub.on_frame_message(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{10, 11, P::TeamRelation::Allied}}), 7);
  const auto second = hub.poll(true, 1, 0.016);
  REQUIRE(second);
  CHECK(second->seq == 2);
  CHECK(second->reason == "teams");
  CHECK(second->plan.relations[0].value == 0.75);
  CHECK(hub.state() == SetupState::Starting);
  CHECK_FALSE(hub.on_md_report("R;1;2;0;0;2;3"));  // the old answer is stale
  CHECK(hub.state() == SetupState::Starting);
  CHECK(hub.on_md_report("R;2;2;0;0;2;3"));
  CHECK(hub.state() == SetupState::Ok);
}

TEST_CASE("teams hub: a frame that does not change the plan sends nothing; a new universe epoch re-applies", "[teams]") {
  TeamHub hub;
  hub.on_welcome(two_team_welcome());
  REQUIRE(hub.poll(true, 1, 0.016));
  // a relation between two teams that do not exist: the plan is the same
  hub.on_frame_message(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{50, 51, P::TeamRelation::Allied}}), 7);
  CHECK_FALSE(hub.poll(true, 1, 0.016));
  const auto again = hub.poll(true, 2, 0.016);  // a new universe was loaded
  REQUIRE(again);
  CHECK(again->reason == "universe_ready");
}

TEST_CASE("teams hub: a Welcome (resume) always sends again; session end resets", "[teams]") {
  TeamHub hub;
  hub.on_welcome(two_team_welcome());
  REQUIRE(hub.poll(true, 1, 0.016));
  hub.on_welcome(two_team_welcome());
  const auto p = hub.poll(true, 1, 0.016);
  REQUIRE(p);
  CHECK(p->reason == "welcome");
  hub.session_ended();
  CHECK_FALSE(hub.has_session());
  CHECK(hub.state() == SetupState::Unknown);
  CHECK_FALSE(hub.poll(true, 5, 0.016));
}

TEST_CASE("teams hub: reports with problems fail the setup; no report in time fails it too; a late report recovers", "[teams]") {
  TeamHub hub;
  hub.on_welcome(two_team_welcome());
  REQUIRE(hub.poll(true, 1, 0.016));
  CHECK(hub.on_md_report("R;1;1;0;0;2;3"));  // only one faction active
  CHECK(hub.state() == SetupState::Failed);
  CHECK(hub.detail().find("active=1/2") != std::string::npos);

  hub.on_frame_message(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{10, 11, P::TeamRelation::Neutral}}), 7);
  REQUIRE(hub.poll(true, 1, 0.016));
  CHECK(hub.on_md_report("R;2;2;1;0;2;3"));  // a relation did not read back
  CHECK(hub.state() == SetupState::Failed);

  hub.on_frame_message(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{10, 11, P::TeamRelation::Allied}}), 7);
  REQUIRE(hub.poll(true, 1, 0.016));
  CHECK(hub.on_md_report("R;3;2;0;1;2;3"));  // player ended up locked
  CHECK(hub.state() == SetupState::Failed);

  hub.on_frame_message(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{10, 11, P::TeamRelation::Hostile}}), 7);
  REQUIRE(hub.poll(true, 1, 0.016));
  CHECK(hub.on_md_report("E;4;bad_payload"));
  CHECK(hub.state() == SetupState::Failed);
  CHECK(hub.detail() == "md_error:bad_payload");

  hub.on_frame_message(kRelations, relations_frame(false, P::TeamRelation::Neutral, {{10, 11, P::TeamRelation::Neutral}}), 7);
  REQUIRE(hub.poll(true, 1, 0.016));
  CHECK(hub.state() == SetupState::Starting);
  CHECK_FALSE(hub.poll(true, 1, TeamHub::kReportTimeoutS - 1.0));
  CHECK(hub.state() == SetupState::Starting);
  CHECK_FALSE(hub.poll(true, 1, 2.0));
  CHECK(hub.state() == SetupState::Failed);
  CHECK(hub.detail() == "no_md_report");
  CHECK(hub.on_md_report("R;5;2;0;0;2;3"));  // late but right
  CHECK(hub.state() == SetupState::Ok);
}

TEST_CASE("teams hub: a failed raise_lua is retried on the next poll", "[teams]") {
  TeamHub hub;
  hub.on_welcome(two_team_welcome());
  REQUIRE(hub.poll(true, 1, 0.016));
  hub.send_failed();
  const auto retry = hub.poll(true, 1, 0.016);
  REQUIRE(retry);
  CHECK(retry->seq == 2);
}

TEST_CASE("teams: MD report parsing", "[teams]") {
  CHECK(parse_md_report("R;3;2;0;0;2;3").valid);
  CHECK_FALSE(parse_md_report("R;3;2;0;0;2").valid);
  CHECK_FALSE(parse_md_report("R;a;2;0;0;2;3").valid);
  CHECK_FALSE(parse_md_report("").valid);
  const auto e = parse_md_report("E;7;no_slots");
  CHECK(e.valid);
  CHECK(e.error);
  CHECK(e.seq == 7);
}
