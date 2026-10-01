#include <atomic>
#include <cstdint>
#include <memory>
#include <numeric>
#include <string>
#include <thread>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "core/queue/queue.h"

using namespace x4mp::queue;

TEST_CASE("spsc ring basic push/pop and capacity", "[queue]") {
  SpscRing<int, 4> q;
  CHECK(SpscRing<int, 4>::capacity() == 3);
  CHECK_FALSE(q.try_pop().has_value());
  CHECK(q.try_push(1));
  CHECK(q.try_push(2));
  CHECK(q.try_push(3));
  CHECK_FALSE(q.try_push(4));  // full
  CHECK(q.try_pop() == 1);
  CHECK(q.try_push(4));        // wrapped
  CHECK(q.try_pop() == 2);
  CHECK(q.try_pop() == 3);
  CHECK(q.try_pop() == 4);
  CHECK_FALSE(q.try_pop().has_value());
}

TEST_CASE("spsc ring preserves order across threads", "[queue]") {
  constexpr std::uint32_t kCount = 200000;
  SpscRing<std::uint32_t, 64> q;
  std::atomic<bool> in_order{true};
  std::thread consumer([&] {
    std::uint32_t expected = 0;
    while (expected < kCount) {
      if (auto v = q.try_pop()) {
        if (*v != expected) in_order = false;
        ++expected;
      }
    }
  });
  for (std::uint32_t i = 0; i < kCount;) {
    if (q.try_push(i)) ++i;
  }
  consumer.join();
  CHECK(in_order.load());
}

TEST_CASE("spsc ring moves owning payloads and checksums them", "[queue]") {
  constexpr std::uint32_t kCount = 50000;
  SpscRing<std::unique_ptr<std::string>, 32> q;
  std::atomic<std::uint64_t> sum{0};
  std::thread consumer([&] {
    std::uint32_t got = 0;
    while (got < kCount) {
      if (auto v = q.try_pop()) {
        sum += std::stoull(**v);
        ++got;
      }
    }
  });
  std::uint64_t expected = 0;
  for (std::uint32_t i = 0; i < kCount;) {
    auto p = std::make_unique<std::string>(std::to_string(i));
    if (q.try_push(std::move(p))) {
      expected += i;
      ++i;
    }
  }
  consumer.join();
  CHECK(sum.load() == expected);
}

TEST_CASE("mpsc ring: capacity rounds up, full rejects and counts drops", "[queue]") {
  MpscRing<int> q(5);
  CHECK(q.capacity() == 8);
  for (int i = 0; i < 8; ++i) CHECK(q.try_push(i));
  CHECK_FALSE(q.try_push(99));
  CHECK_FALSE(q.try_push(99));
  CHECK(q.dropped() == 2);
  for (int i = 0; i < 8; ++i) CHECK(q.try_pop() == i);
  CHECK_FALSE(q.try_pop().has_value());
  CHECK(q.try_push(7));  // reusable after wrap
  CHECK(q.try_pop() == 7);
}

TEST_CASE("mpsc ring under contention: every item once, per-producer order kept", "[queue][stress]") {
  constexpr int kProducers = 4;
  constexpr std::uint64_t kPerProducer = 60000;
  MpscRing<std::uint64_t> q(256);  // small ring => constant wrap-around and full conditions
  std::vector<std::thread> producers;
  for (int p = 0; p < kProducers; ++p) {
    producers.emplace_back([&q, p] {
      for (std::uint64_t i = 0; i < kPerProducer;) {
        if (q.try_push((static_cast<std::uint64_t>(p) << 32) | i)) ++i;
      }
    });
  }
  std::vector<std::uint64_t> next(kProducers, 0);
  std::uint64_t total = 0, checksum = 0;
  bool ordered = true;
  while (total < kProducers * kPerProducer) {
    if (auto v = q.try_pop()) {
      const auto p = static_cast<std::size_t>(*v >> 32);
      const std::uint64_t i = *v & 0xFFFFFFFFu;
      if (i != next[p]) ordered = false;
      next[p] = i + 1;
      checksum += i;
      ++total;
    }
  }
  for (auto& t : producers) t.join();
  CHECK(ordered);
  CHECK(total == kProducers * kPerProducer);
  CHECK(checksum == kProducers * (kPerProducer * (kPerProducer - 1) / 2));
  CHECK_FALSE(q.try_pop().has_value());
}

