#include "features/appearance/appearance_feature.h"

#include <chrono>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include <nlohmann/json.hpp>

#include "message_ids_generated.h"
#include "world_generated.h"

#include "core/config/remembered.h"
#include "features/onfoot/onfoot_hub.h"

namespace x4mp::features {

namespace P = X4MP::Proto;
using host::Cat;
using host::Level;

namespace {
constexpr std::size_t kInboxCap = 8;
constexpr std::size_t kMaxPayloadBytes = 512;

std::int64_t steady_us() {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// The state the picker renders, and the catalogue it needs to draw the lists. Both go out together: a menu that missed a
// change (a reload) still gets a consistent picture from one message.
std::string make_state_json(const appearance::Choice& choice) {
  std::string out = "{\"v\":1,\"race\":\"";
  out += choice.race;
  out += "\",\"preset\":\"";
  out += choice.preset;
  out += "\",\"macro\":\"";
  out += choice.macro;
  out += "\",\"female\":";
  out += choice.is_female ? "true" : "false";
  out += ",\"catalogue\":";
  out += appearance::catalogue_json();
  out += '}';
  return out;
}
}  // namespace

struct AppearanceFeature::Inbox {
  std::mutex m;
  std::vector<std::string> q;
  void push(std::string_view text) {
    if (text.size() > kMaxPayloadBytes) return;
    const std::lock_guard lock(m);
    if (q.size() >= kInboxCap) q.erase(q.begin());
    q.emplace_back(text);
  }
  std::vector<std::string> take() {
    const std::lock_guard lock(m);
    return std::exchange(q, {});
  }
};

AppearanceFeature::AppearanceFeature() : inbox_(std::make_shared<Inbox>()) {}
AppearanceFeature::~AppearanceFeature() = default;

void AppearanceFeature::on_init(host::HostContext& ctx) {
  auto inbox = inbox_;
  // The picker, forwarded by ui/x4mp_menu.lua. May arrive on any thread: copy only.
  const bool ok = ctx.platform.subscribe_event("x4mp.appearance", [inbox](std::string_view text) { inbox->push(text); });
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "appearance: bridge verb x4mp.appearance {}", ok ? "subscribed" : "not available");

  // The stored look, validated: the file may name a look a newer build dropped, which is "default", not an error.
  choice_ = appearance::resolve(ctx.config.avatar_race, ctx.config.avatar_preset);
  if (choice_.empty() && !(ctx.config.avatar_race.empty() && ctx.config.avatar_preset.empty())) {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "appearance: x4mp.json names a look this build does not have ({} / {}), using the default",
              ctx.config.avatar_race, ctx.config.avatar_preset);
  }
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "appearance: {}", appearance::describe(choice_));
  // An empty choice means "let the game pick a crew NPC" - and the game picks a NEW one every time the actor is created, which is
  // why the second client's character changed on every load. A client with no stored look therefore gets one stable look, seeded
  // from its own user file path: the same install always draws the same character, and two clients draw different ones. It is
  // stored like a picked look, so the menu shows it and the picker can change it.
  if (choice_.empty() && ctx.paths != nullptr) {
    std::string seed = ctx.paths->user_file.string();
    if (seed.empty()) seed = ctx.config.player_name;
    if (!seed.empty()) {
      choice_ = appearance::pick_random(static_cast<std::uint32_t>(std::hash<std::string>{}(seed)));
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "appearance: no stored look, picked a stable one for this install ({})",
                appearance::describe(choice_));
      if (!ctx.paths->user_file.empty()) {
        const auto stored = config::update_string_keys(
            ctx.paths->user_file, {{"avatar_race", choice_.race}, {"avatar_preset", choice_.preset}});
        if (!stored.ok) {
          X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "appearance: could not store the look in x4mp.json ({})", stored.error);
        }
      }
    }
  }
  dirty_ = true;  // the server learns it once the session is up
}

void AppearanceFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& /*info*/) {
  drain(ctx);
  auto& hub = onfoot::onfoot_hub();
  if (!hub.linked()) {
    sent_ = false;  // a new session: the relay starts from nothing, so the look goes out again
    return;
  }
  if (!dirty_ && sent_) return;
  if (!send(ctx)) {
    ++send_failed_;
    return;
  }
  dirty_ = false;
  sent_ = true;
  ++sends_;
}

void AppearanceFeature::drain(host::HostContext& ctx) {
  for (const auto& raw : inbox_->take()) handle(ctx, raw);
}

void AppearanceFeature::handle(host::HostContext& ctx, std::string_view payload) {
  nlohmann::json doc;
  try {
    doc = nlohmann::json::parse(payload);
  } catch (const std::exception&) {
    ++refused_;
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "x4mp.appearance refused: not JSON");
    return;
  }
  if (!doc.is_object()) {
    ++refused_;
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "x4mp.appearance refused: not an object");
    return;
  }
  if (doc.value("random", false)) {
    // A fresh look per press: the low bits of the clock are the seed, so two presses a second apart differ.
    apply(ctx, appearance::pick_random(static_cast<std::uint32_t>(steady_us() >> 10)));
    return;
  }
  if (doc.value("default", false)) {
    apply(ctx, appearance::Choice{});
    return;
  }
  const std::string race = doc.value("race", std::string{});
  const std::string preset = doc.value("preset", std::string{});
  if (race.empty() && preset.empty()) {
    publish(ctx);  // a query: the picker opened and wants the current state
    return;
  }
  const appearance::Choice choice = appearance::resolve(race, preset);
  if (choice.empty()) {
    ++refused_;
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "x4mp.appearance refused: {} / {} is not in the catalogue", race, preset);
    publish(ctx);  // resync: the picker shows what the node really uses
    return;
  }
  apply(ctx, choice);
}

void AppearanceFeature::apply(host::HostContext& ctx, const appearance::Choice& choice) {
  if (choice == choice_) {
    publish(ctx);
    return;
  }
  choice_ = choice;
  dirty_ = true;
  ++applied_;
  // x4mp.json: the same atomic writer the remembered Join fields use (core/config/remembered.h). Empty strings mean "default",
  // which has to be written too - the player may have gone back from a chosen look.
  if (ctx.paths != nullptr && !ctx.paths->user_file.empty()) {
    const auto result = config::update_string_keys(
        ctx.paths->user_file, {{"avatar_race", choice.race}, {"avatar_preset", choice.preset}});
    if (!result.ok) {
      X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "appearance: could not store the look in x4mp.json ({})", result.error);
    }
  }
  X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "appearance: now {}", appearance::describe(choice_));
  publish(ctx);
}

void AppearanceFeature::publish(host::HostContext& ctx) {
  if (!ctx.platform.raise_lua("x4mp.appearance_state", make_state_json(choice_))) {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Warn, "appearance: the picker did not take x4mp.appearance_state");
  }
}

bool AppearanceFeature::send(host::HostContext& ctx) {
  auto& hub = onfoot::onfoot_hub();
  fbb_.Clear();
  // The strings must be created before the table: a later CreateString may reallocate the buffer and invalidate an offset that
  // is still an argument expression.
  const auto race = fbb_.CreateString(choice_.race);
  const auto preset = fbb_.CreateString(choice_.preset);
  const auto macro = fbb_.CreateString(choice_.macro);
  const auto clothing = fbb_.CreateString(std::string_view{});
  const auto root = P::CreatePlayerAppearance(fbb_, 0, 0, race, preset, macro, choice_.is_female, clothing);
  fbb_.Finish(root);
  const bool ok = hub.send_control(static_cast<std::uint16_t>(P::MsgType::PlayerAppearance),
                                   {fbb_.GetBufferPointer(), fbb_.GetSize()});
  if (ok) {
    X4MP_CLOG(ctx.log, Cat::Ui, Level::Info, "appearance: sent {} to the relay", appearance::describe(choice_));
  }
  return ok;
}

}  // namespace x4mp::features
