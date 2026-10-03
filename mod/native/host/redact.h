#pragma once
// host/redact: secret redaction for log lines (M2-04). Passwords, tokens and keys must never reach a log file.
//
// Two layers, both applied by RedactingSink to every line the Logger's writer thread hands to a sink:
//   1. Key patterns: for key names password, passwd, pwd, secret, token, api_key, apikey, player_key, private_key
//      (case-insensitive, also inside "resume_token", "admin_password" ...) followed by `=` or `:` (optionally quoted,
//      JSON style), the VALUE is replaced with <redacted>. `Authorization:` and `Bearer` redact the rest of the line /
//      the next word.
//   2. Registered secrets: exact strings (for example the configured password, at least kMinSecretLen characters)
//      are replaced everywhere in the line, whatever surrounds them.
// Thread-safe: add_secret/clear_secrets may be called from the main thread while the writer thread redacts.

#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "core/log/log.h"

namespace x4mp::host {

inline constexpr std::string_view kRedacted = "<redacted>";

class Redactor {
 public:
  static constexpr std::size_t kMinSecretLen = 4;  // shorter strings would shred ordinary text

  void add_secret(std::string_view secret);
  void clear_secrets();
  [[nodiscard]] std::string apply(std::string_view text) const;

  // Stateless layer 1 only.
  [[nodiscard]] static std::string redact_keys(std::string_view text);

 private:
  mutable std::mutex mutex_;
  std::vector<std::string> secrets_;
};

// Sink decorator: redacts, then forwards to `inner`.
class RedactingSink final : public log::Sink {
 public:
  RedactingSink(std::shared_ptr<log::Sink> inner, std::shared_ptr<Redactor> redactor)
      : inner_(std::move(inner)), redactor_(std::move(redactor)) {}
  void write(log::Level level, std::int64_t unix_ms, std::string_view text) override;
  void flush() override { inner_->flush(); }
  [[nodiscard]] log::Level min_level() const noexcept override { return inner_->min_level(); }

 private:
  std::shared_ptr<log::Sink> inner_;
  std::shared_ptr<Redactor> redactor_;
};

}  // namespace x4mp::host
