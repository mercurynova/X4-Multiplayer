#pragma once
// game/x4_platform: IPlatform over the real X4Native SDK (x4n::*). Only compiled into x4mp.dll (the SDK include path
// exists for that target alone). Every method is null-safe against a cleared/absent API pointer.

#include <mutex>
#include <vector>

#include "host/platform.h"

namespace x4mp::game {

class X4Platform final : public host::IPlatform {
 public:
  [[nodiscard]] std::string extension_path() const override;
  [[nodiscard]] std::string game_version() const override;
  [[nodiscard]] std::string x4native_version() const override;
  [[nodiscard]] int game_types_build() const override;
  [[nodiscard]] void* get_game_function(const char* name) const override;
  void native_log(log::Level level, std::string_view message) override;
  bool stash_set(const char* key, const void* data, std::uint32_t size) override;
  [[nodiscard]] const void* stash_get(const char* key, std::uint32_t* size) override;
  bool stash_remove(const char* key) override;
  bool subscribe_event(const char* name, EventFn fn) override;
  bool raise_lua(const char* name, std::string_view text) override;
  ~X4Platform() override;  // unsubscribes the bridge events

 private:
  std::vector<int> event_subscriptions_;
  std::mutex log_mutex_;  // native_log is called from the logger's writer thread and from the main thread
};

}  // namespace x4mp::game
