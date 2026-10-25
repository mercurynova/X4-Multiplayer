#include "features/appearance/appearance_catalogue.h"

namespace x4mp::features::appearance {

namespace {

// Argon: three ethnicities x two genders, the fullest set the base game ships for a civilian look.
constexpr Preset kArgon[] = {
    {"male_cau", "character_argon_male_cau_civ_01_macro", false},
    {"female_cau", "character_argon_female_cau_civ_01_macro", true},
    {"male_afr", "character_argon_male_afr_civ_01_macro", false},
    {"female_afr", "character_argon_female_afr_civ_01_macro", true},
    {"male_asi", "character_argon_male_asi_civ_01_macro", false},
    {"female_asi", "character_argon_female_asi_civ_01_macro", true},
};
constexpr Preset kTeladi[] = {
    {"male", "character_teladi_male_generic_01_macro", false},
    {"female", "character_teladi_female_generic_01_macro", true},
};
// Paranid and Boron have no gender split in the character macros: one preset each.
constexpr Preset kParanid[] = {{"generic", "character_paranid_generic_01_macro", false}};
constexpr Preset kSplit[] = {
    {"male", "character_split_male_generic_01_macro", false},
    {"female", "character_split_female_generic_01_macro", true},
};
constexpr Preset kBoron[] = {{"generic", "character_boron_generic_01_macro", false}};

template <std::size_t N>
constexpr Race race_of(std::string_view id, const Preset (&presets)[N]) {
  return Race{id, presets, N};
}

}  // namespace

const std::vector<Race>& races() {
  static const std::vector<Race> table = {
      race_of("argon", kArgon), race_of("teladi", kTeladi), race_of("paranid", kParanid),
      race_of("split", kSplit), race_of("boron", kBoron),
  };
  return table;
}

const Race* find_race(std::string_view id) {
  if (id.empty()) return nullptr;
  for (const Race& race : races()) {
    if (race.id == id) return &race;
  }
  return nullptr;
}

const Preset* find_preset(const Race& race, std::string_view id) {
  if (id.empty()) return nullptr;
  for (std::size_t i = 0; i < race.count; ++i) {
    if (race.presets[i].id == id) return &race.presets[i];
  }
  return nullptr;
}

Choice resolve(std::string_view race, std::string_view preset) {
  const Race* found = find_race(race);
  if (found == nullptr) return {};
  const Preset* look = find_preset(*found, preset);
  if (look == nullptr) return {};
  Choice choice;
  choice.race.assign(found->id);
  choice.preset.assign(look->id);
  choice.macro.assign(look->macro);
  choice.is_female = look->is_female;
  return choice;
}

Choice pick_random(std::uint32_t seed) {
  // One flat list of every (race, preset) pair keeps the pick uniform over looks, not over races: argon, with six looks,
  // would otherwise be as likely as boron with one.
  std::uint32_t total = 0;
  for (const Race& race : races()) total += static_cast<std::uint32_t>(race.count);
  if (total == 0) return {};
  // xorshift32: no <random>, no allocation, and the same seed always gives the same look on every platform.
  std::uint32_t x = seed != 0 ? seed : 0x9e3779b9u;
  x ^= x << 13;
  x ^= x >> 17;
  x ^= x << 5;
  std::uint32_t index = x % total;
  for (const Race& race : races()) {
    if (index < race.count) return resolve(race.id, race.presets[index].id);
    index -= static_cast<std::uint32_t>(race.count);
  }
  return {};
}

std::string describe(const Choice& choice) {
  if (choice.empty()) return "default";
  std::string out;
  out.reserve(choice.race.size() + choice.preset.size() + 1);
  out += choice.race;
  out += '/';
  out += choice.preset;
  return out;
}

std::string catalogue_json() {
  std::string out = "{\"races\":[";
  bool first_race = true;
  for (const Race& race : races()) {
    if (!first_race) out += ',';
    first_race = false;
    out += "{\"id\":\"";
    out += race.id;
    out += "\",\"presets\":[";
    for (std::size_t i = 0; i < race.count; ++i) {
      if (i != 0) out += ',';
      out += "{\"id\":\"";
      out += race.presets[i].id;
      out += "\",\"female\":";
      out += race.presets[i].is_female ? "true" : "false";
      out += '}';
    }
    out += "]}";
  }
  out += "]}";
  return out;
}

}  // namespace x4mp::features::appearance
