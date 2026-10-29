#pragma once
// features/wallet/wallet_feature: the bridge verb x4mp.money -> wallet_hub (M5-22; docs/m5-plan.md W6, docs/mod-design.md 12.2).
//
// The platform callback runs on the event thread, so it only copies the body into an inbox; on_frame drains it and hands the parsed
// value to wallet_hub(). A body that is not a money reading is counted, never guessed at.

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "host/feature.h"

namespace x4mp::features::wallet {

// Event thread -> frame thread. A few readings per frame is plenty; a flood is capped so a stuck frame thread cannot grow it.
struct WalletInbox {
  void push(std::string text);
  [[nodiscard]] std::vector<std::string> take();

  std::mutex mutex;
  std::vector<std::string> items;
};

class WalletFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "wallet"; }

  void on_init(host::HostContext& ctx) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  void drain(host::HostContext& ctx, std::uint64_t now_us);
  void report(host::HostContext& ctx);

  std::shared_ptr<WalletInbox> inbox_;
  bool universe_ready_ = false;
  std::int64_t last_log_us_ = 0;
};

}  // namespace x4mp::features::wallet
