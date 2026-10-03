#include "features/saves/saves_feature.h"

#include <utility>

#include <nlohmann/json.hpp>

#include "features/diag/diag_hub.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 64;       // bridge messages kept between two frames (oldest dropped)
constexpr std::size_t kMaxTextBytes = 4096;  // a bridge payload is a tiny JSON object
constexpr auto kWarningInterval = std::chrono::seconds(5);
constexpr std::uint64_t kRetryFrames = 120;  // raise_lua failed: try again after this many frames

nlohmann::json parse_object(const std::string& text) {
  try {
    auto j = nlohmann::json::parse(text, nullptr, /*allow_exceptions=*/false);
    if (j.is_object()) return j;
  } catch (...) {
  }
  return nlohmann::json::object();
}

bool get_bool(const nlohmann::json& j, const char* key, bool dflt = false) {
  const auto it = j.find(key);
  return (it != j.end() && it->is_boolean()) ? it->get<bool>() : dflt;
}
}  // namespace

struct SavesFeature::Inbox {
  std::mutex m;
  std::vector<std::pair<std::string, std::string>> q;  // verb, text
  void push(const char* verb, std::string_view text) {
    if (text.size() > kMaxTextBytes) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(verb, std::string(text));
  }
  std::vector<std::pair<std::string, std::string>> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

SavesFeature::SavesFeature() : inbox_(std::make_shared<Inbox>()) {}

void SavesFeature::on_init(host::HostContext& ctx) {
  static constexpr const char* kVerbs[] = {"ui_ready", "saves_status", "game_saved", "saves_debug"};
  int ok = 0;
  for (const char* verb : kVerbs) {
    auto inbox = inbox_;
    std::string v = verb;
    if (ctx.platform.subscribe_event(("x4mp." + v).c_str(),
                                     [inbox, v](std::string_view text) { inbox->push(v.c_str(), text); })) {
      ++ok;
    }
  }
  X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "save control: {} of {} bridge verbs subscribed (no hooks, no pin)", ok,
            static_cast<int>(std::size(kVerbs)));
}

void SavesFeature::on_game_loaded(host::HostContext&) { push_needed_ = true; }
void SavesFeature::on_universe_ready(host::HostContext&) { push_needed_ = true; }

void SavesFeature::on_frame(host::HostContext& ctx, const host::FrameInfo&) {
  for (auto& [verb, text] : inbox_->take()) {
    try {
      handle(ctx, verb, text);
    } catch (const std::exception& e) {
      X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "save control: bad '{}' message ignored ({})", verb, e.what());
    }
  }
  const bool want = diag_hub().is_client();
  const bool changed = last_pushed_ != (want ? 1 : 0);
  if (!changed && !push_needed_) return;
  // One try per frame at most; if Lua is not listening yet (raise fails) wait before retrying.
  if (frames_since_try_ > 0 && ++frames_since_try_ < kRetryFrames) return;
  frames_since_try_ = 0;
  push_block(ctx, want);
}

void SavesFeature::push_block(host::HostContext& ctx, bool block) {
  const std::string payload = std::string("{\"v\":1,\"block\":") + (block ? "true" : "false") + "}";
  if (ctx.platform.raise_lua("x4mp.saves", payload)) {
    last_pushed_ = block ? 1 : 0;
    push_needed_ = false;
    ++counters_.pushes;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "save control: block={} pushed to Lua", block);
  } else {
    frames_since_try_ = 1;
    X4MP_CLOG(ctx.log, Cat::Save, Level::Debug, "save control: raise_lua failed, retrying later");
  }
}

void SavesFeature::handle(host::HostContext& ctx, const std::string& verb, const std::string& text) {
  if (verb == "ui_ready") {
    push_needed_ = true;  // a fresh Lua state: it has no flag yet
    frames_since_try_ = 0;
    return;
  }
  const auto j = parse_object(text);
  if (verb == "saves_status") {
    SavesStatus s;
    s.received = true;
    s.save_game_wrapped = get_bool(j, "save_game");
    s.is_saving_possible_wrapped = get_bool(j, "is_saving_possible");
    s.menu_row_patched = get_bool(j, "menu_row");
    s.tooltip_patched = get_bool(j, "tooltip");
    s.blocking = get_bool(j, "blocking");
    if (const auto it = j.find("detail"); it != j.end() && it->is_string()) s.detail = it->get<std::string>().substr(0, 200);
    diag_hub().set_saves_status(s);
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info,
              "save wrappers: SaveGame={} IsSavingPossible={} menu_row={} tooltip={} blocking={} {}", s.save_game_wrapped,
              s.is_saving_possible_wrapped, s.menu_row_patched, s.tooltip_patched, s.blocking, s.detail);
  } else if (verb == "game_saved") {
    on_game_saved(ctx, text);
  } else if (verb == "saves_debug") {
    if (!ctx.config.selftest) return;  // test seam: only with selftest=true
    const auto it = j.find("role");
    const std::string role = (it != j.end() && it->is_string()) ? it->get<std::string>() : "none";
    const NodeRole r = role == "client" ? NodeRole::Client : role == "authority" ? NodeRole::Authority : NodeRole::None;
    diag_hub().set_connection(r, get_bool(j, "connected"));
    X4MP_CLOG(ctx.log, Cat::Save, Level::Info, "save control: test seam set role={} connected={}", role,
              get_bool(j, "connected"));
  }
}

void SavesFeature::on_game_saved(host::HostContext& ctx, const std::string& text) {
  auto& hub = diag_hub();
  if (!hub.is_client()) {
    X4MP_CLOG(ctx.log, Cat::Save, Level::Debug, "game saved (not a client: no action)");
    return;
  }
  ++counters_.saves_while_client;
  const auto now = std::chrono::steady_clock::now();
  if (warned_once_ && now - last_warning_ < kWarningInterval) return;  // rate limit: one warning per 5 s
  warned_once_ = true;
  last_warning_ = now;
  ++counters_.warnings_sent;
  (void)text;
  constexpr const char* kWarn =
      "a game save was written while connected as a client (a quicksave or another path that bypasses the save "
      "wrapper); the session save is the server's, this local save is not part of the session";
  X4MP_CLOG(ctx.log, Cat::Save, Level::Warn, "{}", kWarn);
  hub.forward_log(Level::Warn, std::string("[saves] ") + kWarn);
  ctx.platform.raise_lua(
      "x4mp.notify",
      R"({"v":1,"level":"warn","text":"Saving is disabled while connected as a client. This save is a local copy only."})");
}

}  // namespace x4mp::features
