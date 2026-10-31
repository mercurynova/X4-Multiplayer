#pragma once
// features/economy_client/economy_wire: the C++ half of the player-facing economy frames (M5-29/M5-30; docs/m5-plan.md W7,
// docs/mod-design.md 12.5-12.7, protocol/schema/economy.fbs).
//
// The server owns every balance and every loan/trade (economy.fbs:2). This file only turns bytes into views and views into
// bytes: the seven requests a player may make (send credits, deposit to and withdraw from the team pool, repay and forgive a
// loan, accept and cancel a trade) and the four answers the node has to understand (EconomyResult for the request it sent,
// LoanStatus, TradeStatus and TradeResult for the state of the books).
//
// Same shape as features/wallet/wallet_wire.h: verify the buffer, then copy the fields out, because the pointer lives only as
// long as the payload does. Nothing here decides anything - economy_client_hub does the bookkeeping.

#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "economy_generated.h"
#include "flatbuffers/flatbuffers.h"
#include "message_ids_generated.h"

namespace x4mp::features::economy_client {

namespace P = X4MP::Proto;

// One economy.fbs Id128 as a value. `hex()` is the form the Lua side and the logs use ("lo:hi" in hex), because a decimal
// 128-bit number is unreadable and the server prints ids the same way.
struct Id128Value {
  std::uint64_t lo = 0;
  std::uint64_t hi = 0;

