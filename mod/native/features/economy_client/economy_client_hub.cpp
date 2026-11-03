#include "features/economy_client/economy_client_hub.h"

#include <algorithm>
#include <cstdio>
#include <ctime>
#include <utility>

#include <nlohmann/json.hpp>

#include "message_ids_generated.h"

namespace x4mp::features::economy_client {

namespace P = X4MP::Proto;

namespace {

// A screen that keeps a few hundred rows is a screen nobody can read, and an unbounded map is a leak in a long session.
constexpr std::size_t kMaxUnsent = 32;
// Two hundred rows is the server's largest page: "load more" past that point only pushes older rows out of the list.
constexpr std::size_t kMaxLedgerRows = 200;

// The ledger carries UTC microseconds and the screen shows the player's own clock. The game's Lua has no os.date at all (it is
// sandboxed away), so the hub is the last place that still knows the local timezone: format once, when the page lands.
std::string clock_hhmm(std::uint64_t at_us) {
  if (at_us == 0) return std::string();
  const std::time_t secs = static_cast<std::time_t>(at_us / 1000000ull);
  std::tm tm{};
#ifdef _WIN32
  if (localtime_s(&tm, &secs) != 0) return std::string();
#else
  if (localtime_r(&secs, &tm) == nullptr) return std::string();
#endif
  char buf[8];
  if (std::snprintf(buf, sizeof buf, "%02d:%02d", tm.tm_hour, tm.tm_min) <= 0) return std::string();
  return std::string(buf);
}

std::string items_json(const std::vector<TradeItemView>& items) {
  nlohmann::json arr = nlohmann::json::array();
  for (const TradeItemView& item : items) {
    nlohmann::json row;
    row["kind"] = static_cast<int>(item.kind);
    row["amount"] = item.amount;
    if (item.ware_ref != 0) row["ware"] = item.ware_ref;
    if (item.asset != 0) row["asset"] = item.asset;
    arr.push_back(std::move(row));
  }
  return arr.dump();
}

}  // namespace

const char* op_name(Op op) noexcept {
  switch (op) {
    case Op::Send: return "send";
    case Op::Deposit: return "deposit";
    case Op::Withdraw: return "withdraw";
    case Op::Repay: return "repay";
    case Op::Forgive: return "forgive";
    case Op::Accept: return "accept";
    case Op::Cancel: return "cancel";
    case Op::Ledger: return "ledger";
    case Op::LoanOffer: return "loan_offer";
    case Op::LoanRespond: return "loan_respond";
  }
  return "?";
}

bool parse_op(std::string_view name, Op& out) noexcept {
  if (name == "send") { out = Op::Send; return true; }
  if (name == "deposit") { out = Op::Deposit; return true; }
  if (name == "withdraw") { out = Op::Withdraw; return true; }
  if (name == "repay") { out = Op::Repay; return true; }
  if (name == "forgive") { out = Op::Forgive; return true; }
  if (name == "accept") { out = Op::Accept; return true; }
  if (name == "cancel" || name == "decline") { out = Op::Cancel; return true; }
  if (name == "ledger") { out = Op::Ledger; return true; }
  if (name == "loan_offer") { out = Op::LoanOffer; return true; }
  if (name == "loan_respond") { out = Op::LoanRespond; return true; }
  return false;
}

EconomyClientHub& economy_client_hub() {
  static EconomyClientHub hub;
  return hub;
}

void EconomyClientHub::reset() {
  self_ = 0;
  key_ = 0;
  stats_ = EconomyStats{};
  dirty_ = false;
  last_.reset();
  last_trade_.reset();
  loans_.clear();
  trades_.clear();
  ledger_.clear();
  ledger_more_ = false;
  ledger_pending_ = false;
  ledger_before_.clear();
  unsent_.clear();
  notices_.clear();
}

Id128Value EconomyClientHub::next_key() noexcept {
  Id128Value key;
  key.lo = ++key_;
  key.hi = self_;
  return key;
}

bool EconomyClientHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload) {
  const auto msg = static_cast<P::MsgType>(type);
  switch (msg) {
    case P::MsgType::EconomyResult: {
      EconomyResultView view;
      if (!decode_economy_result(payload, view)) {
        ++stats_.bad;
        return true;
      }
      ++stats_.results;
      if (view.status == 2) ++stats_.rejected;  // EconomyStatus::Rejected
      last_ = std::move(view);
      dirty_ = true;
      return true;
    }
    case P::MsgType::LoanStatus: {
      LoanRowView view;
      if (!decode_loan_status(payload, view)) {
        ++stats_.bad;
        return true;
      }
      ++stats_.loans;
      put_loan(view);
      dirty_ = true;
      return true;
    }
    case P::MsgType::TradeStatus: {
      TradeRowView view;
      if (!decode_trade_status(payload, view)) {
        ++stats_.bad;
        return true;
      }
      ++stats_.trades;
      put_trade(std::move(view));
      dirty_ = true;
      return true;
    }
    case P::MsgType::TradeResult: {
      TradeResultView view;
      if (!decode_trade_result(payload, view)) {
        ++stats_.bad;
        return true;
      }
      ++stats_.trade_results;
      last_trade_ = std::move(view);
      dirty_ = true;
      return true;
    }
    case P::MsgType::LedgerPage: {
      LedgerPageView view;
      if (!decode_ledger_page(payload, view)) {
        // A page that cannot be read still answers the request: clear the latch, or the screen refuses every
        // later page with "a ledger page is already on its way".
        ledger_pending_ = false;
        ledger_before_.clear();
        ++stats_.bad;
        dirty_ = true;
        return true;
      }
      ++stats_.ledger_pages;
      put_ledger_page(view);
      dirty_ = true;
      return true;
    }
    default:
      return false;
  }
}

