#include <array>

#include <catch2/catch_test_macros.hpp>

#include "game/safe_remove.h"

using namespace x4mp::game;

TEST_CASE("safe_remove refuses the player's ids and id 0", "[safe_remove]") {
  const std::array<ComponentId, 2> guard{1001, 1002};
  set_player_guard(guard);
  const auto before = blocked_removal_count();

  CHECK(safe_remove(1001) == RemoveResult::BlockedPlayerGuard);
  CHECK(safe_remove(1002) == RemoveResult::BlockedPlayerGuard);
  CHECK(safe_remove(0) == RemoveResult::BlockedInvalidId);
  CHECK(blocked_removal_count() == before + 3);
  CHECK(is_player_guarded(1001));
  CHECK_FALSE(is_player_guarded(2000));

  // Not guarded: the stub allows it but removes nothing.
  CHECK(safe_remove(2000) == RemoveResult::NotImplemented);
  CHECK(blocked_removal_count() == before + 3);

  set_player_guard({});
  CHECK_FALSE(is_player_guarded(1001));
}