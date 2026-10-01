#pragma once
// core/queue: the cross-thread hand-off primitives (docs/mod-design.md 2.3). Pure C++, header-only.
//
//   SpscRing<T,N>        net thread -> main thread inbox. Wait-free, lock-free, fixed capacity N-1.
//   ReliableOutbox<T>    main -> net reliable messages. Mutex + swap (see below), byte cap, drop-oldest hooks.
//   LatestWinsSlot<T>    trivially-copyable state, single writer, many readers, seqlock. Never torn.
//   LatestWinsBox<T>     the same idea for owning payloads (byte buffers): mutex + optional.
//   MpscRing<T>          many producers -> one consumer, bounded, lock-free producers, never blocks.
//   MdRing / MdEvent     MpscRing of PODs for MD callbacks that fire on arbitrary game worker threads.
//
// Design choices (why lock-free where, why a mutex where):
//   * SpscRing and MpscRing are lock-free because a producer here may be a game worker thread (MD callback)
//     or the main thread, and neither may ever block on a lock the other side could hold while descheduled.
//   * ReliableOutbox uses a std::mutex on purpose: it is unbounded (owning buffers of arbitrary size), the main
//     thread takes it once per frame to push a batch and the net thread takes it once per wake to swap the whole
//     batch out, so each hold is a few pointer moves. The lock is never held across I/O or while destroying
//     payloads. mod-design 2.3 calls for exactly this ("mutex plus swap is enough for outbox_*").
//   * Allocation: SpscRing, MpscRing, LatestWinsSlot never allocate after construction. ReliableOutbox and
//     LatestWinsBox allocate (deque nodes / the payload itself) and must not be used from MD worker threads.

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <thread>
#include <type_traits>
#include <utility>
#include <vector>

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4324)  // padding due to alignas(cache line): intentional, avoids false sharing
#endif

namespace x4mp::queue {

inline constexpr std::size_t kCacheLine = 64;

// ---------------------------------------------------------------------------------------------
// SpscRing: bounded single-producer / single-consumer ring. Capacity is N - 1 elements.
// try_push is for the producer thread only, try_pop for the consumer thread only.
// Moves are used when given an rvalue / when popping, so T may own memory (decoded messages).
// ---------------------------------------------------------------------------------------------
template <class T, std::size_t N>
class SpscRing {
  static_assert(N >= 2, "capacity must be at least 1");

 public:
  [[nodiscard]] bool try_push(const T& value) { return emplace_impl(value); }
  [[nodiscard]] bool try_push(T&& value) { return emplace_impl(std::move(value)); }

  [[nodiscard]] std::optional<T> try_pop() {
    const std::size_t tail = tail_.load(std::memory_order_relaxed);
    if (tail == head_.load(std::memory_order_acquire)) return std::nullopt;  // empty
    std::optional<T> value(std::move(slots_[tail]));
    tail_.store((tail + 1) % N, std::memory_order_release);
    return value;
  }

  // Approximate (racy) fill level, for diagnostics only.
  [[nodiscard]] std::size_t size_approx() const noexcept {
    const std::size_t head = head_.load(std::memory_order_acquire);
    const std::size_t tail = tail_.load(std::memory_order_acquire);
    return (head + N - tail) % N;
  }

  [[nodiscard]] static constexpr std::size_t capacity() noexcept { return N - 1; }

 private:
  template <class U>
  bool emplace_impl(U&& value) {
    const std::size_t head = head_.load(std::memory_order_relaxed);
    const std::size_t next = (head + 1) % N;
    if (next == tail_.load(std::memory_order_acquire)) return false;  // full
    slots_[head] = std::forward<U>(value);
    head_.store(next, std::memory_order_release);
    return true;
  }

