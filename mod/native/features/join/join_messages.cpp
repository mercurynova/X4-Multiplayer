#include "features/join/join_messages.h"

#include <string>

#include "admin_generated.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace x4mp::features::join {

namespace P = X4MP::Proto;

namespace {
Payload take(const flatbuffers::FlatBufferBuilder& fbb) {
  return Payload(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}
}  // namespace

Payload encode_load_status(JoinPhase phase, float progress, std::string_view detail) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const std::string d(detail);
  fbb.Finish(P::CreateLoadStatusDirect(fbb, static_cast<P::NodePhase>(phase), progress, 0, d.empty() ? nullptr : d.c_str(),
                                       P::DisconnectCode::None));
  return take(fbb);
}

Payload encode_manifest_report_counts(const session::Id128& checkpoint, std::uint32_t total, std::uint32_t matched,
                                      std::uint32_t duration_ms) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const P::Id128 cp(checkpoint.lo, checkpoint.hi);
  fbb.Finish(P::CreateManifestReportDirect(fbb, &cp, total, matched, 0, 0, total > matched ? total - matched : 0, 0, 0, nullptr,
                                           duration_ms));
  return take(fbb);
}

Payload encode_node_ready(std::uint64_t universe_epoch, const std::vector<std::uint8_t>& loaded_save_sha256) {
  flatbuffers::FlatBufferBuilder fbb(128);
  fbb.Finish(P::CreateNodeReadyDirect(fbb, universe_epoch, &loaded_save_sha256));
  return take(fbb);
}

Payload encode_log_forward(int level, std::uint64_t time_us, std::string_view text) {
  flatbuffers::FlatBufferBuilder fbb(256);
  const std::string t(text.substr(0, 1024));
  const auto line = P::CreateLogLineDirect(fbb, static_cast<P::LogLevel>(level < 0 ? 0 : (level > 3 ? 3 : level)), time_us, t.c_str());
  const std::vector<flatbuffers::Offset<P::LogLine>> lines{line};
  fbb.Finish(P::CreateLogForwardDirect(fbb, &lines));
  return take(fbb);
}
std::uint16_t msg_log_forward() noexcept { return static_cast<std::uint16_t>(P::MsgType::LogForward); }

std::uint16_t msg_load_status() noexcept { return static_cast<std::uint16_t>(P::MsgType::LoadStatus); }
std::uint16_t msg_manifest_report() noexcept { return static_cast<std::uint16_t>(P::MsgType::ManifestReport); }
std::uint16_t msg_node_ready() noexcept { return static_cast<std::uint16_t>(P::MsgType::NodeReady); }

}  // namespace x4mp::features::join
