// M5-29: the economy-client wire and hub, against real flatbuffers built the way the server builds them
// (EconomyModule*.cs), so the reader is tested on the actual wire format and not on a hand-made struct.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include "features/economy_client/economy_client_hub.h"
#include "features/economy_client/economy_wire.h"

namespace E = x4mp::features::economy_client;
namespace P = X4MP::Proto;

namespace {

E::Id128Value id_of(std::uint64_t lo, std::uint64_t hi) {
  E::Id128Value id;
  id.lo = lo;
  id.hi = hi;
  return id;
}

std::vector<std::uint8_t> finish(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

// One EconomyResult exactly as server/src/X4MP.Core/.../EconomyModule.cs SendResult packs it.
std::vector<std::uint8_t> economy_result(std::uint64_t lo, std::uint64_t hi, P::EconomyStatus status, P::EconomyReject reason,
                                         const std::string& detail) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 key(lo, hi);
  const P::Id128 ref(0, 0);
  const auto text = fbb.CreateString(detail);
  const auto offset = P::CreateEconomyResult(fbb, &key, status, reason, text, &ref, 0);
  fbb.Finish(offset);
  return finish(fbb);
}

std::vector<std::uint8_t> loan_status(std::uint64_t lo, std::uint64_t hi, P::LoanState state, std::int64_t principal,
                                      std::int64_t repay_total, std::int64_t repaid) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 loan(lo, hi);
  const auto memo = fbb.CreateString("loan memo");
  const auto offset = P::CreateLoanStatus(fbb, &loan, 3, 4, state, principal, repay_total, repaid, 0, 1000, 2000, memo);
  fbb.Finish(offset);
  return finish(fbb);
}

std::vector<std::uint8_t> trade_status(std::uint64_t lo, std::uint64_t hi, std::uint32_t version, P::TradeState state,
                                       std::uint16_t initiator, std::uint16_t counterparty) {
  flatbuffers::FlatBufferBuilder fbb(256);
  const P::Id128 trade(lo, hi);
  const auto give = fbb.CreateVector(std::vector<flatbuffers::Offset<P::TradeItem>>{});
  const auto initiator_side = P::CreateTradeSide(fbb, initiator, 1, give, version);
  const auto counterparty_side = P::CreateTradeSide(fbb, counterparty, 2, give, 0);
  const auto memo = fbb.CreateString("trade memo");
  const auto offset = P::CreateTradeStatus(fbb, &trade, version, state, initiator_side, counterparty_side, 5000, memo);
  fbb.Finish(offset);
  return finish(fbb);
}

// A trade that carries credits on the initiator's side: what the notice quotes as "on the table".
std::vector<std::uint8_t> trade_offer_with_credits(std::uint64_t lo, std::uint64_t hi, std::uint32_t version,
                                                   P::TradeState state, std::uint16_t initiator, std::uint16_t counterparty,
                                                   std::int64_t credits) {
  flatbuffers::FlatBufferBuilder fbb(256);
  const P::Id128 trade(lo, hi);
  const std::vector<flatbuffers::Offset<P::TradeItem>> give_items{
      P::CreateTradeItem(fbb, P::TradeItemKind::Credits, credits, 0, 0)};
  const auto give = fbb.CreateVector(give_items);
  const auto want = fbb.CreateVector(std::vector<flatbuffers::Offset<P::TradeItem>>{});
  const auto initiator_side = P::CreateTradeSide(fbb, initiator, 1, give, version);
  const auto counterparty_side = P::CreateTradeSide(fbb, counterparty, 2, want, 0);
  const auto memo = fbb.CreateString("offer");
  const auto offset = P::CreateTradeStatus(fbb, &trade, version, state, initiator_side, counterparty_side, 5000, memo);
  fbb.Finish(offset);
  return finish(fbb);
}

std::vector<std::uint8_t> trade_result(std::uint64_t lo, std::uint64_t hi, P::TradeState state, P::EconomyReject reason,
                                       const std::string& detail) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 trade(lo, hi);
  const auto text = fbb.CreateString(detail);
  const auto offset = P::CreateTradeResult(fbb, &trade, 2, state, reason, text);
  fbb.Finish(offset);
  return finish(fbb);
}

