#include "features/events/events_feature.h"

#include <utility>

#include "features/events/events_hub.h"
#include "features/events/hook_state.h"

namespace x4mp::features {

using host::Cat;
using host::Level;

namespace {
// How long a loaded universe may go without the MD half saying hello before the mod says so out loud. MD runs its init cues
// on the first frame of a new game, so 30 s is generous - and a node that never loads md/x4mp_events.xml never says hello.
constexpr std::int64_t kHelloWarnMs = 30'000;
// The inbox is a text queue from another thread: 256 KiB of lines is far more than one frame's worth, and anything beyond
// it is a runaway MD script, not a busy battle.
constexpr std::size_t kInboxMaxItems = 512;
}  // namespace

void EventsFeature::Inbox::push(std::string text) {
  std::lock_guard<std::mutex> lock(mutex);
  if (items.size() >= kInboxMaxItems) return;
  items.push_back(std::move(text));
}

std::vector<std::string> EventsFeature::Inbox::take() {
  std::lock_guard<std::mutex> lock(mutex);
  std::vector<std::string> out;
  out.swap(items);
  return out;
}

EventsFeature::EventsFeature() = default;
EventsFeature::~EventsFeature() = default;

void EventsFeature::on_init(host::HostContext& ctx) {
  inbox_ = std::make_shared<Inbox>();
  auto inbox = inbox_;
  (void)ctx.platform.subscribe_event("x4mp.event", [inbox](std::string_view text) {
    if (text.size() <= kMaxLineBytes) inbox->push(std::string(text));
  });
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info,
            "events: capture armed (bridge verb x4mp.event; MD half md/x4mp_events.xml, Lua half ui/x4mp_events.lua)");
}

void EventsFeature::on_universe_ready(host::HostContext& ctx) {
  bus_.reset();
  hooks_armed_ = 0;
  fired_ = 0;
  fallbacks_ = 0;
  bad_ = 0;
  overrun_ = 0;
  alive_ = false;
  last_event_ms_ = -1;
  universe_ready_ms_ = now_ms_;
  hello_warned_ = false;
  publish();
  X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "events: a universe is ready, capture counters reset (waiting for the MD hello)");
}

void EventsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  now_ms_ += static_cast<std::int64_t>(info.delta_s * 1000.0);

  if (inbox_) {
    std::vector<std::string> lines = inbox_->take();
    std::size_t handled = 0;
    for (const std::string& text : lines) {
      if (handled >= kMaxLinesPerFrame) {
        ++overrun_;
        continue;
      }
      ++handled;
      events::MdEvent event;
      if (!events::parse_event_line(text, event)) {
        ++bad_;
        continue;
      }
      if (event.kind == events::EventKind::HookHello) {
        // The MD half announcing itself: how many hooks it armed. Not an event, never queued.
        if (!alive_) {
          X4MP_CLOG(ctx.log, Cat::Md, Level::Info, "events: the MD half said hello, {} hooks armed", static_cast<int>(event.amount));
        }
        alive_ = true;
        hooks_armed_ = event.amount > 0 ? static_cast<std::uint32_t>(event.amount) : 0;
        continue;
      }
      accept(std::move(event), false);
    }
  }

  // The send budget. A sink is installed by the wave that puts events on the wire; until then the event is counted and kept
  // in the bus ring, which is what `[events]` inspects, and the queue never grows past one frame's worth.
  if (sink_) {
    for (const events::MdEvent& event : bus_.take(events::EventBus::kDefaultPerFrame)) sink_(event);
  } else {
    (void)bus_.take(events::EventBus::kDefaultPerFrame);
  }

  // A dead MD half must be visible, not inferred from an empty log. One warning per universe, not one per frame.
  if (!alive_ && info.universe_ready && now_ms_ - universe_ready_ms_ > kHelloWarnMs && !hello_warned_) {
    hello_warned_ = true;
    X4MP_CLOG(ctx.log, Cat::Md, Level::Warn,
              "events: no MD hello after {}s - the capture ring (md/x4mp_events.xml) is not running; kills, trades and captures will not be "
              "reported by hooks",
              static_cast<int>(kHelloWarnMs / 1000));
  }

  publish();
}

bool EventsFeature::submit(events::MdEvent event, bool from_fallback) { return accept(std::move(event), from_fallback); }

bool EventsFeature::accept(events::MdEvent event, bool from_fallback) {
  if (from_fallback) ++fallbacks_;
  else ++fired_;
  event.at_ms = now_ms_;
  if (!bus_.push(std::move(event))) return false;
  last_event_ms_ = now_ms_;
  return true;
}

void EventsFeature::publish() {
  events::HookState state;
  state.hooks_armed = hooks_armed_;
  state.alive = alive_;
  state.fired = fired_;
  state.fallbacks = fallbacks_;
  state.deduped = bus_.stats().deduped;
  state.queued = static_cast<std::uint32_t>(bus_.queued());
  state.last_age_ms = last_event_ms_ >= 0 ? now_ms_ - last_event_ms_ : -1;
  events::events_hub().publish(state);
}

}  // namespace x4mp::features
