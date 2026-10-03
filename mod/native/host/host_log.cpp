#include "host/host_log.h"

#include <algorithm>
#include <cctype>

namespace x4mp::host {

namespace {
constexpr std::array<const char*, kCatCount> kNames = {"host", "net", "sess", "auth", "client",
                                                       "ghost", "md", "save", "ui", "perf"};
}

const char* cat_name(Cat cat) noexcept {
  const auto i = static_cast<std::size_t>(cat);
  return i < kCatCount ? kNames[i] : "?";
}

std::optional<Cat> parse_cat(std::string_view name) noexcept {
  for (std::size_t i = 0; i < kCatCount; ++i) {
    const std::string_view n = kNames[i];
    if (n.size() != name.size()) continue;
    bool eq = true;
    for (std::size_t k = 0; k < n.size(); ++k) {
      eq = eq && std::tolower(static_cast<unsigned char>(name[k])) == n[k];
    }
    if (eq) return static_cast<Cat>(i);
  }
  return std::nullopt;
}

HostLog::HostLog(Options options) : redactor_(std::make_shared<Redactor>()) {
  std::vector<std::shared_ptr<log::Sink>> sinks;
  if (!options.file.empty()) {
    std::error_code ec;
    std::filesystem::create_directories(options.file.parent_path(), ec);
    auto file = std::make_shared<log::FileSink>(options.file);
    file_open_ = file->is_open();
    if (file_open_) sinks.push_back(std::make_shared<RedactingSink>(file, redactor_));
  }
  if (options.native_mirror) {
    sinks.push_back(std::make_shared<RedactingSink>(
        std::make_shared<CallbackSink>(std::move(options.native_mirror), log::Level::Error), redactor_));
  }
  for (auto& s : options.extra_sinks) sinks.push_back(std::move(s));
  for (auto& l : levels_) l.store(options.level);
  log::Logger::Options lo;
  lo.min_level = options.level;
  lo.max_per_window = options.rate_limit;
  logger_ = std::make_unique<log::Logger>(lo, std::move(sinks));
}

HostLog::~HostLog() {
  if (logger_) logger_->flush();
}

void HostLog::set_category_level(Cat cat, log::Level level) noexcept {
  levels_[static_cast<std::size_t>(cat)].store(level, std::memory_order_relaxed);
  recompute_logger_floor();
}

log::Level HostLog::category_level(Cat cat) const noexcept {
  return levels_[static_cast<std::size_t>(cat)].load(std::memory_order_relaxed);
}

void HostLog::recompute_logger_floor() noexcept {
  // The Logger's global floor must not hide a category that asks for more detail than the default.
  log::Level floor = log::Level::Error;
  for (const auto& l : levels_) floor = std::min(floor, l.load(std::memory_order_relaxed));
  logger_->set_min_level(floor);
}

std::vector<std::string> HostLog::apply_config(const config::Config& config) {
  std::vector<std::string> warnings;
  for (auto& l : levels_) l.store(config.log_level, std::memory_order_relaxed);
  for (const auto& [name, level] : config.log_categories) {
    if (const auto cat = parse_cat(name)) {
      levels_[static_cast<std::size_t>(*cat)].store(level, std::memory_order_relaxed);
    } else {
      warnings.push_back("log_categories: unknown category '" + name + "' ignored");
    }
  }
  recompute_logger_floor();
  redactor_->clear_secrets();
  redactor_->add_secret(config.password);
  return warnings;
}

void HostLog::raw(Cat cat, log::Level level, std::string_view text) {
  if (!enabled(cat, level)) return;
  std::string line = "[";
  line += cat_name(cat);
  line += "] ";
  line += text;
  logger_->log_raw(level, line);
}

}  // namespace x4mp::host
