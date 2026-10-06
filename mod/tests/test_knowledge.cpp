// M3-23 knowledge probe: the MD answer decoding, the log line, the hub, and the feature over a real ModHost + FakePlatform (the fake game
// returns fixed numbers: the "MD" is the test, which fires the Lua verb x4mp.knowledge_md the way x4mp_diag.lua would).

#include <algorithm>
#include <cstdio>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include "features/diag/diag_hub.h"
#include "features/diag/knowledge_feature.h"
#include "game/main_thread.h"
#include "host/mod_host.h"
#include "host_fakes.h"

using namespace x4mp;
using namespace x4mp::features;

namespace {

std::int64_t g_clock_ns = 1'000'000'000;
std::int64_t kn_clock() noexcept { return g_clock_ns; }
double kn_time() { return 100.0; }
bool kn_paused() { return false; }
game::GameVersionPod kn_version() { return {9, 0}; }
const char* kn_suffix() { return "611726"; }

struct KFixture {
  test::TempDir dir;
  test::FakePlatform platform{dir.path};
  std::vector<std::pair<log::Level, std::string>> forwarded;
  KFixture() {
    platform.functions["GetGameVersion"] = reinterpret_cast<void*>(&kn_version);
    platform.functions["GetBuildVersionSuffix"] = reinterpret_cast<void*>(&kn_suffix);
    platform.functions["GetCurrentGameTime"] = reinterpret_cast<void*>(&kn_time);
    platform.functions["IsGamePaused"] = reinterpret_cast<void*>(&kn_paused);
    game::main_thread().reset();
    diag_hub().reset();
    knowledge_hub().reset();
    diag_hub().set_log_sender([this](log::Level l, std::string_view t) {
      forwarded.emplace_back(l, std::string(t));
      return true;
    });
  }
  ~KFixture() {
    diag_hub().reset();
    knowledge_hub().reset();
    game::main_thread().reset();
  }
};

struct KRig {
  KFixture& f;
  std::unique_ptr<host::ModHost> host;
  explicit KRig(KFixture& fx) : f(fx) {
    host::HostOptions o;
    o.clock = &kn_clock;
    o.register_features = [](host::FeatureRegistry& r) { r.add(std::make_unique<KnowledgeFeature>()); };
    host = std::make_unique<host::ModHost>(f.platform, o);
    REQUIRE(host->init() == host::InitResult::Started);
  }
  void frames(int n) {
    for (int i = 0; i < n; ++i) {
      g_clock_ns += 16'000'000;
      host->on_frame();
    }
  }
  void load_save() {
    host->on_game_loaded();
    host->on_universe_ready();
  }
  // What x4mp_diag.lua does with the MD answer: the verb x4mp.knowledge_md {"v":1,"data":"..."}
  void md_answers(std::uint32_t seq, const std::string& tail = "3;7;2;4;5;9;1;6;01_001=1,07_001=0,14_001=-") {
    nlohmann::json j;
    j["v"] = 1;
    j["data"] = "K;" + std::to_string(seq) + ";123.4;" + tail;
    f.platform.fire("x4mp.knowledge_md", j.dump());
  }
  [[nodiscard]] std::vector<std::uint32_t> asked_seqs() const {
    std::vector<std::uint32_t> out;
    for (const auto& t : f.platform.raised_named("x4mp.knowledge_ask")) out.push_back(nlohmann::json::parse(t)["seq"].get<std::uint32_t>());
    return out;
  }
  [[nodiscard]] std::string log_text() const {
    host->log()->flush();
    return f.dir.read("logs/x4mp.log");
  }
};

}  // namespace

TEST_CASE("knowledge: the MD answer decodes and formats as one log line", "[knowledge][m323]") {
  const auto c = parse_knowledge("K;5;987.6;12;105;4;20;7;980;30;320;01_001=1,07_001=0,14_001=-");
  REQUIRE(c);
  CHECK(c->seq == 5);
  CHECK(c->sectors_known == 12);
  CHECK(c->sectors == 105);
  CHECK(c->clusters_known == 4);
  CHECK(c->stations == 980);
  CHECK(c->gates_known == 30);
  REQUIRE(c->samples.size() == 3);
  CHECK(c->samples[2] == std::make_pair(std::string("14_001"), '-'));
  CHECK(format_knowledge_line(*c, "after janitor sweep") ==
        "knowledge: sectors_known=12/105 stations_known=7/980 gates_known=30/320 clusters_known=4/20 samples=[01_001=1,07_001=0,14_001=-] "
        "at='after janitor sweep' game_age=987.6s");
}

TEST_CASE("knowledge: a damaged MD answer is refused", "[knowledge][m323]") {
  CHECK_FALSE(parse_knowledge(""));
  CHECK_FALSE(parse_knowledge("N;"));
  CHECK_FALSE(parse_knowledge("K;1;2;3"));
  CHECK_FALSE(parse_knowledge("K;x;1.0;1;1;1;1;1;1;1;1;a=1"));
  CHECK_FALSE(parse_knowledge("K;1;1.0;1;1;1;1;1;1;1;1;a=2"));  // a sample is 0, 1 or -
  CHECK(parse_knowledge("K;1;1.0;1;1;1;1;1;1;1;1;"));          // no samples is fine
}

