#include "host/redact.h"

#include <algorithm>
#include <array>
#include <cctype>

namespace x4mp::host {

namespace {
constexpr std::array<std::string_view, 9> kKeys = {"password", "passwd", "pwd",        "secret",     "token",
                                                   "api_key",  "apikey", "player_key", "private_key"};

char lower(char c) { return static_cast<char>(std::tolower(static_cast<unsigned char>(c))); }

bool ieq_at(std::string_view text, std::size_t pos, std::string_view word) {
  if (pos + word.size() > text.size()) return false;
  for (std::size_t i = 0; i < word.size(); ++i) {
    if (lower(text[pos + i]) != word[i]) return false;
  }
  return true;
}

bool is_value_end(char c) {
  return c == ' ' || c == '\t' || c == '"' || c == '\'' || c == ',' || c == ';' || c == '}' || c == ']' || c == '&' ||
         c == '\r' || c == '\n';
}

// Handles "<key>[quote] [sep] [quote]value"; returns the [begin,end) of the value or {npos,npos}.
std::pair<std::size_t, std::size_t> value_after_key(std::string_view text, std::size_t after_key) {
  std::size_t i = after_key;
  if (i < text.size() && (text[i] == '"' || text[i] == '\'')) ++i;  // closing quote of a JSON key
  while (i < text.size() && (text[i] == ' ' || text[i] == '\t')) ++i;
  if (i >= text.size() || (text[i] != '=' && text[i] != ':')) return {std::string_view::npos, std::string_view::npos};
  ++i;
  while (i < text.size() && (text[i] == ' ' || text[i] == '\t')) ++i;
  if (i < text.size() && (text[i] == '"' || text[i] == '\'')) ++i;  // opening quote of the value
  std::size_t end = i;
  while (end < text.size() && !is_value_end(text[end])) ++end;
  if (end == i) return {std::string_view::npos, std::string_view::npos};
  return {i, end};
}
}  // namespace

std::string Redactor::redact_keys(std::string_view text) {
  std::string out;
  out.reserve(text.size());
  std::size_t i = 0;
  while (i < text.size()) {
    // Authorization: <rest of line>
    if (ieq_at(text, i, "authorization")) {
      const std::size_t colon = text.find(':', i);
      if (colon != std::string_view::npos && colon - i <= 16) {
        std::size_t end = text.find_first_of("\r\n", colon);
        if (end == std::string_view::npos) end = text.size();
        out.append(text.substr(i, colon + 1 - i));
        out.push_back(' ');
        out.append(kRedacted);
        i = end;
        continue;
      }
    }
    // Bearer <token>
    if (ieq_at(text, i, "bearer ") && (i == 0 || !std::isalnum(static_cast<unsigned char>(text[i - 1])))) {
      std::size_t b = i + 7;
      while (b < text.size() && text[b] == ' ') ++b;
      std::size_t e = b;
      while (e < text.size() && !is_value_end(text[e])) ++e;
      if (e > b) {
        out.append(text.substr(i, 7));
        out.append(kRedacted);
        i = e;
        continue;
      }
    }
    bool matched = false;
    for (const auto key : kKeys) {
      if (!ieq_at(text, i, key)) continue;
      const auto [vb, ve] = value_after_key(text, i + key.size());
      if (vb == std::string_view::npos) continue;
      out.append(text.substr(i, vb - i));
      out.append(kRedacted);
      i = ve;
      matched = true;
      break;
    }
    if (matched) continue;
    out.push_back(text[i]);
    ++i;
  }
  return out;
}

void Redactor::add_secret(std::string_view secret) {
  if (secret.size() < kMinSecretLen) return;
  const std::lock_guard lock(mutex_);
  if (std::ranges::find(secrets_, secret) == secrets_.end()) secrets_.emplace_back(secret);
}

void Redactor::clear_secrets() {
  const std::lock_guard lock(mutex_);
  secrets_.clear();
}

std::string Redactor::apply(std::string_view text) const {
  std::string out = redact_keys(text);
  std::vector<std::string> secrets;
  {
    const std::lock_guard lock(mutex_);
    secrets = secrets_;
  }
  for (const auto& s : secrets) {
    std::size_t pos = 0;
    while ((pos = out.find(s, pos)) != std::string::npos) {
      out.replace(pos, s.size(), kRedacted);
      pos += kRedacted.size();
    }
  }
  return out;
}

void RedactingSink::write(log::Level level, std::int64_t unix_ms, std::string_view text) {
  const std::string clean = redactor_->apply(text);
  inner_->write(level, unix_ms, clean);
}

}  // namespace x4mp::host