  std::array<T, N> slots_{};
  alignas(kCacheLine) std::atomic<std::size_t> head_{0};
  alignas(kCacheLine) std::atomic<std::size_t> tail_{0};
};

// ---------------------------------------------------------------------------------------------
// MpscRing: bounded multi-producer / single-consumer ring (Vyukov bounded queue, per-slot sequence number).
//   * try_push may be called from any number of threads concurrently. Lock-free: one CAS to claim a slot, then a
//     plain write and a release store. It never blocks and never allocates; when full it returns false and bumps
//     dropped() so MD workers can never stall the game.
//   * try_pop is for ONE consumer thread only.
//   * A producer descheduled between claiming a slot and publishing it hides later items from the consumer until
//     it resumes (the consumer sees "empty"); nothing is lost or reordered within one producer.
// For MD events use trivially copyable PODs.
// ---------------------------------------------------------------------------------------------
template <class T>
class MpscRing {
 public:
  // capacity is rounded up to a power of two (>= 2). All memory is allocated here, once.
  explicit MpscRing(std::size_t capacity)
      : mask_(std::bit_ceil(std::max<std::size_t>(capacity, 2)) - 1), slots_(std::make_unique<Slot[]>(mask_ + 1)) {
    for (std::size_t i = 0; i <= mask_; ++i) slots_[i].seq.store(i, std::memory_order_relaxed);
  }
  MpscRing(const MpscRing&) = delete;
  MpscRing& operator=(const MpscRing&) = delete;

  [[nodiscard]] bool try_push(const T& value) {
    std::size_t pos = enqueue_pos_.load(std::memory_order_relaxed);
    for (;;) {
      Slot& slot = slots_[pos & mask_];
      const std::size_t seq = slot.seq.load(std::memory_order_acquire);
      const auto diff = static_cast<std::intptr_t>(seq) - static_cast<std::intptr_t>(pos);
      if (diff == 0) {
        if (enqueue_pos_.compare_exchange_weak(pos, pos + 1, std::memory_order_relaxed)) {
          slot.value = value;
          slot.seq.store(pos + 1, std::memory_order_release);
          return true;
        }
      } else if (diff < 0) {
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return false;  // full
      } else {
        pos = enqueue_pos_.load(std::memory_order_relaxed);
      }
    }
  }

  [[nodiscard]] std::optional<T> try_pop() {
    Slot& slot = slots_[dequeue_pos_ & mask_];
    const std::size_t seq = slot.seq.load(std::memory_order_acquire);
    if (static_cast<std::intptr_t>(seq) - static_cast<std::intptr_t>(dequeue_pos_ + 1) != 0) return std::nullopt;
    std::optional<T> out(std::move(slot.value));
    slot.seq.store(dequeue_pos_ + mask_ + 1, std::memory_order_release);
    ++dequeue_pos_;
    return out;
  }

  // Consumer thread only: pops up to max_items, calling fn(T&&) for each. Returns how many were popped.
  template <class F>
  std::size_t drain(F&& fn, std::size_t max_items = static_cast<std::size_t>(-1)) {
    std::size_t n = 0;
    while (n < max_items) {
      auto item = try_pop();
      if (!item) break;
      fn(std::move(*item));
      ++n;
    }
    return n;
  }

  [[nodiscard]] std::size_t capacity() const noexcept { return mask_ + 1; }
  // Number of pushes rejected because the ring was full (the "md_dropped" counter).
  [[nodiscard]] std::uint64_t dropped() const noexcept { return dropped_.load(std::memory_order_relaxed); }

 private:
  struct Slot {
    std::atomic<std::size_t> seq{0};
    T value{};
  };
  const std::size_t mask_;
  std::unique_ptr<Slot[]> slots_;
  alignas(kCacheLine) std::atomic<std::size_t> enqueue_pos_{0};
  alignas(kCacheLine) std::size_t dequeue_pos_{0};  // consumer-only, no atomics needed
  alignas(kCacheLine) std::atomic<std::uint64_t> dropped_{0};
};

// MD callback capture (mod-design 2.3): POD only, copied by value, no game calls, no allocation.
struct MdEvent {
  std::uint32_t type = 0;
  std::uint64_t a = 0;
  std::uint64_t b = 0;
  std::uint64_t c = 0;
  double t = 0.0;
};
static_assert(std::is_trivially_copyable_v<MdEvent>);
using MdRing = MpscRing<MdEvent>;
inline constexpr std::size_t kDefaultMdRingCapacity = 4096;

// ---------------------------------------------------------------------------------------------
// LatestWinsSlot: seqlock for trivially copyable state. ONE writer thread calls publish(); any number of
// reader threads call try_read()/read(). Readers never observe a torn value: the payload is stored as relaxed
// atomic words and validated by the sequence number (odd = write in progress). Writers are wait-free; readers
// retry while a write is in flight. Never allocates.
// ---------------------------------------------------------------------------------------------
template <class T>
class LatestWinsSlot {
  static_assert(std::is_trivially_copyable_v<T> && std::is_default_constructible_v<T>);
  static constexpr std::size_t kWords = (sizeof(T) + 7) / 8;

