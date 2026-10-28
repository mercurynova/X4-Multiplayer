#pragma once
// features/stats/budget_line: the ONE aggregated 30 s line of a session (M4-30; docs/m4-plan.md W7).
//
// The 5 s `stats:` line answers "is the mod healthy right now". This one answers the M4 acceptance question, once per 30 s, in a form a
// person (and docs/m4-exit-report.md) can quote:
//
//   budget: fps=... frame_ms=.../... mod_ms=.../.../... authority_ms=.../.../... clients=N ghosts=N suppressed=N
//           rx=...B/s tx=...B/s per_client=...B/s udp=active
//
//   fps, frame_ms        the client frame rate and its p50/p95 over the window
//   mod_ms               the whole mod's main-thread cost, p50/p95/max
//   authority_ms         the authority flow's step(), p50/p95/max - the "< 3 ms/frame" number of MOD 9 M4
//   clients              other players in the roster (self excluded)
//   ghosts               ghost records this node holds (0 on the authority)
//   suppressed           local NPCs the suppression pass has removed so far (0 on the authority)
//   rx/tx                bytes per second this node moves over TCP + UDP
//   per_client           tx / clients: what ONE client would pull if every client got the whole stream. The server fans out by interest,
//                        so a real client gets this or less - the number is an upper bound and is labelled as such in the docs
//   udp                  the realtime lane's state (off/binding/active/fallback)
//   hooks                the M5 `md_hook` block (M5-03): `hooks=alive md=9 fired=41 age=1.2s fb=2 dup=0 queued=0`, see
//                        features/events/hook_state.h. It rides here rather than in a line of its own so that "the MD half is dead" and
//                        "the node is idle" can never be confused: both are visible in one line.
//
// SDK-free and clock-free: the feature fills the struct, this header formats it, so a Catch2 test pins the exact field order that
// sync-report.ps1 and the exit report read.

#include <cstdint>
#include <format>
#include <string>

namespace x4mp::features::stats {

struct BudgetSample {
  float fps = 0.0f;
  float frame_ms_p50 = 0.0f;
  float frame_ms_p95 = 0.0f;
  float mod_ms_p50 = 0.0f;
  float mod_ms_p95 = 0.0f;
  float mod_ms_max = 0.0f;
  double authority_ms_p50 = 0.0;
  double authority_ms_p95 = 0.0;
  double authority_ms_max = 0.0;
  std::uint32_t clients = 0;
  std::uint32_t ghosts = 0;
  std::uint32_t suppressed = 0;
  std::uint32_t rx_bytes_per_s = 0;
  std::uint32_t tx_bytes_per_s = 0;
  std::uint32_t per_client_bytes_per_s = 0;
  const char* udp_state = "off";  // a literal, never a std::string: the struct is copied per frame
  // M5-03: the formatted `hooks=` block. A std::string, unlike udp_state, because this struct is built once per 30 s (not per
  // frame) and the block has a variable number of fields.
  std::string hooks;
  double window_s = 30.0;
};

// tx split over `clients`; 0 when nobody else is in the session (an idle node has no per-client cost to report).
[[nodiscard]] inline std::uint32_t per_client_rate(std::uint32_t tx_bytes_per_s, std::uint32_t clients) noexcept {
  return clients == 0 ? 0 : tx_bytes_per_s / clients;
}

[[nodiscard]] inline std::string format_budget(const BudgetSample& s) {
  return std::format(
      "budget: fps={:.1f} frame_ms={:.2f}/{:.2f} mod_ms={:.3f}/{:.3f}/{:.3f} authority_ms={:.3f}/{:.3f}/{:.3f} clients={} ghosts={} suppressed={} "
      "rx={}B/s tx={}B/s per_client={}B/s udp={} hooks={}",
      s.fps, s.frame_ms_p50, s.frame_ms_p95, s.mod_ms_p50, s.mod_ms_p95, s.mod_ms_max, s.authority_ms_p50, s.authority_ms_p95, s.authority_ms_max,
      s.clients, s.ghosts, s.suppressed, s.rx_bytes_per_s, s.tx_bytes_per_s, s.per_client_bytes_per_s, s.udp_state != nullptr ? s.udp_state : "off",
      s.hooks.empty() ? "hooks=dead md=0 fired=0 age=none fb=0 dup=0 queued=0" : s.hooks.c_str());
}

}  // namespace x4mp::features::stats
