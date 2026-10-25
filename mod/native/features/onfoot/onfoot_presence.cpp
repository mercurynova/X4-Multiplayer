#include "features/onfoot/onfoot_presence.h"

namespace x4mp::features::onfoot {

bool presence_here(const PresenceRow& row, std::uint32_t local_container) noexcept {
  if (local_container == 0 || row.container == 0) return false;
  if (row.container != local_container) return false;
  return row.mode == Mode::Walking;
}

std::string presence_signature(const std::vector<PresenceRow>& rows, std::uint32_t local_container, Mode local_mode) {
  std::string sig;
  sig.append(std::to_string(local_container));
  sig.push_back('|');
  sig.append(std::to_string(static_cast<int>(local_mode)));
  sig.push_back('|');
  for (const auto& row : rows) {
    sig.append(std::to_string(row.player_id));
    sig.push_back(',');
    sig.push_back(static_cast<char>('0' + static_cast<int>(row.mode) % 10));
    sig.append(std::to_string(row.container));
    sig.push_back(';');
  }
  return sig;
}

}  // namespace x4mp::features::onfoot
