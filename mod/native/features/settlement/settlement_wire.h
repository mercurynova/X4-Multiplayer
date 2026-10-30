#pragma once
// features/settlement/settlement_wire: the C++ half of AssetTransferOrder, AssetTransferConfirm and TradeQuery (M5-31;
// docs/m5-plan.md M5-31, docs/mod-design.md 12.6, protocol/schema/economy.fbs:209-244).
//
// The server escrows both sides of a trade and then hands the asset side to the authority as ONE order: the authority is the
// only node that may move a ship or cargo in the running universe, and the order is the only instruction it takes from the
// ledger. Three frames exist and this file is their whole surface:
//   S->A AssetTransferOrder   {trade_id, lines:[OwnerChange | WareMove], deadline_ms}
//   A->S AssetTransferConfirm {trade_id, ok, failed_line, compensated, error}
//   S->A TradeQuery           {trade_id}          the server asks what became of an order it never heard about
//
// Same shape as features/economy_client/economy_wire.h: verify the buffer, then copy the fields out, because the pointer
// lives only as long as the payload does. Nothing here touches the universe - settlement_journal and the order handler do.
//
// Two facts the contract depends on, recorded here because they are not obvious from the schema:
//   * an asset belongs to a TEAM faction, not to a player: OwnerChange carries to_team (the faction slot the game is told)
//     and to_player only for the ledger. A tester who expects a personally owned ship will be surprised - see
//     docs/two-pc-checklist.md.
//   * v1 moves ships and wares. A station is an owner change without moving its modules.

#include <cstdint>
#include <span>
#include <string>
#include <vector>

#include "economy_generated.h"
#include "features/economy_client/economy_wire.h"  // Id128Value
#include "flatbuffers/flatbuffers.h"
#include "message_ids_generated.h"

namespace x4mp::features::settlement {

namespace P = X4MP::Proto;
using economy_client::Id128Value;

// AssetTransferKind: 0 OwnerChange, 1 WareMove.
enum class LineKind : std::uint8_t { OwnerChange = 0, WareMove = 1 };

[[nodiscard]] inline const char* line_kind_name(std::uint8_t kind) noexcept {
  return kind == 1 ? "ware" : "owner";
}

struct LineView {
  std::uint8_t kind = 0;  // LineKind
  std::uint32_t asset = 0;
  std::uint16_t to_team = 0;
  std::uint16_t to_player = 0;
  std::uint32_t ware_ref = 0;
  std::int64_t amount = 0;
  std::uint32_t dest_asset = 0;

  [[nodiscard]] bool is_ware() const noexcept { return kind == 1; }
};

struct OrderView {
  Id128Value trade_id;
  std::uint32_t deadline_ms = 15000;
  std::vector<LineView> lines;
};

struct ConfirmData {
  Id128Value trade_id;
  bool ok = false;
  std::int16_t failed_line = -1;
  bool compensated = false;
  std::string error;
};

[[nodiscard]] inline bool decode_asset_transfer_order(std::span<const std::uint8_t> bytes, OrderView& out) {
  out = OrderView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::AssetTransferOrder>(nullptr)) return false;
  const P::AssetTransferOrder* order = flatbuffers::GetRoot<P::AssetTransferOrder>(bytes.data());
  if (order == nullptr) return false;
  if (const P::Id128* id = order->trade_id()) {
    out.trade_id.lo = id->lo();
    out.trade_id.hi = id->hi();
  }
  out.deadline_ms = order->deadline_ms();
  if (const auto* lines = order->lines()) {
    out.lines.reserve(lines->size());
    for (const P::AssetTransferLine* line : *lines) {
      if (line == nullptr) continue;
      LineView view;
      view.kind = static_cast<std::uint8_t>(line->kind());
      view.asset = line->asset();
      view.to_team = line->to_team();
      view.to_player = line->to_player();
      view.ware_ref = line->ware_ref();
      view.amount = line->amount();
      view.dest_asset = line->dest_asset();
      out.lines.push_back(view);
    }
  }
  return true;
}

[[nodiscard]] inline bool decode_trade_query(std::span<const std::uint8_t> bytes, Id128Value& out) {
  out = Id128Value{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::TradeQuery>(nullptr)) return false;
  const P::TradeQuery* query = flatbuffers::GetRoot<P::TradeQuery>(bytes.data());
  if (query == nullptr) return false;
  if (const P::Id128* id = query->trade_id()) {
    out.lo = id->lo();
    out.hi = id->hi();
  }
  return true;
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_asset_transfer_confirm(const ConfirmData& data) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 trade = P::Id128(data.trade_id.lo, data.trade_id.hi);
  const auto error = data.error.empty() ? flatbuffers::Offset<flatbuffers::String>() : fbb.CreateString(data.error);
  const auto confirm = P::CreateAssetTransferConfirm(fbb, &trade, data.ok, data.failed_line, data.compensated, error);
  fbb.Finish(confirm);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

}  // namespace x4mp::features::settlement
