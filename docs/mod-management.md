# X4MP Mod Management (design)

Status: design v1, 2026-10-01. Decision record: **ADR-044** (phases and milestones) and
**ADR-043** (library mods, client-only allowlist). `architecture.md` wins on conflict.
Doc abbreviations as in `decisions.md` (PROTO, SRV, MOD, API).

## 0. Goal and the user decision

The user wants playing modded X4 with friends to be easy:

- the server knows **which mods the session wants**, and **which mods each player has**
  every time they try to connect;
- a player who is missing something gets a **clear message** saying exactly what to
  install, enable or disable, with a link for each missing mod;
- the admin can **enable or disable mods** for the session in the GUI;
- later, a launcher **switches the player's local mods on/off** to match the session;
- in the future, X4MP should be able to run **modded games**, not only vanilla + DLC.
  That is not a near goal.

**User decision (2026-10-01): X4MP never hosts, downloads or installs mods.** Each mod in
the session mod list may carry **source links**: a Nexus Mods page and/or a Steam
Workshop item. Nexus or Steam does the hosting and installing. Our GUI, the in-game
rejection screen and the launcher only **show and open** those links. The launcher only
**toggles enable/disable** for mods the player already has installed. This removes the
whole redistribution problem (licensing, signed downloads, trust in server-supplied code).

| Phase | Milestone | What |
|---|---|---|
| 1 | M1 (server, FakeNode) + M2 (mod) | Full extension report in the handshake, stored per player; session mod list + policy (Required / Allowed / Blocked, default for unknown mods, enable/disable); classification sim vs client-only; structured rejection with links; GUI Mods screen; import from authority; save `<patches>` read |
| 2 | M6 (launcher) | HTTP session mod manifest; launcher compares, toggles enable state in the profile `content.xml` with backup/restore, opens Workshop/Nexus links for missing mods |
| 3 | Backlog | Modded-game support: compatibility classes, MD suppression on clients, extension settings sync, saves that require mods, test plan |

---

## 1. Facts we build on

### 1.1 Where extensions come from

| Source | Folder | Id | Notes |
|---|---|---|---|
| DLC | `<X4 install>\extensions\ego_dlc_*` | `ego_dlc_*` | `egosoftextension = true`. Must match across nodes (ADR-004). |
| Install | `<X4 install>\extensions\<folder>\` | `content.xml` `id` | Manual installs (Nexus zip into the game folder), also `x4native`, `x4mp`. Writing needs admin rights under `Program Files`. |
| User | `Documents\Egosoft\X4\extensions\<folder>\` | `content.xml` `id` | No admin needed. `personal = true` in the Lua list **[VERIFY]**. |
| Workshop | `<steam library>\steamapps\workshop\content\392160\<workshop-id>\` | `ws_<workshop-id>` | `isworkshop = true`. Steam downloads/updates them; Steam may auto-update a mod mid-campaign. |

- **Documents** must be resolved with `[Environment]::GetFolderPath('MyDocuments')`
  (C#: `Environment.SpecialFolder.MyDocuments`; native: `SHGetKnownFolderPath(FOLDERID_Documents)`).
  On the dev machine it is under OneDrive.
- **Steam library folders** come from `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`
  (or `HKCU\Software\Valve\Steam\SteamPath`) plus `steamapps\libraryfolders.vdf`. From
  inside the game, the library holding X4 is simply `<X4 install>\..\..` (`steamapps`).
- **Folder name is not the id.** Native must map id → folder by reading each
  candidate's `content.xml`.

### 1.2 Per-profile enable state

`Documents\Egosoft\X4\<steam-id>\content.xml` holds overrides only:

```xml
<content>
  <extension id="ws_2436999794" enabled="true"></extension>
  <extension id="ws_2458720435" enabled="false"></extension>
</content>
```

An extension without an entry uses its own default (`content.xml` `enabled="1"`,
`enabledbydefault` in Lua). Entries may be **stale**: on the dev machine all 17 `ws_*`
entries point to Workshop items that are no longer on disk. The game writes this file when
the player changes Settings → Extensions, and possibly on exit **[VERIFY]**. Whether
Steam Cloud syncs it is **[VERIFY]**.

**Changing extensions requires a game restart.** So enable state can only be changed
**before X4 starts** (launcher, phase 2), never from the running mod.

### 1.3 In-game Lua API (9.00, `x4-unpacked/ui/addons/ego_gameoptions/gameoptions.lua`)

| Function | Signature / fields | Where |
|---|---|---|
| `GetExtensionList()` | array of `{index, id, name, version, date, enabled, enabledbydefault, personal, isworkshop, egosoftextension, sync, syncbydefault, error, errortext, warning, warningtext}` | :5899, :8155, `displayExtensionRow`; also `menu_mapeditor.lua:1012` |
| `C.IsExtensionEnabled` | `bool IsExtensionEnabled(const char* extensionid, bool personal)` | ffi :239 |
| `C.HasExtension` | `bool HasExtension(const char* extensionid, bool personal)` | ffi :221 |
| `C.GetExtensionNameByID` | `const char* GetExtensionNameByID(const char* extensionid, bool personal)` | ffi :144 |
| `C.GetExtensionVersion` | `const char* GetExtensionVersion(const char* extensionid, bool personal)` | ffi :145 |
| `C.GetExtensionErrorText` | `const char* GetExtensionErrorText(const char* extensionid, bool personal)` | ffi :142 |
| `C.GetModifiedBasegameUIFilesExtensions` | `const char* (void)`, `;`-separated list (names, used for display) | ffi :167, used :4089 |
| `GetAllExtensionSettings()` | table indexed by extension `index`, `{enabled, sync}`; index `0` = global sync | :2979, :3372 |
| `SetExtensionSettings(id, personal, key, value)` | `key = "enable"` or `"sync"`; `id = ""` + `"sync"` sets the global sync flag | :3370, :8147, :8187 |
| `GetGlobalSyncSetting()`, `HaveExtensionSettingsChanged()`, `ResetAllExtensionSettings()`, `GetExtensionUpdateWarningText(id, personal)`, `OpenWorkshop(id, personal)` | | :3368, :5903, :8133, :5905, :8193 |
| `C.CanOpenWebBrowser()`, `C.OpenWebBrowser(url)` | | `menu_help.lua:19, 31, 450` |

`SetExtensionSettings` only takes effect after a restart, like the Settings menu.
"Sync" is Egosoft's flag that ties a Workshop extension's settings to the savegame
("global sync"); phase 3 uses it.

### 1.4 Savegames record their extensions

The save header lists the extensions the save depends on (`save="1"` extensions):

```xml
<game id="X4" version="900" build="611726" modified="1" .../>
<patches>
  <patch extension="ego_dlc_split" version="900" name="Split Vendetta"/>
  ...
  <history> ... </history>
