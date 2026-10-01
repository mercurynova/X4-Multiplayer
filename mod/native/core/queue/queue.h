#pragma once
// core/queue: lock-free queues. Placeholder: a bounded single-producer / single-consumer ring (the shape of
// the net-thread -> frame-thread inbox). Reliable outbox, latest-wins slots and the MPSC md_ring follow in M1-N1.

#include <array>
#include <atomic>
#include <cstddef>
#include <optional>

namespace x4mp::queue {

// Capacity is N - 1 elements. try_push is for the producer thread only, try_pop for the consumer thread only.
template <class T, std::size_t N>
class SpscRing {
  static_assert(N >= 2, "capacity must be at least 1");

 public:
  [[nodiscard]] bool try_push(const T& value) {
    const std::size_t head = head_.load(std::memory_order_relaxed);
    const std::size_t next = (head + 1) % N;
    if (next == tail_.load(std::memory_order_acquire)) return false;  // full
    slots_[head] = value;
    head_.store(next, std::memory_order_release);
    return true;
  }

  [[nodiscard]] std::optional<T> try_pop() {
    const std::size_t tail = tail_.load(std::memory_order_relaxed);
    if (tail == head_.load(std::memory_order_acquire)) return std::nullopt;  // empty
    T value = slots_[tail];
    tail_.store((tail + 1) % N, std::memory_order_release);
    return value;
  }

  [[nodiscard]] static constexpr std::size_t capacity() noexcept { return N - 1; }

 private:
  std::array<T, N> slots_{};
  std::atomic<std::size_t> head_{0};
  std::atomic<std::size_t> tail_{0};
};

}  // namespace x4mp::queue