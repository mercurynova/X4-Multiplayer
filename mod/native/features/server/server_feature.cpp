#include "features/server/server_feature.h"

#include <filesystem>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/server/server_process.h"
#include "host/host_log.h"

namespace x4mp::features::server {

namespace {

using host::Cat;
using host::Level;

// The bridge body is one short op; anything far beyond that is not a command and is refused, not parsed.
constexpr std::size_t kMaxBody = 256;
// How often the process is re-read. A start takes a second or two, so this is quick enough to feel instant and cheap
// enough to leave running for a whole session.
constexpr double kPollEveryS = 0.25;

void raise_notify(host::HostContext& ctx, const std::string& text) {
  nlohmann::json j;
  j["v"] = 1;
  j["level"] = "error";
  j["text"] = text;
  (void)ctx.platform.raise_lua("x4mp.notify", j.dump());
}

}  // namespace

// The member that the command handler and the launcher refusals report through; the body lives with the other pure
// helpers above so it stays free of class state.
void ServerFeature::notify(host::HostContext& ctx, const std::string& text) { raise_notify(ctx, text); }

void ServerInbox::push(std::string text) {
  const std::lock_guard lock(mutex);
  if (items.size() >= 64) return;
  items.push_back(std::move(text));
}

std::vector<std::string> ServerInbox::take() {
  const std::lock_guard lock(mutex);
  std::vector<std::string> out;
  out.swap(items);
  return out;
}

void ServerFeature::on_init(host::HostContext& ctx) {
  inbox_ = std::make_shared<ServerInbox>();
  if (launcher_ == nullptr) {
    launcher_ = std::make_unique<Launcher>(nullptr, [](std::uint16_t port) { return port_open(port); });
  }
  std::shared_ptr<ServerInbox> inbox = inbox_;
  (void)ctx.platform.subscribe_event("x4mp.server", [inbox](std::string_view text) { inbox->push(std::string(text)); });
  X4MP_CLOG(ctx.log, Cat::Host, Level::Info, "server: launcher ready");
}

void ServerFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  now_s_ += info.delta_s;
  drain(ctx);
  if (launcher_ == nullptr) return;
  if (last_poll_s_ >= 0.0 && now_s_ - last_poll_s_ < kPollEveryS) return;
  last_poll_s_ = now_s_;
  publish(ctx, launcher_->poll(plan(ctx), now_s_), false);
}

void ServerFeature::on_shutdown(host::HostContext& ctx) {
  if (launcher_ == nullptr || !launcher_->owns_process()) return;
  if (!ctx.config.server.auto_stop) {
    X4MP_CLOG(ctx.log, Cat::Host, Level::Info, "server: left running, the config says auto_stop is off");
    return;
  }
  Status status;
  (void)launcher_->stop(status);
  X4MP_CLOG(ctx.log, Cat::Host, Level::Info, "server: stopped with the game");
}

Plan ServerFeature::plan(const host::HostContext& ctx) const {
  const std::filesystem::path extension(std::string(ctx.extension_path));
  const std::filesystem::path state = ctx.paths != nullptr ? ctx.paths->dir : extension;
  return plan_from(ctx.config.server, extension, state);
}

void ServerFeature::drain(host::HostContext& ctx) {
  if (inbox_ == nullptr || launcher_ == nullptr) return;
  for (const std::string& body : inbox_->take()) {
    if (body.size() > kMaxBody) {
      notify(ctx, "the server command is too long");
      continue;
    }
    const auto j = nlohmann::json::parse(body, nullptr, false);
    if (j.is_discarded() || !j.is_object()) {
      notify(ctx, "the server command is not a JSON object");
      continue;
    }
    const auto name = j.find("op");
    if (name == j.end() || !name->is_string()) {
      notify(ctx, "the server command names no op");
      continue;
    }
    Op op{};
    if (!parse_op(name->get<std::string>(), op)) {
      notify(ctx, "unknown server op '" + name->get<std::string>() + "'");
      continue;
    }

    const Plan current = plan(ctx);
    Status status;
    switch (op) {
      case Op::Start: {
        const bool ok = launcher_->start(current, now_s_, status);
        publish(ctx, status, true);
        if (!ok) notify(ctx, status.error.empty() ? "the server did not start" : status.error);
        break;
      }
      case Op::Stop: {
        const bool ok = launcher_->stop(status);
        publish(ctx, status, true);
        if (!ok) notify(ctx, status.error.empty() ? "the server did not stop" : status.error);
        break;
      }
      case Op::Status:
        publish(ctx, launcher_->poll(current, now_s_), true);
        break;
    }
  }
}

void ServerFeature::publish(host::HostContext& ctx, const Status& status, bool force) {
  const Plan current = plan(ctx);
  const std::string json = status_json(status, current);
  if (!force && json == published_) return;
  published_ = json;
  (void)ctx.platform.raise_lua("x4mp.server", json);
  if (!status.error.empty()) {
    X4MP_CLOG(ctx.log, Cat::Host, Level::Warn, "server: {} port={} error={}", status.state, current.port, status.error);
  } else if (force) {
    X4MP_CLOG(ctx.log, Cat::Host, Level::Info, "server: {} pid={} port={} listening={}", status.state, status.pid,
              current.port, status.listening);
  }
}

}  // namespace x4mp::features::server
