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

-- luacheck: globals X4MPBridge X4MPScreens X4MPHud __X4MP_USER Menus Helper Color View AddUITriggeredEvent getElapsedTime RegisterEvent IsValidWidgetElement

local H = X4MPHud or {}
X4MPHud = H
H.loaded = true

local MENU_NAME = "X4MPHud"
local STATE_TEXT = { connecting = 31, handshaking = 32, checking_save = 33, downloading = 34, loading = 35, matching = 36,
	ingame = 37, rejected = 38, error = 39, save_changed = 45 }

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
	-- View.menus entries that do not count as "another menu is open". TopLevelMenu (menu_toplevel.lua, the top bar) and DockedMenu
	-- (menu_docked.lua, shown while docked) stay up during normal play: session-3 retest logged "blocked for 10 s by TopLevelMenu/Helper2".
	ignoreMenus = { ChatWindow = true, TopLevelMenu = true, DockedMenu = true },
	-- Off: the HUD is on layer 6 and cannot displace the chat window (Helper3). Yielding left the line gone for minutes after the chat closed.
	yieldMenus = {},     -- names of View menus while which the HUD never draws, hides or touches anything
	refreshInterval = 5, -- seconds after which an unchanged frame is drawn once more (heals a frame lost without notice)
	yieldLogInterval = 30, -- seconds between "yielding" log lines
	timerStale = 2,      -- a pending delayed callback older than this many intervals (+1 s) counts as lost and is armed again
	restartLogInterval = 30, -- seconds between "loop restarted" log lines
	blockedMax = 10,     -- seconds another menu may keep the HUD from drawing before it draws anyway (a stale View entry must not hide it for good)
	fullscreenMenus = { MapMenu = true }, -- treated as live when the engine gives no frame-liveness signal (never forced over)
	liveLogInterval = 30, -- seconds between "blocked by X (live), not forcing" log lines
	outcomeLogInterval = 30, -- seconds between repeats of the same unusual tick outcome in the log
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
--- Live session 3: after the chat window (or the map) closed, the line never came back because a View entry of ours outlived the frame.
--- A frame counts as on screen only while the view entry exists AND we still consider it shown (onCloseElement / cleanup clear that) AND the
--- engine still knows the frame widget.
function H.present()
	local v = viewEntryPresent()
	if v == false then return false end
	if menu.shown ~= true then return false end
	if v == true and type(IsValidWidgetElement) == "function" and type(menu.frames) == "table" then
		local f = menu.frames[H.config.layer]
		if f ~= nil then
			local ok, valid = pcall(IsValidWidgetElement, f)
			if ok and not valid then return false end
		end
	end
	return true
end

