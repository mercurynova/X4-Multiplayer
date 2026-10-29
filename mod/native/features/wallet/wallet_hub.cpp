#include "features/wallet/wallet_hub.h"

#include <limits>

#include <nlohmann/json.hpp>

namespace x4mp::features::wallet {

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

void WalletHub::reset() { *this = WalletHub{}; }

WalletHub& wallet_hub() {
  static WalletHub hub;
  return hub;
}

}  // namespace x4mp::features::wallet