// One LedgerPage as EconomyModule.Ledger.cs packs it: the rows are the ones the requester may see, newest first.
std::vector<std::uint8_t> ledger_page(std::uint64_t key_lo, std::uint64_t key_hi, P::EconomyStatus status, bool more,
                                      const std::vector<std::pair<std::string, std::int64_t>>& rows) {
  flatbuffers::FlatBufferBuilder fbb(512);
  const P::Id128 key(key_lo, key_hi);
  std::vector<flatbuffers::Offset<P::LedgerRow>> offsets;
  for (const auto& [id, delta] : rows) {
    const auto row_id = fbb.CreateString(id);
    const auto ref_type = fbb.CreateString("transfer");
    const auto note = fbb.CreateString("memo");
    const P::WalletRef wallet(P::WalletKind::Player, 4);
    offsets.push_back(P::CreateLedgerRow(fbb, row_id, 1'700'000'000'000'000ULL, P::LedgerKind::Transfer, &wallet, delta, 4,
                                         ref_type, 42, note, false));
  }
  const auto vec = fbb.CreateVector(offsets);
  const auto detail = fbb.CreateString(status == P::EconomyStatus::Ok ? "" : "session not running");
  const auto offset = P::CreateLedgerPage(fbb, &key, status, P::EconomyReject::None, detail, vec, more);
  fbb.Finish(offset);
  return finish(fbb);
}

}  // namespace

TEST_CASE("economy_client: a credit transfer encodes what the server reads", "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(7);
  hub.seed_key(1);

  E::RequestParams params;
  params.to_player = 5;
  params.amount = 2500;
  params.memo = "for the fuel";
  std::string error;
  REQUIRE(hub.request(E::Op::Send, params, error));
  REQUIRE(error.empty());

  const std::vector<E::OutFrame> frames = hub.take_unsent(8);
  REQUIRE(frames.size() == 1);
  CHECK(frames[0].type == static_cast<std::uint16_t>(P::MsgType::CreditTransferRequest));
  CHECK(frames[0].key.lo == 2);
  CHECK(frames[0].key.hi == 7);

  flatbuffers::Verifier verifier(frames[0].bytes.data(), frames[0].bytes.size());
  REQUIRE(verifier.VerifyBuffer<P::CreditTransferRequest>(nullptr));
  const P::CreditTransferRequest* request = flatbuffers::GetRoot<P::CreditTransferRequest>(frames[0].bytes.data());
  REQUIRE(request != nullptr);
  REQUIRE(request->request_key() != nullptr);
  CHECK(request->request_key()->lo() == 2);
  CHECK(request->request_key()->hi() == 7);
  CHECK(request->to_player() == 5);
  CHECK(request->amount() == 2500);
  REQUIRE(request->memo() != nullptr);
  CHECK(request->memo()->str() == "for the fuel");
  CHECK(hub.stats().sent == 0);  // nothing was sent yet: take_unsent only hands it over
  CHECK(hub.stats().requests == 1);
}

TEST_CASE("economy_client: a request that cannot be booked is refused before it leaves", "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(7);
  std::string error;

  E::RequestParams self;
  self.to_player = 7;
  self.amount = 100;
  CHECK_FALSE(hub.request(E::Op::Send, self, error));
  CHECK_FALSE(error.empty());

  E::RequestParams zero;
  zero.to_player = 5;
  zero.amount = 0;
  error.clear();
  CHECK_FALSE(hub.request(E::Op::Send, zero, error));
  CHECK_FALSE(error.empty());

  E::RequestParams no_id;
  no_id.amount = 50;
  error.clear();
  CHECK_FALSE(hub.request(E::Op::Repay, no_id, error));
  CHECK_FALSE(error.empty());

  E::RequestParams forgive;
  forgive.id = id_of(1, 2);
  forgive.amount = 0;  // forgive everything is a valid request
  error.clear();
  CHECK(hub.request(E::Op::Forgive, forgive, error));

  CHECK(hub.take_unsent(8).size() == 1);
  CHECK(hub.stats().bad == 3);
  CHECK(hub.stats().requests == 1);
}

