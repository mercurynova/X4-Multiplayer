#pragma once
// features/selftest: the in-game self-test (M2-10, mod-design 8.5).
//
// Triggers: the chat command /x4mp_selftest (x4mp_saves.lua wraps ExecuteDebugCommand and sends the bridge verb
// x4mp.selftest {"v":1}) and selftest=true in x4mp.json (runs once per universe_ready). The run starts on the next frame
// (game calls are frame-thread only) and finishes kSampleFrames frames later, so the game-time check can compare two
// samples. Read-only: it changes nothing in the game.
//
// Output: one log line per check ("SELFTEST PASS  <check>  <detail>"), a summary line, the same lines as LogForward
// (through the DiagHub sender, when a session exists) and a short x4mp.notify to the player. The table is also kept in
// the DiagHub (last_selftest) so a later task can send it as one message.
//
// Checks: x4native.api, x4native.hooks, build.supported, game.adapter, saves.wrappers, saves.block, player.guard,
// game.time, team.factions (M3-08), main_thread. Verdicts: PASS FAIL WARN SKIP. A FAIL never stops the mod.

#include <atomic>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "features/diag/diag_hub.h"
#include "host/feature.h"

namespace x4mp::features {

class SelfTestFeature final : public host::IFeature {
 public:
  static constexpr int kSampleFrames = 3;

  SelfTestFeature();
  [[nodiscard]] std::string_view name() const noexcept override { return "selftest"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_universe_ready(host::HostContext& ctx) override;

  // The checks, exposed for tests. `t0`/`t1` are the two game-time samples (nullopt = unavailable).
  [[nodiscard]] static std::vector<SelfTestRow> run_checks(host::HostContext& ctx, std::optional<double> t0,
                                                           std::optional<double> t1, bool paused);

 private:
  std::shared_ptr<std::atomic<bool>> verb_request_;
  std::atomic<bool> config_request_{false};
  bool armed_ = false;
  int frames_left_ = 0;
  std::string trigger_;
  std::optional<double> t0_;
};

}  // namespace x4mp::features
