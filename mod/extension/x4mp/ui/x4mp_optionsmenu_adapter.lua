-- x4mp_optionsmenu_adapter.lua : the embedded start-menu entry "Multiplayer" (M2-11, mod-design 7.2).
--
-- Adds one row to the vanilla options menu (start menu and Esc menu), right after "Play Timelines". Clicking it opens the X4MP
-- Multiplayer screen through X4MPScreens.open("main"); the renderer (x4mp_ui_standalone.lua, M2-06 restores the start menu on
-- close) owns the window and its closing, this file does not.
--
-- Probe chain for the options-menu `config` table (session 2: PASS without UIX/SirNukes, PASS with both enabled):
--   1. uix      the kuertee UI Extensions accessor OptionsMenu.uix_getConfig (preferred when present)
--   2. debug    require("debug").getupvalue on displayOptions / createOptionsFrame / displayOption, upvalue named "config"
--   3. append   no config reachable: wrap displayOptions and draw our row into the "main" frame just before it is displayed
--               (never exercised in game: session 2 always reached 1 or 2)
--   else        DEGRADED: no embedded row; the window stays reachable with the /x4mp chat command (x4mp_menu.lua)
-- Only vanilla option-row semantics are used; nothing is copied from UIX or SirNukes. Session 2 evidence: the row appears
-- exactly once, survives /reloadui and SirNukes' /rui, and SirNukes' "Extension Options" entry keeps working.
--
-- Idempotence: the row is found by id before it is inserted, so loading this file again (or the game redrawing the menu) never
-- duplicates it. After /reloadui the Lua state is rebuilt: the fresh config gets one row again.
-- One log line per result: "X4MP ui: optionsmenu adapter OK(source=...)" or "... DEGRADED(reason)".
--
-- Sources (under x4-unpacked/ui/addons/): ego_gameoptions/gameoptions.lua (menu.displayOptions, config.optionDefinitions.main,
-- row fields id/name/mouseOverText/callback, menu.currentOption), ego_detailmonitorhelper/helper.lua (Helper.registerMenu).

-- luacheck: globals X4MPBridge X4MPScreens X4MPOptionsAdapter Menus

local A = X4MPOptionsAdapter or {}
X4MPOptionsAdapter = A
A.loaded = true
A.ROW_ID = "x4mp_multiplayer"
A.MENU_NAME = "OptionsMenu"
A.MAX_ATTEMPTS = 5 -- the OptionsMenu may not be registered yet at load; retried on the game's gfx_ok / show events

local function log(msg)
	if type(X4MPBridge) == "table" and type(X4MPBridge.log) == "function" then
		X4MPBridge.log(msg)
	else
		pcall(DebugError, "[X4MP] " .. tostring(msg))
	end
end

local function T(id)
	local S = X4MPScreens
	if type(S) == "table" and type(S.T) == "function" then return S.T(id) end
	return ""
end

------------------------------------------------------------------------------
-- helpers
------------------------------------------------------------------------------
local function findMenu()
	if type(Menus) ~= "table" then return nil end
	for _, m in ipairs(Menus) do
		if type(m) == "table" and m.name == A.MENU_NAME then return m end
	end
	return nil
end

local function validConfig(cfg)
	if type(cfg) ~= "table" or type(cfg.optionsLayer) ~= "number" then return false end
	local defs = cfg.optionDefinitions
	return type(defs) == "table" and type(defs.main) == "table" and #defs.main > 0
end

local function onRowClicked()
	local S = X4MPScreens
	if type(S) ~= "table" or type(S.open) ~= "function" then
		log("ui: multiplayer row clicked but the screens are not loaded")
		return
	end
	local ok, err = pcall(S.open, "main")
	if not ok then log("ui: opening the multiplayer screen failed: " .. tostring(err)) end
end

local function makeRow()
	return { id = A.ROW_ID, name = T(1), mouseOverText = T(300), callback = onRowClicked }
end

local function countRows(main)
	local n = 0
	for _, row in ipairs(main) do
		if type(row) == "table" and row.id == A.ROW_ID then n = n + 1 end
	end
	return n
end

------------------------------------------------------------------------------
-- source 1 and 2: capture the config
------------------------------------------------------------------------------
local function captureFromUix(om)
	if type(om.uix_getConfig) ~= "function" then return nil end
	local ok, cfg = pcall(om.uix_getConfig)
	if ok and validConfig(cfg) then return cfg end
	return nil
end

local function captureFromDebug(om)
	local okReq, dbg = pcall(require, "debug")
	local getup = okReq and type(dbg) == "table" and dbg.getupvalue or nil
	if type(getup) ~= "function" then return nil end
	for _, fname in ipairs({ "displayOptions", "createOptionsFrame", "displayOption" }) do
		local fn = om[fname]
		if type(fn) == "function" then
			for i = 1, 120 do
				local ok, name, val = pcall(getup, fn, i)
				if not ok or name == nil then break end
				if name == "config" and validConfig(val) then return val, fname end
			end
		end
	end
	return nil
