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

-- luacheck: globals X4MPBridge X4MPOnFoot DebugError RegisterEvent AddUITriggeredEvent getElapsedTime Helper

local B = rawget(_G, "X4MPBridge")
if type(B) ~= "table" then
	pcall(DebugError, "[X4MP] onfoot: x4mp_bridge.lua must load first")
	return
end

local O = { MD_SCREEN = "X4MP_OnFoot", interval = 1.0, sends = 0, triggers = 0, skipped = 0 }
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
		pcall(schedule)
	end, false, t + O.interval)
end

pcall(RegisterEvent, "gfx_ok", function() pcall(schedule) end)
pcall(RegisterEvent, "show", function() pcall(schedule) end)

pcall(report) -- the first report does not wait a second (the MD cue is instantiated with the save)
pcall(schedule)
log("loaded")
