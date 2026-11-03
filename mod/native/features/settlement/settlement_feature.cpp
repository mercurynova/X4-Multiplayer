#include "features/settlement/settlement_feature.h"

#include <chrono>
#include <utility>

#include "features/economy/economy_hub.h"
#include "features/events/events_hub.h"
#include "features/ghosts/ghost_core.h"
#include "features/ghosts/ghost_hub.h"
#include "features/settlement/settlement_wire.h"
#include "features/teams/team_hub.h"
#include "game/safe_remove.h"
#include "host/host_log.h"

namespace x4mp::features::settlement {

namespace {

using host::Cat;
using host::Level;

constexpr std::int64_t kLogUs = 30'000'000;

std::int64_t now_us() {
  using namespace std::chrono;
  return duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
}

// The actions shim takes the ware id the game knows ("energycells"); the order carries the FNV-1a ref of the macro. The
// session string table is the bridge, and it is the same table the client hashed to build that ref.
std::string ware_name(std::uint32_t ref) {
  if (ghosts::GhostCore* core = ghosts::ghost_hub().core()) {
    const std::string_view text = core->strings.value(ref);
    if (!text.empty()) return std::string(text);
  }
  return std::string();
}

// A ware line is only worth queuing when the source really holds it: the shim has no result to read back in v1.
bool holds(const game::GameApi& g, std::uint64_t component, std::uint32_t ref, std::int64_t amount) {
  std::int64_t total = 0;
  for (const game::CargoPod& pod : g.cargo(component)) {
    if (economy::ware_ref_of(pod.ware) == ref) total += pod.amount;
  }
  return total >= amount;
}

}  // namespace

SettlementHub& settlement_hub() {
  static SettlementHub hub;
  return hub;
}

void SettlementHub::reset() {
  journal_ = Journal{};
  stats_ = SettlementStats{};
  outbox_.clear();
}

void SettlementHub::open_journal(const std::filesystem::path& path) {
  journal_.load(path);
}

void SettlementHub::queue_confirm(ConfirmData data) {
  outbox_.push_back(std::move(data));
}

ConfirmData SettlementHub::confirm_of(const JournalEntry& entry) {
  ConfirmData data;
  data.trade_id = entry.trade_id;
  data.ok = entry.state == State::Applied;
  data.failed_line = entry.failed_line;
  data.compensated = entry.compensated;
  data.error = entry.error;
  return data;
}

bool SettlementHub::on_frame(host::HostContext& ctx, std::uint16_t type, std::span<const std::uint8_t> payload) {
  const auto msg = static_cast<X4MP::Proto::MsgType>(type);
  if (msg == X4MP::Proto::MsgType::AssetTransferOrder) return handle_order(ctx, payload);
  if (msg == X4MP::Proto::MsgType::TradeQuery) return handle_query(ctx, payload);
  return false;
}

bool SettlementHub::handle_query(host::HostContext& ctx, std::span<const std::uint8_t> payload) {
  Id128Value trade;
  if (!decode_trade_query(payload, trade)) {
    ++stats_.bad;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "settlement: a TradeQuery did not verify");
    return true;
  }
  ++stats_.queries;
  ConfirmData answer;
  answer.trade_id = trade;
  if (const JournalEntry* entry = journal_.find(trade)) {
    answer = confirm_of(*entry);
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: TradeQuery {} answered from the journal: state={}", trade.hex(),
              state_name(entry->state));
  } else {
    // The server escrowed something this authority never saw. Saying so is what lets it refund instead of waiting.
    answer.ok = false;
    answer.error = "no record of this trade on the authority";
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "settlement: TradeQuery {} has no journal entry", trade.hex());
  }
  queue_confirm(std::move(answer));
  return true;
}

