#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <system_error>
#include <thread>

#include "features/server/server_launcher.h"
#include "features/server/server_process.h"

// M5-32: the launcher is pure policy over a ProcessHost, so every case here runs without a game, a server or Win32.
// What cannot be tested this way is the CreateProcessW call itself and the real port probe; both are exercised in game.

namespace {

using namespace x4mp::features::server;
using x4mp::config::ServerConfig;

// A process whose whole life the test decides: spawn, die with a code, stop. No process is created.
class FakeProcessHost final : public ProcessHost {
 public:
  SpawnResult spawn(const SpawnRequest& request) override {
    if (alive_) {
      SpawnResult busy;
      busy.error = "a server is already running";
      return busy;
    }
    last = request;
    ++spawns;
    if (!spawn_ok) {
      SpawnResult refused;
      refused.error = spawn_error;
      return refused;
    }
    alive_ = true;
    SpawnResult started;
    started.ok = true;
    started.pid = pid_;
    return started;
  }

  [[nodiscard]] bool running() override { return alive_; }
  [[nodiscard]] std::uint32_t pid() const override { return pid_; }
  [[nodiscard]] int exit_code() override { return exit_code_; }
  bool stop() override {
    alive_ = false;
    ++stops;
    return true;
  }

  void die(int code) {
    alive_ = false;
    exit_code_ = code;
  }

  SpawnRequest last;
  bool spawn_ok = true;
  std::string spawn_error = "cannot start";
  int spawns = 0;
  int stops = 0;

 private:
  bool alive_ = false;
  std::uint32_t pid_ = 4242;
  int exit_code_ = -1;
};

// A throwaway extension folder with a bundle in it, so plan_from sees a real file.
struct TempTree {
  TempTree() {
    std::error_code ec;
    root = std::filesystem::temp_directory_path() / "x4mp-server-plan-test";
    std::filesystem::remove_all(root, ec);
    std::filesystem::create_directories(root / "extension" / "server", ec);
    std::filesystem::create_directories(root / "state", ec);
    std::ofstream(root / "extension" / "server" / "x4mp-server.exe") << 'x';
  }
  ~TempTree() {
    std::error_code ec;
    std::filesystem::remove_all(root, ec);
  }
  TempTree(const TempTree&) = delete;
  TempTree& operator=(const TempTree&) = delete;

  std::filesystem::path root;
};

}  // namespace

TEST_CASE("server: an op is one of three words, and a typo never starts a process", "[server]") {
  Op op = Op::Status;
  CHECK(parse_op("start", op));
  CHECK(op == Op::Start);
  CHECK(parse_op("stop", op));
  CHECK(op == Op::Stop);
  CHECK(parse_op("status", op));
  CHECK(op == Op::Status);

  op = Op::Status;
  CHECK_FALSE(parse_op("Start", op));
  CHECK_FALSE(parse_op("", op));
  CHECK_FALSE(parse_op("launch", op));
  CHECK(op == Op::Status);
}

TEST_CASE("server: the plan names the bundle, the data folder, the log and the ports", "[server]") {
  TempTree tree;
  const auto extension = tree.root / "extension";
  const auto state = tree.root / "state";
  const Plan plan = plan_from(ServerConfig{}, extension, state);

  CHECK(plan.error.empty());
  CHECK(plan.exe == extension / "server" / "x4mp-server.exe");
  CHECK(plan.data_dir == state / "server-data");
  CHECK(plan.log_file == state / "logs" / "server-console.log");
  CHECK(plan.port == 47780);
  CHECK(plan.http_port == 47790);
  CHECK(plan.auto_stop);

  REQUIRE(plan.args.size() == 4);
  CHECK(plan.args[0] == "--data-dir");
  CHECK(plan.args[1] == plan.data_dir.string());
  CHECK(plan.args[2] == "--port");
  CHECK(plan.args[3] == "47790");

  // The node ports reach the server through the environment, so the launcher never writes a config file.
  REQUIRE(plan.env.size() == 3);
  CHECK(plan.env[0].first == "X4MP__Net__NodeTcpEndpoint");
  CHECK(plan.env[0].second == "0.0.0.0:47780");
  CHECK(plan.env[1].first == "X4MP__Net__UdpPort");
  CHECK(plan.env[1].second == "47781");

  // A server without an admin password refuses the authority join, so the plan always carries one.
  CHECK(plan.admin_password.size() == 20);
  CHECK(plan.env[2].first == "X4MP__Net__AdminPassword");
  CHECK(plan.env[2].second == plan.admin_password);
  CHECK(std::filesystem::is_regular_file(plan.data_dir / "admin-password.txt"));

  // Generated once: the next start of the same server accepts the same password.
  const Plan again = plan_from(ServerConfig{}, extension, state);
  CHECK(again.admin_password == plan.admin_password);
}

