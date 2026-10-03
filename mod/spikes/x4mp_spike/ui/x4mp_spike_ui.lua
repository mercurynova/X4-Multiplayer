-- x4mp_spike_ui.lua : spike v2 blocks `ui`, `ui_standalone`, `hud`, `extensions`, `links` (task M2-001).
--
-- Answers (session-2 part D): V20/R1 menu injection, standalone menu over the start menu, V22 password edit box,
-- V23 layer-3 frame, R7 extension list, R8 web links. Log step names: UI, HUD, EXT, LINKS.
--
-- Sources (all under x4-unpacked/ui/addons/):
--   OptionsMenu is ego_gameoptions/gameoptions.lua; its file-local `config` (:461) holds optionDefinitions.main (:1246)
--   and optionsLayer (:463); `config` is an upvalue of menu.displayOptions (:9345), createOptionsFrame (:3615) and
--   displayOption (:4713). A row definition with a `callback` is run on click (:13578). Rows are drawn by
--   menu.displayOption(ftable, option) (:4713) into the table created by displayOptions.
--   Menu registration + frames: ego_chatwindow/chatwindow.lua:28-46 (Menus + Helper.registerMenu), :511 (layer 3 frame),
--   :720 (frame:display()); ego_detailmonitorhelper/helper.lua:1331 registerMenu (listens for "show<name>"),
--   :3767 createFrameHandle, :1744 closeMenu. Edit box properties incl. textHidden: helper.lua:3239.
--   GetExtensionList(): gameoptions.lua:5899 (fields used there: name, enabled, error, warning, ...);
--   C.GetModifiedBasegameUIFilesExtensions(): gameoptions.lua:167,4089; CanOpenWebBrowser/OpenWebBrowser: gameoptions.lua:92,276.

-- luacheck: globals X4MPSpike DebugError Menus Helper Color OpenMenu GetExtensionList GetUISafeModeOption

local ffi = require("ffi")
local C = ffi.C

local S = X4MPSpike
if type(S) ~= "table" then
	pcall(DebugError, "[X4MP-SPIKE] UI FAIL what=core_not_loaded note=x4mp_spike_core.lua_must_load_first")
	return
end
local K, log = S.K, S.log

for _, def in ipairs({
	"const char* GetModifiedBasegameUIFilesExtensions(void);",
	"bool CanOpenWebBrowser(void);",
	"void OpenWebBrowser(const char* url);",
}) do
	pcall(ffi.cdef, def) -- "redefine" when vanilla declared it first is expected and harmless
end

S.ui = S.ui or {}
local U = S.ui

local ROW_ID = "x4mp_spike"
local ROW_NAME = "Multiplayer (X4MP test)"
local MENU_NAME = "X4MPSpikeMenu"
local HUD_NAME = "X4MPSpikeHud"
local WIN_LAYER = 4
local HUD_LAYER = 3

local URLS = {
	nexus = "https://www.nexusmods.com/x4foundations",
	workshop = "https://steamcommunity.com/app/392160/workshop/",
	steam = "steam://store/392160",
}

------------------------------------------------------------------------------
-- helpers
------------------------------------------------------------------------------
local function findMenu(name)
	for _, m in ipairs(Menus or {}) do
		if type(m) == "table" and m.name == name then return m end
	end
	return nil
end

local function isArray(t)
	return type(t) == "table" and #t > 0
end

local function validConfig(cfg)
	return type(cfg) == "table" and type(cfg.optionDefinitions) == "table" and isArray(cfg.optionDefinitions.main)
		and type(cfg.optionsLayer) == "number"
end

local function countRows(main)
	local n = 0
	for _, row in ipairs(main) do
		if type(row) == "table" and row.id == ROW_ID then n = n + 1 end
	end
	return n
end

------------------------------------------------------------------------------
-- standalone window (also used by the row callback and by the `links` block)
------------------------------------------------------------------------------
local menu = { name = MENU_NAME, mode = "test" }
U.menu = menu

