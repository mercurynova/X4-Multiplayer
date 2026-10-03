#include "features/stats/stats_feature.h"

#include "features/diag/diag_hub.h"
#include "features/stats/stats_message.h"

namespace x4mp::features {

void StatsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  using host::Cat;
  using host::Level;
  const double cost_ms = static_cast<double>(ctx.budget.elapsed_ns()) / 1e6;
  collector_.on_frame(info.delta_s, cost_ms);
  log_collector_.on_frame(info.delta_s, cost_ms);
  since_log_s_ += info.delta_s;

  NetSample net;
  const bool connected = diag_hub().sample_net(net);
  if (!connected) have_prev_ = false;  // a new connection restarts the byte counters

  if (collector_.window_seconds() >= kSendIntervalS) {
    const auto w = collector_.take_window();
    if (connected) {
      stats::NodeStatsSample s;
      s.fps = w.fps;
      s.frame_ms_p95 = w.frame_ms_p95;
      s.game_time = info.game_time.value_or(0.0);
      s.net_main_ms_p95 = w.mod_ms_p95;
      s.rtt_ms = static_cast<float>(net.rtt_us) / 1000.0f;
      s.clock_offset_us = net.clock_offset_us;
      s.tcp_send_queue_bytes = static_cast<std::uint32_t>(net.write_buffer_bytes > 0xFFFFFFFFull ? 0xFFFFFFFFull : net.write_buffer_bytes);
      if (have_prev_) {
        s.rx_bytes_per_s = stats::rate_per_s(prev_in_, net.bytes_in, w.window_s);
        s.tx_bytes_per_s = stats::rate_per_s(prev_out_, net.bytes_out, w.window_s);
      }
      prev_in_ = net.bytes_in;
      prev_out_ = net.bytes_out;
      have_prev_ = true;
      last_rtt_ms_ = s.rtt_ms;
      last_rx_ = s.rx_bytes_per_s;
      last_tx_ = s.tx_bytes_per_s;
      if (diag_hub().send_control(stats::msg_node_stats(), stats::encode_node_stats(s))) ++sent_;
    }
  }

  if (since_log_s_ >= kLogIntervalS) {
    const auto w = log_collector_.take_window();
    since_log_s_ = 0.0;
    X4MP_CLOG(ctx.log, Cat::Perf, Level::Info,
              "stats: fps={:.1f} frame_p95={:.2f}ms mod_p95={:.3f}ms mod_max={:.3f}ms connected={} rtt={:.1f}ms rx={}B/s tx={}B/s node_stats_sent={}",
              w.fps, w.frame_ms_p95, w.mod_ms_p95, w.mod_ms_max, connected ? 1 : 0, last_rtt_ms_, last_rx_, last_tx_, sent_);
  }
}

}  // namespace x4mp::features
