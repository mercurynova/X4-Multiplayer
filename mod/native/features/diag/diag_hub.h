#pragma once
// features/diag/diag_hub: the small process-wide rendezvous between the diagnostics features (saves, selftest,
// janitor) and the session code (M2-10).
//
//  * The join/session feature (M2-06) tells the hub what the node currently is: set_connection(role, connected).
//    The saves feature blocks saves while connected as a pure client; nothing else needs to know why.
//  * The session feature installs a LogForward sender (set_log_sender); the diagnostics features call forward_log().
//    With no sender installed (not connected, capability not granted, early start) forward_log() returns false and
//    the caller has already written the line to the local log, so nothing is lost.
//  * Lua reports the state of its save wrappers (set_saves_status); the self-test reads it back.
//  * The self-test stores its last table (last_selftest) so a later task can send it as one message.
//
// Thread-safe (one mutex); all calls are cheap. The state is process-wide and dies with the DLL (a reload unloads it).

#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "core/log/log.h"

namespace x4mp::features {

enum class NodeRole { None, Client, Authority };

// What the stats feature (M2-12) needs from the live session: the net counters and a Control-lane send. Installed by the join feature
// while the node is welcomed, cleared otherwise. Main thread only (the session is pumped there).
struct NetSample {
  std::int64_t rtt_us = 0;  // smoothed; 0 until the first Pong
  std::int64_t clock_offset_us = 0;
  std::uint64_t bytes_in = 0;   // cumulative since the connection start
  std::uint64_t bytes_out = 0;
  std::uint64_t write_buffer_bytes = 0;
};
struct StatsLink {
  std::function<bool(NetSample&)> sample;
  std::function<bool(std::uint16_t type, std::vector<std::uint8_t> payload)> send_control;
};

// What the Lua save wrappers report (verb x4mp.saves_status).
struct SavesStatus {
  bool received = false;
  bool save_game_wrapped = false;
  bool is_saving_possible_wrapped = false;
  bool menu_row_patched = false;
  bool tooltip_patched = false;
  bool blocking = false;  // Lua's current flag
  std::string detail;     // free text (config source), bounded
};

struct SelfTestRow {
  std::string name;
  std::string verdict;  // PASS FAIL WARN SKIP
  std::string detail;
};

class DiagHub {
 public:
  using LogSender = std::function<bool(log::Level, std::string_view)>;

  void set_connection(NodeRole role, bool connected);
  [[nodiscard]] NodeRole role() const;
  [[nodiscard]] bool connected() const;
  // The node is a pure client of a session: saves must be blocked.
  [[nodiscard]] bool is_client() const;
  [[nodiscard]] std::uint64_t connection_epoch() const;  // bumps on every set_connection that changes anything
  // M3-13: a session exists and is not over (connecting, downloading, loading, in game, rejoining). The janitor of a node that may be about to
  // join waits instead of sweeping the save's [MP] ships (the takeover may want its returning avatar).
  void set_session_pending(bool pending);
  [[nodiscard]] bool session_pending() const;

  void set_log_sender(LogSender sender);  // empty = none
  // Sends one LogForward line when a sender exists and accepts it. Returns whether it was handed to the sender.
  bool forward_log(log::Level level, std::string_view text);

  void set_stats_link(StatsLink link);  // empty = none
  [[nodiscard]] bool sample_net(NetSample& out) const;
  bool send_control(std::uint16_t type, std::vector<std::uint8_t> payload) const;

  void set_saves_status(SavesStatus s);
  [[nodiscard]] SavesStatus saves_status() const;

  void set_last_selftest(std::vector<SelfTestRow> rows);
  [[nodiscard]] std::vector<SelfTestRow> last_selftest() const;

  void reset();  // tests

 private:
  mutable std::mutex m_;
  NodeRole role_ = NodeRole::None;
  bool connected_ = false;
  bool session_pending_ = false;
  std::uint64_t epoch_ = 0;
  LogSender sender_;
  StatsLink stats_link_;
  SavesStatus saves_;
  std::vector<SelfTestRow> selftest_;
};

[[nodiscard]] DiagHub& diag_hub() noexcept;

}  // namespace x4mp::features
