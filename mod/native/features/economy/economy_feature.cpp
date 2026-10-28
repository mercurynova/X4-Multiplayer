#include "features/economy/economy_feature.h"

#include <chrono>
#include <span>
#include <string>

#include <nlohmann/json.hpp>

#include "features/diag/diag_hub.h"
#include "features/economy/stock_apply.h"
#include "features/events/event_types.h"
#include "features/events/events_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "features/selfship/selfship_hub.h"
#include "features/statics/statics_hub.h"
#include "host/host_log.h"

namespace x4mp::features {

namespace {

using events::CargoLine;
using economy::EconomyStats;
using host::Cat;
using host::Level;
using economy::NamedLine;

// M5-14: the own ship's cargo goes upward at most once a second.
constexpr std::int64_t kOwnCargoUs = 1'000'000;
// M5-13: the docked cargo diff runs every 2 s, never per frame.
constexpr std::int64_t kDiffUs = 2'000'000;
// M5-15: one frame's shim carries at most this many ware lines (a 500 unit trade is one line; a resync can be many).
constexpr std::size_t kPerFrameWares = 64;
constexpr std::size_t kPerFrameMoney = 8;
constexpr std::int64_t kLogUs = 30'000'000;

std::int64_t now_us() {
  using namespace std::chrono;
  return duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
}

// A cheap signature of a hold: count + every (ware, amount). Two equal signatures mean "nothing changed".
std::uint64_t signature(std::span<const game::CargoPod> cargo) {
  std::uint64_t h = 1469598103934665603ull;
  const auto mix = [&h](std::uint64_t v) {
    h ^= v;
    h *= 1099511628211ull;
  };
  mix(cargo.size());
  for (const game::CargoPod& line : cargo) {
    mix(economy::ware_ref_of(line.ware));
    mix(static_cast<std::uint64_t>(static_cast<std::int64_t>(line.amount)));
  }
  return h;
}

// M5-16: the wire carries a ware_ref only. A client names it from its own copy of the hold first (no dependency on the session
// string table being in step) and falls back to the ghost core's table.
std::string name_of(std::uint32_t ref, std::span<const game::CargoPod> local) {
  for (const game::CargoPod& line : local) {
    if (economy::ware_ref_of(line.ware) == ref) return line.ware;
  }
  if (ghosts::GhostCore* core = ghosts::ghost_hub().core()) {
    const std::string_view text = core->strings.value(ref);
    if (!text.empty()) return std::string(text);
  }
  return std::string();
}

}  // namespace

void EconomyFeature::on_init(host::HostContext& ctx) {
  economy::economy_hub().reset();
  economy::TradeReportDeps deps;
  deps.station_net_id = [](std::uint64_t component) { return statics::statics_hub().station_net_id(component); };
  deps.self_ship_net_id = [] { return selfship::selfship_hub().own_net_id(); };
  deps.self_player_id = [] { return events::events_hub().self_player_id(); };
  deps.send_control = [](std::uint16_t type, std::span<const std::uint8_t> payload) {
    return events::events_hub().send_control(type, payload);
  };
  deps.now_ms = [] { return std::int64_t{0}; };  // use the event's own timestamp: it is what MD saw
  reporter_ = std::make_unique<economy::TradeReporter>(std::move(deps));
  economy::economy_hub().set_trade_sink([this](const events::MdEvent& event) { return reporter_->on_event(event); });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "economy: trade capture armed (station ids from the statics scan)");
}

void EconomyFeature::on_game_loaded(host::HostContext&) {
  own_hash_valid_ = false;
  docked_hash_valid_ = false;
  own_read_us_ = 0;
  diff_read_us_ = 0;
}

void EconomyFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  universe_ready_ = info.universe_ready;
  on_authority_ = diag_hub().role() == NodeRole::Authority;
  if (!info.universe_ready) return;
  const std::int64_t now = now_us();
  apply_snapshots(ctx);
  if (!on_authority_) {
    report_own_cargo(ctx, now);
    cargo_diff_fallback(ctx, now);
  }
  flush(ctx);
  if (last_log_us_ == 0 || now - last_log_us_ >= kLogUs) {
    last_log_us_ = now;
    report(ctx, false);
  }
}

void EconomyFeature::apply_snapshots(host::HostContext& ctx) {
  std::vector<events::CargoView> views = economy::economy_hub().take_cargo();
  EconomyStats& stats = economy::economy_hub().stats();
  for (const events::CargoView& view : views) {
    if (on_authority_) continue;  // the authority's copy is the source of truth: its own snapshot is a no-op
    if (view.net_id == 0) {
      ++stats.cargo_bad;
      continue;
    }
    if (view.net_id == selfship::selfship_hub().own_net_id()) continue;  // own ship: the game owns that hold
    const std::uint64_t component = statics::statics_hub().station_component(view.net_id);
    if (component == 0) {
      ++stats.cargo_skipped;  // M5-16: an unmatched station is skipped and counted, never guessed
      continue;
    }
    const std::vector<game::CargoPod> local = ctx.game.cargo(component);
    if (local.empty()) {
      ++stats.cargo_bad;  // an empty read is not evidence of an empty hold (M5-13)
      continue;
    }
    std::vector<NamedLine> remote;
    remote.reserve(view.wares.size());
    for (const events::CargoLine& line : view.wares) {
      std::string ware = name_of(line.ware_ref, local);
      if (ware.empty()) {
        ++stats.cargo_bad;  // a ware this node cannot name is not applied: no guessing
        continue;
      }
      remote.push_back(NamedLine{std::move(ware), line.amount});
    }
    if (remote.empty()) continue;
    std::vector<economy::CargoOp> ops = economy::cargo_delta(component, local, remote);
    ++stats.cargo_in;
    ++applied_snapshots_;
    for (const economy::CargoOp& op : ops) economy::economy_hub().queue(op);
  }
}