TEST_CASE("server: a configured bundle and data folder win, and a port outside the range is clamped", "[server]") {
  TempTree tree;
  const auto extension = tree.root / "extension";

  ServerConfig cfg;
  cfg.exe = "bundle/x4mp-server.exe";
  cfg.data_dir = "D:\\X4MP\\data";
  cfg.port = 0;
  const Plan low = plan_from(cfg, extension, tree.root / "state");
  CHECK(low.exe == extension / "bundle" / "x4mp-server.exe");
  CHECK(low.data_dir == std::filesystem::path("D:\\X4MP\\data"));
  CHECK(low.port == 1);
  CHECK(low.http_port == 11);
  // The configured bundle is not there, and the screen has to say exactly that instead of failing at CreateProcess.
  CHECK(low.error.find("no server bundle at") != std::string::npos);

  cfg.exe.clear();
  cfg.port = 70000;
  const Plan high = plan_from(cfg, extension, tree.root / "state");
  CHECK(high.port == 65535);
  CHECK(high.http_port == 65535);

  // A password in x4mp.json is used as it stands: the player may want the same one on the other PC.
  cfg.admin_password = "chosen-by-the-player";
  const Plan chosen = plan_from(cfg, extension, tree.root / "state");
  CHECK(chosen.admin_password == "chosen-by-the-player");
  CHECK(chosen.env.back().second == "chosen-by-the-player");
}

TEST_CASE("server: the snapshot always carries every field the screen reads", "[server]") {
  Plan plan;
  plan.exe = "C:\\X4\\extensions\\x4mp\\server\\x4mp-server.exe";
  plan.data_dir = "C:\\state\\server-data";
  plan.log_file = "C:\\state\\logs\\server-console.log";
  Status status;
  status.state = "running";
  status.pid = 7;
  status.exit_code = 0;
  status.listening = true;
  status.external = true;

  const std::string text = status_json(status, plan);
  CHECK(text.find("\"v\":1") != std::string::npos);
  CHECK(text.find("\"state\":\"running\"") != std::string::npos);
  CHECK(text.find("\"pid\":7") != std::string::npos);
  CHECK(text.find("\"listening\":true") != std::string::npos);
  CHECK(text.find("\"external\":true") != std::string::npos);
  CHECK(text.find("\"port\":47780") != std::string::npos);
  CHECK(text.find("\"http_port\":47790") != std::string::npos);
  CHECK(text.find("\"auto_stop\":true") != std::string::npos);
  CHECK(text.find("x4mp-server.exe") != std::string::npos);
  CHECK(text.find("server-console.log") != std::string::npos);
  CHECK(text.find("\"error\":\"\"") != std::string::npos);
}

TEST_CASE("server: a start says starting until the port answers, and a stop brings it back", "[server]") {
  TempTree tree;
  const Plan plan = plan_from(ServerConfig{}, tree.root / "extension", tree.root / "state");
  REQUIRE(plan.error.empty());

  bool listening = false;
  auto host = std::make_unique<FakeProcessHost>();
  FakeProcessHost* fake = host.get();
  Launcher launcher(std::move(host), [&listening](std::uint16_t port) {
    CHECK(port == 47780);
    return listening;
  });
  Status status;

  CHECK(launcher.start(plan, 0.0, status));
  CHECK(status.state == "starting");
  CHECK(status.pid == 4242);
  CHECK(fake->spawns == 1);
  CHECK(fake->last.work_dir == plan.exe.parent_path());
  CHECK(fake->last.log_file == plan.log_file);
  CHECK(fake->last.args == plan.args);
  REQUIRE(fake->last.env.size() == 3);

  listening = true;
  status = launcher.poll(plan, 1.0);
  CHECK(status.state == "running");
  CHECK(status.listening);

  // A port that stops answering later must not flip a running server back to starting: the player already saw it up.
  listening = false;
  status = launcher.poll(plan, 2.0);
  CHECK(status.state == "running");
  CHECK(status.listening);

  CHECK_FALSE(launcher.start(plan, 3.0, status));
  CHECK(status.error == "the server is already running");
  CHECK(fake->spawns == 1);

  CHECK(launcher.stop(status));
  CHECK(status.state == "stopped");
  CHECK(fake->stops == 1);
  CHECK_FALSE(launcher.owns_process());
}

TEST_CASE("server: a port that already answers refuses the start without spawning", "[server]") {
  TempTree tree;
  const Plan plan = plan_from(ServerConfig{}, tree.root / "extension", tree.root / "state");
  REQUIRE(plan.error.empty());
  auto host = std::make_unique<FakeProcessHost>();
  FakeProcessHost* fake = host.get();
  Launcher launcher(std::move(host), [](std::uint16_t) { return true; });
  Status status;

  CHECK_FALSE(launcher.start(plan, 0.0, status));
  // The server the launcher started is the host's server: the game reports it as running, and says why it will not
  // start a second one.
  CHECK(status.state == "running");
  CHECK(status.listening);
  CHECK(status.external);
  CHECK(status.error == "a server is already listening on port 47780");
  CHECK(fake->spawns == 0);
  CHECK_FALSE(launcher.owns_process());
}

