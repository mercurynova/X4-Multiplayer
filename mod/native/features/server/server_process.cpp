#include "features/server/server_process.h"

#include <cwchar>
#include <system_error>
#include <utility>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>

#include <windows.h>

namespace x4mp::features::server {

namespace {

std::wstring widen(const std::string& utf8) {
  if (utf8.empty()) return {};
  const int size = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
  if (size <= 0) return {};
  std::wstring out(static_cast<std::size_t>(size), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), out.data(), size);
  return out;
}

// One argument as CreateProcessW wants it: quoted, and an inner quote escaped with a backslash.
std::wstring quoted(const std::wstring& text) {
  std::wstring out;
  out.reserve(text.size() + 2);
  out.push_back(L'"');
  for (const wchar_t ch : text) {
    if (ch == L'"') out.push_back(L'\\');
    out.push_back(ch);
  }
  out.push_back(L'"');
  return out;
}

// The parent's environment with a few entries replaced, as CreateProcessW takes it (a double null ends the block).
// Returns an empty vector when there is nothing to override, so the child simply inherits.
std::vector<wchar_t> environment_block(const std::vector<std::pair<std::string, std::string>>& overrides) {
  if (overrides.empty()) return {};
  std::vector<std::wstring> names;
  std::vector<std::wstring> values;
  for (const auto& [name, value] : overrides) {
    std::wstring wide_name = widen(name);
    std::wstring wide_value = widen(value);
    if (wide_name.empty()) continue;
    names.push_back(std::move(wide_name));
    values.push_back(std::move(wide_value));
  }
  std::vector<std::wstring> entries;
  if (const LPWCH block = GetEnvironmentStringsW()) {
    for (LPWCH it = block; *it != L'\0'; it += std::wcslen(it) + 1) {
      // Entries that begin with '=' are the per-drive current-directory variables. Windows keeps them in a process
      // environment but CreateProcessW refuses a block that carries them (ERROR_INVALID_PARAMETER, 87), so drop them.
      if (*it == L'=') continue;
      std::wstring entry(it);
      bool replaced = false;
      for (const auto& name : names) {
        if (entry.size() >= name.size() && std::wcsncmp(entry.c_str(), name.c_str(), name.size()) == 0) {
          replaced = true;
          break;
        }
      }
      if (!replaced) entries.push_back(std::move(entry));
    }
    FreeEnvironmentStringsW(block);
  }
  for (std::size_t i = 0; i < names.size(); ++i) entries.push_back(names[i] + L'=' + values[i]);

  std::vector<wchar_t> out;
  for (const auto& entry : entries) {
    out.insert(out.end(), entry.begin(), entry.end());
    out.push_back(L'\0');
  }
  out.push_back(L'\0');
  return out;
}

class Win32ProcessHost final : public ProcessHost {
 public:
  Win32ProcessHost() = default;
  Win32ProcessHost(const Win32ProcessHost&) = delete;
  Win32ProcessHost& operator=(const Win32ProcessHost&) = delete;
  ~Win32ProcessHost() override { release(); }

  SpawnResult spawn(const SpawnRequest& request) override {
    SpawnResult result;
    if (handle_ != nullptr) {
      if (running()) {
        result.error = "a server is already running";
        return result;
      }
      // The process we started is gone and only its exit code was kept: let the player start a new one.
      release();
    }
    std::error_code ec;
    if (!std::filesystem::is_regular_file(request.exe, ec)) {
      result.error = "no server executable at " + request.exe.string();
      return result;
    }

    std::wstring cmd = quoted(request.exe.wstring());
    for (const auto& arg : request.args) {
      cmd += L' ';
      cmd += quoted(widen(arg));
    }

    // Both streams go to one file: a windowless process that dies leaves its own explanation there and nowhere else.
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof sa;
    sa.bInheritHandle = TRUE;
    const HANDLE log = CreateFileW(request.log_file.wstring().c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   &sa, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (log == INVALID_HANDLE_VALUE) {
      result.error = "cannot open the server log " + request.log_file.string();
      return result;
    }
    const HANDLE null_in = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING,
                                       FILE_ATTRIBUTE_NORMAL, nullptr);

    STARTUPINFOW si{};
    si.cb = sizeof si;
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdInput = null_in == INVALID_HANDLE_VALUE ? nullptr : null_in;
    si.hStdOutput = log;
    si.hStdError = log;

    const std::wstring work = request.work_dir.wstring();
    std::vector<wchar_t> env_block = environment_block(request.env);
    PROCESS_INFORMATION pi{};
    // CREATE_UNICODE_ENVIRONMENT is not optional: without it CreateProcessW reads the block as ANSI and refuses it with
    // ERROR_INVALID_PARAMETER (87), which is exactly how the launcher failed in game the first time.
    const BOOL ok = CreateProcessW(request.exe.wstring().c_str(), cmd.data(), nullptr, nullptr, TRUE,
                                   CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT,
                                   env_block.empty() ? nullptr : env_block.data(), work.empty() ? nullptr : work.c_str(),
                                   &si, &pi);
    const DWORD last = ok ? 0 : GetLastError();
    CloseHandle(log);
    if (null_in != INVALID_HANDLE_VALUE) CloseHandle(null_in);
    if (!ok) {
      result.error = "start failed (" + std::to_string(last) + ")";
      return result;
    }
    CloseHandle(pi.hThread);
    handle_ = pi.hProcess;
    pid_ = pi.dwProcessId;

    // No job object and no kill-on-close handle here: the extension is unloaded on every save load and /reloadui while the
    // game keeps running, and a job owned by this DLL would kill the server the reloaded game is about to rejoin. The
    // server watches --parent-pid (the game process) itself, which also covers a crash.

    result.ok = true;
    result.pid = pid_;
    return result;
  }