TEST_CASE("economy_client: an EconomyResult is decoded, counted and published", "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(7);

  const std::vector<std::uint8_t> bytes =
      economy_result(2, 7, P::EconomyStatus::Rejected, P::EconomyReject::InsufficientFunds, "not enough credits");
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::EconomyResult), bytes));
  CHECK(hub.stats().results == 1);
  CHECK(hub.stats().rejected == 1);
  REQUIRE(hub.last_result().has_value());
  CHECK(hub.last_result()->status == static_cast<std::uint8_t>(P::EconomyStatus::Rejected));
  CHECK(hub.last_result()->reason == static_cast<std::uint8_t>(P::EconomyReject::InsufficientFunds));
  CHECK(hub.last_result()->detail == "not enough credits");
  CHECK(hub.last_result()->request_key.lo == 2);
  CHECK(hub.last_result()->request_key.hi == 7);
  CHECK(hub.dirty());

  const std::string snapshot = hub.snapshot_json();
  CHECK(snapshot.find("\"ok\":false") != std::string::npos);
  CHECK(snapshot.find("not enough credits") != std::string::npos);
  CHECK(snapshot.find("\"self\":7") != std::string::npos);
  hub.clear_dirty();
  CHECK_FALSE(hub.dirty());
}

TEST_CASE("economy_client: loans and trades are kept by id and an older version never rolls the row back", "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(4);

  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LoanStatus),
                               loan_status(10, 11, P::LoanState::Active, 5000, 5500, 0)));
  REQUIRE(hub.loans().size() == 1);
  CHECK(hub.loans()[0].lender == 3);
  CHECK(hub.loans()[0].borrower == 4);
  CHECK(hub.loans()[0].state == static_cast<std::uint8_t>(P::LoanState::Active));
  CHECK(hub.loans()[0].principal == 5000);
  CHECK(hub.loans()[0].memo == "loan memo");

  // The same loan again with a later state replaces the row instead of adding a second one.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LoanStatus),
                               loan_status(10, 11, P::LoanState::Repaid, 5000, 5500, 5500)));
  REQUIRE(hub.loans().size() == 1);
  CHECK(hub.loans()[0].state == static_cast<std::uint8_t>(P::LoanState::Repaid));

  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeStatus),
                               trade_status(20, 21, 2, P::TradeState::Escrowed, 9, 4)));
  REQUIRE(hub.trades().size() == 1);
  CHECK(hub.trades()[0].version == 2);
  CHECK(hub.trades()[0].state == static_cast<std::uint8_t>(P::TradeState::Escrowed));
  CHECK(hub.trades()[0].initiator.player_id == 9);
  CHECK(hub.trades()[0].counterparty.player_id == 4);

  // An older status (version 1) must not undo the newer one.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeStatus),
                               trade_status(20, 21, 1, P::TradeState::Proposed, 9, 4)));
  REQUIRE(hub.trades().size() == 1);
  CHECK(hub.trades()[0].version == 2);
  CHECK(hub.trades()[0].state == static_cast<std::uint8_t>(P::TradeState::Escrowed));

  const std::string snapshot = hub.snapshot_json();
  CHECK(snapshot.find("\"incoming\":true") != std::string::npos);  // the counterparty is this node
  CHECK(snapshot.find("\"mine\":true") != std::string::npos);
}