TEST_CASE("server: an external server is reported and never stopped by the game", "[server]") {
  TempTree tree;
  const Plan plan = plan_from(ServerConfig{}, tree.root / "extension", tree.root / "state");
  REQUIRE(plan.error.empty());
  auto host = std::make_unique<FakeProcessHost>();
  FakeProcessHost* fake = host.get();
  bool listening = true;
  Launcher launcher(std::move(host), [&listening](std::uint16_t) { return listening; });

  Status status = launcher.poll(plan, 0.0);
  CHECK(status.state == "running");
  CHECK(status.external);
  CHECK(status.listening);
  CHECK_FALSE(launcher.owns_process());

  // The port closes: this is not "exited", because this game never owned the process.
  listening = false;
  status = launcher.poll(plan, 1.0);
  CHECK(status.state == "stopped");
  CHECK_FALSE(status.external);

  CHECK_FALSE(launcher.stop(status));
  CHECK(status.error == "the server was not started by this game");
  CHECK(fake->stops == 0);
}

TEST_CASE("server: a process that dies is reported as exited with its code and the log path", "[server]") {
  TempTree tree;
  const Plan plan = plan_from(ServerConfig{}, tree.root / "extension", tree.root / "state");
  auto host = std::make_unique<FakeProcessHost>();
  FakeProcessHost* fake = host.get();
  bool listening = false;
  Launcher launcher(std::move(host), [&listening](std::uint16_t) { return listening; });
  Status status;

  REQUIRE(launcher.start(plan, 0.0, status));
  CHECK(status.state == "starting");
  listening = true;
  status = launcher.poll(plan, 0.5);
  CHECK(status.state == "running");

  fake->die(3);
  status = launcher.poll(plan, 5.0);
  CHECK(status.state == "exited");
  CHECK(status.exit_code == 3);
  CHECK(status.error.find("exited with code 3") != std::string::npos);
  CHECK(status.error.find("server-console.log") != std::string::npos);
  // Still ours: the screen keeps saying exited until the player starts again or stops.
  CHECK(launcher.owns_process());
}

TEST_CASE("server: a launcher that started nothing refuses to stop, and a bad plan never spawns", "[server]") {
  TempTree tree;
  auto host = std::make_unique<FakeProcessHost>();
  FakeProcessHost* fake = host.get();
  Launcher launcher(std::move(host), {});
  Status status;

  CHECK_FALSE(launcher.stop(status));
  CHECK(status.state == "stopped");
  CHECK(status.error == "the server was not started by this game");
  CHECK(fake->stops == 0);

  Plan broken;
  broken.error = "no server bundle at C:\\nowhere";
  CHECK_FALSE(launcher.start(broken, 0.0, status));
  CHECK(status.error == broken.error);
  CHECK(fake->spawns == 0);

  const Plan plan = plan_from(ServerConfig{}, tree.root / "extension", tree.root / "state");
  fake->spawn_ok = false;
  fake->spawn_error = "start failed (2)";
  CHECK_FALSE(launcher.start(plan, 0.0, status));
  CHECK(status.error == "start failed (2)");
  CHECK_FALSE(launcher.owns_process());
}

// The one case that talks to Win32 for real: a spawn that is refused here is refused in game too, with the same code, and
// the message is what the screen shows the player.
TEST_CASE("server: the Win32 host starts a real process, reports its exit code and can start it again", "[server]") {
  // powershell.exe, not cmd.exe: cmd re-parses its own command line and a quoted "/c" becomes a command name, so the exit
  // code of a cmd child says nothing about our quoting. powershell takes argv the way the CRT splits it, like the server.
  const std::filesystem::path cmd = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
  if (!std::filesystem::is_regular_file(cmd)) SKIP("powershell.exe is not where Windows keeps it");

  TempTree tree;
  auto host = make_process_host();
  SpawnRequest request;
  request.exe = cmd;
  request.work_dir = cmd.parent_path();
  request.log_file = tree.root / "console.log";
  request.args = {"-NoProfile", "-NonInteractive", "-Command", "exit 7"};
  // The production plan always overrides two variables, so the test must too: a broken environment block is a CreateProcessW
  // refusal that only shows up when the block is not inherited.
  request.env = {{"X4MP_TEST_SPAWN", "1"}, {"X4MP__Net__UdpPort", "47781"}};

  const SpawnResult first = host->spawn(request);
  INFO("spawn error: " << first.error);
  REQUIRE(first.ok);
  CHECK(first.pid != 0);
  CHECK(host->pid() == first.pid);

  for (int i = 0; i < 100 && host->running(); ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
  }
  CHECK_FALSE(host->running());
  CHECK(host->exit_code() == 7);

  // A process that exited leaves a closed handle behind; starting again must not be refused because of it.
  const SpawnResult second = host->spawn(request);
  INFO("second spawn error: " << second.error);
  CHECK(second.ok);
  CHECK(host->stop());
  CHECK_FALSE(host->running());
}
