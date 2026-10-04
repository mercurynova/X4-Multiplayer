# Vanilla starter ships and loadouts (research for the avatar starter table)

Status: research note, 2026-10-03, written with M3-11. Game build 9.00.611726 data (`libraries/gamestarts.xml`, `libraries/loadouts.xml`, the DLC
`extensions/*/libraries/gamestarts.xml` and `md/gs_*.xml`). **Game data ids only** (macro / loadout / ware ids); no XML is copied. The user note of
2026-10-03 (sitting 0) is the reason for this file: the S13.6 test fighter came with Mk2 weapons and Mk3 engines/shields (the `SpawnObjectAtPos2`
default equipment). Real starter avatars are **early-game ships with early-game equipment**, per race / story, from the vanilla game starts.
The later per-race table (M5, team origins ADR-049 / avatar race ADR-051) is filled from the "ship + loadout" columns below.

## How a vanilla start equips the player ship

* `gamestarts.xml` -> `<gamestart id=..>` -> `<player>` -> `<ship macro="...">` with an **inline** `<loadout><macros>` list: `engine`, `weapon`, `shield`
  (and `turret` for M ships) elements with a `macro` and a `path` (the connection on the ship, e.g. `../con_engine_01`, `../con_primaryweapon_01`,
  `../con_shield_01`), then `<ammunition>`, `<software>` (flight assist, scanners, targeting) and `<virtualmacros>` (the thruster). Starts do **not**
  reference a loadout id; the inline list is the loadout.
* `libraries/loadouts.xml` holds named loadouts (`<loadout id=".." macro="<ship macro>">` with the same sub-elements). Ids relevant to starts:
  `x4ep1_gamestart_trade_*`, `x4ep1_gamestart_scientist_nodan`, the `flightschool_*` set, `scenario_basic_fighter` (Argon Nova macro, Mk3 engines: not
  basic), `battle_*`, `scenario_combat_*` (Mk3 gear: **not** early game). None of them is the Elite's start equipment, so the Elite's basic start loadout
  is carried by `md/x4mp_avatars.xml` (see below).
* MD can apply either form to a ship that already exists: `get_loadout loadout="<id>" macro=<ship macro> result=..` (a loadouts.xml id) or
  `create_loadout` with a fixed `<macros>/<software>/<virtualmacros>` element tree (no script expressions allowed inside), then
  `apply_loadout object=<ship> loadout=<value>`. `create_ship` also accepts a `<loadout loadout=".."/>` child. M3-11 spawns natively
  (`SpawnObjectAtPos2`, sitting-0 S13.1) and applies the loadout afterwards through MD (`apply_loadout`), in the same MD cue that sets the name and min hull.

## What the starts give (base game)

| Start id | Race / story | Ship macro | Engine / shield / weapons (inline loadout macro ids) |
|---|---|---|---|
| `x4ep1_gamestart_intro` | Argon (Juro Topeka; "Intro") | `ship_arg_s_fighter_01_a_macro` (Argon Elite) | `engine_arg_s_allround_01_mk2_macro` x2, `shield_arg_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` x2, thruster `thruster_gen_s_allround_01_mk1_macro` |
| `x4ep1_gamestart_tutorial` | Argon (Val Selton) | `ship_arg_s_fighter_02_a_macro` | `engine_arg_s_allround_01_mk1_macro`, `shield_arg_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` |
| `x4ep1_gamestart_trade` | Teladi (trader) | `ship_tel_s_scout_01_a_macro` | `engine_tel_s_travel_01_mk1_macro`, `shield_tel_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` |
| `x4ep1_gamestart_fight` | Paranid (fighter) | `ship_par_s_fighter_01_a_macro` | `engine_par_s_combat_01_mk1_macro`, `shield_par_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro`, `weapon_gen_s_guided_02_mk1_macro` |
| `x4ep1_gamestart_discover` | Argon scout with Paranid/Teladi parts | `ship_arg_s_scout_01_a_macro` | `engine_par_s_travel_01_mk1_macro`, `shield_tel_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` |
| `custom_budgeted`, `custom_creative` | Custom start | `ship_arg_s_scout_01_a_macro` | `engine_arg_s_travel_01_mk2_macro`, `shield_arg_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` |
| `x4ep1_gamestart_scientist`, `x4ep1_gamestart_boso` | Story starts (ship given in MD) | see `loadouts.xml` id `x4ep1_gamestart_scientist_nodan` (macro `ship_gen_s_fighter_01_a_macro`: `engine_par_s_travel_01_mk2_macro` x3, `shield_par_s_standard_01_mk2_macro`, `weapon_tel_s_charge_01_mk1_macro` x2) | (not an early-game loadout: Mk2) |
| `x4ep1_gamestart_tutorial1` | Tutorial (Paranid ship) | `ship_par_s_fighter_01_a_macro` | no inline loadout in the library |

