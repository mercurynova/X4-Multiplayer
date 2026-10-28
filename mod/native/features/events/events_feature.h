#pragma once
// features/events/events_feature: the node's event capture (M5-01..M5-04; docs/m5-plan.md W1).
//
// It owns the bridge subscription, the inbox between the platform's event thread and the frame thread, the bus, and the
// `md_hook` state. What it does NOT own yet is the wire: a wave that sends an event installs a sink (M5-05 upward, then W2's
// kills), and until a sink is installed a captured event is counted and kept in the bus ring so `[events]` can be inspected.
//
//   MD   md/x4mp_events.xml     raise_lua_event "x4mp.event" "<line>"        (one line per occurrence, never per frame)
//   Lua  ui/x4mp_events.lua     verb x4mp.event {"v":1,"data":"<line>"}
//   here                        parse -> dedupe -> queue -> per-frame budget -> sink
//   line hooks=alive md=9 ...   the 30 s budget line (stats feature reads events_hub())
//
// The fallbacks (M5-04) live here too, because they exist for exactly one reason: to produce the same MdEvent when the MD
// half is dead or missed an occurrence. They run on timers, never per frame, and they feed the same bus, so dedupe covers
// "hook and fallback both saw it".

#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "features/events/event_bus.h"
#include "features/events/event_types.h"
#include "features/events/death_report.h"
#include "features/events/kill_report.h"
#include "host/feature.h"

namespace x4mp::features {

class EventsFeature final : public host::IFeature {
 public:
  EventsFeature();
  ~EventsFeature() override;

  [[nodiscard]] std::string_view name() const noexcept override { return "events"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_universe_ready(host::HostContext& ctx) override;

  // The platform's event thread writes here; the frame thread drains. Bounded on both sides so a flood cannot grow the heap.
  struct Inbox {
    std::mutex mutex;
    std::vector<std::string> items;
    void push(std::string text);
    [[nodiscard]] std::vector<std::string> take();
  };

  // Installed by the wave that puts events on the wire. Empty means "capture only", which is W1's state.
  void set_sink(std::function<void(const events::MdEvent&)> sink) { sink_ = std::move(sink); }
  [[nodiscard]] events::EventBus& bus() noexcept { return bus_; }

  // M5-04: a detector calls this when it saw something the hooks did not. The event goes through the same dedupe, so a
  // detector that races its hook is free.
  bool submit(events::MdEvent event, bool from_fallback);

  static constexpr std::size_t kMaxLinesPerFrame = 256;
  static constexpr std::size_t kMaxLineBytes = 4096;

 private:
  bool accept(events::MdEvent event, bool from_fallback);
  void publish();

  std::shared_ptr<Inbox> inbox_;
  events::EventBus bus_;
  std::unique_ptr<events::KillReporter> reporter_;
  std::unique_ptr<events::DeathReporter> death_;
  std::function<void(const events::MdEvent&)> sink_;
  std::int64_t now_ms_ = 0;
  std::int64_t last_event_ms_ = -1;
  std::int64_t universe_ready_ms_ = 0;
  bool hello_warned_ = false;
  std::uint32_t hooks_armed_ = 0;
  std::uint32_t fired_ = 0;
  std::uint32_t fallbacks_ = 0;
  std::uint32_t bad_ = 0;
  std::uint32_t overrun_ = 0;
  bool alive_ = false;
};

}  // namespace x4mp::features
