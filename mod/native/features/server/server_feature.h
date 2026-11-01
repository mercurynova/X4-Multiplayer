#pragma once
// features/server/server_feature: the dedicated server the game starts itself (M5-32).
//
// Lua x4mp.server {"op":"start|stop|status"} -> inbox -> launcher on the main thread -> topic x4mp.server
// {"v":1,"state":"stopped|starting|running|exited","pid":N,"port":47780,...}. The menu draws that snapshot and offers two
// buttons; /mp server start|stop|status says the same thing in chat.
//
// The server is a self-contained bundle beside the extension (<extension>/server), so it needs no installed .NET and no
// game install of its own: it is a plain process, started hidden, with its output appended to one log file. The game stops
// it on exit when the config says so, so a second start never finds the ports taken.
//
// The event thread only copies the body into an inbox; on_frame parses it on the main thread (same split as wallet and
// economy), because spawning a process from the event thread would be a thread-safety trap for no gain.

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "features/server/server_launcher.h"
#include "host/feature.h"

namespace x4mp::features::server {

// Event thread -> frame thread. The menu can only send a handful of lines per second; a flood is capped.
struct ServerInbox {
  void push(std::string text);
  [[nodiscard]] std::vector<std::string> take();

  std::mutex mutex;
  std::vector<std::string> items;
};

class ServerFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "server"; }

  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

  // A test injects a launcher over a fake process host; the game leaves this alone and gets the Win32 one.
  void use_launcher(std::unique_ptr<Launcher> launcher) { launcher_ = std::move(launcher); }

 private:
  [[nodiscard]] Plan plan(const host::HostContext& ctx) const;
  void drain(host::HostContext& ctx);
  // Publish the snapshot to Lua when something changed, or when force says the player asked and expects an answer.
  void publish(host::HostContext& ctx, const Status& status, bool force);
  void notify(host::HostContext& ctx, const std::string& text);

  std::shared_ptr<ServerInbox> inbox_;
  std::unique_ptr<Launcher> launcher_;
  double now_s_ = 0.0;
  double last_poll_s_ = -1.0;
  std::string published_;
};

}  // namespace x4mp::features::server
