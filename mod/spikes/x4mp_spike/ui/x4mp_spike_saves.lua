-- x4mp_spike_saves.lua : spike v2 blocks saves1, saves1_block, saves2, saves4, clock, money (Lua side), v12 and s9gate
-- (the last two are implemented in md/x4mp_spike_saves.xml and only forwarded). Task M2-002.
--
--   saves1        C1  wrap SaveGame and IsSavingPossible (logging only; chain safe, idempotent)
--   saves1_block  C1  blocking ON ("off=1" turns it OFF): SaveGame is swallowed, IsSavingPossible returns false, the
--                     options-menu Save row is greyed and its tooltip replaced when the menu internals are reachable,
--                     and the MD flag global.$x4mp_noSave is set (the md/notifications.xml diff drops autosaves)
--   saves2        C2  blocking ON, then ask for an autosave: C.TriggerAutosave(true) (the native probe hook logs it;
--                     "native=0" skips it) and the vanilla MD request cue
--   saves4        C4  blocking OFF, SaveGame("x4mp_s2test_1", ...), time to the MD save event
--   clock         C5  1 Hz GetCurrentGameTime vs MD player.age for 240 s; resumes after a save load
--   money         C6  GetPlayerMoney before and after 3 s (the native +-100 is done by the probe, never here)
--   v12, s9gate   MD blocks
--
-- Sources (x4-unpacked/ui/addons/): SaveGame/IsSavingPossible Lua globals ego_gameoptions/gameoptions.lua:9296,6241
-- (note :1277 `selectable = IsSavingPossible` captures the ORIGINAL function, hence the config hook below);
-- TriggerAutosave ffi ego_detailmonitor/menu_userquestion.lua:35; GetCurrentGameTime ffi menu_diplomacy.lua:137;
-- GetSaveFolderPath ffi gameoptions.lua:193; GetPlayerMoney global (menu_map.lua); RegisterEvent chatwindow.lua:32.

-- luacheck: globals X4MPSpike DebugError RegisterEvent SaveGame IsSavingPossible GetPlayerMoney GetCurRealTime Menus X4MP_Probe

local S = X4MPSpike
if type(S) ~= "table" then return end

local ffi = require("ffi")
local C = ffi.C
local K, log = S.K, S.log

for _, def in ipairs({
	"double GetCurrentGameTime(void);",
	"void TriggerAutosave(bool checkenabled);",
	"const char* GetSaveFolderPath(void);",
}) do
	pcall(ffi.cdef, def) -- "redefine" when vanilla declared it first is harmless
end

-- state lives in S so a re-loaded file (same Lua state) sees the same wrappers
S.saves = S.saves or {
	blocking = false, sgCalls = 0, ispCalls = 0, sgPrev = nil, ispPrev = nil, sgWrapper = nil, ispWrapper = nil,
	lastSgT = nil, isp = {}, lastNotify = 0, clockGen = 0,
}
local W = S.saves

local UI_BLOCK_TEXT = "Saving is disabled while connected as a client (X4MP test)"

------------------------------------------------------------------------------
-- helpers
------------------------------------------------------------------------------
local function dbgLib()
	local d = rawget(_G, "debug")
	if type(d) == "table" then return d end
	local ok, r = pcall(require, "debug")
	if ok and type(r) == "table" then return r end
	return nil
end

local function callerText()
	local d = dbgLib()
	if not d or type(d.traceback) ~= "function" then return "no_debug_library" end
	local ok, tb = pcall(d.traceback, "", 3)
	if not ok or type(tb) ~= "string" then return "traceback_failed" end
	tb = tb:gsub("stack traceback:", ""):gsub("%s*\n%s*", "|"):gsub("^|", "")
	if #tb > 300 then tb = tb:sub(1, 300) end
	return tb
end

local function setMdFlag(on)
	S.toMD("saves_noSave", on and "1" or "0")
end

local function setBlocking(on)
	W.blocking = on and true or false
	setMdFlag(W.blocking)
	log("SAVE", "INFO", K("what", "blocking", "value", W.blocking))
end