</patches>
```

The load menu reports a save whose patches are missing, old or disabled via
`savegame.invalidpatches[] = {id, name, requiredversion, state, installedversion}`
(`gameoptions.lua:6086, 6118`). The server already sniffs uploaded saves (SRV §3.1), so it
can read `<patches>` cheaply from the first few KB of the gzip stream.

### 1.5 Constraints

- **Licensing.** Many Nexus mods forbid re-upload; Workshop items are tied to Steam; kuertee
  UIX has no license. The user decision (links only) avoids redistribution entirely.
- **Security.** Mods are code: MD, Lua, and sometimes native DLLs (X4Native mods, SirNukes'
  `winpipe_64.dll`). We never fetch code. The launcher only toggles mods the player
  already chose to install, and it warns before enabling one that ships a DLL.

---

## 2. Classification: what must match

| Class | Meaning | Rule |
|---|---|---|
| `Dlc` | `ego_dlc_*` | Must match the authority exactly (ADR-004). Always in `extensions_hash`. |
| `Sim` | Can change universe state, the save, or what the authority simulates | Must match the session (id + version, and content hash when both have one). In `extensions_hash`. |
| `ClientOnly` | Only changes what one player sees or how their UI works | May differ between players. Not in `extensions_hash`. |
| `Unknown` | Not classified yet | Treated as `Sim` until an admin classifies it. |

**Simulation-affecting** means the extension contains any of: `md/` (mission director
scripts), `aiscripts/`, `libraries/` (wares, factions, jobs, god, modules, loadouts,
region definitions), `maps/`, `index/macros.xml` / `index/components.xml` diffs, new or
changed macros under `assets/` that alter stats, or `content.xml` `save="1"`. Text
(`t/`), UI Lua/XML (`ui/`), textures, sounds and pure visual asset swaps are
**client-only**.

How the class is decided, in order:

1. **Shipped allowlist** (`protocol/` shared constants, ADR-043): client-only libraries
   `kuerteeUIExtensionsAndHUD`, `ws_3477279743` (UIX Workshop), `ws_2042901274` (SirNukes),
   `ws_3514258146` (SirNukes CE). `x4native` and `x4mp` are compared through version
   fields and are excluded from the hash.
2. **Admin override** in the session mod list (the admin knows their mods best).
3. **Heuristic** computed by native from the folder: the top-level folders present
   (loose files or entries in the `.cat` indexes), `save="1"`, a `subst_*.cat` (replaces
   base-game files). Only `t/`, `ui/`, textures/sounds → `ClientOnly`; anything listed
   above → `Sim`; nothing readable → `Unknown`.

Caveat: a "UI" mod that also ships an `md/` script (many do, for menu plumbing, e.g.
SirNukes) classifies as `Sim` by heuristic. That is why the allowlist and the admin
override exist. A wrong `ClientOnly` override can desync a session; the GUI says so.

---

## 3. Phase 1: report, store, policy, reject clearly (M1 server, M2 mod)

### 3.1 What each node reports

At `ClientHello`, every node (authority included) sends its **full** extension list,
enabled and disabled, as `ExtensionInfo`:

| Field | Source | Notes |
|---|---|---|
| `id` | Lua `GetExtensionList().id` | |
| `name` | Lua | |
| `version` | Lua | string, as shown in game |
| `source` | native scan | `Dlc` / `Install` / `User` / `Workshop` |
| `enabled` | Lua `enabled` (the state **this run**, not pending changes) | |
| `egosoft` | Lua `egosoftextension` | |
| `workshop_id` | `ws_<n>` → `n`, else 0 | |
| `content_hash` | native, SHA-256 (§3.2) | empty if not computed |
| `hash_kind` | `None` / `CatIndex` / `Files` | |
| `has_native_dll` | native: any `*.dll` in the folder tree | |
| `replaces_basegame` | native: `subst_*.cat` present; or name listed by `GetModifiedBasegameUIFilesExtensions()` | |
| `save_dependent` | native: `content.xml` `save="1"` | |
| `class_hint` | §2 heuristic | `Dlc` / `Sim` / `ClientOnly` / `Unknown` |
| `error` / `warning` | Lua `error`, `warning` | e.g. missing dependency |
| `dependencies` | native: `content.xml` `<dependency id=… optional=…>` | for "also needs X" hints |

`extensions_hash` is kept as a **fast path**: SHA-256 of sorted `"id@version\n"` lines of
enabled `Dlc` and `Sim` extensions (allowlist and `x4native`/`x4mp` excluded). Equal hash
⇒ no policy evaluation needed.

### 3.2 Content hash (feasibility)

A version string is not proof two copies are the same (Workshop items update silently,
Nexus authors re-upload without bumping). A cheap content hash:

- **`.cat` mods:** hash the `.cat` index files. Each `.cat` line is
  `path size timestamp md5`, so hashing the indexes (a few KB) covers the content without
  reading the `.dat` files. `hash_kind = CatIndex`.
- **Loose-file mods:** SHA-256 over sorted `(relative path, size, sha256)` of files,
  capped at 64 MB / 2,000 files; above the cap → `None`.
- Computed by native on a worker thread at start-menu time and cached in
  `x4mp\ext-hash-cache.json` keyed by `(folder, newest mtime, total size)`. Never on the
  frame thread.
- Policy: if both the session entry and the player have a hash and they differ → treat as
  a version mismatch for `Sim` mods, info only for `ClientOnly`.

### 3.3 Session mod list and policy

Each session has a **mod list** (`ModPolicy`). The admin edits it in the GUI; it is pushed
to nodes in `Welcome`/`SessionSettings`.

```
ModPolicy {
  version            : uint              // bumps on every edit
  source_mode        : AuthorityDefines | AdminList
  unknown_default    : Block | AllowClientOnly | AllowAll      // for mods not in the list
  enforcement        : Strict | Warn                           // Warn admits and flags
  entries            : [ModPolicyEntry]
}
ModPolicyEntry {
  id, name           : string
  rule               : Required | Allowed | Blocked
  enabled            : bool     // admin's on/off switch for this session's mod set
  class              : Dlc | Sim | ClientOnly | Unknown        // admin override of the hint
  version_rule       : Exact | AtLeast | Any
  version            : string   // expected (from import)
  content_hash       : bytes    // optional, from the authority's report
  nexus_url          : string   // optional, validated (§3.6)
  workshop_id        : ulong    // optional; URLs derived
  notes              : string   // shown to players ("needs SirNukes too")
}
```

- **`enabled` is the GUI's enable/disable switch.** An enabled `Required` mod must be
  installed and enabled on every node. Disabling it in the GUI makes it effectively
  `Blocked` for this session, while the entry (links, notes) stays in the list for next
  time. `Allowed` entries ignore `enabled` for admission; it only drives what the
  launcher will switch on by default.
- **`AuthorityDefines`** (default when the list is empty) reproduces today's ADR-004
  behaviour: the authority's enabled `Dlc` + `Sim` extensions become implicit `Required`
  entries, the rest falls to `unknown_default`.
- **`AdminList`**: the list is authoritative; the **authority is checked against it too**,
  so a host who forgot to enable a mod gets told before the session starts.
- **Defaults** (recommended, see §10): `unknown_default = AllowClientOnly`,
  `enforcement = Strict`, library allowlist entries pre-seeded as `Allowed`.

**Import from authority** (admin convenience): fills the list from the authority node's
last report. Enabled `Dlc`/`Sim` mods become `Required` + `enabled`, enabled `ClientOnly`
mods `Allowed`, disabled ones are not imported. Versions and hashes are copied; Workshop
ids fill `workshop_id` automatically; the admin then pastes Nexus URLs. Re-import merges
by id and keeps the admin's links, notes and overrides.

**Save requirements**: when a session save is set, the server reads its `<patches>`
(§1.4) and marks every listed extension as "required by save" in the GUI. If the policy
blocks or omits one, the GUI warns ("this save needs X; loading without it may fail or
drop content").

### 3.4 Evaluation (server, at `ClientHello`, after the version checks)

```
if hello.extensions_hash == session.expected_hash: admit
violations = []
for e in policy.entries:              // a Required ClientOnly mod is enforced too
    p = player.find(e.id)
    if effectiveRule(e) == Required:
        if p is None:                  violations.install += e   (with links)
        elif not p.enabled:            violations.enable  += e
        elif !versionOk(e, p):         violations.update  += e   (have p.version, need e.version)
    if effectiveRule(e) == Blocked and p?.enabled: violations.disable += e
