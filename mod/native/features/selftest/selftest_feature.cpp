#include "features/selftest/selftest_feature.h"

#include <cmath>
#include <format>
#include <utility>

#include "core/version/version.h"
#include "game/main_thread.h"
#include "game/player_guard.h"
#include "host/build_check.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
SelfTestRow row(std::string name, const char* verdict, std::string detail) {
  return SelfTestRow{std::move(name), verdict, std::move(detail)};
}

const char* definition_name(game::MainThreadDefinition d) {
  switch (d) {
    case game::MainThreadDefinition::FrameUpdateThread: return "FrameUpdateThread";
    case game::MainThreadDefinition::InitThread: return "InitThread";
    case game::MainThreadDefinition::Any: return "Any";
  }
  return "?";
}

std::string join_names(const std::vector<std::string>& v) {
  std::string out;
  for (const auto& s : v) out += (out.empty() ? "" : ",") + s;
  return out;
}
}  // namespace

SelfTestFeature::SelfTestFeature() : verb_request_(std::make_shared<std::atomic<bool>>(false)) {}

void SelfTestFeature::on_init(host::HostContext& ctx) {
  auto flag = verb_request_;
  ctx.platform.subscribe_event("x4mp.selftest", [flag](std::string_view) { *flag = true; });
}

void SelfTestFeature::on_universe_ready(host::HostContext& ctx) {
  if (ctx.config.selftest) config_request_ = true;
}

void SelfTestFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  if (!armed_) {
    const bool chat = verb_request_->exchange(false);
    const bool cfg = config_request_.exchange(false);
    if (!chat && !cfg) return;
    armed_ = true;
    frames_left_ = kSampleFrames;
    trigger_ = chat ? "chat" : "config";
    t0_ = info.game_time;
    return;
  }
  if (--frames_left_ > 0) return;
  armed_ = false;
  const auto rows = run_checks(ctx, t0_, info.game_time, info.game_paused);
  // finish() needs the rows: inline it here to keep the header small.
  int pass = 0, fail = 0, warn = 0, skip = 0;
  auto& hub = diag_hub();
  ctx.log.raw(Cat::Host, Level::Info, std::format("SELFTEST begin (trigger={}, x4mp {})", trigger_, version::mod_version()));
  int forwarded = 0;
  for (const auto& r : rows) {
    if (r.verdict == "PASS") ++pass;
    else if (r.verdict == "FAIL") ++fail;
    else if (r.verdict == "WARN") ++warn;
    else ++skip;
    const std::string line = std::format("SELFTEST {:<5} {:<16} {}", r.verdict, r.name, r.detail);
    const Level lv = r.verdict == "FAIL" ? Level::Error : r.verdict == "WARN" ? Level::Warn : Level::Info;
    ctx.log.raw(Cat::Host, lv, line);
    if (hub.forward_log(lv, line)) ++forwarded;
  }
  const std::string summary = std::format("SELFTEST summary: {} PASS, {} FAIL, {} WARN, {} SKIP", pass, fail, warn, skip);
  ctx.log.raw(Cat::Host, fail ? Level::Error : Level::Info, summary);
  if (hub.forward_log(fail ? Level::Error : Level::Info, summary)) ++forwarded;
  ctx.log.raw(Cat::Host, Level::Info,
              forwarded > 0 ? std::format("SELFTEST forwarded {} lines to the server", forwarded)
                            : std::string("SELFTEST not forwarded (no session log sender)"));
  hub.set_last_selftest(rows);
  ctx.platform.raise_lua(
      "x4mp.notify", std::format(R"({{"v":1,"level":"{}","text":"X4MP self-test: {} passed, {} failed, {} warnings. Details are in x4mp.log."}})",
                                 fail ? "error" : (warn ? "warn" : "info"), pass, fail, warn));
}

