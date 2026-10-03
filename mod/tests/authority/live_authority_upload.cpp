// M2-08 live test: the authority upload job against the REAL server, with the connection killed at random points.
//
//   For each run: a fresh published server (own data dir, own ports) <- TCP kill-proxy <- the headless authority driver
//   (core/authority::CheckpointUploader + Session). The proxy aborts the connection (RST) after a random number of client->server
//   bytes: during the handshake, mid-chunk, between chunks, after the last chunk. The authority redials by itself, the session
//   resumes, and the upload must finish on the new connection. Per run it asserts:
//     * the driver reached "checkpoint stored + self-spawn sent" (the server accepted the save AND the manifest);
//     * the server's store holds a file with exactly the saved bytes (size + SHA-256);
//     * cross_generation_reads == 0 and stale_writes_blocked == 0 (an old job never read the new connection's frames, and its frames
//       were never forwarded to the new socket) - the counters are in core/authority/upload_job.h.
//   And over all runs: at least half of them had a kill hit a running upload job, and at least one resumed from offset > 0.
//
// Skipped (exit 77, ctest "skipped") unless X4MP_LIVE_SERVER_EXE names a published x4mp-server. Environment variables are fine
// here: this is a test tool, never the mod runtime.
//   X4MP_LIVE_SERVER_EXE   path to x4mp-server.exe
//   X4MP_LIVE_PORT_BASE    default 47980 (uses base .. base+3; never the defaults 47780/47781/47790)
//   X4MP_LIVE_RUNS         default 50
//   X4MP_LIVE_SAVE_KB      default 3072 (the uploaded save, in KiB)
//   X4MP_LIVE_SEED         default 1 (random kill points are derived from it and the run number)

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <vector>

#include "authority_driver.h"
#include "core/crypto/crypto.h"
#include "core/session/session.h"
#include "gzip_fixture.h"

namespace {
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;
namespace fs = std::filesystem;

std::string env_or(const char* name, const std::string& fallback) {
  char* v = nullptr;
  std::size_t n = 0;
  if (_dupenv_s(&v, &n, name) != 0 || v == nullptr || *v == '\0') {
    std::free(v);
    return fallback;
  }
  std::string s = v;
  std::free(v);
  return s;
}

struct WsaInit {
  WsaInit() {
    WSADATA d{};
    ::WSAStartup(MAKEWORD(2, 2), &d);
  }
  ~WsaInit() { ::WSACleanup(); }
};

// ---- the kill proxy -------------------------------------------------------------------------------------------------------

void abort_socket(SOCKET s) {
  if (s == INVALID_SOCKET) return;
  linger l{1, 0};  // RST on close: the peer sees a reset, not an orderly EOF
  ::setsockopt(s, SOL_SOCKET, SO_LINGER, reinterpret_cast<const char*>(&l), sizeof(l));
  ::closesocket(s);
}

class KillProxy {
 public:
  KillProxy(std::uint16_t listen_port, std::uint16_t target_port) : listen_port_(listen_port), target_port_(target_port) {}
  ~KillProxy() { stop(); }

  // kill_after[i]: abort connection number i after that many client->server bytes (0 = never). Missing entries = never.
  void set_plan(std::vector<std::uint64_t> kill_after) {
    const std::lock_guard lock(mutex_);
    plan_ = std::move(kill_after);
  }

