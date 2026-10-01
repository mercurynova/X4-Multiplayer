#pragma once
// core/log: asynchronous, rate-limited logger (docs/mod-design.md 2.7).
// Pure C++: the X4Native logger is only reached from main.cpp / game/ through a Sink implementation.
//
// Model
//   * Any thread calls Logger::log(site, level, fmt, args...). The line is formatted into a fixed stack buffer and
//     pushed into a preallocated lock-free MPSC ring (core/queue). One background writer thread drains the ring
//     and hands lines to the Sinks. Producers never touch a file and never block.
//   * Per call site rate limiting: each X4MP_LOG* macro owns a static CallSite {file, line}. At most
//     Options::max_per_window lines per Options::window_ms pass; the rest are counted and a summary line
//     "log rate limit: K lines suppressed at file:line" is emitted when the window rolls over, or by the writer
//     sweep / flush() if the site went quiet.
//   * Hot path allocation: none for the log call itself (stack buffer + ring slot) as long as the format
//     arguments do not allocate in std::formatter (strings, integers, floats, pointers do not). The only
//     allocations are: first use of a CallSite (registers it, one vector push), Logger construction, and
//     everything on the writer thread (timestamp text, file I/O). Lines longer than kMaxLineChars are truncated.
//   * If the ring is full the line is dropped and counted; the writer later logs "N lines dropped".
//   * Sinks are called only from the writer thread, so a Sink needs no locking of its own.
//
// Lifetime: CallSites registered with a Logger must outlive it (the macros use function-local statics).
// Nothing here reads environment variables.

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <format>
#include <fstream>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "core/queue/queue.h"

namespace x4mp::log {

enum class Level { Debug = 0, Info = 1, Warn = 2, Error = 3 };

[[nodiscard]] const char* level_name(Level level) noexcept;
[[nodiscard]] std::optional<Level> parse_level(std::string_view text) noexcept;  // case-insensitive
[[nodiscard]] bool should_log(Level minimum, Level message) noexcept;
[[nodiscard]] std::string format_line(Level level, std::string_view message);    // "[INFO] message"

inline constexpr std::size_t kMaxLineChars = 240;

// ---------------------------------------------------------------------------------------------
// Sink: destination of formatted lines. Called from the logger's writer thread only.
// main.cpp can implement one that forwards to x4n::log (typically with min_level() == Error).
// ---------------------------------------------------------------------------------------------
class Sink {
 public:
  virtual ~Sink() = default;
  // `unix_ms` is wall-clock milliseconds since the epoch; `text` is the message without level/time prefix.
  virtual void write(Level level, std::int64_t unix_ms, std::string_view text) = 0;
  virtual void flush() {}
  [[nodiscard]] virtual Level min_level() const noexcept { return Level::Debug; }
};

// "2026-10-01 12:00:00.123Z [INFO] text" (UTC). Shared by the file sink; handy for other sinks.
[[nodiscard]] std::string format_timestamped(Level level, std::int64_t unix_ms, std::string_view text);

// Appends to a file, with size-based rotation (default 20 MB, 5 files kept: x4mp.log, x4mp.log.1 ...).
class FileSink final : public Sink {
 public:
  struct Options {
    std::uintmax_t max_bytes = 20u * 1024u * 1024u;
    int keep = 5;
  };
  explicit FileSink(std::filesystem::path path) : FileSink(std::move(path), Options{}) {}
  FileSink(std::filesystem::path path, Options options);
  [[nodiscard]] bool is_open() const noexcept { return out_.is_open(); }
  void write(Level level, std::int64_t unix_ms, std::string_view text) override;
  void flush() override;

 private:
  void open();
  void rotate();
  std::filesystem::path path_;
  Options options_;
  std::ofstream out_;
  std::uintmax_t bytes_ = 0;
};

// One per log call site (use the macros). Zero-initialised atomics; no allocation on the hot path.
struct CallSite {
  constexpr CallSite(const char* file_, int line_) noexcept : file(file_), line(line_) {}
  CallSite(const CallSite&) = delete;
  CallSite& operator=(const CallSite&) = delete;

  const char* file;
  int line;
  std::atomic<std::int64_t> window_start_ns{INT64_MIN};
  std::atomic<std::uint32_t> count{0};
  std::atomic<std::uint32_t> suppressed{0};
  std::atomic<bool> registered{false};
};

class Logger {
 public:
  using Clock = std::int64_t (*)() noexcept;  // monotonic nanoseconds; injectable for tests