std::vector<SelfTestRow> SelfTestFeature::run_checks(host::HostContext& ctx, std::optional<double> t0,
                                                     std::optional<double> t1, bool paused) {
  std::vector<SelfTestRow> out;
  const auto& fns = ctx.game.fns();
  const auto& info = ctx.game.info();

  // 1. X4Native API: versions known, how many exports resolved.
  {
    const auto missing = game::missing_exports(fns);
    const std::size_t resolved = game::kGameFnCount - missing.size();
    const std::string d = std::format("game {} x4native {} types_build {} exports {}/{}", info.game_version,
                                      info.x4native_version, info.game_types_build, resolved, game::kGameFnCount);
    out.push_back(row("x4native.api", (resolved > 0 && !info.game_version.empty()) ? "PASS" : "FAIL", d));
    // 4. adapter function presence (the list behind the count)
    out.push_back(row("game.adapter", missing.empty() ? "PASS" : "FAIL",
                      missing.empty() ? "all adapter exports present" : "missing: " + join_names(missing)));
  }
  // 2. hooks
  out.push_back(row("x4native.hooks", "PASS", "0 installed by design (no hooks, DLL not pinned)"));

  // 3. supported build
  {
    host::BuildInfo bi;
    bi.game_version = info.game_version;
    bi.version = ctx.game.game_version_struct();
    bi.build_suffix = ctx.game.build_version_suffix();
    bi.game_types_build = info.game_types_build;
    const auto bc = host::check_build(bi, version::game_build_pin());
    const char* v = bc.status == host::BuildStatus::Supported ? "PASS" : bc.status == host::BuildStatus::Unverified ? "WARN" : "FAIL";
    out.push_back(row("build.supported", v, bc.detected + " pin " + std::string(version::game_build_pin()) + ": " + bc.reason));
  }

  // 5/6. save wrappers
  {
    auto& hub = diag_hub();
    const auto s = hub.saves_status();
    if (!s.received) {
      out.push_back(row("saves.wrappers", "FAIL", "no report from Lua (x4mp_saves.lua not loaded or bridge not ready)"));
    } else {
      const bool wrapped = s.save_game_wrapped && s.is_saving_possible_wrapped;
      const bool menu = s.menu_row_patched && s.tooltip_patched;
      const char* v = !wrapped ? "FAIL" : (menu ? "PASS" : "WARN");
      out.push_back(row("saves.wrappers", v,
                        std::format("SaveGame={} IsSavingPossible={} menu_row={} tooltip={} {}", s.save_game_wrapped,
                                    s.is_saving_possible_wrapped, s.menu_row_patched, s.tooltip_patched, s.detail)));
      const bool want = hub.is_client();
      out.push_back(row("saves.block", s.blocking == want ? "PASS" : "FAIL",
                        std::format("expected {} (client={}) lua reports {}", want, want, s.blocking)));
    }
  }

  // 7. player guard
  if (!ctx.gates.universe_ready) {
    out.push_back(row("player.guard", "SKIP", "universe not ready (start menu or loading)"));
  } else {
    const auto ids = game::collect_player_guard_ids(ctx.game);
    out.push_back(row("player.guard", ids.empty() ? "WARN" : "PASS",
                      std::format("{} ids guarded (occupied ship, controlled ship, player, object, container, contexts)", ids.size())));
  }

  // 8. game time
  if (!t0 || !t1) {
    out.push_back(row("game.time", ctx.gates.game_loaded ? "FAIL" : "SKIP",
                      ctx.gates.game_loaded ? "GetCurrentGameTime unavailable" : "no game loaded"));
  } else {
    const bool sane = std::isfinite(*t0) && std::isfinite(*t1) && *t0 >= 0.0 && *t1 >= *t0;
    out.push_back(row("game.time", sane ? "PASS" : "FAIL",
                      std::format("t0={:.2f} t1={:.2f} paused={} monotonic={}", *t0, *t1, paused, *t1 >= *t0)));
  }

  // 9. main thread definition
  {
    auto& mt = game::main_thread();
    const bool ok = mt.is_main() && mt.violations() == 0;
    out.push_back(row("main_thread", ok ? "PASS" : "FAIL",
                      std::format("definition={} on_main={} violations={}", definition_name(mt.definition()), mt.is_main(),
                                  mt.violations())));
  }
  return out;
}

}  // namespace x4mp::features
