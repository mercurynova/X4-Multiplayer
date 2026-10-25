#include "features/onfoot/onfoot_hub.h"

#include <algorithm>

#include <flatbuffers/flatbuffers.h>

#include "message_ids_generated.h"
#include "world_generated.h"

namespace x4mp::features::onfoot {

namespace P = X4MP::Proto;

OnFootHub& onfoot_hub() noexcept {
  static OnFootHub hub;
  return hub;
}

void OnFootHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id) {
  if (payload.empty()) {
    ++counters_.bad_frames;
    return;
  }
  const auto* data = payload.data();
  switch (static_cast<P::MsgType>(type)) {
    case P::MsgType::OnFootState: {
      const auto* state = flatbuffers::GetRoot<P::OnFootState>(data);
      if (state == nullptr || state->player_id() == self_id) return;  // the relay never echoes, but never trust it
      RemoteAvatar avatar;
      avatar.mode = static_cast<Mode>(state->mode());
      avatar.container = state->container_net_id();
      avatar.outer = state->outer_container_net_id();
      avatar.cx = static_cast<float>(static_cast<double>(state->cx()) / 64.0);
      avatar.cy = static_cast<float>(static_cast<double>(state->cy()) / 64.0);
      avatar.cz = static_cast<float>(static_cast<double>(state->cz()) / 64.0);
      avatar.yaw = static_cast<float>(static_cast<double>(state->yaw()) * 3.14159265358979323846 / 32768.0);
      avatar.pitch = static_cast<float>(static_cast<double>(state->look_pitch()) / 255.0 * 3.14159265358979323846 - 1.5707963267948966);
      avatar.anim = static_cast<Anim>(state->anim());
      avatar.emote = state->emote_id();
      avatar.flags = state->flags();
      avatar.seq = state->seq();
      avatar.at_us = static_cast<std::int64_t>(state->sample_time_us());
      if (avatar.mode == Mode::None) {
        remotes_.erase(state->player_id());  // the leave state
      } else {
        remotes_[state->player_id()] = avatar;
      }
      ++counters_.states_in;
      break;
    }
    case P::MsgType::PlayerAppearance: {
      ++counters_.appearances;  // W4: the look of a remote avatar (race + preset, ADR-051)
      break;
    }
    case P::MsgType::PresenceRoster: {
      const auto* msg = flatbuffers::GetRoot<P::PresenceRoster>(data);
      if (msg == nullptr) {
        ++counters_.bad_frames;
        return;
      }
      roster_.clear();
      if (const auto* entries = msg->entries()) {
        roster_.reserve(entries->size());
        for (const auto* e : *entries) {
          if (e == nullptr) continue;
          PresenceRow row;
          row.player_id = e->player_id();
          row.mode = static_cast<Mode>(e->mode());
          row.container = e->container_net_id();
          row.outer = e->outer_container_net_id();
          row.roomtype = e->roomtype();
          if (const auto* name = e->room_name()) row.room_name = name->str();
          roster_.push_back(std::move(row));
        }
      }
      if (const auto* removed = msg->removed()) {
        for (const auto id : *removed) {
          remotes_.erase(id);
          roster_.erase(std::remove_if(roster_.begin(), roster_.end(), [id](const PresenceRow& r) { return r.player_id == id; }),
                        roster_.end());
        }
      }
      roster_full_ = msg->full();
      ++counters_.rosters;
      break;
    }
    case P::MsgType::PlayerInteraction: {
      ++counters_.interactions;  // W4: wave / emote / poke towards this player
      break;
    }
    default:
      break;
  }
}

void OnFootHub::session_ended() {
  const auto counters = counters_;
  const auto link = link_;
  *this = OnFootHub{};
  counters_ = counters;  // the counters are for the whole run, like the other hubs
  link_ = link;          // the join feature clears the link itself; keep it until then so a late frame still goes out
}

}  // namespace x4mp::features::onfoot
