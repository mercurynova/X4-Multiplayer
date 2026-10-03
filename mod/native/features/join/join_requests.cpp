#include "features/join/join_requests.h"

#include <algorithm>
#include <mutex>

namespace x4mp::features::join {

namespace {
std::mutex g_mutex;
std::vector<std::string> g_queue;

void wipe(std::string& s) {
  std::fill(s.begin(), s.end(), '\0');
  s.clear();
}
}  // namespace

void submit_join_payload(std::string payload) {
  const std::lock_guard lock(g_mutex);
  if (g_queue.size() >= 4) {
    wipe(payload);
    return;
  }
  g_queue.push_back(std::move(payload));
}

std::vector<std::string> take_join_payloads() {
  const std::lock_guard lock(g_mutex);
  std::vector<std::string> out;
  out.swap(g_queue);
  return out;
}

void clear_join_payloads() {
  const std::lock_guard lock(g_mutex);
  for (auto& p : g_queue) wipe(p);
  g_queue.clear();
}

}  // namespace x4mp::features::join