void EconomyClientHub::put_loan(const LoanRowView& row) {
  for (LoanRowView& existing : loans_) {
    if (existing.loan_id.lo == row.loan_id.lo && existing.loan_id.hi == row.loan_id.hi) {
      existing = row;
      return;
    }
  }
  loans_.push_back(row);
  if (loans_.size() > kMaxRows) loans_.erase(loans_.begin());
  // M5-30: a loan offered TO this node is the one thing on this screen that waits for an answer. A later LoanStatus for the
  // same loan only updates the row, so the player is told once.
  if (self_ != 0 && row.borrower == self_ && row.state == static_cast<std::uint8_t>(P::LoanState::Offered)) {
    Notice notice;
    notice.kind = NoticeKind::LoanOffer;
    notice.id = row.loan_id;
    notice.from = row.lender;
    notice.amount = row.principal;
    notices_.push_back(notice);
  }
}

namespace {

// The credits the other side puts on the table, when the trade is a straight credit offer: the screen can then say a number
// instead of "see the trades list". A trade of wares or ships leaves it at 0.
std::int64_t credit_offer(const std::vector<TradeItemView>& items) {
  std::int64_t sum = 0;
  for (const TradeItemView& item : items) {
    if (item.kind == static_cast<std::uint8_t>(P::TradeItemKind::Credits)) sum += item.amount;
  }
  return sum;
}

}  // namespace

void EconomyClientHub::put_trade(const TradeRowView& row) {
  for (TradeRowView& existing : trades_) {
    if (existing.trade_id.lo == row.trade_id.lo && existing.trade_id.hi == row.trade_id.hi) {
      if (row.version >= existing.version) existing = row;  // an older status must not roll the screen back
      return;
    }
  }
  trades_.push_back(row);
  if (trades_.size() > kMaxRows) trades_.erase(trades_.begin());
  // M5-30: a new trade where this node is the counterparty and the ball is in its court. A counter-offer arrives as a new
  // version of a trade we already hold, so it is a row update, not a new offer - the player already saw that trade.
  const bool open = row.state == static_cast<std::uint8_t>(P::TradeState::Proposed) ||
                    row.state == static_cast<std::uint8_t>(P::TradeState::Countered);
  if (self_ != 0 && open && row.counterparty.player_id == self_) {
    Notice notice;
    notice.kind = NoticeKind::TradeOffer;
    notice.id = row.trade_id;
    notice.from = row.initiator.player_id;
    notice.amount = credit_offer(row.initiator.give);
    notice.version = row.version;
    notices_.push_back(notice);
  }
}

// The last page replaces the list, a cursor page appends only rows this node has not seen: the cursor is the last id shown, so
// a row cannot repeat, and a resend of the same page changes nothing. A rejected page leaves the list alone - the screen keeps
// what it had and shows the error from `last`.
void EconomyClientHub::put_ledger_page(const LedgerPageView& page) {
  const bool from_newest = ledger_before_.empty();
  ledger_pending_ = false;
  ledger_before_.clear();
  if (page.status != static_cast<std::uint8_t>(P::EconomyStatus::Ok)) return;
  if (from_newest) ledger_.clear();
  for (const LedgerRowView& row : page.rows) {
    const bool seen = std::any_of(ledger_.begin(), ledger_.end(), [&row](const LedgerRowView& held) { return held.id == row.id; });
    if (!seen) {
      LedgerRowView held = row;
      held.at = clock_hhmm(row.at_us);
      ledger_.push_back(std::move(held));
    }
  }
  if (ledger_.size() > kMaxLedgerRows) ledger_.resize(kMaxLedgerRows);
  ledger_more_ = page.more;
}

std::vector<Notice> EconomyClientHub::take_notices() {
  std::vector<Notice> out;
  out.swap(notices_);
  stats_.notices += out.size();
  return out;
}

