-- x4mp_onfoot.lua : Lua half of the on-foot presence (M3b; native features/onfoot, MD half md/x4mp_onfoot.xml).
--
-- Native reads the player's container and the container-local position, but not which ROOM the player stands in: the SDK has no room
-- accessor (sdk/x4n_rooms.h is only an enum -> name mapper). MD owns the room key (the room macro, its anchor and the station it
-- belongs to) and this file only carries it across:
--   MD -> Lua      x4mp.onfoot_room "mode=1;sector=..;smacro=..;sx=..;sy=..;sz=..;rmacro=..;ax=..;ay=..;az=..;kind=.."
--                  "mode=0" when the player is not on foot in an interior, "mode=2" when seated in a cockpit
--   Lua -> native  x4mp.onfoot_room {"v":1,"data":"<the same string>"}
-- The report is pushed by MD on every room change and pulled here once a second: the poll produces the first report after a load and
-- the leave state after the player steps out. Everything else (remote avatars, rates) is native's business.
--
-- The other direction carries the avatars of the other players (W4):
--   native -> Lua   x4mp.onfoot_actors {"v":1,"spawn":[{pid,name,race,macro,rmacro,ax,ay,az,kind,x,y,z,yaw},...],"despawn":[pid,...]}
--   Lua -> MD       one AddUITriggeredEvent("X4MP_OnFoot","spawn",{...}) per entry, one "despawn" trigger per batch
--   MD -> Lua       x4mp.onfoot_actor "spawn;<pid>;<actor>" | "gone;<pid>"  (forwarded to native as it is)
-- Native decides who is visible; this file never decides anything, it only moves the batch across, because MD cannot call native and
-- native cannot call MD. A batch element is positional in MD (a Lua table arrives as an MD list), so the spawn order is fixed:
-- pid, name, race, character macro (empty = let MD pick a crew NPC), room macro, anchor x/y/z (decimetres), kind, x/y/z (container
-- metres, foot height), yaw (degrees).
-- The janitor removes the avatars a crashed session left in the save; it runs once per process, 5 s after the game is in.
--
-- M3c (Tier 2) adds the room catalogue, because native must name a room that is not the player's own and the wire carries only a hash:
--   MD -> Lua      x4mp.onfoot_rooms "room_a_macro;room_b_macro;..."
--   Lua -> native  x4mp.onfoot_rooms {"v":1,"data":"<the same string>"}

-- luacheck: globals X4MPBridge X4MPOnFoot DebugError RegisterEvent AddUITriggeredEvent getElapsedTime Helper ReadText

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] onfoot: x4mp_bridge.lua must load first")
	return
end

local O = { MD_SCREEN = "X4MP_OnFoot", interval = 1.0, sends = 0, triggers = 0, skipped = 0,
	spawnTriggers = 0, despawnTriggers = 0, replies = 0, janitors = 0, loadedAt = nil, janitorDone = false,
	roomSends = 0, roomsTriggers = 0 }
X4MPOnFoot = O

local function log(msg) pcall(DebugError, "[X4MP] onfoot: " .. tostring(msg)) end

local function now()
	if type(getElapsedTime) ~= "function" then return 0 end
	local ok, t = pcall(getElapsedTime)
	return (ok and type(t) == "number") and t or 0
end

-- ---- the room report (MD -> native) ---------------------------------------------------------------------------------------------
-- The bridge is the only thing native listens on; a report that arrives before the native API exists is dropped on purpose (the next
-- poll is one second away), so a start menu without a session does not queue a stale room.
RegisterEvent("x4mp.onfoot_room", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	if not B.getApi() then
		O.skipped = O.skipped + 1
		return
	end
	if B.send("onfoot_room", { data = param }) then
		O.sends = O.sends + 1
	end
end)

-- ---- the room catalogue (MD -> native, M3c) ---------------------------------------------------------------------------------------
-- Every room macro id of the container the player is in, "id;id;...". Native needs the ids because the wire carries only a hash of the
-- room macro and MD cannot hash; without them an avatar in another room of the same station cannot be placed. The ids never change for
-- a given macro, so the native table is never cleared.
RegisterEvent("x4mp.onfoot_rooms", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	if not B.getApi() then
		O.skipped = O.skipped + 1
		return
	end
	if B.send("onfoot_rooms", { data = param }) then
		O.roomSends = O.roomSends + 1
	end
end)

