#pragma once
// features/appearance/appearance_catalogue: the curated looks a player may pick for their avatar (ADR-051, variant B).
//
// The wire carries race + preset + character_macro (protocol/schema/world.fbs PlayerAppearance). This catalogue is the only
// source of those three strings on a node: the picker offers what it lists, the feature validates what it receives, and MD
// spawns the macro it names. Curated instead of read from the install on purpose - a macro the running build does not have
// would spawn a broken actor, and every node has to agree on the list anyway (docs/research/on-foot-presence.md 3.2).
//
// v1 lists what the base game ships as a plain character macro: <create_cue_actor macro="character_..."> is the vanilla way
// (md/scenario_tutorials.xml:18520), while <select race=...> honours neither race nor gender (S10.7, session-5 results).
// Gender and ethnicity are the variants the game exposes per race; a race without a gender split has a single preset.
//
// Pure data and pure functions: no game calls, no allocation in the read path, no I/O. Unit-tested in tests/test_appearance.cpp.

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace x4mp::features::appearance {

struct Preset {
  std::string_view id;     // "female_cau" - wire value, stable
  std::string_view macro;  // "character_argon_female_cau_civ_01_macro"
  bool is_female = false;
};

struct Race {
  std::string_view id;  // "argon" - wire value, stable
  const Preset* presets = nullptr;
  std::size_t count = 0;
};

// What this client renders as on the other nodes. Every field empty = "default": the game picks a crew NPC and the server
// keeps its own default (the team origin, ADR-049; not decided on the client).
struct Choice {
  std::string race;
  std::string preset;
  std::string macro;
  bool is_female = false;
  [[nodiscard]] bool empty() const noexcept { return race.empty() && preset.empty() && macro.empty(); }
  bool operator==(const Choice&) const = default;
};

// The races in picker order.
[[nodiscard]] const std::vector<Race>& races();
[[nodiscard]] const Race* find_race(std::string_view id);
[[nodiscard]] const Preset* find_preset(const Race& race, std::string_view id);

// A known race + a preset of that race give the look; anything else (unknown id, empty, preset of another race) gives an empty
// Choice, i.e. "default". Never throws, never logs: the caller decides what an unknown pair means.
[[nodiscard]] Choice resolve(std::string_view race, std::string_view preset);

// A deterministic pick over the whole catalogue (the picker's "Random"). The same seed gives the same look, so a menu that
// re-renders does not shuffle under the player's cursor.
[[nodiscard]] Choice pick_random(std::uint32_t seed);

// "argon/female_cau", or "default". For logs and diagnostics, never for the wire.
[[nodiscard]] std::string describe(const Choice& choice);

// The catalogue as the picker's JSON: {"races":[{"id":"argon","presets":[{"id":"female_cau","female":true}]}]}. Labels are the
// Lua side's job (text page 92000), so no English leaks into the data.
[[nodiscard]] std::string catalogue_json();

}  // namespace x4mp::features::appearance