------------------------------------------------------------------------------
-- wrappers (C1)
------------------------------------------------------------------------------
local function installWrappers()
	local res = {}
	-- SaveGame
	if W.sgWrapper and SaveGame == W.sgWrapper then
		res.SaveGame = "already_installed"
	elseif type(SaveGame) ~= "function" then
		res.SaveGame = "missing_global"
	else
		W.sgPrev = SaveGame
		W.sgWrapper = function(...)
			W.sgCalls = W.sgCalls + 1
			local a1, a2 = ...
			local t = S.now()
			W.lastSgT = t
			log("SAVE", "INFO", K("what", "SaveGame_called", "n", W.sgCalls, "filename", a1, "name", a2,
				"blocked", W.blocking, "t", t, "caller", callerText()))
			if W.blocking then
				log("SAVE", "INFO", K("what", "SaveGame_swallowed", "n", W.sgCalls))
				if t - (W.lastNotify or 0) > 3 then
					W.lastNotify = t
					S.notify("saves: SaveGame call blocked (see log)")
				end
				return
			end
			return W.sgPrev(...)
		end
		SaveGame = W.sgWrapper
		res.SaveGame = "wrapped"
	end
	-- IsSavingPossible: called very often by the menus, so log a line only for a new (caller,args,result) key or
	-- after 2 s, with the number of calls since the last line
	if W.ispWrapper and IsSavingPossible == W.ispWrapper then
		res.IsSavingPossible = "already_installed"
	elseif type(IsSavingPossible) ~= "function" then
		res.IsSavingPossible = "missing_global"
	else
		W.ispPrev = IsSavingPossible
		W.ispWrapper = function(...)
			W.ispCalls = W.ispCalls + 1
			local real = W.ispPrev(...)
			local result = real
			if W.blocking then result = false end
			local a1 = ...
			local who = callerText()
			local key = who .. "/" .. tostring(a1) .. "/" .. tostring(result)
			local e = W.isp[key]
			local t = S.now()
			if not e then
				e = { last = -100, count = 0 }
				W.isp[key] = e
			end
			e.count = e.count + 1
			if t - e.last >= 2 then
				log("SAVE", "INFO", K("what", "IsSavingPossible_called", "total", W.ispCalls, "arg1", a1, "real", real,
					"returned", result, "calls_since_last_line", e.count, "caller", who))
				e.last, e.count = t, 0
			end
			return result
		end
		IsSavingPossible = W.ispWrapper
		res.IsSavingPossible = "wrapped"
	end
	log("SAVE", "INFO", K("what", "wrappers", "SaveGame", res.SaveGame, "IsSavingPossible", res.IsSavingPossible,
		"debug_traceback", dbgLib() ~= nil))
	return res
end

-- Menu hooks: the vanilla Save row holds the ORIGINAL IsSavingPossible (a captured reference), so wrapping the global
-- alone does not grey it. When the options menu internals are reachable: make the row call the global at use time and
-- replace the tooltip text while blocking. Best effort, logged.
local function findOptionsMenu()
	for _, m in ipairs(Menus or {}) do
		if type(m) == "table" and m.name == "OptionsMenu" then return m end
	end
	return nil
end

local function validConfig(cfg)
	return type(cfg) == "table" and type(cfg.optionDefinitions) == "table" and type(cfg.optionDefinitions.main) == "table"
end

local function getConfig(om)
	if S.ui and validConfig(S.ui.config) then return S.ui.config, "ui_block" end
	if type(om.uix_getConfig) == "function" then
		local ok, cfg = pcall(om.uix_getConfig)
		if ok and validConfig(cfg) then return cfg, "uix" end
	end
	local d = dbgLib()
	if d and type(d.getupvalue) == "function" then
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

