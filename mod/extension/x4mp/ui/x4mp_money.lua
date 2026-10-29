-- x4mp_money.lua : Lua half of the wallet read (M5-22; native features/wallet, docs/m5-plan.md W6, docs/mod-design.md 12.1-12.3).
--
-- Native cannot read the player's money: the game function table has AddPlayerMoney and no getter (x4_game_func_list.inc:40), so the
-- only side that can see a balance is Lua. This file polls GetPlayerMoney() once per frame and tells native when the value changed:
--
--   Lua -> native  x4mp.money {"v":1,"value":<credits, integer>}
--
-- On change only: a quiet frame costs nothing on the wire. The value is rounded to whole credits because the game hands back a float,
-- and a reading that is not a number is not sent at all - a fabricated 0 would look like the player went broke.
--
-- event_player_money_updated (the MD event) is deliberately NOT the source: it is a wake-up hint for a reason, and it does not fire for
-- every change (a script, a mod or a refund can move money silently). The poll is the source; the event will only explain a change.
local B = X4MPBridge

local O = { polls = 0, changes = 0, sends = 0, skipped = 0, bad = 0, value = nil, sent = nil, timerGen = 0, timerPending = false }
X4MPMoney = O

-- The per-frame timer is a one-time callback re-armed from inside itself (the hud does the same). Helper's callback queue is a plain
-- list emptied before its callbacks run, so one raising callback anywhere drops the rest of that batch: a pending timer older than
-- this many seconds is treated as lost and re-armed.
local STALE_S = 1

local function log(msg) pcall(DebugError, "[X4MP] money: " .. tostring(msg)) end

-- The balance, or nil when the game cannot give one. A missing function is the normal case in the start menu and in the tests.
local function read()
	if type(GetPlayerMoney) ~= "function" then return nil end
	local ok, v = pcall(GetPlayerMoney)
	if not ok or type(v) ~= "number" or v ~= v then return nil end -- v ~= v: NaN
	return math.floor(v + 0.5)
end

--- One reading: counts, and sends only what native has not seen yet. Public so a test can drive it without a timer.
function O.poll()
	O.polls = O.polls + 1
	if type(B) == "table" and type(B.isStartMenu) == "function" and B.isStartMenu() then
		O.skipped = O.skipped + 1 -- the start menu has no player and no balance
		return
	end
	local v = read()
	if v == nil then
		O.bad = O.bad + 1
		return
	end
	if O.value ~= v then
		O.value = v
		O.changes = O.changes + 1
	end
	if O.sent == v then return end -- already on the wire: a quiet frame costs nothing
	if type(B) ~= "table" or type(B.send) ~= "function" then
		O.skipped = O.skipped + 1
		return
	end
	-- A refused send stays unsent (O.sent is only advanced on success), so the next poll retries the same value instead of losing it.
	local ok, accepted = pcall(B.send, "money", { value = v })
	if ok and accepted == true then
		O.sent = v
		O.sends = O.sends + 1
	else
		O.skipped = O.skipped + 1
		log("send refused")
	end
end

local function schedule()
	if type(Helper) ~= "table" or type(Helper.addDelayedOneTimeCallbackOnUpdate) ~= "function"
		or type(getElapsedTime) ~= "function" then
		return -- no frame clock (the start menu, the tests): the next gfx_ok/show re-arms it
	end
	local t = getElapsedTime()
	if O.timerPending then
		local age = t - (O.armedAt or t)
		if age >= 0 and age < STALE_S then return end
		log("loop restarted (the timer was lost)")
	end
	O.timerGen = O.timerGen + 1
	local gen = O.timerGen
	O.timerPending, O.armedAt = true, t
	Helper.addDelayedOneTimeCallbackOnUpdate(function()
		if gen ~= O.timerGen then return end -- a newer loop took over
		O.timerPending = false
		pcall(O.poll)
		pcall(schedule)
	end, false, t)
end
O.schedule = schedule

RegisterEvent("gfx_ok", function() pcall(schedule) end)
RegisterEvent("show", function() pcall(schedule) end)

-- The first reading happens at load, not one frame later: a player who joins a running session knows the balance at once.
pcall(O.poll)
pcall(schedule)
