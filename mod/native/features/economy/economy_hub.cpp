#include "features/economy/economy_hub.h"

namespace x4mp::features::economy {

std::uint32_t ware_ref_of(std::string_view ware) noexcept {
  std::uint32_t h = 2166136261u;
  for (const char c : ware) {
    h ^= static_cast<std::uint32_t>(static_cast<unsigned char>(c));
    h *= 16777619u;
  }
  return h;
}

std::optional<MoneyOp> trade_money_op(std::string_view payer, std::string_view station_owner, std::int32_t amount,
                                      std::int32_t unit_price) {
  if (payer.empty() || station_owner.empty()) return std::nullopt;  // nothing to address
  if (payer == station_owner) return std::nullopt;                  // our own station: the money never left the faction
  if (amount == 0 || unit_price <= 0) return std::nullopt;          // no value moved, or no price the trade could be worth
  const std::int64_t units = amount < 0 ? -static_cast<std::int64_t>(amount) : static_cast<std::int64_t>(amount);
  const std::int64_t total = units * static_cast<std::int64_t>(unit_price);
  if (total <= 0) return std::nullopt;
  // The counter-flow is the opposite of the player's: a buy (amount > 0) pays the station owner, a sale (amount < 0) is paid by it
  // (docs/mod-design.md 5.3 and 12.2: "credits or debits the station owner's faction account").
  if (amount > 0) return MoneyOp{std::string(payer), std::string(station_owner), total};
  return MoneyOp{std::string(station_owner), std::string(payer), total};
}

void EconomyHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload) {
  if (type != static_cast<std::uint16_t>(X4MP::Proto::MsgType::EntityCargo)) return;
  events::CargoView view;
  if (!events::decode_entity_cargo(payload, view)) {
    std::lock_guard<std::mutex> lock(mutex_);
    ++stats_.cargo_bad;
    return;
  }
  std::lock_guard<std::mutex> lock(mutex_);
  if (inbox_.size() >= kMaxInbox) {
    ++stats_.cargo_skipped;  // a full inbox means the frame thread is not draining: count, never grow
    return;
  }
  inbox_.push_back(std::move(view));
}

std::vector<events::CargoView> EconomyHub::take_cargo() {
  std::lock_guard<std::mutex> lock(mutex_);
  std::vector<events::CargoView> out;
  out.swap(inbox_);
  return out;
}

std::size_t EconomyHub::queued() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return cargo_ops_.size() + money_ops_.size();
}

void EconomyHub::queue(const CargoOp& op) {
  if (op.component == 0 || op.ware.empty() || op.amount == 0) return;
  std::lock_guard<std::mutex> lock(mutex_);
  if (cargo_ops_.size() >= kMaxOps) {
    ++stats_.cargo_skipped;
    return;
  }
  // Fold into a line that is already queued for the same container and ware: one trade can touch a ware twice in a frame.
  for (CargoOp& queued : cargo_ops_) {
    if (queued.component == op.component && queued.ware == op.ware) {
      queued.amount += op.amount;
      return;
    }
  }
  cargo_ops_.push_back(op);
}

void EconomyHub::queue(const MoneyOp& op) {
  if (op.from.empty() || op.to.empty() || op.from == op.to || op.amount <= 0) return;
  std::lock_guard<std::mutex> lock(mutex_);
  if (money_ops_.size() >= kMaxOps) {
    ++stats_.money_skipped;
    return;
  }
  money_ops_.push_back(op);
}

std::vector<CargoOp> EconomyHub::take_cargo_ops(std::size_t max) {
  std::lock_guard<std::mutex> lock(mutex_);
  std::vector<CargoOp> out;
  const std::size_t n = cargo_ops_.size() < max ? cargo_ops_.size() : max;
  out.reserve(n);
  for (std::size_t i = 0; i < n; ++i) out.push_back(std::move(cargo_ops_[i]));
  cargo_ops_.erase(cargo_ops_.begin(), cargo_ops_.begin() + static_cast<std::ptrdiff_t>(n));
  if (!cargo_ops_.empty()) ++stats_.cargo_burst;  // the budget was hit: the rest goes out next frame
  return out;
}

std::vector<MoneyOp> EconomyHub::take_money_ops(std::size_t max) {
  std::lock_guard<std::mutex> lock(mutex_);
  std::vector<MoneyOp> out;
  const std::size_t n = money_ops_.size() < max ? money_ops_.size() : max;
  out.reserve(n);
  for (std::size_t i = 0; i < n; ++i) out.push_back(std::move(money_ops_[i]));
  money_ops_.erase(money_ops_.begin(), money_ops_.begin() + static_cast<std::ptrdiff_t>(n));
  return out;
}

void EconomyHub::reset() {
  std::lock_guard<std::mutex> lock(mutex_);
  inbox_.clear();
  cargo_ops_.clear();
  money_ops_.clear();
  stats_ = EconomyStats{};
}

EconomyHub& economy_hub() {
  static EconomyHub hub;
  return hub;
}

}  // namespace x4mp::features::economy
