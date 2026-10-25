#pragma once
// features/appearance: the player's chosen avatar look (ADR-051; docs/research/on-foot-presence.md 3.2).
//
//   ui/x4mp_menu.lua (the Appearance screen) -> bridge verb x4mp.appearance
//     {"v":1}                                  report the current look (the picker asks once at load)
//     {"v":1,"race":"argon","preset":"male_cau"}  choose
//     {"v":1,"random":true}                    let the catalogue pick
//     {"v":1,"default":true}                   back to the server default (team origin, ADR-049)
//   -> stored in x4mp.json as avatar_race / avatar_preset (core/config) -> raised back to Lua as the topic
//   x4mp.appearance_state {"v":1,"race":..,"preset":..,"macro":..,"female":bool,"catalogue":{...}}.
//
// The look goes to the other nodes as PlayerAppearance (0x0303) on the Control lane: once per session when the on-foot link
// comes up (it exists exactly while this node is in game) and again after every change. The relay stamps player_id and a
// monotonic revision, stores it and replays it to joining nodes; onfoot_hub() keeps what the peers sent and ActorRegistry
// puts the macro in the spawn command. Nothing here renders: the local character never changes.
//
// Validation is the curated catalogue (appearance_catalogue.h), never the file: an unknown race/preset pair is refused with a
// log line naming the ids and the stored look stays as it was.

#include <cstdint>
#include <memory>
#include <string_view>

#include <flatbuffers/flatbuffers.h>

#include "features/appearance/appearance_catalogue.h"
#include "host/feature.h"

namespace x4mp::features {

class AppearanceFeature final : public host::IFeature {
 public:
  AppearanceFeature();
  ~AppearanceFeature() override;

  [[nodiscard]] std::string_view name() const noexcept override { return "appearance"; }
  void on_init(host::HostContext& ctx) override;
  void on_frame(host::HostContext& ctx, const host::FrameInfo& info) override;

 private:
  struct Inbox;
  void drain(host::HostContext& ctx);
  void handle(host::HostContext& ctx, std::string_view payload);
  void apply(host::HostContext& ctx, const appearance::Choice& choice);
  void publish(host::HostContext& ctx);
  bool send(host::HostContext& ctx);

  std::shared_ptr<Inbox> inbox_;
  appearance::Choice choice_;  // what this node renders as; empty = default
  bool dirty_ = false;         // the server does not know choice_ yet
  bool sent_ = false;          // ... and it does, for this session
  std::uint64_t applied_ = 0;
  std::uint64_t refused_ = 0;
  std::uint64_t send_failed_ = 0;
  std::uint64_t sends_ = 0;
  flatbuffers::FlatBufferBuilder fbb_{256};
};

}  // namespace x4mp::features
