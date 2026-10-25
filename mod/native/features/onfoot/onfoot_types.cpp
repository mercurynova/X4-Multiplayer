#include "features/onfoot/onfoot_types.h"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <string>
#include <utility>
#include <vector>

namespace x4mp::features::onfoot {

namespace {

bool get_field(std::string_view kv, std::string_view name, std::string_view& out) noexcept {
  std::size_t pos = 0;
  while (pos <= kv.size()) {
    const auto end = kv.find(';', pos);
    const auto item = kv.substr(pos, end == std::string_view::npos ? kv.size() - pos : end - pos);
    const auto eq = item.find('=');
    if (eq != std::string_view::npos && item.substr(0, eq) == name) {
      out = item.substr(eq + 1);
      return true;
    }
    if (end == std::string_view::npos) break;
    pos = end + 1;
  }
  return false;
}

template <typename T>
bool parse_int(std::string_view text, T& out) noexcept {
  if (text.empty()) return false;
  const char* first = text.data();
  const char* last = text.data() + text.size();
  const auto res = std::from_chars(first, last, out);
  return res.ec == std::errc{} && res.ptr == last;
}

// MD prints every number as a double ("-2059.785", "0.1658"), so integers coming from MD must be read as doubles.
bool parse_double(std::string_view text, double& out) noexcept {
  if (text.empty()) return false;
  const char* first = text.data();
  const char* last = text.data() + text.size();
  const auto res = std::from_chars(first, last, out);
  return res.ec == std::errc{} && res.ptr == last && std::isfinite(out);
}

// Metres -> decimetres. The wire field is int16 (+-3.27 km); a station whose room sits farther out from the container origin clamps
// instead of turning the whole report into a parse failure. The room-local position stays correct to the metre in that case.
std::int16_t to_decimetres(double metres) noexcept {
  const auto dm = std::llround(metres * 10.0);
  return static_cast<std::int16_t>(std::clamp<long long>(dm, -32768, 32767));
}

std::string_view trim(std::string_view text) noexcept {
  while (!text.empty() && (text.front() == ' ' || text.front() == '\t')) text.remove_prefix(1);
  while (!text.empty() && (text.back() == ' ' || text.back() == '\t')) text.remove_suffix(1);
  return text;
}

}  // namespace

std::uint32_t fnv1a32(std::string_view text) noexcept {
  std::uint32_t hash = 2166136261u;
  for (const char c : text) {
    hash ^= static_cast<std::uint8_t>(c);
    hash *= 16777619u;
  }
  return hash;
}

std::uint32_t matched_container_id(std::string_view key) noexcept {
  if (key.empty()) return 0;
  auto hash = fnv1a32(key) & 0x7fffffffu;
  if (hash == 0) hash = 1;  // 0x8000_0000 alone is reserved for "no container"
  return 0x80000000u | hash;
}

std::string station_match_key(std::string_view sector_macro, std::string_view station_macro, double x, double y, double z) {
  const auto bucket = [](double metres) {
    // 50 m buckets, integer output: no locale, no trailing ".0", identical on both clients.
    return std::llround(metres / 50.0) * 50;
  };
  std::string key;
  key.reserve(sector_macro.size() + station_macro.size() + 40);
  key.append(sector_macro);
  key.push_back('|');
  key.append(station_macro);
  key.push_back('|');
  key.append(std::to_string(bucket(x)));
  key.push_back(',');
  key.append(std::to_string(bucket(y)));
  key.push_back(',');
  key.append(std::to_string(bucket(z)));
  return key;
}

std::optional<RoomContext> parse_room_context(std::string_view kv, std::string* why) {
  const auto fail = [why](const char* field) -> std::optional<RoomContext> {
    if (why) *why = field;
    return std::nullopt;
  };
  RoomContext ctx;
  ctx.valid = true;

  std::string_view v;
  if (!get_field(kv, "mode", v)) return fail("mode");
  unsigned mode = 0;
  if (!parse_int(v, mode) || mode > 5) return fail("mode");
  ctx.mode = static_cast<Mode>(mode);
  if (ctx.mode == Mode::None) return ctx;  // the leave state needs no container

  if (!get_field(kv, "cont", v)) {
    ctx.container = 0;  // optional: the feature reads player_container() when the report does not carry one
  } else if (!parse_int(v, ctx.container)) {
    return fail("cont");
  }
  if (get_field(kv, "outer", v) && !parse_int(v, ctx.outer)) return fail("outer");

  // Station identity: the matched id comes from the ADR-009 key (sector macro, station macro, position rounded to 50 m). MD has no
  // round(), so it sends the parts and the rounding happens here.
  if (get_field(kv, "skey", v)) {
    ctx.container_net_id = matched_container_id(trim(v));
  } else {
    std::string_view sector, smacro, sx, sy, sz;
    if (!get_field(kv, "sector", sector)) return fail("sector");
    if (!get_field(kv, "smacro", smacro)) return fail("smacro");
    double px = 0, py = 0, pz = 0;
    if (!get_field(kv, "sx", sx) || !parse_double(sx, px)) return fail("sx");
    if (!get_field(kv, "sy", sy) || !parse_double(sy, py)) return fail("sy");
    if (!get_field(kv, "sz", sz) || !parse_double(sz, pz)) return fail("sz");
    sector = trim(sector);
    smacro = trim(smacro);
    if (sector.empty() || smacro.empty()) return fail("smacro");
    ctx.container_net_id = matched_container_id(station_match_key(sector, smacro, px, py, pz));
  }
  if (ctx.container_net_id == 0) return fail("smacro");
  if (get_field(kv, "oskey", v)) ctx.outer_net_id = matched_container_id(trim(v));

  if (!get_field(kv, "rmacro", v)) return fail("rmacro");
  ctx.room_macro_ref = fnv1a32(trim(v));
  if (ctx.room_macro_ref == 0) return fail("rmacro");
  if (get_field(kv, "chain", v)) ctx.path_hash = static_cast<std::uint16_t>(fnv1a32(v) & 0xffffu);

  double ax = 0, ay = 0, az = 0;
  if (!get_field(kv, "ax", v) || !parse_double(v, ax)) return fail("ax");
  if (!get_field(kv, "ay", v) || !parse_double(v, ay)) return fail("ay");
  if (!get_field(kv, "az", v) || !parse_double(v, az)) return fail("az");
  ctx.anchor_x = to_decimetres(ax);
  ctx.anchor_y = to_decimetres(ay);
  ctx.anchor_z = to_decimetres(az);

  unsigned small = 0;
  if (get_field(kv, "kind", v)) {
    if (!parse_int(v, small) || small > 255) return fail("kind");
    ctx.kind = static_cast<std::uint8_t>(small);
  }
  if (get_field(kv, "roomtype", v)) {
    if (!parse_int(v, small) || small > 255) return fail("roomtype");
    ctx.roomtype = static_cast<std::uint8_t>(small);
  }
  return ctx;
}

}  // namespace x4mp::features::onfoot
