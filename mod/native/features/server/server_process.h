#pragma once
// features/server/server_process: one dedicated server process, started by the game itself (M5-32).
//
// The extension ships a self-contained server bundle (<extension>/server/x4mp-server.exe plus its runtime), so a player
// never installs .NET and never opens a terminal: the menu starts the process, the menu stops it, and the game exits with
// it stopped. This file is the only place that touches Win32; the feature above it is tested with a fake host.
//
// The process is started with CREATE_NO_WINDOW (a hidden console, so no window ever appears) and CREATE_NEW_PROCESS_GROUP
// (so a stop signal could be aimed at this group alone). Its stdout and stderr are appended to one log file, because a
// windowless process that dies leaves no other trace.
//
// Nothing here decides policy: no exe, no port, no retry. The feature owns all of that.

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace x4mp::features::server {

struct SpawnRequest {
  std::filesystem::path exe;       // the server executable; must exist
  std::filesystem::path work_dir;  // its folder: the bundle's runtime files sit beside the exe
  std::filesystem::path log_file;  // stdout and stderr are appended here
  std::vector<std::string> args;   // UTF-8 arguments, e.g. "--data-dir" "C:\...\server-data"
  // Environment variables added to the inherited block. The server reads its ports from X4MP__Net__*, so this is how the
  // launcher says "listen on the port the player configured" without touching the machine's environment.
  std::vector<std::pair<std::string, std::string>> env;
};

struct SpawnResult {
  bool ok = false;
  std::uint32_t pid = 0;
  std::string error;  // human-readable, never contains a secret
};

// A process we started and still own. Every call is safe when nothing runs.
class ProcessHost {
 public:
  virtual ~ProcessHost() = default;

  // Starts the process. Refuses when one is already running: the feature never stacks two servers.
  virtual SpawnResult spawn(const SpawnRequest& request) = 0;
  // True while the process we started is alive; a process that exited stays false and keeps its exit code.
  [[nodiscard]] virtual bool running() = 0;
  [[nodiscard]] virtual std::uint32_t pid() const = 0;
  // The exit code once the process exited by itself; -1 when it is still running or nothing was started.
  [[nodiscard]] virtual int exit_code() = 0;
  // Ends the process and waits for it. Returns false only when the process survived the wait.
  virtual bool stop() = 0;
};

// True when something answers on 127.0.0.1:<port> (the server's node port). Blocking, localhost only, and fast: a closed
// port refuses at once and an open one completes at once.
[[nodiscard]] bool port_open(std::uint16_t port);

[[nodiscard]] std::unique_ptr<ProcessHost> make_process_host();

}  // namespace x4mp::features::server
