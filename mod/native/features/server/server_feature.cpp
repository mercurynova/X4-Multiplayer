#include "features/server/server_feature.h"

#include <chrono>
#include <filesystem>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/server/server_process.h"
#include "host/frame_budget.h"
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
// A server the game did not start (the launcher's sidecar) is found by asking its port. That is a socket connect, so it
// is checked every few seconds, not on every poll; the last answer is reused in between and nothing flickers.
constexpr double kExternalProbeEveryS = 2.0;
// A poll that costs more than this is not the process/socket check it claims to be: name the phases.
constexpr std::int64_t kSlowPollNs = 2'000'000;

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

ServerFeature::~ServerFeature() = default;

void ServerFeature::on_init(host::HostContext& ctx) {
  inbox_ = std::make_shared<ServerInbox>();
  if (launcher_ == nullptr) {
    // The socket probe runs on its own thread: a connect that the kernel leaves waiting costs tens of milliseconds, and this
    // feature asks four times a second on the frame thread (criterion 11 measured exactly that).
    probe_ = std::make_unique<PortProbe>();
    PortProbe* probe = probe_.get();
    launcher_ = std::make_unique<Launcher>(nullptr, [probe](std::uint16_t port) { return probe->open(port); });
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
  const bool probe_external = last_external_probe_s_ < 0.0 || now_s_ - last_external_probe_s_ >= kExternalProbeEveryS;
  if (probe_external) last_external_probe_s_ = now_s_;
  // Criterion 11: the launcher's poll is the only real work this feature does per 0.25 s, and it touches the disk and a socket.
  // A poll that costs milliseconds is worth a line even when nothing else in the frame did.
  const std::int64_t t_plan = host::qpc_now_ns();
  const Plan& current = plan(ctx);
  const std::int64_t t_poll = host::qpc_now_ns();
  const Status status = launcher_->poll(current, now_s_, probe_external);
  const std::int64_t t_done = host::qpc_now_ns();
  publish(ctx, status, false);
  const std::int64_t t_pub = host::qpc_now_ns();
  if (t_pub - t_plan > kSlowPollNs) {
    X4MP_CLOG(ctx.log, Cat::Perf, Level::Info, "server: slow poll plan={}us poll={}us publish={}us", (t_poll - t_plan) / 1000,
              (t_done - t_poll) / 1000, (t_pub - t_done) / 1000);
  }
}

void ServerFeature::on_shutdown(host::HostContext& ctx) {
  // Nothing is stopped here on purpose. X4N_SHUTDOWN fires for a save load and /reloadui too, and this extension then
  // reloads while the game keeps running: stopping the sidecar there would leave the reloaded game and every client
  // without a listener. The server watches the game's process (--parent-pid) and exits with it, crash included.
  if (launcher_ != nullptr && launcher_->owns_process()) {
    X4MP_CLOG(ctx.log, Cat::Host, Level::Info,
              "server: left running across the extension reload; it exits with the game process (--parent-pid)");
  }
}

Plan const& ServerFeature::plan(const host::HostContext& ctx) const {
  if (!plan_cache_) {
    const std::filesystem::path extension(std::string(ctx.extension_path));
    const std::filesystem::path state = ctx.paths != nullptr ? ctx.paths->dir : extension;
    plan_cache_ = plan_from(ctx.config.server, extension, state, current_process_id());
  }
  return *plan_cache_;
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

    const Plan& current = plan(ctx);
    Status status;
    switch (op) {
      case Op::Start: {
        // A start is a button press, not a frame: ask for a fresh answer so the "already listening" refusal is about now, not
        // about the last tick of the probe.
        if (probe_ != nullptr) (void)probe_->open(current.port, std::chrono::milliseconds(300));
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
        last_external_probe_s_ = now_s_;
        publish(ctx, launcher_->poll(current, now_s_, true), true);
        break;
    }
  }
}

void ServerFeature::publish(host::HostContext& ctx, const Status& status, bool force) {
  const Plan& current = plan(ctx);
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
