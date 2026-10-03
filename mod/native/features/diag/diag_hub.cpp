#include "features/diag/diag_hub.h"

namespace x4mp::features {

void DiagHub::set_connection(NodeRole role, bool connected) {
  const std::lock_guard lock(m_);
  if (role_ != role || connected_ != connected) ++epoch_;
  role_ = role;
  connected_ = connected;
}

NodeRole DiagHub::role() const {
  const std::lock_guard lock(m_);
  return role_;
}

bool DiagHub::connected() const {
  const std::lock_guard lock(m_);
  return connected_;
}

bool DiagHub::is_client() const {
  const std::lock_guard lock(m_);
  return connected_ && role_ == NodeRole::Client;
}

std::uint64_t DiagHub::connection_epoch() const {
  const std::lock_guard lock(m_);
  return epoch_;
}

void DiagHub::set_log_sender(LogSender sender) {
  const std::lock_guard lock(m_);
  sender_ = std::move(sender);
}

bool DiagHub::forward_log(log::Level level, std::string_view text) {
  LogSender copy;
  {
    const std::lock_guard lock(m_);
    copy = sender_;
  }
  if (!copy) return false;
  return copy(level, text);
}

void DiagHub::set_saves_status(SavesStatus s) {
  const std::lock_guard lock(m_);
  saves_ = std::move(s);
}

SavesStatus DiagHub::saves_status() const {
  const std::lock_guard lock(m_);
  return saves_;
}

void DiagHub::set_last_selftest(std::vector<SelfTestRow> rows) {
  const std::lock_guard lock(m_);
  selftest_ = std::move(rows);
}

std::vector<SelfTestRow> DiagHub::last_selftest() const {
  const std::lock_guard lock(m_);
  return selftest_;
}

void DiagHub::reset() {
  const std::lock_guard lock(m_);
  role_ = NodeRole::None;
  connected_ = false;
  ++epoch_;
  sender_ = nullptr;
  saves_ = {};
  selftest_.clear();
}

DiagHub& diag_hub() noexcept {
  static DiagHub hub;
  return hub;
}

}  // namespace x4mp::features
