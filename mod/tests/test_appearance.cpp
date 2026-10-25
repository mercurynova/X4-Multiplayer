// M3b: features/appearance, the pure parts (ADR-051, docs/research/on-foot-presence.md 3.2): the curated catalogue the picker
// offers and the feature validates against, the resolution of a race + preset pair into the character macro MD spawns, the
// deterministic "Random" and the JSON the picker reads. The feature itself (bridge verb, config write, PlayerAppearance) is
// exercised end to end by the hostsim kit and by the in-game two-client check.
#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <string_view>
#include <utility>

#include <nlohmann/json.hpp>

#include "features/appearance/appearance_catalogue.h"

using namespace x4mp::features::appearance;

namespace {

std::size_t look_count() {
  std::size_t n = 0;
  for (const auto& race : races()) n += race.count;
  return n;
}

}  // namespace

TEST_CASE("appearance: the catalogue lists every race with its presets", "[appearance][catalogue]") {
  const auto& all = races();
  REQUIRE(all.size() == 5);
  // Picker order: argon first (the default team origin), then the other races the base game ships.
  CHECK(all.front().id == "argon");
  std::set<std::string_view> races_seen;
  std::set<std::string_view> macros_seen;
  for (const auto& race : all) {
    INFO("race " << race.id);
    CHECK_FALSE(race.id.empty());
    CHECK(race.count >= 1);
    CHECK(race.presets != nullptr);
    CHECK(races_seen.insert(race.id).second);
    std::set<std::string_view> presets_seen;
    for (std::size_t i = 0; i < race.count; ++i) {
      const auto& preset = race.presets[i];
      INFO("preset " << preset.id);
      CHECK_FALSE(preset.id.empty());
      CHECK(presets_seen.insert(preset.id).second);
      // A macro the build does not have spawns a broken actor (S10.7), so every entry must look like a real character macro.
      CHECK(preset.macro.starts_with("character_"));
      CHECK(preset.macro.ends_with("_macro"));
      CHECK(preset.macro.find(race.id) != std::string_view::npos);
      CHECK(macros_seen.insert(preset.macro).second);
    }
  }
  // The base catalogue has no character_terran_* macro (DLC), so terran is deliberately not offered.
  CHECK(races_seen.count("terran") == 0);
  CHECK(look_count() == 12);
}

TEST_CASE("appearance: a known race and preset resolve to the character macro", "[appearance][catalogue]") {
  const auto argon = resolve("argon", "female_cau");
  CHECK(argon.race == "argon");
  CHECK(argon.preset == "female_cau");
  CHECK(argon.macro == "character_argon_female_cau_civ_01_macro");
  CHECK(argon.is_female);
  CHECK_FALSE(argon.empty());

  const auto teladi = resolve("teladi", "male");
  CHECK(teladi.macro == "character_teladi_male_generic_01_macro");
  CHECK_FALSE(teladi.is_female);

  const auto split = resolve("split", "female");
  CHECK(split.macro == "character_split_female_generic_01_macro");
  CHECK(split.is_female);

  // Paranid and boron have no gender split, so their single preset is not female.
  const auto paranid = resolve("paranid", "generic");
  CHECK(paranid.macro == "character_paranid_generic_01_macro");
  CHECK_FALSE(paranid.is_female);
  const auto boron = resolve("boron", "generic");
  CHECK(boron.macro == "character_boron_generic_01_macro");
  CHECK_FALSE(boron.is_female);
}

TEST_CASE("appearance: an unknown race or preset is the default, not an error", "[appearance][catalogue]") {
  // Both empty is what a fresh x4mp.json says: the server keeps its own default (the team origin, ADR-049).
  CHECK(resolve("", "").empty());
  CHECK(resolve("argon", "").empty());
  CHECK(resolve("", "male_cau").empty());
  CHECK(resolve("vulcan", "male").empty());
  // A preset of another race is not a look: the macro would belong to a different body.
  CHECK(resolve("argon", "male").empty());
  CHECK(resolve("teladi", "female_cau").empty());
  // Case and whitespace are not normalised on purpose - the ids on the wire are exact.
  CHECK(resolve("Argon", "female_cau").empty());
  CHECK(resolve("argon", "female_cau ").empty());
  CHECK_FALSE(resolve("argon", "female_cau").empty());
}

TEST_CASE("appearance: a random pick is deterministic and covers every look", "[appearance][catalogue]") {
  // The picker re-renders on every frame of the menu; the same seed must not shuffle under the player's cursor.
  CHECK(pick_random(0) == pick_random(0));
  CHECK(pick_random(12345) == pick_random(12345));

  std::set<std::pair<std::string, std::string>> seen;
  for (std::uint32_t seed = 0; seed < 4096; ++seed) {
    const auto choice = pick_random(seed);
    INFO("seed " << seed);
    REQUIRE_FALSE(choice.empty());
    // Whatever comes out of Random must be a look the catalogue can resolve again.
    CHECK(resolve(choice.race, choice.preset) == choice);
    seen.emplace(choice.race, choice.preset);
  }
  CHECK(seen.size() == look_count());
}

TEST_CASE("appearance: describe names the look or says default", "[appearance][catalogue]") {
  CHECK(describe(Choice{}) == "default");
  CHECK(describe(resolve("argon", "female_cau")) == "argon/female_cau");
  CHECK(describe(resolve("boron", "generic")) == "boron/generic");
}

TEST_CASE("appearance: the catalogue json is the picker's shape", "[appearance][catalogue]") {
  const auto parsed = nlohmann::json::parse(catalogue_json());
  REQUIRE(parsed.contains("races"));
  const auto& list = parsed["races"];
  REQUIRE(list.is_array());
  REQUIRE(list.size() == races().size());

  std::size_t presets_total = 0;
  for (std::size_t i = 0; i < list.size(); ++i) {
    const auto& race = list[i];
    INFO("race " << i);
    CHECK(race["id"].get<std::string>() == races()[i].id);
    REQUIRE(race.contains("presets"));
    const auto& presets = race["presets"];
    REQUIRE(presets.is_array());
    CHECK(presets.size() == races()[i].count);
    for (const auto& preset : presets) {
      // Labels are the Lua side's job (text page 92000), so no English may leak into the data.
      CHECK(preset.contains("id"));
      CHECK(preset.contains("female"));
      CHECK_FALSE(preset.contains("label"));
      CHECK(preset["female"].is_boolean());
      ++presets_total;
    }
  }
  CHECK(presets_total == look_count());
}
