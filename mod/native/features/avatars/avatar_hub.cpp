#include "features/avatars/avatar_hub.h"

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
    case P::MsgType::EntityDespawn:
      if (!have_services_) break;  // a client's EntityDespawn is about ghosts (M3-10)
      if (auto d = decode_despawn(payload)) push_capped(inputs_.despawns, std::move(*d), kMaxQueue);
      break;
    default: break;
  }
}

void AvatarHub::session_ended() {
  inputs_.rosters.clear();
  inputs_.ships.clear();
  inputs_.states.clear();
  inputs_.despawns.clear();
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
