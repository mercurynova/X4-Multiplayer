# Two X4 instances on one PC (local multiplayer test stand)

Status: verified 2026-11-02 on X4 9.00 build 611726, Windows, Steam build of the game.
Purpose: run one X4MP **authority** and one X4MP **client** on a single machine, side by side,
when a second PC (or a VM with a second GPU) is not available.

Read this together with [`../../docs/dev-setup.md`](../../docs/dev-setup.md) (mod deployment)
and [`../../docs/server-admin.md`](../../docs/server-admin.md) (server). Acceptance steps for
the session itself stay in [`../../docs/two-pc-checklist.md`](../../docs/two-pc-checklist.md) —
they do not depend on whether the two nodes are two PCs or two windows.

## 1. What this stand gives you, and what it does not

It gives you both nodes in front of you at once: each window is a full game instance with its
own profile, its own mod state and its own log. Joining, ghost spawn, economy, save control and
the HUD behave exactly as they do across two machines, because the mod sees a normal network
peer in both cases.

It does **not** prove: real network latency (measured RTT here is 0.2–0.4 ms), hardware or DLC
differences, GPU contention effects, or anything about behaviour when two machines boot at
different speeds. Do not quote frame rates from this stand — both instances share one GPU and
one CPU, and the menu ran at roughly 12 fps even with a 30 fps cap set.

## 2. Prerequisites

| Requirement | Why |
|---|---|
| X4 9.00 (Steam) installed and launched once | the profile folder and the settings file must exist |
| Steam client running and logged on | X4 9.00 has no `-nosteam`-style switch (checked in the binary); DLC unlockers such as CreamAPI only unlock DLC, they do not remove the Steam dependency |
| Mod deployed into the first install | `tools/deploy.ps1` |
| Desktop at least two windows wide | 2 × 1280 = 2560 px used below |
| ~2 × 3 GB free RAM | each instance held ~2.8 GB working set at 1280x720 |
| Second install on the same volume as the first | hard links cannot cross volumes |

## 3. Step 1 — a second install

Extensions and the mod's portable state are resolved **from the install folder**, so two
instances must not share one install:

```
<X4 install>\extensions\x4mp\        shared  -> one mod state, one log, one identity
<second install>\extensions\x4mp\    separate -> isolated mod state
```

The second install does not cost a second copy of the game: everything is a hard link, and only
the folders the mod writes to (plus the patched executable) are real copies.

```powershell
$src = '<X4 install>'        # e.g. C:\SteamLibrary\steamapps\common\X4 Foundations
$dst = '<second install>'    # must be on the same volume as $src
$copyRoots = @('extensions\x4mp', 'extensions\x4native', 'extensions\x4mcp')

New-Item -ItemType Directory -Force $dst | Out-Null
$linked = 0; $copied = 0
foreach ($f in Get-ChildItem $src -Recurse -File) {
    $rel = $f.FullName.Substring($src.Length).TrimStart('\')
    $target = Join-Path $dst $rel
    $dir = Split-Path $target -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $isCopy = ($rel -like 'extensions\x4mp*') -or ($rel -like 'extensions\x4native*') -or ($rel -like 'extensions\x4mcp*')
    if ($isCopy) {
        Copy-Item $f.FullName $target -Force; $copied++
    } else {
        if (Test-Path $target) { Remove-Item $target -Force }
        New-Item -ItemType HardLink -Path $target -Target $f.FullName | Out-Null; $linked++
    }
}
"linked=$linked copied=$copied"
```

Two consequences worth knowing:

- Deleting a hard-linked file in one install does not affect the other, but **writing into** a
  hard-linked `.cat`/`.dat` would change both. The game only reads them.
- `<second install>` also gets a copy of the stock `X4.exe`. It is unpatched (step 2) and will
  hit the single-instance guard — launch only `<second install>\X4b.exe`. Deleting the copy is
  the safest option.

## 4. Step 2 — patch the single-instance guard

X4 creates a named mutex called `EGOSOFT_X4_INSTANCE` during startup, and a second process that
finds it exits immediately. The name is stored as a UTF-16LE string in the executable, so
rewriting it in a **copy** of the binary is enough:

