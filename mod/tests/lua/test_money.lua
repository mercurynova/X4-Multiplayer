-- M5-22: ui/x4mp_money.lua (the wallet read). The game function table has no money getter native could call, so Lua polls
-- GetPlayerMoney() every frame and pushes x4mp.money only when the value changes. native features/wallet keeps that stream and
-- reconciles it (mod/tests/test_wallet.cpp pins the native half).
local t, env = ...
local test, eq, truthy, falsy = t.test, t.eq, t.truthy, t.falsy

local function setup(money, startmenu)
	env.reset()
	env.installApi()
	env.startmenu = startmenu == true
	env.loadAll({ "x4mp_bridge.lua" })
	_G.GetPlayerMoney = money
	env.load("x4mp_money.lua")
	return X4MPMoney
end

local function moneyOf(index)
	local sent = env.raisedNamed("x4mp.money")
	if not sent[index] then return nil end
	return X4MPBridge.json.decode(sent[index])
end

test("money: the balance goes out once at load, rounded to whole credits", function()
	local M = setup(function() return 12345.6 end)
	eq(M.polls, 1)
	eq(M.sends, 1)
	eq(M.value, 12346)
	eq(#env.raisedNamed("x4mp.money"), 1)
	local p = moneyOf(1)
	eq(p.v, 1)
	eq(p.value, 12346)
	truthy(string.find(env.raisedNamed("x4mp.money")[1], '"value":12346', 1, true), "the wire must carry an integer literal")
end)

test("money: a quiet frame costs nothing on the wire", function()
	local M = setup(function() return 1000 end)
	M.poll(); M.poll(); M.poll()
	eq(M.polls, 4)
	eq(M.changes, 1)
	eq(M.sends, 1)
	eq(#env.raisedNamed("x4mp.money"), 1)
end)

test("money: a change is sent once, and a repeat of it is not", function()
	local value = 1000
	local M = setup(function() return value end)
	value = 1500
	M.poll()
	eq(M.sends, 2)
	eq(M.changes, 2)
	eq(moneyOf(2).value, 1500)
	M.poll()
	eq(M.sends, 2, "the same balance must not be sent twice")
end)

test("money: no reading is not a balance", function()
	-- The function does not exist yet (the API loads late) - that is a missing reading, not a zero.
	local M = setup(nil)
	eq(M.sends, 0)
	eq(M.bad, 1)
	eq(#env.raisedNamed("x4mp.money"), 0)

	_G.GetPlayerMoney = function() return "1500" end -- a string is not a number
	M.poll()
	eq(M.bad, 2)
	_G.GetPlayerMoney = function() return 0 / 0 end -- NaN
	M.poll()
	eq(M.bad, 3)
	eq(#env.raisedNamed("x4mp.money"), 0)
end)

test("money: the start menu has no balance to report", function()
	local M = setup(function() return 1000 end, true)
	eq(M.skipped, 1)
	eq(M.sends, 0)
	eq(#env.raisedNamed("x4mp.money"), 0)
	env.startmenu = false
	M.poll()
	eq(M.sends, 1)
end)

test("money: a refused send is retried, not lost", function()
	local value = 42
	local M = setup(function() return value end)
	eq(M.sends, 1)
	_G.__X4NATIVE_API = nil -- no API right now: the verb is refused (money is not queueable)
	value = 99
	M.poll()
	eq(M.sends, 1)
	eq(M.skipped, 1)
	env.installApi()
	M.poll()
	eq(M.sends, 2, "the value that could not be sent must go out later")
	eq(moneyOf(2).value, 99)
end)

test("money: a negative balance is a real reading", function()
	local value = 100
	local M = setup(function() return value end)
	value = -250
	M.poll()
	eq(M.sends, 2)
	eq(moneyOf(2).value, -250)
end)

test("money: the frame timer polls once per tick and re-arms itself", function()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	local value, now = 1000, 0
	_G.getElapsedTime = function() return now end
	local queue = {}
	_G.Helper = {
		addDelayedOneTimeCallbackOnUpdate = function(fn) queue[#queue + 1] = fn end,
	}
	_G.GetPlayerMoney = function() return value end
	env.load("x4mp_money.lua")
	local M = X4MPMoney
	eq(#queue, 1, "the loop must arm itself at load")
	value = 1200
	now = 0.016
	table.remove(queue, 1)()
	eq(M.sends, 2)
	eq(moneyOf(2).value, 1200)
	eq(#queue, 1, "the callback must re-arm the next frame")
end)
