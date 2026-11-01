#include "features/server/server_launcher.h"

#include <algorithm>
#include <fstream>
#include <random>
#include <system_error>

#include <nlohmann/json.hpp>

namespace x4mp::features::server {

namespace {

// The HTTP admin GUI sits ten ports above the node port, the way this project's own dev server does it (47780/47790).
std::uint16_t offset_port(std::uint16_t base, int offset) {
  const int value = static_cast<int>(base) + offset;
  return static_cast<std::uint16_t>(std::clamp(value, 1, 65535));
}

std::filesystem::path absolute_under(const std::filesystem::path& base, const std::filesystem::path& path) {
  return path.is_absolute() ? path : base / path;
}

// 20 characters from an alphabet without look-alikes: the player may have to type it on the other PC.
std::string random_password() {
  static constexpr std::string_view kAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  std::random_device device;
  std::mt19937_64 engine((static_cast<std::uint64_t>(device()) << 32) ^ device());
  std::uniform_int_distribution<std::size_t> pick(0, kAlphabet.size() - 1);
  std::string out;
  out.reserve(20);
  for (int i = 0; i < 20; ++i) out.push_back(kAlphabet[pick(engine)]);
  return out;
}

// The node gateway proves an authority join against X4MP:Net:AdminPassword, so a server started without one refuses the
// very game that started it. The value is generated once and kept beside the data folder, so every later start of the same
// server accepts the same password, and the screen can tell the player where it lives.
std::string admin_password_for(const config::ServerConfig& cfg, const std::filesystem::path& data_dir) {
  if (!cfg.admin_password.empty()) return cfg.admin_password;
  const std::filesystem::path file = data_dir / "admin-password.txt";
  {
    std::ifstream in(file, std::ios::binary);
    if (in) {
      std::string line;
      std::getline(in, line);
      while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
      if (line.size() >= 8) return line;
    }
  }
  const std::string password = random_password();
  std::error_code ec;
  std::filesystem::create_directories(data_dir, ec);
  std::ofstream out(file, std::ios::binary | std::ios::trunc);
  if (out) out << password << '\n';
  return password;
}

}  // namespace

bool parse_op(std::string_view name, Op& op) {
  if (name == "start") {
    op = Op::Start;
    return true;
  }
  if (name == "stop") {
    op = Op::Stop;
    return true;
  }
  if (name == "status") {
    op = Op::Status;
    return true;
  }
  return false;
}

Plan plan_from(const config::ServerConfig& cfg, const std::filesystem::path& extension_dir,
               const std::filesystem::path& state_dir) {
  Plan plan;
  plan.port = static_cast<std::uint16_t>(std::clamp(cfg.port, 1, 65535));
  plan.http_port = offset_port(plan.port, 10);
  plan.auto_stop = cfg.auto_stop;
  plan.exe = cfg.exe.empty() ? extension_dir / "server" / "x4mp-server.exe"
                             : absolute_under(extension_dir, std::filesystem::path(cfg.exe));
  plan.data_dir = cfg.data_dir.empty() ? state_dir / "server-data"
                                       : absolute_under(extension_dir, std::filesystem::path(cfg.data_dir));
  plan.log_file = state_dir / "logs" / "server-console.log";
  plan.args = {"--data-dir", plan.data_dir.string(), "--port", std::to_string(plan.http_port)};
  // The server takes its node ports from the environment (X4MP:Net), so the configured port reaches it without a config
  // file and without touching the machine's own variables.
  plan.env = {{"X4MP__Net__NodeTcpEndpoint", "0.0.0.0:" + std::to_string(plan.port)},
              {"X4MP__Net__UdpPort", std::to_string(offset_port(plan.port, 1))}};
  plan.admin_password = admin_password_for(cfg, plan.data_dir);
  plan.env.emplace_back("X4MP__Net__AdminPassword", plan.admin_password);

  std::error_code ec;
  if (!std::filesystem::is_regular_file(plan.exe, ec)) {
    plan.error = "no server bundle at " + plan.exe.string();
  }
  return plan;
}

Launcher::Launcher(std::unique_ptr<ProcessHost> host, Probe probe, double start_grace_s)
    : host_(std::move(host)), probe_(std::move(probe)), grace_s_(start_grace_s) {
  if (host_ == nullptr) host_ = make_process_host();
}

bool Launcher::start(const Plan& plan, double now_s, Status& out) {
  out = poll(plan, now_s);
  if (!plan.error.empty()) {
    error_ = plan.error;
    out.error = error_;
    return false;
  }
  if (started_ && host_->running()) {
    error_ = "the server is already running";
    out = poll(plan, now_s);
    return false;
  }

  std::error_code ec;
  std::filesystem::create_directories(plan.data_dir, ec);
  std::filesystem::create_directories(plan.log_file.parent_path(), ec);

  SpawnRequest request;
  request.exe = plan.exe;
  request.work_dir = plan.exe.parent_path();
  request.log_file = plan.log_file;
  request.args = plan.args;
  request.env = plan.env;
  const SpawnResult spawned = host_->spawn(request);
  if (!spawned.ok) {
    error_ = spawned.error;
    out = poll(plan, now_s);
    return false;
  }
  started_ = true;
  listening_seen_ = false;
  error_.clear();
  started_at_ = now_s;
  out = poll(plan, now_s);
  return true;
}

bool Launcher::stop(Status& out) {
  out = Status{};
  if (!started_) {
    out.state = "stopped";
    out.error = "the server was not started by this game";
    return false;
  }
  (void)host_->stop();
  started_ = false;
  listening_seen_ = false;
  error_.clear();
  out.state = "stopped";
  return true;
}

Status Launcher::poll(const Plan& plan, double now_s) {
  Status status;
  status.error = error_;
  if (!started_) {
    status.state = "stopped";
    return status;
  }
  status.pid = host_->pid();
  if (host_->running()) {
    const bool listening = probe_ ? probe_(plan.port) : false;
    if (listening) listening_seen_ = true;
    status.listening = listening || listening_seen_;
    status.state = status.listening ? "running" : "starting";
    if (status.state == "starting" && now_s - started_at_ > grace_s_) {
      status.error = "the server is up but its port does not answer";
    }
    return status;
  }

  // The process we started is gone. It counts as exited, not stopped: the player did not ask for this, and the exit code
  // is the only explanation there is. started_ stays true so the screen keeps saying so until a start or a stop.
  status.exit_code = host_->exit_code();
  status.state = "exited";
  if (status.exit_code != 0) {
    status.error = "the server exited with code " + std::to_string(status.exit_code) + ", see " + plan.log_file.string();
  }
  return status;
}

std::string status_json(const Status& status, const Plan& plan) {
  nlohmann::json j;
  j["v"] = 1;
  j["state"] = status.state;
  j["pid"] = status.pid;
  j["exit_code"] = status.exit_code;
  j["listening"] = status.listening;
  j["port"] = plan.port;
  j["http_port"] = plan.http_port;
  j["auto_stop"] = plan.auto_stop;
  j["exe"] = plan.exe.string();
  j["data_dir"] = plan.data_dir.string();
  j["log"] = plan.log_file.string();
  j["admin_password"] = plan.admin_password;
  j["admin_password_file"] = (plan.data_dir / "admin-password.txt").string();
  j["error"] = status.error;
  return j.dump();
}

}  // namespace x4mp::features::server