local function closeWindow(dueToClose)
	log("UI", "INFO", K("what", "standalone_close", "mode", menu.mode, "due_to", dueToClose))
	Helper.closeMenu(menu, dueToClose or "close")
	menu.cleanup()
end

local function openUrl(which)
	local url = URLS[which]
	local okCan, can = pcall(function() return C.CanOpenWebBrowser() end)
	log("LINKS", "INFO", K("what", "click", "which", which, "url", url, "can_open_web_browser", okCan and can or "err"))
	if okCan and can then
		local ok, err = pcall(function() C.OpenWebBrowser(url) end)
		log("LINKS", ok and "PASS" or "FAIL", K("what", "OpenWebBrowser_called", "which", which, "err", ok and "" or err))
	else
		log("LINKS", "INFO", K("what", "OpenWebBrowser_skipped", "which", which, "reason", "CanOpenWebBrowser_false"))
		S.notify("links: cannot open a web browser here. URL: " .. url)
	end
end

function menu.display()
	Helper.clearDataForRefresh(menu, WIN_LAYER)
	local width = Helper.scaleX(420)
	menu.frame = Helper.createFrameHandle(menu, {
		layer = WIN_LAYER,
		standardButtons = Helper.standardButtons_Close,
		width = width,
		x = math.floor((Helper.viewWidth - width) / 2),
		y = math.floor(Helper.viewHeight / 4),
		autoFrameHeight = true,
		startAnimation = false,
		blurBackground = false,
		playerControls = false,
	})
	menu.frame:setBackground("solid", { color = Color["frame_background_semitransparent"] })

	local ftable = menu.frame:addTable(2, { tabOrder = 1, highlightMode = "off" })
	ftable:setColWidthPercent(1, 40)

	local row = ftable:addRow(false, { fixed = true })
	row[1]:setColSpan(2):createText(menu.mode == "links" and "X4MP link test" or "X4MP test window", Helper.headerRowCenteredProperties)

	if menu.mode == "links" then
		local row2 = ftable:addRow(false, {})
		row2[1]:setColSpan(2):createText("Click each button and note what opens.", { wordwrap = true })
		for _, entry in ipairs({ { "nexus", "Nexus page" }, { "workshop", "Workshop page" }, { "steam", "Steam link" } }) do
			local r = ftable:addRow(true, {})
			r[1]:setColSpan(2):createButton({}):setText(entry[2], { halign = "center" })
			r[1].handlers.onClick = function() openUrl(entry[1]) end
		end
	else
		local row2 = ftable:addRow(false, {})
		row2[1]:setColSpan(2):createText("Spike window. Nothing here is saved.", { wordwrap = true })
		-- V22: text hidden edit box. The text only lives in a local; only its LENGTH is ever logged.
		local pr = ftable:addRow(true, {})
		pr[1]:createText("Test password", {})
		pr[2]:createEditBox({ height = Helper.standardButtonHeight, description = "Test password", maxChars = 64,
			textHidden = true, selectTextOnActivation = false }):setText(menu.pwText or "", {})
		pr[2].handlers.onTextChanged = function(_, text) menu.pwText = text end
		pr[2].handlers.onEditBoxDeactivated = function(_, text) menu.pwText = text end
		local cr = ftable:addRow(true, {})
		cr[1]:setColSpan(2):createButton({}):setText("Check", { halign = "center" })
		cr[1].handlers.onClick = function()
			local len = menu.pwText and #menu.pwText or -1
			log("UI", "INFO", K("what", "password_check", "length", len, "text_hidden_requested", true))
			menu.pwText = nil -- never keep it
			S.notify("ui: password box check logged (length " .. len .. ")")
			menu.display()
		end
		local br = ftable:addRow(true, {})
		br[1]:setColSpan(2):createButton({}):setText("Close", { halign = "center" })
		br[1].handlers.onClick = function() closeWindow("close") end
	end

	menu.frame:display()
end

