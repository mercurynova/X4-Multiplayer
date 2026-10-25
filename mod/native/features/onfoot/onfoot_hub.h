#pragma once
// features/onfoot: the session-scoped half of the on-foot presence (M3b). Mirrors features/selfship/selfship_hub.h.
//
//   join_feature.cpp installs the link while the node is in game and hands every frame to on_frame_message() (one hook line each, like the
//   other hubs). This hub owns: the last room context MD pushed, the remote avatars the relay reported, the roster, and the counters the
//   status JSON shows.

#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include "features/onfoot/onfoot_types.h"

namespace x4mp::features::onfoot {

struct OnFootLink {
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_realtime;
  // Control lane: PlayerAppearance (0x0303) must go there (MessagePolicy: Control only), while the states ride the Realtime lane.
  std::function<bool(std::uint16_t type, std::span<const std::uint8_t> payload)> send_control;
  std::function<bool(std::int64_t& server_now_us)> server_now;
};

struct PresenceRow {
  std::uint16_t player_id = 0;
  Mode mode = Mode::None;
  std::uint32_t container = 0;
  std::uint32_t outer = 0;
  std::uint8_t roomtype = 21;
  std::string room_name;
};

// One PlayerInteraction (0x0305, Control lane) that another player sent to this one. The server stamps from_player, so the hub only
// keeps what the receiver needs: who waved, and what kind. W5 ships Wave; the other kinds are on the wire for M5b.
struct Interaction {
  std::uint16_t from = 0;
  std::uint8_t kind = 0;   // P::InteractionKind: 0 wave, 1 emote, 2 poke, 3 invite team, 4 open trade
  std::uint8_t emote = 0;  // valid with kind 1
  std::uint32_t arg = 0;   // kind-specific
};

class OnFootHub {
 public:
  // --- link (installed by the join feature) ---
  void set_link(OnFootLink link) { link_ = std::move(link); }
  void clear_link() { link_ = {}; }
  [[nodiscard]] bool linked() const noexcept { return static_cast<bool>(link_.send_realtime); }
  bool send_realtime(std::uint16_t type, std::span<const std::uint8_t> payload) const {
    return link_.send_realtime && link_.send_realtime(type, payload);
  }
  bool send_control(std::uint16_t type, std::span<const std::uint8_t> payload) const {
    return link_.send_control && link_.send_control(type, payload);
  }
  [[nodiscard]] bool server_now(std::int64_t& out) const { return link_.server_now && link_.server_now(out); }

  // --- local room context, pushed by MD over the bridge verb ---
  void set_room_context(RoomContext ctx) noexcept { room_ = ctx; }
  [[nodiscard]] const RoomContext& room() const noexcept { return room_; }

  void set_own_player_id(std::uint16_t id) noexcept { own_player_id_ = id; }
  [[nodiscard]] std::uint16_t own_player_id() const noexcept { return own_player_id_; }

  // --- remote players ---
  void on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id);
  [[nodiscard]] const std::map<std::uint16_t, RemoteAvatar>& remotes() const noexcept { return remotes_; }
  [[nodiscard]] const std::vector<PresenceRow>& roster() const noexcept { return roster_; }
  [[nodiscard]] bool roster_full() const noexcept { return roster_full_; }
  [[nodiscard]] const std::map<std::uint16_t, Appearance>& appearances() const noexcept { return appearances_; }
  [[nodiscard]] const Appearance* appearance(std::uint16_t player_id) const noexcept {
    const auto it = appearances_.find(player_id);
    return it == appearances_.end() ? nullptr : &it->second;
  }
  void forget(std::uint16_t player_id) { remotes_.erase(player_id); }

  // --- interactions (W5: a wave; the other kinds are M5b) ---
  // Everything received since the last call. The feature raises one Lua event per frame, so a burst cannot flood the UI.
  std::vector<Interaction> take_interactions();
  // A wave or emote to another player. Control lane: PlayerInteraction is not a realtime message (MessagePolicy).
  bool send_interaction(std::uint16_t to, std::uint8_t kind, std::uint8_t emote = 0, std::uint32_t arg = 0) const;

  void session_ended();

  struct Counters {
    std::uint32_t states_in = 0;
    std::uint32_t states_out = 0;
    std::uint32_t send_failed = 0;
    std::uint32_t bad_frames = 0;
    std::uint32_t rosters = 0;
    std::uint32_t appearances = 0;
    std::uint32_t interactions = 0;
  };
  [[nodiscard]] const Counters& counters() const noexcept { return counters_; }

  void reset() { *this = OnFootHub{}; }

 private:
  OnFootLink link_;
  RoomContext room_;
  std::uint16_t own_player_id_ = 0;
  std::map<std::uint16_t, RemoteAvatar> remotes_;
  std::map<std::uint16_t, Appearance> appearances_;
  std::vector<PresenceRow> roster_;
  bool roster_full_ = false;
  std::vector<Interaction> interactions_;
  Counters counters_;
};

[[nodiscard]] OnFootHub& onfoot_hub() noexcept;

}  // namespace x4mp::features::onfoot
