# Research: X4 library mods as dependencies for X4MP

Status: research note, 2026-10-01. Nothing here is decided until the lead takes it into
`decisions.md` and `mod-design.md`. Anything not confirmed from source or a primary page is
marked **[UNVERIFIED]**.

Scope: two widely used X4 "library" mods, judged as possible dependencies of, or reference
material for, X4MP. The judgement uses our constraints: X4Native as the only hard dependency,
the pinned build 9.00 / 611726 (ADR-004), Windows-first (ADR-003), Protected UI Mode off
(PIT-003), and the `extensions_hash` handshake (protocol §4.2).

Sources were read at these commits (cloned with `git clone --depth`):

| Repo | Commit | Date |
|---|---|---|
| https://github.com/bvbohnen/x4-projects (SirNukes) | `ba199a43edfb046163a9f35a4ba8811b0933402b` | 2025-12-08 |
| https://github.com/kuertee/x4-mod-ui-extensions (kuertee) | `e3d419eb22126b8c465d1faa690b7c522abe9a64` | 2026-09-11 |

In this note, paths that start with `sn/` mean `x4-projects/extensions/sn_mod_support_apis/`,
and paths that start with `uix/` mean the root of `x4-mod-ui-extensions/`. Line numbers refer
to the commits above.

---

## 1. Summary and recommendations