bool EconomyClientHub::request(Op op, const RequestParams& p, std::string& error) {
  error.clear();
  if (unsent_.size() >= kMaxUnsent) {
    error = "too many unsent requests";
    ++stats_.bad;
    return false;
  }
  const Id128Value key = next_key();
  OutFrame frame;
  frame.key = key;
  frame.op = op;

  switch (op) {
    case Op::Send: {
      if (p.to_player == 0) { error = "no recipient"; break; }
      if (p.to_player == self_) { error = "cannot send to yourself"; break; }
      if (p.amount <= 0) { error = "amount must be positive"; break; }
      CreditTransferData data;
      data.key = key;
      data.to_player = p.to_player;
      data.amount = p.amount;
      data.memo = p.memo;
      frame.type = static_cast<std::uint16_t>(P::MsgType::CreditTransferRequest);
      frame.bytes = encode_credit_transfer(data);
      break;
    }
    case Op::Deposit:
    case Op::Withdraw: {
      if (p.amount <= 0) { error = "amount must be positive"; break; }
      PoolMoveData data;
      data.key = key;
      data.amount = p.amount;
      frame.type = static_cast<std::uint16_t>(op == Op::Deposit ? P::MsgType::PoolDepositRequest
                                                                : P::MsgType::PoolWithdrawRequest);
      frame.bytes = op == Op::Deposit ? encode_pool_deposit(data) : encode_pool_withdraw(data);
      break;
    }
    case Op::Repay:
    case Op::Forgive: {
      if (p.id.empty()) { error = "no loan id"; break; }
      if (op == Op::Repay && p.amount <= 0) { error = "amount must be positive"; break; }
      if (op == Op::Forgive && p.amount < 0) { error = "amount cannot be negative"; break; }
      LoanActionData data;
      data.key = key;
      data.loan_id = p.id;
      data.amount = p.amount;
      frame.type = static_cast<std::uint16_t>(op == Op::Repay ? P::MsgType::LoanRepay : P::MsgType::LoanForgive);
      frame.bytes = op == Op::Repay ? encode_loan_repay(data) : encode_loan_forgive(data);
      break;
    }
    case Op::Accept: {
      if (p.id.empty()) { error = "no trade id"; break; }
      TradeAcceptData data;
      data.key = key;
      data.trade_id = p.id;
      data.version = p.version;
      data.receive_into_asset = p.receive_into_asset;
      frame.type = static_cast<std::uint16_t>(P::MsgType::TradeAccept);
      frame.bytes = encode_trade_accept(data);
      break;
    }
    case Op::Cancel: {
      if (p.id.empty()) { error = "no trade id"; break; }
      TradeCancelData data;
      data.key = key;
      data.trade_id = p.id;
      frame.type = static_cast<std::uint16_t>(P::MsgType::TradeCancel);
      frame.bytes = encode_trade_cancel(data);
      break;
    }
    case Op::LoanOffer: {
      if (p.to_player == 0) { error = "no borrower"; break; }
      if (p.to_player == self_) { error = "cannot offer a loan to yourself"; break; }
      if (p.amount <= 0) { error = "amount must be positive"; break; }
      const std::int64_t repay_total = p.repay_total > 0 ? p.repay_total : p.amount;
      if (repay_total < p.amount) { error = "the repayment cannot be smaller than the loan"; break; }
      if (p.auto_repay_pct > 100) { error = "the automatic repayment must be 0 to 100 percent"; break; }
      LoanOfferData data;
      data.key = key;
      data.borrower = p.to_player;
      data.principal = p.amount;
      data.repay_total = repay_total;
      data.due_in_s = p.due_in_s;
      data.auto_repay_pct = p.auto_repay_pct;
      data.memo = p.memo;
      frame.type = static_cast<std::uint16_t>(P::MsgType::LoanOffer);
      frame.bytes = encode_loan_offer(data);
      break;
    }
    case Op::LoanRespond: {
      if (p.id.empty()) { error = "no loan id"; break; }
      LoanRespondData data;
      data.key = key;
      data.loan_id = p.id;
      data.accept = p.accept;
      frame.type = static_cast<std::uint16_t>(P::MsgType::LoanRespond);
      frame.bytes = encode_loan_respond(data);
      break;
    }
    case Op::Ledger: {
      // One page in flight at a time: two answers cannot be told apart, and the list would jump when they interleave.
      if (ledger_pending_) { error = "a ledger page is already on its way"; break; }
      if (p.before.size() > 64) { error = "bad ledger cursor"; break; }
      LedgerRequestData data;
      data.key = key;
      data.limit = p.limit;
      data.before = p.before;
      frame.type = static_cast<std::uint16_t>(P::MsgType::LedgerRequest);
      frame.bytes = encode_ledger_request(data);
      ledger_pending_ = true;
      ledger_before_ = p.before;
      break;
    }
  }

  if (!error.empty()) {
    ++stats_.bad;
    return false;
  }
  ++stats_.requests;
  unsent_.push_back(std::move(frame));
  return true;
}

