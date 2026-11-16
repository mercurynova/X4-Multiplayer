#include "features/admin/admin_feature.h"

#include <algorithm>
#include <chrono>
#include <mutex>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include <nlohmann/json.hpp>

#include "admin_generated.h"
#include "features/admin/admin_hub.h"
#include "features/onfoot/onfoot_hub.h"
#include "message_ids_generated.h"

namespace x4mp::features {

namespace P = X4MP::Proto;
using host::Cat;
using host::Level;

namespace {

constexpr std::size_t kInboxCap = 8;
constexpr std::size_t kMaxPayloadBytes = 512;
constexpr std::uint32_t kMaxBanMinutes = 5'256'000;  // the admin API's ceiling

}  // namespace

struct AdminFeature::Inbox {
  std::mutex m;
  std::vector<std::string> q;

  void push(std::string_view text) {
    if (text.size() > kMaxPayloadBytes) return;
    std::lock_guard<std::mutex> lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(text);
  }

  std::vector<std::string> take() {
    std::lock_guard<std::mutex> lock(m);
    return std::exchange(q, {});
  }
};

AdminFeature::AdminFeature() : inbox_(std::make_shared<Inbox>()) {}
AdminFeature::~AdminFeature() = default;

void AdminFeature::on_init(host::HostContext& ctx) {
  const auto inbox = inbox_;
  (void)ctx.platform.subscribe_event("x4mp.admin", [inbox](std::string_view text) { inbox->push(text); });
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "admin: ready (in-game kick/ban for an Admin-role node)");
}

void AdminFeature::on_frame(host::HostContext& ctx, const host::FrameInfo&) {
  drain(ctx);
  publish(ctx);
}

void AdminFeature::drain(host::HostContext& ctx) {
  for (const std::string& payload : inbox_->take()) handle(ctx, payload);
}

void AdminFeature::handle(host::HostContext& ctx, std::string_view payload) {
  const nlohmann::json j = nlohmann::json::parse(payload, nullptr, false);
  if (j.is_discarded() || !j.is_object()) {
    refuse(ctx, 0, "the request is not a JSON object");
    return;
  }

  const std::string verb = j.value("verb", std::string());
  const int player = j.value("player", 0);
  const std::string reason = j.value("reason", std::string());
  if (player <= 0 || player > 0xFFFF) {
    refuse(ctx, 0, "a positive player id is required");
    return;
  }

  if (verb == "kick") {
    (void)send_kick(ctx, static_cast<std::uint32_t>(player), reason);
    return;
  }

  if (verb == "ban") {
    const int minutes = j.value("minutes", 0);
    if (minutes < 0) {
      refuse(ctx, 0, "minutes must not be negative");
      return;
    }
    const std::uint32_t duration_s = static_cast<std::uint32_t>(minutes) * 60u;
    (void)send_ban(ctx, static_cast<std::uint32_t>(player), reason, duration_s);
    return;
  }

  refuse(ctx, 0, "only kick and ban are available in game; use the admin API for the rest");
}

bool AdminFeature::send_kick(host::HostContext& ctx, std::uint32_t player, std::string_view reason) {
  if (++request_id_ == 0) ++request_id_;  // 0 means "no request" on the wire
  fbb_.Clear();
  const auto reason_off = fbb_.CreateString(reason);
  const auto body = P::CreateKickCmd(fbb_, static_cast<std::uint16_t>(player), reason_off);
  const auto root = P::CreateAdminCommand(fbb_, nullptr, request_id_, P::AdminBody::KickCmd, body.Union());
  fbb_.Finish(root);
  return send(ctx, {fbb_.GetBufferPointer(), fbb_.GetSize()});
}

bool AdminFeature::send_ban(host::HostContext& ctx, std::uint32_t player, std::string_view reason,
                            std::uint32_t duration_s) {
  if (duration_s / 60u > kMaxBanMinutes) {
    refuse(ctx, 0, "the ban is longer than the server allows");
    return false;
  }
  if (++request_id_ == 0) ++request_id_;
  fbb_.Clear();
  const auto reason_off = fbb_.CreateString(reason);
  const auto body = P::CreateBanCmd(fbb_, static_cast<std::uint16_t>(player), reason_off, duration_s);
  const auto root = P::CreateAdminCommand(fbb_, nullptr, request_id_, P::AdminBody::BanCmd, body.Union());
  fbb_.Finish(root);
  return send(ctx, {fbb_.GetBufferPointer(), fbb_.GetSize()});
}

bool AdminFeature::send(host::HostContext& ctx, std::span<const std::uint8_t> payload) {
  auto& hub = onfoot::onfoot_hub();
  if (!hub.linked()) {
    if (!logged_unlinked_) {
      logged_unlinked_ = true;
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "admin: the in-game link is down, the command was not sent");
    }
    ++failed_;
    return false;
  }
  logged_unlinked_ = false;
  if (!hub.send_control(static_cast<std::uint16_t>(P::MsgType::AdminCommand), payload)) {
    ++failed_;
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "admin: the Control-lane send failed (request {})", request_id_);
    return false;
  }
  ++sent_;
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "admin: request {} sent", request_id_);
  return true;
}

void AdminFeature::publish(host::HostContext& ctx) {
  for (const admin::Result& result : admin::admin_hub().take_results()) {
    nlohmann::json j;
    j["v"] = 1;
    j["request"] = result.request_id;
    j["ok"] = result.ok;
    j["message"] = result.message;
    if (!ctx.platform.raise_lua("x4mp.admin_result", j.dump())) {
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Debug, "admin: result {} not delivered to Lua", result.request_id);
    }
    X4MP_CLOG(ctx.log, Cat::Ui, result.ok ? Level::Info : Level::Warn, "admin: request {} -> {} ({})", result.request_id,
              result.ok ? "ok" : "refused", result.message);
  }
}

void AdminFeature::refuse(host::HostContext& ctx, std::uint32_t request_id, std::string_view why) {
  ++refused_;
  nlohmann::json j;
  j["v"] = 1;
  j["request"] = request_id;
  j["ok"] = false;
  j["message"] = std::string(why);
  (void)ctx.platform.raise_lua("x4mp.admin_result", j.dump());
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "admin: request refused: {}", why);
}

}  // namespace x4mp::features