void EconomyFeature::report_own_cargo(host::HostContext& ctx, std::int64_t now) {
  const game::UniverseId ship = ctx.game.player_occupied_ship();
  const std::uint32_t net = selfship::selfship_hub().own_net_id();
  if (ship == 0 || net == 0) return;
  if (own_read_us_ != 0 && now - own_read_us_ < kOwnCargoUs) return;
  own_read_us_ = now;
  const std::vector<game::CargoPod> cargo = ctx.game.cargo(ship);
  if (cargo.empty()) return;  // no read, no state change (M5-13)
  const std::uint64_t h = signature(cargo);
  if (own_hash_valid_ && h == own_hash_) return;
  own_hash_ = h;
  own_hash_valid_ = true;
  EconomyStats& stats = economy::economy_hub().stats();
  ++stats.own_changes;
  std::vector<CargoLine> lines;
  lines.reserve(cargo.size());
  for (const game::CargoPod& line : cargo) lines.push_back(CargoLine{economy::ware_ref_of(line.ware), line.amount});
  const std::vector<std::uint8_t> bytes = events::encode_entity_cargo(net, lines);
  if (!events::events_hub().send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::EntityCargo), bytes)) {
    ++stats.own_failed;
    return;
  }
  ++stats.own_sent;
}

void EconomyFeature::cargo_diff_fallback(host::HostContext& ctx, std::int64_t now) {
  if (!ctx.game.player_ship_docked()) {
    docked_hash_valid_ = false;  // the next docking starts a fresh baseline
    return;
  }
  if (diff_read_us_ != 0 && now - diff_read_us_ < kDiffUs) return;
  diff_read_us_ = now;
  EconomyStats& stats = economy::economy_hub().stats();
  const std::vector<game::CargoPod> cargo = ctx.game.cargo(ctx.game.player_occupied_ship());
  if (cargo.empty()) {
    ++stats.diff_failed;  // M5-13: a failed cargo read produces no report
    return;
  }
  ++stats.diff_reads;
  const std::uint64_t h = signature(cargo);
  const bool changed = docked_hash_valid_ && h != docked_hash_;
  docked_hash_ = h;
  docked_hash_valid_ = true;
  if (!changed) return;
  const std::uint64_t sent = reporter_ ? reporter_->stats().sent : 0;
  if (sent == seen_trades_) {
    ++stats.diff_missed;  // the hook missed this one: counted and logged, never turned into a guessed report
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "economy: cargo changed while docked and no trade hook fired (missed={})", stats.diff_missed);
  }
  seen_trades_ = sent;
}

void EconomyFeature::flush(host::HostContext& ctx) {
  std::vector<economy::CargoOp> cargo = economy::economy_hub().take_cargo_ops(kPerFrameWares);
  std::vector<economy::MoneyOp> money = economy::economy_hub().take_money_ops(kPerFrameMoney);
  if (cargo.empty() && money.empty()) return;
  EconomyStats& stats = economy::economy_hub().stats();

  nlohmann::json j;
  j["v"] = 1;
  j["cargo"] = nlohmann::json::array();
  for (const economy::CargoOp& op : cargo) j["cargo"].push_back({op.component, op.ware, op.amount});
  j["money"] = nlohmann::json::array();
  for (const economy::MoneyOp& op : money) j["money"].push_back({op.from, op.to, op.amount});

  const std::string text = j.dump();
  if (!ctx.platform.raise_lua("x4mp.actions", text)) {
    stats.cargo_skipped += cargo.size();
    stats.money_skipped += money.size();
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "economy: actions shim refused {} cargo / {} money line(s)", cargo.size(), money.size());
    return;
  }
  stats.cargo_ops += cargo.size();
  stats.money_ops += money.size();
  ++stats.cargo_frames;
  if (cargo.size() >= kPerFrameWares) ++budget_hits_;
  for (const economy::CargoOp& op : cargo) {
    if (op.amount > 0) ++stats.cargo_out; else ++stats.cargo_applied;
  }
}

void EconomyFeature::report(host::HostContext& ctx, bool force) {
  (void)force;
  const EconomyStats& s = economy::economy_hub().stats();
  const economy::TradeReportStats& t = reporter_ ? reporter_->stats() : economy::TradeReportStats{};
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[economy] trades sent={} deduped={} unmapped={} failed={} | cargo in={} skipped={} bad={} ops={} frames={} burst={} "
            "| money={} | own sent={} changes={} | diff reads={} failed={} missed={} | shim refused={}",
            t.sent, t.deduped, t.unmapped_station + t.unmapped_ship, t.send_failed, s.cargo_in, s.cargo_skipped, s.cargo_bad,
            s.cargo_ops, s.cargo_frames, s.cargo_burst, s.money_ops, s.own_sent, s.own_changes, s.diff_reads, s.diff_failed,
            s.diff_missed, s.cargo_skipped);
}

void EconomyFeature::on_shutdown(host::HostContext& ctx) {
  report(ctx, true);
  economy::economy_hub().set_trade_sink({});
  reporter_.reset();
  economy::economy_hub().reset();
}

}  // namespace x4mp::features