for p in player.enabled not in policy:
    cls = classify(p)                  // allowlist > hint
    if unknown_default == Block, or (AllowClientOnly and cls != ClientOnly):
        violations.disable += p        // "not part of this session"
if violations.empty: admit
elif policy.enforcement == Warn: admit, flag player (GUI badge, audit event)
else: Disconnect(ExtensionsMismatch, detail = ModPolicyViolation)
```

`Dlc` differences always reject (ADR-004), whatever `enforcement` says.

### 3.4a Clients that connect before the authority (decision, M1-X3/X4)

In `AuthorityDefines` mode the authority's list is the standard, so with no authority known yet there is nothing to compare
a client with. Chosen behaviour: **admit now, judge later.** The gateway admits the client and marks it `ModCheckPending`
(it is still stored as an `Admitted` report). When the first authority is admitted (the moment `GatewayState.Authority` is
set) the session actor re-evaluates every pending client with the normal evaluator and the authority's list:

- Strict mismatch: `Disconnect{ExtensionsMismatch}` with the exact `ModPolicyViolation` (same shape as at the gateway),
  the slot is freed, and the violation is stored as a new `Rejected` report.
- Warn mismatch: the usual `ServerNotice`, a new `Warned` report, the client stays.
- Match: nothing happens.

Not chosen: a retryable reject while no authority is known. It would stop friends joining a lobby that is waiting for the
host (the session phase is `WaitingForAuthority` on purpose) and would break every flow that connects clients first.
A pending flag is cleared by the check, so a later authority change does not re-kick anybody (MM3). `AdminList` mode needs no
deferral: the list itself is the standard and is evaluated at once (the authority is checked against it too).

### 3.5 Rejection message

`Disconnect{code = ExtensionsMismatch}` carries a structured `ModPolicyViolation`:

```
ModPolicyViolation {
  policy_version : uint
  install : [ModRef]   // missing: id, name, version, nexus_url, workshop_id, notes
  enable  : [ModRef]   // installed but disabled
  disable : [ModRef]   // enabled but blocked / not part of this session
  update  : [ModRef]   // wrong version or hash (have / need)
}
```

In game (Join dialog status area, MOD §7.4), grouped and actionable:

```
Can't join "Friday Run": your mods don't match this session.
 Install (2):
   Warehouse Fleets 1.4         [Open Nexus page] [Open Workshop page]
   SirNukes Mod Support APIs    [Open Workshop page]
 Enable (1):   Reactive Docking  (Settings > Extensions, then restart)
 Disable (1):  Cheat Menu        (not allowed in this session)
 Update (1):   DeadAir Scripts   you have 2.1, session uses 2.3