```powershell
function Set-InstanceMutexName {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string]$Old = 'EGOSOFT_X4_INSTANCE',
        [string]$New = 'EGOSOFT_X4_INSTANCX'
    )
    $bytes = [System.IO.File]::ReadAllBytes($ExePath)
    $enc   = [System.Text.Encoding]::Unicode
    $oldB  = $enc.GetBytes($Old); $newB = $enc.GetBytes($New)
    if ($oldB.Length -ne $newB.Length) { throw 'old and new mutex names must be the same length' }

    $hits = 0; $pos = 0; $target = -1
    while (($pos = [Array]::IndexOf($bytes, $oldB[0], $pos)) -ge 0) {
        $match = $true
        for ($i = 1; $i -lt $oldB.Length; $i++) { if ($bytes[$pos + $i] -ne $oldB[$i]) { $match = $false; break } }
        if ($match) { $hits++; $target = $pos }
        $pos++
    }
    if ($hits -eq 0) { throw "mutex name '$Old' not found - already patched or wrong build" }
    if ($hits -gt 1) { throw "mutex name '$Old' found $hits times - refusing to guess" }

    [Array]::Copy($newB, 0, $bytes, $target, $newB.Length)
    [System.IO.File]::WriteAllBytes($ExePath, $bytes)
    "patched $ExePath at byte offset $target"
}

Copy-Item '<X4 install>\X4.exe' '<second install>\X4b.exe' -Force
Set-InstanceMutexName -ExePath '<second install>\X4b.exe'
```

Rules:

- Patch only the copy. `<X4 install>\X4.exe` must stay untouched — Steam will repair or overwrite
  it, and the first instance must keep its normal name.
- The replacement is exactly the same length, so no offsets inside the PE change; the patched
  binary is byte-for-byte the stock one apart from those 20 characters.
- Running the function twice fails on purpose (`not found`), because the second pass would find
  nothing to replace. Restore the original exe and patch again if you ever need to redo it.

## 5. Step 3 — two player profiles

Each instance gets its own profile folder, selected with `-personalfolderid`:

```
X4.exe  -personalfolderid <profile A> ...
X4b.exe -personalfolderid <profile B> ...
```