-- ---- the remote avatars (native -> MD) -------------------------------------------------------------------------------------------
-- One spawn trigger per avatar: MD reads the values positionally and a flat list is the only shape MD handles reliably (the spike's
-- OF_Spawn does the same). Nothing is queued when the native API is gone - the native registry re-asks every 2 s anyway.
B.on("onfoot_actors", function(p)
	if type(p) ~= "table" then return end
	local spawns = p.spawn
	if type(spawns) == "table" then
		for i = 1, #spawns do
			local a = spawns[i]
			if type(a) == "table" then
				local ok = pcall(AddUITriggeredEvent, O.MD_SCREEN, "spawn", {
					a.pid, a.name or "", a.race or "", a.macro or "", a.rmacro, a.ax, a.ay, a.az, a.kind,
					a.x, a.y, a.z, a.yaw,
				})
				if ok then O.spawnTriggers = O.spawnTriggers + 1 else log("spawn trigger failed for pid " .. tostring(a.pid)) end
			end
		end
	end
	local gone = p.despawn
	if type(gone) == "table" and #gone > 0 then
		local ok = pcall(AddUITriggeredEvent, O.MD_SCREEN, "despawn", gone)
		if ok then O.despawnTriggers = O.despawnTriggers + #gone else log("despawn trigger failed") end
	end
end)

-- ---- the walk hybrid (native -> MD) --------------------------------------------------------------------------------------------
-- The engine has no animation API: only MD's slot walk animates an actor. Native therefore stops writing the pose for a moment and
-- asks for a walk here; MD picks a free slot in the avatar's room and starts the walk. A stop command ends it, after which native
-- takes the pose back and snaps the actor exactly onto the sender's position.
B.on("onfoot_walk", function(p)
	if type(p) ~= "table" then return end
	pcall(AddUITriggeredEvent, O.MD_SCREEN, "walk", { p.pid, p.speed or 1.4 })
end)
B.on("onfoot_stop_walk", function(p)
	if type(p) ~= "table" then return end
	pcall(AddUITriggeredEvent, O.MD_SCREEN, "stop_walk", { p.pid })
end)

-- MD answers every spawn with the actor id it created (and every despawn with "gone"), so native can drive that actor. The string is
-- passed through untouched: "spawn;<pid>;<component id>" / "gone;<pid>".
RegisterEvent("x4mp.onfoot_actor", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	if not B.getApi() then
		O.skipped = O.skipped + 1
		return
	end
	if B.send("onfoot_actor", { data = param }) then
		O.replies = O.replies + 1
	end
end)