TEST_CASE("knowledge: the universe-ready probe is asked first, the answers are logged with the tag they were asked with", "[knowledge][m323]") {
  KFixture f;
  KRig r(f);
  r.load_save();
  REQUIRE(r.asked_seqs() == std::vector<std::uint32_t>{1});
  knowledge_probe("takeover: avatar spawned");
  knowledge_probe("takeover: teleported");
  knowledge_probe("takeover: guard confirmed");
  knowledge_probe("takeover: original removed");
  knowledge_probe("after janitor sweep");
  knowledge_probe("after first ghost spawn");
  REQUIRE(r.asked_seqs() == std::vector<std::uint32_t>{1, 2, 3, 4, 5, 6, 7});
  for (std::uint32_t s = 1; s <= 7; ++s) r.md_answers(s);
  r.frames(1);
  const std::string log = r.log_text();
  std::size_t at = 0;
  for (const char* tag : {"at='universe ready'", "at='takeover: avatar spawned'", "at='takeover: teleported'", "at='takeover: guard confirmed'",
                          "at='takeover: original removed'", "at='after janitor sweep'", "at='after first ghost spawn'"}) {
    const auto i = log.find(tag, at);
    INFO(tag);
    REQUIRE(i != std::string::npos);
    at = i;
  }
  CHECK(log.find("knowledge: sectors_known=3/7 stations_known=5/9 gates_known=1/6 clusters_known=2/4 samples=[01_001=1,07_001=0,14_001=-]") != std::string::npos);
  CHECK(std::ranges::any_of(f.forwarded, [](const auto& p) { return p.second.find("[knowledge] knowledge: sectors_known=3/7") != std::string::npos; }));
}

TEST_CASE("knowledge: a /reloadui universe is labelled, a probe asked before the feature is up is sent at its first frame", "[knowledge][m323]") {
  KFixture f;
  knowledge_probe("early");  // no sender yet: queued
  KRig r(f);
  CHECK(r.asked_seqs().empty());
  r.frames(1);
  CHECK(r.asked_seqs() == std::vector<std::uint32_t>{1});
  r.md_answers(1);
  r.frames(1);
  CHECK(r.log_text().find("at='early'") != std::string::npos);
}

TEST_CASE("knowledge: the chat command asks a probe; an unreadable or unknown answer is logged, never fatal", "[knowledge][m323]") {
  KFixture f;
  KRig r(f);
  r.frames(1);
  f.platform.fire("x4mp.knowledge_cmd", R"({"v":1})");
  r.frames(1);
  REQUIRE(r.asked_seqs() == std::vector<std::uint32_t>{1});
  r.md_answers(1);
  r.md_answers(99);  // a sequence nobody asked for: still logged, tag '?'
  f.platform.fire("x4mp.knowledge_md", "not json");
  f.platform.fire("x4mp.knowledge_md", R"({"v":1,"data":"K;1;2"})");
  r.frames(1);
  const std::string log = r.log_text();
  CHECK(log.find("at='chat command /x4mp knowledge'") != std::string::npos);
  CHECK(log.find("at='?'") != std::string::npos);
  CHECK(log.find("unreadable") != std::string::npos);
}

// ---- M3-28 watch --------------------------------------------------------------------------------------------------------------------------------
namespace {
const char* kWatchAnswer =
    "W;%u;456.7;cluster_22_sector001;ABC-123;none;3|DEF-456,x4mp_team_1,1.0,1,0,0,0,1,cluster_22_sector001|GHI-789,x4mp_team_2,0.0,0,1,0,1,1,cluster_04_sector001";
std::string watch_answer(std::uint32_t seq) {
  char buf[512];
  std::snprintf(buf, sizeof buf, kWatchAnswer, seq);
  return buf;
}
}  // namespace

TEST_CASE("knowledge watch: the W answer decodes; damaged answers are refused", "[knowledge][m328]") {
  const auto t = parse_watch(watch_answer(7));
  REQUIRE(t);
  CHECK(t->seq == 7);
  CHECK(t->age_s == 456.7);
  CHECK(t->player_sector == "cluster_22_sector001");
  CHECK(t->player_ship == "ABC-123");
  CHECK(t->undiscovered == "none");
  CHECK(t->total == 3);
  REQUIRE(t->ships.size() == 2);
  CHECK(t->ships[0].idcode == "DEF-456");
  CHECK(t->ships[0].owner == "x4mp_team_1");
  CHECK(t->ships[0].relation == "1.0");
  CHECK(t->ships[0].radar == '1');
  CHECK(t->ships[0].live == '0');
  CHECK(t->ships[1].live == '1');
  CHECK(t->ships[1].active == '1');
  CHECK(parse_watch("W;1;1.0;-;-;-;0"));  // no ships is fine
  CHECK_FALSE(parse_watch("K;1;1.0;-;-;-;0"));
  CHECK_FALSE(parse_watch("W;1;1.0;-;-;0"));                       // header too short
  CHECK_FALSE(parse_watch("W;x;1.0;-;-;-;0"));
  CHECK_FALSE(parse_watch("W;1;1.0;-;-;-;1|A,b,0,1,1,1,1"));      // ship with too few fields
  CHECK_FALSE(parse_watch("W;1;1.0;-;-;-;1|A,b,0,1,1,1,1,2,s"));   // a flag is 0, 1 or -
}

