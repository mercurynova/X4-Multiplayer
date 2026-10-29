#pragma once
// features/wallet/wallet_wire: the C++ half of WalletUpdate, the message that tells a node what its money is
// (M5-23; docs/mod-design.md 12.2, protocol/schema/economy.fbs:33-39).
//
// The server is the single source of truth for balances (economy.fbs:2), and WalletUpdate is how it says so: every balance the
// receiver may see, the ledger version of each, and acked_delta_seq, the highest CreditDelta.seq of THIS node the ledger has
// booked. The node converges its local money to serverBalance + its own unbooked deltas; nothing here decides anything, it only
// turns bytes into a view (wallet_hub does the arithmetic).
//
// Same shape as features/events/event_wire.h: verify the buffer, then copy the fields out, because the pointer lives only as long
// as the payload does.
//
// The other direction is one CreditDelta (M5-24, economy.fbs:44-55): the node's own change going up, with the seq the ledger echoes
// back in acked_delta_seq.

#include <cstdint>
#include <span>
#include <vector>

#include "economy_generated.h"
#include "flatbuffers/flatbuffers.h"
#include "message_ids_generated.h"

namespace x4mp::features::wallet {

namespace P = X4MP::Proto;

// One WalletBalance as the wire carries it. `kind`/`owner` are the wallet ref (economy.fbs:17-18: owner_id is the player id for
// Player, the team id for TeamShared/TeamPool, 0 for Escrow); `version` is the ledger sequence, and an older update must be
// ignored (economy.fbs:23).
struct WalletBalanceView {
  std::uint8_t kind = 0;
  std::uint16_t owner = 0;
  std::int64_t balance = 0;
  std::uint64_t version = 0;
};

struct WalletUpdateView {
  std::uint8_t reason = 0;          // LedgerReason: history only, never an amount (mod-design 12.2)
  std::uint8_t effective_mode = 0;  // EffectiveCreditMode: 0 = PerPlayer, 1 = Shared
  std::uint64_t ack_seq = 0;        // highest CreditDelta.seq of this node the ledger has booked (0 = none)
  std::vector<WalletBalanceView> balances;
};

// False when the bytes are not a WalletUpdate (short buffer, failed verification, no root). `out` is cleared first.
[[nodiscard]] inline bool decode_wallet_update(std::span<const std::uint8_t> bytes, WalletUpdateView& out) {
  out = WalletUpdateView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::WalletUpdate>(nullptr)) return false;
  const P::WalletUpdate* update = flatbuffers::GetRoot<P::WalletUpdate>(bytes.data());
  if (update == nullptr) return false;
  out.reason = static_cast<std::uint8_t>(update->reason());
  out.effective_mode = static_cast<std::uint8_t>(update->effective_mode());
  out.ack_seq = update->acked_delta_seq();
  if (const auto* balances = update->balances()) {
    out.balances.reserve(balances->size());
    for (const P::WalletBalance* row : *balances) {
      if (row == nullptr) continue;
      WalletBalanceView view;
      if (const P::WalletRef* wallet = row->wallet()) {
        view.kind = static_cast<std::uint8_t>(wallet->kind());
        view.owner = wallet->owner_id();
      }
      view.balance = row->balance();
      view.version = row->version();
      out.balances.push_back(view);
    }
  }
  return true;
}

// M5-24: one delta of this node, going up (economy.fbs:44-55). `seq` is the node's own counter and comes back as
// WalletUpdate.acked_delta_seq; `request_key` is fixed when the delta is booked, so a resend after a reconnect is the same request
// and the server books it once. player_id/team_id are A->S only (a client's are stamped by the server), so a client leaves them 0.
struct CreditDeltaData {
  std::uint64_t key_lo = 0;
  std::uint64_t key_hi = 0;
  std::int64_t amount = 0;          // signed: positive is income, negative is spending
  std::uint8_t source = 0;          // CreditSource: why it happened, history only
  std::uint64_t ref_event_seq = 0;  // the event that caused it, 0 when none did
  double game_time = 0.0;
  std::uint64_t seq = 0;
};

[[nodiscard]] inline std::vector<std::uint8_t> encode_credit_delta(const CreditDeltaData& data) {
  flatbuffers::FlatBufferBuilder fbb(64);
  const P::Id128 key(data.key_lo, data.key_hi);
  const flatbuffers::Offset<P::CreditDelta> delta =
      P::CreateCreditDelta(fbb, &key, 0, 0, data.amount, static_cast<P::CreditSource>(data.source), data.ref_event_seq,
                           data.game_time, data.seq);
  fbb.Finish(delta);
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

}  // namespace x4mp::features::wallet
