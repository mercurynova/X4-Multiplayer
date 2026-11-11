#pragma once
// features/teams/teams_wire: the three invite frames of M5b (protocol/schema/teams.fbs) in the shape the hub and its tests use.
//
//   TeamInviteRequest  0x070A  C->S  request_key, to_player                    (leader -> server)
//   TeamInvite         0x070B  S->C  invite_id, from_player, from_team, team_name, expires_in_s   (server -> the invitee only)
//   TeamInviteRespond  0x070C  C->S  request_key, invite_id, accept             (the invitee -> server)
// The answer to both requests is the usual TeamRequestResult (0x0706): Ok, or Rejected with a TeamRejectReason the player is shown.
//
// Everything here is SDK-free so mod/tests/test_teams.cpp can encode and decode without a game.

#include <cstdint>
#include <span>
#include <string>
#include <vector>

#include <flatbuffers/flatbuffers.h>

#include "message_ids_generated.h"
#include "teams_generated.h"

namespace x4mp::features::teams {

namespace P = X4MP::Proto;

// What the hub books a sent request under: the request key (so a TeamRequestResult can be matched) and the op the player used.
struct InviteOut {
  std::uint16_t type = 0;
  std::vector<std::uint8_t> bytes;
  std::uint64_t key_lo = 0;
};

// A TeamInvite that arrived: the player is told who invited them where, and how long they have.
struct InviteNoticeView {
  std::uint32_t invite_id = 0;
  std::uint16_t from_player = 0;
  std::uint16_t from_team = 0;
  std::string team_name;
  std::uint16_t expires_in_s = 0;
};

// A TeamRequestResult for a request this hub sent: what the player asked for and how it went.
struct InviteResultView {
  std::uint64_t key_lo = 0;
  std::string op;  // "invite" | "respond"
  std::uint8_t status = 0;  // 0 Ok, 1 Pending, 2 Rejected
  std::uint8_t reason = 0;  // TeamRejectReason
  std::uint16_t team_id = 0;
  std::string detail;
};

namespace detail {

inline std::vector<std::uint8_t> finish(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

}  // namespace detail

[[nodiscard]] inline std::uint16_t type_of(P::MsgType type) noexcept { return static_cast<std::uint16_t>(type); }

[[nodiscard]] inline std::vector<std::uint8_t> encode_invite_request(std::uint64_t key_lo, std::uint64_t key_hi, std::uint16_t to_player) {
  flatbuffers::FlatBufferBuilder fbb(64);
  const P::Id128 key(key_lo, key_hi);
  const auto req = P::CreateTeamInviteRequest(fbb, &key, to_player);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline std::vector<std::uint8_t> encode_invite_respond(std::uint64_t key_lo, std::uint64_t key_hi, std::uint32_t invite_id,
                                                                    bool accept) {
  flatbuffers::FlatBufferBuilder fbb(64);
  const P::Id128 key(key_lo, key_hi);
  const auto req = P::CreateTeamInviteRespond(fbb, &key, invite_id, accept);
  fbb.Finish(req);
  return detail::finish(fbb);
}

[[nodiscard]] inline bool decode_team_invite(std::span<const std::uint8_t> bytes, InviteNoticeView& out) {
  out = InviteNoticeView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::TeamInvite>(nullptr)) return false;
  const P::TeamInvite* r = flatbuffers::GetRoot<P::TeamInvite>(bytes.data());
  if (r == nullptr) return false;
  out.invite_id = r->invite_id();
  out.from_player = r->from_player();
  out.from_team = r->from_team();
  out.expires_in_s = r->expires_in_s();
  if (const auto* name = r->team_name()) out.team_name = name->str();
  return true;
}

[[nodiscard]] inline bool decode_request_result(std::span<const std::uint8_t> bytes, InviteResultView& out) {
  out = InviteResultView{};
  if (bytes.size() < 4) return false;
  flatbuffers::Verifier verifier(bytes.data(), bytes.size());
  if (!verifier.VerifyBuffer<P::TeamRequestResult>(nullptr)) return false;
  const P::TeamRequestResult* r = flatbuffers::GetRoot<P::TeamRequestResult>(bytes.data());
  if (r == nullptr) return false;
  if (const P::Id128* key = r->request_key()) out.key_lo = key->lo();
  out.status = static_cast<std::uint8_t>(r->status());
  out.reason = static_cast<std::uint8_t>(r->reason());
  out.team_id = r->team_id();
  if (const auto* detail = r->detail()) out.detail = detail->str();
  return true;
}

// The reject reason as the player-facing token the Lua side turns into a text id.
[[nodiscard]] inline const char* reject_token(std::uint8_t reason) noexcept {
  switch (static_cast<P::TeamRejectReason>(reason)) {
    case P::TeamRejectReason::Locked: return "locked";
    case P::TeamRejectReason::Full: return "full";
    case P::TeamRejectReason::BadPassword: return "bad_password";
    case P::TeamRejectReason::NoFactionSlot: return "no_slot";
    case P::TeamRejectReason::NameTaken: return "name_taken";
    case P::TeamRejectReason::NotPermitted: return "not_permitted";
    case P::TeamRejectReason::NotLeader: return "not_leader";
    case P::TeamRejectReason::SameTeam: return "same_team";
    case P::TeamRejectReason::UnknownTeam: return "unknown_team";
    case P::TeamRejectReason::SessionRunningRestricted: return "session_running";
    case P::TeamRejectReason::RateLimited: return "rate_limited";
    case P::TeamRejectReason::None: break;
  }
  return "none";
}

}  // namespace x4mp::features::teams
