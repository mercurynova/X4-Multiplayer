#include "features/selfship/selfship_hub.h"

namespace x4mp::features::selfship {

SelfShipHub& selfship_hub() noexcept {
  static SelfShipHub hub;
  return hub;
}

}  // namespace x4mp::features::selfship