local function hookMenu()
	local om = findOptionsMenu()
	if not om then
		log("SAVE", "INFO", K("what", "menu_hook", "result", "OptionsMenu_not_found"))
		return
	end
	local tip = "already"
	if type(om.saveMouseOverText) == "function" and not W.tipHooked then
		local orig = om.saveMouseOverText
		om.saveMouseOverText = function(...)
			if S.saves.blocking then return UI_BLOCK_TEXT end
			return orig(...)
		end
		W.tipHooked = true
		tip = "hooked"
	elseif type(om.saveMouseOverText) ~= "function" then
		tip = "missing"
	end
	local cfg, source = getConfig(om)
	local row = "no_config"
	if cfg then
		row = "no_save_row"
		for _, def in ipairs(cfg.optionDefinitions.main) do
			if type(def) == "table" and def.id == "save" then
				if def.x4mpHooked then
					row = "already"
				else
					def.x4mpHooked = true
					def.selectable = function(...) return IsSavingPossible(...) end
					row = "hooked"
				end
			end
		end
	end
	log("SAVE", "INFO", K("what", "menu_hook", "tooltip", tip, "save_row", row, "config_source", source))
end

-- MD -> Lua: every game save (any source). C3: a save with no wrapper call just before it did not use the Lua path.
if not W.eventRegistered then
	W.eventRegistered = true
	pcall(RegisterEvent, "x4mp_spike.game_saved", function(_, param)
		local s = S.saves
		local t = S.now()
		s.lastSaveEventT = t
		local since = s.lastSgT and (t - s.lastSgT) or -1
		local viaLua = (since >= 0 and since < 30)
		log("SAVE", "INFO", K("what", "game_saved_event", "param", param, "t", t, "wrapper_calls_total", s.sgCalls,
			"seconds_since_last_wrapper_call", since, "plausibly_via_wrapper", viaLua))
		if s.saveWaiter then s.saveWaiter(t, param) end
	end)
end

------------------------------------------------------------------------------
-- blocks
------------------------------------------------------------------------------
S.register("saves1", function()
	installWrappers()
	hookMenu()
	S.notify("saves1: wrapper installed (logging only)")
end, "C1: wrap SaveGame/IsSavingPossible (logging only)")

S.register("saves1_block", function(args)
	installWrappers()
	hookMenu()
	if args.off == "1" then
		setBlocking(false)
		S.notify("saves1: blocking OFF")
	else
		setBlocking(true)
		S.notify("saves1: blocking ON")
	end
end, "C1: blocking ON (off=1: OFF) for SaveGame/IsSavingPossible and the MD autosave flag")

S.register("saves2", function(args)
	installWrappers()
	hookMenu()
	setBlocking(true)
	S.startRoutine("saves2", function()
		S.waitSeconds(0.5) -- let the MD flag arrive first
		if args.native == "0" then
			log("SAVE", "INFO", K("what", "TriggerAutosave", "result", "skipped_native_0"))
		else
			local t0 = S.now()
			local ok, err = pcall(function() C.TriggerAutosave(true) end)
			log("SAVE", ok and "INFO" or "FAIL", K("what", "TriggerAutosave_called", "checkenabled", true, "ok", ok,
				"err", ok and "" or err, "t", t0))
		end
		S.toMD("saves2_md", "")
		S.waitSeconds(3)
		log("SAVE", "INFO", K("what", "saves2_summary", "last_game_saved_event_t", W.lastSaveEventT or -1, "now", S.now()))
		S.notify("saves2: done")
	end)
end, "C2: blocker on, ask for an autosave natively and through MD")