function menu.onShowMenu()
	menu.shown = true
	local okS, isStart = pcall(function() return C.IsStartmenu() end)
	log("UI", "INFO", K("what", "standalone_onShowMenu", "mode", menu.mode, "is_startmenu", okS and isStart or "n/a"))
	local ok, err = pcall(menu.display)
	log("UI", ok and "PASS" or "FAIL", K("what", "standalone_displayed", "mode", menu.mode, "err", ok and "" or err))
end

function menu.onCloseElement(dueToClose)
	closeWindow(dueToClose)
end

function menu.cleanup()
	menu.shown = nil
	menu.frame = nil
	menu.pwText = nil
end

local function ensureMenuRegistered()
	if U.menuRegistered then return true end
	if type(Helper) ~= "table" or type(Helper.registerMenu) ~= "function" then
		log("UI", "FAIL", K("what", "helper_missing", "helper", type(Helper)))
		return false
	end
	Menus = Menus or {}
	for i = #Menus, 1, -1 do -- drop a stale entry of ours (file reloaded in the same Lua state)
		if type(Menus[i]) == "table" and Menus[i].name == MENU_NAME then table.remove(Menus, i) end
	end
	table.insert(Menus, menu)
	local ok, err = pcall(Helper.registerMenu, menu)
	U.menuRegistered = ok
	log("UI", ok and "INFO" or "FAIL", K("what", "standalone_menu_registered", "name", MENU_NAME, "err", ok and "" or err))
	return ok
end

local function openWindow(mode)
	if not ensureMenuRegistered() then return false end
	menu.mode = mode
	local ok, err = pcall(OpenMenu, MENU_NAME, { 0, 0 }, nil)
	log("UI", ok and "INFO" or "FAIL", K("what", "standalone_OpenMenu_called", "mode", mode, "err", ok and "" or err))
	return ok
end

------------------------------------------------------------------------------
-- block `ui`: config capture chain + row append
------------------------------------------------------------------------------
local function onRowClicked()
	log("UI", "INFO", K("what", "row_clicked", "row", ROW_NAME))
	S.notify("ui: row '" .. ROW_NAME .. "' clicked, opening the test window")
	openWindow("test")
end

local function makeRow()
	return {
		id = ROW_ID,
		name = ROW_NAME,
		mouseOverText = "X4MP spike test row",
		callback = onRowClicked,
	}
end

-- Capture chain (mod-design 7.2): 1 UIX accessor, 2 require("debug").getupvalue over several vanilla functions.
local function captureConfig(om)
	if validConfig(U.config) then
		log("UI", "INFO", K("what", "config_capture", "source", U.configSource, "reused", true))
		return U.config, U.configSource
	end
	local result, source = nil, "none"

	-- 1. UIX
	local hasUix = type(om.uix_getConfig) == "function"
	log("UI", "INFO", K("what", "uix_accessor", "present", hasUix))
	if hasUix then
		local ok, cfg = pcall(om.uix_getConfig)
		local valid = ok and validConfig(cfg)
		log("UI", valid and "PASS" or "INFO", K("what", "uix_getConfig", "ok", ok, "valid", valid))
		if valid then result, source = cfg, "uix" end
	end

	-- 2. require("debug").getupvalue
	local okReq, dbg = pcall(require, "debug")
	local getup = okReq and type(dbg) == "table" and dbg.getupvalue or nil
	log("UI", getup and "PASS" or "INFO", K("what", "require_debug", "require_ok", okReq, "type", okReq and type(dbg) or tostring(dbg),
		"getupvalue", type(getup), "global_debug", type(_G.debug)))
	if not result and type(getup) == "function" then
		for _, fname in ipairs({ "displayOptions", "createOptionsFrame", "displayOption" }) do
			local fn = om[fname]
			local scanned, hit = 0, false
			if type(fn) == "function" then
				for i = 1, 120 do
					local ok, name, val = pcall(getup, fn, i)
					if not ok or name == nil then break end
					scanned = scanned + 1
					if name == "config" then
						local valid = validConfig(val)
						log("UI", "INFO", K("what", "upvalue_named_config", "function", fname, "index", i, "valid", valid))
						if valid then
							result, source, hit = val, "debug:" .. fname, true
							break
						end
					end
				end
			end
			log("UI", "INFO", K("what", "upvalue_scan", "function", fname, "is_function", type(fn) == "function", "upvalues", scanned, "found", hit))
			if result then break end
		end
	end

	if result then
		log("UI", "PASS", K("what", "config_capture", "source", source, "main_rows", #result.optionDefinitions.main,
			"optionsLayer", result.optionsLayer))
	else
		log("UI", "INFO", K("what", "config_capture", "source", "none", "note", "falling_back_to_displayOptions_wrapper"))
	end
	U.config, U.configSource = result, source
	return result, source