Changes need a game restart. The X4MP launcher can do the enable/disable part for you.
```

- Buttons call `C.OpenWebBrowser(url)` when `C.CanOpenWebBrowser()`; Workshop items can
  also use `OpenWorkshop`-style links **[VERIFY: which URL forms OpenWebBrowser accepts;
  steam:// may need the overlay]**. Otherwise the URL is shown as text.
- The same structure is logged to the mod log and shown on the server's Players page.
- `enable`/`disable` hints name the in-game path; the game itself can toggle them from
  Settings → Extensions (no launcher needed in phase 1).

### 3.6 Links: what we accept

- **Workshop:** from `workshop_id` the server derives
  `https://steamcommunity.com/sharedfiles/filedetails/?id=<n>` and
  `steam://url/CommunityFilePage/<n>`. Auto-filled for every `ws_<n>` id.
- **Nexus:** admin-pasted, validated against
  `^https://(www\.)?nexusmods\.com/x4foundations/mods/(\d+)(/.*)?$`, stored normalised as
  `https://www.nexusmods.com/x4foundations/mods/<n>`. Anything else → 400.
- No other link types in v1 (no arbitrary URLs: they would let a server send players
  anywhere). Both the server (on save) and every consumer (mod, launcher) re-validate.

### 3.7 Phase 1 acceptance (summary; tasks in roadmap)

- FakeNode `--extensions <file.json>` reports a list; mismatches produce the exact
  `ModPolicyViolation` expected, per rule × state table test.
- Admin can import from the authority, toggle enable/disable, set rules, paste a Nexus URL
  (bad URL rejected), and see each player's diff live.
- In game (M2), a client missing a `Required` mod sees the grouped message with working
  links; with matching mods it joins exactly as before.

---

## 4. Phase 2: launcher sync (M6)

### 4.1 Session mod manifest (HTTP)

`GET /api/v1/join/mod-manifest` on 47790 returns the effective session list for players:

```json
{ "session": "Friday Run", "policyVersion": 7, "gameBuild": "900-611726",
  "unknownDefault": "AllowClientOnly",
  "mods": [ { "id": "ws_2042901274", "name": "SirNukes Mod Support APIs", "rule": "Allowed",
              "enabled": true, "class": "ClientOnly", "version": "195",
              "workshopId": 2042901274, "nexusUrl": null, "notes": "" } ] }
```

Access: unauthenticated when the session has no join password; otherwise the launcher
proves the password with the same HMAC construction as PROTO §4.3 (nonce from
`GET /api/v1/join/nonce`). The manifest has no secrets; it is only gated so a stranger on
the LAN can't enumerate it. The launcher re-validates every link (§3.6).

### 4.2 Launcher flow (C#, `x4mp-launcher.exe`, ships in the mod zip)

1. Read `x4mp.json` / last server; fetch the manifest.
2. **Scan** the three sources (§1.1) and the profile `content.xml` (newest
   `<steam-id>` folder, or ask if several). Build the local `ExtensionInfo` list the same
   way native does (shared rules, shared allowlist).
3. **Plan** and show it:
   - *Toggle* (installed mods only): enable `Required`+`enabled` and `Allowed`+`enabled`
     entries; disable `Blocked` and not-in-session `Sim` mods.
   - *Missing*: for each, **[Open Workshop page]** (`steam://url/CommunityFilePage/<n>`,
     fallback https) and/or **[Open Nexus page]**. The launcher never downloads. For
     Workshop it then waits (polls the workshop folder) until Steam has the item, with
     "Check again".
   - *Version mismatch*: show have/need and the link; can't fix it for the player.
4. **Trust and warnings** before applying:
   - First contact with a server: show server name and address; "This server wants to
     change which of your installed mods are enabled. Nothing is downloaded." Remember the
     answer per server.
   - Enabling a mod with `has_native_dll` or `replaces_basegame`: extra confirmation
     ("contains native code / replaces game UI files").
5. **Apply**: X4 must not be running (check process `X4.exe`; refuse otherwise). Back up
   `content.xml` to `x4mp\profile-backups\content.<utc>.xml`, write a
   `x4mp\profile-backups\pending-restore.json` journal (server, original entries), then
   rewrite only the affected `<extension>` entries (preserve unknown attributes, keep
   stale entries untouched, write to a temp file and atomic-rename). OneDrive: retry on
   sharing violations.
