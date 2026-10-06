-- Tests for ui/x4mp_optionsmenu_adapter.lua and ui/x4mp_hud.lua (M2-11).
local t, env = ...
local test, eq, truthy, falsy, contains = t.test, t.eq, t.truthy, t.falsy, t.contains

local ROW = "x4mp_multiplayer"

local function resetOwn()
	_G.X4MPOptionsAdapter, _G.X4MPHud, _G.View = nil, nil, nil
end

local function countRows(config)
	local n = 0
	for _, row in ipairs(config.optionDefinitions.main) do
		if row.id == ROW then n = n + 1 end
	end
	return n
end

local function rowIndex(config, id)
	for i, row in ipairs(config.optionDefinitions.main) do
		if row.id == id then return i end
	end
	return nil
end

local function countLines(needle)
	local n = 0
	for _, line in ipairs(env.debug) do
		if line:find(needle, 1, true) then n = n + 1 end
	end
	return n
end

------------------------------------------------------------------------------
-- stubs shaped like the three real situations
------------------------------------------------------------------------------
local function newConfig()
	return { optionsLayer = 4, optionDefinitions = { main = {
		{ id = "continue", name = "Continue" }, { id = "timelines", name = "Play Timelines" },
		{ id = "load", name = "Load Game" }, { id = "settings", name = "Settings" }, { id = "exit", name = "Exit" } } } }
end

--- vanilla: the config is a plain upvalue of the menu functions, no accessor
local function vanillaMenu()
	local config = newConfig()
	local om = { name = "OptionsMenu", redraws = 0 }
	function om.displayOptions(p)
		if p == "main" then om.redraws = om.redraws + 1 end
		return config.optionDefinitions[p]
	end
	function om.createOptionsFrame() return config end
	return om, config
end

--- UIX: an accessor is present; the functions do not expose the config as an upvalue
local function uixMenu()
	local config = newConfig()
	local om = { name = "OptionsMenu", uix_getConfig = function() return config end }
	function om.displayOptions() return nil end
	return om, config
end

--- SirNukes + UIX: UIX accessor plus SirNukes' own "Extension Options" row in the same config
local function sirnukesMenu()
	local om, config = uixMenu()
	table.insert(config.optionDefinitions.main, 4, { id = "extensions", name = "Extension Options" })
	return om, config
end

local function setup(menus)
	resetOwn()
	env.installApi()
	env.loadAll()
	X4MPScreens.shown = {}
	X4MPScreens.setRenderer({ isOpen = function() return false end, close = function() end,
		show = function(model) X4MPScreens.shown[#X4MPScreens.shown + 1] = model.screen end })
	_G.Menus = menus or {}
end

------------------------------------------------------------------------------
-- adapter
------------------------------------------------------------------------------
test("vanilla shape: debug upvalue capture inserts the row once after Play Timelines", function()
	local om, config = vanillaMenu()
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countRows(config), 1)
	eq(rowIndex(config, ROW), rowIndex(config, "timelines") + 1)
	eq(X4MPOptionsAdapter.source, "debug:displayOptions")
	eq(countLines("optionsmenu adapter OK(source=debug:displayOptions)"), 1)
	local row = config.optionDefinitions.main[rowIndex(config, ROW)]
	eq(row.name, "Multiplayer")
	truthy(row.mouseOverText ~= "" and not row.mouseOverText:find("%["), "row hint text comes from page 92000")
end)

test("the row opens the Multiplayer screen and does nothing else", function()
	local om, config = vanillaMenu()
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	config.optionDefinitions.main[rowIndex(config, ROW)].callback()
	eq(X4MPScreens.shown[1], "main")
	eq(#X4MPScreens.shown, 1)
end)

test("UIX shape: the accessor is preferred over the upvalue", function()
	local om, config = uixMenu()
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countRows(config), 1)
	eq(X4MPOptionsAdapter.source, "uix")
	eq(countLines("OK(source=uix)"), 1)
end)

test("SirNukes + UIX shape: one row, Extension Options untouched, survives a reload", function()
	local om, config = sirnukesMenu()
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countRows(config), 1)
	truthy(rowIndex(config, "extensions"), "Extension Options row still there")
	eq(config.optionDefinitions.main[rowIndex(config, "extensions")].name, "Extension Options")
	-- /reloadui or /rui: the Lua state is rebuilt, the options menu has a fresh config
	local om2, config2 = sirnukesMenu()
	setup({ om2 })
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countRows(config2), 1)
	truthy(rowIndex(config2, "extensions"))
	eq(countRows(config), 1, "the old config is not touched again")
end)

