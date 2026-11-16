#include "features/admin/admin_hub.h"

#include <utility>

#include <flatbuffers/flatbuffers.h>

#include "admin_generated.h"
#include "message_ids_generated.h"

namespace x4mp::features::admin {

namespace P = X4MP::Proto;

void AdminHub::on_frame_message(std::uint16_t type, std::span<const std::uint8_t> payload, std::uint16_t /*self_id*/) {
  if (type != static_cast<std::uint16_t>(P::MsgType::AdminResult) || payload.size() < 8) return;
  const auto* msg = flatbuffers::GetRoot<P::AdminResult>(payload.data());
  if (msg == nullptr) return;
  Result r;
  r.request_id = msg->request_id();
  r.ok = msg->ok();
  if (const flatbuffers::String* text = msg->message(); text != nullptr) r.message = text->str();
  std::lock_guard<std::mutex> lock(m_);
  if (results_.size() >= kMaxResults) results_.erase(results_.begin());
  results_.push_back(std::move(r));
}

std::vector<Result> AdminHub::take_results() {
  std::lock_guard<std::mutex> lock(m_);
  return std::exchange(results_, {});
}

AdminHub& admin_hub() {
  static AdminHub hub;
  return hub;
}

}  // namespace x4mp::features::admin