| | **SirNukes Mod Support APIs** | **kuertee UI Extensions and HUD (UIX)** |
|---|---|---|
| Recommendation | **(c) Reference only.** MIT, so we may reuse small pieces with attribution. Plus **mandatory coexistence work** (§4), because many players will have it installed. | **(b) Optional, detected at runtime, narrow use:** only `OptionsMenu.uix_getConfig()` as an extra source for the options-menu `config` table. No `content.xml` dependency, no install step, no use of its HUD or callbacks. |
| License | **MIT** (`x4-projects/LICENSE`, "Copyright (c) 2019,2020 Brent Bohnenstiehl"). We could redistribute it, but we have no reason to. | **No license file**; GitHub reports no license. The Nexus permissions are said to be "not allowed to upload this file to other sites", with asset reuse allowed with credit (from a search-result summary, **[UNVERIFIED]** on the page itself). **Do not redistribute.** Players must install it themselves. |
| Content id (for `<dependency id=…>`) | `ws_2042901274` on every channel, because the id is hard-coded in `sn/content.xml:3` | **Two ids.** Nexus/GitHub: `kuerteeUIExtensionsAndHUD` (`uix/content.xml:2`). Steam Workshop repack (by Valador, with permission): `ws_3477279743`. |
| Latest release | 1.95 (`sn/content.xml:7` `version="195"`); last commit 2025-12-08; Steam updated Dec 8 2025 | v9.0.0.14, 2026-09-11 (`uix/README.md:11`); Steam repack v9.0.0.9, updated Aug 10 (2026) |
| X4 9.00 | **No 9.00 release.** The last update predates 9.00 (released 2026-06-10). Third-party "(FIXED 9.0)" bug-fix patch on Nexus (#2373). Open issue #34 (2026-09-14): "Extensions menu disappearing". **[UNVERIFIED]** on 611726. | **Yes.** "v9.0.0.1, 11 Jun 2026: Compatibility: 9.0" (`uix/README.md:392`). Its replacement files match our `x4-unpacked/` 9.00 Lua apart from UIX's own blocks (§3.3). |
| Reaches menu internals by | Monkey-patching public `menu.*` functions found through `Menus` (no `debug` library). Also replaces the global `require` (`sn/ui/lua_loader.lua:181`). | Replacing 22 vanilla UI Lua files wholesale (`subst_01.cat/.dat`). The replacements add `menu.uix_getConfig()` and `menu.registerCallback()`. No `debug` library. |
| Patches vanilla UI files? | No file replacement. It does runtime patching of `OptionsMenu`, `ChatWindow`, `InteractMenu` and several `Online*` globals. | **Yes, full replacement** of gameoptions, helper, toplevel, map, interact menu and more (`uix/subst_01.cat`) |
| Protected UI Mode | Mostly works with it on. The pipes and hotkeys need it **off** (`sn/ui/c_library/winpipe.lua:44`). | The author recommends it **on** (`uix/README.md:22-36`); it works either way. X4MP needs it off regardless. |
| Usage | Steam: **77,598 subscribers**, 108,240 visitors. Nexus #503: about 2,931 endorsements (search snippet). Also a pre-8.0 "Community Edition" fork (Steam 3514258146, 2,161 subs, deprecated). | Steam repack: **18,098 subscribers**. Nexus #552 download count **[UNVERIFIED]** (Cloudflare blocked the fetch). |
| Mods requiring it | Many. GitHub code search finds `ws_2042901274` in content.xml of DeadAir scripts and dynamic wars, Warehouse Fleets, Reactive Docking, Gunnery Control and others. | Many of kuertee's own mods, plus Warehouse Fleets (hard), Reactive Docking (optional), Gunnery Control (optional), UniTrader Advanced Renaming, ChemODun's mods |

**V20 answer.** Neither mod uses the Lua `debug` library. No `getupvalue`, `setupvalue` or
`require("debug")` appears anywhere in either tree (grep over all `.lua`/`.xpl`/`.txt` files).
So neither one gives evidence for or against `require("debug")` on 9.00. Each avoids the need
for it in its own way:

- **kuertee** owns the file, so it exposes the private table directly:
  `function menu.uix_getConfig() return config end` (`uix/ui/addons/ego_gameoptions/gameoptions.xpl:2952`).
- **SirNukes** does not touch `config`. It wraps `menu.displayOptions`, captures the frame by
  temporarily swapping `menu.createOptionsFrame`, and appends a row with the public
  `menu.displayOption(ftable, option)` (`sn/ui/simple_menu/options_menu.lua:95-216`).

The only primary evidence for `require("debug")` is still X4Native itself. Its vendored
`x4n_settings_menu.lua:45-56` uses it, and its `docs/EXTENSION_GUIDE.md:619` (eg3r/X4Native
`main`) says that with Protected UI Mode on, "the `debug` library is unavailable and the Lua
injector can't wire up the rows". SirNukes' README (`sn/Readme.md:42`) agrees: Protected UI
Mode "limits the lua 'require' function to a handful of whitelisted modules (eg. 'ffi')".
Since X4MP already requires Protected UI Mode **off**, `require("debug")` remains the expected
path. V20 still has to be retested in session 2 as planned.

**New finding: a real coexistence hazard for §7.2 (and for X4Native's own settings
injector).** SirNukes replaces `OptionsMenu.displayOptions` with a wrapper
(`sn/ui/simple_menu/options_menu.lua:378-379`, run from an on-load init,
`options_menu.lua:735-737`). The wrapper's upvalues are SirNukes locals, and there is **no
`config` upvalue** among them. The Hotkey API patches `displayOptions` too
(`sn/ui/hotkey/interface.lua:247`). So `getupvalue(menu.displayOptions, i)` finds `config`
only if it runs **before** SirNukes' init. SirNukes' init runs after MD signals `Lua_Loader`
Ready (`sn/ui/lua_loader.lua:212-240`, `sn/md/lua_loader.xml:17-27`), which is after the UI
files load. X4Native's injector runs at file load and wins that race. Our adapter must too
(§5.1). This is inferred from source; confirm in game **[UNVERIFIED]**.

---

## 2. SirNukes Mod Support APIs (`sn_mod_support_apis`)

### 2.1 Identity, license, maintenance

- Repo: https://github.com/bvbohnen/x4-projects/tree/master/extensions/sn_mod_support_apis
- **License: MIT** (`x4-projects/LICENSE`, lines 1-3). GitHub's API also reports `MIT`.
  Redistribution and reuse are allowed if the copyright notice and license text are kept.
- `sn/content.xml:3` `id="ws_2042901274"`, `:7` `version="195"`, `:9` `save="false"`,
  `:25` `<dependency version="300" />` (minimum game 3.00 only, with no upper bound).
- Change log (`sn/change_log.md`): 1.91 "Update for x4 8.0, including 7.5 ui loading
  changes"; 1.95 "Fixed chat window api wrongly suppressing some commands". **No 9.00 entry.**
- Last commit 2025-12-08 (`ba199a4`). GitHub release tag `v1.12` is from 2020, and later
  versions ship only through Steam and Nexus.
- Steam Workshop 2042901274: updated Dec 8, 2025; 77,598 subscribers; 108,240 unique visitors;
  6,068 favorites (page fetched 2026-10-01). Nexus #503: about 2,931 endorsements, v1.95
  (search snippet; the page itself was blocked by Cloudflare).
- 9.00 status: no official update. A third-party patch, "(FIXED 9.0) Sir Nukes Mod Support
  APIs Bug Fix Compatibility Patch" (https://www.nexusmods.com/x4foundations/mods/2373),
  claims to fix accumulated bugs such as "No Menus Registered". Open repo issue #34
  (2026-09-14) is "Possible solution for Extensions menu disappearing". The core APIs are
  believed to work on 9.00 **[UNVERIFIED on 611726]**.

### 2.2 How a dependent mod declares it

```xml
<dependency id="ws_2042901274" optional="true" name="SirNukes Mod Support APIs"/>
```
(For example radlinsky/x4-gunnery-control `content.xml:11`, runekn/x4-reactive-docking
`content.xml`. DeadAirRT/deadair_scripts uses `optional="false"`.) Lua files that call its
globals also add `<dependency name="sn_mod_support_apis"/>` in `ui.xml` so that its Lua
loads first (`sn/Readme.md:32-37`). Its own `ui.xml` depends on `ego_chatwindow`,
`ego_detailmonitor`, `ego_gameoptions`, `ego_interactmenu` and `ego_targetmonitor`
(`sn/ui.xml:42-46`).

### 2.3 APIs relevant to X4MP

| Need | SirNukes API | Notes for us |
|---|---|---|
| Main/options menu entry | Simple Menu API adds one **"Extension Options"** row to the main menu and registered submenus under it (`sn/ui/simple_menu/options_menu.lua:155-166`, row placed before Settings at :176-190) | We would be a submenu one level down, not a top-level "Multiplayer" row. The technique is reusable (MIT). |
| Custom menu with text input | Simple Menu API `Create_Menu` (standalone) and `Register_Options_Menu`, widgets `Make_EditBox` with `textHidden` / `encrypted` (`sn/documentation/Simple_Menu_API.md:579-637`) | **MD-driven.** Arguments travel through an MD blackboard var `player.entity.$simple_menu_args` (`Simple_Menu_API.md:9`), so a typed password would pass through MD, and possibly into a save. Unsuitable for the Join dialog. Also documents "x4 is limited to 5 text edit boxes in a single menu" (:583); our Join dialog uses 3. |
| Options (toggles, sliders) | Simple Menu Options API (`Simple_Menu_Options_API.md`), stored in userdata | X4Native's `x4native.json` settings already cover this |
| HUD widgets | none | |
| Chat window | Chat Window API: MD cues `Print`, `Text_Entered`, custom `/commands`. It **replaces the globals** `ExecuteDebugCommand`, `OnlineSendChatMessage`, `OnlineGetChatMessages` and `OnlineGetUserName` (`sn/ui/chat_window/interface.lua:127-146`). | Same technique as our §7.6 (good evidence it works offline: "If not logged in, entering text does nothing", :12-13). It is also a **direct conflict** with §7.6 (§4.2). |
| Hotkeys | Hotkey API: needs the **external Python pipe server** for key capture (`sn/md/hotkey_api.xml:9-10`; Steam page: "requires an external python program") | Not acceptable as a player requirement. X4 input actions or the chat-window hotkey are better. |
| Lua↔MD plumbing | Uses the standard `AddUITriggeredEvent` / `raise_lua_event`; documents a ≥1-frame Lua→MD delay (`Named_Pipes_API.md`, "Operation notes") | Already in x4-api-notes 3.4 |
| Interact (context) menu | Interact Menu API, MD-driven: `md.Interact_Menu_API.Add_Action`, `Get_Actions` (`sn/documentation/Interact_Menu_API.md`). UIX builds its custom-action groups on top of it (`uix/README.md:183-206`). | Could later be used for "Whisper" / "Locate player" on ghosts. Not needed in v1. |
| Named pipes / external process | Named Pipes API: Lua plugin and `winpipe_64.dll` loaded through `package.loadlib` (`sn/ui/c_library/winpipe.lua:34`, guarded by `GetUISafeModeOption() == false` at :44). X4 is the pipe **client**. Optional Python host `X4_Python_Pipe_Server` (pywin32). | See §2.5 |
| Per-frame / time | Time API: `Register_NewFrame_Callback`, `Set_Alarm`, `Set_Frame_Alarm`, and an MD event `Time`/`Frame_Advanced` every frame, even when paused (`Time_API.md`) | X4Native's `on_frame_update` covers this natively, with better latency |
| Lua loader | `Register_Require_Response`, `Register_OnLoad_Init` (`sn/Readme.md:42-55`). Patches the global `require` (`sn/ui/lua_loader.lua:181-199`); the patch falls through to the original for unregistered names. | `require("debug")` passes through unchanged when SirNukes is installed |
| Userdata | Userdata API (`Userdata_API.md`) | We use `<savedvariable storage="userdata">` directly |

### 2.4 How it reaches menu internals

- It finds menus by name in the global `Menus` (`sn/ui/library.lua:13-25`,
  `Get_Egosoft_Menu`).
- **OptionsMenu.** It wraps `displayOptions`, `submenuHandler` and `viewCreated`, and chains
  `onRowChanged`, `onColChanged` and `onSelectElement`
  (`sn/ui/simple_menu/options_menu.lua:370-418`). It never reads `config`. It copies the
  values it needs into its own table instead (`Tables.config`, `options_menu.lua:19`). To add
  the main-menu row, it swaps `menu.createOptionsFrame` during the original
  `displayOptions` call to capture the frame and stop `frame:display()`. It then finds the
  table in `frame.content`, calls `menu.displayOption(ftable, {...})`, moves the row, fixes
  `row.index`, and finally displays (`options_menu.lua:95-216`).
- **ChatWindow.** It replaces only globals and calls `ChatWindow.onChatMessageReceived()` /
  sets `messagesOutdated` (`sn/ui/chat_window/interface.lua:238-241, 286-287`).
- **Online functions.** It wraps several `Online*` globals to hide "Restricted function …
  called from non-verified source" errors that Protected UI Mode triggers when a modded file
  touches them (`options_menu.lua:286-367`). The Readme warns that X4 online features do not
  work with the mod (`sn/Readme.md:3`, change log 1.92).

### 2.5 Named pipes compared with our native DLL

| | SirNukes pipes | X4MP (X4Native + `x4mp.dll`) |
|---|---|---|
| Transport | Windows named pipe, X4 as client; a separate process (Python) does the networking | Winsock TCP/UDP inside the game process |
| Thread model | Lua polls the pipe from the UI frame; results reach MD at least one frame later | Native threads with lock-free queues; game calls on the frame update (mod-design §2.2-2.4) |
| Game API access | Only through Lua/MD | Direct exported functions and MD hooks through X4Native |
| Extra install | Python 3 + pywin32 or a bundled exe. Antivirus false-positive reports (issue #29), pipe ACL "Access Denied" (issue #33) | None beyond the mod |
| Protected UI Mode | Must be off | Must be off |

Conclusion: the pipe path is strictly worse for realtime multiplayer. It is still a sensible
pattern for a later **local** IPC need, for example a desktop launcher talking to the running
game, and x4-api-notes 5.4 already says so.

### 2.6 Risks if we depended on it

- Extra install step for every player; no 9.00 release from the author; a third-party patch
  ecosystem around it.
- It patches the same OptionsMenu and ChatWindow surfaces we patch, so we have to handle it
  anyway (§4).
- MD-driven menus put UI state, including any password, through the MD blackboard.
- Hotkeys need an external Python process.

**Recommendation: (c) reference only.** Borrow the `displayOption` row-append technique as an
upvalue-free injection fallback, with MIT attribution if code is copied (§5.1). Make X4MP
coexist with it (§4).

---

## 3. kuertee UI Extensions and HUD (UIX)

### 3.1 Identity, license, maintenance

- Repo: https://github.com/kuertee/x4-mod-ui-extensions. Nexus:
  https://www.nexusmods.com/x4foundations/mods/552. Steam repack:
  https://steamcommunity.com/workshop/filedetails/?id=3477279743.
- **License: none in the repo.** There is no LICENSE file, and `gh api repos/kuertee/x4-mod-ui-extensions`
  returns a null license. By default that means all rights reserved. Nexus permissions,
  according to a search-result summary: upload to other sites not allowed; asset use allowed
  with credit; not in paid mods **[UNVERIFIED: page blocked by Cloudflare]**. The Steam copy
  exists "with kuertee's permission" (radlinsky/x4-gunnery-control `DEVELOPMENT.md:970-971`).
  **We must not bundle it, and we must not copy its replacement files.** They are also
  derived from Egosoft's own UI code.
- `uix/content.xml:2` `id="kuerteeUIExtensionsAndHUD"`, `:5` `version="900"`,
  `:6` `date="2026-09-11"`, `:7` `save="false"`. Install folder `extensions/kuertee_ui_extensions/`
  (`uix/README.md:37, 321`).
- Very active: v9.0.0.14 on 2026-09-11 (`uix/README.md:11`), with many contributors
  (ChemODun and others), and 9.0 beta and RC merges before release (`uix/README.md:403-448`).
  "v9.0.0.1, 11 Jun 2026: Compatibility: 9.0." (`uix/README.md:392-393`). The Steam repack
  says "FULLY COMPATIBLE WITH X4 9.X" (v9.0.0.9) and keeps a "Legacy 8.X" item
  (3742643872).
- **Matches our pinned Lua.** For files UIX touches lightly, the diff against our
  `x4-unpacked/` 9.00 sources removes no vanilla lines except UIX's own commented-out ones:
  `menu_research` 0 lines, `customgame` 1, `menu_toplevel` 2, `menu_scenario_debriefing` 1,
  `menu_userquestion` 11. So the current UIX is built on the same 9.00 UI files as 611726
  (a sample check, not the full 22 files).

### 3.2 How a dependent mod declares it (the two-id problem)

```xml
<dependency id="kuerteeUIExtensionsAndHUD" version="900" optional="true" name="kuertee UI Extensions and HUD"/>
```
Workshop installs carry the id `ws_3477279743` instead. radlinsky/x4-gunnery-control
`content.xml:4-8` gives the reason for `optional="true"`: "UI Extensions ships from Nexus and
from the Workshop under two different extension ids, so no single id matches every install
that actually has it. A hard dependency disables us for whichever half of users installed the
other one." The same repo's `DEVELOPMENT.md:964-981` adds that WorkshopTool refuses to publish
a mod that depends on a non-Workshop id, even when it is optional. For runtime detection,
other mods scan `GetExtensionList()` for the id (runekn/x4-reactive-docking
`ui/ui_initializer.lua:4-12`).

**Load order.** UIX's Lua replaces base-game files through `subst_01.cat` (`uix/subst_01.cat`,
22 entries). Those files load in the base-game position, before any extension's `ui.xml`
files, whatever the dependency declarations say. Its own `ui.xml` registers no files, only
two `savedvariable`s (`uix/ui.xml:1-11`).

### 3.3 How it reaches menu internals

- **File replacement, no `debug`.** The developer workflow is to edit the "XPL files"
  (plain-text Lua named `.xpl`) and repack the subst catalog (`uix/README.md:43-52`,
  `uix/dev-make-cat-file.bat`). Replaced files (from `uix/subst_01.cat`):
  `ego_gameoptions/gameoptions`, `customgame`; `ego_detailmonitorhelper/helper`;
  `ego_interactmenu/menu_interactmenu`; `ego_targetmonitor/targetmonitor`; and
  `ego_detailmonitor/menu_{map,toplevel,diplomacy,docked,encyclopedia,playerinfo,research,
  scenario_debriefing,scenario_selection,ship_configuration,station_configuration,
  station_overview,trader_blueprintsorlicences,transactionlog,transporter,userquestion}`.
- Each replaced menu gets `menu.uix_callbacks = {}`, `menu.registerCallback(name, fn, id)`,
  `menu.deregisterCallback(...)` (`gameoptions.xpl:404-411, 14575-14640`) and
  `menu.uix_getConfig()` (`gameoptions.xpl:2952`; `menu_toplevel.xpl:23`;
  `menu_map.xpl:1910`; and others). `Helper.registerCallback` is at `helper.xpl:14936`.

### 3.4 APIs relevant to X4MP

| Need | UIX facility | Notes for us |
|---|---|---|
| Main/options menu entry | `OptionsMenu.uix_getConfig()` returns the real `config` (`gameoptions.xpl:2952`), so we can edit `config.optionDefinitions.main` without `debug`. Callbacks `submenuHandler_preDisplayOptions` (:5254-5260) and `submenuHandler_customPage(optionParameter, config)`; if a callback returns true, vanilla rendering is skipped (:5272-5287). | Gives exactly what §7.2 needs, but only when UIX is installed |
| Custom menu with text input | None of its own. You build with vanilla `Helper` widgets. | Our §7.4 plan stands |
| HUD widgets | `kHUD`: register `kHUD_get_is_show_custom_hud` and `kHUD_add_tables(frame)` on `TopLevelMenu`. Shown **only while the top-level menu is collapsed** (`menu_toplevel.xpl:358-400`). | Our §7.5 standalone layer-3 menu does not need it |
| Chat window | Not replaced (`ego_chatwindow` is not in `subst_01.cat`) | No interaction with §7.6 |
| Hotkeys | ChemODun's input-remap callbacks in gameoptions (`remapInput_*`, `displayControls_modifyControlsOrder`, `gameoptions.xpl:5632, 5905, 13079`) | Possible future custom input actions **[UNVERIFIED usefulness]** |
| Lua↔MD plumbing | MD event on mod deactivation: `event_ui_triggered screen='OptionsMenu' control='uix_deactivate_mod'` (`uix/README.md:163-170`) | Not needed |
| Interact menu | Custom action groups and sub-groups via MD or Lua (`uix/README.md:183-307`) | Future, optional |
| Per-frame | `TopLevelMenu` callback `onUpdate_start(curtime)` (`menu_toplevel.xpl:241-247`); `Helper.uix_callbacks["onUpdate"]` (`helper.xpl:1044-1047`) | X4Native frame hook covers this |
| Vanilla menu callbacks | Hundreds of `<menu>.registerCallback` points across 22 menus. Docs: https://chemodun.github.io/x4/modding-support/ui-modding/uix-callbacks/ | Useful only if we later extend the map or info menus |
| Save loading | Wraps the vanilla `loadSave` handler with a `loadGameCallback_preLoadGame` callback and still calls `LoadGame(filename)` (`gameoptions.xpl:2981-3003`) | Compatible with our §7.4 `loadSave` path |

### 3.5 Behaviour changes that touch us

- The "Online" entry under Settings is made non-selectable (`gameoptions.xpl:1512-1515`).
  We don't use Egosoft online, so this doesn't matter to us.
- The Settings → Extensions list is re-sorted (mod sorter, `gameoptions.xpl:10881-10950`,
  userdata `__userdata_uix_gameoptions`). X4Native injects into `extensionsettings`, a
  different page. Coexistence is expected but **[UNVERIFIED]** in game.
- **Conflict risk:** any other mod that ships a replacement of one of those 22 files is
  mutually exclusive with UIX. Some players report problems: issue #21 "While using UI
  Extensions and HUD I can no longer view the start menu" (2024, open), #47 (8.0 beta).

### 3.6 Risks if we depended on it

- Not redistributable, so it would be a separate install step for every player, with two ids
  depending on the store.
- File replacement ties each UIX release to one game build. A game patch breaks it until
  kuertee re-merges (so far within about a day of 9.0). Our pinned-build policy turns this
  into a **second pin**: we would have to tell players which UIX version to use and keep them
  from updating it. Nexus does not auto-update, but Steam does.
- A game patch can also break UIX for players who don't run X4MP. That is not our problem,
  but it would land in our support channel.

**Recommendation: (b) optional, used when present, own fallback, narrowly scoped.** Use only
`uix_getConfig()` as one more upvalue-free config source in the §7.2 probe chain. Declare
nothing in `content.xml`: it isn't needed for load order (subst files load first), and a
declared id would be wrong for half the installs. Don't use kHUD, its callbacks or its
helpers. Don't copy its code.

---

## 4. Coexistence with X4Native and X4MP (needed whatever we decide)

Assume a large share of players run one or both of these mods.

### 4.1 OptionsMenu: who patches what

| Patcher | What it touches | When |
|---|---|---|
| UIX | Replaces `gameoptions.lua` (adds `uix_getConfig`, `registerCallback`; keeps `local config` and `menu.submenuHandler` as vanilla-shaped) | Base-game load |
| X4Native `x4n_settings_menu.lua` | `getupvalue(menu.displayOptions)` → `config`; wraps `menu.submenuHandler` for `"extensionsettings"` (`x4n_settings_menu.lua:225-245`) | Its file load, retry on `gfx_ok`/`show` |
| X4MP (planned §7.2) | Same `config` upvalue; edits `optionDefinitions.main`; wraps `submenuHandler` | Our file load or retry |
| SirNukes Simple Menu | Wraps `displayOptions`, `submenuHandler`, `viewCreated`, the `onRowChanged/onColChanged/onSelectElement` chain | **On-load init**, after MD Lua_Loader Ready (later) |
| SirNukes Hotkey API | Patches `displayOptions` and the controls pages (`sn/ui/hotkey/interface.lua:247`) | On-load init |

- **Wrapper chaining works** as long as each wrapper delegates unknown ids. SirNukes
  delegates (`options_menu.lua:258-260`), X4Native delegates (`:238-243`), and ours must too.
- **The `config` lookup is order-sensitive.** After SirNukes wraps `displayOptions`,
  `getupvalue(menu.displayOptions)` no longer reaches `config`. A `/reloadui` reloads
  `gameoptions` and every addon, so the order repeats on each reload, but our *retry* path
  (on `gfx_ok`/`show`) could run after SirNukes' init. The fix is in §5.1.
- No known reported conflicts between X4Native and either mod. Searches of
  eg3r/X4Native issues for "kuertee" and "sirnukes" found nothing.

### 4.2 Chat window (direct conflict with §7.6)

When SirNukes is installed, `OnlineSendChatMessage`, `OnlineGetChatMessages`,
`OnlineGetUserName` and `ExecuteDebugCommand` are already SirNukes functions. Its
`OnlineGetChatMessages` returns only its own ring buffer, and its `OnlineGetUserName` returns
a fixed local name (`sn/ui/chat_window/interface.lua:204-230`). Consequences:

- Our wrapper must **capture the current global at wrap time and delegate to it**. Never
  cache the vanilla function at file load, because SirNukes installs later (on-load init).
  Re-wrap if the global changes identity, and unwrap only if the global is still ours.
- Merge our messages with the result of the **previous** `OnlineGetChatMessages`, so
  SirNukes' `/command` output stays visible.
- Route our session chat before delegating, and pass `/…` text and non-session text through
  unchanged, so SirNukes' `Text_Entered` cues keep working.
- §7.4 uses `OnlineGetUserName()` as the default player name. Under SirNukes that returns a
  fixed placeholder (`L.user_name`). Treat any value that equals a known placeholder, or that
  is non-unique, as empty.
- SirNukes' own notes are useful evidence for our open §7.6 [VERIFY]. The 8.0+ chat window
  sends non-`/` text to `OnlineSendChatMessage` and shows nothing when offline, and
  replacing the two globals makes it work offline
  (`sn/ui/chat_window/interface.lua:7-48`).

### 4.3 `require`

SirNukes replaces global `require` but falls through for unregistered names
(`sn/ui/lua_loader.lua:181-199`). `require("ffi")` and `require("debug")` keep their normal
behaviour.

### 4.4 Multiplayer handshake

Neither library changes simulation by itself (both have `save="false"`; SirNukes' MD only
relays UI events; UIX is UI only). The **mods that depend on them** may change simulation, and
those mods appear in the extension list on their own. Requiring identical library lists
across players would add friction without protecting anything (see §5.3).

---

## 5. Concrete changes proposed (for the lead to review)

### 5.1 mod-design §7.2 (main-menu entry): revised probe order

Replace the "Upvalue reader, in probe order" list with a "**`config` source, in probe
order**" list. Capture `config` **once, as early as possible**, and cache it for the session.

1. **UIX accessor:** `if type(OptionsMenu.uix_getConfig) == "function" then config = OptionsMenu.uix_getConfig() end`.
   No `debug`, and it is immune to wrapper order.
2. **`require("debug").getupvalue`**, run **at our file load**, which is before SirNukes'
   on-load init (§4.1). Search the upvalues of several **vanilla** functions, not only
   `displayOptions`, because any of them can be wrapped by another mod. Vanilla 9.00 has
   `config` as an upvalue of `menu.displayOptions` (`gameoptions.lua:9345`),
   `menu.createOptionsFrame` (:3615) and `menu.displayOption` (:4713). Accept a candidate
   only if it has `optionDefinitions.main` as an array **and** `optionsLayer` as a number, to
   reject SirNukes' copy-table named `config`.
3. Native `lua_getupvalue` (unchanged), with the same multi-function search.
4. **New, upvalue-free row append** (SirNukes' technique, `sn/ui/simple_menu/options_menu.lua:95-216`,
   MIT; reimplement it, with an attribution comment if any code is copied). Wrap
   `menu.displayOptions`. For `"main"`, swap `menu.createOptionsFrame` to capture the frame,
   call the original, append our row with `menu.displayOption(ftable, {id="x4mp", …})`,
   move it after `timelines`, and display. Our ids still go through the `submenuHandler`
   wrapper. This gives the embedded entry without any `config` access. Only the embedded
   *screens* (7.3/7.4 drawn in the OptionsMenu frame) still need `config.optionsLayer`. For
   those, use the literal `4`, which is vanilla `optionsLayer` (`gameoptions.xpl:480`),
   checked against `menu.createOptionsFrame` output **[VERIFY]**.
5. Nothing works: standalone menu (unchanged).

Also:
- Replace the bullet "kuertee's UI Extensions mod would give stable hooks, but we do **not**
  take it as a dependency" with: "UIX, when present, is used only as config source 1 (see
  research/library-mods.md). It is never required and never declared in content.xml."
- Add to the adapter log line which source won: `X4MP ui: optionsmenu adapter OK(source=uix|debug|native|append)|DEGRADED(...)`.
- Self-test (8.5) runs the probes with UIX and SirNukes present when the tester has them
  installed. Add a test-matrix row (§5.4).

### 5.2 mod-design §7.6 (chat) and §7.4 (Join)

- §7.6: replace "wraps both globals … Disconnect restores the originals" with the
  chain-safe rules in §4.2: capture the *current* global at wrap time, delegate, merge, and
  conditionally unwrap. Note that SirNukes installs its wrappers late, so wrap on
  `gfx_ok`/`show` and on session start, and check identity each time.
- §7.4 Player-name default: ignore `OnlineGetUserName()` results that are empty or equal to
  a known placeholder.
- §7.4: keep the Join dialog in Lua. **Do not** use SirNukes' MD Simple Menu, because it
  routes field values through an MD blackboard (`Simple_Menu_API.md:9`), which would expose
  the password to MD and saves.

### 5.3 Extension-list handshake (protocol §4.2, mod-design §6.4, ADR-004)

Proposal: split the reported extension list into `sim_extensions` and `client_only_extensions`.

- `extensions_hash` is computed over **enabled DLCs + all non-Egosoft extensions except a
  shipped allowlist of known client-only UI libraries**. Initial allowlist, by content id:
  `kuerteeUIExtensionsAndHUD`, `ws_3477279743`, `ws_2042901274`, `ws_3514258146`
  (SirNukes Community Edition), and `x4native`/`x4mp` (already compared through version
  fields). The allowlist lives in the shared constants file (ADR-004 consequence).
- The full list is still sent for the diff view and the admin GUI. Library mismatches show
  as **info**, not `ExtensionsMismatch`.
- Rationale: these libraries alter neither simulation nor saves. The mods that use them are
  hashed normally.

### 5.4 docs/dev-setup.md (§5 Game machine)

No install step is added, because neither mod is required. Proposed edits:

- In the "Third-party mods" row: "**disabled** during dev/testing, **except** the
  compatibility pass (below)."
- New paragraph, "Compatibility pass (before each mod release)": install SirNukes Mod
  Support APIs (Steam 2042901274 or Nexus 503) and kuertee UI Extensions and HUD (Nexus 552
  or Steam 3477279743, version 9.0.0.x) into `<X4 install>\extensions\`. Run the self-test
  and the Join/chat script in four configurations: none, SirNukes only, UIX only, both.
  Record the adapter's `source=` value and whether chat round-trips.
- No player-facing install step. The player README says: "Works with or without kuertee UI
  Extensions and SirNukes Mod Support APIs."

---

## 6. Open items

- [UNVERIFIED] SirNukes on 9.00 / 611726: does Simple Menu's "Extension Options" row still
  render? (Issue #34 and Nexus #2373 suggest problems.)
- [UNVERIFIED] Nexus download counts and the exact permission text for both mods (Cloudflare
  blocked direct fetches; values above come from search snippets).
- [UNVERIFIED] In game: with SirNukes installed, does X4Native's own Settings → Extensions →
  X4 Multiplayer page still appear? It depends on X4Native's injector winning the race in §4.1.
- [UNVERIFIED] Whether X4 9.00 has had post-611726 hotfixes that UIX now targets. Our check
  in §3.1 compared only a sample of files.
- V20 itself still needs the session-2 retest of `require("debug")` with Protected UI Mode
  off.

## 7. Sources

- bvbohnen/x4-projects @ `ba199a4`: `LICENSE`; `extensions/sn_mod_support_apis/{content.xml, ui.xml, Readme.md, change_log.md, documentation/*.md, ui/lua_loader.lua, ui/library.lua, ui/simple_menu/options_menu.lua, ui/simple_menu/tables.lua, ui/chat_window/interface.lua, ui/hotkey/interface.lua, ui/c_library/winpipe.lua, md/lua_loader.xml, md/hotkey_api.xml}`; issues #29, #33, #34.
- kuertee/x4-mod-ui-extensions @ `e3d419e`: `content.xml`, `ui.xml`, `README.md`, `subst_01.cat`, `dev-make-cat-file.bat`, `ui/addons/ego_gameoptions/gameoptions.xpl`, `ui/addons/ego_detailmonitor/menu_toplevel.xpl`, `ui/addons/ego_detailmonitorhelper/helper.xpl`; issues #21, #47.
- eg3r/X4Native: vendored `mod/third_party/x4native/v9.0.0-611726/x4native/ui/x4n_settings_menu.lua:45-62, 225-245`; `docs/EXTENSION_GUIDE.md:619` (main branch).
- Steam Workshop pages (fetched 2026-10-01): https://steamcommunity.com/sharedfiles/filedetails/?id=2042901274, https://steamcommunity.com/sharedfiles/filedetails/?id=3514258146, https://steamcommunity.com/sharedfiles/filedetails/?id=3477279743.
- Nexus (via search results only): https://www.nexusmods.com/x4foundations/mods/503, https://www.nexusmods.com/x4foundations/mods/552, https://www.nexusmods.com/x4foundations/mods/2373.
- Dependent-mod examples: radlinsky/x4-gunnery-control (`content.xml:4-11`, `DEVELOPMENT.md:964-981`), runekn/x4-reactive-docking (`content.xml`, `ui/ui_initializer.lua:4-12`), mbleichner/x4-warehouse-fleets (`content.xml`), DeadAirRT/deadair_scripts (`content.xml`).
- X4 9.00 release date (2026-06-10): https://steamcommunity.com/games/392160/announcements/detail/528744981882470469 and the X4 wiki "Update 9.00".
