-- M5-26: the shared-pool debt on the HUD (ui/x4mp_hud.lua + ui/x4mp_bridge.lua, t/0001-l044.xml ids 303/304).
--
-- Native publishes x4mp.wallet {v, local, target, debt, kind, owner, mode} on change only. What a test can pin without a game: the
-- topic reaches a handler through the bridge, the line carries the debt while the pool is in the red and drops it when the pool
-- recovers, and a descent warns exactly once (the frame is redrawn every 0.25 s).
local t, env = ...
local test, eq, truthy, falsy, contains = t.test, t.eq, t.truthy, t.falsy, t.contains

local clock

local function setup()
	env.reset()
	_G.X4MPHud = nil  -- env.reset() keeps it: a HUD from an earlier test is already hooked to a dead bridge
	env.installApi()
	env.installUi()
	env.loadAll()
	clock = 0
	_G.getElapsedTime = function() return clock end
	_G.View = { menus = {} }
	env.notifications = {}
	_G.AddUITriggeredEvent = function(screen, control, value)
		env.notifications[#env.notifications + 1] = { screen = screen, control = control, value = value }
		return true
	end
	local H = _G.Helper
	H.scaleY = function(v) return v end
	H.clearFrame = function() end
	H.addDelayedOneTimeCallbackOnUpdate = function() end
	env.load("x4mp_hud.lua")
	X4MPBridge.status = { state = "ingame", players = 1, ping_ms = 20 }
	return X4MPHud
end

local function wallet(debt)
	return '{"v":1,"local":0,"target":' .. tostring(-debt) .. ',"debt":' .. tostring(debt) ..
		',"kind":2,"owner":3,"mode":"shared"}'
end

test("wallet: the topic reaches the HUD and the line carries the pool debt", function()
	local H = setup()
	env.fire("x4mp.wallet", wallet(300))
	eq(H.walletText(), "pool debt 300 Cr")
	contains(H.text(), " | pool debt 300 Cr")
end)

test("wallet: a pool in the black adds nothing to the line", function()
	local H = setup()
	env.fire("x4mp.wallet", '{"v":1,"local":4200,"target":4200,"debt":0,"kind":0,"owner":7,"mode":"per_player"}')
	falsy(H.walletText())
	falsy(H.text():find("pool debt", 1, true))
	contains(H.text(), "X4MP:")
end)

test("wallet: one warning per descent, and a recovered pool arms the next one", function()
	local H = setup()
	env.fire("x4mp.wallet", wallet(300))
	eq(#env.notifications, 1)
	eq(env.notifications[1].screen, "X4MP")
	eq(env.notifications[1].control, "notify")
	contains(env.notifications[1].value, "300 credits in the red")

	-- deeper in the red is still the same descent: the player already knows
	env.fire("x4mp.wallet", wallet(900))
	eq(#env.notifications, 1)
	eq(H.walletText(), "pool debt 900 Cr")

	-- back in the black, then down again: that is a new descent and a new warning
	env.fire("x4mp.wallet", wallet(0))
	falsy(H.walletText())
	env.fire("x4mp.wallet", wallet(50))
	eq(#env.notifications, 2)
	contains(env.notifications[2].value, "50 credits in the red")
end)

test("wallet: a payload the bridge refuses leaves the last known debt alone", function()
	local H = setup()
	env.fire("x4mp.wallet", wallet(300))
	eq(H.walletText(), "pool debt 300 Cr")

	-- The bridge drops a body that is not a JSON object before any handler runs (B.dispatch, "bad payload"), so a garbled
	-- message must not be read as "the debt is gone": the line keeps the number native last published.
	env.fire("x4mp.wallet", "not json")
	eq(H.walletText(), "pool debt 300 Cr")
	env.fire("x4mp.wallet", '{"v":1,')
	eq(H.walletText(), "pool debt 300 Cr")

	-- A well-formed message that simply has no debt field is a different thing: the pool is back in the black.
	env.fire("x4mp.wallet", '{"v":1,"local":500,"target":500,"kind":0,"owner":7,"mode":"per_player"}')
	falsy(H.walletText())
	eq(#env.notifications, 1)  -- and coming back does not warn again
end)