TEST_CASE("economy_client: a TradeResult is decoded, and a frame that is not ours is left alone", "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(4);

  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeResult),
                               trade_result(20, 21, P::TradeState::RolledBack, P::EconomyReject::None, "line 2 failed")));
  CHECK(hub.stats().trade_results == 1);
  REQUIRE(hub.last_trade_result().has_value());
  CHECK(hub.last_trade_result()->state == static_cast<std::uint8_t>(P::TradeState::RolledBack));
  CHECK(hub.last_trade_result()->detail == "line 2 failed");

  CHECK_FALSE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::ChatMessage), std::vector<std::uint8_t>{1, 2, 3}));
  CHECK(hub.stats().results == 0);
  CHECK(hub.stats().loans == 0);
  CHECK(hub.stats().trades == 0);

  // A payload of ours that does not verify is counted, never guessed at.
  const std::vector<std::uint8_t> rubbish{1, 2, 3, 4, 5, 6, 7, 8};
  CHECK(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LoanStatus), rubbish));
  CHECK(hub.stats().bad == 1);
  CHECK(hub.loans().empty());
}

TEST_CASE("economy_client: the op names the chat commands use parse, and nothing else does", "[economy_client]") {
  E::Op op = E::Op::Send;
  CHECK(E::parse_op("send", op));
  CHECK(op == E::Op::Send);
  CHECK(E::parse_op("deposit", op));
  CHECK(op == E::Op::Deposit);
  CHECK(E::parse_op("withdraw", op));
  CHECK(op == E::Op::Withdraw);
  CHECK(E::parse_op("repay", op));
  CHECK(op == E::Op::Repay);
  CHECK(E::parse_op("forgive", op));
  CHECK(op == E::Op::Forgive);
  CHECK(E::parse_op("accept", op));
  CHECK(op == E::Op::Accept);
  CHECK(E::parse_op("decline", op));
  CHECK(op == E::Op::Cancel);
  CHECK(E::parse_op("cancel", op));
  CHECK(op == E::Op::Cancel);
  CHECK_FALSE(E::parse_op("donate", op));
  CHECK_FALSE(E::parse_op("", op));
}

TEST_CASE("economy_client: an incoming offer is announced once, and a repeat of the same row is not", "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(4);  // this node

  // A trade where this node is the counterparty: the ball is in its court, so it rings once.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeStatus),
                               trade_offer_with_credits(30, 31, 1, P::TradeState::Proposed, 9, 4, 1200)));
  std::vector<E::Notice> notices = hub.take_notices();
  REQUIRE(notices.size() == 1);
  CHECK(notices[0].kind == E::NoticeKind::TradeOffer);
  CHECK(notices[0].id.lo == 30);
  CHECK(notices[0].id.hi == 31);
  CHECK(notices[0].from == 9);
  CHECK(notices[0].amount == 1200);  // the credits the other side put on the table
  CHECK(notices[0].version == 1);
  CHECK(hub.take_notices().empty());  // taking them is what marks them announced

  // A later status of the same trade is a row update, not a new offer: the player already saw this trade.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeStatus),
                               trade_offer_with_credits(30, 31, 2, P::TradeState::Countered, 9, 4, 1200)));
  CHECK(hub.take_notices().empty());

  // A trade this node started is not an incoming offer.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeStatus),
                               trade_offer_with_credits(32, 33, 1, P::TradeState::Proposed, 4, 9, 500)));
  CHECK(hub.take_notices().empty());

  // A closed trade does not ring even when this node is the counterparty.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::TradeStatus),
                               trade_offer_with_credits(34, 35, 1, P::TradeState::Completed, 9, 4, 500)));
  CHECK(hub.take_notices().empty());

  // A loan rings while it is only offered; the same loan turning active does not.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LoanStatus),
                               loan_status(10, 11, P::LoanState::Offered, 5000, 5500, 0)));
  notices = hub.take_notices();
  REQUIRE(notices.size() == 1);
  CHECK(notices[0].kind == E::NoticeKind::LoanOffer);
  CHECK(notices[0].id.lo == 10);
  CHECK(notices[0].from == 3);  // the lender, from the fixture
  CHECK(notices[0].amount == 5000);
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LoanStatus),
                               loan_status(10, 11, P::LoanState::Active, 5000, 5500, 0)));
  CHECK(hub.take_notices().empty());

  // Two offers were announced in total, and nothing else rang.
  CHECK(hub.stats().notices == 2);
}

