#pragma once
// features/economy/trade_report: the client half of M5-13/M5-14.
//
// The MD hook (md/x4mp_events.xml, event_player_trade_completed) already turns a completed trade into a T line, and the events
// feature parses it into an MdEvent: component = the station's local component, macro = the ware, amount = the signed amount the
// ship gained (buy) or lost (sell), price = the unit price. This class turns that into exactly one Intent{TradeReport}:
//
//   station  statics_hub().station_net_id(component)  0 = this node does not know that station: count it, send nothing
//   ship     selfship_hub().own_net_id()              0 = no own ship yet: count it, send nothing
//   ware_ref ware_ref_of(macro)                       the string table ref; `ware` carries the macro itself (W4 part 1)
//
// Dedupe: the hook and the cargo-diff fallback can both notice one trade, so a (station, ware, second) key is kept in a small ring
// and a repeat is dropped (docs/m5-plan.md: hook + fallback dedupe by (kind, subject, second)).
//
// The class is header-only and takes its world through TradeReportDeps, so a test drives it without a game or a session.

#include <array>
#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "features/economy/economy_hub.h"
#include "features/events/event_types.h"
#include "features/events/event_wire.h"

namespace x4mp::features::economy {

struct TradeReportDeps {
  std::function<std::uint32_t(std::uint64_t component)> station_net_id;  // 0: unknown to this node
  std::function<std::uint32_t()> self_ship_net_id;                       // 0: no own ship
  std::function<std::uint16_t()> self_player_id;
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_control;
  std::function<std::int64_t()> now_ms;  // the dedupe clock; 0 = use the event's own timestamp
};

struct TradeReportStats {
  std::uint64_t sent = 0;
  std::uint64_t deduped = 0;
  std::uint64_t unmapped_station = 0;
  std::uint64_t unmapped_ship = 0;
  std::uint64_t send_failed = 0;
  std::uint64_t bad_amount = 0;
};

class TradeReporter {
 public:
  explicit TradeReporter(TradeReportDeps deps) : deps_(std::move(deps)) {}

  // True when `event` was a trade this reporter handled (sent or counted); false for every other kind.
  bool on_event(const events::MdEvent& event) {
    if (event.kind != events::EventKind::Trade) return false;
    if (event.amount == 0) {
      ++stats_.bad_amount;  // a zero trade is not a trade
      return true;
    }
    const std::uint32_t station = deps_.station_net_id ? deps_.station_net_id(event.component) : 0;
    if (station == 0) {
      ++stats_.unmapped_station;
      return true;
    }
    const std::uint32_t ship = deps_.self_ship_net_id ? deps_.self_ship_net_id() : 0;
    if (ship == 0) {
      ++stats_.unmapped_ship;
      return true;
    }
    const std::int64_t second = (deps_.now_ms ? deps_.now_ms() : event.at_ms) / 1000;
    if (seen(station, event.macro, second)) {
      ++stats_.deduped;
      return true;
    }
    remember(station, event.macro, second);

    events::TradeReportData trade;
    trade.station = station;
    trade.ship = ship;
    trade.ware = event.macro;
    trade.ware_ref = ware_ref_of(event.macro);
    trade.amount = static_cast<std::int32_t>(event.amount);
    trade.unit_price = static_cast<std::int32_t>(event.price);

    events::IntentRequest request;
    request.key_lo = ++next_key_;
    request.key_hi = deps_.self_player_id ? deps_.self_player_id() : 0;
    request.request_id = static_cast<std::uint32_t>(next_key_);
    request.player_id = static_cast<std::uint16_t>(request.key_hi);

    const std::vector<std::uint8_t> bytes = events::encode_trade_report(request, trade);
    if (!deps_.send_control || !deps_.send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::Intent), bytes)) {
      ++stats_.send_failed;
      return true;
    }
    ++stats_.sent;
    return true;
  }

  [[nodiscard]] const TradeReportStats& stats() const noexcept { return stats_; }
  void reset() {
    stats_ = TradeReportStats{};
    next_key_ = 0;
    recent_ = {};
    next_slot_ = 0;
  }

 private:
  struct Key {
    std::uint32_t station = 0;
    std::string ware;
    std::int64_t second = 0;
    bool set = false;
  };
  static constexpr std::size_t kRing = 8;

  [[nodiscard]] bool seen(std::uint32_t station, const std::string& ware, std::int64_t second) const {
    for (const Key& key : recent_) {
      if (key.set && key.station == station && key.second == second && key.ware == ware) return true;
    }
    return false;
  }
  void remember(std::uint32_t station, const std::string& ware, std::int64_t second) {
    Key& slot = recent_[next_slot_];
    slot.station = station;
    slot.ware = ware;
    slot.second = second;
    slot.set = true;
    next_slot_ = (next_slot_ + 1) % kRing;
  }

  TradeReportDeps deps_;
  TradeReportStats stats_{};
  std::array<Key, kRing> recent_{};
  std::size_t next_slot_ = 0;
  std::uint64_t next_key_ = 0;
};

}  // namespace x4mp::features::economy