std::vector<OutFrame> EconomyClientHub::take_unsent(std::size_t max) {
  std::vector<OutFrame> out;
  if (max == 0) return out;
  const std::size_t n = std::min(max, unsent_.size());
  out.reserve(n);
  for (std::size_t i = 0; i < n; ++i) out.push_back(std::move(unsent_[i]));
  unsent_.erase(unsent_.begin(), unsent_.begin() + static_cast<std::ptrdiff_t>(n));
  return out;
}

void EconomyClientHub::note_send_failed() { ++stats_.send_failed; }

std::string EconomyClientHub::snapshot_json() const {
  nlohmann::json j;
  j["v"] = 1;
  j["self"] = self_;

  nlohmann::json loans = nlohmann::json::array();
  for (const LoanRowView& row : loans_) {
    nlohmann::json r;
    r["id"] = row.loan_id.hex();
    r["lender"] = row.lender;
    r["borrower"] = row.borrower;
    r["state"] = static_cast<int>(row.state);
    r["principal"] = row.principal;
    r["repay_total"] = row.repay_total;
    r["repaid"] = row.repaid;
    r["forgiven"] = row.forgiven;
    r["created_us"] = row.created_time_us;
    r["due_us"] = row.due_time_us;
    r["memo"] = row.memo;
    r["mine"] = (row.lender == self_ || row.borrower == self_);
    r["i_lend"] = (row.lender == self_);
    loans.push_back(std::move(r));
  }
  j["loans"] = std::move(loans);

  nlohmann::json trades = nlohmann::json::array();
  for (const TradeRowView& row : trades_) {
    nlohmann::json r;
    r["id"] = row.trade_id.hex();
    r["version"] = row.version;
    r["state"] = static_cast<int>(row.state);
    r["initiator"] = row.initiator.player_id;
    r["counterparty"] = row.counterparty.player_id;
    r["initiator_team"] = row.initiator.team_id;
    r["counterparty_team"] = row.counterparty.team_id;
    r["give"] = nlohmann::json::parse(items_json(row.initiator.give));
    r["want"] = nlohmann::json::parse(items_json(row.counterparty.give));
    r["expires_us"] = row.expires_time_us;
    r["memo"] = row.memo;
    r["incoming"] = (row.counterparty.player_id == self_);
    r["mine"] = (row.initiator.player_id == self_ || row.counterparty.player_id == self_);
    trades.push_back(std::move(r));
  }
  j["trades"] = std::move(trades);

  // M5-29: the last entries the player may see, newest first, plus whether the server has an older page. The screen renders
  // these rows itself; nothing here formats a line or decides what a row means.
  nlohmann::json ledger = nlohmann::json::array();
  for (const LedgerRowView& row : ledger_) {
    nlohmann::json r;
    r["id"] = row.id;
    r["at_us"] = row.at_us;
    r["at"] = row.at;
    r["kind"] = static_cast<int>(row.kind);
    r["wallet_kind"] = static_cast<int>(row.wallet_kind);
    r["wallet_owner"] = row.wallet_owner;
    r["delta"] = row.delta;
    r["actor"] = row.actor;
    r["ref_type"] = row.ref_type;
    r["ref_id"] = row.ref_id;
    r["note"] = row.note;
    r["reversed"] = row.reversed;
    ledger.push_back(std::move(r));
  }
  j["ledger"] = std::move(ledger);
  j["ledger_more"] = ledger_more_;
  // a counter, so the UI can tell a fresh page from a redraw of the same one
  j["ledger_pages"] = stats_.ledger_pages;

  if (last_.has_value()) {
    nlohmann::json r;
    r["ok"] = last_->status == 0;  // EconomyStatus::Ok
    r["status"] = static_cast<int>(last_->status);
    r["reason"] = static_cast<int>(last_->reason);
    r["detail"] = last_->detail;
    r["ref"] = last_->ref_id.hex();
    r["key"] = last_->request_key.hex();
    j["last"] = std::move(r);
  }
  if (last_trade_.has_value()) {
    nlohmann::json r;
    r["id"] = last_trade_->trade_id.hex();
    r["version"] = last_trade_->version;
    r["state"] = static_cast<int>(last_trade_->state);
    r["reason"] = static_cast<int>(last_trade_->reason);
    r["detail"] = last_trade_->detail;
    j["last_trade"] = std::move(r);
  }
  return j.dump();
}

}  // namespace x4mp::features::economy_client
