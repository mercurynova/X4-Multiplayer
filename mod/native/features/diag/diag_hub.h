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

  void set_log_sender(LogSender sender);  // empty = none
  // Sends one LogForward line when a sender exists and accepts it. Returns whether it was handed to the sender.
  bool forward_log(log::Level level, std::string_view text);

  void set_saves_status(SavesStatus s);
  [[nodiscard]] SavesStatus saves_status() const;

  void set_last_selftest(std::vector<SelfTestRow> rows);
  [[nodiscard]] std::vector<SelfTestRow> last_selftest() const;

  void reset();  // tests

 private:
  mutable std::mutex m_;
  NodeRole role_ = NodeRole::None;
  bool connected_ = false;
  std::uint64_t epoch_ = 0;
  LogSender sender_;
  SavesStatus saves_;
  std::vector<SelfTestRow> selftest_;
};

[[nodiscard]] DiagHub& diag_hub() noexcept;

}  // namespace x4mp::features
