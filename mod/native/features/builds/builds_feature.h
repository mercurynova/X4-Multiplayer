#pragma once
// features/builds: the client half of M5-18 - a finished build becomes one Intent{StationBuildRequest}, and the authority's
// IntentResult binds the station id this node will use from then on (docs/m5-plan.md W5, docs/protocol.md 1195).
//
// The events feature already parses the MD B line (event_player_build_finished) into an MdEvent with the buildanchor component and
// the station macro. BuildReporter asks the game for the sector index and the pose, sends the request, and remembers it until the
// IntentResult arrives; BuildsHub routes that result back and pins the id into the statics hub (M5-19's "the client binds its
// local station to result_net_id").
//
// The authority's EntitySpawn is what tells every other node about the station; this node needs nothing from it, because it
// already has the station - only the id has to be agreed on.

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string>

#include "features/builds/build_report.h"
#include "features/events/event_wire.h"
#include "host/feature.h"
#include "message_ids_generated.h"

namespace x4mp::features::builds {

// Frame routing for the client: IntentResult frames are addressed to this node, not to a hub of their own.
class BuildsHub {
 public:
  void set_result_sink(std::function<void(const events::IntentResultView&)> sink);
  // The events feature hands every Build event here, exactly like the economy hub takes Trade events.
  void set_event_sink(std::function<bool(const events::MdEvent&)> sink) { event_sink_ = std::move(sink); }
  bool on_event(const events::MdEvent& event) { return event_sink_ ? event_sink_(event) : false; }
  void on_frame_message(X4MP::Proto::MsgType type, std::span<const std::uint8_t> payload);
  [[nodiscard]] std::uint64_t results() const noexcept { return results_; }
  [[nodiscard]] std::uint64_t bad() const noexcept { return bad_; }
  void reset();

 private:
  std::function<void(const events::IntentResultView&)> sink_;
  std::function<bool(const events::MdEvent&)> event_sink_;
  std::uint64_t results_ = 0;
  std::uint64_t bad_ = 0;
};

[[nodiscard]] BuildsHub& builds_hub();

class BuildsFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "builds"; }

  void on_init(host::HostContext& ctx) override;
  void on_game_loaded(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  void on_result(host::HostContext& ctx, const events::IntentResultView& view);
  void report(host::HostContext& ctx, bool force);

  std::unique_ptr<BuildReporter> reporter_;
  host::HostContext* ctx_ = nullptr;  // the describe callback needs the game API; valid from on_init to on_shutdown
  std::uint64_t bound_ = 0;
  std::uint64_t rejected_ = 0;
  std::uint64_t last_log_ms_ = 0;
  bool on_authority_ = false;
};

}  // namespace x4mp::features::builds