TEST_CASE("economy_client: a ledger page replaces the list, a cursor page appends, and one page is in flight at a time",
          "[economy_client]") {
  E::EconomyClientHub hub;
  hub.reset();
  hub.set_self(4);
  hub.seed_key(1);

  // The request the screen sends: a limit and no cursor, so the answer is the newest page.
  E::RequestParams params;
  params.limit = 2;
  std::string error;
  REQUIRE(hub.request(E::Op::Ledger, params, error));
  CHECK(error.empty());
  std::vector<E::OutFrame> frames = hub.take_unsent(8);
  REQUIRE(frames.size() == 1);
  CHECK(frames[0].type == static_cast<std::uint16_t>(P::MsgType::LedgerRequest));
  const P::LedgerRequest* request = flatbuffers::GetRoot<P::LedgerRequest>(frames[0].bytes.data());
  REQUIRE(request != nullptr);
  CHECK(request->limit() == 2);
  CHECK(request->before() == nullptr);  // empty: from the newest row
  CHECK(hub.ledger_pending());

  // A second page while one is on its way would be indistinguishable from the first one's answer.
  CHECK_FALSE(hub.request(E::Op::Ledger, params, error));
  CHECK(error == "a ledger page is already on its way");
  CHECK(hub.take_unsent(8).empty());

  // The newest page: two rows, and the server says there is an older one.
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LedgerPage),
                               ledger_page(1, 4, P::EconomyStatus::Ok, true, {{"ulid-a", -200}, {"ulid-b", 1000}})));
  CHECK_FALSE(hub.ledger_pending());
  REQUIRE(hub.ledger().size() == 2);
  CHECK(hub.ledger()[0].id == "ulid-a");
  CHECK(hub.ledger()[0].delta == -200);
  CHECK(hub.ledger()[0].kind == static_cast<std::uint8_t>(P::LedgerKind::Transfer));
  CHECK(hub.ledger()[0].wallet_kind == static_cast<std::uint8_t>(P::WalletKind::Player));
  CHECK(hub.ledger()[0].wallet_owner == 4);
  CHECK(hub.ledger()[0].actor == 4);
  CHECK(hub.ledger()[0].ref_type == "transfer");
  CHECK(hub.ledger()[0].ref_id == 42);
  CHECK_FALSE(hub.ledger()[0].reversed);
  CHECK(hub.ledger_more());

  // The cursor page: the row the cursor names is not repeated, the older one is appended.
  params.before = "ulid-b";
  REQUIRE(hub.request(E::Op::Ledger, params, error));
  frames = hub.take_unsent(8);
  REQUIRE(frames.size() == 1);
  CHECK(flatbuffers::GetRoot<P::LedgerRequest>(frames[0].bytes.data())->before()->str() == "ulid-b");
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LedgerPage),
                               ledger_page(2, 4, P::EconomyStatus::Ok, false, {{"ulid-b", 1000}, {"ulid-c", -50}})));
  REQUIRE(hub.ledger().size() == 3);
  CHECK(hub.ledger()[2].id == "ulid-c");
  CHECK_FALSE(hub.ledger_more());

  // A refused page keeps the rows the screen already shows: the error is in `last`, not in an empty list.
  params.before = "ulid-c";
  REQUIRE(hub.request(E::Op::Ledger, params, error));
  CHECK(hub.take_unsent(8).size() == 1);
  REQUIRE(hub.on_frame_message(static_cast<std::uint16_t>(P::MsgType::LedgerPage),
                               ledger_page(3, 4, P::EconomyStatus::Rejected, false, {})));
  CHECK(hub.ledger().size() == 3);
  CHECK_FALSE(hub.ledger_pending());
  CHECK(hub.stats().ledger_pages == 3);

  const std::string snapshot = hub.snapshot_json();
  CHECK(snapshot.find("\"ledger_more\":false") != std::string::npos);
  // the UI prints a page when this counter rises: a redraw of the same rows must not print again
  CHECK(snapshot.find("\"ledger_pages\":3") != std::string::npos);
  CHECK(snapshot.find("ulid-a") != std::string::npos);
  CHECK(snapshot.find("ulid-c") != std::string::npos);
}
