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
#include "features/diag/knowledge_feature.h"
#include "features/janitor/janitor_feature.h"
#include "features/saves/saves_feature.h"
#include "features/selftest/selftest_feature.h"
#include "features/join/join_feature.h"
#include "features/launch/launch_feature.h"
#include "features/stats/stats_feature.h"
#include "features/chat/chat_feature.h"
#include "features/teams/teams_feature.h"
#include "features/avatars/avatars_feature.h"
#include "features/selfship/selfship_feature.h"
#include "features/ghosts/ghost_feature.h"
#include "features/statics/statics_feature.h"
#include "features/onfoot/onfoot_feature.h"
#include "features/admin/admin_feature.h"
#include "features/appearance/appearance_feature.h"
#include "features/events/events_feature.h"
#include "features/damage/damage_feature.h"
#include "features/economy/economy_feature.h"
#include "features/builds/builds_feature.h"
#include "features/capture/capture_feature.h"
#include "features/wallet/wallet_feature.h"
#include "features/economy_client/economy_client_feature.h"
#include "features/settlement/settlement_feature.h"
#include "features/server/server_feature.h"

namespace x4mp::host {

void register_builtin_features(FeatureRegistry& registry) {
  // ---- registry (append, one line per feature) ----
  registry.add(std::make_unique<x4mp::features::KnowledgeFeature>());  // FIRST: its universe-ready probe runs before anything else of ours (M3-23)
  registry.add(std::make_unique<x4mp::features::SavesFeature>());
  registry.add(std::make_unique<x4mp::features::SelfTestFeature>());
  registry.add(std::make_unique<x4mp::features::JanitorFeature>());
  registry.add(std::make_unique<x4mp::features::SelfShipFeature>());  // before join: the seat edge reaches the authority flow in the same frame
  registry.add(std::make_unique<x4mp::features::JoinFeature>());
  registry.add(std::make_unique<x4mp::features::LaunchFeature>());
  registry.add(std::make_unique<x4mp::features::ChatFeature>());  // after join: it forwards what the join pump collected this frame
  registry.add(std::make_unique<x4mp::features::TeamsFeature>());  // after join: it applies what the join pump collected this frame (M3-08)
  registry.add(std::make_unique<x4mp::features::AvatarsFeature>());  // after teams and selfship: needs the team factions and the sector map (M3-11)
  registry.add(std::make_unique<x4mp::features::GhostFeature>());  // after avatars: ghosts are the client half, avatars the authority half (they never act on the same node); after join/teams: frames and faction readiness (M3-10)
  registry.add(std::make_unique<x4mp::features::StaticsFeature>());  // after avatars (the manifest path) and ghosts (the interest sectors); before onfoot, which reads the station ids it binds (M4-18)
  registry.add(std::make_unique<x4mp::features::OnFootFeature>());  // after ghosts and selfship: it sends on-foot samples through join's realtime link and reads the sector map the selfship feature keeps (M3b)
  registry.add(std::make_unique<x4mp::features::AdminFeature>());  // after join: it sends AdminCommand on the control lane of the link join installed this frame (m3-plan Q6)
  registry.add(std::make_unique<x4mp::features::AppearanceFeature>());  // after join: it sends PlayerAppearance on the control lane of the link join installed this frame (M3b, ADR-051)
  registry.add(std::make_unique<x4mp::features::EventsFeature>());  // M5: drains the MD capture ring into the bus and publishes the md_hook state; before stats, which reads it (M5-01..M5-04)
  registry.add(std::make_unique<x4mp::features::EconomyFeature>());  // M5-13..M5-17: trades upward, station stock down, the actions shim; after events (it feeds it the Trade events) and statics (station ids)
  registry.add(std::make_unique<x4mp::features::DamageFeature>());  // M15: the hull/shield lines both nodes apply through MD; after economy, whose actions shim it shares
  registry.add(std::make_unique<x4mp::features::builds::BuildsFeature>());  // M5-18/M5-19 client half: a finished build goes up as one request and the result pins the station id; after events (Build events) and statics (the id table)
  registry.add(std::make_unique<x4mp::features::capture::CaptureFeature>());  // M5-20: the typed EntityChangedOwner hook reports a captured ghost; after ghosts (the component map) and events (the send path)
  registry.add(std::make_unique<x4mp::features::wallet::WalletFeature>());  // M5-22: the x4mp.money poll into wallet_hub; after economy, which will book its changes upward (M5-23)
  registry.add(std::make_unique<x4mp::features::economy_client::EconomyClientFeature>());  // M5-29/30: the x4mp.economy verb and the EconomyResult/LoanStatus/TradeStatus/TradeResult snapshot, after wallet so the screen sees the balance
  registry.add(std::make_unique<x4mp::features::settlement::SettlementFeature>());  // M5-31: the authority's side of an escrowed trade - AssetTransferOrder in, AssetTransferConfirm out, a journal on disk so a restart can still answer TradeQuery
  registry.add(std::make_unique<x4mp::features::server::ServerFeature>());  // M5-32: the menu starts and stops the dedicated server this game ships; no world state, so its place in the order only matters for shutdown
  registry.add(std::make_unique<x4mp::features::StatsFeature>());  // last: its frame cost sample covers the earlier features
}

}  // namespace x4mp::host