bool SettlementHub::handle_order(host::HostContext& ctx, std::span<const std::uint8_t> payload) {
  OrderView order;
  if (!decode_asset_transfer_order(payload, order)) {
    ++stats_.bad;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "settlement: an AssetTransferOrder did not verify");
    return true;
  }
  ++stats_.orders;
  const std::string trade = order.trade_id.hex();

  // Settle once. A repeated order is not a second settlement: the same answer goes out again, which is what a resend after a
  // lost confirm needs.
  if (const JournalEntry* known = journal_.find(order.trade_id); known != nullptr && known->state != State::Applying) {
    ++stats_.duplicates;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: order {} repeated, the journal already says {} (applied={} compensated={})",
              trade, state_name(known->state), known->applied, known->compensated ? 1 : 0);
    queue_confirm(confirm_of(*known));
    return true;
  }

  JournalEntry entry;
  entry.trade_id = order.trade_id;
  entry.state = State::Applying;
  entry.lines = static_cast<std::uint32_t>(order.lines.size());
  entry.time_us = static_cast<std::uint64_t>(now_us());

  auto refuse = [&](std::int16_t line, std::string why) {
    entry.state = State::Refused;
    entry.failed_line = line;
    entry.error = std::move(why);
    ++stats_.refused;
    journal_.record(entry);
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "settlement: order {} refused at line {}: {} (nothing was changed)", trade, line,
              entry.error);
    queue_confirm(confirm_of(entry));
  };

  if (order.lines.empty()) {
    refuse(-1, "the order carries no lines");
    return true;
  }

  // Precheck everything before the first change: a refusal here has nothing to undo.
  std::vector<std::string> old_owner;
  std::vector<std::string> new_owner;
  std::vector<std::string> ware_names;
  old_owner.reserve(order.lines.size());
  new_owner.reserve(order.lines.size());
  for (std::size_t i = 0; i < order.lines.size(); ++i) {
    const LineView& line = order.lines[i];
    const auto at = static_cast<std::int16_t>(i);
    if (line.is_ware()) {
      const std::string ware = ware_name(line.ware_ref);
      if (ware.empty()) {
        refuse(at, "the ware is not in the session table");
        return true;
      }
      if (line.asset == 0 || !ctx.game.is_valid_component(line.asset)) {
        refuse(at, "the ware source is not a valid component");
        return true;
      }
      if (line.dest_asset == 0 || !ctx.game.is_valid_component(line.dest_asset)) {
        refuse(at, "the ware destination is not a valid component");
        return true;
      }
      if (line.amount <= 0 || line.amount > 2'000'000'000) {
        refuse(at, "the ware amount is out of range");
        return true;
      }
      if (!holds(ctx.game, line.asset, line.ware_ref, line.amount)) {
        refuse(at, "the source does not hold that many");
        return true;
      }
      ware_names.push_back(ware);
      old_owner.push_back(std::string());
      new_owner.push_back(std::string());
      continue;
    }
    if (line.asset == 0) {
      refuse(at, "the line names no asset");
      return true;
    }
    if (!ctx.game.is_valid_component(line.asset)) {
      refuse(at, "the asset is not a valid component");
      return true;
    }
    if (ctx.game.component_wrecked(line.asset)) {
      refuse(at, "the asset is a wreck");
      return true;
    }
    if (game::is_player_guarded(line.asset)) {
      refuse(at, "the asset belongs to a player");
      return true;
    }
    if (line.to_team == 0) {
      refuse(at, "the line names no team");
      return true;
    }
    const std::string faction = teams::team_hub().faction_of_team(line.to_team);
    if (faction.empty()) {
      refuse(at, "the team has no faction");
      return true;
    }
    const std::string owner = ctx.game.owner_faction(line.asset);
    if (owner.empty()) {
      refuse(at, "the asset has no readable owner");
      return true;
    }
    old_owner.push_back(owner);
    new_owner.push_back(faction);
  }

  // Apply in order. `set_component_owner` is true when the call was made, so the check that follows is the owner itself.
  std::uint32_t applied = 0;
  std::int16_t failed = -1;
  for (std::size_t i = 0; i < order.lines.size(); ++i) {
    const LineView& line = order.lines[i];
    if (line.is_ware()) {
      // Two shim lines: the source loses the ware, the destination gains it. exact= caps at what is there, so the read that
      // follows is the only proof - there is no result to wait for in v1 (x4mp_actions.xml:12-15).
      economy::economy_hub().queue(
          economy::CargoOp{line.asset, ware_names[i], -static_cast<std::int32_t>(line.amount)});
      economy::economy_hub().queue(
          economy::CargoOp{line.dest_asset, ware_names[i], static_cast<std::int32_t>(line.amount)});
      applied = static_cast<std::uint32_t>(i + 1);
      X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: order {} line {} ware '{}' x{} {} -> {}", trade, i, ware_names[i],
                line.amount, line.asset, line.dest_asset);
      continue;
    }
    if (!ctx.game.set_component_owner(line.asset, new_owner[i].c_str())) {
      failed = static_cast<std::int16_t>(i);
      entry.error = "the game refused the owner change";
      break;
    }
    applied = static_cast<std::uint32_t>(i + 1);
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: order {} line {} component {} '{}' -> '{}'", trade, i, line.asset,
              old_owner[i], new_owner[i]);
  }

  entry.applied = applied;
  if (failed < 0) {
    entry.state = State::Applied;
    entry.failed_line = -1;
    ++stats_.applied;
    journal_.record(entry);
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: trade={} applied={} compensated=0 state={}", trade, applied,
              state_name(entry.state));
    queue_confirm(confirm_of(entry));
    return true;
  }

  // Undo the applied prefix in reverse order, which puts every asset back to the owner its line found.
  bool undone = true;
  for (std::uint32_t i = applied; i > 0; --i) {
    const std::size_t index = static_cast<std::size_t>(i - 1);
    const LineView& line = order.lines[index];
    if (line.is_ware()) {
      economy::economy_hub().queue(
          economy::CargoOp{line.dest_asset, ware_names[index], -static_cast<std::int32_t>(line.amount)});
      economy::economy_hub().queue(economy::CargoOp{line.asset, ware_names[index], static_cast<std::int32_t>(line.amount)});
      continue;
    }
    if (!ctx.game.set_component_owner(line.asset, old_owner[index].c_str()) ||
        ctx.game.owner_faction(line.asset) != old_owner[index]) {
      undone = false;
      entry.error += entry.error.empty() ? "" : "; ";
      entry.error += "component " + std::to_string(line.asset) + " could not be returned to '" + old_owner[index] + "'";
      X4MP_CLOG(ctx.log, Cat::Md, Level::Error, "settlement: order {} could not undo component {} to '{}'", trade, line.asset,
                old_owner[index]);
      break;
    }
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: order {} line {} undone, component {} is '{}' again", trade, index,
              line.asset, old_owner[index]);
  }

  entry.failed_line = failed;
  if (undone) {
    entry.state = State::Compensated;
    entry.compensated = true;
    ++stats_.compensated;
  } else {
    // Wares are never invented and assets are never left in a state nobody knows: the residual is named here and in the
    // journal, and the server will not treat the trade as settled.
    entry.state = State::NeedsAdmin;
    entry.compensated = false;
    ++stats_.needs_admin;
  }
  journal_.record(entry);
  X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "settlement: trade={} applied={} compensated={} state={} failed_line={} {}",
            trade, applied, entry.compensated ? 1 : 0, state_name(entry.state), entry.failed_line, entry.error);
  queue_confirm(confirm_of(entry));
  return true;
}