 public:
  void publish(const T& value) noexcept {
    std::uint64_t tmp[kWords] = {};
    std::memcpy(tmp, &value, sizeof(T));
    const std::uint32_t s = seq_.load(std::memory_order_relaxed);
    seq_.store(s + 1, std::memory_order_relaxed);  // odd: write in progress
    std::atomic_thread_fence(std::memory_order_release);
    for (std::size_t i = 0; i < kWords; ++i) words_[i].store(tmp[i], std::memory_order_relaxed);
    seq_.store(s + 2, std::memory_order_release);
  }

  // Single attempt. Returns false if a write was in flight / raced, or nothing was ever published.
  // On success `version` (if given) is the number of publishes so far.
  [[nodiscard]] bool try_read(T& out, std::uint32_t* version = nullptr) const noexcept {
    const std::uint32_t s1 = seq_.load(std::memory_order_acquire);
    if ((s1 & 1U) != 0 || s1 == 0) return false;
    std::uint64_t tmp[kWords];
    for (std::size_t i = 0; i < kWords; ++i) tmp[i] = words_[i].load(std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_acquire);
    if (seq_.load(std::memory_order_relaxed) != s1) return false;
    std::memcpy(&out, tmp, sizeof(T));
    if (version) *version = s1 / 2;
    return true;
  }

  // Retries until a consistent snapshot is obtained. nullopt only if nothing has been published yet.
  [[nodiscard]] std::optional<T> read(std::uint32_t* version = nullptr) const noexcept {
    T out{};
    for (unsigned spins = 0;; ++spins) {
      if (try_read(out, version)) return out;
      if (seq_.load(std::memory_order_relaxed) == 0) return std::nullopt;
      if (spins > 64) std::this_thread::yield();
    }
  }

  // For the net thread: returns the state only if it was published after `last_version` (updated on success).
  [[nodiscard]] std::optional<T> read_if_newer(std::uint32_t& last_version) const noexcept {
    if (version() == last_version) return std::nullopt;
    std::uint32_t v = 0;
    auto out = read(&v);
    if (out) last_version = v;
    return out;
  }

  [[nodiscard]] std::uint32_t version() const noexcept { return seq_.load(std::memory_order_acquire) / 2; }

 private:
  std::atomic<std::uint32_t> seq_{0};
  std::atomic<std::uint64_t> words_[kWords]{};
};

// ---------------------------------------------------------------------------------------------
// LatestWinsBox: latest-wins for owning payloads (e.g. an encoded state batch for one channel). put() replaces
// any unsent value; take() hands the newest one over. A short mutex hold (pointer moves); the replaced payload is
// destroyed outside the lock. Bounded memory regardless of network stall: at most one payload per box.
// ---------------------------------------------------------------------------------------------
template <class T>
class LatestWinsBox {
 public:
  // Returns true if an unsent value was overwritten (a "stale drop" for stats).
  bool put(T value) {
    std::optional<T> old;
    bool replaced = false;
    {
      std::lock_guard lock(mutex_);
      old = std::move(value_);
      replaced = old.has_value();
      value_ = std::move(value);
      if (replaced) ++overwritten_;
    }
    return replaced;  // `old` destroyed here, outside the lock
  }
  [[nodiscard]] std::optional<T> take() {
    std::lock_guard lock(mutex_);
    std::optional<T> out = std::move(value_);
    value_.reset();
    return out;
  }
  [[nodiscard]] std::uint64_t overwritten() const {
    std::lock_guard lock(mutex_);
    return overwritten_;
  }