The folder is `Documents\Egosoft\X4\<profile id>\` and holds `config.xml`, saves, `uidata.xml`
and the per-instance logs. Never let both instances share one profile id: `config.xml` and
`uidata.xml` are rewritten on exit, and the second instance would inherit the first one's saves.

The numeric ids are arbitrary; `700000001` / `700000002` are used here because they cannot
collide with a real Steam id.

## 6. Step 4 — settings, written into `config.xml`

Configure both profiles by editing `Documents\Egosoft\X4\<profile id>\config.xml`, not through
the in-game menu: the file is what the game reads at start, it can be diffed, and it survives a
crash before the menu.

The keys that matter for a two-instance stand (all names verified against a 9.00 `config.xml`):

| Key | Value | Why |
|---|---|---|
| `<fullscreen>` | `false` | two windows must be visible at once |
| `<borderless>` | `false` | borderless means windowed-fullscreen: the window would cover the whole desktop and the two instances would overlap |
| `<res_width>` / `<res_height>` | `1280` / `720` | two windows on one screen, and the cheapest render load |
| `<uisafemode>` | `false` | **required for the mod**: see below |
| `<frameratelimit>` / `<frameratetarget>` | `true` / `30` | one GPU serves both instances |
| `<gfxquality>`, `<texturequality>`, `<shadows>`, `<ssao>`, `<glow>` | `0` / `low` / `1` / `0` / `0` | keeps both instances usable while testing logic, not visuals |
| `<lodfactor>`, `<effectdistance>` | `0.00` | large win in the map and in busy sectors |
| `<nosound>`, `<volumetotal>` | `true` / `0.00` | two audio devices are not needed and OpenAL is the first thing to break on a shared stand |
| `<chardensity>`, `<trafficdensity>` | `0.50` | halves the NPC load on both instances |
| `<gamespeed>` | `0.5` | optional; slows the simulation so a shared CPU keeps up |
| `<sendcrashreports>` | `false` | **check this**; with two instances writing dumps the install fills with `.dmp` files |

### Protected UI Mode must be off

`<uisafemode>false</uisafemode>` is not a performance setting, it is a requirement. In safe
mode the Lua sandbox refuses the calls X4Native needs:

```
[X4MP] X4Native settings menu: __X4NATIVE_API not set - skipping
Restricted function OnlineGetVersionIncompatibilityState() called from non-verified source 'extensions/x4mp/ui/x4mp_optionsmenu_adapter.lua'! Aborting call!
```

`__X4NATIVE_API` stays unset because the sandbox blocks `require("debug")` inside X4Native, so
the whole native bridge is dead even though the extension loads. Setting the key in
`config.xml` is more reliable than the in-game checkbox: it cannot be forgotten per profile.

Note that `<sounddevice>` remembers a machine-specific device string. Keep it out of shared
configs; it carries no meaning for anyone else.

## 7. Step 5 — portable mod state for the second instance

By default the mod keeps its state in `%LOCALAPPDATA%\X4MP` (`x4mp.json`, `launch.json`,
`logs\x4mp.log`). Two instances would share that folder and interleave into one log.

Create a zero-byte marker in the second install:

```powershell
New-Item -ItemType File -Force '<second install>\extensions\x4mp\x4mp.portable'
```

`resolve_config_paths()` (`native/host/paths.cpp:45-70`) then treats the extension folder as the
config dir, so the second instance writes:

```
<second install>\extensions\x4mp\x4mp.json
<second install>\extensions\x4mp\launch.json
<second install>\extensions\x4mp\logs\x4mp.log
```

The first instance keeps the normal location. From here on, "the mod log" means
`%LOCALAPPDATA%\X4MP\logs\x4mp.log` for the authority and
`<second install>\extensions\x4mp\logs\x4mp.log` for the client. Mod log timestamps are UTC
with a `Z` suffix.

## 8. Step 6 — free the tool ports

`x4mcp` listens on a fixed port from its own config file; two instances cannot both own 7779:

```powershell
# <second install>\extensions\x4mcp\x4mcp_config.json
{ "port": 7780, "bind_address": "127.0.0.1" }
```

The X4MP ports (47780/TCP, 47781/UDP to the server) need no change: both instances connect to
the server as ordinary clients, and the server tells them apart by player id. If the realtime
lane misbehaves on the second instance, the `[perf] stats:` line reports the state of that
instance's UDP path (`udp=`, `udp_binds=`, `udp_fb=`, `udp_reprobe=`), and the mod log names the
bind (`net: udp: binding to 127.0.0.1:47781 (mode auto)`).

## 9. Step 7 — launching

```
# authority, first install
cd <X4 install>
X4.exe -personalfolderid <profile A> -logfile authority.log -skipintro -debug all

# client, second install
cd <second install>
X4b.exe -personalfolderid <profile B> -logfile client.log -skipintro -debug all
```

Start the authority first, then the client about 8 s later. `-logfile <name>` lands in the
instance's own profile folder (`Documents\Egosoft\X4\<profile id>\`), so the two logs never
collide. `-debug all` keeps the Lua errors (`[=ERROR=]` lines) in the game log; without it you
lose the mod's message logging.

Timings observed at these settings: the authority reaches `Entering startmenu` after **160 s**,
the client after **195 s** (starting the client while the authority is still booting costs it
extra time). Budget 2–3 minutes and do not diagnose a hang before that.

**The game log stops growing after the main menu.** Both logs ended at ~8 KB containing
`Entering startmenu`, and nothing further is written while the game sits in the menu, even
though the process is alive and the mod is exchanging packets. Use the mod log for liveness, not
the game log size.

## 10. Step 8 — putting the windows side by side

[`../tools/position-windows.ps1`](../tools/position-windows.ps1) pins one window to the left edge
and the other to the right edge and keeps correcting them:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\position-windows.ps1
# optional: -LeftProcess X4 -RightProcess X4b -Seconds 300 -Poll 1
```

Why it looks the way it does:

