#pragma once
// features/join/platform_stash: session::IStash over the host's in-process stash (IPlatform stash_*). The stash survives the
// extension reload that a save load and /reloadui cause (session 2, B4), so the Session intent (resume token) put here by
// unload_for_reload() is what the next incarnation resumes from. Thread-safe (the IStash contract).

#include <mutex>
#include <optional>
#include <string>
#include <string_view>

#include "core/session/session.h"
#include "host/platform.h"

namespace x4mp::features::join {

class PlatformStash final : public session::IStash {
 public:
  explicit PlatformStash(host::IPlatform& platform, std::string prefix = "join.") : platform_(platform), prefix_(std::move(prefix)) {}

  void put(std::string_view key, std::string_view value) override {
    const std::lock_guard lock(mutex_);
    platform_.stash_set(full(key).c_str(), value.data(), static_cast<std::uint32_t>(value.size()));
  }
  [[nodiscard]] std::optional<std::string> get(std::string_view key) const override {
    const std::lock_guard lock(mutex_);
    std::uint32_t size = 0;
    const void* p = platform_.stash_get(full(key).c_str(), &size);
    if (p == nullptr) return std::nullopt;
    return std::string(static_cast<const char*>(p), size);
  }
  void erase(std::string_view key) override {
    const std::lock_guard lock(mutex_);
    platform_.stash_remove(full(key).c_str());
  }

 private:
  [[nodiscard]] std::string full(std::string_view key) const { return prefix_ + std::string(key); }

  host::IPlatform& platform_;
  std::string prefix_;
  mutable std::mutex mutex_;
};

}  // namespace x4mp::features::join
