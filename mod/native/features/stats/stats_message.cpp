#include "features/stats/stats_message.h"

#include "admin_generated.h"
#include "message_ids_generated.h"

namespace x4mp::features::stats {

namespace P = X4MP::Proto;

std::vector<std::uint8_t> encode_node_stats(const NodeStatsSample& s) {
  flatbuffers::FlatBufferBuilder fbb(160);
  P::NodeStatsBuilder b(fbb);
  b.add_fps(s.fps);
  b.add_frame_ms_p95(s.frame_ms_p95);
  b.add_game_time(s.game_time);
  b.add_tcp_send_queue_bytes(s.tcp_send_queue_bytes);
  b.add_rx_bytes_per_s(s.rx_bytes_per_s);
  b.add_tx_bytes_per_s(s.tx_bytes_per_s);
  b.add_clock_offset_us(s.clock_offset_us);
  b.add_rtt_ms(s.rtt_ms);
  b.add_net_main_ms_p95(s.net_main_ms_p95);
  b.add_ghosts(s.ghosts);
  b.add_udp_active(s.udp_active);
  b.add_udp_rx_loss_pct(s.udp_rx_loss_pct);
  b.add_team_setup_state(static_cast<P::FeatureState>(s.team_setup_state));
  fbb.Finish(b.Finish());
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

std::uint16_t msg_node_stats() noexcept { return static_cast<std::uint16_t>(P::MsgType::NodeStats); }

}  // namespace x4mp::features::stats
