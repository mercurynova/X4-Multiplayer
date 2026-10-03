-- Session 3, B4: the player is flying as a client, the server restarts, the mod re-joins with a FRESH Welcome (resume token refused).
-- Whatever native reports in game, Lua must never open a window by itself and never touch the cockpit HUD: X4 hides the vanilla HUD
-- (radar, steering overlay) whenever ANY entry in View.menus lacks keepHUDVisible (viewhelper.lua keepHUDVisible() is an AND).
-- The status sequence below is what features/join/join_feature.cpp publishes for this case (build_status).
local t, env = ...
local test, eq, truthy, falsy, contains = t.test, t.eq, t.truthy, t.falsy, t.contains

local clock, timers

local function setup()
	env.reset()
	_G.X4MPHud, _G.X4MPSaves, _G.X4MPAuthority, _G.View = nil, nil, nil, nil
	env.installApi()
	env.installUi()
	env.startmenu = false -- in game, not on the start menu
	clock, timers = 0, {}
	_G.getElapsedTime = function() return clock end
	_G.View = { menus = {}, currentFrames = 0 }
	_G.Menus = {}
	env.registered, env.notifications, env.vanillaCalls = {}, {}, {}
	_G.AddUITriggeredEvent = function(screen, control, value) env.notifications[#env.notifications + 1] = { screen, control, value } end
	_G.SaveGame = function() end
	_G.IsSavingPossible = function() return true end
	local H = _G.Helper
	H.scaleY = function(v) return v end
	H.addDelayedOneTimeCallbackOnUpdate = function(cb, _, at) timers[#timers + 1] = { cb = cb, at = at } end
	-- every call that could disturb the vanilla HUD is recorded
	local baseRegister = H.registerMenu
	H.registerMenu = function(menu) env.registered[#env.registered + 1] = menu.name return baseRegister(menu) end
	for _, fn in ipairs({ "closeMenu", "closeMenuAndOpenNewMenu", "clearMenu" }) do
		local base = H[fn]
		H[fn] = function(...) env.vanillaCalls[#env.vanillaCalls + 1] = fn return base(...) end
	end
	H.clearFrame = function(menu, layer)
		env.vanillaCalls[#env.vanillaCalls + 1] = "clearFrame" .. tostring(layer)
		for i = #_G.View.menus, 1, -1 do
			if _G.View.menus[i].id == "Helper" .. layer then table.remove(_G.View.menus, i) end
		end
	end
	local baseCreate = H.createFrameHandle
	env.frameProps = {}
	H.createFrameHandle = function(menu, props)
		env.frameProps[#env.frameProps + 1] = { menu = menu.name, props = props }
		local frame = baseCreate(menu, props)
		local baseDisplay = frame.display
		function frame:display()
			baseDisplay(self)
			local id = "Helper" .. props.layer
			local present = false
			for _, e in ipairs(_G.View.menus) do if e.id == id then present = true end end
			if not present then
				table.insert(_G.View.menus, { id = id, name = menu.name, type = "Helper", properties = props })
				_G.View.currentFrames = #_G.View.menus
			end
		end
		return frame
	end
	env.loadAll({ "x4mp_bridge.lua", "x4mp_menu.lua", "x4mp_ui_standalone.lua", "x4mp_extensions.lua", "x4mp_saves.lua", "x4mp_join_mods.lua",
		"x4mp_hud.lua" })
end

local function advance(seconds)
	for _ = 1, math.floor(seconds / 0.25 + 0.5) do
		clock = clock + 0.25
		local due, rest = {}, {}
		for _, tm in ipairs(timers) do if tm.at <= clock then due[#due + 1] = tm else rest[#rest + 1] = tm end end
		timers = rest
		for _, tm in ipairs(due) do tm.cb() end
	end
end

local function status(json) env.fire("x4mp.status", json) advance(1) end

local POLICY = '{"v":1,"version":3,"source_mode":"authority","unknown_default":"client_only","enforcement":"warn","entries":[{"id":"ego_dlc_split","name":"Split Vendetta","rule":"required","enabled":true,"version":"9.00"}]}'

--- everything native publishes while a flying client loses the server and re-joins with a fresh Welcome whose save is the running universe
local function runReconnect()
	status('{"v":1,"state":"ingame","players":2,"ping_ms":3,"server":"127.0.0.1:47780","role":"client"}')
	env.fire("x4mp.saves", '{"v":1,"block":false}')                                             -- connection lost: saving allowed again
	status('{"v":1,"state":"connecting","detail":"reconnecting","server":"127.0.0.1:47780"}')
	advance(20)
	status('{"v":1,"state":"connecting","detail":"connect failed (winsock error 10061)"}')   -- server still down
	env.fire("x4mp.mod_policy", POLICY)                                                          -- fresh Welcome
	status('{"v":1,"state":"checking_save","server":"127.0.0.1:47780"}')                      -- Rejoining, waiting for the save info
	env.fire("x4mp.saves", '{"v":1,"block":true}')
	status('{"v":1,"state":"matching","players":2}')                                             -- the session save is the running universe
	status('{"v":1,"state":"ingame","players":2,"ping_ms":2,"role":"client"}')
	advance(10)
	status('{"v":1,"state":"ingame","players":2,"ping_ms":1,"role":"client"}')
end

test("in-game reconnect with a fresh Welcome opens no menu and registers no window besides the HUD", function()
	setup()
	runReconnect()
	eq(#env.opened, 0, "OpenMenu was never called")
	falsy(X4MPMenu.renderer.isOpen(), "the Multiplayer window is not open")
	eq(#env.vanillaCalls, 0, "no Helper.closeMenu / closeMenuAndOpenNewMenu / clearMenu / clearFrame")
	for _, name in ipairs(env.registered) do eq(name, "X4MPHud", "only the HUD registers a menu in game") end
	falsy(X4MPMenu.registered, "the standalone window was never registered")
	for _, n in ipairs(env.notifications) do eq(n[1], "X4MP_Saves", "the only MD events are the save-block flag, no notify cue") end
end)

test("in-game reconnect: every frame drawn keeps the cockpit HUD, crosshair and ship controls", function()
	setup()
	runReconnect()
	truthy(#env.frameProps > 3, "the HUD followed the state changes")
	for i, f in ipairs(env.frameProps) do
		eq(f.menu, "X4MPHud", "frame " .. i .. " belongs to the HUD")
		eq(f.props.layer, 6, "frame " .. i .. " is on the HUD layer (not the chat window's 3, not the interact menu's 2/3)")
		eq(f.props.keepHUDVisible, true, "frame " .. i .. " keepHUDVisible")
		eq(f.props.keepCrosshairVisible, true, "frame " .. i .. " keepCrosshairVisible")
		eq(f.props.playerControls, true, "frame " .. i .. " playerControls")
	end
	-- the engine's AND over View.menus (viewhelper.lua keepHUDVisible / hasPlayerControls) stays true after every step
	for _, e in ipairs(View.menus) do
		eq(e.properties.keepHUDVisible, true)
		eq(e.properties.playerControls, true)
	end
	eq(#View.menus, 1)
	eq(View.menus[1].id, "Helper6")
end)

test("in-game reconnect: the HUD line follows the states and ends on Connected", function()
	setup()
	runReconnect()
	local line = env.cells()[1].text
	eq(line, "X4MP: Connected, 2 players, 1 ms")
	contains(env.debugText(), "hud: view Helper6=X4MPHud[hud=1,pc=1] frames=1")
end)

test("a changed session save in game: HUD line, no window, no load until the player asks", function()
	setup()
	status('{"v":1,"state":"ingame","players":2,"ping_ms":3}')
	status('{"v":1,"state":"save_changed","detail":"New save","players":2}')
	eq(#env.opened, 0)
	eq(#env.raisedNamed("x4mp.load_session"), 0, "nothing was sent to native")
	contains(env.cells()[1].text, "The session save changed: New save")
	-- the player opens the window on purpose: the choice is there, and only the button sends the verb
	truthy(X4MPScreens.open("status"))
	eq(env.opened[1], "X4MPMenu")
	local btn = env.findCell("button", function(c) return c.text == X4MPScreens.T(46) end)
	truthy(btn, "a button offers loading the new save")
	eq(#env.raisedNamed("x4mp.load_session"), 0)
	btn.handlers.onClick()
	eq(#env.raisedNamed("x4mp.load_session"), 1)
end)

test("a stale HUD registration never outlives the HUD: another menu opens, then the session ends", function()
	setup()
	status('{"v":1,"state":"ingame","players":2,"ping_ms":3}')
	eq(#View.menus, 1)
	-- a vanilla Helper menu opens: View.clearMenus only calls our clearCallback, our entry must be gone afterwards
	table.insert(View.menus, { id = "Helper2", name = "MapMenu", type = "Helper", properties = { keepHUDVisible = false } })
	X4MPHud.menu.onCloseElement("close")
	for _, e in ipairs(View.menus) do truthy(e.id ~= "Helper6", "no stale HUD entry") end
	View.menus = {}
	advance(3)
	eq(#View.menus, 1, "the HUD is back by itself")
	-- the frame was closed by the engine, only our entry is left: disconnecting must remove it too
	status('{"v":1,"state":"disconnected"}')
	eq(#View.menus, 0)
end)