test("repeated load in one state is idempotent: one row, one log line", function()
	local om, config = vanillaMenu()
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	env.load("x4mp_optionsmenu_adapter.lua")
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countRows(config), 1)
	eq(countLines("optionsmenu adapter"), 1)
	-- the game's own retry events after success do nothing
	env.fire("show")
	env.fire("gfx_ok")
	eq(countRows(config), 1)
	eq(countLines("optionsmenu adapter"), 1)
end)

test("the main menu is redrawn when it is on screen at install time", function()
	local om = vanillaMenu()
	om.currentOption = "main"
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(om.redraws, 1)
end)

test("OptionsMenu registered later: pending, then installed on the show event", function()
	setup({})
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countLines("optionsmenu adapter"), 0, "no verdict yet")
	local om, config = vanillaMenu()
	table.insert(_G.Menus, om)
	env.fire("show")
	eq(countRows(config), 1)
	eq(countLines("optionsmenu adapter OK("), 1)
end)

test("DEGRADED when the OptionsMenu never appears, logged once", function()
	setup({})
	env.load("x4mp_optionsmenu_adapter.lua")
	for _ = 1, 10 do env.fire("show") end
	eq(countLines("optionsmenu adapter DEGRADED(optionsmenu_not_found)"), 1)
	falsy(X4MPOptionsAdapter.source)
end)

test("DEGRADED when no config source and no wrappable functions exist", function()
	local om = { name = "OptionsMenu", displayOptions = function() return nil end }
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	eq(countLines("optionsmenu adapter DEGRADED(no_config_source)"), 1)
	eq(X4MPOptionsAdapter.degraded, "no_config_source")
end)

