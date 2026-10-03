#pragma once
// host/host_log: the host's logging facade (M2-04, mod-design 2.7): categories with their own level, secret redaction
// on every sink, an optional mirror of ERROR lines into the X4Native log.
//
//   HostLog log(opts);
//   X4MP_CLOG(log, Cat::Net, Level::Info, "connected to {}", host);
//
// Lines look like "2026-10-02 12:00:00.123Z [INFO] [net] connected to ...". Pure C++ (no SDK).
// Pipeline: producer -> core Logger ring (rate limited per call site) -> writer thread -> RedactingSink -> file /
// native mirror. The category level is checked first, so a disabled category costs two loads.

#include <array>
#include <atomic>
#include <filesystem>
#include <format>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "core/config/config.h"
#include "core/log/log.h"
#include "host/redact.h"

namespace x4mp::host {

enum class Cat : std::uint8_t { Host, Net, Sess, Auth, Client, Ghost, Md, Save, Ui, Perf, Count };
inline constexpr std::size_t kCatCount = static_cast<std::size_t>(Cat::Count);

[[nodiscard]] const char* cat_name(Cat cat) noexcept;
[[nodiscard]] std::optional<Cat> parse_cat(std::string_view name) noexcept;  // case-insensitive

using log::Level;

// Forwards lines to a callback (the host uses it to mirror ERROR lines into X4Native's log).
class CallbackSink final : public log::Sink {
 public:
  using Fn = std::function<void(log::Level, std::string_view)>;
  CallbackSink(Fn fn, log::Level min) : fn_(std::move(fn)), min_(min) {}
  void write(log::Level level, std::int64_t, std::string_view text) override {
    if (fn_) fn_(level, text);
  }
  [[nodiscard]] log::Level min_level() const noexcept override { return min_; }

 private:
  Fn fn_;
  log::Level min_;
};

class HostLog {
 public:
  struct Options {
    std::filesystem::path file;              // empty: no file sink
    log::Level level = log::Level::Info;     // default level of every category
    std::uint32_t rate_limit = 5;            // lines per second per call site
    std::vector<std::shared_ptr<log::Sink>> extra_sinks;  // already-final sinks (tests); NOT redacted by us
    CallbackSink::Fn native_mirror;          // ERROR lines (already redacted); may be empty
  };

  explicit HostLog(Options options);
  ~HostLog();  // flushes and joins the writer
  HostLog(const HostLog&) = delete;
  HostLog& operator=(const HostLog&) = delete;

  [[nodiscard]] bool file_open() const noexcept { return file_open_; }
  [[nodiscard]] log::Logger& logger() noexcept { return *logger_; }
  [[nodiscard]] Redactor& redactor() noexcept { return *redactor_; }

  // Applies level + category levels + the secret (password) from a config. Safe to call again on reload; unknown
  // category names are reported through the returned warnings (key names only).
  std::vector<std::string> apply_config(const config::Config& config);

  void set_category_level(Cat cat, log::Level level) noexcept;
  [[nodiscard]] log::Level category_level(Cat cat) const noexcept;
  [[nodiscard]] bool enabled(Cat cat, log::Level level) const noexcept { return log::should_log(category_level(cat), level); }

  template <class... Args>
  void log(log::CallSite& site, Cat cat, log::Level level, std::format_string<Args...> fmt, Args&&... args) noexcept {
    if (!enabled(cat, level)) return;
    char tmp[log::kMaxLineChars];
    std::size_t n = 0;
    try {
      const auto r = std::format_to_n(tmp, static_cast<std::ptrdiff_t>(sizeof(tmp)), fmt, std::forward<Args>(args)...);
      n = static_cast<std::size_t>(r.size) > sizeof(tmp) ? sizeof(tmp) : static_cast<std::size_t>(r.size);
    } catch (...) {
      return;
    }
    logger_->log(site, level, "[{}] {}", cat_name(cat), std::string_view(tmp, n));
  }

  // Unlimited line (headers, summaries).
  void raw(Cat cat, log::Level level, std::string_view text);
  void flush() { logger_->flush(); }

 private:
  void recompute_logger_floor() noexcept;

  std::shared_ptr<Redactor> redactor_;
  std::unique_ptr<log::Logger> logger_;
  std::array<std::atomic<log::Level>, kCatCount> levels_;
  bool file_open_ = false;
};

}  // namespace x4mp::host

// One static CallSite per expansion (rate-limit key = file:line), like X4MP_LOG_TO.
#define X4MP_CLOG(hostlog_, cat_, level_, ...)                                       \
  do {                                                                                \
    static ::x4mp::log::CallSite x4mp_clog_site_{__FILE__, __LINE__};                 \
    (hostlog_).log(x4mp_clog_site_, (cat_), (level_), __VA_ARGS__);                   \
  } while (false)
