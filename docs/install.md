# Installing X4MP

This is the player-facing install guide. It covers the one-zip mod package only; if you
want to build from source or run a standalone server, read [dev-setup.md](dev-setup.md)
and [server-admin.md](server-admin.md) instead.

## What is in the zip

```
x4mp-mod-<version>\
  Install.bat               installs the mod into X4
  Check Install.bat         health check: one verdict, exit code 1 when the mod cannot run
  Collect Logs.bat          zips logs and settings for a bug report
  Uninstall.bat             removes the mod, never touches saves
  x4mp-launcher.exe         optional launcher (server + mod plan preview)
  installer\                the scripts the .bat files call (Common/Install/CheckInstall/CollectLogs/Uninstall)
  extensions\x4mp\          the mod: content.xml, ui, md, libraries, native\x4mp.dll, server\x4mp-server.exe
  extensions\x4native\      X4Native, the native API the mod is built on
```

The mod ships its own server as `<game>\extensions\x4mp\server\x4mp-server.exe`. You never
start it by hand: the mod starts it as a hidden sidecar when you host, and stops it when the
game exits. Everything it writes goes to `%LOCALAPPDATA%\X4MP`.

## Requirements

- X4: Foundations 9.00, build 611726 (the installer refuses another `version.dat` unless you
  pass `-Force`).
- Windows 10 or newer, 64-bit, with the Visual C++ 2015-2022 redistributable (almost every
  game install already has it; Check Install tells you if it is missing).
- Steam's X4 install. The installer finds it through Steam; pass the folder if it does not:
  `Install.bat "D:\Steam\steamapps\common\X4 Foundations"`.
- About 1 GB free on the game drive while installing (the package unpacks next to the mod).

## Install

1. Close X4. The installer refuses to run over a running game, because the dll is loaded.
2. Unpack the zip somewhere writable (not inside Program Files).
3. Run `Install.bat`. Windows may show a SmartScreen prompt for the script: choose
   *More info -> Run anyway*. The installer removes the Mark-of-the-Web from the files it
   copies, so the game can load them.