  bool start() {
    listen_ = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listen_ == INVALID_SOCKET) return false;
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(listen_port_);
    ::inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
    BOOL excl = TRUE;
    ::setsockopt(listen_, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&excl), sizeof(excl));
    if (::bind(listen_, reinterpret_cast<sockaddr*>(&a), sizeof(a)) != 0 || ::listen(listen_, 8) != 0) {
      ::closesocket(listen_);
      listen_ = INVALID_SOCKET;
      return false;
    }
    accept_thread_ = std::thread([this] { accept_loop(); });
    return true;
  }

  void stop() {
    if (stopping_.exchange(true)) return;
    if (listen_ != INVALID_SOCKET) {
      ::closesocket(listen_);
      listen_ = INVALID_SOCKET;
    }
    if (accept_thread_.joinable()) accept_thread_.join();
    std::vector<std::shared_ptr<Pair>> pairs;
    {
      const std::lock_guard lock(mutex_);
      pairs = pairs_;
    }
    for (auto& p : pairs) p->close();
    for (auto& p : pairs) {
      if (p->up.joinable()) p->up.join();
      if (p->down.joinable()) p->down.join();
    }
  }

  [[nodiscard]] int kills() const { return kills_.load(); }
  [[nodiscard]] int connections() const { return connections_.load(); }

 private:
  struct Pair {
    SOCKET client = INVALID_SOCKET;
    SOCKET server = INVALID_SOCKET;
    std::atomic<bool> closed{false};
    std::thread up, down;
    void close() {
      if (closed.exchange(true)) return;
      abort_socket(client);
      abort_socket(server);
    }
  };

  void accept_loop() {
    for (;;) {
      SOCKET c = ::accept(listen_, nullptr, nullptr);
      if (c == INVALID_SOCKET) return;
      SOCKET s = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      sockaddr_in a{};
      a.sin_family = AF_INET;
      a.sin_port = htons(target_port_);
      ::inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
      if (s == INVALID_SOCKET || ::connect(s, reinterpret_cast<sockaddr*>(&a), sizeof(a)) != 0) {
        abort_socket(s);
        abort_socket(c);
        continue;
      }
      BOOL nodelay = TRUE;
      ::setsockopt(c, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&nodelay), sizeof(nodelay));
      ::setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&nodelay), sizeof(nodelay));
      const int index = connections_.fetch_add(1);
      std::uint64_t limit = 0;
      {
        const std::lock_guard lock(mutex_);
        if (static_cast<std::size_t>(index) < plan_.size()) limit = plan_[static_cast<std::size_t>(index)];
      }
      auto pair = std::make_shared<Pair>();
      pair->client = c;
      pair->server = s;
      pair->up = std::thread([this, pair, limit] { relay(pair, true, limit); });
      pair->down = std::thread([this, pair] { relay(pair, false, 0); });
      const std::lock_guard lock(mutex_);
      pairs_.push_back(std::move(pair));
    }
  }

  // up = client -> server (where the kill budget is spent), down = server -> client.
  void relay(const std::shared_ptr<Pair>& p, bool up, std::uint64_t limit) {
    const SOCKET from = up ? p->client : p->server;
    const SOCKET to = up ? p->server : p->client;
    std::vector<char> buf(64 * 1024);
    std::uint64_t total = 0;
    for (;;) {
      const int n = ::recv(from, buf.data(), static_cast<int>(buf.size()), 0);
      if (n <= 0) break;
      int allowed = n;
      bool kill = false;
      if (up && limit != 0 && total + static_cast<std::uint64_t>(n) >= limit) {
        allowed = static_cast<int>(limit - total);  // the cut can land inside a frame
        kill = true;
      }
      for (int sent = 0; sent < allowed;) {
        const int w = ::send(to, buf.data() + sent, allowed - sent, 0);
        if (w <= 0) {
          p->close();
          return;
        }
        sent += w;
      }
      total += static_cast<std::uint64_t>(allowed);
      if (kill) {
        ++kills_;
        p->close();
        return;
      }
    }
    p->close();
  }

  std::uint16_t listen_port_, target_port_;
  SOCKET listen_ = INVALID_SOCKET;
  std::thread accept_thread_;
  std::atomic<bool> stopping_{false};
  std::atomic<int> kills_{0}, connections_{0};
  std::mutex mutex_;
  std::vector<std::uint64_t> plan_;
  std::vector<std::shared_ptr<Pair>> pairs_;
};

// ---- the real server process ----------------------------------------------------------------------------------------------

class ServerProcess {
 public:
  ~ServerProcess() { stop(); }