  struct Options {
    Level min_level = Level::Info;
    std::uint32_t max_per_window = 5;   // lines per call site per window
    std::uint32_t window_ms = 1000;
    std::size_t queue_capacity = 2048;  // ring slots (rounded up to a power of two)
    Clock clock = nullptr;              // nullptr => std::chrono::steady_clock
  };

  Logger(Options options, std::vector<std::shared_ptr<Sink>> sinks);
  ~Logger();  // drains the queue, flushes sinks, joins the writer thread
  Logger(const Logger&) = delete;
  Logger& operator=(const Logger&) = delete;

  template <class... Args>
  void log(CallSite& site, Level level, std::format_string<Args...> fmt, Args&&... args) noexcept {
    if (!should_log(min_level(), level)) return;
    if (!admit(site)) return;
    char buf[kMaxLineChars];
    std::size_t n = 0;
    try {
      const auto r = std::format_to_n(buf, static_cast<std::ptrdiff_t>(kMaxLineChars), fmt, std::forward<Args>(args)...);
      n = static_cast<std::size_t>(r.size) > kMaxLineChars ? kMaxLineChars : static_cast<std::size_t>(r.size);
    } catch (...) {
      return;  // formatting must never take the caller down
    }
    enqueue(level, std::string_view(buf, n));
  }

  // Unlimited, unformatted line (config diagnostics, summaries). Still async, no per-site limit.
  void log_raw(Level level, std::string_view text) noexcept;

  // Emits pending suppression summaries for quiet sites, then blocks until everything enqueued so far has been
  // written and the sinks flushed (or a 5 s timeout). Safe from any thread except a Sink.
  void flush();

  void set_min_level(Level level) noexcept { min_level_.store(level, std::memory_order_relaxed); }
  [[nodiscard]] Level min_level() const noexcept { return min_level_.load(std::memory_order_relaxed); }
  [[nodiscard]] std::uint64_t dropped_lines() const noexcept { return ring_.dropped(); }

  // Process-wide default used by the X4MP_LOG* macros. Not owned. nullptr => those macros do nothing.
  static void set_global(Logger* logger) noexcept;
  [[nodiscard]] static Logger* global() noexcept;

 private:
  struct Record {
    std::int64_t unix_ms = 0;
    Level level = Level::Info;
    std::uint16_t len = 0;
    char text[kMaxLineChars] = {};
  };

  bool admit(CallSite& site) noexcept;
  void enqueue(Level level, std::string_view text) noexcept;
  void emit_summary(const CallSite& site, std::uint32_t suppressed) noexcept;
  void sweep_sites();
  void writer_loop();
  void write_direct(Level level, std::string_view text);
  [[nodiscard]] std::int64_t now_ns() const noexcept;

  Options options_;
  std::vector<std::shared_ptr<Sink>> sinks_;
  queue::MpscRing<Record> ring_;
  std::atomic<Level> min_level_;

  std::mutex sites_mutex_;
  std::vector<CallSite*> sites_;

  std::atomic<std::uint64_t> enqueued_{0};
  std::atomic<std::uint64_t> done_{0};
  std::uint64_t reported_dropped_ = 0;  // writer thread only

  std::mutex wake_mutex_;
  std::condition_variable wake_;
  std::atomic<bool> stop_{false};
  std::thread writer_;
};

}  // namespace x4mp::log

// Logging macros. Each expansion owns a static CallSite (the rate-limit key is file:line).
//   X4MP_LOG_TO(logger_ref, Level::Info, "x {}", v);   X4MP_LOGI("x {}", v);  // global logger
#define X4MP_LOG_TO(logger_, level_, ...)                                          \
  do {                                                                              \
    static ::x4mp::log::CallSite x4mp_log_site_{__FILE__, __LINE__};                \
    (logger_).log(x4mp_log_site_, (level_), __VA_ARGS__);                           \
  } while (false)

#define X4MP_LOG_GLOBAL(level_, ...)                                                \
  do {                                                                              \
    if (::x4mp::log::Logger* x4mp_lg_ = ::x4mp::log::Logger::global()) {            \
      X4MP_LOG_TO(*x4mp_lg_, (level_), __VA_ARGS__);                                \
    }                                                                               \
  } while (false)

#define X4MP_LOGD(...) X4MP_LOG_GLOBAL(::x4mp::log::Level::Debug, __VA_ARGS__)
#define X4MP_LOGI(...) X4MP_LOG_GLOBAL(::x4mp::log::Level::Info, __VA_ARGS__)
#define X4MP_LOGW(...) X4MP_LOG_GLOBAL(::x4mp::log::Level::Warn, __VA_ARGS__)
#define X4MP_LOGE(...) X4MP_LOG_GLOBAL(::x4mp::log::Level::Error, __VA_ARGS__)
