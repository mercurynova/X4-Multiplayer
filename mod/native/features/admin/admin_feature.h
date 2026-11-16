#pragma once
// features/admin: outbound half of the in-game admin bridge - see admin_hub.h for the whole picture.
//
//   ui/ command -> bridge verb x4mp.admin
//     {"v":1,"verb":"kick","player":4,"reason":"cheating"}
//     {"v":1,"verb":"ban","player":4,"reason":"cheating","minutes":60}   minutes absent or 0 = permanent
//   -> AdminCommand (0x0502) on the Control lane; the reply comes back as
//     x4mp.admin_result {"v":1,"request":7,"ok":true,"message":"Pia was kicked"}.

#include <cstdint>
#include <memory>
#include <string_view>

#include <flatbuffers/flatbuffers.h>

#include "host/feature.h"

namespace x4mp::features {

class AdminFeature final : public host::IFeature {
 public:
  AdminFeature();
  ~AdminFeature() override;

  [[nodiscard]] std::string_view name() const noexcept override { return "admin"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;

 private:
  struct Inbox;

  void drain(host::HostContext& ctx);
  void handle(host::HostContext& ctx, std::string_view payload);
  void publish(host::HostContext& ctx);
  void refuse(host::HostContext& ctx, std::uint32_t request_id, std::string_view why);
  bool send_kick(host::HostContext& ctx, std::uint32_t player, std::string_view reason);
  bool send_ban(host::HostContext& ctx, std::uint32_t player, std::string_view reason, std::uint32_t duration_s);
  bool send(host::HostContext& ctx, std::span<const std::uint8_t> payload);

  std::shared_ptr<Inbox> inbox_;
  flatbuffers::FlatBufferBuilder fbb_{256};
  std::uint32_t request_id_ = 0;  // monotonic per session; the reply echoes it
  std::uint64_t sent_ = 0;
  std::uint64_t refused_ = 0;
  std::uint64_t failed_ = 0;
  bool logged_unlinked_ = false;
};

}  // namespace x4mp::features
