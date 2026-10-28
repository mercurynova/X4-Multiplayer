#pragma once
// features/events/event_wire: the C++ half of events.fbs, for the bodies this mod actually sends (M5-05; docs/m5-plan.md W1).
//
// events.fbs is the contract and the server already implements all of it (RelayModule.Intents.cs validates every body,
// RelayModule.Events.cs fans out every kind). What was missing was a way for the mod to speak it: no C++ source under
// mod/native referenced Intent, IntentResult or GameEvent before this header. The pattern the schema states and this header
// keeps:
//
//   a node that observes something it does not own sends an Intent   -> here: encode_*  (C -> S, S -> A)
//   the authority applies it and emits world mutations + a GameEvent -> here: decode_*  (A -> S -> N)
//   exactly one IntentResult per Intent                              -> here: decode_intent_result
//
// A client never sends a GameEvent (the schema says A->S) and never applies an IntentResult beyond logging it.
//
// Everything is a flatbuffer built into a fresh FlatBufferBuilder and returned as bytes: the caller hands the vector to
// session.send(lane, MsgType::Intent, bytes). Net ids are 32-bit; a component id is 64-bit and never travels here.

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

#include "common_generated.h"
#include "events_generated.h"
#include "flatbuffers/flatbuffers.h"

namespace x4mp::features::events {

namespace P = X4MP::Proto;

// The header every Intent carries. `key` is the idempotency key: the server applies one request per key, so a resend after a
// reconnect is safe. `player_id` is stamped by the server and ignored from a client, which is why it defaults to 0 here.
struct IntentRequest {
  std::uint64_t key_lo = 0;
  std::uint64_t key_hi = 0;
  std::uint32_t request_id = 0;
  std::uint16_t player_id = 0;
  double game_time = 0.0;
};

struct KillClaimData {
  std::uint32_t target = 0;
  std::uint32_t killer = 0;
  std::uint32_t weapon_macro_ref = 0;
};

struct PlayerDeathData {
  std::uint32_t killer = 0;
};

struct TradeReportData {
  std::uint32_t station = 0;
  std::uint32_t ship = 0;
  std::uint32_t ware_ref = 0;
  std::int32_t amount = 0;      // > 0: the ship bought from the station; < 0: it sold
  std::int32_t unit_price = 0;  // informational only - money moves as CreditDelta (mod-design 12.2)
};

struct StationBuildData {
  std::string macro;
  std::string construction_plan;  // empty: the authority spawns the macro and adds modules
  std::uint16_t sector = 0;
  float position[3] = {0.0f, 0.0f, 0.0f};
  float rotation[3] = {0.0f, 0.0f, 0.0f};  // yaw, pitch, roll
  std::uint64_t local_component_id = 0;    // the station the client already built locally
};

struct CaptureReportData {
  std::uint32_t target = 0;
  std::uint32_t new_owner_ref = 0;
};

struct HitEntryData {
  std::uint32_t target = 0;
  float hull_damage = 0.0f;
  float shield_damage = 0.0f;
};

namespace detail {

inline std::vector<std::uint8_t> take_buffer(flatbuffers::FlatBufferBuilder& fbb) {
  const std::uint8_t* begin = fbb.GetBufferPointer();
  return std::vector<std::uint8_t>(begin, begin + fbb.GetSize());
}

inline std::vector<std::uint8_t> finish_intent(const IntentRequest& request, P::IntentBody type, flatbuffers::Offset<void> body,
                                               flatbuffers::FlatBufferBuilder& fbb) {
  const P::Id128 key(request.key_lo, request.key_hi);
  const flatbuffers::Offset<P::Intent> intent = P::CreateIntent(fbb, &key, request.request_id, request.player_id, request.game_time, type, body);
  fbb.Finish(intent);
  return take_buffer(fbb);
}

}  // namespace detail

[[nodiscard]] inline std::vector<std::uint8_t> encode_kill_claim(const IntentRequest& request, const KillClaimData& claim) {
  flatbuffers::FlatBufferBuilder fbb(256);
  const auto body = P::CreateKillClaim(fbb, claim.target, claim.killer, claim.weapon_macro_ref);
  return detail::finish_intent(request, P::IntentBody::KillClaim, body.Union(), fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_player_death(const IntentRequest& request, const PlayerDeathData& death) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto body = P::CreatePlayerDeath(fbb, death.killer);
  return detail::finish_intent(request, P::IntentBody::PlayerDeath, body.Union(), fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_trade_report(const IntentRequest& request, const TradeReportData& trade) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto body = P::CreateTradeReport(fbb, trade.station, trade.ship, trade.ware_ref, trade.amount, trade.unit_price);
  return detail::finish_intent(request, P::IntentBody::TradeReport, body.Union(), fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_station_build(const IntentRequest& request, const StationBuildData& build) {
  flatbuffers::FlatBufferBuilder fbb(512);
  const auto macro = fbb.CreateString(build.macro);
  const auto plan = fbb.CreateString(build.construction_plan);
  const P::Vec3f position(build.position[0], build.position[1], build.position[2]);
  const P::Rot3f rotation(build.rotation[0], build.rotation[1], build.rotation[2]);
  const auto body = P::CreateStationBuildRequest(fbb, macro, plan, build.sector, &position, &rotation, build.local_component_id);
  return detail::finish_intent(request, P::IntentBody::StationBuildRequest, body.Union(), fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_capture_report(const IntentRequest& request, const CaptureReportData& capture) {
  flatbuffers::FlatBufferBuilder fbb(128);
  const auto body = P::CreateCaptureReport(fbb, capture.target, capture.new_owner_ref);
  return detail::finish_intent(request, P::IntentBody::CaptureReport, body.Union(), fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_hit_report(const IntentRequest& request, std::span<const HitEntryData> hits) {
  flatbuffers::FlatBufferBuilder fbb(256 + hits.size() * 16);
  std::vector<P::HitEntry> entries;
  entries.reserve(hits.size());
  for (const HitEntryData& hit : hits) entries.emplace_back(hit.target, hit.hull_damage, hit.shield_damage);
  const auto vector = fbb.CreateVectorOfStructs(entries);
  const auto body = P::CreateHitReport(fbb, vector);
  return detail::finish_intent(request, P::IntentBody::HitReport, body.Union(), fbb);
}

// ---- decoding (S -> N) ----

struct IntentView {
  IntentRequest request;
  P::IntentBody type = P::IntentBody::NONE;
};

[[nodiscard]] inline bool decode_intent(std::span<const std::uint8_t> bytes, IntentView& out) {
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::Intent>(nullptr)) return false;
  const P::Intent* intent = flatbuffers::GetRoot<P::Intent>(bytes.data());
  if (intent == nullptr) return false;
  out.type = intent->body_type();
  out.request.key_lo = intent->request_key() != nullptr ? intent->request_key()->lo() : 0;
  out.request.key_hi = intent->request_key() != nullptr ? intent->request_key()->hi() : 0;
  out.request.request_id = intent->request_id();
  out.request.player_id = intent->player_id();
  out.request.game_time = intent->game_time();
  return true;
}

struct IntentResultView {
  std::uint64_t key_lo = 0;
  std::uint64_t key_hi = 0;
  std::uint32_t request_id = 0;
  P::IntentStatus status = P::IntentStatus::Rejected;
  P::RejectReason reason = P::RejectReason::None;
  std::string detail;
  std::uint32_t result_net_id = 0;
};

[[nodiscard]] inline bool decode_intent_result(std::span<const std::uint8_t> bytes, IntentResultView& out) {
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::IntentResult>(nullptr)) return false;
  const P::IntentResult* result = flatbuffers::GetRoot<P::IntentResult>(bytes.data());
  if (result == nullptr) return false;
  out.key_lo = result->request_key() != nullptr ? result->request_key()->lo() : 0;
  out.key_hi = result->request_key() != nullptr ? result->request_key()->hi() : 0;
  out.request_id = result->request_id();
  out.status = result->status();
  out.reason = result->reason();
  out.detail = result->detail() != nullptr ? std::string(result->detail()->c_str(), result->detail()->size()) : std::string();
  out.result_net_id = result->result_net_id();
  return true;
}

struct GameEventView {
  P::GameEventBody type = P::GameEventBody::NONE;
  std::uint64_t event_seq = 0;
  std::uint64_t server_time_us = 0;
  std::uint16_t sector = 0;
};

[[nodiscard]] inline bool decode_game_event(std::span<const std::uint8_t> bytes, GameEventView& out) {
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::GameEvent>(nullptr)) return false;
  const P::GameEvent* event = flatbuffers::GetRoot<P::GameEvent>(bytes.data());
  if (event == nullptr) return false;
  out.type = event->body_type();
  out.event_seq = event->event_seq();
  out.server_time_us = event->server_time_us();
  out.sector = event->sector();
  return true;
}

}  // namespace x4mp::features::events
