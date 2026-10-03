#include "host/build_check.h"

#include <cctype>
#include <charconv>

namespace x4mp::host {

namespace {
bool is_digit(char c) { return c >= '0' && c <= '9'; }

std::optional<int> to_int(std::string_view s) {
  if (s.empty() || s.size() > 6) return std::nullopt;
  int v = 0;
  const auto r = std::from_chars(s.data(), s.data() + s.size(), v);
  if (r.ec != std::errc{} || r.ptr != s.data() + s.size()) return std::nullopt;
  return v;
}
}  // namespace

std::optional<int> parse_version_code(std::string_view text) {
  // First "<digits>.<digits>" in the text.
  for (std::size_t i = 0; i < text.size(); ++i) {
    if (!is_digit(text[i])) continue;
    std::size_t a = i;
    while (a < text.size() && is_digit(text[a])) ++a;
    if (a < text.size() && text[a] == '.' && a + 1 < text.size() && is_digit(text[a + 1])) {
      std::size_t b = a + 1;
      while (b < text.size() && is_digit(text[b])) ++b;
      const auto major = to_int(text.substr(i, a - i));
      const auto minor = to_int(text.substr(a + 1, b - a - 1));
      if (major && minor) return *major * 100 + *minor;
      return std::nullopt;
    }
    i = a;
  }
  return std::nullopt;
}

std::optional<std::string> extract_build_number(std::string_view text) {
  std::string_view best;
  for (std::size_t i = 0; i < text.size();) {
    if (!is_digit(text[i])) {
      ++i;
      continue;
    }
    std::size_t j = i;
    while (j < text.size() && is_digit(text[j])) ++j;
    if (j - i >= 5 && j - i > best.size()) best = text.substr(i, j - i);
    i = j;
  }
  if (best.empty()) return std::nullopt;
  return std::string(best);
}

BuildCheck check_build(const BuildInfo& info, std::string_view pin) {
  BuildCheck out;
  const auto dash = pin.find('-');
  const std::string_view pin_ver_text = pin.substr(0, dash);
  const std::string_view pin_build = dash == std::string_view::npos ? std::string_view{} : pin.substr(dash + 1);
  const auto pin_ver = to_int(pin_ver_text);

  // Game version code: the game's own struct wins, then X4Native's string, then the SDK types build.
  std::optional<int> ver;
  if (info.version) ver = info.version->major * 100 + info.version->minor;
  if (!ver) ver = parse_version_code(info.game_version);
  if (!ver && info.game_types_build > 0) ver = info.game_types_build;

  std::optional<std::string> build;
  if (info.build_suffix) build = extract_build_number(*info.build_suffix);

  out.detected = (ver ? std::to_string(*ver) : std::string("?")) + "-" + (build ? *build : std::string("?"));
  const std::string supported = "supported: " + std::string(pin);

  if (!pin_ver || pin_build.empty()) {
    out.status = BuildStatus::Unsupported;
    out.reason = "invalid build pin '" + std::string(pin) + "'";
    return out;
  }
  if (!ver) {
    out.status = BuildStatus::Unsupported;
    out.reason = "cannot determine the X4 version (" + supported + ")";
    return out;
  }
  if (*ver != *pin_ver) {
    out.status = BuildStatus::Unsupported;
    out.reason = "Unsupported X4 version " + std::to_string(*ver) + " (" + supported + ")";
    return out;
  }
  if (build) {
    if (*build == pin_build) {
      out.status = BuildStatus::Supported;
      out.reason = "X4 build " + out.detected + " matches the pinned build";
    } else {
      out.status = BuildStatus::Unsupported;
      out.reason = "Unsupported X4 build " + out.detected + " (" + supported + ")";
    }
    return out;
  }
  out.status = BuildStatus::Unverified;
  out.reason = "X4 version " + std::to_string(*ver) + " matches but the build number could not be read (" + supported + ")";
  return out;
}

}  // namespace x4mp::host
