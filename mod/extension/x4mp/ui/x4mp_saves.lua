-- x4mp_saves.lua : client save control, Lua half (M2-10).
--
-- While this node is connected to a session as a client, saving is blocked:
--   * SaveGame is swallowed, IsSavingPossible returns false (global wrappers, chained, see below);
--   * the Esc-menu "Save Game" row is greyed and its tooltip says why. The options menu captured the ORIGINAL
--     IsSavingPossible in its config table, so the row's selectable function is replaced by one that looks the global up
--     at call time (config reached through UIX's uix_getConfig, an explicit handoff, or require("debug") upvalues);
--   * the MD flag global.$x4mp_noSave is set (md/notifications.xml drops the vanilla autosave request, md/x4mp_saves.xml
--     sets the flag and reports every game save).
-- Quicksave bypasses Lua (session 2, C3): it is only DETECTED. md/x4mp_saves.xml raises "x4mp.md_game_saved" for every save
-- (event_game_saved) and this file forwards it to native as the verb x4mp.game_saved; native warns while we are a client.
--
-- The flag is in-memory and is LOST on every save load and /reloadui. It is never decided here: native owns the state and
-- pushes the flag on every ui_ready / load (topic x4mp.saves). On load this file starts unblocked and clears the MD flag
-- (which lives in the save game and may be stale), then waits for the push.
--
-- Bridge additions (own file, the bridge itself is M2-06's): native -> Lua topic  x4mp.saves {"v":1,"block":bool};
-- Lua -> native verbs  x4mp.saves_status, x4mp.game_saved, x4mp.selftest  (contract: docs/mod-design.md section 7).
--
-- Wrapper rules: a wrapper calls whatever function was installed before it (chaining, so UIX / SirNukes / other mods keep
-- working). uninstall() puts the previous function back ONLY when ours is still the top of the chain; if somebody wrapped on
-- top of us, our wrapper stays in place but becomes a pure pass-through, so it can never block anything after uninstall.
-- install() is idempotent (a second load in the same Lua state does not stack a second wrapper).
--
-- Other X4MP code: X4MPSaves.allowSaves(fn) runs fn with the block lifted (the authority's SaveJob, M2-09, uses it).
--
-- luacheck: globals X4MPSaves X4MPBridge DebugError RegisterEvent SaveGame IsSavingPossible ExecuteDebugCommand
-- luacheck: globals AddUITriggeredEvent ReadText Menus

local S = rawget(_G, "X4MPSaves")
if type(S) ~= "table" then
	S = { blocking = false, bypass = 0, wrappers = {}, hooks = {} }
	X4MPSaves = S
end
S.MD_SCREEN = "X4MP_Saves"
S.TEXT_PAGE, S.TEXT_ID_TOOLTIP = 92000, 60
S.FALLBACK_TOOLTIP = "Saving is disabled while connected as a client"

local unpack = rawget(_G, "unpack") or rawget(table, "unpack")

local function log(msg)
	pcall(DebugError, "[X4MP] saves: " .. tostring(msg))
end

local function bridge()
	local B = rawget(_G, "X4MPBridge")
	if type(B) == "table" and type(B.json) == "table" then return B end
	return nil
end

------------------------------------------------------------------------------
-- chained global wrappers
------------------------------------------------------------------------------
-- spec: name of the global, make(prev, rec) -> the wrapper function. rec.active is false after uninstall().
local function installWrapper(name, make)
	local rec = S.wrappers[name]
	if rec and rec.active then return "already_installed" end
	local prev = rawget(_G, name)
	if type(prev) ~= "function" then return "missing_global" end
	rec = { prev = prev, active = true }
	rec.fn = make(prev, rec)
	S.wrappers[name] = rec
	_G[name] = rec.fn
	return "installed"
end

local function uninstallWrapper(name)
	local rec = S.wrappers[name]
	if not rec or not rec.active then return "not_installed" end
	rec.active = false -- a wrapper that is still referenced by someone's chain turns into a pass-through
	if rawget(_G, name) == rec.fn then
		_G[name] = rec.prev
		return "removed"
	end
	return "left_in_place"
end

local function blocked()
	return S.blocking and S.bypass == 0
end

function S.install()
	local res = {}
	res.SaveGame = installWrapper("SaveGame", function(prev, rec)
		return function(...)
			if rec.active and blocked() then
				log("SaveGame blocked (connected as a client)")
				return
			end
			return prev(...)
		end
	end)
	res.IsSavingPossible = installWrapper("IsSavingPossible", function(prev, rec)
		return function(...)
			if rec.active and blocked() then return false end
			return prev(...)
		end
	end)
	res.ExecuteDebugCommand = installWrapper("ExecuteDebugCommand", function(prev, rec)
		return function(cmd, ...)
			if rec.active and cmd == "x4mp_selftest" then
				S.requestSelfTest()
				return
			end
			return prev(cmd, ...)
		end
	end)
	S.hookMenu()
	return res
end

--- Puts the previous functions back where ours is still on top; see the header for the rest.
function S.uninstall()
	local res = {}
	for _, name in ipairs({ "SaveGame", "IsSavingPossible", "ExecuteDebugCommand" }) do
		res[name] = uninstallWrapper(name)
	end
	return res
end

--- Runs fn() with the block lifted (nested calls fine); returns fn's results. For code that must save while connected.
function S.allowSaves(fn)
	S.bypass = S.bypass + 1
	local res = { pcall(fn) }
	S.bypass = S.bypass - 1
	if not res[1] then error(res[2], 0) end
	return unpack(res, 2)
end

------------------------------------------------------------------------------
-- Esc menu: greyed Save row and tooltip
------------------------------------------------------------------------------
function S.tooltipText()
	local ok, text = pcall(ReadText, S.TEXT_PAGE, S.TEXT_ID_TOOLTIP)
	if ok and type(text) == "string" and text ~= "" then return text end
	return S.FALLBACK_TOOLTIP
end

local function validConfig(cfg)
	return type(cfg) == "table" and type(cfg.optionDefinitions) == "table" and type(cfg.optionDefinitions.main) == "table"
end

local function findOptionsMenu()
	local menus = rawget(_G, "Menus")
	if type(menus) ~= "table" then return nil end
	for _, m in ipairs(menus) do
		if type(m) == "table" and m.name == "OptionsMenu" then return m end
	end
	return nil
end

--- Another X4MP file that already holds the options menu config can hand it over (M2-11).
function S.setMenuConfig(cfg)
	if validConfig(cfg) then S.menuConfig = cfg end
end

local function getConfig(om)
	if validConfig(S.menuConfig) then return S.menuConfig, "handoff" end
	if type(om.uix_getConfig) == "function" then
		local ok, cfg = pcall(om.uix_getConfig)
		if ok and validConfig(cfg) then return cfg, "uix" end
	end
	local d = rawget(_G, "debug")
	if type(d) ~= "table" then
		local ok, lib = pcall(require, "debug")
		d = ok and lib or nil
	end
	if type(d) == "table" and type(d.getupvalue) == "function" then
		for _, fname in ipairs({ "displayOptions", "createOptionsFrame", "displayOption" }) do
			local fn = om[fname]
			if type(fn) == "function" then
				for i = 1, 120 do
					local ok, name, val = pcall(d.getupvalue, fn, i)
					if not ok or name == nil then break end
					if name == "config" and validConfig(val) then return val, "debug:" .. fname end
				end
			end
		end
	end
	return nil, "none"
end

--- Patches the options menu once it is reachable. Safe to call any number of times (idempotent). Returns true when both
--- the row and the tooltip are patched.
function S.hookMenu()
	local om = findOptionsMenu()
	if not om then
		S.menu = { found = false, detail = "OptionsMenu_not_found" }
		return false
	end
	local m = S.menu
	if not (m and m.found) then
		m = { found = true, tooltip = false, row = false, detail = "" }
		S.menu = m
	end
	if not m.tooltip and type(om.saveMouseOverText) == "function" then
		local orig = om.saveMouseOverText
		om.saveMouseOverText = function(...)
			if blocked() then return S.tooltipText() end
			return orig(...)
		end
		m.tooltip = true
	end
	if not m.row then
		local cfg, source = getConfig(om)
		m.detail = "config:" .. source
		if cfg then
			for _, def in ipairs(cfg.optionDefinitions.main) do
				if type(def) == "table" and def.id == "save" then
					def.selectable = function(...) return IsSavingPossible(...) end -- global at call time: wrapper or not
					m.row = true
				end
			end
		end
	end
	return m.row and m.tooltip
end

------------------------------------------------------------------------------
-- state: block flag, MD flag, report to native
------------------------------------------------------------------------------
function S.sendMdFlag(on)
	local ok, err = pcall(AddUITriggeredEvent, S.MD_SCREEN, "noSave", on and "1" or "0")
	if not ok then log("MD flag not sent: " .. tostring(err)) end
	return ok
end

local function rawSend(verb, payload)
	local B = bridge()
	if not B or not B.getApi then return false, "no_bridge" end
	local api = B.getApi()
	if not api then return false, "no_api" end
	payload.v = 1
	local text = B.json.encode(payload)
	if not text then return false, "encode" end
	local ok, err = pcall(api.raise_event, "x4mp." .. verb, text)
	return ok, err
end

--- What the wrappers did, for the native self-test. Sent whenever it may have changed and on every native push.
function S.reportStatus()
	local sg, isp = S.wrappers.SaveGame, S.wrappers.IsSavingPossible
	local m = S.menu or {}
	return rawSend("saves_status", {
		save_game = sg ~= nil and sg.active == true,
		is_saving_possible = isp ~= nil and isp.active == true,
		menu_row = m.row == true,
		tooltip = m.tooltip == true,
		blocking = S.blocking == true,
		detail = m.detail or "",
	})
end

function S.setBlocking(on)
	on = on and true or false
	local changed = on ~= S.blocking
	S.blocking = on
	S.sendMdFlag(on)
	if changed then log("block " .. (on and "ON" or "OFF")) end
	S.hookMenu()
	S.reportStatus()
end

function S.requestSelfTest()
	local ok, err = rawSend("selftest", {})
	if not ok then log("selftest request not sent: " .. tostring(err)) end
	return ok
end

--- MD event_game_saved -> native (param "success=<0|1>;age=<seconds>")
function S.onMdGameSaved(param)
	local text = type(param) == "string" and param or tostring(param or "")
	local success = tonumber(text:match("success=(%d)")) or 1
	local age = tonumber(text:match("age=([%d%.]+)")) or 0
	rawSend("game_saved", { success = success, age = age })
end

------------------------------------------------------------------------------
-- start-up (idempotent: handlers registered once per Lua state)
------------------------------------------------------------------------------
if not S.eventsRegistered then
	S.eventsRegistered = true
	local B = bridge()
	if B and B.on then
		B.on("saves", function(p)
			S.setBlocking(p.block == true)
		end)
	end
	pcall(RegisterEvent, "x4mp.md_game_saved", function(_, param) S.onMdGameSaved(param) end)
	-- the options menu may register after us: retry on the usual UI events until both patches are in place
	for _, ev in ipairs({ "gfx_ok", "show" }) do
		pcall(RegisterEvent, ev, function()
			if not (S.menu and S.menu.row and S.menu.tooltip) then
				S.hookMenu()
				S.reportStatus()
			end
		end)
	end
end

local res = S.install()
S.sendMdFlag(S.blocking) -- fresh Lua state: unblocked until native pushes the real value (the MD flag lives in the save game)
S.reportStatus()
log(string.format("loaded: SaveGame=%s IsSavingPossible=%s chat=%s menu=%s", res.SaveGame, res.IsSavingPossible,
	res.ExecuteDebugCommand, S.menu and S.menu.detail or "?"))