  bool start(const std::string& exe, const fs::path& data_dir, const fs::path& log_file, std::uint16_t tcp, std::uint16_t udp, std::uint16_t http) {
    job_ = ::CreateJobObjectW(nullptr, nullptr);
    if (job_ != nullptr) {
      JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
      info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;  // a crashed test never leaves a server behind
      ::SetInformationJobObject(job_, JobObjectExtendedLimitInformation, &info, sizeof(info));
    }
    // environment block = ours + the server's configuration (the published server reads X4MP__ variables; e2e.ps1 does the same)
    std::wstring env;
    if (LPWCH cur = ::GetEnvironmentStringsW()) {
      for (const wchar_t* p = cur; *p != L'\0'; p += wcslen(p) + 1) env.append(p, wcslen(p) + 1);
      ::FreeEnvironmentStringsW(cur);
    }
    const auto add = [&env](const std::wstring& kv) { env.append(kv); env.push_back(L'\0'); };
    add(L"X4MP__Net__MaxConnectionsPerIp=64");
    add(L"X4MP__Net__MaxPlayers=16");
    add(L"X4MP__Net__ModBuildStrict=false");
    add(L"X4MP__Net__NodeTcpEndpoint=127.0.0.1:" + std::to_wstring(tcp));
    add(L"X4MP__Net__UdpPort=" + std::to_wstring(udp));
    env.push_back(L'\0');

    SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE};
    HANDLE log = ::CreateFileW(log_file.wstring().c_str(), GENERIC_WRITE, FILE_SHARE_READ, &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = ::GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = log;
    si.hStdError = log;
    std::wstring cmd = L"\"" + fs::path(exe).wstring() + L"\" --data-dir \"" + data_dir.wstring() + L"\" --port " + std::to_wstring(http);
    PROCESS_INFORMATION pi{};
    const BOOL ok = ::CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, TRUE, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | CREATE_SUSPENDED,
                                     env.data(), nullptr, &si, &pi);
    if (log != INVALID_HANDLE_VALUE) ::CloseHandle(log);
    if (!ok) return false;
    if (job_ != nullptr) ::AssignProcessToJobObject(job_, pi.hProcess);
    ::ResumeThread(pi.hThread);
    ::CloseHandle(pi.hThread);
    process_ = pi.hProcess;
    return true;
  }

  // "still running" or "exited with code N": goes into the failure text so CI logs show why a server never came up.
  [[nodiscard]] std::string describe_exit() const {
    if (process_ == nullptr) return "not started";
    DWORD code = 0;
    if (!::GetExitCodeProcess(process_, &code)) return "exit code unknown";
    return code == STILL_ACTIVE ? "server still running" : "server exited with code " + std::to_string(static_cast<long>(static_cast<int>(code)));
  }

  [[nodiscard]] bool alive() const { return process_ != nullptr && ::WaitForSingleObject(process_, 0) == WAIT_TIMEOUT; }

  // Waits (blocking connects, short backoff) until the node port accepts a TCP connection, the process dies or the timeout passes.
  bool wait_ready(std::uint16_t tcp, std::chrono::milliseconds timeout) {
    const auto end = Clock::now() + timeout;
    while (Clock::now() < end && alive()) {
      SOCKET s = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      sockaddr_in a{};
      a.sin_family = AF_INET;
      a.sin_port = htons(tcp);
      ::inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
      const bool up = ::connect(s, reinterpret_cast<sockaddr*>(&a), sizeof(a)) == 0;
      ::closesocket(s);
      if (up) return true;
      ::WaitForSingleObject(process_, 50);  // returns early if the server exits
    }
    return false;
  }

  void stop() {
    if (process_ != nullptr) {
      ::TerminateProcess(process_, 0);
      ::WaitForSingleObject(process_, 5000);
      ::CloseHandle(process_);
      process_ = nullptr;
    }
    if (job_ != nullptr) {
      ::CloseHandle(job_);
      job_ = nullptr;
    }
  }

 private:
  HANDLE process_ = nullptr;
  HANDLE job_ = nullptr;
};

