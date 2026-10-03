#pragma once
// Test doubles shared by the host tests: a fake IPlatform (X4Native stand-in) and a temp "portable" extension folder.

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <random>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "host/platform.h"

namespace x4mp::test {

struct TempDir {
  std::filesystem::path path;
  TempDir() {
    std::random_device rd;
    path = std::filesystem::temp_directory_path() / ("x4mp_host_test_" + std::to_string(rd()) + "_" + std::to_string(rd()));
    std::filesystem::create_directories(path);
    std::ofstream(path / "x4mp.portable").put('\n');  // portable mode: never touch Documents
  }
  ~TempDir() {
    std::error_code ec;
    std::filesystem::remove_all(path, ec);
  }
  TempDir(const TempDir&) = delete;
  TempDir& operator=(const TempDir&) = delete;
  void write(const std::string& name, const std::string& text) const { std::ofstream(path / name, std::ios::binary) << text; }
  [[nodiscard]] std::string read(const std::filesystem::path& rel) const {
    std::ifstream in(path / rel, std::ios::binary);
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
  }
};

class FakePlatform final : public host::IPlatform {
 public:
  explicit FakePlatform(std::filesystem::path ext) : ext_(std::move(ext)) {}

  std::string extension_path() const override { return ext_.string(); }
  std::string game_version() const override { return version; }
  std::string x4native_version() const override { return "9.0.0"; }
  int game_types_build() const override { return 900; }
  void* get_game_function(const char* name) const override {
    const auto it = functions.find(name);
    return it == functions.end() ? nullptr : it->second;
  }
  void native_log(log::Level level, std::string_view message) override {
    const std::lock_guard lock(m_);
    native_lines.push_back(std::string(log::level_name(level)) + " " + std::string(message));
  }
  bool stash_set(const char* key, const void* data, std::uint32_t size) override {
    auto& v = stash[key];
    v.assign(static_cast<const std::uint8_t*>(data), static_cast<const std::uint8_t*>(data) + size);
    return true;
  }
  const void* stash_get(const char* key, std::uint32_t* size) override {
    const auto it = stash.find(key);
    if (it == stash.end()) return nullptr;
    if (size) *size = static_cast<std::uint32_t>(it->second.size());
    return it->second.data();
  }
  bool stash_remove(const char* key) override { return stash.erase(key) > 0; }
  // Bridge (M2-10): subscriptions are kept so tests can fire a Lua->native verb; raise_lua calls are recorded.
  bool subscribe_event(const char* name, EventFn fn) override {
    subscribers[name].push_back(std::move(fn));
    return true;
  }
  bool raise_lua(const char* name, std::string_view text) override {
    if (!lua_ok) return false;
    raised.emplace_back(name, std::string(text));
    return true;
  }
  void fire(const std::string& name, const std::string& text) {
    for (auto& fn : subscribers[name]) fn(text);
  }
  [[nodiscard]] std::vector<std::string> raised_named(const std::string& name) const {
    std::vector<std::string> out;
    for (const auto& [n, t] : raised) {
      if (n == name) out.push_back(t);
    }
    return out;
  }

  std::string version = "9.00";
  std::map<std::string, void*> functions;
  std::map<std::string, std::vector<std::uint8_t>> stash;  // survives "reload": reuse the same FakePlatform
  std::vector<std::string> native_lines;
  std::map<std::string, std::vector<EventFn>> subscribers;
  std::vector<std::pair<std::string, std::string>> raised;
  bool lua_ok = true;

 private:
  std::filesystem::path ext_;
  std::mutex m_;
};

}  // namespace x4mp::test
