#pragma once
// features/economy_client/economy_client_feature: the Lua half of the economy screen and its commands (M5-29, M5-30;
// docs/m5-plan.md W7, docs/mod-design.md 12.7).
//
// The server is the only place that decides an economy action; this feature is the postman in both directions:
//   Lua x4mp.economy {"op":"send|deposit|withdraw|repay|forgive|accept|cancel", ...} -> inbox -> hub.request() -> a frame on
//   the Control lane, retried every frame until the link takes it (the request key is fixed, so a retry books once).
//   server EconomyResult / LoanStatus / TradeStatus / TradeResult -> hub -> x4mp.economy snapshot on change only.
//
// Nothing here reads or writes the ledger, and nothing here is trusted: a refusal comes back as an EconomyResult and is
// published in the same snapshot as an acceptance, with its reason. The screen therefore never has to guess.
//
// The event thread only copies the body into an inbox; on_frame parses it on the main thread (same split as wallet).

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "host/feature.h"

namespace x4mp::features::economy_client {

// Event thread -> frame thread. A player cannot type faster than a few actions per frame; a flood is capped.
struct EconomyInbox {
  void push(std::string text);
  [[nodiscard]] std::vector<std::string> take();

  std::mutex mutex;
  std::vector<std::string> items;
};

class EconomyClientFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "economy_client"; }

  void on_init(host::HostContext& ctx) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  void drain(host::HostContext& ctx);
  // Hand every unsent frame to the link, oldest first; a refused send stops the batch and goes again next frame.
  void flush();
  // Publish the snapshot to Lua when something changed (a result, a loan, a trade), never on a quiet frame.
  void publish(host::HostContext& ctx);
  void report(host::HostContext& ctx);

  std::shared_ptr<EconomyInbox> inbox_;
  bool universe_ready_ = false;
  bool linked_prev_ = false;
  std::int64_t last_log_us_ = 0;
  bool published_ = false;
};

}  // namespace x4mp::features::economy_client