// ---- one run --------------------------------------------------------------------------------------------------------------

// The first and last `max_chars / 2` of a text file on one line (newlines become " | "), for failure messages.
std::string tail_of(const fs::path& file, std::size_t max_chars) {
  std::ifstream in(file, std::ios::binary);
  if (!in) return "(no log file)";
  std::string all((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
  if (all.size() > max_chars) all = all.substr(0, max_chars / 2) + " ... " + all.substr(all.size() - (max_chars / 2));  // head (the message) + tail (the stack)
  for (auto& c : all) {
    if (c == '\r') c = ' ';
    else if (c == '\n') c = '|';
  }
  return all.empty() ? "(empty log)" : all;
}

struct RunResult {
  bool ok = false;
  std::string why;
  int kills_fired = 0;
  int connections = 0;
  std::uint64_t jobs_cancelled = 0;
  std::uint64_t resumed_jobs = 0;
  std::uint64_t cross_reads = 0;
  std::uint64_t stale_writes = 0;
  std::uint64_t max_resume_offset = 0;
  std::chrono::milliseconds took{0};
};

// True if some file under `dir` has exactly the bytes of `save` (the server's store holds what we uploaded).
bool store_has_file(const fs::path& dir, const fs::path& save) {
  std::error_code ec;
  const auto size = fs::file_size(save, ec);
  const auto want = x4mp::crypto::sha256_file(save);
  if (ec || !want) return false;
  for (auto it = fs::recursive_directory_iterator(dir, fs::directory_options::skip_permission_denied, ec); !ec && it != fs::recursive_directory_iterator(); it.increment(ec)) {
    std::error_code e2;
    if (!it->is_regular_file(e2) || it->file_size(e2) != size) continue;
    const auto got = x4mp::crypto::sha256_file(it->path());
    if (got && *got == *want) return true;
  }
  return false;
}

RunResult run_once(int run, const std::string& server_exe, std::uint16_t base, std::uint64_t save_kb, std::uint64_t seed, const fs::path& root, bool keep) {
  RunResult res;
  const auto t0 = Clock::now();
  const fs::path dir = root / ("run" + std::to_string(run));
  std::error_code ec;
  fs::remove_all(dir, ec);
  fs::create_directories(dir / "data", ec);

  const std::uint16_t tcp = base, udp = static_cast<std::uint16_t>(base + 1), http = static_cast<std::uint16_t>(base + 2), proxy_port = static_cast<std::uint16_t>(base + 3);
  std::uint64_t save_bytes = 0;
  const auto save_path = dir / "save.xml.gz";
  if (!x4mp::testing::write_stored_gzip(save_path.string(), save_kb * 1024, seed * 1000 + static_cast<std::uint64_t>(run), &save_bytes)) {
    res.why = "cannot write the fixture save";
    return res;
  }

  ServerProcess server;
  if (!server.start(server_exe, dir / "data", dir / "server.log", tcp, udp, http)) {
    res.why = "cannot start the server";
    return res;
  }
  if (!server.wait_ready(tcp, 60s)) {
    res.why = "the server did not open its node port (" + server.describe_exit() + "; see " + (dir / "server.log").string() + ") log tail: " + tail_of(dir / "server.log", 3000);
    return res;
  }

  // Random kill plan: connection 1 dies after a random number of client->server bytes anywhere in the handshake + upload; 45 % of
  // the runs also kill the resumed connection, 15 % a third. Bytes are counted per connection.
  std::mt19937_64 rng(seed * 7919 + static_cast<std::uint64_t>(run) * 104729);
  const std::uint64_t span = save_bytes + 4096;  // handshake + control frames + the save; the manifest is tiny
  std::vector<std::uint64_t> plan;
  plan.push_back(1 + rng() % span);
  if (rng() % 100 < 45) plan.push_back(1 + rng() % (span / 2));
  if (rng() % 100 < 15) plan.push_back(1 + rng() % (span / 3));

  KillProxy proxy(proxy_port, tcp);
  if (env_or("X4MP_LIVE_NOKILL", "") == "1") plan.clear();  // control: the same flow without cutting the connection
  proxy.set_plan(plan);
  if (!proxy.start()) {
    res.why = "cannot start the kill proxy on port " + std::to_string(proxy_port);
    return res;
  }

  x4mp::session::SessionOptions opt;
  opt.endpoint = x4mp::net::Endpoint{"127.0.0.1", proxy_port};
  opt.player_name = "LiveAuthority";
  opt.requested_roles = 3;  // Authority | Client
  opt.auto_download = false;
  opt.net.backoff_first_ms = 100;
  opt.net.backoff_cap_ms = 400;
  opt.net.heartbeat_timeout_ms = 5000;
  if (!x4mp::crypto::random_bytes(opt.player_key)) {
    res.why = "no random source";
    return res;
  }
  x4mp::session::MemoryStash stash;
  opt.stash = &stash;

  std::vector<std::string> log;
  {
    x4mp::session::Session s(opt);
    if (!s.start()) {
      res.why = "session did not start";
      return res;
    }
    x4mp::headless::AuthorityDriverOptions dopt;
    dopt.save_file = save_path;
    dopt.work_dir = dir / "work";
    dopt.step_timeout = 30s;
    dopt.log = [&log](std::string_view line) { log.emplace_back(line); };
    x4mp::headless::AuthorityDriver drv(s, std::move(dopt));
    const auto deadline = Clock::now() + 90s;
    while (Clock::now() < deadline && !drv.done() && !drv.failed()) {
      drv.step();
      std::this_thread::sleep_for(2ms);
    }
    const auto st = drv.upload_stats();
    res.kills_fired = proxy.kills();
    res.connections = proxy.connections();
    res.jobs_cancelled = st.jobs_cancelled;
    res.resumed_jobs = st.resumed_jobs;
    res.cross_reads = st.cross_generation_reads;
    res.stale_writes = st.stale_writes_blocked;
    if (const auto& r = drv.last_upload()) {
      for (const auto& f : r->files) res.max_resume_offset = std::max(res.max_resume_offset, f.resume_offset);
    }
    if (drv.failed()) res.why = "driver failed: " + drv.failure();
    else if (!drv.done()) res.why = "timeout: the checkpoint was not stored and the spawn sent within 90 s";
    else if (res.cross_reads != 0) res.why = "cross-connection reads: " + std::to_string(res.cross_reads);
    else if (res.stale_writes != 0) res.why = "stale writes blocked: " + std::to_string(res.stale_writes);
    else if (!server.alive()) res.why = "the server died";
    s.stop();
  }
  proxy.stop();
  if (res.why.empty() && !store_has_file(dir / "data", save_path)) res.why = "the server's store has no file with the uploaded bytes";
  server.stop();
  res.took = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - t0);
  res.ok = res.why.empty();
  if (!res.ok || keep) {
    std::ofstream out(dir / "driver.log");
    for (const auto& l : log) out << l << "\n";
    std::printf("  run %d kept in %s\n", run, dir.string().c_str());
  } else {
    fs::remove_all(dir, ec);
  }
  return res;
}

}  // namespace