end

--- inserts (or refreshes) the row after "Play Timelines"; returns the number of our rows in main afterwards
local function insertIntoConfig(cfg)
	local main = cfg.optionDefinitions.main
	if countRows(main) == 0 then
		local pos = #main + 1
		for i, row in ipairs(main) do
			if type(row) == "table" and row.id == "timelines" then
				pos = i + 1
				break
			end
		end
		table.insert(main, pos, makeRow())
	else
		-- already there (repeated load): refresh text and callback in place, never add a second row
		for _, row in ipairs(main) do
			if type(row) == "table" and row.id == A.ROW_ID then
				row.name, row.mouseOverText, row.callback = T(1), T(300), onRowClicked
			end
		end
	end
	return countRows(main)
end

------------------------------------------------------------------------------
-- source 3: wrap displayOptions (chains to whatever was there before, installed once)
------------------------------------------------------------------------------
local function installWrapper(om)
	if om.x4mpDisplayWrapped then return true end
	local prev = om.displayOptions
	if type(prev) ~= "function" or type(om.createOptionsFrame) ~= "function" or type(om.displayOption) ~= "function" then
		return false
	end
	om.x4mpDisplayWrapped = true
	om.displayOptions = function(optionParameter, ...)
		if optionParameter ~= "main" then return prev(optionParameter, ...) end
		local prevCreate = om.createOptionsFrame
		om.createOptionsFrame = function(...)
			local frame = prevCreate(...)
			om.createOptionsFrame = prevCreate -- one shot
			if type(frame) == "table" and type(frame.display) == "function" then
				local origDisplay = frame.display
				rawset(frame, "display", function(self, ...)
					local ftable = type(self.content) == "table" and self.content[1] or nil
					if type(ftable) == "table" then pcall(om.displayOption, ftable, makeRow()) end
					return origDisplay(self, ...)
				end)
			end
			return frame
		end
		local res = { pcall(prev, optionParameter, ...) }
		om.createOptionsFrame = prevCreate
		if not res[1] then error(res[2], 0) end
		return unpack(res, 2)
	end
	return true
end

------------------------------------------------------------------------------
-- install
------------------------------------------------------------------------------
local function report(line)
	if A.lastLine == line then return end
	A.lastLine = line
	log("ui: optionsmenu adapter " .. line)
end

--- tries the probe chain once; returns "ok", "degraded" or "pending" (menu not there yet)
function A.install()
	local om = findMenu()
	if not om then return "pending" end

	local cfg, source = captureFromUix(om), "uix"
	if not cfg then
		local fname
		cfg, fname = captureFromDebug(om)
		source = fname and ("debug:" .. fname) or "debug"
	end

	local method
	if cfg then
		local rows = insertIntoConfig(cfg)
		if rows ~= 1 then
			A.source, A.degraded = nil, "row_count_" .. rows
			report("DEGRADED(" .. A.degraded .. ")")
			return "degraded"
		end
		method = source
	elseif installWrapper(om) then
		method = "append"
	else
		A.source, A.degraded = nil, "no_config_source"
		report("DEGRADED(no_config_source)")
		return "degraded"
	end
	A.source, A.degraded = method, nil
	report("OK(source=" .. method .. ")")

	-- redraw the main menu if it is on screen, so the row shows without leaving and re-entering it
	if om.currentOption == "main" and type(om.displayOptions) == "function" then
		local ok, err = pcall(om.displayOptions, "main")
		if not ok then log("ui: main menu redraw failed: " .. tostring(err)) end
	end
	return "ok"
end

--- load-time and retry entry: stops after success; reports DEGRADED when the OptionsMenu never shows up
function A.attempt()
	if A.source then return "ok" end -- already installed in this Lua state
	A.attempts = (A.attempts or 0) + 1
	local ok, res = pcall(A.install)
	if not ok then
		log("ui: optionsmenu adapter raised: " .. tostring(res))
		A.degraded = "error"
		report("DEGRADED(error)")
		return "degraded"
	end
	if res == "pending" then
		if A.attempts >= A.MAX_ATTEMPTS then
			A.degraded = "optionsmenu_not_found"
			report("DEGRADED(optionsmenu_not_found)")
			return "degraded"
		end
		return "pending"
	end
	return res
end

if not A.hooked then
	A.hooked = true
	pcall(RegisterEvent, "gfx_ok", function() A.attempt() end)
	pcall(RegisterEvent, "show", function() A.attempt() end)
end

-- every load starts a fresh run of the chain (the row is found by id, so this cannot duplicate it)
A.source, A.attempts = nil, 0
A.attempt()
