-- x4mp_hud.lua : connection status HUD (M2-11, mod-design 7.5).
--
-- A small passive frame at the top right of the screen on its own layer 6 (NOT the chat window's layer 3, see config.layer), showing e.g.
-- "X4MP: Connected, 3 players, 45 ms" from the x4mp.status topic. It takes no input and no focus.
--
-- Session 2 (D5): the frame displays fine, but any other menu (map, Esc) closes it and it does not come back. So this
-- file keeps the *wish* to show the HUD separately from the frame, notices when the frame is gone (the engine calls
-- menu.onCloseElement, and the frame disappears from View.menus) and draws it again once no other menu is open. The check is
-- driven by several redundant sources, throttled to one run per 0.25 s, because the game offers no documented "menu closed" event:
--   - a self-rescheduling Helper.addDelayedOneTimeCallbackOnUpdate loop (blockinput false, so it never eats input)
--   - menu.onUpdate (menu.updateInterval), in case the engine runs it for a registered menu without a frame
--   - the game's show / gfx_ok events and every x4mp.status topic
-- Which of these actually fires while a menu is open is only known in game (see the report); any one is enough.
--
-- Config (all optional): __X4MP_USER.hudMode = "hud" | "notify" | "off" (default "hud"); X4MPHud.config for the rest.
--   hud     frame as above; after maxFailures failed draws it degrades to "notify" by itself
--   notify  no frame: a short message on every state / player-count change via AddUITriggeredEvent("X4MP", "notify", text)
--           (the MD side shows it as a notification)
--   off     nothing
-- The HUD is only wanted while a session is connecting or running; it hides on "disconnected".
--
-- Sources (under x4-unpacked/ui/addons/): ego_chatwindow/chatwindow.lua:18-46 (menu table, registration, layer 3),
-- ego_viewhelper/viewhelper.lua (View.menus entries carry name/type/minimized), ego_detailmonitorhelper/helper.lua
-- (Helper.registerMenu :1331, createFrameHandle, clearFrame, addDelayedOneTimeCallbackOnUpdate :940).

-- luacheck: globals X4MPBridge X4MPScreens X4MPHud __X4MP_USER Menus Helper Color View AddUITriggeredEvent getElapsedTime RegisterEvent

local H = X4MPHud or {}
X4MPHud = H
H.loaded = true

local MENU_NAME = "X4MPHud"
local STATE_TEXT = { connecting = 31, handshaking = 32, checking_save = 33, downloading = 34, loading = 35, matching = 36,
	ingame = 37, rejected = 38, error = 39 }

H.config = H.config or {
	mode = "hud",
	-- Session 3 root cause: View.registerMenu / Helper.clearFrame key a frame by "Helper" .. layer (helper.lua:4115, :1578), so a second
	-- frame on layer 3 REPLACED the chat window's registration (chatwindow.lua:54 layer = 3) and the engine closed the chat. Layer 3 is
	-- also used by movie.lua:18, 2 by many menus, 1 helptext, 0 debuglog, 4 is the Helper default. 6 is unused by vanilla.
	layer = 6,
	width = 300,        -- scaled with Helper.scaleX
	margin = 20,        -- distance from the top and right screen edge
	interval = 1.0,     -- seconds between checks of the self-rescheduling loop
	reshowDelay = 1.0,  -- seconds the frame must have been gone before it is drawn again
	maxDetail = 60,     -- characters of a status detail shown on the one-line HUD (the full text is on the Multiplayer screen)
	maxFailures = 3,    -- failed draws in a row before degrading to "notify"
	ignoreMenus = { ChatWindow = true }, -- View.menus entries that do not count as "another menu is open"
	yieldMenus = { ChatWindow = true },  -- while one of these is open the HUD never draws, hides or touches anything (belt and braces)
	yieldLogInterval = 30, -- seconds between "yielding" log lines
}

local function log(msg)
	if type(X4MPBridge) == "table" and type(X4MPBridge.log) == "function" then X4MPBridge.log(msg) end
end

local function now()
	if type(getElapsedTime) == "function" then
		local ok, t = pcall(getElapsedTime)
		if ok and type(t) == "number" then return t end
	end
	return 0
end

local function T(id, ...)
	local S = X4MPScreens
	if type(S) == "table" and type(S.T) == "function" then return S.T(id, ...) end
	return ""
end

local function mode()
	local m = H.modeOverride
	if m == nil and type(__X4MP_USER) == "table" then m = __X4MP_USER.hudMode end
	if m == nil then m = H.config.mode end
	if m == "notify" or m == "off" then return m end
	return "hud"
end

------------------------------------------------------------------------------
-- status -> text
------------------------------------------------------------------------------
local function status()
	return type(X4MPBridge) == "table" and X4MPBridge.status or nil
end

--- true while a session is being joined or runs (the HUD stays out of the way of a plain single-player game)
function H.wanted()
	local st = status()
	return mode() ~= "off" and type(st) == "table" and type(st.state) == "string" and st.state ~= "disconnected"
end

--- the one-line text for the current status, nil when there is nothing to say
function H.text()
	local st = status()
	if not (st and H.wanted()) then return nil end
	local id = STATE_TEXT[st.state] or 39
	local detail = tostring(st.detail or st.reject or "")
	if #detail > H.config.maxDetail then detail = detail:sub(1, H.config.maxDetail - 3) .. "..." end -- a mod refusal detail lists every DLC
	local label = T(id, detail)
	if st.state == "ingame" and type(st.players) == "number" and type(st.ping_ms) == "number" then
		return T(302, label, st.players, math.floor(st.ping_ms + 0.5))
	end
	return T(301, label)
end

------------------------------------------------------------------------------
-- the menu (frame)
------------------------------------------------------------------------------
local menu = H.menu or { name = MENU_NAME }
H.menu = menu
menu.updateInterval = 1.0

local function viewEntryPresent()
	if type(View) ~= "table" or type(View.menus) ~= "table" then return nil end
	for _, entry in ipairs(View.menus) do
		if type(entry) == "table" and entry.name == MENU_NAME then return true end
	end
	return false
end

--- true when our frame is on screen
function H.present()
	local v = viewEntryPresent()
	if v ~= nil then return v end
	return menu.shown == true
end

--- name of an open menu from config.yieldMenus (the chat window), or nil
function H.yielding()
	if type(View) ~= "table" or type(View.menus) ~= "table" then return nil end
	local y = H.config.yieldMenus
	for _, entry in ipairs(View.menus) do
		if type(entry) == "table" and entry.name and y[entry.name] then return entry.name end
	end
	return nil
end

--- true when another (non-ignored, non-minimized) menu is open: drawing then would be useless or disturb it
function H.blocked()
	if type(View) ~= "table" or type(View.menus) ~= "table" then return false end
	local ignore = H.config.ignoreMenus
	for _, entry in ipairs(View.menus) do
		if type(entry) == "table" and entry.name ~= MENU_NAME and not entry.minimized and not (entry.name and ignore[entry.name]) then
			return true
		end
	end
	return false
end

function menu.display()
	local cfg = H.config
	Helper.clearDataForRefresh(menu, cfg.layer)
	local width = Helper.scaleX(cfg.width)
	menu.frame = Helper.createFrameHandle(menu, {
		layer = cfg.layer,
		x = Helper.viewWidth - width - Helper.scaleX(cfg.margin),
		y = Helper.scaleY(cfg.margin),
		width = width,
		autoFrameHeight = true,
		standardButtons = {},
		startAnimation = false,
		blurBackground = false,
		playerControls = true, -- passive: the player keeps control of the ship and the mouse
		-- Session 3 (live): without these two the engine treats the frame like any menu and HIDES the whole cockpit HUD (steering overlay,
		-- radar, target monitor) for as long as it is up. Defaults are false (helper.lua frame properties :3136-3137, forwarded to
		-- View.registerMenu :4056-4057); menu_followcamera.lua:97 sets them the same way for a passive in-flight frame.
		keepHUDVisible = true,
		keepCrosshairVisible = true,
	})
	menu.frame:setBackground("solid", { color = Color["frame_background_semitransparent"] })
	local ftable = menu.frame:addTable(1, { tabOrder = 0, highlightMode = "off", reserveScrollBar = false })
	local row = ftable:addRow(false, { fixed = true })
	row[1]:createText(menu.text or "", { halign = "center", wordwrap = true })
	menu.frame:display()
end

function menu.onShowMenu()
	menu.shown = true
	menu.display()
end

function menu.cleanup()
	menu.shown = nil
	menu.frame = nil
end

--- the engine closes our frame whenever another menu opens (session 2): note it, draw again later; never reopen from here
function menu.onCloseElement()
	if menu.closing then return end
	if menu.shown then H.goneSince = now() end
	menu.cleanup()
end

function menu.onUpdate()
	H.tick()
end

local function registerMenu()
	if H.registered then return true end
	if type(Helper) ~= "table" or type(Helper.registerMenu) ~= "function" then return false end
	Menus = Menus or {}
	for i = #Menus, 1, -1 do
		if type(Menus[i]) == "table" and Menus[i].name == MENU_NAME then table.remove(Menus, i) end
	end
	table.insert(Menus, menu)
	local ok, err = pcall(Helper.registerMenu, menu)
	if not ok then
		log("hud: menu registration failed: " .. tostring(err))
		return false
	end
	H.registered = true
	return true
end

------------------------------------------------------------------------------
-- show / hide / notify
------------------------------------------------------------------------------
local function draw(text)
	if not registerMenu() then return false, "no_helper" end
	menu.text = text
	local ok, err = pcall(menu.onShowMenu)
	if not ok then
		menu.cleanup()
		return false, tostring(err)
	end
	return true
end

function H.hide()
	if menu.shown then
		menu.closing = true
		pcall(function() Helper.clearFrame(menu, H.config.layer) end)
		menu.closing = nil
		menu.cleanup()
	end
	H.lastText, H.goneSince = nil, nil
end

local function notify(st, text)
	local key = tostring(st.state) .. "|" .. tostring(st.players)
	if H.lastNotifyKey == key then return false end
	H.lastNotifyKey = key
	pcall(AddUITriggeredEvent, "X4MP", "notify", text)
	return true
end

------------------------------------------------------------------------------
-- tick: decides what to do now. Returns a short word for the tests and the log.
------------------------------------------------------------------------------
function H.tick(force)
	local t = now()
	if not force and H.lastTick and t >= H.lastTick and t - H.lastTick < 0.25 then return "throttled" end
	H.lastTick = t

	local m = mode()
	local text = H.text()
	if text and m == "hud" then
		local who = H.yielding()
		if who then -- never draw, clear or re-open anything while the chat window is up; the frame is on another layer and stays as it is
			if not H.lastYieldLog or t < H.lastYieldLog or t - H.lastYieldLog >= H.config.yieldLogInterval then
				H.lastYieldLog = t
				log("hud: yielding to open menu " .. who)
			end
			return "yielding"
		end
	end
	if not text then
		H.hide()
		H.lastNotifyKey = nil
		return "idle"
	end

	-- A refusal/disconnect in game never opens a menu: the HUD line plus one notification line; the player opens the Multiplayer
	-- window (/x4mp) for the details.
	local st0 = status()
	if m == "hud" and st0 and (st0.state == "rejected" or st0.state == "error") then notify(st0, text) end

	if m == "notify" then
		H.hide()
		return notify(status(), text) and "notified" or "idle"
	end

	if H.present() then
		if text ~= H.lastText then -- redraw only on change
			local ok = draw(text)
			if ok then H.lastText = text end
			return ok and "updated" or "failed"
		end
		return "present"
	end

	-- frame not on screen: either never drawn, or an engine-side close (menu) removed it
	if menu.shown then menu.cleanup() end
	if not H.goneSince then
		if H.lastText ~= nil then H.goneSince = t end -- it was drawn before: wait out the delay; a first draw is immediate
	end
	if H.goneSince and t - H.goneSince < H.config.reshowDelay then return "waiting" end
	if H.blocked() then return "blocked" end
	local ok, err = draw(text)
	if ok then
		if H.goneSince then H.reshows = (H.reshows or 0) + 1 end -- counts comebacks, not the first draw
		H.failures, H.lastText, H.goneSince = 0, text, nil
		return "shown"
	end
	H.failures = (H.failures or 0) + 1
	if H.failures >= H.config.maxFailures then
		H.modeOverride = "notify"
		log("hud: drawing failed " .. H.failures .. " times (" .. tostring(err) .. "), falling back to notifications")
		return notify(status(), text) and "degraded" or "failed"
	end
	return "failed"
end

------------------------------------------------------------------------------
-- drivers
------------------------------------------------------------------------------
local function schedule()
	if H.timerPending then return end
	if type(Helper) ~= "table" or type(Helper.addDelayedOneTimeCallbackOnUpdate) ~= "function" or type(getElapsedTime) ~= "function" then
		return
	end
	H.timerPending = true
	Helper.addDelayedOneTimeCallbackOnUpdate(function()
		H.timerPending = false
		local ok, err = pcall(H.tick)
		if not ok then log("hud: tick raised: " .. tostring(err)) end
		if H.wanted() or menu.shown then schedule() end
	end, false, getElapsedTime() + H.config.interval)
end
H.schedule = schedule

local function kick(force)
	local ok, err = pcall(H.tick, force)
	if not ok then log("hud: tick raised: " .. tostring(err)) end
	if H.wanted() then schedule() end
end

if not H.hooked then
	H.hooked = true
	pcall(RegisterEvent, "show", function() kick() end)
	pcall(RegisterEvent, "gfx_ok", function() kick() end)
	if type(X4MPBridge) == "table" and type(X4MPBridge.on) == "function" then
		X4MPBridge.on("status", function() kick(true) end) -- a status change is never throttled
	else
		log("hud: bridge missing, the HUD will not update")
	end
end

kick()
