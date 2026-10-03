// THE feature registry: the one place that lists the features shipped in x4mp.dll (M2-04).
//
// To add a feature (m2-plan 5 shared-file rules, later M2/M3 tasks):
//   1. Put the feature in mod/native/features/<name>/ (any .cpp there is picked up by CMake automatically, see the
//      "M2-04 host" block in mod/CMakeLists.txt). It implements x4mp::host::IFeature (host/feature.h).
//   2. Add ONE #include line below the "feature includes" marker and ONE registry line below the "registry" marker.
//      Example:
//        #include "features/join/join_feature.h"
//        registry.add(std::make_unique<x4mp::features::JoinFeature>());
// Order of the registry lines = order of on_init / on_frame; shutdown runs in reverse.

#include <memory>

#include "host/feature.h"
#include "host/mod_host.h"

// ---- feature includes (append, one per feature) ----
#include "features/janitor/janitor_feature.h"
#include "features/saves/saves_feature.h"
#include "features/selftest/selftest_feature.h"
#include "features/join/join_feature.h"
#include "features/launch/launch_feature.h"
#include "features/stats/stats_feature.h"
#include "features/chat/chat_feature.h"
#include "features/teams/teams_feature.h"
#include "features/selfship/selfship_feature.h"

namespace x4mp::host {

void register_builtin_features(FeatureRegistry& registry) {
  // ---- registry (append, one line per feature) ----
  registry.add(std::make_unique<x4mp::features::SavesFeature>());
  registry.add(std::make_unique<x4mp::features::SelfTestFeature>());
  registry.add(std::make_unique<x4mp::features::JanitorFeature>());
  registry.add(std::make_unique<x4mp::features::SelfShipFeature>());  // before join: the seat edge reaches the authority flow in the same frame
  registry.add(std::make_unique<x4mp::features::JoinFeature>());
  registry.add(std::make_unique<x4mp::features::LaunchFeature>());
  registry.add(std::make_unique<x4mp::features::ChatFeature>());  // after join: it forwards what the join pump collected this frame
  registry.add(std::make_unique<x4mp::features::TeamsFeature>());  // after join: it applies what the join pump collected this frame (M3-08)
  registry.add(std::make_unique<x4mp::features::StatsFeature>());  // last: its frame cost sample covers the earlier features
}

}  // namespace x4mp::host
