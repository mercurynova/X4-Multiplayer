#pragma once
// Counts global operator new calls while armed (x4mp_ghost_tests replaces operator new in alloc_counter.cpp).
#include <cstdint>

namespace x4mp::test {
void alloc_counter_arm();
std::uint64_t alloc_counter_disarm();  // returns the number of allocations since arm()
}  // namespace x4mp::test
