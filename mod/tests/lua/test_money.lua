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

test("money: the start-menu check is asked once a second, not once a frame", function()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	local now = 0
	_G.getElapsedTime = function() return now end
	_G.GetPlayerMoney = function() return 1000 end
	local asked = 0
	-- Session 8: this went through B.isStartMenu() -> ffi.C.IsStartmenu() once per frame (100 times a second) and the
	-- game died with 0xc00000fd (STACK_OVERFLOW in lua51_64.dll); the same build played 12 minutes with the call
	-- stubbed out. One answer per second is all the poll needs.
	X4MPBridge.isStartMenu = function() asked = asked + 1 return false end
	env.load("x4mp_money.lua")
	local M = X4MPMoney
	asked = 0 -- the load itself asks once; the poll is what must not
	for i = 1, 99 do -- 10 ms apart, so every one of them is still inside the first second
		now = i * 0.01
		M.poll()
	end
	eq(asked, 0, "99 polls inside one second must reuse the cached answer")
	now = 1.01
	M.poll()
	eq(asked, 1, "the next second asks the game again")
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

test("money: a poll before the bridge has an API neither reads nor logs an error", function()
	local M = setup(function() return 321 end)
	local before = #env.debug -- the load itself may log; the poll must not add a line
	_G.__X4NATIVE_API = nil -- native init has not happened yet
	_G.GetPlayerMoney = function() error("the balance must not be read without an API") end
	M.poll()
	eq(M.skipped, 1, "the poll waits for the API instead of sending")
	eq(#env.debug, before, "the wait must not reach the game log as an error")
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
	local queue, due = {}, {}
	_G.Helper = {
		addDelayedOneTimeCallbackOnUpdate = function(fn, _unused, at)
			queue[#queue + 1] = fn
			due[#due + 1] = at
		end,
	}
	_G.GetPlayerMoney = function() return value end
	env.load("x4mp_money.lua")
	local M = X4MPMoney
	eq(#queue, 1, "the loop must arm itself at load")
	-- A callback armed for "now" is already due, and Helper runs due callbacks while it is still flushing the queue:
	-- arm -> flush -> callback -> schedule -> arm recursed without bound and the game exited with a Lua
	-- STACK_OVERFLOW at universe_ready in session 8. The armed time must therefore be strictly in the future.
	eq(due[1] > now, true, "the arm must be due in the future, never now")
	value = 1200
	now = 0.016
	table.remove(queue, 1)()
	eq(M.sends, 2)
	eq(moneyOf(2).value, 1200)
	eq(#queue, 1, "the callback must re-arm the next frame")
	eq(due[2] > now, true, "the re-arm must be due in the future too")
end)

-- Session 8 proved the future-dated arm alone is not enough: X4 runs due callbacks inside the arm call itself in some
-- situations (right after a save drops every timer, the game loaded with 0xc00000fd in lua51_64.dll ~140 s later, the
-- dump showing an ~8040-deep X4.exe -> Lua -> X4.exe cycle). The loop must notice that it ran synchronously, back off
-- and stop instead of eating the stack.
test("money: a Helper that runs the callback inside the arm call cannot recurse", function()
	env.reset()
	env.installApi()
	env.loadAll({ "x4mp_bridge.lua" })
	_G.getElapsedTime = function() return 0 end
	local arms = 0
	_G.Helper = {
		addDelayedOneTimeCallbackOnUpdate = function(fn)
			arms = arms + 1
			fn() -- Helper runs the callback right away, still inside our arm call
		end,
	}
	_G.GetPlayerMoney = function() return 1000 end
	env.load("x4mp_money.lua")
	local M = X4MPMoney
	eq(arms, 4, "the guard must give up after a bounded number of arms")
	eq(M.sends, 1, "the paused loop must not poll")
	eq(M.timerPending, false, "the paused loop must not stay armed")
end)
