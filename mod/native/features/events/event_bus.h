#pragma once
// features/events/event_bus: the node's inbox of things that happened, deduped and rate-limited (M5-02; docs/m5-plan.md W1).
//
// Three jobs, and nothing else:
//
//   1. DEDUPE. A hook and its fallback both see the same kill, and MD may fire twice for one event. Every event is hashed to
//      (kind, subject, second subject, amount, second) and the last kDedupeSlots hashes are remembered, so the second copy of
//      the same occurrence is dropped and counted instead of being sent. The window is deliberately one second wide: two
//      real trades of the same ware on the same station in the same second carry different amounts, and two identical ones
//      are indistinguishable anyway.
//   2. BOUND. A frame never sends more than its budget, and the queue has a hard cap. An event that cannot be queued is
//      dropped and counted, never silently lost - the counter is in the `hooks=` block of the 30 s line.
//   3. COUNT. Per-kind totals, plus parsed / bad / deduped / overflow, are what `md_hook` state reports (M5-03).
//
// Single-threaded by contract: the feature parses and pushes on the frame thread only. The platform's event thread writes
// into the inbox, not into the bus.

#include <array>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <string>
#include <utility>
#include <vector>

#include "features/events/event_types.h"

namespace x4mp::features::events {

struct BusStats {
  std::array<std::uint32_t, kEventKindCount> by_kind{};
  std::uint32_t parsed = 0;    // lines that reached push()
  std::uint32_t bad = 0;       // lines the parser refused
  std::uint32_t deduped = 0;   // dropped as a repeat of something inside the window
  std::uint32_t overflow = 0;  // dropped because the queue was full
  std::uint32_t taken = 0;     // handed to a sink (or to take())
};

class EventBus {
 public:
  static constexpr std::size_t kDedupeSlots = 256;
  static constexpr std::size_t kRingSlots = 64;
  static constexpr std::size_t kMaxQueued = 1024;
  static constexpr std::size_t kDefaultPerFrame = 32;

  // A parsed event. False means "not queued": either a repeat, or the queue is full. `bad` is counted by the caller, which
  // is the only one that sees the text.
  bool push(MdEvent event) {
    ++stats_.parsed;
    const std::uint64_t key = fingerprint(event);
    if (seen(key)) {
      ++stats_.deduped;
      return false;
    }
    remember(key);
    if (queue_.size() >= kMaxQueued) {
      ++stats_.overflow;
      return false;
    }
    ++stats_.by_kind[static_cast<std::size_t>(event.kind)];
    remember_event(std::move(event));
    return true;
  }

  // Up to `budget` events, oldest first. The caller sends them and reports what it did.
  [[nodiscard]] std::vector<MdEvent> take(std::size_t budget = kDefaultPerFrame) {
    std::vector<MdEvent> out;
    if (budget == 0) return out;
    out.reserve(budget < queue_.size() ? budget : queue_.size());
    while (!queue_.empty() && out.size() < budget) {
      out.push_back(std::move(queue_.front()));
      queue_.pop_front();
      ++stats_.taken;
    }
    return out;
  }

  [[nodiscard]] std::size_t queued() const noexcept { return queue_.size(); }
  [[nodiscard]] const BusStats& stats() const noexcept { return stats_; }

  // The newest kRingSlots events, newest last: `[events] dump` prints these so a tester can see what the node actually saw.
  [[nodiscard]] const std::deque<MdEvent>& ring() const noexcept { return ring_; }

  void reset() {
    queue_.clear();
    ring_.clear();
    stats_ = BusStats{};
    seen_.fill(0);
    seen_cursor_ = 0;
    seen_count_ = 0;
  }

  // FNV-1a over the fields that make two occurrences the same one. Public for the test that pins the key.
  [[nodiscard]] static std::uint64_t fingerprint(const MdEvent& event) noexcept {
    std::uint64_t hash = 1469598103934665603ull;
    const auto mix = [&hash](std::uint64_t value) noexcept {
      for (int i = 0; i < 8; ++i) {
        hash ^= (value >> (i * 8)) & 0xFFull;
        hash *= 1099511628211ull;
      }
    };
    mix(static_cast<std::uint64_t>(event.kind));
    mix(event.component);
    mix(event.other);
    mix(event.third);
    mix(static_cast<std::uint64_t>(event.amount));
    mix(static_cast<std::uint64_t>(event.at_ms / 1000));  // the one-second window
    return hash;
  }

 private:
  [[nodiscard]] bool seen(std::uint64_t key) const noexcept {
    for (std::size_t i = 0; i < seen_count_; ++i) {
      if (seen_[i] == key) return true;
    }
    return false;
  }

  void remember(std::uint64_t key) noexcept {
    seen_[seen_cursor_] = key;
    seen_cursor_ = (seen_cursor_ + 1) % kDedupeSlots;
    if (seen_count_ < kDedupeSlots) ++seen_count_;
  }

  void remember_event(MdEvent event) {
    queue_.push_back(event);
    if (ring_.size() >= kRingSlots) ring_.pop_front();
    ring_.push_back(std::move(event));
  }

  std::array<std::uint64_t, kDedupeSlots> seen_{};
  std::size_t seen_cursor_ = 0;
  std::size_t seen_count_ = 0;
  std::deque<MdEvent> queue_;
  std::deque<MdEvent> ring_;
  BusStats stats_{};
};

}  // namespace x4mp::features::events
