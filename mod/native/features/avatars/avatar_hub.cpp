#include "features/avatars/avatar_hub.h"

#include "features/ghosts/ghost_hub.h"
#include "features/selfship/selfship_hub.h"
#include "message_ids_generated.h"

namespace x4mp::features::avatars {

namespace P = X4MP::Proto;

namespace {
template <class V, class T>
void push_capped(V& v, T&& item, std::size_t cap) {
  if (v.size() >= cap) v.erase(v.begin());
  v.push_back(std::forward<T>(item));
}
}  // namespace

void AvatarHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t self_id) {
  (void)self_id;
  switch (static_cast<P::MsgType>(type)) {
    case P::MsgType::RosterUpdate:
      if (auto r = decode_roster(payload)) push_capped(inputs_.rosters, std::move(*r), kMaxQueue);
      break;
    case P::MsgType::ServerSettingsUpdate:
      if (auto s = decode_settings(payload)) push_capped(inputs_.settings, std::move(*s), 16);
      break;
    case P::MsgType::PlayerShip:
      if (!have_services_) break;  // only the authority answers
      if (auto s = decode_player_ship(payload)) push_capped(inputs_.ships, std::move(*s), kMaxQueue);
      break;
    case P::MsgType::PlayerState:
      if (!have_services_) break;
      if (auto s = decode_player_state(payload)) push_capped(inputs_.states, std::move(*s), kMaxQueue);
      break;
    case P::MsgType::EntitySpawn:
      if (have_services_) break;  // the authority sends them, it does not take over
      if (auto a = decode_avatar_spawns(payload); a && !a->empty()) {
        for (auto& info : *a) push_capped(inputs_.avatar_spawns, std::move(info), kMaxQueue);
      }
      break;
    case P::MsgType::EntityDespawn:
      if (!have_services_) break;  // a client's EntityDespawn is about ghosts (M3-10)
      if (auto d = decode_despawn(payload)) push_capped(inputs_.despawns, std::move(*d), kMaxQueue);
      break;
    default: break;
  }
}

void AvatarHub::set_client_link(ClientLink link) {
  client_ = std::move(link);
  selfship::selfship_hub().set_state_hold(true);  // M3-12: no PlayerState until the avatar is taken over
  ghosts::ghost_hub().set_spawn_hold(true);       // and no ghosts until the copies are removed (the takeover lifts it at Done)
}

void AvatarHub::clear_client_link() {
  if (!client_.send_control && !client_.player_id) return;
  client_ = {};
  takeover_ = {};
  selfship::selfship_hub().set_state_hold(false);
  ghosts::ghost_hub().set_spawn_hold(false);
  selfship::selfship_hub().set_own_net_id(0);
}

void AvatarHub::session_ended() {
  inputs_.avatar_spawns.clear();
  inputs_.rosters.clear();
  inputs_.ships.clear();
  inputs_.states.clear();
  inputs_.despawns.clear();
  inputs_.lineage.clear();
  inputs_.session_ended = true;
}

HubInputs AvatarHub::take_inputs() {
  HubInputs out = std::move(inputs_);
  inputs_ = HubInputs{};
  return out;
}

void AvatarHub::reset() { *this = AvatarHub{}; }

AvatarHub& avatar_hub() noexcept {
  static AvatarHub hub;
  return hub;
}

}  // namespace x4mp::features::avatars