TEST_CASE("md ring never blocks producers: received + dropped == sent", "[queue][stress]") {
  constexpr int kProducers = 4;
  constexpr std::uint64_t kPerProducer = 50000;
  MdRing ring(64);
  std::atomic<bool> done{false};
  std::atomic<std::uint64_t> received{0};
  std::atomic<std::uint64_t> bad{0};
  std::thread consumer([&] {
    for (;;) {
      const bool finished = done.load();
      const auto n = ring.drain([&](MdEvent&& e) {
        if (e.b != e.a * 3 || e.c != e.a + 7) ++bad;  // payload must never be torn
        ++received;
      });
      if (finished && n == 0) break;
    }
  });
  std::vector<std::thread> producers;
  for (int p = 0; p < kProducers; ++p) {
    producers.emplace_back([&ring] {
      for (std::uint64_t i = 0; i < kPerProducer; ++i) {
        MdEvent e;
        e.type = 1;
        e.a = i;
        e.b = i * 3;
        e.c = i + 7;
        e.t = static_cast<double>(i);
        (void)ring.try_push(e);  // drop on full, never wait
      }
    });
  }
  for (auto& t : producers) t.join();
  done = true;
  consumer.join();
  CHECK(bad.load() == 0);
  CHECK(received.load() + ring.dropped() == kProducers * kPerProducer);
}

namespace {
struct Wide {
  std::uint64_t w[8];  // 64 bytes: all words equal unless torn
};
}  // namespace

TEST_CASE("latest-wins slot basics", "[queue]") {
  LatestWinsSlot<Wide> slot;
  CHECK_FALSE(slot.read().has_value());
  Wide a{};
  for (auto& x : a.w) x = 5;
  slot.publish(a);
  std::uint32_t v = 0;
  auto got = slot.read(&v);
  REQUIRE(got.has_value());
  CHECK(got->w[3] == 5);
  CHECK(v == 1);
  std::uint32_t last = 0;
  CHECK(slot.read_if_newer(last).has_value());
  CHECK(last == 1);
  CHECK_FALSE(slot.read_if_newer(last).has_value());  // nothing newer
  for (auto& x : a.w) x = 6;
  slot.publish(a);
  auto newer = slot.read_if_newer(last);
  REQUIRE(newer.has_value());
  CHECK(newer->w[0] == 6);
  CHECK(last == 2);
}

TEST_CASE("latest-wins slot never returns torn data", "[queue][stress]") {
  LatestWinsSlot<Wide> slot;
  std::atomic<bool> stop{false};
  std::atomic<std::uint64_t> torn{0}, reads{0}, regress{0};
  std::vector<std::thread> readers;
  for (int r = 0; r < 3; ++r) {
    readers.emplace_back([&] {
      std::uint64_t last = 0;
      while (!stop.load(std::memory_order_relaxed)) {
        if (auto v = slot.read()) {
          for (int i = 1; i < 8; ++i) {
            if (v->w[i] != v->w[0]) ++torn;
          }
          if (v->w[0] < last) ++regress;  // single writer publishes increasing values
          last = v->w[0];
          ++reads;
        }
      }
    });
  }
  Wide data{};
  for (std::uint64_t i = 1; i <= 300000; ++i) {
    for (auto& x : data.w) x = i;
    slot.publish(data);
  }
  stop = true;
  for (auto& t : readers) t.join();
  CHECK(torn.load() == 0);
  CHECK(regress.load() == 0);
  CHECK(reads.load() > 0);
  CHECK(slot.read()->w[0] == 300000);
}

