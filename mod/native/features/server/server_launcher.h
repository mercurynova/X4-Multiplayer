#pragma once
// features/server/server_launcher: what to start, and what state it is in (M5-32).
//
// The game starts the dedicated server that ships beside the extension. This file decides the executable, the data
// folder, the log file, the arguments and the ports (pure, and therefore testable without a game), and owns the small
// state machine on top of a ProcessHost:
//
//   stopped -> starting -> running        (the port answers)
//   running -> stopped                    (the player asked to stop)
//   running -> exited                     (it died; the exit code says how)
//   stopped -> running (external)         (a server this game did not start answers on the port)
//
// Nothing here touches Lua or the host context; server_feature is the adapter that does.

#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "core/config/config.h"
#include "features/server/server_process.h"

namespace x4mp::features::server {

enum class Op : std::uint8_t { Start = 0, Stop, Status };

// "start", "stop", "status"; anything else is refused, because a typo must never start a process.
[[nodiscard]] bool parse_op(std::string_view name, Op& op);

struct Plan {
  std::filesystem::path exe;
  std::filesystem::path data_dir;
  std::filesystem::path log_file;
  std::vector<std::string> args;
  std::vector<std::pair<std::string, std::string>> env;
  std::uint16_t port = 47780;
  std::uint16_t http_port = 47790;
  bool auto_stop = true;
  // The authority join is proved against X4MP:Net:AdminPassword, so the game that starts a server must also hand it a
  // password: generated once, kept beside the data folder, and the same on every later start.
  std::string admin_password;
  std::string error;  // non-empty: this plan cannot be started, and the screen shows exactly this
};

// extension_dir is the x4mp extension folder (the bundle lives in <extension>/server), state_dir is <LocalAppData>/X4MP
// in a normal install and the extension folder in portable mode. parent_pid is the game process: the server watches it and
// stops when it exits, so it outlives an extension reload but never a closed game (0 = no watcher, used by tests and the
// launcher's own sidecar).
[[nodiscard]] Plan plan_from(const config::ServerConfig& cfg, const std::filesystem::path& extension_dir,
                             const std::filesystem::path& state_dir, std::uint32_t parent_pid = 0);

struct Status {
  std::string state;  // "stopped", "starting", "running", "exited"
  std::uint32_t pid = 0;
  int exit_code = -1;
  bool listening = false;
  // A server this game did not start (the launcher's sidecar, or a previous game process). It is reported as running so
  // the screen tells the truth, but this game must never stop it: its owner is the launcher or that earlier process.
  bool external = false;
  std::string error;
};

class Launcher {
 public:
  using Probe = std::function<bool(std::uint16_t)>;

  // probe is what says "the port answers"; the feature passes port_open, a test passes a flag it controls.
  explicit Launcher(std::unique_ptr<ProcessHost> host, Probe probe = {}, double start_grace_s = 20.0);

  // Starts the plan at monotonic time now_s. Refuses a plan that cannot run and a second start.
  bool start(const Plan& plan, double now_s, Status& out);
  bool stop(Status& out);
  // Re-reads the process. Cheap enough for every frame; the feature calls it a few times a second. probe_external asks the
  // port again; false reuses the last answer, so the feature can check for the launcher's server once every few seconds
  // instead of four times a second.
  [[nodiscard]] Status poll(const Plan& plan, double now_s, bool probe_external = true);

  [[nodiscard]] bool owns_process() const { return started_; }

 private:
  std::unique_ptr<ProcessHost> host_;
  Probe probe_;
  double grace_s_ = 20.0;
  double started_at_ = 0.0;
  bool started_ = false;
  bool listening_seen_ = false;
  bool external_ = false;
  std::string error_;
};

// The snapshot the Lua half reads: one object, every field always present, so the screen never has to guess.
[[nodiscard]] std::string status_json(const Status& status, const Plan& plan);

}  // namespace x4mp::features::server
