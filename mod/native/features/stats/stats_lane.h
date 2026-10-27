#pragma once
// features/stats/stats_lane: the per-lane half of the session status line (M4-25; docs/m4-plan.md W6).
//
// M4-25 asks that "a session runs WorldUpdate / EntityStatusBatch over UDP with TCP as fallback, visible in the 30 s line".
// The lane itself is core/net/udp_lane.h (M3-03) and its counters already live in net::UdpStats; this file is only the bridge
// from those counters to NetSample (apply_lane, used by the join feature's stats link) and the text the 5 s `stats:` line
// carries (format_lane), so a host test can assert the shape without a session:
//
//   udp=active udp_frames=1204/1180 udp_dgram=1204/1180 udp_loss=0.4% udp_binds=1 udp_fb=0 udp_reprobe=0 udp_bad=0 udp_dup=0
//
// frames are the sub-messages that rode the lane (hello excluded), datagrams the packets that carried them, out/in. When the
// lane is off or still binding every counter is 0, and that is the honest reading: Realtime is on TCP.

#include <format>
#include <string>

#include "core/net/udp_types.h"
#include "features/diag/diag_hub.h"

namespace x4mp::features::stats {

// Copy the lane's counters into the sample the stats feature holds. Everything but the byte counters is new in M4-25; the
// first four were the M3-14 subset.
inline void apply_lane(NetSample& out, const net::UdpStats& udp) {
  out.udp_active = udp.state == net::UdpState::Active;
  out.udp_rx_loss_pct = udp.rx_loss_pct;
  out.udp_bytes_in = udp.bytes_in;
  out.udp_bytes_out = udp.bytes_out;
  out.udp_state = net::to_string(udp.state);
  out.udp_frames_out = udp.frames_out;
  out.udp_frames_in = udp.frames_in;
  out.udp_datagrams_out = udp.datagrams_out;
  out.udp_datagrams_in = udp.datagrams_in;
  out.udp_binds = udp.binds;
  out.udp_fallbacks = udp.fallbacks;
  out.udp_reprobes = udp.reprobes;
  out.udp_malformed = udp.malformed;
  out.udp_duplicates = udp.duplicates;
}

[[nodiscard]] inline std::string format_lane(const NetSample& net) {
  return std::format(
      "udp={} udp_frames={}/{} udp_dgram={}/{} udp_loss={:.1f}% udp_binds={} udp_fb={} udp_reprobe={} udp_bad={} udp_dup={}",
      net.udp_state != nullptr ? net.udp_state : "off", net.udp_frames_out, net.udp_frames_in, net.udp_datagrams_out,
      net.udp_datagrams_in, static_cast<double>(net.udp_rx_loss_pct), net.udp_binds, net.udp_fallbacks, net.udp_reprobes,
      net.udp_malformed, net.udp_duplicates);
}

}  // namespace x4mp::features::stats
