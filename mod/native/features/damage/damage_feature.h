#pragma once
// features/damage: the actions-shim half of the damage relay (M15).
//
// The hub collects the damage both nodes want applied; this feature flushes it once a frame to ui/x4mp_actions.lua, which
// hands it to md/x4mp_actions.xml. MD is the only writer of hull and shield, so the feature itself never touches the game
// beyond that one raise_lua call.

#include <string_view>

#include "features/damage/damage_hub.h"
#include "host/feature.h"

namespace x4mp::features {

class DamageFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "damage"; }

  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  // M15: how many damage lines one frame may carry. Combat is bursty, but a frame that spends its whole time in MD is worse
  // than damage arriving a frame or two late; the rest stays queued.
  static constexpr std::size_t kPerFrame = 64;
  std::uint64_t last_log_us_ = 0;
  void flush(host::HostContext& ctx);
  void report(host::HostContext& ctx);
};

}  // namespace x4mp::features
