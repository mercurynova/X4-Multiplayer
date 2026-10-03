#pragma once
// features/janitor: load-time janitor SKELETON (M2-10, mod-design 6.2 item 4).
//
// At on_universe_ready (both roles) count the objects that carry the ghost name prefix "[MP] " and log the number.
// Nothing is removed yet: ghosts only exist from M4, which adds the removal through SafeRemove (and the reference mod's
// leftovers). The scan covers the factions that can own such objects today: the player and the team factions
// x4mp_team_1..8 (only those the game lists via GetAllFactions: asking for an undefined faction writes an error line to the game log). It only uses exports resolved by name through the platform, so a game without them logs "skipped".
//
// The scan runs on the first frame after on_universe_ready (game calls are frame-thread only) and is bounded
// (kMaxObjects names read per run).

#include <atomic>
#include <cstdint>
#include <string>
#include <string_view>

#include "host/feature.h"

namespace x4mp::features {

inline constexpr std::string_view kGhostNamePrefix = "[MP] ";

struct JanitorResult {
  bool ran = false;
  bool exports_missing = false;
  std::uint32_t scanned = 0;
  std::uint32_t marked = 0;  // names starting with kGhostNamePrefix
  std::uint32_t factions_queried = 0;  // "player" + the team factions the game actually lists
};

class JanitorFeature final : public host::IFeature {
 public:
  static constexpr std::uint32_t kMaxObjects = 4000;

  [[nodiscard]] std::string_view name() const noexcept override { return "janitor"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_universe_ready(host::HostContext&) override { pending_ = true; }
  void on_game_loaded(host::HostContext&) override { pending_ = false; }

  [[nodiscard]] static JanitorResult scan(host::HostContext& ctx);
  [[nodiscard]] const JanitorResult& last() const noexcept { return last_; }

 private:
  std::atomic<bool> pending_{false};
  JanitorResult last_;
};

}  // namespace x4mp::features
