#include "features/resume/resume_state.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <span>

#include <nlohmann/json.hpp>

#include "core/crypto/crypto.h"

namespace x4mp::features::resume {

bool is_resumable_stage(std::string_view stage) noexcept {
  return stage == "joining" || stage == "downloading" || stage == "preparing" || stage == "loading" || stage == "ingame";
}

std::string to_json(const State& s) {
  nlohmann::json j;
  j["stage"] = s.stage;
  j["name"] = s.save_name;
  j["sha"] = crypto::to_hex(std::span<const std::uint8_t>(s.save_sha));
  j["manifest"] = s.has_manifest;
  j["cp_lo"] = s.checkpoint.lo;
  j["cp_hi"] = s.checkpoint.hi;
  char epoch[24];
  std::snprintf(epoch, sizeof epoch, "%016llx", static_cast<unsigned long long>(s.epoch));
  j["epoch"] = epoch;  // hex text: a 64-bit value does not survive every JSON number reader
  j["fp"] = s.fingerprint.valid;
  j["pid"] = s.fingerprint.player_id;
  j["gt"] = s.fingerprint.game_time;
  return j.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace);
}

std::optional<State> parse(std::string_view text) {
  try {
    const auto j = nlohmann::json::parse(text, nullptr, false);
    if (!j.is_object()) return std::nullopt;
    State s;
    s.stage = j.value("stage", std::string{});
    s.save_name = j.value("name", std::string{});
    s.has_manifest = j.value("manifest", false);
    s.checkpoint = session::Id128{j.value("cp_lo", std::uint64_t{0}), j.value("cp_hi", std::uint64_t{0})};
    std::vector<std::uint8_t> sha;
    if (crypto::from_hex(j.value("sha", std::string{}), sha)) s.save_sha = std::move(sha);
    const std::string epoch = j.value("epoch", std::string{});
    if (!epoch.empty() && epoch.size() <= 16) {
      char* end = nullptr;
      s.epoch = std::strtoull(epoch.c_str(), &end, 16);
      if (end == nullptr || *end != '\0') s.epoch = 0;
    }
    s.fingerprint.valid = j.value("fp", false);
    s.fingerprint.player_id = j.value("pid", std::uint64_t{0});
    s.fingerprint.game_time = j.value("gt", 0.0);
    return s;
  } catch (...) {
    return std::nullopt;  // wrong field types
  }
}

Decision decide_universe(const State& previous, const Fingerprint& now) noexcept {
  if (previous.stage != "ingame") return {Verdict::NewUniverse, "the node was not in-game"};
  if (!previous.fingerprint.valid) return {Verdict::NewUniverse, "no fingerprint was sampled before the reload"};
  if (!now.valid) return {Verdict::NewUniverse, "no fingerprint available now"};
  if (previous.fingerprint.player_id != now.player_id) return {Verdict::NewUniverse, "the player id changed"};
  const double dt = now.game_time - previous.fingerprint.game_time;
  if (!std::isfinite(dt)) return {Verdict::NewUniverse, "the game clock is not a number"};
  if (dt < -kClockBackSlackS) return {Verdict::NewUniverse, "the game clock went backwards"};
  if (dt > kClockForwardWindowS) return {Verdict::NewUniverse, "the game clock jumped forward"};
  return {Verdict::SameUniverse, "same player id, clock continuous"};
}

}  // namespace x4mp::features::resume
