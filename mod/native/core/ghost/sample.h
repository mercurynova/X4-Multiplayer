#pragma once
// core/ghost: the per-entity sample (one pose of a remote ship at one server time) and the StateFlags bits the ghost code reads.
// Pure C++, no X4 SDK.

#include <cstdint>

#include "core/ghost/math.h"

namespace x4mp::ghost {

// StateFlags (protocol/schema/common.fbs, bit_flags: bit n = 1 << n). Bits 0..9 exist today; kHidden is schema delta D1 of
// docs/m3-plan.md 4.13 ("append Hidden"), the next free bit. If the generated schema ever disagrees, fix it HERE only.
inline constexpr std::uint16_t kVelCoarse = 1u << 0;
inline constexpr std::uint16_t kDocked = 1u << 1;
inline constexpr std::uint16_t kInHighway = 1u << 2;
inline constexpr std::uint16_t kTravelDrive = 1u << 3;
inline constexpr std::uint16_t kBoost = 1u << 4;
inline constexpr std::uint16_t kTeleport = 1u << 5;
inline constexpr std::uint16_t kKeyframe = 1u << 6;
inline constexpr std::uint16_t kPlayerControlled = 1u << 7;
inline constexpr std::uint16_t kDying = 1u << 8;
inline constexpr std::uint16_t kBoardingExempt = 1u << 9;
inline constexpr std::uint16_t kHidden = 1u << 10;  // D1

// One sample. Server time in microseconds; position sector-relative metres; velocity m/s; angles radians (math.h convention).
struct Sample {
  std::int64_t t_us = 0;
  std::uint16_t sector = 0;
  std::uint16_t flags = 0;
  Vec3 pos{};
  Vec3 vel{};
  Euler rot{};
  std::uint8_t hull = 255;
  std::uint8_t shield = 255;
};

}  // namespace x4mp::ghost
