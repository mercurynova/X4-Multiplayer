# In-game test session 6 (M3b acceptance: two players on foot see each other)

> **Status (2026-10-26): written from the code as committed at `03dd1cd`, not yet run.** Everything named here exists in that build
> except the one measurement line added after it (`onfoot: avatar pid=... lag=...m`, see 6.3): build once more before the session.
> Session 5 was the on-foot spike ([spikes/session-5-onfoot-results.md](spikes/session-5-onfoot-results.md)); this is the first run
> of the finished feature. Claude can't play X4, so you run the steps and send back the log zips plus your notes.

M3b is "on-foot presence v1" ([roadmap.md](roadmap.md) row M3b, [mod-design.md](mod-design.md) 4.12). The code half is done: capture,
remote avatars, appearance, Talk menu, presence list. What is left is exactly what a machine cannot check:

| # | Acceptance criterion | Step |
|---|---|---|
| 1 | Two players on foot in the same loaded interior see each other at the correct position (error < 0.5 m) and heading | 6.3, 6.4 |
| 2 | The Talk menu works both ways (wave, message) | 6.6 |
| 3 | The HUD presence list is correct for any shared container | 6.5 |
| 4 | A chosen avatar appearance shows up on the other player | 6.7 |
| 5 | Zero MP actors in any save (`tools/savescan`), and a presence save loads without the mod | 6.8 |

Known limits of v1, so you do not chase them: the avatar **does not walk** (MD has no locomotion action; it glides in the idle pose
while position and heading come from the network); a spacesuit player on a platform is not reported; a doorway or a lift reports
"not on foot" for a moment, so an avatar can blink for a second there.

## 6.0 Before you start (~15 min, both PCs)

Reuse the session-4 kit; nothing new is needed.

