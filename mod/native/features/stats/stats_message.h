#pragma once
// features/stats/stats_message: the NodeStats wire message (protocol.md 22; N->S Control lane every 2 s). SDK-free.

#include <cstdint>
#include <vector>

namespace x4mp::features::stats {

struct NodeStatsSample {
  float fps = 0.0f;
  float frame_ms_p95 = 0.0f;
  double game_time = 0.0;  // 0 until a game is loaded
  std::uint32_t tcp_send_queue_bytes = 0;
  std::uint32_t rx_bytes_per_s = 0;
  std::uint32_t tx_bytes_per_s = 0;
  std::int64_t clock_offset_us = 0;
  float rtt_ms = 0.0f;
  float net_main_ms_p95 = 0.0f;  // the mod's own main-thread cost per frame, p95 over the window (appended field)
};

[[nodiscard]] std::vector<std::uint8_t> encode_node_stats(const NodeStatsSample& sample);
[[nodiscard]] std::uint16_t msg_node_stats() noexcept;

}  // namespace x4mp::features::stats