6. **Launch**: write the one-shot `launch.json` (MOD §2.6) and start X4 through Steam
   (`steam://rungameid/392160`).
7. **Restore**: when X4 exits (launcher waits, or on the next launcher start if it was
   closed), restore the backed-up entries unless the player ticked "keep this mod set".
   The restore only touches entries the launcher changed, and only if the file still has
   the value the launcher wrote (the player may have changed things in game).
8. Keep the last 10 backups; "Restore my original mods" button always available.

Steam may auto-update a Workshop mod between sessions; the next manifest check then
reports `update`. Nexus mods never auto-update.

### 4.3 GUI "Mods" screen (Sessions → Mods), phases 1 + 2

```
┌ Mods · Session "Friday Run" ─────── policy v7 · [Strict ▾] · Unknown mods: [Allow client-only ▾] ┐
│ [Import from authority (Alice)]  [+ Add mod]  Mode: (•) Admin list ( ) Authority defines       │
│ ┌──┬──────────────────────────┬─────────┬─────────┬──────────┬────────────────┬─────────────┐ │
│ │On│ Mod                      │ Class   │ Rule    │ Version  │ Links          │ Players     │ │
│ ├──┼──────────────────────────┼─────────┼─────────┼──────────┼────────────────┼─────────────┤ │
│ │☑ │ Warehouse Fleets         │ Sim     │Required▾│ =1.4 ▾   │ Nexus ✓ WS ✓   │ 3/4 ✓ 1 ✗   │ │
│ │☑ │ SirNukes Mod Support APIs│ Client* │Allowed ▾│ any      │ WS ✓           │ 2 have      │ │
│ │☐ │ Cheat Menu               │ Sim     │Blocked ▾│ —        │ Nexus [paste]  │ 1 enabled ✗ │ │
│ │☑ │ DeadAir Scripts          │ Sim  ⚠  │Required▾│ =2.3     │ WS ✓           │ 1 outdated  │ │
│ └──┴──────────────────────────┴─────────┴─────────┴──────────┴────────────────┴─────────────┘ │
│ * allowlisted library   ⚠ required by the session save (<patches>)   DLL/UI-replace icons     │
├ Per-player status ─────────────────────────────────────────────────────────────────────────┤
│ Alice (authority)  ✓ matches                                                             │
│ Bob                ✗ install Warehouse Fleets · disable Cheat Menu      [show full diff] │
│ Eve                ⚠ admitted (Warn): DeadAir 2.1 ≠ 2.3                                   │
├ Selected: Warehouse Fleets ────────────────────────────────────────────────────────────────┤
│ id ws_1234567890 · source Workshop · authority hash 3f9a… · has DLL: no · replaces UI: no │
│ Nexus URL [https://www.nexusmods.com/x4foundations/mods/1234____] ✓ valid                 │
│ Workshop: steam://url/CommunityFilePage/1234567890 (auto)   Notes [needs SirNukes____]    │
│                                                         [Discard]  [Save changes (2)]     │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Players → player detail gains a **Mods** tab: that player's full report (source, enabled,
version, hash, DLL/UI flags) with the diff against the session highlighted.

---

## 5. Phase 3: modded-game support (backlog)

What X4MP must handle before "any mod" is safe:

| Mod kind | Example effect | Under X4MP |
|---|---|---|
| New macros / wares / ships (`libraries/`, `assets/`, `index/`) | New ship classes, new wares | **Fine if identical on all nodes** (hash-enforced). Spawns of modded macros replicate like vanilla ones. Matching keys (PROTO §8.4) must include them. |
| MD scripts that spawn or change universe state | Dynamic wars, faction logic, custom jobs | **Must run only on the authority.** On clients they would spawn local entities that fight the replicated world. Needs a client-side suppression strategy: disable the mod's cues on clients (MD `<cue>` patching or a per-mod "client stub" diff), or rely on the client ghost model to prune strays (MOD §4.6), which is costly. Per-mod decision → compatibility class. |
| AI scripts | Smarter trading, combat | Authority-only by nature (clients' NPCs are ghosts). Fine. |
| UI-only | HUD, menus, sorting | Fine (`ClientOnly`). Must coexist with our wrappers (ADR-043 rules). |
| Economy / balance mods | Prices, production, station modules | Fine on the authority; client UI may display wrong local prices until the economy sync (ADR-034) covers it. Must match. |
| Mods that replace base-game files (`subst_*.cat`) | UIX | Fine if UI-only; flagged in reports. |
| Mods with native DLLs | X4Native extensions | Allowed only if `ClientOnly` or identical; they may hook the same functions we do. Case by case. |

Further phase-3 items:

- **Extension settings sync.** `SetExtensionSettings(id, personal, "sync", true)` and the
  global sync flag tie Workshop extension settings to the savegame. The session save
  should carry the authority's settings; the launcher (or the mod in the start menu) can
  set `sync` to match. Investigate what exactly is synced **[VERIFY]**.
- **Saves that require mods.** The session save's `<patches>` is the ground truth for
  `save_dependent` mods. A client without one cannot load the save (game refuses with
  `invalidpatches`), so these become hard `Required` automatically. Removing a
  `save="1"` mod from a campaign is unsafe; the GUI must refuse or warn heavily.
- **Compatibility classification.** A per-mod record `compat = Verified | ClientOnly |
  AuthorityOnlyMD | Incompatible | Untested`, with notes, kept in a shipped list (like the
  library allowlist) and overridable per server. `AuthorityOnlyMD` mods need the client
  suppression from row 2.
- **Test plan.** For each mod promoted to `Verified`: (1) two nodes, identical mod set,
  30-min M4 acceptance run (station match ≥ 99%, no stray local spawns, ghost error
  budget); (2) save/reload on the authority with `tools/savescan` clean; (3) join of a
  third node from the session save; (4) record `compat` + mod version. Start with three
  popular mods of different kinds (one ship pack, one MD gameplay mod, one UI mod).

---

## 6. Protocol and schema impact (described, not yet in `.fbs`)

| Message / type | Change |
|---|---|
| `ExtensionInfo` (new table, `common.fbs`) | Fields as §3.1. Enums `ExtensionSource { Dlc, Install, User, Workshop }`, `ExtensionClass { Unknown, Dlc, Sim, ClientOnly }`, `HashKind { None, CatIndex, Files }`. |
| `ClientHello` | `extensions:[string]` → **deprecated**; add `extension_list:[ExtensionInfo]`. `extensions_hash` semantics per §3.1 (Dlc + Sim, allowlist excluded). |
| `ServerHello` | `extensions_hash` unchanged in meaning (authority/session expected hash); add `mod_policy_version:uint` so a launcher-started node can tell if its local sync is current. |
| `ModPolicy`, `ModPolicyEntry`, `ModRule { Required, Allowed, Blocked }`, `UnknownModDefault`, `ModEnforcement`, `VersionRule` (new, `control.fbs` or new `mods.fbs`) | As §3.3. |
| `Welcome` / `SessionSettings` | Add `mod_policy:ModPolicy` (full, so the mod can show "what this session uses" in the Multiplayer screen). |
| `ModPolicyChanged` (new, S→N, Control, id in the 0x01xx range) | Pushed when the admin edits the policy. Nodes that now violate it are **not** kicked mid-session; they get a notice and the GUI flags them. Takes effect at next join. |
| `Disconnect` | Add optional `mod_violation:ModPolicyViolation` (§3.5), used with code 13 `ExtensionsMismatch`. |
| `ModRef` (new) | `id, name, version, have_version, nexus_url, workshop_id, notes`. |
| Shared constants | `ClientOnlyLibraryIds` allowlist; Nexus URL regex; hash line format. Generated for C#, C++ and the launcher. |

Applied as a schema delta in the M1 protocol task (a new ADR-036-style list), keeping
protocol 0.x.

**Applied (M1-X1, M1-X2).** `protocol/schema/mods.fbs` holds the policy tables, `common.fbs` the `ExtensionInfo`
family, message `ModPolicyChanged` is 0x0117. Deviations from the sketch above: `ModPolicyEntry.class` is
`mod_class` (`class` is a C# keyword); `ExtensionInfo.dependencies` is `[ExtensionDependency{id, optional}]`;
`mod_policy` lives on `SessionSettings` (Welcome carries it through `Welcome.settings`);
`ClientHello.extensions:[string]` stays (documented deprecated, the server lifts it into an extension list when
`extension_list` is empty). Enum defaults are the safe/recommended ones (`AuthorityDefines`, `AllowClientOnly`,
`Strict`, `Exact`). Shared constants live in `protocol/constants/mod_policy.json` and are generated for C#
(`ModPolicyConstants.g.cs`) and C++ (`mod_policy_constants.h`).
Evaluator specifics (X2): `ModPolicyEvaluator` is pure (`X4MP.Core.Mods`). A `Required` entry the admin switched off
acts as `Blocked`; `Allowed` never rejects for presence but a Sim/Dlc copy must satisfy the entry's version rule; an
allowlisted library is never rejected for version or hash (an explicit `Required`/`Blocked` entry for it still
applies); in `AdminList` mode the authority's enabled Dlc extensions are still implicit `Required` entries
(ADR-004) while its Sim mods are not. The hash fast path is used only for `AuthorityDefines` with an empty entry
list and `unknown_default != Block`; everything else evaluates the list. Policy source until M1-X3: the live
`X4MP:Mods` settings (`SourceMode`, `UnknownDefault`, `Enforcement`) plus in-memory entries
(`InMemoryModPolicyProvider`); `NetOptions.ExtensionsMismatchIsWarning` still maps to `Enforcement = Warn`.

**Applied (M1-X3, M1-X4, server).** Migration `0008_mods.sql` creates `player_extension_reports` (plus `policy_version`),
`session_mod_policy`, `session_mod_entries` and `mod_catalog`. The server has one standing policy that every session uses, stored
under `session_id = 0` with no foreign key (it is edited before any session row exists and outlives sessions); entries keep the
admin's order (`sort`). `PersistentModPolicyProvider` (`IModPolicyEditor`) loads it on first use; the three knobs come from the
`X4MP:Mods` settings until the first edit, afterwards the stored policy is the truth and a later settings change is adopted as a
newer edit (version + 1, last writer wins). Every real change bumps `version` by one, an edit that changes nothing does not. Every
`ClientHello` stores its full list and the verdict (the gateway holds a rejection back until the player is identified, so an
unauthenticated client can neither learn the policy nor fill the table); the newest 20 reports per player are kept (pruned in the
insert transaction). Reports also teach `mod_catalog` names and Workshop ids (an admin's edit is never overwritten).
`ModPolicyChanged` goes to every announced node on every change; nobody is kicked.

REST (flat, like `/teams`: the server runs one session, so there is no `/sessions/{sid}` prefix). Roles: `Viewer` reads, `ModEditor`
(new role string, Viewer rights + mod edits only) and `Admin` edit. `ModListVisibility` hides players' lists and the catalog from a
`Viewer` under `AdminsOnly` (403 `ModListHidden`; `GET /mods` then returns `playersHidden` and no players); Admin and ModEditor
always see them; `AllPlayers` lets a Viewer read but never edit.

| Method and path | Role | Notes |
|---|---|---|
| `GET /api/v1/mods` | Viewer | `ModsStateDto`: policy with entries, per-player status against the current policy, `canEdit`, `playersHidden` |
| `PATCH /api/v1/mods/policy` | ModEditor | `{sourceMode?, unknownDefault?, enforcement?}` |
| `PUT /api/v1/mods/entries/{extId}` | ModEditor | upsert (201 new, 200 update), omitted fields keep their value; Nexus URL validated, Workshop id derived from `ws_<n>`; also writes `mod_catalog` |
| `DELETE /api/v1/mods/entries/{extId}` | ModEditor | 204 / 404 |
| `POST /api/v1/mods/import-from-authority` | ModEditor | `{merge?: true}`; 409 `NoAuthorityReport` |
| `GET /api/v1/mods/save-requirements` | Viewer | **501** until a `<patches>` reader exists (not built yet) |
| `GET /api/v1/mods/catalog`, `PUT /api/v1/mods/catalog/{extId}` | Viewer, ModEditor | |
| `GET /api/v1/players/{id}/extensions?limit=` | Viewer | newest report in full + history summaries (max 20) |

Audit actions: `mods.policy`, `mods.entry.add`, `mods.entry.update`, `mods.entry.delete`, `mods.import`, `mods.catalog`. Hub: topic
`SubscribeMods`/`UnsubscribeMods` (returns `ModsStateDto` for the caller's role), pushes `ModPolicyChanged(ModPolicyDto)` and
`PlayerModsReported(PlayerModStatusDto)` (the latter only to clients allowed to see players' lists).

---

## 7. Server design impact

- **Domain** (`X4MP.Core`): `ExtensionInfo`, `ExtensionReport{playerId, sessionId, ts,
  hash, items}`, `ModPolicy` (+ entries), `ModPolicyEvaluator` (pure function: report ×
  policy × allowlist → `ModPolicyViolation`), `NexusUrl` value type with validation,
  `WorkshopLinks.From(id)`, `SavePatchesReader` (reads `<patches>` from the gzip head).
- **Gateway** (`NodeGateway`, M1-03): the extensions check calls the evaluator; result
  stored and published as `PlayerModsReported` / `PlayerModsRejected` events.
- **Persistence** (new migration):

```sql
CREATE TABLE player_extension_reports (
  id INTEGER PRIMARY KEY, player_id INTEGER NOT NULL REFERENCES players(id),
  session_id INTEGER REFERENCES sessions(id), ts TEXT NOT NULL,
  ext_hash BLOB, items_json TEXT NOT NULL,          -- [ExtensionInfo]
  outcome TEXT NOT NULL,                            -- 'admitted'|'warned'|'rejected'
  violation_json TEXT);
