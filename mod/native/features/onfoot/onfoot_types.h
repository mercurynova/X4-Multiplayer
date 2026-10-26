#pragma once
// features/onfoot: the on-foot presence context (M3b; docs/research/on-foot-presence.md 3.1-3.3, ADR-046).
//
// Native can read the player's container and its container-local position (GetPlayerContainerID / GetPositionalOffset) but NOT which room
// the player stands in: the SDK has no room accessor (sdk/x4n_rooms.h is only an enum->name mapper). So MD owns the room key and pushes it
// over the Lua->native bridge as a flat `key=value;...` string; this header parses it.
//
// The room key must compare equal on two clients that never exchanged a string table, so every identity here is a hash of a macro id:
//   container_net_id = 0x8000_0000 | (fnv1a32(station match key) & 0x7fff_ffff)
// The top bit marks a client-matched id (ADR-009 key: sector macro, station macro, position rounded to 50 m). The server accepts such an id
// without a world-mirror lookup; M4 replaces it with the manifest station id and the wire format does not change.

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace x4mp::features::onfoot {

// Values as they appear on the wire (protocol/schema/world.fbs, enum OnFootMode).
enum class Mode : std::uint8_t { None = 0, Walking = 1, Seated = 2, InTransit = 3, Conversation = 4, Menu = 5 };

// enum OnFootAnim.
enum class Anim : std::uint8_t { Idle = 0, Walk = 1, Run = 2, Crouch = 3, Jump = 4, Sit = 5, Talk = 6, Emote = 7 };

// What MD knows and native cannot read.
struct RoomContext {
  bool valid = false;
  Mode mode = Mode::None;
  std::uint64_t container = 0;          // game UniverseId of the interior the player is in; 0 = unknown (native reads player_container())
  std::uint64_t outer = 0;              // the container's container (a station in a zone), 0 when unknown
  std::uint32_t container_net_id = 0;   // matched id derived from the station key
  std::uint32_t outer_net_id = 0;
  std::uint32_t room_macro_ref = 0;     // FNV-1a 32 of the room macro id
  // The room macro id itself, as MD spells it. The wire carries only room_macro_ref, but MD cannot hash: the spawn cue looks the
  // room up with find_room and compares $Rm.macro.id to this string. same_room() guarantees the two clients share the hash, so the
  // viewer's own id is also the sender's id.
  std::string room_macro;
  std::uint16_t path_hash = 0;          // FNV-1a 32 of the macro chain, truncated
  std::int16_t anchor_x = 0;            // room origin in the container, decimetres (S10.2: raw metres are not stable)
  std::int16_t anchor_y = 0;
  std::int16_t anchor_z = 0;
  std::uint8_t kind = 0;                // enum RoomKind: 0 unknown, 1 static, 2 dynamic, 3 platform
  std::uint8_t roomtype = 21;           // enum X4RoomType, 21 = none (control rooms and dock areas report null)

  bool operator==(const RoomContext&) const = default;
};

// The room half of a state (protocol/schema/world.fbs RoomKey): the same quantised key the sender built from its own room context, so
// two clients compare it without a string table. Absent while the mode is Seated (the game already places a seated avatar).
struct RoomRef {
  std::uint32_t macro_ref = 0;   // FNV-1a 32 of the room macro id; 0 = no room
  std::int16_t anchor_x = 0;     // room origin in the container, decimetres
  std::int16_t anchor_y = 0;
  std::int16_t anchor_z = 0;
  std::uint16_t path_hash = 0;   // FNV-1a 32 of the macro chain, truncated
  std::uint8_t kind = 0;         // enum RoomKind
  std::uint8_t roomtype = 21;    // enum X4RoomType

  [[nodiscard]] bool known() const noexcept { return macro_ref != 0; }
  bool operator==(const RoomRef&) const = default;
};

// One remote player as the relay reports it.
struct RemoteAvatar {
  Mode mode = Mode::None;
  std::uint32_t container = 0;
  std::uint32_t outer = 0;
  RoomRef room;  // absent (macro_ref 0) while seated or when the sender had no room
  float cx = 0;  // metres, container space (eye height, like the sender's GetPositionalOffset)
  float cy = 0;
  float cz = 0;
  float px = 0;  // metres, room space: cx/cy/cz minus the room anchor (what the avatar is placed with)
  float py = 0;
  float pz = 0;
  float yaw = 0;  // radians
  float pitch = 0;
  Anim anim = Anim::Idle;
  std::uint8_t emote = 0;
  std::uint8_t flags = 0;
  std::uint32_t seq = 0;
  std::int64_t at_us = 0;
};

// How a remote player looks (protocol/schema/world.fbs PlayerAppearance). The server stamps the revision, so the newest wins and a late
// older frame is ignored. An empty race means "the server's default for the team origin"; the actor spawn then keeps the player's own race.
struct Appearance {
  std::uint32_t revision = 0;
  std::string race;
  std::string preset;
  std::string character_macro;
  std::string clothing;
  bool is_female = false;
};

[[nodiscard]] std::uint32_t fnv1a32(std::string_view text) noexcept;
// 0x8000_0000 | hash; never 0x8000_0000 itself, and 0 for an empty key (no container).
[[nodiscard]] std::uint32_t matched_container_id(std::string_view key) noexcept;

// The canonical station match key "<sector macro>|<station macro>|<x>,<y>,<z>". MD has no round(), so it sends the raw sector-space
// position and the rounding happens here: the bucket is 50 m, which is far above the sub-millimetre drift S10.2 measured between two
// runs of the same save. The same doubles always produce the same string on both clients, which is what makes the hash comparable.
[[nodiscard]] std::string station_match_key(std::string_view sector_macro, std::string_view station_macro, double x, double y,
                                            double z);

// Parses the MD report. Fields (all `key=value`, separated by ';', order free):
//   mode=0..5                      required
//   cont=<u64> outer=<u64>         optional; 0/absent = the feature reads the player's container itself
//   skey=<station key>             the composed key, or the three parts below instead
//   sector=<macro id> smacro=<macro id> sx= sy= sz=   sector-space station position in metres (MD prints e.g. -2059.785)
//   rmacro=<room macro id>         required while the mode is not None
//   ax= ay= az=                    room origin in the container, metres with decimals; rounded to decimetres here
//   kind=0..255 roomtype=0..255    optional (roomtype stays 21: MD prints a name, not the ubyte)
// Returns nullopt when a required field is missing or unparsable; `why` then names the field.
[[nodiscard]] std::optional<RoomContext> parse_room_context(std::string_view kv, std::string* why = nullptr);

// True when `avatar` stands in the room `local` describes: same container and the same quantised room key (the anchor is part of the
// identity because one station macro can appear several times in a station). A seated or unknown remote, or a local player who is not
// on foot, never matches.
[[nodiscard]] bool same_room(const RemoteAvatar& avatar, const RoomContext& local) noexcept;

// True when the remote player is inside the container the local player is in (M3c, Tier 2): the avatar then belongs in that
// container even when it stands in another room, because MD can place an actor in any room of a loaded container. Says nothing
// about the room: same_room() is the strict case, and an avatar with an unknown room key cannot be placed at all.
[[nodiscard]] bool same_container(const RemoteAvatar& avatar, const RoomContext& local) noexcept;

}  // namespace x4mp::features::onfoot
