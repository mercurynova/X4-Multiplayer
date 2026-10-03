#include "alloc_counter.h"

#include <atomic>
#include <cstdlib>
#include <new>

namespace {
std::atomic<bool> g_armed{false};
std::atomic<std::uint64_t> g_count{0};

void* counted_alloc(std::size_t n) {
  if (g_armed.load(std::memory_order_relaxed)) g_count.fetch_add(1, std::memory_order_relaxed);
  if (n == 0) n = 1;
  if (void* p = std::malloc(n)) return p;
  throw std::bad_alloc();
}
}  // namespace

void* operator new(std::size_t n) { return counted_alloc(n); }
void* operator new[](std::size_t n) { return counted_alloc(n); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }

namespace x4mp::test {
void alloc_counter_arm() {
  g_count = 0;
  g_armed = true;
}
std::uint64_t alloc_counter_disarm() {
  g_armed = false;
  return g_count.load();
}
}  // namespace x4mp::test