Reading: "early game" = Mk1 shields / lasers and Mk1-Mk2 engines, one tier of weapon (`weapon_gen_s_laser_01_mk1_macro`), the standard shield
(`shield_<race>_s_standard_01_mk1_macro`), no missiles but the Paranid start.

## DLC starts

| Start id | Race / story | Ship macro | Equipment macro ids |
|---|---|---|---|
| `x4ep1_gamestart_boron1` (Boron) | Boron | `ship_arg_s_scout_01_a_macro` (the library names an Argon scout) | `engine_par_s_travel_01_mk1_macro`, `shield_tel_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` |
| `x4ep1_gamestart_boron2` | Boron (story 2) | chosen in MD (`md/gs_boron1.xml` creates the player ship from `$PlayerShipMacro`) | MD script |
| `x4ep1_gamestart_pirate1` | Pirate | `ship_pir_s_trans_container_01_a_macro` | `engine_arg_s_allround_01_mk1_macro`, `shield_arg_s_standard_01_mk1_macro`, `weapon_gen_s_laser_01_mk1_macro` |
| `x4ep1_gamestart_pirate2` | Pirate (story 2) | MD script | MD script |
| `x4ep1_gamestart_split1` | Split (Vendetta prologue) | `ship_spl_xs_spacesuit_01_a_macro` (starts in a spacesuit; the pick-up ship in `md/gs_split1.xml` is `ship_spl_s_scout_01_a_macro`) | none inline |
| `x4ep1_gamestart_split2` | Split | `ship_spl_s_fighter_02_a_macro` | `engine_spl_s_combat_01_mk1_macro`, `shield_spl_s_standard_01_mk1_macro`, `weapon_spl_s_shotgun_01_mk1_macro`, `weapon_gen_s_guided_02_mk1_macro` |
| `x4ep1_gamestart_terran1` | Terran | `ship_ter_s_fighter_01_a_macro` | `engine_ter_s_allround_01_mk1_macro`, `shield_ter_s_standard_01_mk2_macro`, `weapon_ter_s_laser_01_mk1_macro` |
| `x4ep1_gamestart_terran2` | Terran (story 2) | MD script | MD script |
| `x4ep1_gamestart_hyperion` | Hyperion (mini DLC 1) | `ship_par_l_expeditionary_01_a_macro` (a large ship: not an avatar candidate) | none inline |
| `x4ep1_gamestart_dlc_mini_02` | Mini DLC 2 | `ship_tel_m_bomber_01_b_macro` (M ship) | none inline |

Timelines DLC scenarios (`scenario_*`) use Mk2/Mk3 gear and are not starts of a campaign; they are not used for the avatar table.

## What M3-11 does with this

* The ship macro and the loadout id come from **one function**, `resolve_starter()` in `mod/native/features/avatars/avatar_plan.cpp`, fed by the server settings
  `Avatars.StarterShipMacro` (default `ship_arg_s_fighter_01_a_macro`, the Argon Elite, user answer Q5) and `Avatars.StarterLoadout` (a loadouts.xml id,
  empty = the basic early-game loadout). A later milestone changes only that function (team origin / race table from the rows above).
* **Basic early-game loadout of the Elite** (`md/x4mp_avatars.xml`, `create_loadout` + `apply_loadout`): the `x4ep1_gamestart_intro` gear with **Mk1** engines:
  `engine_arg_s_allround_01_mk1_macro` on `../con_engine_01` and `../con_engine_02`, `weapon_gen_s_laser_01_mk1_macro` on `../con_primaryweapon_01` and
  `../con_primaryweapon_02`, `shield_arg_s_standard_01_mk1_macro` on `../con_shield_01`, thruster `thruster_gen_s_allround_01_mk1_macro`, software
  `software_flightassistmk1`, `software_scannerlongrangemk1`, `software_scannerobjectmk1`, `software_targetmk1`. Other ship macros get no built-in loadout until the
  table exists: they keep the spawn default, unless `Avatars.StarterLoadout` names a loadout for them (a warning is logged).
* Not verified in game (add to the next sitting): that `apply_loadout` on a `SpawnObjectAtPos2` ship swaps the default Mk2/Mk3 parts out; that the
  connection paths of the Elite above are right in 9.00 (they are the ones of the vanilla intro start).
