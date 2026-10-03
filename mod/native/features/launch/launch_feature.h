#pragma once
// features/launch: the one-shot launch.json auto-connect (M2-12; docs/mod-design.md 2.6 item 4).
//
// At init the feature reads <config dir>\launch.json, deletes it immediately (also when it is invalid or expired), and, for a valid
// unexpired request, hands an x4mp.join payload to the join flow (features/join/join_requests.h), so the join starts without any UI
// and goes exactly the way the Join screen's x4mp.join verb does. A resumed session (reload) wins over the request. Secrets are never
// logged.

#include "host/feature.h"

namespace x4mp::features {

class LaunchFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "launch"; }
  void on_init(host::HostContext& ctx) override;
};

}  // namespace x4mp::features
