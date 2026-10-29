#include "features/wallet/wallet_feature.h"

#include <chrono>
#include <utility>

#include "features/events/events_hub.h"
#include "features/wallet/wallet_hub.h"
#include "features/wallet/wallet_wire.h"
#include "host/host_log.h"

namespace x4mp::features::wallet {

namespace {

using host::Cat;
using host::Level;

constexpr std::int64_t kLogUs = 30'000'000;
// M5-25 (mod-design 12.3): how often a node asks the ledger to say what it has booked, even when nothing happened.
constexpr std::int64_t kRefreshUs = 30'000'000;
// A frame that books a burst must not send a burst: the rest goes in the following frames, in order.
constexpr std::size_t kMaxSendsPerFrame = 8;

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
  // A new universe means a new player account: the old balance says nothing about this one, and an unbooked delta of the old
  // universe cannot be booked into the new one (the ledger it belonged to is gone with the save).
  wallet_hub().reset();
  linked_prev_ = false;
  last_log_us_ = 0;
  last_refresh_us_ = 0;  // the new session gets its balances on attach, so the first probe waits a full period
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "wallet: a new universe was loaded: the balance baseline is dropped");
}

void WalletFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  universe_ready_ = info.universe_ready;
  if (!info.universe_ready || !inbox_) return;
  const std::int64_t now = now_us();
  // M5-24: the link's rising edge is the reconnect. Every delta the ledger has not acked goes again with its original key and seq,
  // so a reconnect in the middle of a purchase loses nothing and the server still books each delta once (economy.fbs:53-54).
  const bool linked = events::events_hub().linked();
  if (linked && !linked_prev_) {
    wallet_hub().resend_all();
    if (wallet_hub().pending_count() > 0) {
      X4MP_CLOG(ctx.log, Cat::Net, Level::Info, "wallet: the link is back, {} unacked delta(s) go again",
                wallet_hub().pending_unsent());
    }
  }
  linked_prev_ = linked;
  drain(ctx, static_cast<std::uint64_t>(now), info.frame_index);
  refresh(ctx, now);
  flush(info);
  converge(ctx);
  if (last_log_us_ == 0 || now - last_log_us_ >= kLogUs) {
    last_log_us_ = now;
    report(ctx);
  }
}

void WalletFeature::flush(const host::FrameInfo& info) {
  if (!events::events_hub().linked()) return;
  for (const PendingDelta& row : wallet_hub().take_unsent(kMaxSendsPerFrame)) {
    CreditDeltaData data;
    data.key_lo = row.key_lo;
    data.key_hi = row.key_hi;
    data.amount = row.delta;
    data.source = row.source;
    data.ref_event_seq = row.ref_event_seq;
    data.game_time = info.game_time.value_or(0.0);
    data.seq = row.seq;
    const std::vector<std::uint8_t> bytes = encode_credit_delta(data);
    if (!events::events_hub().send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::CreditDelta), bytes)) {
      wallet_hub().note_send_failed();
      return;  // the same row is first in line next frame, so the order the deltas were booked in is kept
    }
    wallet_hub().mark_sent(row.seq);
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

void WalletFeature::drain(host::HostContext& ctx, std::uint64_t now, std::uint64_t frame) {
  std::vector<std::string> bodies = inbox_->take();
  for (const std::string& body : bodies) {
    const std::optional<std::int64_t> value = parse_money_value(body);
    if (!value.has_value()) {
      wallet_hub().note_bad();
      continue;
    }
    if (wallet_hub().observe(*value, now) != Observation::Change) continue;
    // M5-24: a change this node did not cause is income or spending the ledger has not seen yet, so it is booked upward with the
    // cause the events of this frame window named (a trade, a finished build), and sent as one CreditDelta on the next flush.
    const ReasonHint reason = wallet_hub().take_reason(frame);
    const std::uint16_t player = events::events_hub().self_player_id();
    const std::optional<PendingDelta> row = wallet_hub().book_upward(wallet_hub().last_change(), reason, player);
    if (!row.has_value()) continue;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "wallet: change {} booked as seq {} (source {})", row->delta, row->seq,
              static_cast<int>(row->source));
  }
}

void WalletFeature::refresh(host::HostContext& ctx, std::int64_t now) {
  // M5-25 (mod-design 12.3): the ledger is asked, it does not volunteer. A zero-amount CreditDelta books nothing and is answered
  // with acked_delta_seq (server EconomyService.cs:440-453), so a delta whose ack was lost stops being resent. Its WalletUpdate
  // carries no balances, so a *balance* divergence is corrected by the next real change or by a reconnect, where the server sends
  // every visible balance on attach (EconomyModule.cs:119-123).
  if (last_refresh_us_ == 0) {
    last_refresh_us_ = now;  // the first probe waits a full period: a fresh session already got its balances on attach
    return;
  }
  if (now - last_refresh_us_ < kRefreshUs) return;
  last_refresh_us_ = now;
  if (!events::events_hub().linked()) return;  // nothing to ask, and flush() would refuse the row anyway
  wallet_hub().book_probe(events::events_hub().self_player_id());
  X4MP_CLOG(ctx.log, Cat::Net, Level::Debug, "wallet: asking the ledger what it has booked");
}

void WalletFeature::report(host::HostContext& ctx) {
  const WalletHub& hub = wallet_hub();
  const WalletStats& s = hub.stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[wallet] local={} known={} changes={} echoes={} applies={} quiet={} bad={} expected={} server={} target={} pending={}/{} "
            "updates={} stale={} converged={} writes={} failed={} acked={} dropped={} booked={} sent={} sendfail={} resent={} "
            "reasons={} unsent={} events={} wallet={}:{} switched={} probes={}",
            hub.local_balance(), hub.has_balance() ? 1 : 0, s.changes, s.echoes, s.applies, s.quiet, s.bad, hub.expected_adjust(),
            hub.server_known() ? hub.server_balance() : 0, hub.target_balance(), hub.pending_count(), hub.pending_sum(), s.updates,
            s.stale, s.converged, s.writes, s.write_failed, s.pending_acked, s.pending_dropped, s.booked, s.sent, s.send_failed,
            s.resent, s.reasons, hub.pending_unsent(), hub.events_seen(), static_cast<int>(hub.wallet_kind()), hub.wallet_owner(),
            s.switched, s.probes);
}

void WalletFeature::on_shutdown(host::HostContext& ctx) {
  report(ctx);
  inbox_.reset();
  wallet_hub().reset();
}

}  // namespace x4mp::features::wallet
