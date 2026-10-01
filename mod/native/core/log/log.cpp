#include "core/log/log.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdio>
#include <ctime>
#include <system_error>

namespace x4mp::log {

const char* level_name(Level level) noexcept {
  switch (level) {
    case Level::Debug: return "DEBUG";
    case Level::Info: return "INFO";
    case Level::Warn: return "WARN";
    case Level::Error: return "ERROR";
  }
  return "?";
}

std::optional<Level> parse_level(std::string_view text) noexcept {
  constexpr std::array<Level, 4> kAll{Level::Debug, Level::Info, Level::Warn, Level::Error};
  for (Level level : kAll) {
    std::string_view name = level_name(level);
    if (name.size() != text.size()) continue;
    bool equal = true;
    for (std::size_t i = 0; i < name.size(); ++i) {
      if (std::toupper(static_cast<unsigned char>(text[i])) != name[i]) {
        equal = false;
        break;
      }
    }
    if (equal) return level;
  }
  return std::nullopt;
}

bool should_log(Level minimum, Level message) noexcept {
  return static_cast<int>(message) >= static_cast<int>(minimum);
}

std::string format_line(Level level, std::string_view message) {
  std::string line;
  line.reserve(message.size() + 10);
  line += '[';
  line += level_name(level);
  line += "] ";
  line += message;
  return line;
}

std::string format_timestamped(Level level, std::int64_t unix_ms, std::string_view text) {
  const std::time_t secs = static_cast<std::time_t>(unix_ms / 1000);
  int ms = static_cast<int>(unix_ms % 1000);
  if (ms < 0) ms += 1000;
  std::tm tm{};
#ifdef _WIN32
  gmtime_s(&tm, &secs);
#else
  gmtime_r(&secs, &tm);
#endif
  char head[40];
  std::snprintf(head, sizeof head, "%04d-%02d-%02d %02d:%02d:%02d.%03dZ ", tm.tm_year + 1900, tm.tm_mon + 1,
                tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec, ms);
  std::string line = head;
  line += '[';
  line += level_name(level);
  line += "] ";
  line += text;
  return line;
}

// ---------------------------------------------------------------------------------------------
// FileSink
// ---------------------------------------------------------------------------------------------
FileSink::FileSink(std::filesystem::path path, Options options) : path_(std::move(path)), options_(options) {
  std::error_code ec;
  if (path_.has_parent_path()) std::filesystem::create_directories(path_.parent_path(), ec);
  open();
}

void FileSink::open() {
  std::error_code ec;
  const auto size = std::filesystem::file_size(path_, ec);
  bytes_ = ec ? 0 : size;
  out_.open(path_, std::ios::binary | std::ios::app);
}

void FileSink::rotate() {
  out_.close();
  std::error_code ec;
  const int keep = std::max(options_.keep, 1);
  auto numbered = [&](int i) {
    auto p = path_;
    p += "." + std::to_string(i);
    return p;
  };
  std::filesystem::remove(numbered(keep - 1), ec);
  for (int i = keep - 2; i >= 1; --i) std::filesystem::rename(numbered(i), numbered(i + 1), ec);
  if (keep > 1) std::filesystem::rename(path_, numbered(1), ec);
  else std::filesystem::remove(path_, ec);
  open();
}

void FileSink::write(Level level, std::int64_t unix_ms, std::string_view text) {
  if (!out_.is_open()) return;
  if (bytes_ >= options_.max_bytes) {
    rotate();
    if (!out_.is_open()) return;
  }
  std::string line = format_timestamped(level, unix_ms, text);
  line += '\n';
  out_.write(line.data(), static_cast<std::streamsize>(line.size()));
  bytes_ += line.size();
}

void FileSink::flush() {
  if (out_.is_open()) out_.flush();
}

// ---------------------------------------------------------------------------------------------
// Logger
// ---------------------------------------------------------------------------------------------
namespace {
std::atomic<Logger*> g_global{nullptr};

std::int64_t steady_ns() noexcept {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
      .count();
}

std::int64_t unix_ms_now() noexcept {
  return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch())
      .count();
}

std::string_view base_name(const char* path) noexcept {
  std::string_view p(path);
  const auto pos = p.find_last_of("/\\");
  return pos == std::string_view::npos ? p : p.substr(pos + 1);
}
}  // namespace

void Logger::set_global(Logger* logger) noexcept { g_global.store(logger, std::memory_order_release); }
Logger* Logger::global() noexcept { return g_global.load(std::memory_order_acquire); }

Logger::Logger(Options options, std::vector<std::shared_ptr<Sink>> sinks)
    : options_(options), sinks_(std::move(sinks)), ring_(options.queue_capacity), min_level_(options.min_level) {
  writer_ = std::thread([this] { writer_loop(); });
}

Logger::~Logger() {
  if (global() == this) set_global(nullptr);
  stop_.store(true, std::memory_order_release);
  wake_.notify_all();
  if (writer_.joinable()) writer_.join();
}

std::int64_t Logger::now_ns() const noexcept { return options_.clock ? options_.clock() : steady_ns(); }