 private:
  mutable std::mutex mutex_;
  std::optional<T> value_;
  std::uint64_t overwritten_ = 0;
};

// ---------------------------------------------------------------------------------------------
// ReliableOutbox: main -> net. Unbounded in item count, bounded in bytes by a policy:
//   * push(item, bytes) appends. If total bytes would exceed byte_cap, the oldest items for which
//     Options::droppable returns true are evicted (oldest first) until the new item fits.
//   * Items that are not droppable are never evicted (reliable events, chat, acks). If the cap still cannot be
//     met the item is accepted anyway and PushResult::over_cap is set, so the caller (net thread / session)
//     can decide to drop the connection rather than lose reliable data silently.
//   * Options::on_drop (optional) is called for every evicted item, outside the lock, on the pushing thread.
// With no `droppable` hook, every item is droppable (plain drop-oldest). take_all() swaps the whole batch out
// in one short lock hold (the consumer is the net thread).
// ---------------------------------------------------------------------------------------------
template <class T>
class ReliableOutbox {
 public:
  struct Options {
    std::size_t byte_cap = 8u * 1024u * 1024u;
    std::function<bool(const T&)> droppable;              // empty => everything droppable
    std::function<void(T&&, std::size_t bytes)> on_drop;  // empty => silently discarded
  };
  struct PushResult {
    bool was_empty = false;  // queue was empty before this push: the caller should signal the net thread
    bool over_cap = false;   // cap exceeded and nothing (more) could be evicted
    std::size_t dropped_items = 0;
    std::size_t dropped_bytes = 0;
  };

  ReliableOutbox() : ReliableOutbox(Options{}) {}
  explicit ReliableOutbox(Options options) : options_(std::move(options)) {}

  PushResult push(T item, std::size_t bytes) {
    PushResult result;
    std::vector<std::pair<T, std::size_t>> evicted;
    {
      std::lock_guard lock(mutex_);
      result.was_empty = items_.empty();
      if (bytes_ + bytes > options_.byte_cap) {
        for (auto it = items_.begin(); it != items_.end() && bytes_ + bytes > options_.byte_cap;) {
          if (!options_.droppable || options_.droppable(it->item)) {
            bytes_ -= it->bytes;
            evicted.emplace_back(std::move(it->item), it->bytes);
            it = items_.erase(it);
          } else {
            ++it;
          }
        }
        result.over_cap = bytes_ + bytes > options_.byte_cap;
      }
      items_.push_back(Entry{std::move(item), bytes});
      bytes_ += bytes;
      for (const auto& e : evicted) result.dropped_bytes += e.second;
      result.dropped_items = evicted.size();
      dropped_items_ += result.dropped_items;
      dropped_bytes_ += result.dropped_bytes;
    }
    if (options_.on_drop) {
      for (auto& e : evicted) options_.on_drop(std::move(e.first), e.second);
    }
    return result;
  }

  // Moves every queued item (oldest first) into `out` (appending) and returns the number moved.
  std::size_t take_all(std::vector<T>& out) {
    std::deque<Entry> batch;
    {
      std::lock_guard lock(mutex_);
      batch.swap(items_);
      bytes_ = 0;
    }
    for (auto& e : batch) out.push_back(std::move(e.item));
    return batch.size();
  }

  [[nodiscard]] std::size_t bytes() const {
    std::lock_guard lock(mutex_);
    return bytes_;
  }
  [[nodiscard]] std::size_t size() const {
    std::lock_guard lock(mutex_);
    return items_.size();
  }
  [[nodiscard]] std::uint64_t dropped_items() const {
    std::lock_guard lock(mutex_);
    return dropped_items_;
  }
  [[nodiscard]] std::uint64_t dropped_bytes() const {
    std::lock_guard lock(mutex_);
    return dropped_bytes_;
  }

 private:
  struct Entry {
    T item;
    std::size_t bytes;
  };
  Options options_;
  mutable std::mutex mutex_;
  std::deque<Entry> items_;
  std::size_t bytes_ = 0;
  std::uint64_t dropped_items_ = 0;
  std::uint64_t dropped_bytes_ = 0;
};

}  // namespace x4mp::queue

#ifdef _MSC_VER
#pragma warning(pop)
#endif