S.register("saves4", function()
	installWrappers()
	setBlocking(false)
	S.startRoutine("saves4", function()
		S.waitSeconds(0.5)
		local name = "x4mp_s2test_1"
		local okP, possible = pcall(IsSavingPossible, false)
		log("SAVE", "INFO", K("what", "saves4_before", "IsSavingPossible", okP and possible or "err", "game_time",
			C.GetCurrentGameTime()))
		local done, t1, param = false, nil, nil
		W.saveWaiter = function(t, p) done, t1, param = true, t, p end
		local t0 = S.now()
		if X4MP_Probe and X4MP_Probe.markSaveBegin then
			pcall(X4MP_Probe.markSaveBegin, name)
		end
		local ok, err = pcall(SaveGame, name, "X4MP S2 test 1")
		local tReturn = S.now()
		log("SAVE", ok and "INFO" or "FAIL", K("what", "SaveGame_returned", "filename", name, "ok", ok,
			"err", ok and "" or err, "call_ms", (tReturn - t0) * 1000))
		local waited = 0
		while not done and waited < 60 do
			S.waitSeconds(0.1)
			waited = waited + 0.1
		end
		W.saveWaiter = nil
		if done then
			log("SAVE", "MEASURE", K("what", "saves4_timing", "to_game_saved_event_ms", (t1 - t0) * 1000,
				"returned_before_event", tReturn <= t1, "event_param", param))
		else
			log("SAVE", "FAIL", K("what", "saves4_timing", "result", "no_game_saved_event_in_60s"))
		end
		-- file check, only if Lua io is available (usually it is not)
		local okF, folder = pcall(function() return ffi.string(C.GetSaveFolderPath()) end)
		if okF and rawget(_G, "io") and io.open then
			for _, ext in ipairs({ ".xml.gz", ".xml" }) do
				local f = io.open(folder .. name .. ext, "rb")
				if f then
					local size = f:seek("end")
					f:close()
					log("SAVE", "INFO", K("what", "saves4_file", "path_ext", ext, "bytes", size))
				end
			end
		else
			log("SAVE", "INFO", K("what", "saves4_file", "result", "io_unavailable", "save_folder_ok", okF))
		end
		S.notify("saves4: done")
	end)
end, "C4: blocking off, SaveGame('x4mp_s2test_1') and time to the MD save event")

S.register("clock", function(args)
	local resume = args.resume == "1"
	local dur = tonumber(args.dur) or 240
	local n = resume and (tonumber(args.n) or 0) or 0
	W.clockGen = W.clockGen + 1
	local gen = W.clockGen
	log("CLOCK", "INFO", K("what", "clock_start", "resume", resume, "n", n, "duration_s", dur, "gen", gen))
	if not resume then
		S.toMD("clock_start", dur)
		S.notify("clock: started, runs " .. dur .. " s. Fly 30 s, pause 20 s, SETA 20 s")
	else
		S.notify("clock: resumed after load at " .. n .. " s")
	end
	S.startRoutine("clock", function()
		local saveNoticeSent = resume or n >= 120
		while n < dur and gen == W.clockGen do
			S.waitSeconds(1)
			if gen ~= W.clockGen then return end
			n = n + 1
			local okGt, gt = pcall(function() return C.GetCurrentGameTime() end)
			log("CLOCK", "MEASURE", K("side", "lua", "n", n, "game_time", okGt and gt or -1, "real", S.now(),
				"frame", S.frame(), "resume", resume))
			S.toMD("clock_tick", n)
			if n >= 120 and not saveNoticeSent then
				saveNoticeSent = true
				S.notify("clock: save now (new slot), then load it and wait until 'clock: done'")
			end
		end
		if gen == W.clockGen then
			S.toMD("clock_done", "")
			log("CLOCK", "INFO", K("what", "clock_done", "n", n))
			S.notify("clock: done")
		end
	end)
end, "C5: GetCurrentGameTime vs MD player.age at 1 Hz (resumes after a load)")

-- C6: the native +-100 test is done by the probe; this block never changes money.
S.register("money", function()
	S.startRoutine("money", function()
		local ok0, m0 = pcall(GetPlayerMoney)
		log("MONEY", "INFO", K("what", "lua_GetPlayerMoney", "stage", "before", "ok", ok0, "value", ok0 and m0 or -1))
		S.toMD("money_md", "before")
		S.waitSeconds(3)
		local ok1, m1 = pcall(GetPlayerMoney)
		log("MONEY", "INFO", K("what", "lua_GetPlayerMoney", "stage", "after_3s", "ok", ok1, "value", ok1 and m1 or -1,
			"delta", (ok0 and ok1) and (m1 - m0) or "n/a"))
		S.toMD("money_md", "after_3s")
		S.notify("money: logged before and after (the probe does the +-100)")
	end)
end, "C6: log GetPlayerMoney before and 3 s later (native +-100 comes from the probe)")

S.registerMD("v12", "V12 retest: object variables on a ship and a station (md/x4mp_spike_saves.xml)")
S.registerMD("s9gate", "S9 retest: activate an inactive gate (md/x4mp_spike_saves.xml)")

log("LUA", "INFO", K("what", "saves_blocks_loaded", "file", "x4mp_spike_saves"))