1. **Back up** `Documents\Egosoft\X4\<id>\save\` on both PCs (OneDrive paused, Steam Cloud syncs it). Same X4 build **611726** and the
   same DLCs on both PCs.
2. Steam launch options on both: `-debug all -logfile x4mp_s6.log`. Steam auto-update for X4 off. In X4 bind **Toggle Chat Window**
   (Settings > Controls).
3. Build once on PC A: `powershell -ExecutionPolicy Bypass -File mod\build.ps1`, then
   `powershell -ExecutionPolicy Bypass -File tools\session4\install.ps1`. On PC B either run the same two commands from a copy of the
   repo or use the kit zip from `tools\session4\make-client-kit.ps1` (the way session 4 sitting 3 did).
4. X4 > Settings > Extensions on both: **Protected UI Mode OFF**, **x4native** and **X4 Multiplayer** on.
5. Start the server on PC A: `powershell -ExecutionPolicy Bypass -File tools\session4\start-server-lan.ps1 -SaveName save_004`
   (it prints the firewall lines and the admin password; PC B joins `<PC A address>:47780`).
6. Host on PC A with your usual name, then join on PC B. Both must be **Connected** before you walk anywhere.

**The hard part is meeting.** There is no lounge by design: you see each other only where both are loaded, so both players have to be
in the **same interior of the same station**, in the **same room**. Recipe: agree on one station before you start (the one nearest
your start sector is fine), both dock there, both walk into the same public room (bar, crew quarters, dock level), and stay there for
the whole session. The mod matches a room by its macro **and** its position inside the station, so two different rooms of the same
type do not count as the same place.

Log files: mod log `%LocalAppData%\X4MP\logs\x4mp.log` on each PC, game log `x4mp_s6.log` in the X4 user folder.

## 6.1 Sanity: the room report works at all (2 min, each PC alone)

Do this before you meet, on either PC, standing in the station.

*Do:* nothing. *Look:* the mod log.

Expected, once per second while you stand (grep `onfoot:`):

```
onfoot: bridge verb x4mp.onfoot_room subscribed
onfoot: bridge verb x4mp.onfoot_actor subscribed
onfoot: bridge verb x4mp.onfoot_talk subscribed
```

and every 30 s a line with `reports=` above 0. If `bad room report (field ...)` appears, send that line: the field name is the whole
diagnosis. If `reports=` stays 0 while you walk around a station, the MD half is not running (check the extension list, then the log).

## 6.2 Presence list before you meet

*Look:* the HUD status line (the small X4MP line) and the Multiplayer screen (chat `/x4mp`, then **Players**).

Expected: PC B's player table shows PC A as **on foot** (not "here") while you are in different places, and the HUD line shows no
names. Walk to the same room (6.3) and both change to **here**.

## 6.3 Meeting: position and heading (the main step, ~20 min)

*Do:* both players stand in the same room, 2-3 m apart, and take turns:

- **A stands still, B walks around A** in a circle, then B stands still and A walks around B.
- **A walks a straight line** 10 m across the room and back, twice: once walking, once sprinting if you can.
- **A stands still and turns on the spot** (look around slowly), B watches the avatar's head and shoulders.

*Look on the other PC:*

| What | Expected |
|---|---|
| The avatar | a normal station NPC (crew look) with the player's name over it |
| Where it stands | on the floor at the other player's real position, not floating and not sunk |
| Heading | it faces where the other player faces, including while turning on the spot |
| Motion | smooth gliding, no stepping, no snapping back and forth, no avatar popping in and out |
| Leaving the room | walk out of the room: the avatar disappears within a second; come back: it appears again |

*Log (on the **watching** PC), grep `onfoot: avatar`:* one line per avatar per 5 s.

```
onfoot: avatar pid=7 lag=0.12m speed=1.40m/s age=91ms
```

`lag` is how far behind the newest network sample the avatar is drawn. **That is the "error < 0.5 m" number**: it must stay below
0.5 m while the other player walks, and read 0.00 m while they stand. `speed` is their speed (1.4 m/s walking, ~4 m/s sprinting),
`age` is the age of the newest sample (expect 50-150 ms on a LAN/Tailscale, up to ~300 ms over the internet).

*Write down:* your rating 1-5 for smoothness, the highest `lag` you saw while walking, and anything the avatar did wrong (floating,
sunk, facing backwards, sliding sideways, blinking in a doorway).

If the avatar is **not there at all**: check the watching PC's log for `onfoot: avatar pid=` (nothing = the actor was never spawned:
look for `onfoot: linked=` and `actors=0/1` in the 30 s line, and for `onfoot spawn: no character` from MD), and for
`onfoot: bad room report`. If the avatar is there but in the wrong place, note whether it is off by roughly a room (anchor problem)
or by 1.7 m (height problem) - both are one-line fixes.

## 6.4 Rooms: the second room

*Do:* both walk together into a **different** room (a corridor, then a bar), stopping in each for ~10 s.

*Look:* the avatar follows you from room to room without disappearing for more than a second at the doorway. Standing in different
rooms of the same station, you must **not** see each other.

## 6.5 The presence list

*Do:* with both in the same room, open the chat window and type `/x4mp` > **Players**, then walk apart into different rooms.

*Look:*

- Same room: the HUD line shows `<name> here`; the player table shows the same player as **on foot, here**.
- Different rooms of the same station: the table says **on foot** (no "here"), the HUD line has no names.
- A player who leaves the station (undocks, flies away): **in a cockpit** / **in transit** as appropriate.
- First arrival and last departure print a chat line: `<name> is here` / `<name> is no longer here`.

## 6.6 The Talk menu (both ways)

*Do:* **A**: walk up to B's avatar, target/interact with it, open the conversation. Pick **Wave**. Then pick **Message**.
Then **B** does the same to A's avatar.

*Look:*

- The conversation with the avatar opens the vanilla NPC conversation, with **Wave**, **Message** and **Goodbye** at the top.
- Picking **Wave** makes **every** viewer in the room see the avatar nod (`nod01`), and the other player gets the chat line
  `<name> waves at you` plus `You wave at <name>` on the sender's side.
- Picking **Message** prints `To whisper <name> type /w <name> your message` (the vanilla chat cannot be prefilled). Type it once and
  check the whisper arrives.
- **Goodbye** closes the conversation and nothing else happens.

## 6.7 Appearance

*Do:* on each PC: chat `/x4mp` > **Appearance**. Cycle the race and the look, press **Random** once, then settle on something
obvious (e.g. a Teladi). Then walk out of the room and back in, or leave and rejoin the session.

*Look on the other PC:* the avatar changes to the chosen race/look. The look is applied when the avatar is (re)created, so a change
made while you are standing in the room shows up on the next spawn - that is expected.

*Also check:* **Default** puts you back to the station's own crew look, and your **own** character never changes (this is what others
see, not you). The choice survives a restart of X4 (it is stored in `x4mp.json` as `avatar_race` / `avatar_preset`).

## 6.8 Save hygiene (criterion 5)

Do this **while an avatar of the other player is visible** - that is the whole point.

1. On PC A: Esc > **Save Game** into a **new slot** you can throw away (write the slot name down).
2. On PC A: `powershell -ExecutionPolicy Bypass -File tools\session4\savescan.ps1 -Save <slot name> -Role Client`
   (use `-Role Any` if it complains about the role). Expected: `[MP] objects: 0` and exit code 0.
3. **Load that save without the mod**: copy it to a normal slot with Explorer, run
   `powershell -ExecutionPolicy Bypass -File tools\session4\uninstall.ps1`, start X4 **single player** and load it.
   *Look:* the game loads normally, the player is where the save was made, and there are **no leftover NPCs** with a player name
   standing in the station. Note exactly what X4 says if it complains.
4. Reinstall (`install.ps1`) afterwards.

If `savescan` finds objects, send the save name and the scan output: the fix is to mark the avatars `temporary` in MD (vanilla's
mission actors use exactly that), which is a one-word change, but it must be checked in game, so it is worth knowing now.

## 6.9 After the session

1. Quit X4 on both PCs, then on each: `powershell -ExecutionPolicy Bypass -File tools\session4\collect-logs.ps1 -Label s6`
   (before the next X4 start: X4 overwrites its log).
2. `uninstall.ps1` on both PCs, remove the launch option.
3. Send: the two zip names, your notes with clock times, the 1-5 ratings, the highest `lag` per direction, the `savescan` output and
   the save name, and every line that looked wrong (`onfoot:`, `bad room report`, `no character`).

## 6.10 If something does not work

| Symptom | What to do |
|---|---|
| No avatar on the other PC at all | Both must be **standing** in the same room of the same station. Check `actors=` in the 30 s `onfoot:` line (0/1 = nothing visible) and `reports=` (0 = no room report) |
| Avatar exists but 1.7 m too high/low | Note it: it is the eye-to-foot constant. Send a screenshot |
| Avatar exists but in another room / far away | Note the distance: it is the room anchor. Send the `onfoot: avatar` lines and the room you were in |
| Avatar blinks at doorways and lifts | Expected for a moment (the room is null there). Note how long |
| The conversation has no Wave/Message | The Talk cue is gated on the actor's player id: it only appears on **remote avatars**, not on station NPCs |
| Wave does nothing on the other PC | Check the sender's log for `onfoot: linked=` and the receiver's for the `onfoot: avatar pid=` line (an emote needs a live actor) |
| `savescan` says the save does not exist | Pass the exact file name (`save_0NN`) or a full path |
| Chat lines from 6.5 do not appear | The first presence payload is the baseline and prints nothing: leave and come back to see an arrival |
