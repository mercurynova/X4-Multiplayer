#include "features/economy_client/economy_client_feature.h"

#include <chrono>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/economy_client/economy_client_hub.h"
#include "features/events/events_hub.h"
#include "host/host_log.h"

namespace x4mp::features::economy_client {

namespace {

using host::Cat;
using host::Level;

constexpr std::int64_t kLogUs = 30'000'000;
// A frame that queues a burst must not send a burst: the rest goes in the following frames, in order.
constexpr std::size_t kMaxSendsPerFrame = 8;
// The bridge body is a few hundred bytes; anything far beyond that is not a command and is refused, not parsed.
constexpr std::size_t kMaxBody = 512;

std::int64_t now_us() {
  using namespace std::chrono;
  return duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
}

// One command from Lua. A body that is not a JSON object, or names no known op, is counted and dropped: the screen must
// never be able to book something the player did not ask for.
bool parse_request(std::string_view body, Op& op, RequestParams& params) {
  if (body.size() > kMaxBody) return false;
  nlohmann::json j = nlohmann::json::parse(body, nullptr, false);
  if (j.is_discarded() || !j.is_object()) return false;
  const auto name = j.find("op");
  if (name == j.end() || !name->is_string()) return false;
  if (!parse_op(name->get<std::string>(), op)) return false;
  if (const auto it = j.find("to"); it != j.end() && it->is_number_unsigned()) {
    const std::uint64_t to = it->get<std::uint64_t>();
    if (to > 0xFFFF) return false;
    params.to_player = static_cast<std::uint16_t>(to);
  }
  if (const auto it = j.find("amount"); it != j.end() && it->is_number_integer()) {
    params.amount = it->get<std::int64_t>();
  }
  if (const auto it = j.find("id"); it != j.end() && it->is_string()) {
    if (!Id128Value::parse(it->get<std::string>(), params.id)) return false;
  }
  if (const auto it = j.find("version"); it != j.end() && it->is_number_unsigned()) {
    params.version = it->get<std::uint32_t>();
  }
  if (const auto it = j.find("asset"); it != j.end() && it->is_number_unsigned()) {
    params.receive_into_asset = it->get<std::uint32_t>();
  }
  if (const auto it = j.find("memo"); it != j.end() && it->is_string()) {
    params.memo = it->get<std::string>();
    if (params.memo.size() > 120) params.memo.resize(120);
  }
  return true;
}

void notify(host::HostContext& ctx, const std::string& text) {
  nlohmann::json j;
  j["v"] = 1;
  j["level"] = "error";
  j["text"] = text;
  (void)ctx.platform.raise_lua("x4mp.notify", j.dump());
}

}  // namespace

void EconomyInbox::push(std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= 64) items.erase(items.begin());  // a stuck frame thread must not grow this without bound
  items.push_back(std::move(text));
}

std::vector<std::string> EconomyInbox::take() {
  const std::lock_guard lock(mutex);
  return std::exchange(items, {});
}

void EconomyClientFeature::on_init(host::HostContext& ctx) {
  economy_client_hub().reset();
  // M5-29: the key counter starts at the wall clock, so a restart never reuses (1, player) with a different payload - the
  // ledger answers a reused key with RequestIdReuse and the action would look refused for no reason the player can see.
  economy_client_hub().seed_key(static_cast<std::uint64_t>(now_us()));
  inbox_ = std::make_shared<EconomyInbox>();
  auto inbox = inbox_;
  const bool ok = ctx.platform.subscribe_event("x4mp.economy", [inbox](std::string_view text) {
    if (text.size() <= kMaxBody) inbox->push(std::string(text));
  });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "economy_client: init (bridge verb x4mp.economy {})", ok ? "subscribed" : "not available");
}

void EconomyClientFeature::on_game_loaded(host::HostContext& ctx) {
  // A new universe is a new session on the server: the old rows belong to a session that is gone, and a request queued
  // for it cannot be booked here.
  economy_client_hub().reset();
  linked_prev_ = false;
  last_log_us_ = 0;
  published_ = false;
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "economy_client: a new universe was loaded: the rows are dropped");
}

void EconomyClientFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  universe_ready_ = info.universe_ready;
  if (!info.universe_ready || !inbox_) return;
  const std::int64_t now = now_us();
  economy_client_hub().set_self(events::events_hub().self_player_id());
  const bool linked = events::events_hub().linked();
  if (linked && !linked_prev_) {
    published_ = false;  // the new session's rows and results are published as they arrive
  }
  linked_prev_ = linked;
  drain(ctx);
  flush();
  publish(ctx);
  if (last_log_us_ == 0 || now - last_log_us_ >= kLogUs) {
    last_log_us_ = now;
    report(ctx);
  }
}

void EconomyClientFeature::drain(host::HostContext& ctx) {
  for (const std::string& body : inbox_->take()) {
    Op op = Op::Send;
    RequestParams params;
    if (!parse_request(body, op, params)) {
      // Nothing is booked for a body that is not a command: the screen gets no result, which is the honest answer.
      X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "economy_client: a body that is not a command was dropped");
      continue;
    }
    std::string error;
    if (!economy_client_hub().request(op, params, error)) {
      X4MP_CLOG(ctx.log, Cat::Md, Level::Warn, "economy_client: {} refused locally: {}", op_name(op), error);
      notify(ctx, "X4MP: " + std::string(op_name(op)) + ": " + error);
    }
  }
}

void EconomyClientFeature::flush() {
  if (!events::events_hub().linked()) return;
  for (const OutFrame& frame : economy_client_hub().take_unsent(kMaxSendsPerFrame)) {
    if (!events::events_hub().send_control(frame.type, frame.bytes)) {
      economy_client_hub().note_send_failed();
      return;  // the same frame is first in line next frame, so the order the player asked in is kept
    }
  }
}

void EconomyClientFeature::publish(host::HostContext& ctx) {
  if (!economy_client_hub().dirty() && published_) return;
  if (!ctx.platform.raise_lua("x4mp.economy", economy_client_hub().snapshot_json())) return;  // the next change publishes again
  economy_client_hub().clear_dirty();
  published_ = true;
}

void EconomyClientFeature::report(host::HostContext& ctx) {
  const EconomyStats& s = economy_client_hub().stats();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "[economy_client] requests={} sent={} sendfail={} results={} rejected={} loans={} trades={} trade_results={} bad={} "
            "self={} linked={}",
            s.requests, s.sent, s.send_failed, s.results, s.rejected, s.loans, s.trades, s.trade_results, s.bad,
            economy_client_hub().self(), events::events_hub().linked() ? 1 : 0);
}

void EconomyClientFeature::on_shutdown(host::HostContext& ctx) {
  report(ctx);
  inbox_.reset();
  economy_client_hub().reset();
}

}  // namespace x4mp::features::economy_client