TEST_CASE("latest-wins box keeps only the newest payload", "[queue]") {
  LatestWinsBox<std::string> box;
  CHECK_FALSE(box.take().has_value());
  CHECK_FALSE(box.put("a"));
  CHECK(box.put("b"));
  CHECK(box.put("c"));
  CHECK(box.take() == "c");
  CHECK_FALSE(box.take().has_value());
  CHECK(box.overwritten() == 2);
}

TEST_CASE("reliable outbox: drop-oldest at the byte cap, honours droppable hook", "[queue]") {
  struct Item {
    int id;
    bool reliable;
  };
  std::vector<int> dropped_ids;
  ReliableOutbox<Item>::Options opt;
  opt.byte_cap = 100;
  opt.droppable = [](const Item& i) { return !i.reliable; };
  opt.on_drop = [&](Item&& i, std::size_t) { dropped_ids.push_back(i.id); };
  ReliableOutbox<Item> box(opt);

  CHECK(box.push({1, false}, 40).was_empty);
  CHECK_FALSE(box.push({2, true}, 40).was_empty);
  auto r = box.push({3, false}, 40);  // 120 > 100: evict oldest droppable (1)
  CHECK(r.dropped_items == 1);
  CHECK(r.dropped_bytes == 40);
  CHECK_FALSE(r.over_cap);
  CHECK(dropped_ids == std::vector<int>{1});
  CHECK(box.bytes() == 80);

  r = box.push({4, true}, 80);  // 160: evict 3 (droppable), 2 is reliable and stays => still over cap
  CHECK(r.dropped_items == 1);
  CHECK(r.over_cap);
  CHECK(box.size() == 2);

  std::vector<Item> out;
  CHECK(box.take_all(out) == 2);
  REQUIRE(out.size() == 2);
  CHECK(out[0].id == 2);  // oldest first
  CHECK(out[1].id == 4);
  CHECK(box.size() == 0);
  CHECK(box.bytes() == 0);
  CHECK(box.dropped_items() == 2);
}

TEST_CASE("reliable outbox: default policy is plain drop-oldest", "[queue]") {
  ReliableOutbox<int>::Options opt;
  opt.byte_cap = 30;
  ReliableOutbox<int> box(opt);
  for (int i = 0; i < 5; ++i) (void)box.push(int{i}, 10);
  std::vector<int> out;
  box.take_all(out);
  CHECK(out == std::vector<int>{2, 3, 4});
}

TEST_CASE("reliable outbox under contention: nothing lost under the cap, checksum matches", "[queue][stress]") {
  constexpr int kProducers = 3;
  constexpr std::uint64_t kPerProducer = 40000;
  ReliableOutbox<std::uint64_t>::Options opt;
  opt.byte_cap = static_cast<std::size_t>(1) << 40;  // effectively unbounded here
  ReliableOutbox<std::uint64_t> box(opt);
  std::atomic<int> live{kProducers};
  std::vector<std::thread> producers;
  for (int p = 0; p < kProducers; ++p) {
    producers.emplace_back([&] {
      for (std::uint64_t i = 1; i <= kPerProducer; ++i) (void)box.push(std::uint64_t{i}, 8);
      --live;
    });
  }
  std::uint64_t count = 0, sum = 0;
  std::vector<std::uint64_t> batch;
  for (;;) {
    const bool finished = live.load() == 0;
    batch.clear();
    box.take_all(batch);
    for (auto v : batch) {
      sum += v;
      ++count;
    }
    if (finished && batch.empty()) break;
  }
  for (auto& t : producers) t.join();
  CHECK(count == kProducers * kPerProducer);
  CHECK(sum == kProducers * (kPerProducer * (kPerProducer + 1) / 2));
}
