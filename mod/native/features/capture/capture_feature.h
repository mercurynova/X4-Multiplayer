#pragma once
// features/capture: the client half of M5-20 (docs/m5-plan.md W5).
//
// The MD event ring cannot see an owner change, so this feature subscribes to the game's typed MD hook EntityChangedOwner
// (type 175) through the platform and hands every change to CaptureReporter, which reports a capture of a ghost to the
// authority. The hook runs on the game thread: it only copies the component into a ring, and on_frame drains it.
//
// The authority half (M5-21) lives in features/authority/authority_flow.cpp (on_capture_report).

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "features/capture/capture_report.h"
#include "host/feature.h"

namespace x4mp::features::capture {

class CaptureFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "capture"; }

  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

  // Called from the typed hook (game thread). Bounded: a hook storm cannot grow the ring without limit.
  void push(std::uint64_t component);

 private:
  void report(host::HostContext& ctx, bool force);

  std::unique_ptr<CaptureReporter> reporter_;
  std::mutex mutex_;
  std::vector<std::uint64_t> ring_;
  std::uint64_t dropped_ = 0;
  std::uint64_t drained_ = 0;
  std::int64_t last_log_us_ = 0;
  bool hook_armed_ = false;
  bool on_authority_ = false;
};

}  // namespace x4mp::features::capture