int main() {
  const std::string server_exe = env_or("X4MP_LIVE_SERVER_EXE", "");
  if (server_exe.empty() || !fs::exists(server_exe)) {
    std::puts("X4MP_LIVE_SERVER_EXE not set (or missing): skipping the live authority upload test");
    return 77;
  }
  const WsaInit wsa;
  const auto base = static_cast<std::uint16_t>(std::atoi(env_or("X4MP_LIVE_PORT_BASE", "47980").c_str()));
  const int runs = std::atoi(env_or("X4MP_LIVE_RUNS", "50").c_str());
  const auto save_kb = static_cast<std::uint64_t>(std::atoll(env_or("X4MP_LIVE_SAVE_KB", "3072").c_str()));
  const auto seed = static_cast<std::uint64_t>(std::atoll(env_or("X4MP_LIVE_SEED", "1").c_str()));
  const bool keep = env_or("X4MP_LIVE_KEEP", "") == "1";
  if (base == 47780 || base == 47781 || base == 47790 || (base >= 47776 && base <= 47792)) {
    std::fprintf(stderr, "refusing to use the default server ports\n");
    return 2;
  }
  const fs::path root = fs::temp_directory_path() / ("x4mp-live-authority-" + std::to_string(::GetCurrentProcessId()));
  fs::create_directories(root);
  std::printf("live authority upload test: %d runs, %llu KiB save, ports %u..%u, seed %llu, server %s\n", runs,
              static_cast<unsigned long long>(save_kb), static_cast<unsigned>(base), static_cast<unsigned>(base + 3),
              static_cast<unsigned long long>(seed), server_exe.c_str());

  int failures = 0, runs_with_kill_on_job = 0, runs_with_resume_offset = 0;
  std::uint64_t kills = 0, cancelled = 0, resumed = 0, cross = 0, stale = 0;
  const auto t0 = Clock::now();
  for (int i = 1; i <= runs; ++i) {
    const RunResult r = run_once(i, server_exe, base, save_kb, seed, root, keep);
    std::printf("run %2d: %s kills_fired=%d connections=%d jobs_cancelled=%llu resumed_jobs=%llu resume_offset=%llu cross_reads=%llu stale_writes=%llu %lld ms%s%s\n",
                i, r.ok ? "ok  " : "FAIL", r.kills_fired, r.connections, static_cast<unsigned long long>(r.jobs_cancelled),
                static_cast<unsigned long long>(r.resumed_jobs), static_cast<unsigned long long>(r.max_resume_offset),
                static_cast<unsigned long long>(r.cross_reads), static_cast<unsigned long long>(r.stale_writes),
                static_cast<long long>(r.took.count()), r.ok ? "" : "  <- ", r.why.c_str());
    std::fflush(stdout);
    if (!r.ok) ++failures;
    kills += static_cast<std::uint64_t>(r.kills_fired);
    cancelled += r.jobs_cancelled;
    resumed += r.resumed_jobs;
    cross += r.cross_reads;
    stale += r.stale_writes;
    if (r.jobs_cancelled > 0) ++runs_with_kill_on_job;
    if (r.max_resume_offset > 0) ++runs_with_resume_offset;
  }
  std::error_code ec;
  if (failures == 0) fs::remove_all(root, ec);
  std::printf("SUMMARY: runs=%d failures=%d kills=%llu runs_with_kill_on_running_job=%d runs_resumed_from_offset=%d resumed_jobs=%llu cancelled_jobs=%llu cross_connection_reads=%llu stale_writes=%llu total=%lld s\n",
              runs, failures, static_cast<unsigned long long>(kills), runs_with_kill_on_job, runs_with_resume_offset,
              static_cast<unsigned long long>(resumed), static_cast<unsigned long long>(cancelled), static_cast<unsigned long long>(cross),
              static_cast<unsigned long long>(stale),
              static_cast<long long>(std::chrono::duration_cast<std::chrono::seconds>(Clock::now() - t0).count()));
  bool pass = failures == 0 && cross == 0 && stale == 0;
  if (runs >= 10 && runs_with_kill_on_job * 2 < runs) {
    std::puts("FAILED: fewer than half of the runs had a kill hit a running upload job: the test is not exercising the resume path");
    pass = false;
  }
  if (runs >= 10 && runs_with_resume_offset == 0) {
    std::puts("FAILED: no run ever resumed from a nonzero offset");
    pass = false;
  }
  std::puts(pass ? "PASS" : "FAIL");
  return pass ? 0 : 1;
}