  [[nodiscard]] bool empty() const noexcept { return lo == 0 && hi == 0; }
  [[nodiscard]] std::string hex() const;
  // Parses "lo:hi" (both hex, with or without 0x) or a single hex word into `lo`. False on anything else.
  [[nodiscard]] static bool parse(std::string_view text, Id128Value& out);
};

inline std::string Id128Value::hex() const {
  char buf[40];
  std::snprintf(buf, sizeof(buf), "%016llx:%016llx", static_cast<unsigned long long>(lo), static_cast<unsigned long long>(hi));
  return std::string(buf);
}

inline bool Id128Value::parse(std::string_view text, Id128Value& out) {
  out = Id128Value{};
  std::string s(text);
  while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.erase(s.begin());
  while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.pop_back();
  if (s.rfind("0x", 0) == 0) s.erase(0, 2);
  const std::size_t colon = s.find(':');
  auto word = [](const std::string& part, std::uint64_t& value) {
    if (part.empty() || part.size() > 16) return false;
    for (const char c : part) {
      if (!std::isxdigit(static_cast<unsigned char>(c))) return false;
    }
    value = std::strtoull(part.c_str(), nullptr, 16);
    return true;
  };
  if (colon == std::string::npos) return word(s, out.lo);
  return word(s.substr(0, colon), out.lo) && word(s.substr(colon + 1), out.hi);
}

struct EconomyResultView {
  Id128Value request_key;
  std::uint8_t status = 0;  // EconomyStatus: 0 Ok, 1 Pending, 2 Rejected
  std::uint8_t reason = 0;  // EconomyReject: 0 None, ...
  std::string detail;
  Id128Value ref_id;
};

struct LoanRowView {
  Id128Value loan_id;
  std::uint16_t lender = 0;
  std::uint16_t borrower = 0;
  std::uint8_t state = 0;  // LoanState
  std::int64_t principal = 0;
  std::int64_t repay_total = 0;
  std::int64_t repaid = 0;
  std::int64_t forgiven = 0;
  std::uint64_t created_time_us = 0;
  std::uint64_t due_time_us = 0;
  std::string memo;
};

struct TradeItemView {
  std::uint8_t kind = 0;  // TradeItemKind: 0 Credits, 1 Ware, 2 Ship, 3 Station
  std::int64_t amount = 0;
  std::uint32_t ware_ref = 0;
  std::uint32_t asset = 0;
};

struct TradeSideView {
  std::uint16_t player_id = 0;
  std::uint16_t team_id = 0;
  std::uint32_t accepted_version = 0;
  std::vector<TradeItemView> give;
};

struct TradeRowView {
  Id128Value trade_id;
  std::uint32_t version = 0;
  std::uint8_t state = 0;  // TradeState
  TradeSideView initiator;
  TradeSideView counterparty;
  std::uint64_t expires_time_us = 0;
  std::string memo;
};

struct TradeResultView {
  Id128Value trade_id;
  std::uint32_t version = 0;
  std::uint8_t state = 0;
  std::uint8_t reason = 0;
  std::string detail;
};

// One committed transaction as this node may see it (economy.fbs LedgerRow). Only the wallets this node could already see in
// a WalletUpdate are counted into `delta`, so a row never implies the counterparty's balance. `id` is a ULID and doubles as
// the paging cursor: the next page asks for rows strictly older than the last one seen.
struct LedgerRowView {
  std::string id;
  std::uint64_t at_us = 0;
  std::uint8_t kind = 0;  // LedgerKind
  std::uint8_t wallet_kind = 0;  // WalletKind of the first visible line
  std::uint16_t wallet_owner = 0;
  std::int64_t delta = 0;  // net change to this node's visible wallets
  std::uint16_t actor = 0;  // the player whose action caused it; 0 = the server or the authority
  std::string ref_type;
  std::int64_t ref_id = 0;
  std::string note;
  bool reversed = false;
  // Not on the wire: the hub fills this with the row's time on the player's own clock (HH:MM) when the page lands. The game's
  // Lua has no os.date, and the row's timestamp is UTC microseconds, so the only place that can render it is here.
  std::string at;
};

struct LedgerPageView {
  Id128Value request_key;
  std::uint8_t status = 0;  // EconomyStatus
  std::uint8_t reason = 0;  // EconomyReject
  std::string detail;
  std::vector<LedgerRowView> rows;  // newest first
  bool more = false;                // at least one older row this node may see exists
};

// --- requests (node -> server) ---------------------------------------------------------------------------------------

struct CreditTransferData {
  Id128Value key;
  std::uint16_t to_player = 0;
  std::int64_t amount = 0;
  std::string memo;
};

struct PoolMoveData {
  Id128Value key;
  std::int64_t amount = 0;
};

struct LoanActionData {
  Id128Value key;
  Id128Value loan_id;
  std::int64_t amount = 0;  // repay: credits to pay; forgive: 0 = the rest
};

struct TradeAcceptData {
  Id128Value key;
  Id128Value trade_id;
  std::uint32_t version = 0;
  std::uint32_t receive_into_asset = 0;
};

struct TradeCancelData {
  Id128Value key;
  Id128Value trade_id;
};

struct LedgerRequestData {
  Id128Value key;
  std::uint16_t limit = 0;  // 0 = the server default (50); the server clamps 1..200
  std::string before;       // ULID cursor; empty = from the newest
};

namespace detail {

inline std::vector<std::uint8_t> finish(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

inline P::Id128 to_wire(const Id128Value& id) { return P::Id128(id.lo, id.hi); }

}  // namespace detail

[[nodiscard]] inline std::vector<std::uint8_t> encode_credit_transfer(const CreditTransferData& data) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 key = detail::to_wire(data.key);
  const auto memo = data.memo.empty() ? flatbuffers::Offset<flatbuffers::String>() : fbb.CreateString(data.memo);
  const auto req = P::CreateCreditTransferRequest(fbb, &key, data.to_player, data.amount, memo);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_pool_deposit(const PoolMoveData& data) {
  flatbuffers::FlatBufferBuilder fbb(64);
  const P::Id128 key = detail::to_wire(data.key);
  const auto req = P::CreatePoolDepositRequest(fbb, &key, data.amount);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_pool_withdraw(const PoolMoveData& data) {
  flatbuffers::FlatBufferBuilder fbb(64);
  const P::Id128 key = detail::to_wire(data.key);
  const auto req = P::CreatePoolWithdrawRequest(fbb, &key, data.amount);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_loan_repay(const LoanActionData& data) {
  flatbuffers::FlatBufferBuilder fbb(96);
  const P::Id128 key = detail::to_wire(data.key);
  const P::Id128 loan = detail::to_wire(data.loan_id);
  const auto req = P::CreateLoanRepay(fbb, &key, &loan, data.amount);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_loan_forgive(const LoanActionData& data) {
  flatbuffers::FlatBufferBuilder fbb(96);
  const P::Id128 key = detail::to_wire(data.key);
  const P::Id128 loan = detail::to_wire(data.loan_id);
  const auto req = P::CreateLoanForgive(fbb, &key, &loan, data.amount);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_trade_accept(const TradeAcceptData& data) {
  flatbuffers::FlatBufferBuilder fbb(96);
  const P::Id128 key = detail::to_wire(data.key);
  const P::Id128 trade = detail::to_wire(data.trade_id);
  const auto req = P::CreateTradeAccept(fbb, &key, &trade, data.version, data.receive_into_asset);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_trade_cancel(const TradeCancelData& data) {
  flatbuffers::FlatBufferBuilder fbb(96);
  const P::Id128 key = detail::to_wire(data.key);
  const P::Id128 trade = detail::to_wire(data.trade_id);
  const auto req = P::CreateTradeCancel(fbb, &key, &trade);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_ledger_request(const LedgerRequestData& data) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 key = detail::to_wire(data.key);
  const auto before = data.before.empty() ? flatbuffers::Offset<flatbuffers::String>() : fbb.CreateString(data.before);
  const auto req = P::CreateLedgerRequest(fbb, &key, data.limit, before);
  fbb.Finish(req);
  return detail::finish(fbb);
}

// --- answers (server -> node) ---------------------------------------------------------------------------------------

[[nodiscard]] inline bool decode_economy_result(std::span<const std::uint8_t> bytes, EconomyResultView& out) {
  out = EconomyResultView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::EconomyResult>(nullptr)) return false;
  const P::EconomyResult* r = flatbuffers::GetRoot<P::EconomyResult>(bytes.data());
  if (r == nullptr) return false;
  if (const P::Id128* key = r->request_key()) {
    out.request_key.lo = key->lo();
    out.request_key.hi = key->hi();
  }
  out.status = static_cast<std::uint8_t>(r->status());
  out.reason = static_cast<std::uint8_t>(r->reason());
  if (const auto* detail = r->detail()) out.detail = detail->str();
  if (const P::Id128* ref = r->ref_id()) {
    out.ref_id.lo = ref->lo();
    out.ref_id.hi = ref->hi();
  }
  return true;
}

[[nodiscard]] inline bool decode_loan_status(std::span<const std::uint8_t> bytes, LoanRowView& out) {
  out = LoanRowView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::LoanStatus>(nullptr)) return false;
  const P::LoanStatus* r = flatbuffers::GetRoot<P::LoanStatus>(bytes.data());
  if (r == nullptr) return false;
  if (const P::Id128* id = r->loan_id()) {
    out.loan_id.lo = id->lo();
    out.loan_id.hi = id->hi();
  }
  out.lender = r->lender();
  out.borrower = r->borrower();
  out.state = static_cast<std::uint8_t>(r->state());
  out.principal = r->principal();
  out.repay_total = r->repay_total();
  out.repaid = r->repaid();
  out.forgiven = r->forgiven();
  out.created_time_us = r->created_time_us();
  out.due_time_us = r->due_time_us();
  if (const auto* memo = r->memo()) out.memo = memo->str();
  return true;
}

namespace detail {

inline void copy_items(const flatbuffers::Vector<flatbuffers::Offset<P::TradeItem>>* items, std::vector<TradeItemView>& out) {
  if (items == nullptr) return;
  out.reserve(items->size());
  for (const P::TradeItem* item : *items) {
    if (item == nullptr) continue;
    TradeItemView view;
    view.kind = static_cast<std::uint8_t>(item->kind());
    view.amount = item->amount();
    view.ware_ref = item->ware_ref();
    view.asset = item->asset();
    out.push_back(view);
  }
}

inline void copy_side(const P::TradeSide* side, TradeSideView& out) {
  if (side == nullptr) return;
  out.player_id = side->player_id();
  out.team_id = side->team_id();
  out.accepted_version = side->accepted_version();
  copy_items(side->give(), out.give);
}

}  // namespace detail

[[nodiscard]] inline bool decode_trade_status(std::span<const std::uint8_t> bytes, TradeRowView& out) {
  out = TradeRowView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::TradeStatus>(nullptr)) return false;
  const P::TradeStatus* r = flatbuffers::GetRoot<P::TradeStatus>(bytes.data());
  if (r == nullptr) return false;
  if (const P::Id128* id = r->trade_id()) {
    out.trade_id.lo = id->lo();
    out.trade_id.hi = id->hi();
  }
  out.version = r->version();
  out.state = static_cast<std::uint8_t>(r->state());
  detail::copy_side(r->initiator(), out.initiator);
  detail::copy_side(r->counterparty(), out.counterparty);
  out.expires_time_us = r->expires_time_us();
  if (const auto* memo = r->memo()) out.memo = memo->str();
  return true;
}

[[nodiscard]] inline bool decode_trade_result(std::span<const std::uint8_t> bytes, TradeResultView& out) {
  out = TradeResultView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::TradeResult>(nullptr)) return false;
  const P::TradeResult* r = flatbuffers::GetRoot<P::TradeResult>(bytes.data());
  if (r == nullptr) return false;
  if (const P::Id128* id = r->trade_id()) {
    out.trade_id.lo = id->lo();
    out.trade_id.hi = id->hi();
  }
  out.version = r->version();
  out.state = static_cast<std::uint8_t>(r->state());
  out.reason = static_cast<std::uint8_t>(r->reason());
  if (const auto* detail = r->detail()) out.detail = detail->str();
  return true;
}

[[nodiscard]] inline bool decode_ledger_page(std::span<const std::uint8_t> bytes, LedgerPageView& out) {
  out = LedgerPageView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::LedgerPage>(nullptr)) return false;
  const P::LedgerPage* r = flatbuffers::GetRoot<P::LedgerPage>(bytes.data());
  if (r == nullptr) return false;
  if (const P::Id128* key = r->request_key()) {
    out.request_key.lo = key->lo();
    out.request_key.hi = key->hi();
  }
  out.status = static_cast<std::uint8_t>(r->status());
  out.reason = static_cast<std::uint8_t>(r->reason());
  if (const auto* detail = r->detail()) out.detail = detail->str();
  out.more = r->more();
  if (const auto* rows = r->rows()) {
    out.rows.reserve(rows->size());
    for (const P::LedgerRow* row : *rows) {
      if (row == nullptr) continue;
      LedgerRowView view;
      if (const auto* id = row->id()) view.id = id->str();
      view.at_us = row->at_us();
      view.kind = static_cast<std::uint8_t>(row->kind());
      if (const P::WalletRef* wallet = row->wallet()) {
        view.wallet_kind = static_cast<std::uint8_t>(wallet->kind());
        view.wallet_owner = wallet->owner_id();
      }
      view.delta = row->delta();
      view.actor = row->actor();
      if (const auto* ref_type = row->ref_type()) view.ref_type = ref_type->str();
      view.ref_id = row->ref_id();
      if (const auto* note = row->note()) view.note = note->str();
      view.reversed = row->reversed();
      out.rows.push_back(std::move(view));
    }
  }
  return true;
}

}  // namespace x4mp::features::economy_client
