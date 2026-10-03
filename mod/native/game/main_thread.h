#pragma once
// game/main_thread: the "main thread" definition and assert_main_thread (M2-04; m2-plan 4.1 finding B7).
//
// X4 callbacks may arrive on several threads (MD callbacks can run on workers; whether X4N_EXTENSION and Lua-bridged
// events share the on_frame_update thread is session-2 finding B7). Game API calls are only legal on ONE thread, so
// "which thread is main" is a swappable definition:
//
//   FrameUpdateThread (default)  main = the thread that delivers the CURRENT on_frame_update (re-captured at the start of every
//                                frame: session 2 saw two thread ids in one run). Until the first frame nothing is captured
//                                and every thread passes (init-time calls).
//   InitThread                   main = the thread that ran x4native_init (capture_init).
//   Any                          the check always passes (escape hatch if B7 shows a single thread is meaningless).
//
// A failed check never crashes the game: assert_main_thread() returns false, counts the violation and invokes the
// violation handler (the host installs one that logs, rate limited). Callers must then skip the game call.
// Thread safety: all members are atomics, callable from any thread.

#include <cstdint>

namespace x4mp::game {

enum class MainThreadDefinition { FrameUpdateThread, InitThread, Any };

class MainThread {
 public:
  using ViolationHandler = void (*)(const char* where) noexcept;

  void set_definition(MainThreadDefinition def) noexcept;
  [[nodiscard]] MainThreadDefinition definition() const noexcept;

  // Forget any captured thread (called by the host at every init, so a reload that lands on another thread works).
  void reset() noexcept;
  void capture_init() noexcept;   // call from x4native_init
  void capture_frame() noexcept;  // call at the start of every on_frame_update; the latest call wins

  [[nodiscard]] bool is_main() const noexcept;
  // True when the caller may touch the game API. On failure: counts, calls the handler, returns false.
  [[nodiscard]] bool assert_main_thread(const char* where) noexcept;

  void set_violation_handler(ViolationHandler handler) noexcept;
  [[nodiscard]] std::uint64_t violations() const noexcept;
};

// Process-wide instance used by game/ adapters and the host. (Hostsim and tests may swap the definition.)
[[nodiscard]] MainThread& main_thread() noexcept;
[[nodiscard]] inline bool assert_main_thread(const char* where) noexcept { return main_thread().assert_main_thread(where); }

}  // namespace x4mp::game
