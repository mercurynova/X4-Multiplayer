// M5-31: the authority's side of an escrowed trade - the order it receives, the confirm it sends back, and the journal that
// survives a restart (docs/m5-plan.md:522, docs/mod-design.md 12.6).
//
// The frames are built here the way the server builds them (EconomyService.Trades.cs BuildOrder, EconomyModule.Trades.cs
// SendOrderToAuthority), so the decoder is tested against the real wire format rather than against a hand-made struct.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "economy_generated.h"
#include "features/settlement/settlement_journal.h"
#include "features/settlement/settlement_wire.h"

namespace S = x4mp::features::settlement;
namespace P = X4MP::Proto;

namespace {

S::Id128Value id_of(std::uint64_t lo, std::uint64_t hi) {
  S::Id128Value id;
  id.lo = lo;
  id.hi = hi;
  return id;
}

std::vector<std::uint8_t> finish(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

// One AssetTransferOrder with one owner-change line, packed as EconomyService.Trades.cs BuildOrder does it.
std::vector<std::uint8_t> order_with_one_line(std::uint64_t lo, std::uint64_t hi, std::uint32_t asset, std::uint16_t team) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 trade(lo, hi);
  std::vector<flatbuffers::Offset<P::AssetTransferLine>> lines;
  lines.push_back(P::CreateAssetTransferLine(fbb, P::AssetTransferKind::OwnerChange, asset, team, 7));
  const auto vector = fbb.CreateVector(lines);
  const auto order = P::CreateAssetTransferOrder(fbb, &trade, vector, 15000);
  fbb.Finish(order);
  return finish(fbb);
}

// A journal path that does not collide with a real session: the test owns it and removes it at the end.
std::filesystem::path journal_path(const char* name) {
  const std::filesystem::path path = std::filesystem::temp_directory_path() / (std::string("x4mp_settlement_test_") + name + ".json");
  std::error_code ec;
  std::filesystem::remove(path, ec);
  std::filesystem::remove(path.string() + ".tmp", ec);
  return path;
}

}  // namespace

TEST_CASE("settlement: an asset transfer order decodes what the server packs", "[settlement]") {
  const std::vector<std::uint8_t> bytes = order_with_one_line(40, 41, 1234, 2);
  S::OrderView order;
  REQUIRE(S::decode_asset_transfer_order(bytes, order));
  CHECK(order.trade_id.lo == 40);
  CHECK(order.trade_id.hi == 41);
  CHECK(order.deadline_ms == 15000);
  REQUIRE(order.lines.size() == 1);
  CHECK(order.lines[0].kind == static_cast<std::uint8_t>(S::LineKind::OwnerChange));
  CHECK_FALSE(order.lines[0].is_ware());
  CHECK(order.lines[0].asset == 1234);
  CHECK(order.lines[0].to_team == 2);
  CHECK(order.lines[0].to_player == 7);

  // A payload that is not an order is refused, never guessed at.
  S::OrderView broken;
  const std::vector<std::uint8_t> rubbish{1, 2, 3, 4, 5, 6, 7, 8};
  CHECK_FALSE(S::decode_asset_transfer_order(rubbish, broken));
  CHECK(broken.lines.empty());
  CHECK_FALSE(S::decode_asset_transfer_order(std::vector<std::uint8_t>{1, 2}, broken));
}

TEST_CASE("settlement: a confirm carries the outcome the server reads", "[settlement]") {
  S::ConfirmData data;
  data.trade_id = id_of(40, 41);
  data.ok = false;
  data.failed_line = 1;
  data.compensated = true;
  data.error = "line 1 failed, both lines are back";
  const std::vector<std::uint8_t> bytes = S::encode_asset_transfer_confirm(data);

  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  REQUIRE(verifier.VerifyBuffer<P::AssetTransferConfirm>(nullptr));
  const P::AssetTransferConfirm* confirm = flatbuffers::GetRoot<P::AssetTransferConfirm>(bytes.data());
  REQUIRE(confirm != nullptr);
  REQUIRE(confirm->trade_id() != nullptr);
  CHECK(confirm->trade_id()->lo() == 40);
  CHECK(confirm->trade_id()->hi() == 41);
  CHECK_FALSE(confirm->ok());
  CHECK(confirm->failed_line() == 1);
  CHECK(confirm->compensated());
  REQUIRE(confirm->error() != nullptr);
  CHECK(confirm->error()->str() == "line 1 failed, both lines are back");

  // A success carries no failed line and no text, and that has to survive the round trip too.
  S::ConfirmData good;
  good.trade_id = id_of(1, 2);
  good.ok = true;
  const std::vector<std::uint8_t> ok_bytes = S::encode_asset_transfer_confirm(good);
  flatbuffers::Verifier ok_verifier(ok_bytes.data(), ok_bytes.size());
  REQUIRE(ok_verifier.VerifyBuffer<P::AssetTransferConfirm>(nullptr));
  const P::AssetTransferConfirm* ok_confirm = flatbuffers::GetRoot<P::AssetTransferConfirm>(ok_bytes.data());
  REQUIRE(ok_confirm != nullptr);
  CHECK(ok_confirm->ok());
  CHECK(ok_confirm->failed_line() == -1);
  CHECK_FALSE(ok_confirm->compensated());
  CHECK(ok_confirm->error() == nullptr);
}