TEST_CASE("knowledge watch: one header line per tick, a ship line only when its state changed (or on a full tick)", "[knowledge][m328]") {
  std::map<std::string, std::string> last;
  const auto t = *parse_watch(watch_answer(1));
  auto lines = format_watch_lines(t, last, false);
  REQUIRE(lines.size() == 3);
  CHECK(lines[0] == "knowledge-watch: age=456.7s psector=cluster_22_sector001 pship=ABC-123 undisc2km=none ships=2/3");
  CHECK(lines[1] == "knowledge-watch:   DEF-456 own=t1 rel=1.0 radar=1 live=0 gravidar=0 active=0 known=1 sec=cluster_22_sector001");
  CHECK(lines[2].find("GHI-789 own=t2 rel=0.0 radar=0 live=1 gravidar=0 active=1 known=1 sec=cluster_04_sector001") != std::string::npos);
  lines = format_watch_lines(t, last, false);
  CHECK(lines.size() == 1);  // unchanged: the header only
  auto t2 = t;
  t2.ships[0].live = '1';  // the flip we are hunting
  lines = format_watch_lines(t2, last, false);
  REQUIRE(lines.size() == 2);
  CHECK(lines[1].find("DEF-456") != std::string::npos);
  CHECK(lines[1].find("live=1") != std::string::npos);
  CHECK(format_watch_lines(t2, last, true).size() == 3);  // a full tick repeats everything
}

TEST_CASE("knowledge watch: schedule asks once per period, never two outstanding questions, switches itself off", "[knowledge][m328]") {
  WatchSchedule s;
  CHECK_FALSE(s.due(0));
  s.set(true, 10.0);
  CHECK(s.due(10.0));
  s.asked(10.0);
  CHECK_FALSE(s.due(12.5));  // no answer yet, within the timeout
  s.answered();
  CHECK_FALSE(s.due(11.0));  // answered, but not due before 2 s
  CHECK(s.due(12.0));
  s.asked(12.0);
  CHECK_FALSE(s.due(15.0));  // outstanding
  CHECK(s.due(18.5));        // no answer for 6 s: asks again
  s.set(false, 20.0);
  CHECK_FALSE(s.due(25.0));
  s.set(true, 100.0);
  CHECK(s.due(100.0));
  CHECK_FALSE(s.due(100.0 + kWatchAutoOffS + 1));
  CHECK_FALSE(s.on());
}

TEST_CASE("knowledge watch: the chat verb toggles it, MD is asked every 2 s, answers are logged and forwarded, off stops it", "[knowledge][m328]") {
  KFixture f;
  KRig r(f);
  r.frames(1);
  const auto asks = [&] { return f.platform.raised_named("x4mp.knowledge_watch").size(); };
  CHECK(asks() == 0);
  f.platform.fire("x4mp.knowledge_cmd", R"({"v":1,"watch":"toggle"})");
  r.frames(1);
  REQUIRE(asks() == 1);
  CHECK(nlohmann::json::parse(f.platform.raised_named("x4mp.knowledge_watch")[0])["max"] == kWatchMaxShips);
  nlohmann::json a;
  a["v"] = 1;
  a["data"] = watch_answer(1);
  f.platform.fire("x4mp.knowledge_md", a.dump());
  r.frames(1);
  CHECK(asks() == 1);  // within 2 s: no new question (16 ms frames)
  r.frames(130);       // > 2 s of frames
  CHECK(asks() == 2);
  const std::string log = r.log_text();
  CHECK(log.find("knowledge-watch: ON") != std::string::npos);
  CHECK(log.find("knowledge-watch: age=456.7s psector=cluster_22_sector001") != std::string::npos);
  CHECK(log.find("knowledge-watch:   DEF-456 own=t1") != std::string::npos);
  CHECK(std::ranges::any_of(f.forwarded, [](const auto& p) { return p.second.find("[knowledge] knowledge-watch: age=456.7s") != std::string::npos; }));
  f.platform.fire("x4mp.knowledge_cmd", R"({"v":1,"watch":"off"})");
  r.frames(200);
  CHECK(asks() == 2);
  CHECK(r.log_text().find("knowledge-watch: OFF") != std::string::npos);
  // the plain command is unchanged and the one-shot probe still works with the watch off
  f.platform.fire("x4mp.knowledge_cmd", R"({"v":1})");
  r.frames(1);
  CHECK_FALSE(r.asked_seqs().empty());
}

TEST_CASE("knowledge: without a Lua bridge the probe says so instead of failing", "[knowledge][m323]") {
  KFixture f;
  f.platform.lua_ok = false;
  KRig r(f);
  r.load_save();
  CHECK(r.asked_seqs().empty());
  CHECK(r.log_text().find("not sent (the Lua bridge is not available)") != std::string::npos);
}
