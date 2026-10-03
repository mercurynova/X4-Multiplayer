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
#include "features/join/join_feature.h"

namespace x4mp::host {

void register_builtin_features(FeatureRegistry& registry) {
  // ---- registry (append, one line per feature) ----
  registry.add(std::make_unique<x4mp::features::JoinFeature>());
}

}  // namespace x4mp::host