--- names of the View entries that block drawing, for the log
function H.blockers()
	local names = {}
	if type(View) == "table" and type(View.menus) == "table" then
		local ignore = H.config.ignoreMenus
		for _, entry in ipairs(View.menus) do
			if type(entry) == "table" and entry.name ~= MENU_NAME and not entry.minimized and not (entry.name and ignore[entry.name]) then
				names[#names + 1] = tostring(entry.name) .. "/" .. tostring(entry.id)
			end
		end
	end
	return #names > 0 and table.concat(names, ",") or "-"
end

--- number of View entries that are not ours (any change = some menu opened or closed: draw once more to be safe)
local function otherMenuCount()
	if type(View) ~= "table" or type(View.menus) ~= "table" then return 0 end
	local n = 0
	for _, entry in ipairs(View.menus) do
		if type(entry) == "table" and entry.name ~= MENU_NAME then n = n + 1 end
	end
	return n
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

--- A minimized vanilla menu (Helper.minimizedMenu): every frame:display() runs Helper.closeMinimizedMenus (helper.lua:4113, defined :1510) and
--- would close it, so the HUD never draws while one exists (it would otherwise do so on every 5 s refresh).
function H.minimizedMenuOpen()
	return type(Helper) == "table" and Helper.minimizedMenu ~= nil and Helper.minimizedMenu ~= false
end

--- true when another (non-ignored, non-minimized) menu is open: drawing then would be useless or disturb it
function H.blocked()
	if H.minimizedMenuOpen() then return true end
	if type(View) ~= "table" or type(View.menus) ~= "table" then return false end
	local ignore = H.config.ignoreMenus
	for _, entry in ipairs(View.menus) do
		if type(entry) == "table" and entry.name ~= MENU_NAME and not entry.minimized and not (entry.name and ignore[entry.name]) then
			return true
		end
	end
	return false
end

--- Is a blocking View entry a live menu? View.registerMenu / View.updateMenu (viewhelper.lua) store the engine frame ids in entry.frames, the
--- same ids Helper keeps in menu.frames[layer] and checks with IsValidWidgetElement (helper.lua:1660). A real open menu has at least one valid
--- frame; a stale entry (session 3: TopLevelMenu/Helper2 after the checkpoint saves) has none. Returns the names of the live blockers or nil.
--- Without IsValidWidgetElement / entry.frames there is no signal: only config.fullscreenMenus count as live then.
function H.liveBlockers()
	local names = {}
	if H.minimizedMenuOpen() then names[#names + 1] = "Minimized" end
	if type(View) == "table" and type(View.menus) == "table" then
		local ignore = H.config.ignoreMenus
		for _, entry in ipairs(View.menus) do
			if type(entry) == "table" and entry.name ~= MENU_NAME and not entry.minimized and not (entry.name and ignore[entry.name]) then
				local live
				if type(IsValidWidgetElement) == "function" and type(entry.frames) == "table" then
					live = false
					for _, f in pairs(entry.frames) do
						local ok, valid = pcall(IsValidWidgetElement, f)
						if ok and valid then live = true break end
					end
				else
					live = entry.name ~= nil and H.config.fullscreenMenus[entry.name] == true
				end
				if live then names[#names + 1] = tostring(entry.name) end
			end
		end
	end
	return #names > 0 and table.concat(names, ",") or nil
end

--- M3-28: another menu (the map) can destroy our frame before onCloseElement ran; menu.frames[layer] then holds an id the engine no longer knows and
--- Helper.clearDataForRefresh -> GetChildren(frame) logs "invalid frame ID" (helper.lua:2418; 14 errors in the sitting 3 authority log). Drop a stale id
--- first. No IsValidWidgetElement = no signal = nothing is dropped (as before). Returns true when an id was dropped.
function H.dropStaleFrame(layer)
	local frames = menu.frames
	if type(frames) ~= "table" or frames[layer] == nil or type(IsValidWidgetElement) ~= "function" then return false end
	local ok, valid = pcall(IsValidWidgetElement, frames[layer])
	if ok and valid ~= true then
		frames[layer] = nil
		return true
	end
	return false
end

function menu.display()
	local cfg = H.config
	H.dropStaleFrame(cfg.layer)
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
		-- Close-out A, item 1 (chat + Esc): the default type is "Helper" (helper.lua:3140), which vanilla treats as "a menu is open":
		-- View.clearMenus({ Helper = true }) (helper.lua:1404) closes every such entry through its clearCallback, View.hasMenu({ Helper = true })
		-- (helper.lua:1359, :1371, :1845) stops the docked / top-level menu from opening, and the engine's Esc handling for "close the open
		-- menu" ran on our frame instead of leaving the chat edit box (chatwindow.lua editboxSendMessage) to deactivate itself: the chat
		-- stayed in its typing state (typing = true, playerControls = false, never fades) and the cockpit HUD stayed hidden. The chat window
		-- avoids all of that with its own type (chatwindow.lua:521); a passive status line gets one too, so it never takes part in the
		-- vanilla "close the Helper menus" paths and Esc / Enter behave exactly as without X4MP.
		viewHelperType = "X4MPHud",
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

--- Releases our registration in View the way vanilla Helper.clearMenu does for its own frames (helper.lua clearFrame -> View.unregisterMenu).
--- View.clearMenus({ Helper = true }) (run by every vanilla Helper menu that opens, helper.lua:1404) only calls our clearCallback; the entry
--- stays in View.menus unless the callback removes it. A stale "Helper<layer>" entry would still be counted by View.currentFrames and by the
--- AND-ed View.keepHUDVisible() / hasPlayerControls() flags that every later CreateView / UpdateFrame / CloseFrame call passes to the engine.
local function releaseRegistration()
	if type(Helper) ~= "table" or type(Helper.clearFrame) ~= "function" then return end
	menu.closing = true
	local ok, err = pcall(Helper.clearFrame, menu, H.config.layer)
	menu.closing = nil
	if not ok then log("hud: releasing the view registration failed: " .. tostring(err)) end
end

--- the engine closes our frame whenever another menu opens (session 2): note it, draw again later; never reopen from here
function menu.onCloseElement()
	if menu.closing then return end
	if menu.shown then H.goneSince = now() end
	if viewEntryPresent() == true then releaseRegistration() end
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
--- What View holds right now, one short line: "Helper6=X4MPHud[hud=1,pc=1] Helper2=MapMenu[hud=0,pc=0] frames=2". The cockpit HUD is shown only
--- while every entry says hud=1 (viewhelper.lua keepHUDVisible / hasPlayerControls are ANDs over all entries), so this line is the evidence
--- for "why is the vanilla HUD gone". It is logged only when it changes.
function H.viewSnapshot()
	if type(View) ~= "table" or type(View.menus) ~= "table" then return nil end
	local parts = {}
	for _, entry in ipairs(View.menus) do
		if type(entry) == "table" then
			local pr = type(entry.properties) == "table" and entry.properties or {}
			parts[#parts + 1] = string.format("%s=%s[hud=%d,pc=%d%s]", tostring(entry.id), tostring(entry.name), pr.keepHUDVisible and 1 or 0,
				pr.playerControls and 1 or 0, entry.minimized and ",min" or "")
		end
	end
	return table.concat(parts, " ") .. " frames=" .. tostring(View.currentFrames)
end

local function logViewChange()
	local snap = H.viewSnapshot()
	if snap and snap ~= H.lastSnapshot then
		H.lastSnapshot = snap
		log("hud: view " .. snap)
	end
end

local function draw(text)
	if not registerMenu() then return false, "no_helper" end
	menu.text = text
	local ok, err = pcall(menu.onShowMenu)
	if not ok then
		menu.cleanup()
		return false, tostring(err)
	end
	H.lastDrawAt = now()
	logViewChange()
	return true
end

function H.hide()
	-- also when only a stale registration is left (the frame is gone but View still lists us): it must not outlive the HUD
	if menu.shown or viewEntryPresent() == true then
		releaseRegistration()
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

	local others = otherMenuCount()
	if H.lastOthers ~= nil and others ~= H.lastOthers then H.forceRedraw = true end -- a menu opened or closed
	H.lastOthers = others

	if H.present() then
		if H.minimizedMenuOpen() then return "present" end -- drawing would close the minimized menu
		local stale = H.lastDrawAt and (t < H.lastDrawAt or t - H.lastDrawAt >= H.config.refreshInterval)
		if text ~= H.lastText or H.forceRedraw or stale then
			local ok, err = draw(text)
			if ok then H.lastText, H.forceRedraw = text, nil else log("hud: redraw failed: " .. tostring(err)) end
			return ok and "updated" or "failed"
		end
		return "present"
	end
	H.forceRedraw = nil

	-- frame not on screen: either never drawn, or an engine-side close (menu) removed it
	if menu.shown then menu.cleanup() end
	if not H.goneSince then
		if H.lastText ~= nil then H.goneSince = t end -- it was drawn before: wait out the delay; a first draw is immediate
	end
	if H.goneSince and t - H.goneSince < H.config.reshowDelay then return "waiting" end
	if H.minimizedMenuOpen() then return "blocked" end -- never forced: drawing closes the minimized menu
	if H.blocked() then
		H.blockedSince = H.blockedSince or t
		if t < H.blockedSince or t - H.blockedSince < H.config.blockedMax then return "blocked" end
		-- A live menu (valid frames) is never drawn over: frame:display() kills its render target (live session 4: the open map went blurry
		-- 11.6 s in). Only an entry without any valid frame counts as stale, for blockedMax from when it last looked live.
		local live = H.liveBlockers()
		if live then
			H.blockedSince = t
			if not H.lastLiveLogAt or t < H.lastLiveLogAt or t - H.lastLiveLogAt >= H.config.liveLogInterval then
				H.lastLiveLogAt = t
				log("hud: blocked by " .. live .. " (live), not forcing")
			end
			return "blocked"
		end
		-- Blocked for a long time: probably a stale View entry (session 3 Run 3: the line never came back after the checkpoint saves). Draw
		-- anyway, at most once per refreshInterval; the engine closes our frame again if a real menu is up, which costs nothing.
		if H.lastForcedAt and t >= H.lastForcedAt and t - H.lastForcedAt < H.config.refreshInterval then return "blocked" end
		H.lastForcedAt = t
		log("hud: blocked for " .. string.format("%.0f", t - H.blockedSince) .. " s by " .. H.blockers() .. ", drawing anyway")
	end
	H.blockedSince = nil
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
local function logRestart(reason)
	local t = now()
	if not H.lastRestartLog or t < H.lastRestartLog or t - H.lastRestartLog >= H.config.restartLogInterval then
		H.lastRestartLog = t
		log("hud: loop restarted (" .. tostring(reason) .. ")")
	end
end

--- Arms the self-rescheduling check. Helper's one-time callback queue (helper.lua:922-938, run by the global onUpdate script) is a plain list that
--- is emptied before its callbacks run, so ONE callback that raises (any mod's) drops the rest of that batch, ours included, and our
--- "pending" flag would then stay true forever. So a pending callback older than the stale limit counts as lost: it is armed again under a
--- new generation number (the old one, should it still run, does nothing).
local function schedule(reason)
	if type(Helper) ~= "table" or type(Helper.addDelayedOneTimeCallbackOnUpdate) ~= "function" or type(getElapsedTime) ~= "function" then
		return
	end
	local t = now()
	if H.timerPending then
		local age = t - (H.timerArmedAt or t)
		if age >= 0 and age < H.config.interval * H.config.timerStale + 1 then return end
		logRestart(reason or "timer lost")
	end
	H.timerGen = (H.timerGen or 0) + 1
	local gen = H.timerGen
	H.timerPending, H.timerArmedAt = true, t
	Helper.addDelayedOneTimeCallbackOnUpdate(function()
		if gen ~= H.timerGen then return end -- superseded by a newer arm
		H.timerPending = false
		local ok, err = pcall(H.tick)
		if not ok then log("hud: tick raised: " .. tostring(err)) end
		if H.wanted() or menu.shown then pcall(schedule, "loop") end
	end, false, t + H.config.interval)
end
H.schedule = schedule

--- every outside nudge (status event, show / gfx_ok, ...) runs one check and makes sure the loop is armed
local function kick(force, reason)
	local ok, res = pcall(H.tick, force)
	if not ok then log("hud: tick raised: " .. tostring(res)) end
	if H.wanted() then schedule(reason) end
end
H.kick = kick

if not H.hooked then
	H.hooked = true
	pcall(RegisterEvent, "show", function() kick(false, "show") end)
	pcall(RegisterEvent, "gfx_ok", function() kick(false, "gfx_ok") end)
	if type(X4MPBridge) == "table" and type(X4MPBridge.on) == "function" then
		X4MPBridge.on("status", function() kick(true, "status") end) -- a status change is never throttled
	else
		log("hud: bridge missing, the HUD will not update")
	end
end

kick(false, "load")