  [[nodiscard]] bool running() override {
    if (handle_ == nullptr) return false;
    if (WaitForSingleObject(handle_, 0) == WAIT_TIMEOUT) return true;
    DWORD code = 0;
    if (GetExitCodeProcess(handle_, &code)) exit_code_ = static_cast<int>(code);
    return false;
  }

  [[nodiscard]] std::uint32_t pid() const override { return pid_; }

  [[nodiscard]] int exit_code() override {
    (void)running();
    return exit_code_;
  }

  bool stop() override {
    if (handle_ == nullptr) return true;
    if (running()) {
      // A windowless console cannot be signalled directly, but it can be joined: attach to it and aim a Ctrl+Break at the
      // child's own process group, which CREATE_NEW_PROCESS_GROUP gave it. Group 0 would mean every process on that
      // console, and this process is attached to it now, so the game would die with its server. The .NET host turns the
      // break into a graceful shutdown, which is what keeps a checkpoint write whole. A server that ignores it still
      // ends: the wait below gives up and kills it.
      if (AttachConsole(pid_) != 0) {
        (void)GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid_);
        (void)FreeConsole();
      }
      if (WaitForSingleObject(handle_, 5000) != WAIT_OBJECT_0) {
        // The last resort: a windowless console gets no Ctrl+Break from a GUI process, and the database is in WAL mode,
        // which survives this (the project has killed its dev server this way for months).
        (void)TerminateProcess(handle_, 1);
        (void)WaitForSingleObject(handle_, 5000);
      }
    }
    release();
    return true;
  }

 private:
  void release() {
    if (handle_ != nullptr) {
      CloseHandle(handle_);
      handle_ = nullptr;
    }
    pid_ = 0;
  }

  HANDLE handle_ = nullptr;
  std::uint32_t pid_ = 0;
  int exit_code_ = -1;
};

}  // namespace

std::uint32_t current_process_id() { return static_cast<std::uint32_t>(GetCurrentProcessId()); }

bool port_open(std::uint16_t port) {
  WSADATA data{};
  const bool started = WSAStartup(MAKEWORD(2, 2), &data) == 0;
  bool open = false;
  const SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (s != INVALID_SOCKET) {
    // Never block the frame thread on a SYN the machine drops (a firewall can do that): loopback either accepts at once
    // or refuses at once, so a bounded wait is enough and a 2 s TCP retransmission never reaches the game loop.
    u_long nonblocking = 1;
    (void)ioctlsocket(s, FIONBIO, &nonblocking);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    const int rc = connect(s, reinterpret_cast<const sockaddr*>(&addr), static_cast<int>(sizeof addr));
    if (rc == 0) {
      open = true;
    } else if (WSAGetLastError() == WSAEWOULDBLOCK) {
      fd_set writable;
      fd_set failed;
      FD_ZERO(&writable);
      FD_ZERO(&failed);
      FD_SET(s, &writable);
      FD_SET(s, &failed);
      timeval timeout{};
      timeout.tv_sec = 0;
      timeout.tv_usec = 50 * 1000;
      if (select(0, nullptr, &writable, &failed, &timeout) > 0 && FD_ISSET(s, &writable) && !FD_ISSET(s, &failed)) {
        int error = 0;
        int length = static_cast<int>(sizeof error);
        if (getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length) == 0 && error == 0) {
          open = true;
        }
      }
    }
    closesocket(s);
  }
  if (started) (void)WSACleanup();
  return open;
}

std::unique_ptr<ProcessHost> make_process_host() { return std::make_unique<Win32ProcessHost>(); }

}  // namespace x4mp::features::server