end

local function insertIntoConfig(cfg)
	local main = cfg.optionDefinitions.main
	local before = countRows(main)
	if before == 0 then
		local pos = #main + 1
		for i, row in ipairs(main) do
			if type(row) == "table" and row.id == "timelines" then pos = i + 1 break end
		end
		table.insert(main, pos, makeRow())
	end
	local after = countRows(main)
	log("UI", after == 1 and "PASS" or "FAIL", K("what", "row_state", "method", "config_insert", "action", before == 0 and "inserted" or "already_present",
		"rows_with_id", after))
end

-- Upvalue-free append (mod-design 7.2 source 4): wrap displayOptions; for "main" capture the frame that
-- createOptionsFrame returns, and draw our row into its table just before the frame is displayed.
local function appendIntoFrame(om, frame)
	local ftable = type(frame.content) == "table" and frame.content[1] or nil
	if type(ftable) ~= "table" or type(om.displayOption) ~= "function" then
		log("UI", "FAIL", K("what", "row_state", "method", "wrap_displayOptions", "action", "frame_layout_unexpected",
			"content_type", type(frame.content), "displayOption", type(om.displayOption)))
		return
	end
	local ok, err = pcall(om.displayOption, ftable, makeRow())
	log("UI", ok and "PASS" or "FAIL", K("what", "row_state", "method", "wrap_displayOptions", "action", "appended_at_end", "err", ok and "" or err))
end

local function installWrapper(om)
	if U.displayWrapper and om.displayOptions == U.displayWrapper then
		log("UI", "PASS", K("what", "row_state", "method", "wrap_displayOptions", "action", "wrapper_already_installed", "wrappers", 1))
		return
	end
	local prev = om.displayOptions
	if type(prev) ~= "function" then
		log("UI", "FAIL", K("what", "row_state", "method", "wrap_displayOptions", "action", "no_displayOptions"))
		return
	end
	local wrapper = function(optionParameter, ...)
		if optionParameter ~= "main" then return prev(optionParameter, ...) end
		local prevCreate = om.createOptionsFrame
		om.createOptionsFrame = function(...)
			local frame = prevCreate(...)
			om.createOptionsFrame = prevCreate -- one shot
			local origDisplay = frame.display
			rawset(frame, "display", function(self, ...)
				appendIntoFrame(om, self)
				return origDisplay(self, ...)
			end)
			return frame
		end
		local res = { pcall(prev, optionParameter, ...) }
		om.createOptionsFrame = prevCreate
		if not res[1] then error(res[2], 0) end
		return unpack(res, 2)
	end
	U.displayWrapper = wrapper
	om.displayOptions = wrapper
	log("UI", "INFO", K("what", "row_state", "method", "wrap_displayOptions", "action", "wrapper_installed", "wrappers", 1))
end