test("append source: wraps displayOptions once, chains to the previous one, draws the row into the main frame", function()
	local drawn, mainDisplays = {}, 0
	local om = { name = "OptionsMenu" }
	function om.displayOption(ftable, row) ftable.rows[#ftable.rows + 1] = row end
	function om.createOptionsFrame()
		return { content = { { rows = {} } }, display = function(self) mainDisplays = mainDisplays + 1 drawn = self.content[1].rows end }
	end
	local prevCalls = 0
	function om.displayOptions(p)
		prevCalls = prevCalls + 1
		if p == "main" then om.createOptionsFrame():display() end
		return "vanilla:" .. tostring(p)
	end
	setup({ om })
	env.load("x4mp_optionsmenu_adapter.lua")
	env.load("x4mp_optionsmenu_adapter.lua") -- second load must not wrap again
	eq(X4MPOptionsAdapter.source, "append")
	eq(countLines("OK(source=append)"), 1)
	eq(om.displayOptions("main"), "vanilla:main")
	eq(prevCalls, 1, "chained exactly once")
	eq(#drawn, 1)
	eq(drawn[1].id, ROW)
	eq(om.displayOptions("graphics"), "vanilla:graphics")
	eq(prevCalls, 2)
end)

------------------------------------------------------------------------------
-- HUD
------------------------------------------------------------------------------
local clock, timers

local function hudSetup(user)
	resetOwn()
	env.installApi()
	env.installUi()
	env.loadAll()
	clock, timers = 0, {}
	_G.getElapsedTime = function() return clock end
	_G.__X4MP_USER = user
	_G.View = { menus = {} }
	env.notifications, env.cleared, env.failDraw = {}, 0, false
	_G.AddUITriggeredEvent = function(screen, control, value) env.notifications[#env.notifications + 1] = { screen, control, value } end
	local H = _G.Helper
	H.scaleY = function(v) return v end
	H.clearFrame = function() env.cleared = env.cleared + 1 for i = #_G.View.menus, 1, -1 do
		if _G.View.menus[i].name == "X4MPHud" then table.remove(_G.View.menus, i) end end end
	H.addDelayedOneTimeCallbackOnUpdate = function(cb, _, at) timers[#timers + 1] = { cb = cb, at = at } end
	local baseCreate = H.createFrameHandle
	H.createFrameHandle = function(menu, props)
		if env.failDraw then error("draw failed") end
		local frame = baseCreate(menu, props)
		local baseDisplay = frame.display
		function frame:display()
			baseDisplay(self)
			if menu.name == "X4MPHud" then
				local present = false
				for _, e in ipairs(_G.View.menus) do if e.name == "X4MPHud" then present = true end end
				if not present then table.insert(_G.View.menus, { name = "X4MPHud", type = "Helper", id = "Helper" .. props.layer, properties = props }) end
			end
		end
		return frame
	end
	env.load("x4mp_hud.lua")
end

--- moves the clock forward in steps, running due self-scheduled callbacks
local function advance(seconds)
	local step = 0.25
	local n = math.floor(seconds / step + 0.5)
	for _ = 1, n do
		clock = clock + step
		local due, rest = {}, {}
		for _, tm in ipairs(timers) do
			if tm.at <= clock then due[#due + 1] = tm else rest[#rest + 1] = tm end
		end
		timers = rest
		for _, tm in ipairs(due) do tm.cb() end
	end
end

local function status(json) env.fire("x4mp.status", json) end
local INGAME = '{"v":1,"state":"ingame","players":3,"ping_ms":45}'

local function hudCells()
	local out = {}
	for _, c in ipairs(env.cells()) do out[#out + 1] = c.text end
	return out
end
--- another menu opens: it is in View.menus and the engine closes our frame
--- another menu opens: it is in View.menus and View.clearMenus runs our clearCallback (menu.onCloseElement), which must release our entry itself
local function openOtherMenu(name)
	table.insert(_G.View.menus, { name = name or "MapMenu", type = "Helper" })
	X4MPHud.menu.onCloseElement("close")
end

local function closeOtherMenus()
	_G.View.menus = {}
end

test("hud: shows the connection state, players and ping top right on its own layer", function()
	hudSetup()
	eq(#env.frames, 0, "nothing before a session")
	status(INGAME)
	eq(#env.frames, 1)
	eq(env.lastFrame.props.layer, 6)
	-- layer 3 is the chat window's: View.registerMenu keys frames by "Helper"..layer, a second frame there replaces the chat
	truthy(env.lastFrame.props.layer ~= 3, "must not share the chat layer")
	truthy(env.lastFrame.props.x > 1920 / 2, "right half")
	eq(env.lastFrame.props.y, 20)
	eq(env.lastFrame.props.playerControls, true)
	eq(hudCells()[1], "X4MP: Connected, 3 players, 45 ms")
end)

test("hud: the frame keeps the cockpit HUD and crosshair visible (session 3: the vanilla HUD vanished)", function()
	hudSetup()
	status(INGAME)
	eq(env.lastFrame.props.keepHUDVisible, true)
	eq(env.lastFrame.props.keepCrosshairVisible, true)
end)

test("hud: the frame has its own view type, never 'Helper' (chat + Esc: vanilla Helper-menu paths must not touch it)", function()
	hudSetup()
	status(INGAME)
	eq(env.lastFrame.props.viewHelperType, "X4MPHud")
	truthy(env.lastFrame.props.viewHelperType ~= "Helper")
end)

test("hud: a minimized vanilla menu is never closed by a redraw (frame display runs Helper.closeMinimizedMenus)", function()
	hudSetup()
	status(INGAME)
	local n0 = #env.frames
	_G.Helper.minimizedMenu = { name = "MapMenu" }
	advance(12) -- several refresh intervals
	eq(#env.frames, n0, "no redraw while a menu is minimized")
	status('{"v":1,"state":"ingame","players":4,"ping_ms":45}')
	eq(#env.frames, n0, "not even for a text change")
	_G.Helper.minimizedMenu = nil
	advance(2)
	truthy(#env.frames > n0, "drawn again once it is gone")
	eq(hudCells()[1], "X4MP: Connected, 4 players, 45 ms")
end)

test("hud: a long rejection detail is cut to one short line and a notification is sent once", function()
	hudSetup()
	local long = string.rep("ego_dlc_boron@9.00, ", 40)
	status('{"v":1,"state":"rejected","reject":"mod","detail":"' .. long .. '"}')
	local line = hudCells()[1]
	truthy(#line < 120, "short line, got " .. #line)
	eq(#env.notifications, 1)
	status('{"v":1,"state":"rejected","reject":"mod","detail":"' .. long .. '"}')
	eq(#env.notifications, 1, "not repeated")
end)

test("hud: no frame while disconnected, hidden again after a disconnect", function()
	hudSetup()
	status('{"v":1,"state":"disconnected"}')
	eq(#env.frames, 0)
	status(INGAME)
	eq(#env.frames, 1)
	status('{"v":1,"state":"disconnected"}')
	eq(env.cleared, 1)
	falsy(X4MPHud.present())
end)

test("hud: states before ingame show their label", function()
	hudSetup()
	status('{"v":1,"state":"connecting"}')
	eq(hudCells()[1], "X4MP: Connecting")
	status('{"v":1,"state":"downloading","detail":"x4mp_ab12"}')
	eq(hudCells()[1], "X4MP: Downloading the session save: x4mp_ab12")
end)

test("hud: redraws only when the text changes", function()
	hudSetup()
	status(INGAME)
	status(INGAME)
	advance(2)
	eq(#env.frames, 1)
	status('{"v":1,"state":"ingame","players":3,"ping_ms":52}')
	eq(#env.frames, 2)
	eq(hudCells()[1], "X4MP: Connected, 3 players, 52 ms")
end)

test("hud: another menu closes the frame, it comes back after the menu closes", function()
	hudSetup()
	status(INGAME)
	eq(#env.frames, 1)
	openOtherMenu("MapMenu")
	falsy(X4MPHud.present())
	advance(5) -- the map stays open: the self-scheduled loop must not draw over it
	eq(#env.frames, 1, "not drawn while another menu is open")
	truthy(X4MPHud.blocked())
	closeOtherMenus()
	advance(1.5)
	eq(#env.frames, 2, "drawn again by the timer loop alone")
	truthy(X4MPHud.present())
	eq(X4MPHud.reshows, 1)
	advance(4)
	eq(#env.frames, 2, "and not again while present")
end)

test("hud: the show event also brings it back", function()
	hudSetup()
	status(INGAME)
	openOtherMenu("MapMenu")
	closeOtherMenus()
	clock = clock + 2
	env.fire("show")
	eq(#env.frames, 2)
end)

test("hud: the chat window does not count as another menu", function()
	hudSetup()
	status(INGAME)
	openOtherMenu("ChatWindow")
	falsy(X4MPHud.blocked())
end)

test("hud: the top bar and the docked menu stay up in normal play and do not block the line", function()
	hudSetup()
	status(INGAME)
	openOtherMenu("TopLevelMenu")
	falsy(X4MPHud.blocked())
	openOtherMenu("DockedMenu")
	falsy(X4MPHud.blocked())
end)

-- chatwindow.lua (vanilla): layer 3 ("Helper3"), viewHelperType "Chat", no keepHUDVisible; Enter (edit box) sets playerControls false. The vanilla
-- cockpit HUD is therefore hidden by the chat itself, not by us. Our frame is Helper6, so it neither displaces nor needs to yield to the chat.
test("hud: chat open persistently -> the line stays and keeps updating, the chat is never touched", function()
	hudSetup()
	status(INGAME)
	table.insert(_G.View.menus, { name = "ChatWindow", type = "Chat", id = "Helper3" })
	local chatClosed, closeCalls = 0, 0
	_G.Menus[#_G.Menus + 1] = { name = "ChatWindow", onCloseElement = function() chatClosed = chatClosed + 1 end }
	_G.Helper.closeMenu = function() closeCalls = closeCalls + 1 end
	for i = 1, 10 do
		status('{"v":1,"state":"ingame","players":3,"ping_ms":' .. (50 + i) .. '}')
		advance(1)
	end
	truthy(X4MPHud.present())
	eq(hudCells()[1], "X4MP: Connected, 3 players, 60 ms")
	eq(closeCalls, 0)
	eq(chatClosed, 0)
	for _, f in ipairs(env.frames) do eq(f.props.layer, 6) end
end)

test("hud: chat closed with X -> the line is redrawn within reshowDelay + interval even if the view entry outlived the frame", function()
	hudSetup()
	status(INGAME)
	local n0 = #env.frames
	table.insert(_G.View.menus, { name = "ChatWindow", type = "Chat", id = "Helper3" })
	advance(3)
	-- the engine lost our frame without telling us: our menu is no longer shown, the stale View entry stays
	X4MPHud.menu.shown = nil
	table.remove(_G.View.menus, 2) -- the chat closes
	advance(3)
	truthy(#env.frames > n0, "drawn again")
	truthy(X4MPHud.present())
end)

test("hud: map opened and closed -> the line comes back even though a stale entry said 'present'", function()
	hudSetup()
	status(INGAME)
	local n0 = #env.frames
	-- a vanilla menu opens but our callback is not run (or the entry was left behind): entry present, frame not shown
	table.insert(_G.View.menus, { name = "MapMenu", type = "Helper" })
	X4MPHud.menu.shown = nil
	advance(2)
	closeOtherMenus()
	table.insert(_G.View.menus, { name = "X4MPHud", type = "Helper", id = "Helper6", properties = {} }) -- stale entry
	advance(3)
	truthy(#env.frames > n0, "redrawn")
	truthy(X4MPHud.present())
end)

test("hud: a lost frame is healed by the periodic refresh even when nothing signalled it", function()
	hudSetup()
	status(INGAME)
	local n0 = #env.frames
	advance(6)
	truthy(#env.frames > n0, "unchanged line drawn once more after refreshInterval")
end)

test("hud: unchanged text and frame present -> no re-show on any tick", function()
	hudSetup()
	status(INGAME)
	eq(#env.frames, 1)
	advance(4)
	status(INGAME)
	eq(#env.frames, 1)
	eq(X4MPHud.tick(true), "present")
end)

test("hud: frame closed by the engine and the delayed-callback queue dropped -> the next status event redraws and re-arms the timer", function()
	hudSetup()
	status(INGAME)
	local n0 = #env.frames
	-- the engine closes our frame without onCloseElement (save screen) and Helper's one-time queue is lost (a callback raised)
	_G.View.menus = {}
	X4MPHud.menu.shown = nil
	X4MPHud.menu.frame = nil
	timers = {}
	truthy(X4MPHud.timerPending, "the flag is stuck: the loop is dead")
	for _ = 1, 3 do -- the native heartbeat: one status every 2 s
		status(INGAME)
		advance(2)
	end
	truthy(#env.frames > n0, "redrawn after the status event")
	truthy(X4MPHud.present())
	truthy(#timers >= 1, "timer armed again")
	-- and the loop runs by itself afterwards
	local n1 = #env.frames
	_G.View.menus = {}
	X4MPHud.menu.shown = nil
	advance(4)
	truthy(#env.frames > n1, "timer loop alive")
end)

test("hud: a lost timer is armed again by a status event once it is stale, logged as loop restarted", function()
	hudSetup()
	status(INGAME)
	timers = {}
	clock = clock + 20
	status(INGAME)
	truthy(#timers >= 1)
	truthy(X4MPHud.timerPending)
	contains(env.debugText(), "hud: loop restarted (status)")
end)

test("hud: blocked for a long time by a stale view entry -> drawn anyway and logged", function()
	hudSetup()
	status(INGAME)
	_G.View.menus = { { name = "SaveStale", type = "Helper", id = "Helper2" } }
	X4MPHud.menu.shown = nil
	advance(4)
	eq(#env.frames, 1)
	advance(12)
	truthy(#env.frames > 1, "drawn despite the blocker")
	contains(env.debugText(), "drawing anyway")
	contains(env.debugText(), "SaveStale")
end)

test("hud: a live MapMenu entry (valid frame) is never drawn over, even after 60 s", function()
	hudSetup()
	status(INGAME)
	_G.IsValidWidgetElement = function(f) return f == 77 end
	_G.View.menus = { { name = "MapMenu", type = "Helper", id = "Helper1", frames = { 77 } } }
	X4MPHud.menu.shown = nil
	advance(4)
	local n = #env.frames
	advance(60)
	eq(#env.frames, n, "no draw over the live map")
	contains(env.debugText(), "blocked by MapMenu (live), not forcing")
	-- the map closes: the HUD shows again after the reshow delay
	_G.View.menus = {}
	advance(3)
	truthy(#env.frames > n, "shown again after the menu closed")
end)

test("hud: stale entry with no valid frame is forced after blockedMax even when frames are tracked", function()
	hudSetup()
	status(INGAME)
	_G.IsValidWidgetElement = function() return false end
	_G.View.menus = { { name = "TopLevelStale", type = "Helper", id = "Helper2", frames = { 5 } } }
	X4MPHud.menu.shown = nil
	advance(4)
	local n = #env.frames
	advance(12)
	truthy(#env.frames > n, "forced")
	contains(env.debugText(), "drawing anyway")
	_G.IsValidWidgetElement = nil
end)

test("hud (M3-28): display() drops a frame id the engine already destroyed before clearDataForRefresh; a valid one stays", function()
	hudSetup()
	status(INGAME)
	local seen
	_G.Helper.clearDataForRefresh = function(m, layer) seen = m.frames and m.frames[layer] end
	local layer = X4MPHud.config.layer
	X4MPHud.menu.frames = { [layer] = 99 }
	_G.IsValidWidgetElement = function() return false end
	X4MPHud.menu.display()
	eq(seen, nil, "the stale id was dropped before the helper looked at it")
	X4MPHud.menu.frames = { [layer] = 99 }
	_G.IsValidWidgetElement = function(f) return f == 99 end
	X4MPHud.menu.display()
	eq(seen, 99, "a valid id is left alone")
	X4MPHud.menu.frames = { [layer] = 99 }
	_G.IsValidWidgetElement = nil -- no signal: nothing is dropped
	X4MPHud.menu.display()
	eq(seen, 99)
end)

test("hud: notify mode sends one notification per change, never a frame", function()
	hudSetup({ hudMode = "notify" })
	status(INGAME)
	status(INGAME)
	eq(#env.frames, 0)
	eq(#env.notifications, 1)
	eq(env.notifications[1][3], "X4MP: Connected, 3 players, 45 ms")
	status('{"v":1,"state":"ingame","players":4,"ping_ms":45}')
	eq(#env.notifications, 2)
end)

test("hud: off mode does nothing", function()
	hudSetup({ hudMode = "off" })
	status(INGAME)
	eq(#env.frames, 0)
	eq(#env.notifications, 0)
end)

test("hud: repeated drawing failures degrade to notifications", function()
	hudSetup()
	env.failDraw = true
	status(INGAME)
	advance(8)
	eq(#env.frames, 0)
	eq(X4MPHud.modeOverride, "notify")
	truthy(#env.notifications >= 1)
	contains(env.debugText(), "falling back to notifications")
end)

test("hud and adapter texts exist on page 92000 without reserved characters", function()
	env.reset()
	for _, id in ipairs({ 31, 32, 33, 34, 35, 36, 37, 38, 39, 300, 301, 302 }) do
		truthy(env.texts[id], "text " .. id)
	end
	for _, id in ipairs({ 300, 301, 302 }) do
		falsy(env.texts[id]:find("[%(%){}]"), "text " .. id .. " has ( ) { }")
	end
	for _, file in ipairs({ "x4mp_optionsmenu_adapter.lua", "x4mp_hud.lua" }) do
		for id in env.readFile(env.uiPath(file)):gmatch("[^%w_]T%((%d+)") do
			truthy(env.texts[tonumber(id)], file .. " uses missing text " .. id)
		end
	end
end)