TEST_CASE("settlement: a TradeQuery names the trade the server lost track of", "[settlement]") {
  flatbuffers::FlatBufferBuilder fbb(64);
  const P::Id128 trade(77, 78);
  const auto query = P::CreateTradeQuery(fbb, &trade);
  fbb.Finish(query);
  const std::vector<std::uint8_t> bytes = finish(fbb);

  S::Id128Value id;
  REQUIRE(S::decode_trade_query(bytes, id));
  CHECK(id.lo == 77);
  CHECK(id.hi == 78);

  S::Id128Value broken;
  CHECK_FALSE(S::decode_trade_query(std::vector<std::uint8_t>{9, 9, 9, 9, 9}, broken));
  CHECK(broken.empty());
}

TEST_CASE("settlement: the journal keeps each trade's outcome across a restart", "[settlement]") {
  const std::filesystem::path path = journal_path("roundtrip");

  S::JournalEntry entry;
  entry.trade_id = id_of(40, 41);
  entry.state = S::State::Applied;
  entry.lines = 2;
  entry.applied = 2;
  entry.time_us = 1000;

  {
    S::Journal journal;
    journal.load(path);  // nothing there yet: an empty journal, not a problem
    CHECK(journal.problem().empty());
    CHECK(journal.entries().empty());
    journal.record(entry);
    CHECK(journal.problem().empty());
    REQUIRE(journal.find(entry.trade_id) != nullptr);
    CHECK(journal.find(entry.trade_id)->state == S::State::Applied);
  }

  // A second object is what a restarted game sees.
  S::Journal reloaded;
  reloaded.load(path);
  CHECK(reloaded.problem().empty());
  REQUIRE(reloaded.entries().size() == 1);
  const S::JournalEntry* found = reloaded.find(id_of(40, 41));
  REQUIRE(found != nullptr);
  CHECK(found->state == S::State::Applied);
  CHECK(found->lines == 2);
  CHECK(found->applied == 2);
  CHECK(found->failed_line == -1);
  CHECK_FALSE(found->compensated);
  CHECK(found->time_us == 1000);
  CHECK(reloaded.find(id_of(1, 2)) == nullptr);

  // A later outcome replaces the row instead of adding a second one for the same trade.
  S::JournalEntry later = entry;
  later.state = S::State::Compensated;
  later.compensated = true;
  later.applied = 0;
  reloaded.record(later);
  REQUIRE(reloaded.entries().size() == 1);
  CHECK(reloaded.find(entry.trade_id)->state == S::State::Compensated);
  CHECK(reloaded.find(entry.trade_id)->compensated);

  std::error_code ec;
  std::filesystem::remove(path, ec);
  std::filesystem::remove(path.string() + ".tmp", ec);
}

TEST_CASE("settlement: an entry that was still applying at load becomes needs_admin", "[settlement]") {
  const std::filesystem::path path = journal_path("applying");
  {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    REQUIRE(out.good());
    out << R"({"v":1,"entries":[{"trade_id":"0000000000000028:0000000000000029","state":"applying","failed_line":-1,"lines":2,"applied":1,"compensated":false,"error":"","time_us":5}]})";
  }

  S::Journal journal;
  journal.load(path);
  CHECK(journal.problem().empty());
  REQUIRE(journal.entries().size() == 1);
  const S::JournalEntry& entry = journal.entries()[0];
  CHECK(entry.trade_id.lo == 40);
  CHECK(entry.trade_id.hi == 41);
  CHECK(entry.state == S::State::NeedsAdmin);
  CHECK(entry.error == "the game stopped during settlement");
  CHECK_FALSE(entry.compensated);
  CHECK(entry.applied == 1);

  std::error_code ec;
  std::filesystem::remove(path, ec);
}

TEST_CASE("settlement: an unreadable journal is reported, not fatal", "[settlement]") {
  const std::filesystem::path path = journal_path("garbage");
  {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    REQUIRE(out.good());
    out << "this is not json at all";
  }

  S::Journal journal;
  journal.load(path);
  CHECK_FALSE(journal.problem().empty());
  CHECK(journal.entries().empty());
  CHECK(journal.find(id_of(40, 41)) == nullptr);

  // The journal stays usable: an order that arrives after a broken file is still recorded, and the write repairs the file.
  S::JournalEntry entry;
  entry.trade_id = id_of(40, 41);
  entry.state = S::State::Refused;
  journal.record(entry);
  CHECK(journal.problem().empty());
  REQUIRE(journal.find(entry.trade_id) != nullptr);

  std::error_code ec;
  std::filesystem::remove(path, ec);
  std::filesystem::remove(path.string() + ".tmp", ec);
}

TEST_CASE("settlement: the journal without a path refuses to write instead of throwing", "[settlement]") {
  S::Journal journal;
  S::JournalEntry entry;
  entry.trade_id = id_of(3, 4);
  entry.state = S::State::Applied;
  journal.record(entry);  // no path: the outcome is kept in memory and the problem is reported
  CHECK_FALSE(journal.problem().empty());
  REQUIRE(journal.find(entry.trade_id) != nullptr);
}
