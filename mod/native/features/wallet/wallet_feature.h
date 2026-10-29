#pragma once
// features/wallet/wallet_feature: the bridge verb x4mp.money -> wallet_hub (M5-22; docs/m5-plan.md W6, docs/mod-design.md 12.2).
//
// The platform callback runs on the event thread, so it only copies the body into an inbox; on_frame drains it and hands the parsed
// value to wallet_hub(). A body that is not a money reading is counted, never guessed at.
//
// M5-24: a change the node did not cause is booked upward and sent as CreditDelta on the Control lane. The send is retried every
// frame until the link takes it, and when the link comes back after a reconnect every unacked delta goes again with its original
// key and seq - the ledger books one per key, so a resend costs nothing and a lost send is recovered.
//
// M5-25: the spendable wallet is whatever the last WalletUpdate named (wallet:<player> in PerPlayer, pool:<team> in Shared), and a
// mode switch is taken as a new wallet - see wallet_hub.h. Every 30 s a zero-amount CreditDelta asks the ledger to say what it has
// booked (mod-design 12.3), so a node whose ack was lost does not resend forever.

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
  void drain(host::HostContext& ctx, std::uint64_t now_us, std::uint64_t frame);
  // M5-23: make the local money equal serverBalance + pending, one AddPlayerMoney at a time.
  void converge(host::HostContext& ctx);
  // M5-24: hand every unsent delta to the link, oldest first; a refused send stops the batch and goes again next frame.
  void flush(const host::FrameInfo& info);
  // M5-25: every 30 s ask the ledger for its acked seq with a zero-amount CreditDelta (nothing is booked, only the ack comes back).
  void refresh(host::HostContext& ctx, std::int64_t now_us);
  void report(host::HostContext& ctx);

  std::shared_ptr<WalletInbox> inbox_;
  bool universe_ready_ = false;
  bool linked_prev_ = false;  // the link's state last frame: its rising edge arms every pending delta again
  std::int64_t last_log_us_ = 0;
  std::int64_t last_refresh_us_ = 0;  // M5-25: when the last ack probe went out
};

}  // namespace x4mp::features::wallet