- **Client area, not window rect.** An X4 window at 1280x720 is 1296x759: 8 px border on each
  side, 31 px title bar. Aligning window rectangles leaves an 8 px strip of desktop visible at
  both outer edges — that is the "gap" you see if you use plain window coordinates.
- **Move only.** `SetWindowPos` with a new size does not stick; X4 restores its own window size
  on menu and save transitions. The script passes `SWP_NOSIZE`.
- **Loop.** The game also resets its own position, so the script re-applies the offsets every
  two seconds. Positions on disk are not remembered by `config.xml`, which is why a helper is
  needed at all.

Expected output when a correction is needed:

```
virtual screen: 0,0 2560x1440; left=X4 right=X4b
X4 pid=17604 window 500,150 -> -8,-31 (client 0..1280 x 0..720)
X4b pid=7404 window 500,150 -> 1272,-31 (client 1280..2560 x 0..720)
```

The window's left edge is negative so that the 8 px border hangs off-screen and the render area
starts at x=0; the title bar is above the screen for the same reason. Result on a 2560x1440
desktop: render areas 0…1280 and 1280…2560 — no gap at the edges, no overlap at the seam.

If you prefer a different split (for example 60/40), the client areas must still tile exactly;
nothing else in the mod depends on the geometry.

## 11. Step 9 — auto-connect on start

A one-shot file makes an instance connect by itself at native init, so nobody has to click
through the Join screen twice per restart.

The schema is `native/features/launch/launch_json.h`:

```json
{
  "server": "<server>:47780",
  "name": "Host",
  "role": "authority",
  "admin_password": "<server admin password>",
  "expires_utc": "2026-11-02T12:00:00Z"
}
```

`server` and `name` are required; `role` is `client` (default) or `authority`;
`admin_password` is only meaningful for an authority request; `expires_utc` is optional and a
request past it is ignored.

Where the file goes:

| Instance | Path |
|---|---|
| normal mode | `%LOCALAPPDATA%\X4MP\launch.json` |
| portable mode (step 5) | `<second install>\extensions\x4mp\launch.json` |

Behaviour that trips people up (`native/features/launch/launch_feature.cpp`):

- The file is **deleted immediately**, also when it is invalid, expired or ignored. "It exists,
  therefore it works" is false — check the mod log for `launch.json consumed: ...` instead.
- It is ignored on a save reload (`launch.json ignored: this is a reload (#N), the resume takes
  over`), so it cannot be used to re-join after reloading a save.
- It fires at init only. Relaunching the game after a crash therefore needs a new file, or a
  manual Join.

A successful pair looks like this in the mod logs (UTC):

```
[sess] launch.json consumed: server=<server>:47780 name=Host role=authority password=<redacted> admin_password=<redacted>
[auth] Welcome: player_id=1 roles=11 resumed=false
[sess] launch.json consumed: server=<server>:47780 name=Client role=client password=<redacted> admin_password=<redacted>
[auth] Welcome: player_id=2 roles=2 resumed=false
```

## 11a. Joining a session that carries a save reloads the UI once - give it three minutes

A join into a session with a save (the normal case on this stand) is not finished when `Welcome` arrives:

| after the Join | log line | meaning |
|---|---|---|
| +15 s | `[host] config reloaded (ui reload)`, `[auth] Welcome: ... resumed=true` | the downloaded session save is being loaded, X4 reloads the UI, the extension re-inits and re-joins as a resume |
| +1.5-2 min | `[sess] game loaded (stage 4)` | the pushed save finished loading |
| +2.5-3 min | `[sync] ghosts tracked=...` (guest), a new `CaptureSet epoch` (authority) | the world stream is live |

Verified 2026-11-08 on the two-instance stand. Waiting for a reload *before* joining is pointless - the join causes
it - and a session that is `InSession` with `rx` around 140 B/s is still loading, not wedged. `launch.json` cannot
help here either (see above): on the reload it is ignored and the resume takes over.

## 12. Verifying the stand

Run these after both windows are in the main menu.

