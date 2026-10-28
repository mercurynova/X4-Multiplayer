#pragma once
// features/events: what MD says happened, and how one line of it is read (M5-01; docs/m5-plan.md W1).
//
// The mod learns about the world through three layers, and this header is the first one: the MD capture ring in
// extension/x4mp/md/x4mp_events.xml pushes ONE compact line per occurrence over the bridge the capture scan already uses:
//
//   MD -> Lua     raise_lua_event "x4mp.event" "<line>"
//   Lua -> native verb x4mp.event {"v":1,"data":"<line>"}
//
// The line is not JSON on purpose. MD string concatenation is cheap, JSON escaping in MD is not, and every field here is an
// identifier, a macro or an integer - so field 0 is a kind letter and the rest are positional:
//
//   H;<n>                                     hook hello: the MD half armed <n> hooks (sent once, at script init)
//   K;<victim>;<macro>;<owner>;<killer>;<weapon>   a ship died, and we know who killed it
//   X;<component>                             an object was destroyed and no killer was observed
//   P;                                        the player's own ship died (no component: it is "ours")
//   T;<station>;<ware>;<amount>;<price>       a trade finished: station, ware macro, signed units, unit price
//   B;<station>;<macro>;<sector>              a build finished: the station, its macro, the sector macro
//   C;<component>;<old_owner>;<new_owner>     an object changed owner (boarding, capture, claim)
//   M;<value>                                 the player's money changed to <value>
//   S;<component>                             a boarding operation started on that ship
//   E;<component>                             a boarding operation ended on that ship
//
// A component is what MD prints when an object is concatenated into a string ("0x1b1d"), so an id is parsed as hex when it
// starts with 0x and as decimal otherwise - the same rule the capture scan uses. Trailing fields may be missing (an older MD
// half) or extra (a newer one); the parser never fails on either.
//
// Deliberately SDK-free and clock-free: this is the half a Catch2 test pins, and the feature only adds the plumbing.

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace x4mp::features::events {

enum class EventKind : std::uint8_t {
  Unknown = 0,
  HookHello,     // H
  Kill,          // K
  Destroyed,     // X
  PlayerDeath,   // P
  Trade,         // T
  Build,         // B
  Capture,       // C
  Money,         // M
  BoardingStart, // S
  BoardingEnd,   // E
  Count,
};

inline constexpr std::size_t kEventKindCount = static_cast<std::size_t>(EventKind::Count);

// The letters above, as a table the log line and the tests can walk.
[[nodiscard]] inline std::string_view to_string(EventKind kind) noexcept {
  switch (kind) {
    case EventKind::HookHello: return "hello";
    case EventKind::Kill: return "kill";
    case EventKind::Destroyed: return "destroyed";
    case EventKind::PlayerDeath: return "player_death";
    case EventKind::Trade: return "trade";
    case EventKind::Build: return "build";
    case EventKind::Capture: return "capture";
    case EventKind::Money: return "money";
    case EventKind::BoardingStart: return "boarding_start";
    case EventKind::BoardingEnd: return "boarding_end";
    case EventKind::Unknown: break;
    case EventKind::Count: break;
  }
  return "unknown";
}

// One parsed occurrence. Small and copyable: the bus queues these, and a wave that needs a field the wire carries does not
// re-parse the text.
struct MdEvent {
  EventKind kind = EventKind::Unknown;
  std::uint64_t component = 0;  // the subject: victim, station, captured ship, boarding target
  std::uint64_t other = 0;      // killer component, or the old owner id
  std::uint64_t third = 0;      // the new owner id (Capture only)
  std::string macro;            // ship/station macro, or the ware macro for Trade
  std::string owner;            // owner id exactly as MD prints it (may be empty)
  std::string sector;           // sector macro (Build only)
  std::int64_t amount = 0;      // signed trade units, or the money value
  std::int64_t price = 0;       // unit price (Trade only)
  std::int64_t at_ms = 0;       // monotonic milliseconds when the bus accepted it
};

// "0x1b1d" -> 0x1b1d, "18197" -> 18197, anything else -> false. Empty and oversized inputs are false, never 0.
[[nodiscard]] inline bool parse_id(std::string_view text, std::uint64_t& out) noexcept {
  if (text.empty() || text.size() > 18) return false;
  std::uint64_t value = 0;
  std::size_t index = 0;
  unsigned base = 10;
  if (text.size() > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) {
    base = 16;
    index = 2;
  }
  if (index >= text.size()) return false;
  for (; index < text.size(); ++index) {
    const char c = text[index];
    unsigned digit = 0;
    if (c >= '0' && c <= '9') digit = static_cast<unsigned>(c - '0');
    else if (base == 16 && c >= 'a' && c <= 'f') digit = static_cast<unsigned>(c - 'a') + 10;
    else if (base == 16 && c >= 'A' && c <= 'F') digit = static_cast<unsigned>(c - 'A') + 10;
    else return false;
    value = value * base + digit;
  }
  out = value;
  return true;
}

namespace detail {

inline constexpr std::size_t kMaxFields = 7;

// Splits on ';' into at most kMaxFields views. Returns the number written; a line with more fields keeps the first
// kMaxFields and ignores the rest, which is what "trailing fields may be extra" means in practice.
[[nodiscard]] inline std::size_t split_fields(std::string_view line, std::string_view (&out)[kMaxFields]) noexcept {
  std::size_t count = 0;
  std::size_t start = 0;
  while (count < kMaxFields) {
    const std::size_t sep = line.find(';', start);
    if (sep == std::string_view::npos) {
      out[count++] = line.substr(start);
      break;
    }
    out[count++] = line.substr(start, sep - start);
    start = sep + 1;
  }
  return count;
}

[[nodiscard]] inline bool to_i64(std::string_view text, std::int64_t& out) noexcept {
  if (text.empty() || text.size() > 20) return false;
  bool negative = false;
  std::size_t index = 0;
  if (text[0] == '-') {
    negative = true;
    index = 1;
    if (text.size() == 1) return false;
  }
  std::int64_t value = 0;
  for (; index < text.size(); ++index) {
    const char c = text[index];
    if (c < '0' || c > '9') return false;
    value = value * 10 + (c - '0');
  }
  out = negative ? -value : value;
  return true;
}

}  // namespace detail

// Reads one line. A kind letter we do not know is a false return, not an exception: a newer MD half must not break an older
// DLL. Fields a kind does not use are left at their defaults.
[[nodiscard]] inline bool parse_event_line(std::string_view line, MdEvent& out) {
  out = MdEvent{};
  if (line.size() < 2) return false;
  std::string_view fields[detail::kMaxFields];
  const std::size_t count = detail::split_fields(line, fields);
  if (count < 1 || fields[0].size() != 1) return false;

  const auto need = [count](std::size_t wanted) noexcept { return count >= wanted; };

  switch (fields[0][0]) {
    case 'H':
      if (!need(2)) return false;
      out.kind = EventKind::HookHello;
      if (!detail::to_i64(fields[1], out.amount)) return false;
      return true;
    case 'K':
      if (!need(5)) return false;
      if (!parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::Kill;
      out.macro.assign(fields[2]);
      out.owner.assign(fields[3]);
      if (!fields[4].empty() && !parse_id(fields[4], out.other)) return false;
      return true;
    case 'X':
      if (!need(2) || !parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::Destroyed;
      return true;
    case 'P':
      out.kind = EventKind::PlayerDeath;
      return true;
    case 'T':
      if (!need(5)) return false;
      if (!parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::Trade;
      out.macro.assign(fields[2]);
      if (!detail::to_i64(fields[3], out.amount)) return false;
      if (!detail::to_i64(fields[4], out.price)) return false;
      return true;
    case 'B':
      if (!need(4) || !parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::Build;
      out.macro.assign(fields[2]);
      out.sector.assign(fields[3]);
      return true;
    case 'C':
      if (!need(4) || !parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::Capture;
      // An owner is an id when MD prints one ("0x1b1d"); a name ("argon", "player") is kept as text rather than failing the
      // line, because losing a capture because the MD half printed a faction instead of a component would be worse than a 0 id.
      if (!fields[2].empty() && !parse_id(fields[2], out.other)) out.owner.assign(fields[2]);
      if (!fields[3].empty() && !parse_id(fields[3], out.third)) out.macro.assign(fields[3]);
      return true;
    case 'M':
      if (!need(2)) return false;
      out.kind = EventKind::Money;
      return detail::to_i64(fields[1], out.amount);
    case 'S':
      if (!need(2) || !parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::BoardingStart;
      return true;
    case 'E':
      if (!need(2) || !parse_id(fields[1], out.component)) return false;
      out.kind = EventKind::BoardingEnd;
      return true;
    default:
      return false;
  }
}

}  // namespace x4mp::features::events
