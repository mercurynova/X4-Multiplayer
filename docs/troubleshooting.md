# Troubleshooting X4MP

Start here: run `Check Install.bat`. It prints one row per check and a verdict, and exits with
code 1 only when something blocks the mod. The rows carry the fix, so in most cases the
first thing to read is the row, not this file.

| Verdict | Means |
|---|---|
| `[blocker]` | The mod cannot run until this is fixed. |
| `[warning]` | The mod may run, but not the way you want (a missing optional piece, a settings file the mod ignores). |
| `[note]` | Information: a port already in use, an optional launcher, a firewall rule you may want. |

Every row has a severity, a text and a fix (that is what `-JsonPath` exports).

## The X4MP window does not appear

1. Is **Protected UI** off? Settings -> Game Settings. It is the single most common cause; the
   game restarts when you change it, and no mod can start while it is on.
2. Are both extensions enabled? Game *Extensions* menu, `x4mp` and `x4native`. If the installer
   could not enable them (you had never started the game before installing), enable them here
   or run `Install.bat` again.
3. Same game build? The mod is built for X4 9.00 build 611726; `version.dat` must say `900`.

## "The extension is not enabled" or "no extension has been enabled yet"

The installer enables `x4mp` and `x4native` in the newest profile's `content.xml` and keeps the
old file as `content.xml.bak-<stamp>`. If the check still complains, the profile was created
after the install (start the game once, then run `Install.bat` again) or the entry is missing
from the list: enable both in the Extensions menu.

## Files blocked by Windows ("internet mark")

Files copied out of a downloaded zip can carry Mark-of-the-Web, and the game then refuses to
load the dll. `Check Install.bat` lists them and treats a blocked dll as a blocker. The
installer clears the mark on every file it copies, so reinstalling from the zip fixes it. If
you copied files by hand: right-click the zip -> *Properties* -> *Unblock* before unpacking.

## "The game is version X, the mod was built for 900"

X4 updated (or you rolled it back) and the native API changed. Install the mod build that
matches your game, or roll the game back in Steam. `Install.bat -Force` installs anyway, which
is only useful if you know the build is compatible.

## "your mods do not match this session"

The host's session has a mod/DLC policy and your extension report differs from it. The
disconnect message names the exact change per extension, for example
`disable: ego_dlc_ventures@1.27` or `update: x4mcp 9.00 -> 9.00`. Do what it says in the
*Extensions* menu and join again. Everyone in a session must run the same X4 build and the
same DLC set.

A related trap on a hosted save: a session whose save has Ventures disabled expects the
Ventures entry to be off *in the extension report that the game writes*. Loading the same save
locally before joining gives you the same report as the host; joining straight from the main
menu can be refused with the message above even though the DLC is installed.

## Joining hangs, or the client restarts

The client downloads the session save and reloads the game once, so a short `stop requested` in
the log followed by a reload is normal. A reload that takes longer than a few minutes usually
means one of:

- the download is slow or blocked: the save comes over 47790/TCP, and the host must be
  reachable on that port as well as 47780/TCP and 47781/UDP;
- not enough free space for the save and its temporary copy (a session save is as large as the
  host's save, tens of MB to a few hundred MB);
- the session ended while you were loading (the host closed the game); the log says so.

`Check Install.bat` reports free space on the game drive and whether the save folder is
writable, which covers the first two.

## "Connection timed out" / cannot reach the host

- The address is `host:47780`; a bare IP is accepted and gets the default port.
- On the host, the sidecar listens on loopback, so players on the LAN or a VPN need the
  firewall rules from [install.md](install.md#hosting-and-joining).
- Only one process can host on a port: `Check Install.bat` notes when 47780/47790/47781 are
  already in use, which usually means a server is already running on that machine.
- A host behind NAT needs port forwarding for 47780/TCP, 47781/UDP and 47790/TCP.

## The server will not start when I host

The mod starts `<game>\extensions\x4mp\server\x4mp-server.exe` as a hidden sidecar. If it
exits immediately, check:

- `%LOCALAPPDATA%\X4MP\logs\server-console.log` for the reason;
- the server bundle is present (Check Install warns when it is not — a client-only install
  cannot host, and that is intentional);
- `x4mp.json` parses. A hand-edited settings file with a broken value is reported by the check
  as a warning; delete it and let the mod write a new one;
- antivirus quarantine: a freshly written exe may be scanned and blocked for a few seconds;
  if it keeps happening, exclude the extension folder.

## Low frame rate with many ships

The client renders every ship the server streams. The mod reports its own cost per frame as
`mod_p95` in the log; a busy sector with hundreds of ghosts costs more than an empty one. If
the client is the bottleneck, the first things to try are a lower graphics preset and joining a
session with fewer ships nearby; the mod itself is designed to keep its own frame cost under
the budget the log prints (`over_budget` counts frames above it).

## The launcher is missing

`x4mp-launcher.exe` ships at the zip root and is optional: the mod and the server work without
it. `Check Install.bat` reports it as a note only. Reinstalling from the zip restores it.

## What to send with a bug report

1. `Collect Logs.bat` and attach the `X4MP-logs-<stamp>.zip` it puts on your Desktop. It
   contains the mod log with all rotations, both log locations, the settings files (join
   secrets redacted) and the profile metadata.
2. `Check Install.bat -JsonPath x4mp-check.json` and attach `x4mp-check.json`.
3. What you did, in order, and the approximate local time. The log timestamps are UTC.
