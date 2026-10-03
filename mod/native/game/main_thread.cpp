#include "game/main_thread.h"

#include <atomic>
#include <functional>
#include <thread>

namespace x4mp::game {

namespace {
std::uint64_t this_thread_token() noexcept {
  // Non-zero hash of the thread id (0 is reserved for "not captured").
  const auto h = static_cast<std::uint64_t>(std::hash<std::thread::id>{}(std::this_thread::get_id()));
  return h == 0 ? 1 : h;
}

struct State {
  std::atomic<MainThreadDefinition> def{MainThreadDefinition::FrameUpdateThread};
  std::atomic<std::uint64_t> init_thread{0};
  std::atomic<std::uint64_t> frame_thread{0};
  std::atomic<std::uint64_t> violations{0};
  std::atomic<MainThread::ViolationHandler> handler{nullptr};
};

State& state() noexcept {
  static State s;
  return s;
}
}  // namespace

void MainThread::set_definition(MainThreadDefinition def) noexcept { state().def.store(def); }
MainThreadDefinition MainThread::definition() const noexcept { return state().def.load(); }

void MainThread::reset() noexcept {
  state().init_thread.store(0);
  state().frame_thread.store(0);
}

void MainThread::capture_init() noexcept { state().init_thread.store(this_thread_token()); }

// Session-2 finding: on_frame_update was seen on two different thread ids in one run. "Main" is therefore the thread of the
// CURRENT on_frame_update call (re-captured every frame), not the thread of the first one.
void MainThread::capture_frame() noexcept { state().frame_thread.store(this_thread_token()); }

bool MainThread::is_main() const noexcept {
  auto& s = state();
  switch (s.def.load()) {
    case MainThreadDefinition::Any: return true;
    case MainThreadDefinition::InitThread: {
      const auto t = s.init_thread.load();
      return t == 0 || t == this_thread_token();
    }
    case MainThreadDefinition::FrameUpdateThread: {
      const auto t = s.frame_thread.load();
      return t == 0 || t == this_thread_token();
    }
  }
  return false;
}

bool MainThread::assert_main_thread(const char* where) noexcept {
  if (is_main()) return true;
  auto& s = state();
  s.violations.fetch_add(1);
  if (const auto h = s.handler.load()) h(where);
  return false;
}

void MainThread::set_violation_handler(ViolationHandler handler) noexcept { state().handler.store(handler); }
std::uint64_t MainThread::violations() const noexcept { return state().violations.load(); }

MainThread& main_thread() noexcept {
  static MainThread instance;
  return instance;
}

}  // namespace x4mp::game