```powershell
# 1. both game processes alive, one instance per exe
Get-Process X4, X4b | Select-Object Id, ProcessName, Responding

# 2. two sockets to the game port, one per instance
Get-NetTCPConnection -State Established -RemotePort 47780 |
    Select-Object LocalPort, RemoteAddress, OwningProcess

# 3. server alive
(Invoke-WebRequest 'http://127.0.0.1:47790/healthz' -UseBasicParsing).Content

# 4. join evidence - the authoritative record
Select-String '<server data dir>\logs\server-*.log' -Pattern 'joined as|admitted' | Select-Object -Last 4
```

What "good" looks like:

```
player 1 (Host) joined as "Authority, Client, Admin"
session "<id>": "WaitingForAuthority" -> "AuthorityLoading" (authority admitted)
player 2 (Client) joined as "Client"
conn 6: admitted Client (player 2)
```

The session then stays in `AuthorityLoading` until the authority loads a save — that is the
expected state, not a failure. Once the save is up it moves on and both nodes see the world.

Liveness afterwards: the mod log prints a `[perf] stats:` line about every five seconds, per
instance:

```
[perf] stats: fps=12.1 frame_p95=86.21ms mod_p95=0.115ms mod_max=0.185ms connected=1 rtt=0.4ms ... udp=active udp_binds=1
```

`connected=1` on both instances means the node link is up. `mod_max` staying in the sub-
millisecond range means the mod itself is not what slows the game down.

## 13. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| The second instance exits within a second, nothing in its log | single-instance mutex still in place, or the stock `X4.exe` from the second install was launched | run `<second install>\X4b.exe`, and re-apply step 2 to that file |
| Both windows show the same player, same saves | both instances use the same `-personalfolderid` | give each instance its own id (step 3) |
| Game opens on the Settings screen and asks to save settings at every start | stale `uidata.xml`: it persists a pending "save settings?" question (observed with a stored `oldfullscreen=true` while `config.xml` had `<fullscreen>false</fullscreen>`) | close the game, delete `Documents\Egosoft\X4\<profile id>\uidata.xml`, start again — it is regenerated |
| `__X4NATIVE_API not set` and `Restricted function ... Aborting call!` in the game log | Protected UI Mode is on for that profile | `<uisafemode>false</uisafemode>` in that profile's `config.xml` (step 4) |
| An 8 px strip of desktop at the left and right screen edges | alignment done on window rectangles | use `tools/position-windows.ps1` (step 8) |
| Game log frozen at ~800 bytes for minutes | normal — the log does not grow after the main menu | read the mod log instead; give the boot 2–3 minutes |
| No auto-connect after a restart | `launch.json` was consumed at the previous start | recreate it before launching (step 9) |
| Only one instance appears in `Get-Process X4` | the second process is named `X4b` | query `Get-Process X4, X4b` |
| Both instances crawl in game | one GPU, one CPU, two full simulations | lower `fps`, `chardensity`, `trafficdensity`, `effectdistance` further, and treat the stand as a logic test only |

## 14. Known issues seen on this stand

Both entries that were found on this stand first are fixed as of 2026-11-02: the spurious
`money: send refused` at every start, and the reconnect grace that was shorter than a boot — the
default is 300 s now, so a restart of both instances resumes instead of rejoining. The stand's
later run found two more, both answered on 2026-11-03: every transfer and loan between two players
of one team is refused `SameWallet` unless the server runs `Economy:CreditMode=PerPlayer` — the
default stays, but the refusal now names that setting — and the mod could not offer a loan or
answer one, which it now can with `/mp offer <player> <credits> [repay] [days] [memo]` and
`/mp accept`/`/mp decline`. What is still open is the trade proposal, which needs an asset list the
mod does not have. All four, plus what looks like a bug but is not:
[`known-issues.md`](known-issues.md).

## 15. Related

- [`../../docs/dev-setup.md`](../../docs/dev-setup.md) — mod deployment, game settings, ports
- [`../../docs/server-admin.md`](../../docs/server-admin.md) — running the server, the GUI settings page
- [`../../docs/two-pc-checklist.md`](../../docs/two-pc-checklist.md) — the acceptance checks themselves
- [`../tools/position-windows.ps1`](../tools/position-windows.ps1) — the window helper
