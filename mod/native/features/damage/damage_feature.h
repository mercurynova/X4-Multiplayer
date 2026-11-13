#pragma once
// features/damage: both halves of the damage relay (M15).
//
// Authority: DamageHub collects the hull/shield lines the authority applies, and flush() hands them to the actions shim.
// Client:     the same feature watches the game's own HullDamaged / ShieldDamaged hooks for the ghosts this node holds, asks Lua
//             for the percentages (neither hook carries a percentage), turns the drop into a delta and reports it to the authority
//             as a HitReport at <= 4 Hz. That is the host-authoritative NPC combat path: the ghost takes the hit locally, the
//             authority applies it to the real ship, and replication drives the hull back down.

#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "features/damage/damage_hub.h"
#include "host/feature.h"

namespace x4mp::features {

class DamageFeature final : public host::IFeature {
 public:
  [[nodiscard]] std::string_view name() const noexcept override { return "damage"; }

  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;
  void on_shutdown(host::HostContext& ctx) override;

 private:
  // M15: how many damage lines one frame may carry. Combat is bursty, but a frame that spends its whole time in MD is worse
  // than damage arriving a frame or two late; the rest stays queued.
  static constexpr std::size_t kPerFrame = 64;
  // M15: the HitReport rate the protocol allows (events.fbs: "sent <= 4 Hz").
  static constexpr std::int64_t kProbePeriodUs = 250'000;

  // ---- authority half ----
  std::uint64_t last_log_us_ = 0;
  void flush(host::HostContext& ctx);
  void report(host::HostContext& ctx);

  // ---- client half ----
  void on_game_hit(const host::IPlatform::MdDamage& hit);  // game thread: copy only
  void on_state_text(std::string_view text);              // bridge thread: copy only
  void send_probe(host::HostContext& ctx, std::int64_t now_us);

  std::mutex mutex_;
  std::vector<std::uint64_t> hits_;                            // components hit since the last drain
  std::vector<std::string> texts_;                             // probe answers waiting for the main thread
  std::unordered_map<std::uint32_t, std::uint64_t> in_flight_;  // probe seq -> component
  std::unordered_map<std::uint32_t, damage::Percent> known_;       // ghost net id -> last seen percentage
  std::uint32_t next_seq_ = 0;
  std::uint32_t next_key_ = 0;
  std::int64_t last_probe_us_ = 0;
  std::uint64_t probes_ = 0, states_ = 0, sent_ = 0, ignored_ = 0, baselines_ = 0, hook_off_ = 0;
  bool hook_armed_ = false;
};

}  // namespace x4mp::features