-- ---- the Talk menu (W5) ----------------------------------------------------------------------------------------------------------
-- MD adds two choices to the conversation with an MP actor and forwards the pick:
--   MD -> Lua      x4mp.onfoot_talk "wave;<pid>"
--   Lua -> native  x4mp.onfoot_talk {"v":1,"to":<pid>,"kind":"wave"}   (native sends PlayerInteraction on the Control lane)
--   native -> Lua  x4mp.onfoot_talk_in {"v":1,"from":<pid>,"name":"Alice","kind":"wave"}
--   Lua -> MD      AddUITriggeredEvent("X4MP_OnFoot","wave",{pid})      (MD plays the emote on that player's actor)
-- "Message" is not a wire action: it prints the whisper command in the chat, because the vanilla chat window cannot be prefilled.
local function chatLine(text)
	local chat = rawget(_G, "X4MPChat")
	if type(chat) == "table" and type(chat.systemLine) == "function" then pcall(chat.systemLine, text) end
end

RegisterEvent("x4mp.onfoot_talk", function(_, param)
	if type(param) ~= "string" or param == "" then return end
	local kind, pidText, name = param:match("^(%a+);(%d+);?(.*)$")
	if not kind then return end
	local pid = tonumber(pidText)
	if not pid then return end
	if name == "" then name = "player " .. tostring(pid) end
	if kind == "message" then
		chatLine("To whisper " .. name .. " type /w " .. name .. " your message")
		return
	end
	if kind ~= "wave" then return end
	if not B.getApi() then
		O.skipped = O.skipped + 1
		return
	end
	if B.send("onfoot_talk", { v = 1, to = pid, kind = "wave" }) then
		O.waves = (O.waves or 0) + 1
		chatLine("You wave at " .. name)
	end
end)

B.on("onfoot_talk_in", function(p)
	if type(p) ~= "table" or p.kind ~= "wave" then return end
	local pid = tonumber(p.from)
	if not pid then return end
	if type(AddUITriggeredEvent) == "function" then
		if pcall(AddUITriggeredEvent, O.MD_SCREEN, "wave", { pid }) then O.waveTriggers = (O.waveTriggers or 0) + 1 end
	end
	local name = (type(p.name) == "string" and p.name ~= "") and p.name or ("player " .. tostring(pid))
	chatLine(name .. " waves at you")
end)

-- ---- the presence list (W6, Tier 0) ----------------------------------------------------------------------------------------------
--   native -> Lua  x4mp.presence {"v":1,"self":2,"container":<net id>,"mode":<local on-foot mode>,
--                                  "entries":[{"id":3,"name":"Alice","mode":1,"here":true}]}
-- The relay sends the roster galaxy-wide, but a container the local player is not in has no name on this side until the station mirror
-- arrives with M4, so the only place we can name is "here". The HUD line lists the players who are here, the player table gets a short
-- on-foot state per row, and a peer who enters or leaves the container is announced once. The first payload of a session is the
-- baseline, not a burst of arrivals.
O.presence = O.presence or { byId = {}, hereList = {}, hereIds = {}, ready = false, arrivals = 0, departures = 0, raises = 0 }

local function T(id, ...)
	local text = ""
	if type(ReadText) == "function" then
		local ok, res = pcall(ReadText, 92000, id)
		if ok and type(res) == "string" then text = res end
	end
	if text == "" then text = "[92000," .. id .. "]" end
	if select("#", ...) > 0 then
		local ok, res = pcall(string.format, text, ...)
		if ok then text = res end
	end
	return text
end

--- the short state of one roster row: "on foot, here" / "on foot" / "in a cockpit" / "in transit", nil when there is nothing to say
local function stateText(row)
	local mode = tonumber(row.mode) or 0
	if mode == 2 then return T(962) end
	if mode == 3 then return T(963) end
	if mode ~= 1 then return nil end
	return row.here and T(960) or T(961)
end

local function displayName(row)
	return row.name or ("player " .. tostring(row.id))
end

--- the HUD suffix: "Alice here", "Alice, Bob here", "Alice, Bob, Cara and 2 more here"
local function hudText()
	local list = O.presence.hereList
	if type(list) ~= "table" or #list == 0 then return nil end
	local parts = {}
	for i = 1, math.min(#list, 3) do parts[#parts + 1] = displayName(list[i]) end
	local text = T(966, table.concat(parts, ", "))
	if #list > 3 then text = text .. " " .. T(967, #list - 3) end
	return text
end

--- the player-table suffix for one row (x4mp_players.lua calls this through X4MPPlayers.presenceResolver)
local function presenceOf(id)
	local row = O.presence.byId[tonumber(id) or -1]
	return row and stateText(row) or nil
end

--- Hand the two hooks to the HUD and the player table. Both files may load after this one, so this runs again on every payload.
local function install()
	local hud = rawget(_G, "X4MPHud")
	if type(hud) == "table" then hud.presenceText = hudText end
	local players = rawget(_G, "X4MPPlayers")
	if type(players) == "table" then players.presenceResolver = presenceOf end
end

local function onPresence(p)
	if type(p) ~= "table" then return end
	O.presence.raises = O.presence.raises + 1
	local byId, hereList, hereIds = {}, {}, {}
	local entries = p.entries
	if type(entries) == "table" then
		for i = 1, #entries do
			local e = entries[i]
			if type(e) == "table" and tonumber(e.id) then
				local row = {
					id = tonumber(e.id), mode = tonumber(e.mode) or 0, here = e.here == true,
					name = (type(e.name) == "string" and e.name ~= "") and e.name or nil,
				}
				byId[row.id] = row
				if row.here and row.mode == 1 then
					hereList[#hereList + 1] = row
					hereIds[row.id] = row
				end
			end
		end
	end
	if O.presence.ready then
		for _, row in ipairs(hereList) do
			if not O.presence.hereIds[row.id] then
				O.presence.arrivals = O.presence.arrivals + 1
				chatLine(T(964, displayName(row)))
			end
		end
		for id, row in pairs(O.presence.hereIds) do
			if not hereIds[id] then
				O.presence.departures = O.presence.departures + 1
				chatLine(T(965, displayName(row)))
			end
		end
	end
	O.presence.byId, O.presence.hereList, O.presence.hereIds, O.presence.ready = byId, hereList, hereIds, true
	install()
	local hud = rawget(_G, "X4MPHud")
	if type(hud) == "table" and type(hud.tick) == "function" then pcall(hud.tick, true) end
	local S = rawget(_G, "X4MPScreens")
	if type(S) == "table" and S.state and S.state.screen == "main" and type(S.render) == "function" then pcall(S.render) end
end

B.on("presence", onPresence)
install()

-- ---- the 1 Hz pull ---------------------------------------------------------------------------------------------------------------
local function report()
	if type(AddUITriggeredEvent) ~= "function" then return end
	if B.isStartMenu() then return end
	local ok, err = pcall(AddUITriggeredEvent, O.MD_SCREEN, "report", 0)
	if ok then
		O.triggers = O.triggers + 1
	else
		log("report trigger failed: " .. tostring(err))
	end
end

-- ---- the janitor -----------------------------------------------------------------------------------------------------------------
-- Removes the avatars a crashed or killed session left in the save. It has to be driven from here: a <delay>, event_game_loaded and
-- check_interval are all silent no-ops in this mdscript (S10). Once per process, five seconds after the game is in, from the poll loop;
-- native's own registry only knows the players of the current session, so the leftovers of a previous one need this.
local function maybe_janitor()
	if O.janitorDone or B.isStartMenu() then return end
	local t = now()
	if (O.loadedAt or t) + 5.0 > t then return end
	O.janitorDone = true
	if type(AddUITriggeredEvent) ~= "function" then return end
	if pcall(AddUITriggeredEvent, O.MD_SCREEN, "janitor", 0) then
		O.janitors = O.janitors + 1
		log("janitor fired")
	end
	-- M3c: ask for the room catalogue. MD pushes it on its own when the container changes, but a reload of the DLL empties the
	-- native table while the container stays the same, so one ask per UI session keeps the two in step.
	if pcall(AddUITriggeredEvent, O.MD_SCREEN, "rooms", 0) then
		O.roomsTriggers = O.roomsTriggers + 1
	end
end

-- Helper's one-time callback queue is a plain list emptied before its callbacks run, so one raising callback (any mod's) drops the
-- rest of that batch, ours included. A pending callback older than a few intervals counts as lost and is re-armed under a new
-- generation number; the stale one, should it still run, does nothing. Same guard as x4mp_hud.lua.
local function schedule()
	if type(Helper) ~= "table" or type(Helper.addDelayedOneTimeCallbackOnUpdate) ~= "function" then return end
	if O.inArm then return end -- re-entered from inside our own arm call: never recurse
	local t = now()
	if O.timerPending then
		local age = t - (O.timerArmedAt or t)
		if age >= 0 and age < O.interval * 5 then return end
		log("loop restarted (timer lost)")
	end
	O.timerGen = (O.timerGen or 0) + 1
	local gen = O.timerGen
	O.timerPending, O.timerArmedAt = true, t
	-- A callback armed for a time that has already passed runs while Helper is still inside the arm call, and this
	-- callback re-arms itself: that recursed ~8000 deep and the game died with 0xc00000fd (STACK_OVERFLOW in
	-- lua51_64.dll) in session 8. The arm is repeated with a growing delay until the callback really lands in the
	-- future; if it never does, the loop stops instead of eating the stack.
	local delay = O.interval
	for _ = 1, 4 do
		local ranNow = false
		O.inArm = true
		Helper.addDelayedOneTimeCallbackOnUpdate(function()
			if O.inArm then ranNow = true; return end -- ran synchronously inside the arm call
			if gen ~= O.timerGen then return end
			O.timerPending = false
			pcall(report)
			pcall(maybe_janitor)
			pcall(schedule)
		end, false, t + delay)
		O.inArm = false
		if not ranNow then return end
		delay = delay * 10
	end
	O.timerPending = false
	log("the timer keeps running inside the arm call; loop paused until the next event")
end

pcall(RegisterEvent, "gfx_ok", function() pcall(schedule) end)
pcall(RegisterEvent, "show", function() pcall(schedule) end)

pcall(report) -- the first report does not wait a second (the MD cue is instantiated with the save)
O.loadedAt = now()
pcall(schedule)
log("loaded")