S.register("ui", function()
	local om = findMenu("OptionsMenu")
	log("UI", om and "INFO" or "FAIL", K("what", "optionsmenu_found", "found", om ~= nil, "menus", type(Menus) == "table" and #Menus or "n/a"))
	if not om then
		S.notify("ui: OptionsMenu not found, see log")
		return
	end
	log("UI", "INFO", K("what", "optionsmenu_functions", "displayOptions", type(om.displayOptions), "createOptionsFrame", type(om.createOptionsFrame),
		"displayOption", type(om.displayOption), "submenuHandler", type(om.submenuHandler), "currentOption", om.currentOption,
		"isStartmenu", om.isStartmenu))
	local okSafe, safe = pcall(GetUISafeModeOption)
	log("UI", "INFO", K("what", "protected_ui_mode", "value", okSafe and safe or "err"))

	local cfg, source = captureConfig(om)
	if cfg then
		insertIntoConfig(cfg)
	else
		installWrapper(om)
	end

	-- redraw the main menu if it is on screen, so the row shows without navigating away and back
	if om.currentOption == "main" and type(om.displayOptions) == "function" then
		local ok, err = pcall(om.displayOptions, "main")
		log("UI", ok and "INFO" or "FAIL", K("what", "main_menu_redraw", "ok", ok, "err", ok and "" or err))
	else
		log("UI", "INFO", K("what", "main_menu_redraw", "ok", false, "reason", "main_menu_not_showing", "currentOption", om.currentOption))
	end
	log("UI", "INFO", K("what", "ui_block_done", "source", source))
	S.notify("ui: done (source " .. tostring(source) .. "). Look for the row '" .. ROW_NAME .. "'; if missing open Settings and go back.")
end, "V20/R1: capture OptionsMenu config, append the row 'Multiplayer (X4MP test)'")

------------------------------------------------------------------------------
-- block `ui_standalone`
------------------------------------------------------------------------------
S.register("ui_standalone", function()
	if openWindow("test") then
		S.notify("ui_standalone: window requested, see log (UI standalone_*)")
	else
		S.notify("ui_standalone: could not open the window, see log")
	end
end, "D2/D4: standalone test window with 'Test password' field and 'Check' button")

------------------------------------------------------------------------------
-- block `links`
------------------------------------------------------------------------------
S.register("links", function()
	if openWindow("links") then
		S.notify("links: window requested. Click the three buttons.")
	else
		S.notify("links: could not open the window, see log")
	end
end, "R8: window with Nexus, Workshop and steam:// buttons")

------------------------------------------------------------------------------
-- block `hud` (V23): passive frame on layer 3 (like the chat window), top right
------------------------------------------------------------------------------
local hud = { name = HUD_NAME }
U.hud = hud

function hud.display()
	Helper.clearDataForRefresh(hud, HUD_LAYER)
	local width = Helper.scaleX(220)
	hud.frame = Helper.createFrameHandle(hud, {
		layer = HUD_LAYER,
		x = Helper.viewWidth - width - Helper.scaleX(20),
		y = Helper.scaleY(20),
		width = width,
		autoFrameHeight = true,
		standardButtons = {},
		startAnimation = false,
		blurBackground = false,
		playerControls = true,
	})
	hud.frame:setBackground("solid", { color = Color["frame_background_semitransparent"] })
	local ftable = hud.frame:addTable(1, { tabOrder = 0, highlightMode = "off", reserveScrollBar = false })
	local row = ftable:addRow(false, { fixed = true })
	row[1]:createText("X4MP test HUD", Helper.headerRowCenteredProperties)
	local row2 = ftable:addRow(false, { fixed = true })
	row2[1]:createText("connected (fake)", { halign = "center" })
	hud.frame:display()
end

function hud.onShowMenu()
	hud.shown = true
	local ok, err = pcall(hud.display)
	log("HUD", ok and "PASS" or "FAIL", K("what", "hud_displayed", "layer", HUD_LAYER, "err", ok and "" or err))
end

function hud.close()
	if hud.shown then
		Helper.clearFrame(hud, HUD_LAYER)
		hud.cleanup()
		log("HUD", "INFO", K("what", "hud_closed"))
	end
end

function hud.onCloseElement()
	hud.close()
end

function hud.cleanup()
	hud.shown = nil
	hud.frame = nil
end

local function ensureHudRegistered()
	if U.hudRegistered then return true end
	if type(Helper) ~= "table" or type(Helper.registerMenu) ~= "function" then return false end
	Menus = Menus or {}
	for i = #Menus, 1, -1 do
		if type(Menus[i]) == "table" and Menus[i].name == HUD_NAME then table.remove(Menus, i) end
	end
	table.insert(Menus, hud)
	local ok, err = pcall(Helper.registerMenu, hud)
	U.hudRegistered = ok
	log("HUD", ok and "INFO" or "FAIL", K("what", "hud_menu_registered", "err", ok and "" or err))
	return ok
end

-- passive watcher: logs (at most once per change) whether our layer-3 frame is still on screen after other menus
local lastPresent = nil
local nextCheck = 0
S.addUpdate(function()
	if not hud.shown then return end
	local t = S.now()
	if t < nextCheck then return end
	nextCheck = t + 1
	local present = type(hud.frames) == "table" and hud.frames[HUD_LAYER] ~= nil
	if present ~= lastPresent then
		lastPresent = present
		log("HUD", "INFO", K("what", "frame_present_change", "present", present))
	end
end)

S.register("hud", function(args)
	if args._[1] == "off" then
		hud.close()
		return
	end
	if not ensureHudRegistered() then
		S.notify("hud: Helper missing, see log")
		return
	end
	hud.onShowMenu() -- same as the chat window: draws directly, never goes through OpenMenu (idempotent: rebuilds the one frame)
	S.notify("hud: 'X4MP test HUD' requested at the top right. '/x4mpspike hud off' removes it.")
end, "V23: passive layer-3 frame 'X4MP test HUD' at the top right (arg: off)")

------------------------------------------------------------------------------
-- block `extensions` (R7)
------------------------------------------------------------------------------
local function scalar(v)
	local t = type(v)
	if t == "string" or t == "number" or t == "boolean" then return v end
	return nil
end

S.register("extensions", function()
	local ok, list = pcall(GetExtensionList)
	if not ok or type(list) ~= "table" then
		log("EXT", "FAIL", K("what", "GetExtensionList", "ok", ok, "err", ok and type(list) or list))
	else
		log("EXT", "INFO", K("what", "GetExtensionList", "count", #list))
		local fields = {}
		local seen = {}
		for _, ext in ipairs(list) do
			if type(ext) == "table" then
				for k in pairs(ext) do
					if not seen[k] then seen[k] = true fields[#fields + 1] = tostring(k) .. ":" .. type(ext[k]) end
				end
			end
		end
		table.sort(fields)
		log("EXT", "INFO", K("what", "field_names", "fields", table.concat(fields, ",")))
		for i, ext in ipairs(list) do
			if i > 200 then
				log("EXT", "INFO", K("what", "truncated", "after", 200))
				break
			end
			if type(ext) == "table" then
				local keys = {}
				for k in pairs(ext) do if scalar(ext[k]) ~= nil then keys[#keys + 1] = tostring(k) end end
				table.sort(keys)
				local kv = {}
				for _, k in ipairs(keys) do
					local v = scalar(ext[k])
					if type(v) == "string" and #v > 80 then v = v:sub(1, 80) end
					kv[#kv + 1] = k
					kv[#kv + 1] = v
				end
				log("EXT", "INFO", "extension index=" .. i .. " " .. K(unpack(kv)))
			end
		end
	end
	local ok2, res = pcall(function() return ffi.string(C.GetModifiedBasegameUIFilesExtensions()) end)
	log("EXT", ok2 and "INFO" or "FAIL", K("what", "GetModifiedBasegameUIFilesExtensions", "value", ok2 and (res == "" and "(empty)" or res) or res))
	S.notify("extensions: list written to the log")
end, "R7: dump GetExtensionList() and GetModifiedBasegameUIFilesExtensions() to the log")