CREATE INDEX ix_ext_reports_player ON player_extension_reports(player_id, ts);
CREATE TABLE session_mod_policy (
  session_id INTEGER PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
  version INTEGER NOT NULL, source_mode TEXT NOT NULL, unknown_default TEXT NOT NULL,
  enforcement TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL);
CREATE TABLE session_mod_entries (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  ext_id TEXT NOT NULL, name TEXT NOT NULL, rule TEXT NOT NULL, enabled INTEGER NOT NULL,
  class TEXT NOT NULL, version_rule TEXT NOT NULL, version TEXT, content_hash BLOB,
  nexus_url TEXT, workshop_id INTEGER, notes TEXT, sort INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (session_id, ext_id));
CREATE TABLE mod_catalog (                           -- server-wide memory of links/notes/class
  ext_id TEXT PRIMARY KEY, name TEXT, nexus_url TEXT, workshop_id INTEGER,
  class_override TEXT, notes TEXT, updated_at TEXT NOT NULL);
```

  Retention: keep the last 20 reports per player (janitor). `mod_catalog` lets links and
  notes typed once be reused by every new session ("Add mod" autocompletes from it).
- **Admin REST** (`/api/v1`):

| Method and path | Role | Body | Returns |
|---|---|---|---|
| `GET /sessions/{sid}/mods` | Viewer | | `ModPolicyDto` + per-player status |
| `PATCH /sessions/{sid}/mods/policy` | Admin | `{sourceMode?, unknownDefault?, enforcement?}` | `ModPolicyDto` |
| `PUT /sessions/{sid}/mods/{extId}` | Admin | `ModEntryDto` (rule, enabled, class, versionRule, version, nexusUrl, workshopId, notes) | `ModEntryDto` (400 bad Nexus URL) |
| `DELETE /sessions/{sid}/mods/{extId}` | Admin | | 204 |
| `POST /sessions/{sid}/mods/import-from-authority` | Admin | `{merge: bool}` | `ModPolicyDto` (409 if no authority report) |
| `GET /sessions/{sid}/mods/save-requirements` | Viewer | | `SavePatchDto[]` |
| `GET /players/{id}/extensions` | Viewer | `?limit=` | `ExtensionReportDto[]` |
| `GET /mod-catalog` / `PUT /mod-catalog/{extId}` | Viewer / Admin | | catalog |
| `GET /join/mod-manifest`, `GET /join/nonce` (phase 2) | anon / HMAC | | §4.1 |

  All admin writes are audited and bump `policy.version` (pushed as `ModPolicyChanged`).
- **SignalR**: `ModPolicyChanged(ModPolicyDto)`, `PlayerModsReported(playerId, summary)`
  to the Mods and Players pages.
- **GUI**: Sessions → **Mods** page (§4.3; the links/launcher bits are display-only in
  phase 1), Players → detail **Mods** tab, Dashboard badge "1 player rejected for mods".
- **FakeNode**: `--extensions <json>` and `--extensions-preset vanilla|modded|mismatch`.

## 8. Mod design impact

- **Gathering (Lua, start menu).** `x4mp_bridge.lua` calls `GetExtensionList()` as soon
  as the start menu is up (it works there; the Settings → Extensions page uses it) and
  sends `x4mp.extensions` (JSON array of the Lua fields, §1.3) to native. Also sends
  `C.GetModifiedBasegameUIFilesExtensions()`. Re-sent on `/reloadui`. Lua does no file I/O.
- **Enrichment (native, worker thread).** Map ids to folders across the three sources
  (§1.1; X4 dir from `GetModuleFileNameW(nullptr)`, Documents from
  `SHGetKnownFolderPath`, Workshop from `<X4 dir>\..\..\workshop\content\392160`), read each
  `content.xml` (`save`, dependencies), detect DLLs and `subst_*.cat`, compute the class
  hint and the content hash (§3.2, cached). Result kept in `core/session` and used for
  `ClientHello`. If enrichment is not finished when the player clicks Connect, wait up to
  2 s, then send without hashes (`hash_kind = None`).
- **Rejection UI.** The Join dialog status area renders `ModPolicyViolation` (§3.5) with
  link buttons; Multiplayer screen shows the session's mod list when connected.
- **No in-game toggling.** The mod never calls `SetExtensionSettings`; changes need a
  restart and belong to the launcher (phase 2) or the player.
- **`core/` stays X4-free**: the scanner and hasher live in `core/mods/` (pure C++ +
  Win32 file APIs behind an interface), unit-tested with fixture folders.

## 9. Risks, licensing, security

| Risk | Mitigation |
|---|---|
| Wrong `ClientOnly` classification lets a sim mod differ → desync | Conservative default (`Unknown` = `Sim`), allowlist small and reviewed, GUI warning on manual override, phase-3 compat list |
| Version strings lie (silent Workshop updates, Nexus re-uploads) | Content hash (§3.2) |
| Steam auto-updates a Workshop mod between sessions | Next join reports `update`; admin re-imports from the authority |
| Launcher corrupts `content.xml` | Backup + journal + atomic write + only touch changed entries + restore button |
| OneDrive locks/syncs the profile folder | Retry on sharing violations; note in docs |
| Malicious or spoofed server (no TLS on LAN) | It can only (a) toggle mods the player already installed, with confirmation and DLL warnings, and (b) show links restricted to nexusmods.com/x4foundations and Steam Workshop. No downloads, no arbitrary URLs. |
| **Licensing** | Nothing is redistributed. We store only ids, names, versions, hashes and links. Nexus/Steam remain the distribution channel; authors keep their download counts. |
| Mod list reveals what a player has installed | Reports are visible to server admins only (Viewer role), not to other players; documented in the player README |
| Large mod lists bloat `ClientHello` | ~200 bytes per mod; 300 mods ≈ 60 KB, inside `MaxFrameBytes` (1 MiB). Cap at 1,000 entries. |

## 10. Open questions for the user (recommended defaults)

> **Answered 2026-10-01:** the user accepted the defaults for MM1–MM4 and MM6–MM9. MM5 was changed to a configurable setting (below).

| # | Question | Recommended default |
|---|---|---|
| MM1 | What happens to a player with an **unknown mod** (not in the session list)? | `AllowClientOnly`: allowed if it looks client-only (UI/text/visual), rejected otherwise with "disable X". |
| MM2 | Should a mod mismatch **reject** or just **warn**? | `Strict` reject for `Required`/`Blocked` and sim mods. `Warn` is a per-session option for casual play. DLC mismatch always rejects. |
| MM3 | When the admin edits the mod list mid-session, kick players who now violate it? | **No.** Flag them in the GUI and send a notice; the new list applies at their next join. |
| MM4 | Must versions match exactly? | **Exact** for sim mods (plus content hash when available), **any** for client-only. Per-mod override in the GUI. |
| MM5 | Who sees a player's mod list? | **Decided (user, 2026-10-01): a session setting `ModListVisibility`** = `AdminsOnly` (default) / `AdminsAndViewers` / `AllPlayers` (players see each other's lists in game and in a read-only GUI view). There's also an optional per-account **`ModEditor`** permission, so a non-admin GUI account (e.g. a friend helping choose mods) can edit the session mod list without full admin rights. All changes are audited. |
| MM6 | Launcher: restore the player's original mods after the session automatically? | **Yes**, with a "keep this mod set" checkbox. |
| MM7 | Is the authority checked against the admin's list? | **Yes** in `AdminList` mode (catches a host who forgot a mod). In `AuthorityDefines` mode the authority's set is the list. |
| MM8 | Default list mode for a new session? | `AuthorityDefines` until the admin imports or edits; the GUI nudges "Import from authority to add links". |
| MM9 | Should the launcher also toggle Steam Workshop **subscriptions** (unsubscribe blocked mods)? | **No.** Only enable/disable in `content.xml`; subscriptions are the player's business. |
