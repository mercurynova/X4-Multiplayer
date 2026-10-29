#include "features/wallet/wallet_hub.h"

#include <limits>

#include <nlohmann/json.hpp>

#include "features/wallet/wallet_wire.h"

namespace x4mp::features::wallet {

namespace {

// The WalletKind a node spends from, per credit mode (mod-design 12.1: wallet:<player> in PerPlayer, pool:<team> in Shared).
// TeamShared is not one of the two spendable accounts the design names, so it is never picked here.
constexpr std::uint8_t kKindPlayer = static_cast<std::uint8_t>(P::WalletKind::Player);
constexpr std::uint8_t kKindTeamPool = static_cast<std::uint8_t>(P::WalletKind::TeamPool);

}  // namespace

std::optional<std::int64_t> parse_money_value(std::string_view text) {
  if (text.empty() || text.size() > kMaxMoneyBody) return std::nullopt;
  const nlohmann::json body = nlohmann::json::parse(text.begin(), text.end(), nullptr, /*allow_exceptions=*/false);
  if (body.is_discarded() || !body.is_object()) return std::nullopt;
  const auto it = body.find("value");
  if (it == body.end()) return std::nullopt;
  // The bridge prints an integral number as an integer literal (ui/x4mp_bridge.lua encodeValue), so anything else is not a balance.
  if (it->is_number_unsigned()) {
    const auto v = it->get<std::uint64_t>();
    if (v > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) return std::nullopt;
    return static_cast<std::int64_t>(v);
  }
  if (it->is_number_integer()) return it->get<std::int64_t>();
  return std::nullopt;
}

Observation WalletHub::observe(std::int64_t local_money, std::uint64_t now_us) {
  observed_at_us_ = now_us;
  ++stats_.polls;
  if (!has_balance_) {
    has_balance_ = true;
    local_balance_ = local_money;
    last_change_ = 0;
    ++stats_.baseline;
    return Observation::Baseline;
  }
  if (local_money == local_balance_) {
    ++stats_.quiet;
    return Observation::Quiet;
  }
  const std::int64_t delta = local_money - local_balance_;
  local_balance_ = local_money;
  last_change_ = delta;
  // Our own write coming back. A change that is NOT the expected one is the player's and must be booked; the expectation is kept, so an
  // echo that lands after a real change is still recognised as ours.
  if (expected_adjust_ != 0 && delta == expected_adjust_) {
    expected_adjust_ = 0;
    ++stats_.echoes;
    return Observation::Echo;
  }
  ++stats_.changes;
  return Observation::Change;
}

void WalletHub::note_apply(std::int64_t delta) {
  ++stats_.applies;
  if (delta == 0) return;
  expected_adjust_ += delta;
}

bool WalletHub::note_server_balance(std::int64_t balance, std::uint64_t version, std::uint64_t ack_seq) {
  // economy.fbs:23: `version` is the ledger sequence, and an older update must not undo a newer one. Equal means nothing new.
  if (server_known_ && version <= server_version_) {
    ++stats_.stale;
    return false;
  }
  server_known_ = true;
  server_version_ = version;
  server_balance_ = balance;
  if (ack_seq > ack_seq_) ack_seq_ = ack_seq;
  // A booked delta is already inside `balance`: keeping it in pending would count it twice.
  const auto before = pending_.size();
  for (auto it = pending_.begin(); it != pending_.end();) {
    if (it->seq <= ack_seq_) {
      pending_sum_ -= it->delta;
      it = pending_.erase(it);
    } else {
      ++it;
    }
  }
  stats_.pending_acked += static_cast<std::uint64_t>(before - pending_.size());
  ++stats_.updates;
  if (has_balance_ && effective_local() == target_balance()) ++stats_.converged;
  return true;
}

std::uint64_t WalletHub::book_pending(std::int64_t delta, std::uint8_t reason) {
  const std::uint64_t seq = ++seq_;  // strictly monotonic even for a zero delta, so an ack can never point at a gap
  if (delta == 0) return seq;
  make_room();
  pending_.push_back(PendingDelta{seq, delta, reason});
  pending_sum_ += delta;
  return seq;
}

void WalletHub::make_room() {
  if (pending_.size() < kMaxPending) return;
  pending_sum_ -= pending_.front().delta;
  pending_.erase(pending_.begin());
  ++stats_.pending_dropped;
}

void WalletHub::note_event(events::EventKind kind, std::uint64_t frame) {
  ++event_seq_;
  std::uint8_t source = 0;
  switch (kind) {
    case events::EventKind::Trade: source = static_cast<std::uint8_t>(P::CreditSource::Trade); break;
    case events::EventKind::Build: source = static_cast<std::uint8_t>(P::CreditSource::Build); break;
    default:
      // Not a cause of money: the counter moved (a delta can still name the event by its number), the hint did not.
      return;
  }
  hint_.source = source;
  hint_.ref_event_seq = event_seq_;
  hint_.frame = frame;
}

ReasonHint WalletHub::take_reason(std::uint64_t frame) {
  ReasonHint out;
  const bool fresh = hint_.named() && frame >= hint_.frame && frame - hint_.frame <= kReasonFrames;
  if (fresh) out = hint_;
  hint_ = ReasonHint{};  // consumed either way: a stale event must not pay for the next change
  return out;
}

std::optional<PendingDelta> WalletHub::book_upward(std::int64_t delta, ReasonHint reason, std::uint16_t player_id) {
  if (delta == 0) {
    ++seq_;  // the seq stays strictly monotonic, so an ack can never point at a gap
    return std::nullopt;
  }
  make_room();
  PendingDelta row;
  row.seq = ++seq_;
  row.delta = delta;
  row.key_lo = ++key_;
  row.key_hi = player_id;
  row.source = reason.source;
  row.ref_event_seq = reason.ref_event_seq;
  if (reason.named()) ++stats_.reasons;
  pending_.push_back(row);
  pending_sum_ += delta;
  ++stats_.booked;
  return row;
}

std::vector<PendingDelta> WalletHub::take_unsent(std::size_t max) const {
  std::vector<PendingDelta> out;
  for (const PendingDelta& row : pending_) {
    if (out.size() >= max) break;
    if (!row.sent) out.push_back(row);
  }
  return out;
}

void WalletHub::mark_sent(std::uint64_t seq) {
  for (PendingDelta& row : pending_) {
    if (row.seq != seq) continue;
    if (!row.sent) {
      row.sent = true;
      ++stats_.sent;
    }
    return;
  }
}

void WalletHub::resend_all() {
  std::uint64_t armed = 0;
  for (PendingDelta& row : pending_) {
    if (!row.sent) continue;
    row.sent = false;
    ++armed;
  }
  stats_.resent += armed;
}

std::size_t WalletHub::pending_unsent() const noexcept {
  std::size_t count = 0;
  for (const PendingDelta& row : pending_) {
    if (!row.sent) ++count;
  }
  return count;
}

std::optional<std::int64_t> WalletHub::take_convergence() const {
  // Without both halves there is nothing to compare: no reading yet (the first poll supplies it) or no WalletUpdate yet.
  if (!server_known_ || !has_balance_) return std::nullopt;
  const std::int64_t adjust = target_balance() - effective_local();
  if (adjust == 0) return std::nullopt;
  return adjust;
}

void WalletHub::note_write(std::int64_t delta) {
  ++stats_.writes;
  note_apply(delta);
}

bool WalletHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t own_player,
                                 std::uint16_t own_team) {
  if (type != static_cast<std::uint16_t>(P::MsgType::WalletUpdate)) return false;
  WalletUpdateView view;
  if (!decode_wallet_update(payload, view)) {
    ++stats_.bad_update;
    return false;
  }
  // Which row is ours: the shared pool in Shared mode, our own wallet in PerPlayer (mod-design 12.1). No owner means this node is
  // not in a session yet, and a row we do not have is not an error either: the update simply named no wallet of ours.
  const bool shared = view.effective_mode == static_cast<std::uint8_t>(P::EffectiveCreditMode::Shared);
  const std::uint8_t want = shared ? kKindTeamPool : kKindPlayer;
  const std::uint16_t owner = shared ? own_team : own_player;
  if (owner == 0) {
    ++stats_.no_wallet;
    return false;
  }
  for (const WalletBalanceView& row : view.balances) {
    if (row.kind != want || row.owner != owner) continue;
    return note_server_balance(row.balance, row.version, view.ack_seq);
  }
  ++stats_.no_wallet;
  return false;
}

void WalletHub::reset() { *this = WalletHub{}; }

WalletHub& wallet_hub() {
  static WalletHub hub;
  return hub;
}

}  // namespace x4mp::features::wallet