bool Logger::admit(CallSite& site) noexcept {
  if (!site.registered.load(std::memory_order_relaxed) && !site.registered.exchange(true)) {
    try {
      std::lock_guard lock(sites_mutex_);
      sites_.push_back(&site);  // allocates, once per site
    } catch (...) {
    }
  }
  const std::int64_t now = now_ns();
  const std::int64_t window_ns = static_cast<std::int64_t>(options_.window_ms) * 1'000'000;
  std::int64_t ws = site.window_start_ns.load(std::memory_order_relaxed);
  if (ws == INT64_MIN || now - ws >= window_ns) {
    // Exactly one thread wins the rollover: it resets the counter and reports what the last window swallowed.
    if (site.window_start_ns.compare_exchange_strong(ws, now, std::memory_order_acq_rel)) {
      const std::uint32_t sup = site.suppressed.exchange(0, std::memory_order_acq_rel);
      site.count.store(0, std::memory_order_release);
      if (sup > 0) emit_summary(site, sup);
    }
  }
  if (site.count.fetch_add(1, std::memory_order_acq_rel) < options_.max_per_window) return true;
  site.suppressed.fetch_add(1, std::memory_order_relaxed);
  return false;
}

void Logger::emit_summary(const CallSite& site, std::uint32_t suppressed) noexcept {
  char buf[kMaxLineChars];
  try {
    const auto name = base_name(site.file);
    const auto r = std::format_to_n(buf, static_cast<std::ptrdiff_t>(kMaxLineChars),
                                    "log rate limit: {} lines suppressed at {}:{}", suppressed, name, site.line);
    const std::size_t n = std::min<std::size_t>(static_cast<std::size_t>(r.size), kMaxLineChars);
    enqueue(Level::Warn, std::string_view(buf, n));
  } catch (...) {
  }
}

void Logger::log_raw(Level level, std::string_view text) noexcept {
  if (!should_log(min_level(), level)) return;
  enqueue(level, text);
}

void Logger::enqueue(Level level, std::string_view text) noexcept {
  Record rec;
  rec.unix_ms = unix_ms_now();
  rec.level = level;
  const std::size_t n = std::min(text.size(), kMaxLineChars);
  std::copy_n(text.data(), n, rec.text);
  rec.len = static_cast<std::uint16_t>(n);
  if (ring_.try_push(rec)) enqueued_.fetch_add(1, std::memory_order_release);
}

void Logger::sweep_sites() {
  const std::int64_t now = now_ns();
  const std::int64_t window_ns = static_cast<std::int64_t>(options_.window_ms) * 1'000'000;
  std::lock_guard lock(sites_mutex_);
  for (CallSite* site : sites_) {
    const std::int64_t ws = site->window_start_ns.load(std::memory_order_acquire);
    if (ws == INT64_MIN || now - ws < window_ns) continue;
    if (site->suppressed.load(std::memory_order_relaxed) == 0) continue;
    const std::uint32_t sup = site->suppressed.exchange(0, std::memory_order_acq_rel);
    if (sup > 0) emit_summary(*site, sup);
  }
}

void Logger::write_direct(Level level, std::string_view text) {
  const std::int64_t ms = unix_ms_now();
  for (auto& sink : sinks_) {
    if (should_log(sink->min_level(), level)) sink->write(level, ms, text);
  }
}

void Logger::writer_loop() {
  std::uint64_t sweep_due_ms = 0;
  for (;;) {
    std::uint64_t written = 0;
    const std::size_t n = ring_.drain(
        [&](Record&& rec) {
          const std::string_view text(rec.text, rec.len);
          for (auto& sink : sinks_) {
            if (should_log(sink->min_level(), rec.level)) sink->write(rec.level, rec.unix_ms, text);
          }
          ++written;
        },
        256);
    const std::uint64_t dropped = ring_.dropped();
    if (dropped > reported_dropped_) {
      const std::string msg = std::format("log queue overflow: {} lines dropped", dropped - reported_dropped_);
      reported_dropped_ = dropped;
      write_direct(Level::Warn, msg);
    }
    if (n > 0) {
      for (auto& sink : sinks_) sink->flush();
      done_.fetch_add(written, std::memory_order_release);
      continue;  // keep draining without waiting
    }
    if (stop_.load(std::memory_order_acquire)) break;  // ring observed empty after stop was requested
    const auto now_ms = static_cast<std::uint64_t>(unix_ms_now());
    if (now_ms >= sweep_due_ms) {
      sweep_sites();
      sweep_due_ms = now_ms + std::max<std::uint32_t>(options_.window_ms / 2, 50);
      continue;
    }
    std::unique_lock lock(wake_mutex_);
    wake_.wait_for(lock, std::chrono::milliseconds(10));
  }
  for (auto& sink : sinks_) sink->flush();
}

void Logger::flush() {
  sweep_sites();
  const std::uint64_t target = enqueued_.load(std::memory_order_acquire);
  wake_.notify_all();
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
  while (done_.load(std::memory_order_acquire) < target && std::chrono::steady_clock::now() < deadline) {
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
}

}  // namespace x4mp::log
