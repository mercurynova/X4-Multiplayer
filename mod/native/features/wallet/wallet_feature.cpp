#include "features/wallet/wallet_feature.h"

#include <chrono>
#include <utility>

#include "features/wallet/wallet_hub.h"
#include "host/host_log.h"

namespace x4mp::features::wallet {

namespace {

using host::Cat;
using host::Level;

constexpr std::int64_t kLogUs = 30'000'000;

std::int64_t now_us() {
  using namespace std::chrono;
  return duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
}

}  // namespace

void WalletInbox::push(std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= 64) items.erase(items.begin());  // a stuck frame thread must not grow this without bound
  items.push_back(std::move(text));
}

std::vector<std::string> WalletInbox::take() {
  const std::lock_guard lock(mutex);
  return std::exchange(items, {});
}

void WalletFeature::on_init(host::HostContext& ctx) {
  wallet_hub().reset();
  inbox_ = std::make_shared<WalletInbox>();
  auto inbox = inbox_;
  const bool ok = ctx.platform.subscribe_event("x4mp.money", [inbox](std::string_view text) {
    if (text.size() <= kMaxMoneyBody) inbox->push(std::string(text));
  });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "wallet: init (bridge verb x4mp.money {})", ok ? "subscribed" : "not available");
}

void WalletFeature::on_game_loaded(host::HostContext& ctx) {
  // A new universe means a new player account: the old balance says nothing about this one.
  wallet_hub().reset();
  last_log_us_ = 0;
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "wallet: a new universe was loaded: the balance baseline is dropped");
}

void WalletFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  universe_ready_ = info.universe_ready;
  if (!info.universe_ready || !inbox_) return;
  const std::int64_t now = now_us();
  drain(ctx, static_cast<std::uint64_t>(now));
  converge(ctx);
  if (last_log_us_ == 0 || now - last_log_us_ >= kLogUs) {
    last_log_us_ = now;
    report(ctx);
  }
}

void WalletFeature::converge(host::HostContext& ctx) {
  // M5-23 (mod-design 12.2): the ledger is the truth, the game is the copy. Nothing here decides a balance; it only makes the
  // game agree with serverBalance + this node's own unbooked deltas. A write of ours comes back as the next poll's change and is
  // recognised as our echo (M5-22), so it is never booked upward as the player's income.
  const std::optional<std::int64_t> adjust = wallet_hub().take_convergence();
  if (!adjust.has_value()) return;
  if (!ctx.game.add_player_money(*adjust)) {
    wallet_hub().note_write_failed();
    if (wallet_hub().stats().write_failed <= 3) {
      X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "wallet: cannot adjust the local money by {} (AddPlayerMoney unavailable)", *adjust);
    }
    return;
  }
  wallet_hub().note_write(*adjust);
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "wallet: local {} -> {} (target {})", wallet_hub().local_balance(),
            wallet_hub().effective_local(), wallet_hub().target_balance());
}

void WalletFeature::drain(host::HostContext& ctx, std::uint64_t now) {
  std::vector<std::string> bodies = inbox_->take();
  for (const std::string& body : bodies) {
    const std::optional<std::int64_t> value = parse_money_value(body);
    if (!value.has_value()) {
      wallet_hub().note_bad();
      continue;
    }
    if (wallet_hub().observe(*value, now) != Observation::Change) continue;
    // A change this node did not cause. M5-23 books it upward from here; for now it is logged so session 8 can read it.
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "wallet: local balance {} (change {})", wallet_hub().local_balance(),
              wallet_hub().last_change());
  }
}

void WalletFeature::report(host::HostContext& ctx) {
  const WalletHub& hub = wallet_hub();
  const WalletStats& s = hub.stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[wallet] local={} known={} changes={} echoes={} applies={} quiet={} bad={} expected={} server={} target={} pending={}/{} "
            "updates={} stale={} converged={} writes={} failed={} acked={} dropped={}",
            hub.local_balance(), hub.has_balance() ? 1 : 0, s.changes, s.echoes, s.applies, s.quiet, s.bad, hub.expected_adjust(),
            hub.server_known() ? hub.server_balance() : 0, hub.target_balance(), hub.pending_count(), hub.pending_sum(), s.updates,
            s.stale, s.converged, s.writes, s.write_failed, s.pending_acked, s.pending_dropped);
}

void WalletFeature::on_shutdown(host::HostContext& ctx) {
  report(ctx);
  inbox_.reset();
  wallet_hub().reset();
}

}  // namespace x4mp::features::wallet
