#include "features/stats/stats_feature.h"

#include <cstddef>

#include "features/chat/chat_json.h"
#include "features/diag/diag_hub.h"
#include "features/ghosts/ghost_hub.h"
#include "features/stats/budget_line.h"
#include "features/stats/frame_timing.h"
#include "features/stats/stats_message.h"
#include "features/stats/stats_lane.h"
#include "features/teams/team_hub.h"

namespace x4mp::features {

namespace {

// M4-30: the roster counts the other players, never this node. `size()` includes the local row when the server listed it.
[[nodiscard]] std::uint32_t other_players() noexcept {
  const chat::RosterTracker& roster = chat::chat_hub().roster();
  const std::size_t total = roster.size();
  const bool self_listed = roster.find(chat::chat_hub().self_id()) != nullptr;
  return static_cast<std::uint32_t>(self_listed && total > 0 ? total - 1 : total);
}

}  // namespace

void StatsFeature::on_frame(host::HostContext& ctx, const host::FrameInfo& info) {
  using host::Cat;
  using host::Level;
  const double cost_ms = static_cast<double>(ctx.budget.elapsed_ns()) / 1e6;
  collector_.on_frame(info.delta_s, cost_ms);
  log_collector_.on_frame(info.delta_s, cost_ms);
  budget_collector_.on_frame(info.delta_s, cost_ms);
  since_log_s_ += info.delta_s;
  since_budget_s_ += info.delta_s;

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
      s.team_setup_state = static_cast<std::uint8_t>(teams::team_hub().state());
      s.rtt_ms = static_cast<float>(net.rtt_us) / 1000.0f;
      s.clock_offset_us = net.clock_offset_us;
      s.ghosts = static_cast<std::uint32_t>(ghosts::ghost_hub().ghost_count());
      s.udp_active = net.udp_active;
      s.udp_rx_loss_pct = net.udp_rx_loss_pct;
      s.tcp_send_queue_bytes = static_cast<std::uint32_t>(net.write_buffer_bytes > 0xFFFFFFFFull ? 0xFFFFFFFFull : net.write_buffer_bytes);
      if (have_prev_) {
        // TCP + UDP (the Realtime traffic is the bulk of it when the lane is up)
        s.rx_bytes_per_s = stats::rate_per_s(prev_in_, net.bytes_in + net.udp_bytes_in, w.window_s);
        s.tx_bytes_per_s = stats::rate_per_s(prev_out_, net.bytes_out + net.udp_bytes_out, w.window_s);
      }
      prev_in_ = net.bytes_in + net.udp_bytes_in;
      prev_out_ = net.bytes_out + net.udp_bytes_out;
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
              "stats: fps={:.1f} frame_p95={:.2f}ms mod_p95={:.3f}ms mod_max={:.3f}ms connected={} rtt={:.1f}ms rx={}B/s tx={}B/s node_stats_sent={} {}",
              w.fps, w.frame_ms_p95, w.mod_ms_p95, w.mod_ms_max, connected ? 1 : 0, last_rtt_ms_, last_rx_, last_tx_, sent_,
              stats::format_lane(net));
  }

  // M4-30: the aggregated budget line. It runs on the frame clock like the other two windows, so a paused game (no frames) never
  // produces a line with a stale window in it, and the authority's timing slot is taken even when the node has no ghosts.
  if (since_budget_s_ >= kBudgetIntervalS) {
    const auto w = budget_collector_.take_window();
    const auto auth = stats::timing_hub().take("authority");
    since_budget_s_ = 0.0;
    stats::BudgetSample s;
    s.fps = w.fps;
    s.frame_ms_p50 = w.frame_ms_p50;
    s.frame_ms_p95 = w.frame_ms_p95;
    s.mod_ms_p50 = w.mod_ms_p50;
    s.mod_ms_p95 = w.mod_ms_p95;
    s.mod_ms_max = w.mod_ms_max;
    s.authority_ms_p50 = auth.ms_p50;
    s.authority_ms_p95 = auth.ms_p95;
    s.authority_ms_max = auth.ms_max;
    s.clients = other_players();
    s.ghosts = static_cast<std::uint32_t>(ghosts::ghost_hub().ghost_count());
    s.suppressed = static_cast<std::uint32_t>(ghosts::ghost_hub().suppressed());
    s.rx_bytes_per_s = last_rx_;
    s.tx_bytes_per_s = last_tx_;
    s.per_client_bytes_per_s = stats::per_client_rate(last_tx_, s.clients);
    s.udp_state = net.udp_state != nullptr ? net.udp_state : "off";
    s.window_s = w.window_s;
    X4MP_CLOG(ctx.log, Cat::Perf, Level::Info, "{}", stats::format_budget(s));
  }
}

}  // namespace x4mp::features