4. Read the last lines. The installer:
   - backs up any existing `extensions\x4mp` and `extensions\x4native` to
     `%LOCALAPPDATA%\X4MP\install-backup\<stamp>\` before overwriting them;
   - copies both extension trees and verifies the files it expects to find (`content.xml`,
     a 64-bit `native\x4mp.dll`, `ui`, `md`, `libraries`, `t\0001-l044.xml`, `x4native.json`,
     `x4native\native\x4native_64.dll`);
   - puts the previous version back and exits with an error if that verification fails;
   - enables `x4mp` and `x4native` in the newest profile's `content.xml` (a copy of the old
     file is kept as `content.xml.bak-<stamp>`).
5. Do the two things the installer cannot do for you:
   - **Settings -> Game Settings -> turn "Protected UI" off.** The game restarts. No mod can
     start while it is on.
   - Start a game and open the **X4MP** window from the main menu.

If you started X4 before installing, there was no profile yet and the installer could not
enable the extensions. Run `Install.bat` again, or enable both extensions by hand in the
game's *Extensions* menu.

## Check that it works

Run `Check Install.bat`. It prints one row per check and a verdict, and exits with code 1 only
when something blocks the mod. It looks at:

- the game folder, `X4.exe` and `version.dat`;
- every file the mod needs, including a 64-bit `native\x4mp.dll` and the `version_db`
  functions for build `900-611726`;
- Mark-of-the-Web on the extension trees (a blocked dll is a blocker);
- the profile: `config.xml` protected UI off, `content.xml` with both extensions enabled;
- the Visual C++ runtime;
- the server bundle, the log folder, the save folder and free space on the game drive;
- the launcher beside the game, the settings files `x4mp.json` (both locations) and the
  sidecar exe being a 64-bit image;
- ports 47780/TCP, 47781/UDP and 47790/TCP, plus a hint if no `X4MP*` firewall rule exists.

`Check Install.bat -JsonPath out.json` writes the same rows as JSON
(`{version, game, profile, problems[]}`), which is what to attach to a bug report.
A `blocker` means the mod cannot work; a `warning` means it may not work as intended; a
`note` is information (a port already in use, an optional launcher missing, a firewall rule
you may want).

## Hosting and joining

- **Host:** open the X4MP window, choose *Host*. The mod starts its hidden server, you load
  your save, and the session opens on 47780/TCP (control), 47781/UDP (realtime) and
  47790/TCP (HTTP: save transfer and the local server pages). The generated admin password is
  in the X4MP window; share the join address, the password and the same DLC set with your
  friends.
- **Join:** enter the host address, the password and your name. The client downloads the
  session save first; that is the only moment the game closes and reloads itself.
- Everyone must run the same X4 build and the same DLC set. A mismatch is refused with the
  exact list of extensions to enable or disable.

Windows Firewall: the sidecar listens on loopback only, so hosting on one PC for players on
the same LAN or a VPN needs the rule the health check prints:

```powershell
New-NetFirewallRule -DisplayName "X4MP server TCP" -Direction Inbound -Protocol TCP -LocalPort 47780,47790 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "X4MP server UDP" -Direction Inbound -Protocol UDP -LocalPort 47781 -Action Allow -Profile Private
```

## Logs

- Mod log: `%LOCALAPPDATA%\X4MP\logs\x4mp.log` (rotates at 20 MB into `x4mp.log.1`..`x4mp.log.4`).
- A portable/two-instance install also writes `<game>\extensions\x4mp\logs\x4mp.log`.
- Server: `%LOCALAPPDATA%\X4MP\logs\server-console.log` and the server data folder.

`Collect Logs.bat` gathers all of the above, both log folders and every rotation, the settings
files and the profile metadata into `X4MP-logs-<stamp>.zip` on your Desktop. Join secrets are
redacted: `password` and the admin password are replaced with `<redacted>` before the zip is
written, and a settings file that cannot be parsed is skipped rather than shipped raw.

## Updating

Close X4 and run `Install.bat` from the new zip. Your previous extension folders are kept in
`%LOCALAPPDATA%\X4MP\install-backup\<stamp>\`, so a bad update can be undone by copying them
back (the installer already does this automatically when its own verification fails).

## Uninstalling

Close X4 and run `Uninstall.bat`. It removes `extensions\x4mp` and `extensions\x4native` and
nothing else: saves, profiles and screenshots are never read or written. Options:

- `-KeepX4Native` leaves X4Native in place (for another mod that needs it);
- `-RemoveData` also deletes `%LOCALAPPDATA%\X4MP` (logs, settings and any server data).

The extension entry stays in the profile's `content.xml`; the game ignores an entry whose
folder is gone, and you can untick it in the Extensions menu to keep the list clean.

## Verifying a download

Every release publishes `SHA256SUMS` and `SHA256SUMS.asc` next to the zips, plus
`x4mp-release-signing-key.asc` (the public half of the key that signed them). With GnuPG:

```powershell
gpg --import x4mp-release-signing-key.asc
gpg --verify SHA256SUMS.asc SHA256SUMS
# then check that the zip you downloaded matches its line
Get-FileHash .\x4mp-mod-<version>.zip -Algorithm SHA256
```

The first `gpg --verify` must say *Good signature*; the hash must match the entry for that
file in `SHA256SUMS`. A zip whose hash does not match was truncated or tampered with: do not
run its installer.

(The key lives in the repository secret `RELEASE_GPG_PRIVATE_KEY` and is used only by
`.github/workflows/release.yml`. A release without that secret fails instead of shipping
unsigned checksums.)

## When something goes wrong

See [troubleshooting.md](troubleshooting.md). Attach the `Collect Logs` zip and the JSON
from `Check Install.bat -JsonPath`.