void SettlementHub::step(host::HostContext& ctx) {
  while (!outbox_.empty()) {
    const ConfirmData& data = outbox_.front();
    const std::vector<std::uint8_t> bytes = encode_asset_transfer_confirm(data);
    if (!events::events_hub().send_control(static_cast<std::uint16_t>(X4MP::Proto::MsgType::AssetTransferConfirm), bytes)) {
      ++stats_.confirm_failed;
      X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "settlement: the confirm for {} did not go out, it is retried next frame",
                data.trade_id.hex());
      return;  // the same confirm is first in line next frame, so the server sees one answer per trade, in order
    }
    X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: confirm trade={} ok={} compensated={} failed_line={}", data.trade_id.hex(),
              data.ok ? 1 : 0, data.compensated ? 1 : 0, data.failed_line);
    outbox_.erase(outbox_.begin());
  }
}

void SettlementFeature::on_init(host::HostContext& ctx) {
  settlement_hub().reset();
  // A bare test context has no paths: the journal then keeps its outcomes in memory and answers from them, which is what a
  // session that never restarts needs anyway.
  settlement_hub().open_journal(ctx.paths != nullptr ? ctx.paths->dir / "settlement.json" : std::filesystem::path());
  const std::string& problem = settlement_hub().journal().problem();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: journal {} ({} entries){}", settlement_hub().journal().path().string(),
            settlement_hub().journal().entries().size(), problem.empty() ? "" : (", problem: " + problem));
}

void SettlementFeature::on_universe_ready(host::HostContext& ctx) {
  const Journal& journal = settlement_hub().journal();
  std::size_t pending = 0;
  for (const JournalEntry& entry : journal.entries()) {
    if (entry.state == State::NeedsAdmin) ++pending;
  }
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "settlement: universe ready, {} journal entries, {} of them need an admin", journal.entries().size(),
            pending);
}

void SettlementFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  if (!info.universe_ready) return;
  settlement_hub().step(ctx);
  const std::int64_t now = now_us();
  if (last_log_us_ == 0 || now - last_log_us_ >= kLogUs) {
    last_log_us_ = now;
    report(ctx);
  }
}

void SettlementFeature::report(host::HostContext& ctx) {
  const SettlementStats& s = settlement_hub().stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[settlement] orders={} applied={} compensated={} refused={} needs_admin={} queries={} duplicates={} sendfail={} bad={}",
            s.orders, s.applied, s.compensated, s.refused, s.needs_admin, s.queries, s.duplicates, s.confirm_failed, s.bad);
}

void SettlementFeature::on_shutdown(host::HostContext& ctx) {
  report(ctx);
}

}  // namespace x4mp::features::settlement
