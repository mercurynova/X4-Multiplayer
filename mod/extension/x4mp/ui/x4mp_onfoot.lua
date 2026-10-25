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

-- luacheck: globals X4MPBridge X4MPOnFoot DebugError RegisterEvent AddUITriggeredEvent getElapsedTime Helper

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] onfoot: x4mp_bridge.lua must load first")
	return
end

local O = { MD_SCREEN = "X4MP_OnFoot", interval = 1.0, sends = 0, triggers = 0, skipped = 0,
	spawnTriggers = 0, despawnTriggers = 0, replies = 0, janitors = 0, loadedAt = nil, janitorDone = false }
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
end

-- Helper's one-time callback queue is a plain list emptied before its callbacks run, so one raising callback (any mod's) drops the
-- rest of that batch, ours included. A pending callback older than a few intervals counts as lost and is re-armed under a new
-- generation number; the stale one, should it still run, does nothing. Same guard as x4mp_hud.lua.
local function schedule()
	if type(Helper) ~= "table" or type(Helper.addDelayedOneTimeCallbackOnUpdate) ~= "function" then return end
	local t = now()
	if O.timerPending then
		local age = t - (O.timerArmedAt or t)
		if age >= 0 and age < O.interval * 5 then return end
		log("loop restarted (timer lost)")
	end
	O.timerGen = (O.timerGen or 0) + 1
	local gen = O.timerGen
	O.timerPending, O.timerArmedAt = true, t
	Helper.addDelayedOneTimeCallbackOnUpdate(function()
		if gen ~= O.timerGen then return end
		O.timerPending = false
		pcall(report)
		pcall(maybe_janitor)
		pcall(schedule)
	end, false, t + O.interval)
end

pcall(RegisterEvent, "gfx_ok", function() pcall(schedule) end)
pcall(RegisterEvent, "show", function() pcall(schedule) end)

pcall(report) -- the first report does not wait a second (the MD cue is instantiated with the save)
O.loadedAt = now()
pcall(schedule)
log("loaded")
